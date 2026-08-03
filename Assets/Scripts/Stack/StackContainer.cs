using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace PrisonLife
{
    /// <summary>
    /// 적재 슬롯 관리 컴포넌트. ADR-001 §2.2.
    /// Player 등, Desk 상단, Processor 입출력 4개 사이트에서 동일 컴포넌트로 재사용.
    /// </summary>
    /// <remarks>
    /// 시각 보간 패턴 (ADR-003 §2.3 R5): async UniTaskVoid + this.destroyCancellationToken.
    /// .Forget() / discard / GetCancellationTokenOnDestroy() 셋 다 금지.
    /// PlayerLoopTiming.LastPostLateUpdate — Rigidbody.MovePosition(FixedUpdate)과 분리(위험 d, Stack jitter 회피).
    /// 인덱스 정합은 _items List 동기 적재/제거 기준. 트윈은 시각 표현만.
    /// </remarks>
    [DisallowMultipleComponent]
    public class StackContainer : MonoBehaviour
    {
        [Tooltip("최대 적재 수량. Inspector 직접 설정. 일부 site는 Awake에서 Settings 기반 SetCapacity 코드 주입 (DeskZone.HandcuffCapacity, ProcessorMachine.Input/OutputCapacity, DeskNPC.DeskNPCStackMax).")]
        [SerializeField] private int _capacity = 8;

        [Tooltip("슬롯 간 로컬 오프셋(units). Player 등 뒤(선형)는 Y축 누적, Desk(2D 격자)는 X·Z축 사용. ADR-001 §2.2.")]
        [SerializeField] private Vector3 _slotOffset = new Vector3(0f, 0.3f, 0f);

        [Tooltip("슬롯 트윈 1회 길이(sec). 너무 길면 추가 적재 race 위험.")]
        [SerializeField] private float _tweenDuration = 0.25f;

        [Tooltip("슬롯 트윈 포물선 최고점 높이(units). Mathf.Sin(t*PI) * arcHeight.")]
        [SerializeField] private float _tweenArcHeight = 0.5f;

        [Tooltip("MAX UI billboard GameObject (옵션). null 가능 — 부착 site만 MAX 표시. IsFull 도달 시 Settings.UI.MaxTextShowDelay 후 SetActive(true), Count 감소 시 즉시 SetActive(false). 4일차 visualCap 폐기 + MAX UI 도입.")]
        [SerializeField] private GameObject _maxBillboard;

        [Tooltip("MAX billboard yaw 정렬용 카메라 Transform. 비어있으면 Awake에서 Camera.main 폴백. 둘 다 null이면 회전 비활성. PrisonerNPC.SpeechBubble 패턴 정합.")]
        [SerializeField] private Transform _cameraTransform;

        private readonly List<Stackable> _items = new List<Stackable>();
        private CancellationTokenSource _maxShowCts;

        /// <summary>최대 적재 수량 (기능 max). IsFull 판정 기준. 4일차 visualCap 폐기 — capacity만큼 시각 표시.</summary>
        public int Capacity => _capacity;

        /// <summary>현재 적재된 아이템 개수.</summary>
        public int Count => _items.Count;

        /// <summary>Count == Capacity.</summary>
        public bool IsFull => _items.Count >= _capacity;

        /// <summary>Count == 0.</summary>
        public bool IsEmpty => _items.Count == 0;

        /// <summary>입력 타입 필터(R2). null이면 모든 타입 허용. Desk(Handcuff only) 등 2일차 분기용.</summary>
        public StackableType? AcceptedType { get; set; }

        /// <summary>Capacity 도달 순간 1회 발행.</summary>
        public event Action OnFull;

        /// <summary>Count == 0 도달 순간 1회 발행.</summary>
        public event Action OnEmpty;

        /// <summary>적재 수량 변경 시 발행. 인자는 변경 후 Count.</summary>
        public event Action<int> OnCountChanged;

        /// <summary>Drill 업그레이드 등 런타임 capacity 갱신용.</summary>
        public void SetCapacity(int newCapacity)
        {
            _capacity = Mathf.Max(0, newCapacity);
        }

        /// <summary>슬롯 인덱스의 로컬 좌표. ADR-001 §2.2 stackIndex * stackOffset 벡터.</summary>
        public Vector3 GetSlotLocalPosition(int slotIndex)
        {
            return _slotOffset * slotIndex;
        }

        /// <summary>적재. 슬롯 인덱스 반환, 실패 시 -1. 4일차 visualCap 폐기 — 모든 slot 시각 표시 + capacity 도달 시 MAX UI schedule.</summary>
        public int Add(Stackable item)
        {
            if (item == null) return -1;
            if (IsFull) return -1;
            if (AcceptedType.HasValue && item.Type != AcceptedType.Value) return -1;

            int slotIndex = _items.Count;
            _items.Add(item);

            // 월드 좌표 유지하며 부모 변경 → 트윈이 현재 위치에서 슬롯까지 자연스럽게 보간.
            item.transform.SetParent(transform, worldPositionStays: true);
            item.gameObject.SetActive(true);
            Vector3 targetLocal = GetSlotLocalPosition(slotIndex);
            TweenToSlotAsync(item.transform, targetLocal, this.destroyCancellationToken).Forget();

            OnCountChanged?.Invoke(_items.Count);
            if (IsFull)
            {
                OnFull?.Invoke();
                ScheduleMaxShow();
            }
            return slotIndex;
        }

        /// <summary>최상단(LIFO) 아이템 제거 후 반환. 비어있으면 null. SetParent/풀 회수는 호출자 책임.</summary>
        public Stackable Remove()
        {
            if (_items.Count == 0) return null;
            int last = _items.Count - 1;
            var item = _items[last];
            _items.RemoveAt(last);

            // capacity 미만 도달 — MAX UI 즉시 hide (cancel scheduled show).
            HideMax();

            OnCountChanged?.Invoke(_items.Count);
            if (_items.Count == 0) OnEmpty?.Invoke();
            return item;
        }

        /// <summary>최상단 아이템을 제거하지 않고 조회.</summary>
        public bool TryPeek(out Stackable item)
        {
            if (_items.Count == 0)
            {
                item = null;
                return false;
            }
            item = _items[_items.Count - 1];
            return true;
        }

        /// <summary>특정 타입 아이템이 1개라도 보유 중이면 true. MineZone의 handcuff 차단 가드 등 Type 단위 다중 검사용.</summary>
        public bool ContainsType(StackableType type)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i] != null && _items[i].Type == type) return true;
            }
            return false;
        }

        /// <summary>최상단 N개 일괄 Pop. n 초과 시 가능한 만큼만. 반환은 Pop된 인스턴스 list (LIFO top-first). 비어있으면 빈 list. 호출자: ProcessorMachine.PushOres/PullHandcuffs 등 일괄 이동.</summary>
        public List<Stackable> RemoveRange(int n)
        {
            var result = new List<Stackable>();
            int popCount = Mathf.Min(n, _items.Count);
            for (int i = 0; i < popCount; i++)
            {
                var item = Remove();
                if (item != null) result.Add(item);
            }
            return result;
        }

        /// <summary>items를 순서대로 Add (각각 Add() 단일 트윈 호출). capacity 또는 AcceptedType 미일치 첫 발생 시점부터 거부 — 실제 추가 성공 개수 반환. 거부된 인스턴스(반환값 이후 인덱스)의 풀 회수는 호출자 책임.</summary>
        public int AddBatch(IList<Stackable> items)
        {
            if (items == null) return 0;
            int added = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] == null) continue;
                int slot = Add(items[i]);
                if (slot < 0) break; // capacity 또는 type 미일치 — 이후 모두 거부.
                added++;
            }
            return added;
        }

        /// <summary>모든 아이템을 즉시 제거. 풀 회수는 호출자 책임.</summary>
        public void Clear()
        {
            if (_items.Count == 0) return;
            _items.Clear();
            HideMax();
            OnCountChanged?.Invoke(0);
            OnEmpty?.Invoke();
        }

        // --- MAX UI billboard (4일차 visualCap 폐기 + MAX UI 도입) ---

        private void Awake()
        {
            // _cameraTransform Inspector 미박 시 Camera.main fallback (PrisonerNPC / UpgradeZone 패턴 정합).
            if (_cameraTransform == null)
            {
                var mainCam = Camera.main;
                if (mainCam != null) _cameraTransform = mainCam.transform;
            }

            // MAX UI always-on-top — 자식 TMP fontMaterial shader → "TextMeshPro/Distance Field Overlay" 변경.
            // Overlay shader는 ZTest Always + RenderQueue Overlay 박혀있음 — World Space Canvas mesh 가림 회피.
            // fontMaterial 박는 시점에 자동 인스턴스 생성 박음 — 다른 TMP 공유 material 영향 무.
            // Shader.Find는 Always Included Shaders 박혀있어야 박음 (TMP_SDF Overlay.shader 박혀있음).
            if (_maxBillboard != null)
            {
                var tmp = _maxBillboard.GetComponentInChildren<TMP_Text>(includeInactive: true);
                if (tmp != null && tmp.fontMaterial != null)
                {
                    var overlayShader = Shader.Find("TextMeshPro/Distance Field Overlay");
                    if (overlayShader != null)
                    {
                        tmp.fontMaterial.shader = overlayShader;
                    }
                    else
                    {
                        Debug.LogWarning("[StackContainer] TextMeshPro/Distance Field Overlay shader not found. MAX UI gameobject 가림 회피 무. Project Settings → Graphics → Always Included Shaders 박는 게 권장.", this);
                    }
                }
            }
        }

        private void LateUpdate()
        {
            // MAX billboard yaw 정렬 — _maxBillboard active 시만 박음. xz pitch 0 유지 (PrisonerNPC.SpeechBubble 패턴 정합).
            if (_maxBillboard == null || !_maxBillboard.activeSelf) return;
            if (_cameraTransform == null) return;
            float yaw = _cameraTransform.eulerAngles.y;
            _maxBillboard.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        /// <summary>IsFull 도달 시 호출. Settings.UI.MaxTextShowDelay 후 _maxBillboard.SetActive(true). delay 중 Count 감소 시 cancel.</summary>
        private void ScheduleMaxShow()
        {
            if (_maxBillboard == null) return;
            CancelMaxShowToken();
            _maxShowCts = CancellationTokenSource.CreateLinkedTokenSource(this.destroyCancellationToken);
            DelayedShowMaxAsync(_maxShowCts.Token).Forget();
        }

        /// <summary>Count 감소 시 호출. _maxBillboard 즉시 hide + scheduled show cancel.</summary>
        private void HideMax()
        {
            CancelMaxShowToken();
            if (_maxBillboard != null) _maxBillboard.SetActive(false);
        }

        private async UniTaskVoid DelayedShowMaxAsync(CancellationToken ct)
        {
            var settings = GameManager.Instance != null ? GameManager.Instance.Settings : null;
            float delay = settings != null ? settings.UI.MaxTextShowDelay : 1f;
            try
            {
                if (delay > 0f)
                {
                    await UniTask.Delay((int)(delay * 1000), cancellationToken: ct);
                }
                if (_maxBillboard != null) _maxBillboard.SetActive(true);
            }
            catch (OperationCanceledException) { /* hide 박힌 결과 — 정상 */ }
        }

        private void CancelMaxShowToken()
        {
            if (_maxShowCts != null)
            {
                _maxShowCts.Cancel();
                _maxShowCts.Dispose();
                _maxShowCts = null;
            }
        }

        private void OnDisable()
        {
            CancelMaxShowToken();
        }

        /// <summary>슬롯 위치까지 포물선 트윈. ADR-003 §2.3 표준 패턴.</summary>
        private async UniTaskVoid TweenToSlotAsync(Transform itemTransform, Vector3 targetLocal, CancellationToken ct)
        {
            if (itemTransform == null) return;

            Vector3 startLocal = itemTransform.localPosition;
            float elapsed = 0f;

            while (elapsed < _tweenDuration)
            {
                ct.ThrowIfCancellationRequested();
                // Destroy 또는 Remove 후 다른 부모로 SetParent된 케이스 즉시 종료(위험 e).
                if (itemTransform == null || itemTransform.parent != transform) return;

                float t = elapsed / _tweenDuration;
                Vector3 lerp = Vector3.Lerp(startLocal, targetLocal, t);
                lerp.y += Mathf.Sin(t * Mathf.PI) * _tweenArcHeight;
                itemTransform.localPosition = lerp;

                elapsed += Time.deltaTime;
                await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate, ct);
            }

            if (itemTransform != null && itemTransform.parent == transform)
            {
                itemTransform.localPosition = targetLocal;
            }
        }
    }
}
