using UnityEngine;

namespace PrisonLife
{
    /// <summary>
    /// Drill 업그레이드 — Mine.MineDurationPerOre 1.0 → Settings.Upgrade.DrillUpgradeMineDuration (0.5).
    /// 효과는 OreNode가 GameManager.IsDrillUpgraded 폴링 (게이트 5d). 본 클래스는 NotifyDrillUpgraded만 호출.
    /// IsFirstMoneyEarned 도달 시 해금. 결제 1회로 영구 적용.
    /// </summary>
    /// <remarks>
    /// 3일차 게이트 5c 결정 #27/#28 구현 첫 파생.
    /// abstract 4개: Cost (DrillUpgradeCost = 20) / IsUnlockedAtStart (IsFirstMoneyEarned) / ApplyUpgrade (빈) / RaiseProgressionEvent (NotifyDrillUpgraded).
    /// 이벤트 구독: GameManager.OnFirstMoneyEarned += HandleUnlock (Awake) / -= (OnDestroy).
    /// </remarks>
    public class DrillUpgradeZone : UpgradeZone
    {
        protected override int Cost => GameManager.Instance.Settings.Upgrade.DrillUpgradeCost;

        protected override string DisplayName => "Drill";

        protected override bool IsUnlockedAtStart() => GameManager.Instance != null && GameManager.Instance.IsFirstMoneyEarned;

        protected override void ApplyUpgrade()
        {
            // 빈 — OreNode가 게이트 5d에서 GameManager.IsDrillUpgraded 폴링 + Settings.Upgrade.DrillUpgradeMineDuration 사용.
        }

        protected override void RaiseProgressionEvent()
        {
            if (GameManager.Instance != null) GameManager.Instance.NotifyDrillUpgraded();
        }

        protected override void Awake()
        {
            base.Awake();
            if (GameManager.Instance != null) GameManager.Instance.OnFirstMoneyEarned += HandleUnlock;
        }

        private void OnDestroy()
        {
            if (GameManager.Instance != null) GameManager.Instance.OnFirstMoneyEarned -= HandleUnlock;
        }
    }
}
