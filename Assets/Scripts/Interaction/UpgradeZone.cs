using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PrisonLife
{
    /// <summary>
    /// UpgradeZone 베이스 — InteractionZone 파생 abstract. 3일차 게이트 5c 결정 #27.
    /// 4개 파생(DrillUpgradeZone / TractorUpgradeZone / MinerSpawnZone / JailUpgradeZone)이 공유.
    /// 1회 결제 패턴: OnPlayerEnter 트리거 → BackStack RemoveRange → block N개 zone으로 트윈 + cost 카운트다운 → 도달 후 TrySpendMoney + ApplyUpgrade + RaiseProgressionEvent + SetActive(false).
    /// </summary>
    /// <remarks>
    /// 결정 #27 — InteractionZone 파생 + abstract 4개 (Cost / IsUnlockedAtStart / ApplyUpgrade / RaiseProgressionEvent).
    /// 결정 #28 — SetActive 토글: 씬 SetActive(true) 시작 + Awake에서 IsUnlockedAtStart() false 시 SetActive(false). 파생이 GameManager 이벤트 구독 (HandleUnlock 헬퍼 호출).
    /// 결정 3-A — 즉시 결제 (OnPlayerEnter 트리거, accumulator 미사용).
    /// Sub-Q 1-A — BackStack 트윈 출력 + billboard 가격 카운트다운.
    /// Sub-Q 1-B 옵션 B — 트윈 완료 후 TrySpendMoney 일괄 차감.
    /// Sub-Q _cameraTransform — Inspector slot + Camera.main 폴백.
    /// _isProcessing 가드 — async 진행 중 OnPlayerEnter 재진입 방어.
    /// </remarks>
    [DisallowMultipleComponent]
    public abstract class UpgradeZone : InteractionZone
    {
        private const float TWEEN_DURATION = 0.3f;
        private const float TWEEN_ARC_HEIGHT = 0.5f;
        private const int TWEEN_INTERVAL_MS = 50;        // 0.05s 간격 다음 block 트윈 시작.

        [Tooltip("Zone 위 World Space TextMeshProUGUI — 가격 표시 + 결제 중 카운트다운 ($Cost → $0). Inspector 드래그 필수.")]
        [SerializeField] protected TextMeshProUGUI _costText;

        [Tooltip("Billboard 회전 대상 Transform — Canvas 부모. LateUpdate에서 yaw 정렬. Inspector 드래그 필수.")]
        [SerializeField] protected Transform _billboardRoot;

        [Tooltip("Billboard yaw 정렬용 카메라. 비어있으면 Awake에서 Camera.main 폴백. 둘 다 null이면 LogWarning + 회전 비활성.")]
        [SerializeField] protected Transform _cameraTransform;

        [Tooltip("결제 진행률 fill Image (옵션). Image Type = Filled 박음. 결제 시작 시 fillAmount=0, 각 block 도달 시 (Cost - remaining)/Cost 갱신, 완료 시 1. null 가능 — 박지 않은 zone 영향 무.")]
        [SerializeField] protected Image _progressFill;

        private bool _isProcessing;
        // 4일차 부분 결제 — Cost 도달까지 누적 차감 박음. Awake에서 Cost로 초기화 + 트윈 콜백에서 차감.
        private int _remaining;

        // --- abstract (파생 4종 구현 필수) ---

        /// <summary>결제 비용 (Money). Settings 참조 (예: Settings.Upgrade.DrillUpgradeCost).</summary>
        protected abstract int Cost { get; }

        /// <summary>Billboard 1줄차 표시명. Awake + 카운트다운 콜백에서 `$"{DisplayName}\n${Cost}"` 형태로 박음. 영문 표기 (LiberationSans SDF 정합).</summary>
        protected abstract string DisplayName { get; }

        /// <summary>씬 시작 시 활성 상태 여부. GameManager progression 플래그 참조 (예: IsFirstMoneyEarned). false면 Awake에서 즉시 SetActive(false).</summary>
        protected abstract bool IsUnlockedAtStart();

        /// <summary>결제 통과 시 효과 적용. Drill: 빈(OreNode 폴링) / Tractor: 빈(OreNode 폴링) / MinerSpawn: MinerSpawner.SetActive(true) / JailUpgrade: JailZone.IncreaseCapacity.</summary>
        protected abstract void ApplyUpgrade();

        /// <summary>GameManager.NotifyXxx 호출. Drill: NotifyDrillUpgraded / JailUpgrade: NotifyJailUpgraded / Tractor·MinerSpawn: 빈 (별도 progression 이벤트 무).</summary>
        protected abstract void RaiseProgressionEvent();

        // --- 베이스 본체 ---

        protected virtual void Awake()
        {
            // _cameraTransform Inspector 드래그 안되어있으면 Camera.main 폴백.
            if (_cameraTransform == null)
            {
                var mainCam = Camera.main;
                if (mainCam != null) _cameraTransform = mainCam.transform;
                else Debug.LogWarning("[UpgradeZone] _cameraTransform null + Camera.main null. Billboard rotation disabled.", this);
            }

            // 부분 결제 누적 — Awake에서 _remaining = Cost 초기화. 매 cycle 트윈 콜백에서 차감.
            _remaining = Cost;

            if (_costText == null)
                Debug.LogWarning($"[{GetType().Name}] _costText not assigned. Cost display + countdown 비활성.", this);
            else
                _costText.text = $"{DisplayName}\n${_remaining}";

            if (_billboardRoot == null)
                Debug.LogWarning($"[{GetType().Name}] _billboardRoot not assigned. LateUpdate yaw billboard 비활성.", this);

            // 결제 진행률 fill 초기화 — 0 (빈 게이지). null 가드 박음.
            if (_progressFill != null) _progressFill.fillAmount = 0f;

            // 초기 비활성 검사 — 파생 IsUnlockedAtStart() false면 즉시 SetActive(false).
            if (!IsUnlockedAtStart()) gameObject.SetActive(false);
        }

        /// <summary>파생이 GameManager.OnXxx += 구독 시 본 헬퍼를 handler로 박음. SetActive(true) 1줄.</summary>
        protected void HandleUnlock()
        {
            gameObject.SetActive(true);
        }

        protected override void OnPlayerEnter(PlayerController player)
        {
            if (_isProcessing) return;
            if (player == null || player.BackStack == null) return;
            if (_remaining <= 0) return; // 이미 결제 완료 (이론상 SetActive(false) 박힘 — 안전망).

            var settings = GameManager.Instance != null ? GameManager.Instance.Settings : null;
            if (settings == null) return;

            int moneyPerBlock = settings.MoneyPile.MoneyPerBlock;
            if (moneyPerBlock <= 0) return;

            // 4일차 부분 결제 — BackStack 박힌 만큼 박음. _remaining 박은 양 초과 박지 않음.
            int blocksNeeded = Mathf.CeilToInt((float)_remaining / moneyPerBlock);
            int availableBlocks = Mathf.Min(player.BackStack.Count, blocksNeeded);
            if (availableBlocks <= 0) return; // BackStack 빈 — 결제 무.

            _isProcessing = true;
            PerformPurchaseAsync(player, availableBlocks, moneyPerBlock).Forget();
        }

        // --- abstract 3 stub (InteractionZone 누적 미사용 — Desk/MoneyPile 패턴) ---
        protected override void OnAccumulatorTick(IInteractionUser user, float accumulated, float deltaTime) { }
        protected override bool IsAccumulatorComplete(IInteractionUser user, float accumulated) => false;
        protected override void OnInteractionComplete(IInteractionUser user) { }

        // --- 결제 + 트윈 ---

        private async UniTaskVoid PerformPurchaseAsync(PlayerController player, int blockCount, int moneyPerBlock)
        {
            var ct = this.destroyCancellationToken;

            var blocks = player.BackStack.RemoveRange(blockCount);
            if (blocks.Count == 0)
            {
                Debug.LogWarning($"[{GetType().Name}] BackStack RemoveRange returned empty. Aborting.", this);
                _isProcessing = false;
                return;
            }

            int blocksRemaining = blocks.Count;

            // 4일차 부분 결제 — _progressFill reset 박지 않음 (이전 cycle 박은 fill 누적 유지). 트윈 콜백에서 (Cost - _remaining)/Cost 갱신.

            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];               // closure 캡처 회피.
                TweenMoneyToZoneAsync(block, () =>
                {
                    // instance _remaining 박음 — 누적 차감. 다음 cycle 진입 시 _remaining 박음 정합.
                    _remaining = Mathf.Max(0, _remaining - moneyPerBlock);
                    blocksRemaining--;
                    if (_costText != null) _costText.text = $"{DisplayName}\n${_remaining}";
                    if (_progressFill != null && Cost > 0)
                    {
                        float progress = Mathf.Clamp01((float)(Cost - _remaining) / Cost);
                        _progressFill.fillAmount = progress;
                    }
                    if (block != null) block.ReturnToPool();
                }, ct).Forget();

                try { await UniTask.Delay(TWEEN_INTERVAL_MS, cancellationToken: ct); }
                catch (OperationCanceledException) { _isProcessing = false; return; }
            }

            // 모든 블록 도달 대기 (마지막 트윈 0.3s 도달까지 polling).
            try { await UniTask.WaitUntil(() => blocksRemaining <= 0, cancellationToken: ct); }
            catch (OperationCanceledException) { _isProcessing = false; return; }

            // 박은 만큼 차감 — 부분 결제 case 박은 cost 일부만. 잉여 case (cost not multiple of moneyPerBlock) 무 (본 프로젝트 cost 모두 moneyPerBlock 배수).
            int spentNow = blocks.Count * moneyPerBlock;
            if (GameManager.Instance != null) GameManager.Instance.TrySpendMoney(spentNow);

            if (_remaining <= 0)
            {
                ApplyUpgrade();
                RaiseProgressionEvent();
                gameObject.SetActive(false);
                // _isProcessing은 SetActive(false)로 다음 enable까지 무관.
            }
            else
            {
                // 부분 결제 — zone 활성 유지. 다음 OnPlayerEnter 박음.
                _isProcessing = false;
            }
        }

        private async UniTaskVoid TweenMoneyToZoneAsync(Stackable block, Action onArrive, CancellationToken ct)
        {
            if (block == null) { onArrive?.Invoke(); return; }
            Transform t = block.transform;
            if (t == null) { onArrive?.Invoke(); return; }

            // BackStack에서 분리 — world position 유지하며 parent null.
            t.SetParent(null, worldPositionStays: true);

            Vector3 startWorld = t.position;
            Vector3 endWorld = transform.position;
            float elapsed = 0f;

            try
            {
                while (elapsed < TWEEN_DURATION)
                {
                    ct.ThrowIfCancellationRequested();
                    if (t == null) return;

                    float p = elapsed / TWEEN_DURATION;
                    Vector3 lerp = Vector3.Lerp(startWorld, endWorld, p);
                    lerp.y += Mathf.Sin(p * Mathf.PI) * TWEEN_ARC_HEIGHT;
                    t.position = lerp;

                    elapsed += Time.deltaTime;
                    await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate, ct);
                }
            }
            catch (OperationCanceledException) { /* fall through to finally */ }
            finally
            {
                onArrive?.Invoke();
            }
        }

        // 발판 윗면 평면 텍스트 디자인 — Canvas RectTransform LocalRotation (90,0,0)으로 X-Z 평면에 정적 배치.
        // _billboardRoot / _cameraTransform 필드는 Inspector wiring 호환성 차원에서 존치 (LateUpdate yaw billboard 제거 후 미사용).
    }
}
