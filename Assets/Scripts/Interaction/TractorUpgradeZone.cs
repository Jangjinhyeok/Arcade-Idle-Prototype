using UnityEngine;

namespace PrisonLife
{
    /// <summary>
    /// Tractor 업그레이드 — Player 자식 Tractor GameObject 활성화 + TractorMiner 콜라이더 sweep로 OrePlacement 즉시 mine.
    /// 효과는 PlayerController.UpdateVisuals (IsTractorUpgraded + Mine zone 진입 시 _tractorObject SetActive(true)) + MineZone.ProcessMining (Player + IsTractorUpgraded 시 누적 skip).
    /// IsDrillUpgraded 도달 시 해금. 결제 1회로 영구 적용.
    /// </summary>
    /// <remarks>
    /// 3일차 게이트 5c 결정 #27/#28 구현 두 번째 파생.
    /// abstract 4개: Cost (TractorUpgradeCost = 50) / IsUnlockedAtStart (IsDrillUpgraded) / ApplyUpgrade (NotifyTractorUpgraded — 플래그 set) / RaiseProgressionEvent (빈, OnTractorUnlocked 폐기 결정 6).
    /// 이벤트 구독: GameManager.OnDrillUpgraded += HandleUnlock (Awake) / -= (OnDestroy).
    /// ApplyUpgrade에 NotifyTractorUpgraded 박는 비대칭은 의미적 정합 — Tractor는 progression 이벤트 발행 안 함, 플래그 set은 효과 적용 측면.
    /// </remarks>
    public class TractorUpgradeZone : UpgradeZone
    {
        protected override int Cost => GameManager.Instance.Settings.Upgrade.TractorUpgradeCost;

        protected override string DisplayName => "Tractor";

        protected override bool IsUnlockedAtStart() => GameManager.Instance != null && GameManager.Instance.IsDrillUpgraded;

        protected override void ApplyUpgrade()
        {
            if (GameManager.Instance != null) GameManager.Instance.NotifyTractorUpgraded();
        }

        protected override void RaiseProgressionEvent()
        {
            // 빈 — Tractor unlock 이벤트 폐기 (결정 6). IsTractorUpgraded 플래그만 set, 5d OreNode가 폴링.
        }

        protected override void Awake()
        {
            base.Awake();
            if (GameManager.Instance != null) GameManager.Instance.OnDrillUpgraded += HandleUnlock;
        }

        private void OnDestroy()
        {
            if (GameManager.Instance != null) GameManager.Instance.OnDrillUpgraded -= HandleUnlock;
        }
    }
}
