using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UI;

namespace PrisonLife
{
    /// <summary>
    /// 죄수 NPC. NavMeshAgent로 Queue slot → Desk → MoneyPile spawn → Jail 이동 → JailZone 변환.
    /// FSM 6상태 (3일차 게이트 5b): Spawning → MovingToQueueSlot → MovingToDesk → WaitingAtDesk → MovingToJail → EnteringJail.
    /// handoff §4 #24 lock 정합 (5a Leaving 폐기 + MovingToJail/EnteringJail 신규로 6상태).
    /// 게이트 4 동작 보존: RequestQuantity 1~3 random + SpeechBubble billboard + 일괄 거래.
    /// </summary>
    /// <remarks>
    /// 게이트 5b 변경:
    /// - Initialize 시그니처 7인자 (jailZone 추가). _jailZone은 검증 전 첫 줄 set (옵션 A 패턴).
    /// - Leaving 상태 폐기 → MovingToJail (jail destination 이동) + EnteringJail (1프레임 marker).
    /// - EnterLeaving 메서드 폐기 → EnterMovingToJail로 교체. SetDestination = jailZone.transform.position (게이트 5b 결정 1 단순화).
    /// - MovingToJail 도달 시 atomic — TryConvertToJailObject + spawner.Release (결과 무관, sub-Q 6 옵션 a fallback) + _state = EnteringJail (marker).
    /// - OnDisable cleanup에 _jailZone = null 추가.
    /// 거래 cascade (5a 본체 그대로): TryAcquireHandcuff 성공 → money 처리 → desk.OnPrisonerDealComplete(this)
    /// → desk가 RemoveAt(0) + 나머지 PromoteSlot + 본 prisoner.EnterMovingToJail 호출 (5a EnterLeaving → 5b EnterMovingToJail로 교체).
    /// EnteringJail dead branch: spawner.Release 이후 line 실행으로 OnDisable 리셋 후 _state = EnteringJail 잔존, Update 호출 불가 (SetActive false).
    /// </remarks>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NavMeshAgent))]
    public class PrisonerNPC : NPCBase
    {
        private enum PrisonerState { Spawning, MovingToQueueSlot, MovingToDesk, WaitingAtDesk, MovingToJail, WaitingAtJail, EnteringJail }

        private const float ARRIVAL_DISTANCE = 0.5f;
        private const float WAIT_RETRY_INTERVAL = 0.5f;

        [Tooltip("NavMeshAgent. Inspector 주입 우선, 비어있으면 Awake에서 GetComponent fallback.")]
        [SerializeField] private NavMeshAgent _agent;

        [Tooltip("Animator. char02_X.prefab Animator 컴포넌트 ref. CharacterAnimator Controller + IsMoving 파라미터. 게이트 6.5 신설.")]
        [SerializeField] private Animator _animator;

        [Tooltip("SpeechBubble 루트 GameObject (자식 World Space Canvas). MovingToJail 진입 시 SetActive(false).")]
        [SerializeField] private GameObject _speechBubbleRoot;

        [Tooltip("SpeechBubble TextMeshProUGUI. Initialize에서 \"x{RequestQuantity}\" 1회 박음.")]
        [SerializeField] private TextMeshProUGUI _speechBubbleText;

        [Tooltip("거래 진행 fill Image (옵션). Image Type = Filled 박음. 1개 pickup 시마다 (acquired/total) 갱신. null 가능.")]
        [SerializeField] private Image _progressFill;

        private DeskZone _desk;
        private PrisonerSpawner _spawner;
        private MoneyPileZone _moneyPile;
        private Transform _cameraTransform;
        private JailZone _jailZone;
        private Vector3 _spawnPosition;
        private int _slotIndex = -1;
        private int _requestQuantity;
        private PrisonerState _state = PrisonerState.Spawning;
        private float _waitTimer;
        // 4일차 — handcuff 1개씩 pickup async cycle 가드. true 동안 추가 TryAcquireHandcuff 박지 않음.
        private bool _isPickingUp;
        private const int PICKUP_INTERVAL_MS = 200;
        private const float HANDCUFF_TWEEN_DURATION = 0.3f;
        private const float HANDCUFF_TWEEN_ARC_HEIGHT = 0.5f;
        private const float HANDCUFF_TARGET_HEIGHT = 1.0f; // prisoner head 부근 박음.

        /// <summary>거래 요청 handcuff 개수. DeskZone.NeedsHandcuffRefill에서 front prisoner 요구량 검사용 (DeskNPC deadlock 회피).</summary>
        public int RequestQuantity => _requestQuantity;

        private void Awake()
        {
            if (_agent == null) _agent = GetComponent<NavMeshAgent>();
        }

        /// <summary>
        /// Spawner가 풀 Get + desk.TryEnqueue 직후 호출. desk/spawner/queueSlotIndex/moneyPile/requestQuantity/cameraTransform/jailZone 주입 + 즉시 상태 전이.
        /// queueSlotIndex == 0 → MovingToDesk 직진입 / > 0 → MovingToQueueSlot.
        /// 반환값: 성공 true / 실패 false. 실패 시 호출자(Spawner)는 즉시 _pool.Release 호출 — OnDisable의 RemoveFromQueue가 dangling cleanup 처리.
        /// _jailZone은 검증 전 첫 줄 set (옵션 A 패턴) — 검증 실패 분기에서도 OnDisable cleanup 안전망 (실제 dangling은 두 풀 격리로 무).
        /// moneyPile null fallback: 즉시 GameManager.AddMoney (block visual 없음).
        /// </summary>
        public bool Initialize(DeskZone desk, PrisonerSpawner spawner, int queueSlotIndex,
                                MoneyPileZone moneyPile, int requestQuantity, Transform cameraTransform,
                                JailZone jailZone)
        {
            // 검증 전 첫 줄 set (옵션 A) — _jailZone에 한해. OnDisable cleanup 안전망.
            _jailZone = jailZone;

            if (desk == null || spawner == null)
            {
                Debug.LogError("[PrisonerNPC] Initialize received null desk/spawner.", this);
                return false;
            }
            if (_agent == null || !_agent.isOnNavMesh)
            {
                Debug.LogError("[PrisonerNPC] NavMeshAgent missing or not on NavMesh. Spawn point may be off NavMesh.", this);
                return false;
            }
            if (requestQuantity <= 0)
            {
                Debug.LogError($"[PrisonerNPC] Invalid requestQuantity ({requestQuantity}). Must be > 0.", this);
                return false;
            }

            _desk = desk;
            _spawner = spawner;
            _moneyPile = moneyPile;
            _requestQuantity = requestQuantity;
            _cameraTransform = cameraTransform;
            _slotIndex = queueSlotIndex;

            if (_moneyPile == null)
            {
                Debug.LogWarning("[PrisonerNPC] _moneyPile null. Will fall back to immediate AddMoney without block spawn.", this);
            }
            if (_cameraTransform == null)
            {
                Debug.LogWarning("[PrisonerNPC] _cameraTransform null. SpeechBubble billboard will not rotate.", this);
            }
            if (_jailZone == null)
            {
                Debug.LogWarning("[PrisonerNPC] _jailZone null. EnterMovingToJail will fail (LogError) when invoked. Spawner Inspector slot 확인.", this);
            }

            _spawnPosition = transform.position; // OnGetPrisoner에서 _spawnPoint.position 박은 직후라 정확.

            var settings = GameManager.Instance != null ? GameManager.Instance.Settings : null;
            if (settings != null) _agent.speed = settings.NPC.PrisonerMoveSpeed;

            // SpeechBubble + fill 박음 — RequestQuantity 표시 + fill 0 reset.
            if (_speechBubbleRoot != null) _speechBubbleRoot.SetActive(true);
            if (_speechBubbleText != null) _speechBubbleText.text = $"x{_requestQuantity}";
            if (_progressFill != null) _progressFill.fillAmount = 0f;

            // destination + 상태 전이 — slot 0 직진입 vs MovingToQueueSlot 분기.
            var slotTransform = _desk.GetQueueSlot(_slotIndex);
            if (slotTransform == null)
            {
                Debug.LogError($"[PrisonerNPC] desk.GetQueueSlot({_slotIndex}) returned null. Initialize aborted.", this);
                return false;
            }
            _agent.SetDestination(slotTransform.position);
            _state = (_slotIndex == 0) ? PrisonerState.MovingToDesk : PrisonerState.MovingToQueueSlot;
            _waitTimer = 0f;
            return true;
        }

        private void Update()
        {
            // 게이트 6.5 — Animator 갱신. NavMeshAgent.velocity 기반 IsMoving (이동 중인지).
            if (_animator != null && _agent != null)
            {
                _animator.SetBool("IsMoving", _agent.velocity.sqrMagnitude > 0.01f);
            }

            switch (_state)
            {
                case PrisonerState.Spawning:
                    // Initialize 호출 직전 상태 — Initialize 즉시 다음 상태로 전환되므로 도달 안 함 (안전망).
                    break;

                case PrisonerState.MovingToQueueSlot:
                    // NavMeshAgent.SetDestination이 처리. 도착 후 자체 정지 (Update 무동작).
                    // PromoteSlot 호출이 destination 갱신 또는 MovingToDesk 전이 트리거.
                    break;

                case PrisonerState.MovingToDesk:
                    if (_agent.pathPending) break;
                    if (_agent.remainingDistance > ARRIVAL_DISTANCE) break;
                    // 4일차 Desk + DeskNPC 시스템 — 점유자 (Player or DeskNPC) 부재 시 거래 안 박음. WaitingAtDesk 전이.
                    if (!_desk.IsOccupiedByPlayerOrDeskNPC)
                    {
                        _state = PrisonerState.WaitingAtDesk;
                        _waitTimer = 0f;
                        break;
                    }
                    if (!TryAcquireHandcuff())
                    {
                        _state = PrisonerState.WaitingAtDesk;
                        _waitTimer = 0f;
                    }
                    // 성공 시 cascade(desk.OnPrisonerDealComplete)로 EnterMovingToJail 호출됨 → _state = MovingToJail.
                    break;

                case PrisonerState.WaitingAtDesk:
                    _waitTimer += Time.deltaTime;
                    if (_waitTimer < WAIT_RETRY_INTERVAL) break;
                    _waitTimer = 0f;
                    // 4일차 Desk + DeskNPC 시스템 — 점유자 부재 시 재시도 의미 무.
                    if (!_desk.IsOccupiedByPlayerOrDeskNPC) break;
                    // 결정 #5 — 무한 대기 패턴. desk count >= RequestQuantity 충족까지 0.5s 주기 재시도.
                    TryAcquireHandcuff(); // 성공 시 cascade로 MovingToJail 전환.
                    break;

                case PrisonerState.MovingToJail:
                    // 폴백 — JailEntryZone trigger 미발동 case (NavMeshAgent + isTrigger 호환 위험) 도달 거리 검사 박음.
                    // 정상 경로: JailEntryZone.OnTriggerEnter → OnEnterJailEntry() → TryEnterJailOrWait.
                    if (_agent.pathPending) break;
                    if (_agent.remainingDistance > ARRIVAL_DISTANCE) break;
                    TryEnterJailOrWait();
                    break;

                case PrisonerState.WaitingAtJail:
                    // 이벤트 기반 — JailZone.OnCapacityChanged 발행 시 HandleJailCapacityChanged가 재시도. Update 무동작.
                    break;

                case PrisonerState.EnteringJail:
                    // Update는 비활성 prisoner엔 호출 안 됨 — 본 분기 실제 도달 안 함 (안전망 stub).
                    break;
            }
        }

        private void LateUpdate()
        {
            // SpeechBubble billboard — 카메라 yaw만 정렬 (xz pitch는 0 유지). _cameraTransform null 시 회전 미적용.
            if (_speechBubbleRoot == null || !_speechBubbleRoot.activeSelf) return;
            if (_cameraTransform == null) return;

            float yaw = _cameraTransform.eulerAngles.y;
            _speechBubbleRoot.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        private bool TryAcquireHandcuff()
        {
            if (_isPickingUp) return true; // 이미 pickup async 진행 중 — 호출자에 success 반환 박아 WaitingAtDesk 전이 회피.
            if (_desk == null) return false;

            // RequestQuantity만큼 일괄 Pop. 부족 시 false (호출자가 WaitingAtDesk 전이).
            if (!_desk.TryAcquireHandcuffs(_requestQuantity, out var handcuffs)) return false;

            // 4일차 — 1개씩 timer pickup 연출. PICKUP_INTERVAL_MS (200ms) 마다 1개 ReturnToPool + fill / text 갱신.
            _isPickingUp = true;
            PickupHandcuffsAsync(handcuffs).Forget();
            return true;
        }

        /// <summary>4일차 — handcuff 1개씩 timer pickup async cycle. 매 PICKUP_INTERVAL_MS 마다 1개 트윈 시작 (background).
        /// 트윈 도달 시점 fill (acquired/total) + text "x{remaining}" 갱신 + ReturnToPool.
        /// 모든 트윈 종료 후 money 처리 + cascade (desk.OnPrisonerDealComplete → EnterMovingToJail).</summary>
        private async UniTaskVoid PickupHandcuffsAsync(List<Stackable> handcuffs)
        {
            int total = handcuffs.Count;
            int processed = 0;

            try
            {
                for (int i = 0; i < total; i++)
                {
                    if (!_isPickingUp) break; // OnDisable 외부 cancel.
                    await UniTask.Delay(PICKUP_INTERVAL_MS, cancellationToken: this.destroyCancellationToken);

                    if (handcuffs[i] != null)
                    {
                        // 트윈 background 박음 — 도달 시점 onArrive 콜백에서 fill/text 갱신 + ReturnToPool.
                        TweenHandcuffToPrisonerAsync(handcuffs[i], () =>
                        {
                            processed++;
                            int remaining = total - processed;
                            if (_speechBubbleText != null) _speechBubbleText.text = remaining > 0 ? $"x{remaining}" : "";
                            if (_progressFill != null && total > 0) _progressFill.fillAmount = (float)processed / total;
                        }, this.destroyCancellationToken).Forget();
                    }
                    else
                    {
                        // null handcuff — processed 박은 게 갱신 박지만 트윈 무.
                        processed++;
                    }
                }

                // 모든 트윈 종료 대기 — 마지막 handcuff 도달까지 polling.
                await UniTask.WaitUntil(() => processed >= total || !_isPickingUp, cancellationToken: this.destroyCancellationToken);
            }
            catch (OperationCanceledException) { /* finally cleanup */ }

            if (!_isPickingUp) return; // OnDisable cancel — cascade 박지 않음.

            // money 처리 — pickup cycle 종료 후 일괄.
            if (_moneyPile != null)
            {
                _moneyPile.SpawnMoney(_requestQuantity);
            }
            else
            {
                var settings = GameManager.Instance != null ? GameManager.Instance.Settings : null;
                int moneyPerBlock = (settings != null) ? settings.MoneyPile.MoneyPerBlock : 10;
                GameManager.Instance?.AddMoney(_requestQuantity * moneyPerBlock);
            }

            _isPickingUp = false;

            // cascade — desk가 RemoveAt(0) + PromoteSlot + 본 prisoner.EnterMovingToJail 호출.
            if (_desk != null) _desk.OnPrisonerDealComplete(this);
        }

        /// <summary>handcuff 박은 게 desk 박은 위치 → prisoner head 위치 parabolic arc 트윈. 트윈 종료 시 onArrive 콜백 + ReturnToPool.
        /// prisoner 이동 case 매 frame target 위치 갱신 박음 (단 pickup 동안 prisoner 정지 가정).</summary>
        private async UniTaskVoid TweenHandcuffToPrisonerAsync(Stackable hc, Action onArrive, CancellationToken ct)
        {
            if (hc == null) { onArrive?.Invoke(); return; }
            Transform t = hc.transform;
            if (t == null) { onArrive?.Invoke(); return; }

            // desk 자식 박은 채로 박은 거 SetParent(null) 박아 독립 박음 + worldPositionStays.
            t.SetParent(null, worldPositionStays: true);
            Vector3 startWorld = t.position;

            float elapsed = 0f;

            try
            {
                while (elapsed < HANDCUFF_TWEEN_DURATION)
                {
                    ct.ThrowIfCancellationRequested();
                    if (t == null || hc == null) return;
                    if (this == null) return; // PrisonerNPC destroyed case.

                    Vector3 endWorld = transform.position + Vector3.up * HANDCUFF_TARGET_HEIGHT;
                    float p = elapsed / HANDCUFF_TWEEN_DURATION;
                    Vector3 lerp = Vector3.Lerp(startWorld, endWorld, p);
                    lerp.y += Mathf.Sin(p * Mathf.PI) * HANDCUFF_TWEEN_ARC_HEIGHT;
                    t.position = lerp;
                    elapsed += Time.deltaTime;
                    await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate, ct);
                }
            }
            catch (OperationCanceledException) { /* finally — onArrive + ReturnToPool */ }
            finally
            {
                if (this != null) onArrive?.Invoke();
                if (hc != null) hc.ReturnToPool();
            }
        }

        /// <summary>desk.OnPrisonerDealComplete cascade에서 호출 — _slotIndex 갱신 + destination 재설정 + 상태 전이.
        /// newIndex == 0 → MovingToDesk 진입(slot 0 = DeskPoint = desk 거래 위치).
        /// newIndex > 0 → MovingToQueueSlot 유지(destination만 갱신, NavMesh가 자동 이동).</summary>
        public void PromoteSlot(int newIndex)
        {
            _slotIndex = newIndex;
            if (_desk == null) return;
            var slot = _desk.GetQueueSlot(newIndex);
            if (slot == null) return;
            if (_agent == null || !_agent.isOnNavMesh) return;

            _agent.SetDestination(slot.position);
            _state = (newIndex == 0) ? PrisonerState.MovingToDesk : PrisonerState.MovingToQueueSlot;
            _waitTimer = 0f;
        }

        /// <summary>desk.OnPrisonerDealComplete cascade에서 호출 — 거래 완료 후 jail로 이동 시작.
        /// SpeechBubble 비활성 + NavMeshAgent destination = _jailZone.EntryAnchor (4일차 — 별도 entry trigger 위치).
        /// 5a EnterLeaving 폐기 + 본 메서드로 supersede.</summary>
        public void EnterMovingToJail()
        {
            _state = PrisonerState.MovingToJail;
            if (_speechBubbleRoot != null) _speechBubbleRoot.SetActive(false);
            if (_jailZone == null)
            {
                Debug.LogError("[PrisonerNPC] _jailZone null at EnterMovingToJail. Cannot set destination.", this);
                return;
            }
            if (_agent != null && _agent.isOnNavMesh)
            {
                Transform dest = _jailZone.EntryAnchor;
                _agent.SetDestination(dest != null ? dest.position : _jailZone.transform.position);
            }
            _waitTimer = 0f;
        }

        /// <summary>JailEntryZone.OnTriggerEnter에서 호출 — prisoner가 entry trigger 박을 시 변환 시도. idempotent (이미 EnteringJail/WaitingAtJail 박힘 시 skip).</summary>
        public void OnEnterJailEntry()
        {
            if (_state != PrisonerState.MovingToJail) return;
            TryEnterJailOrWait();
        }

        /// <summary>jail 진입 시도 — 성공 시 ReleaseToPool, 실패 (jail full) 시 EnterWaitingAtJail. JailEntryZone trigger 진입 + MovingToJail 거리 폴백 양쪽 호출.</summary>
        private void TryEnterJailOrWait()
        {
            if (_jailZone == null)
            {
                Debug.LogWarning("[PrisonerNPC] _jailZone null at jail entry. spawner.Release fallback.", this);
                ReleaseToPool();
                return;
            }
            if (_jailZone.TryConvertToJailObject(this))
            {
                ReleaseToPool();
                return;
            }
            // jail full — No Cell 말풍선 + OnCapacityChanged 구독.
            EnterWaitingAtJail();
        }

        private void ReleaseToPool()
        {
            if (_spawner != null)
            {
                _spawner.Release(this); // OnDisable이 _state/_desk/_jailZone 등 리셋 + OnCapacityChanged unsubscribe.
            }
            else
            {
                Debug.LogWarning("[PrisonerNPC] _spawner null at jail completion. Destroy fallback.", this);
                Destroy(gameObject);
            }
            _state = PrisonerState.EnteringJail; // 1 frame marker — OnDisable 후 박힘.
        }

        /// <summary>jail full 시 No Cell 말풍선 표시 + OnCapacityChanged 이벤트 구독. capacity 증가 시 HandleJailCapacityChanged가 재시도.</summary>
        private void EnterWaitingAtJail()
        {
            _state = PrisonerState.WaitingAtJail;
            if (_agent != null && _agent.isOnNavMesh) _agent.ResetPath();
            if (_speechBubbleRoot != null) _speechBubbleRoot.SetActive(true);
            if (_speechBubbleText != null) _speechBubbleText.text = "No Cell!";
            if (_jailZone != null) _jailZone.OnCapacityChanged += HandleJailCapacityChanged;
        }

        private void HandleJailCapacityChanged()
        {
            if (_state != PrisonerState.WaitingAtJail) return;
            // unsubscribe + retry. 실패 시 EnterWaitingAtJail가 재구독.
            if (_jailZone != null) _jailZone.OnCapacityChanged -= HandleJailCapacityChanged;
            _state = PrisonerState.MovingToJail;
            TryEnterJailOrWait();
        }

        private void OnDisable()
        {
            // Queue dangling cleanup — Initialize 실패 또는 거래 미완료 강제 Release 케이스.
            // 정상 흐름이면 OnPrisonerDealComplete가 이미 RemoveAt(0) → idx < 0 early return.
            if (_desk != null) _desk.RemoveFromQueue(this);

            // OnCapacityChanged stale subscription 정리 (WaitingAtJail 박힌 상태에서 강제 Release case).
            if (_jailZone != null) _jailZone.OnCapacityChanged -= HandleJailCapacityChanged;

            // Pool Release 시 actionOnRelease의 SetActive(false) → OnDisable 자동 호출.
            // 다음 Get 시 Initialize까지 깨끗한 상태 유지.
            _state = PrisonerState.Spawning;
            _waitTimer = 0f;
            _slotIndex = -1;
            _requestQuantity = 0;
            _isPickingUp = false;                   // 4일차 — pickup async 진행 중 강제 cancel.
            _desk = null;
            _spawner = null;
            _moneyPile = null;
            _cameraTransform = null;
            _jailZone = null;                       // ← 신규 (5b)
            if (_speechBubbleRoot != null) _speechBubbleRoot.SetActive(false);
            if (_progressFill != null) _progressFill.fillAmount = 0f;
            if (_agent != null && _agent.isOnNavMesh) _agent.ResetPath();
        }
    }
}
