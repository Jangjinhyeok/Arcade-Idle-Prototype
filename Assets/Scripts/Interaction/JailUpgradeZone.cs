using UnityEngine;

namespace PrisonLife
{
    /// <summary>
    /// JailUpgrade — JailZone.IncreaseCapacity() 호출 (cap ×2: 20 → 40). RaiseProgressionEvent에서 NotifyJailUpgraded → 게이트 6 GameClearedUI 트리거.
    /// IsJailFull 도달 시 해금. 결제 1회로 영구 적용. handoff §4 #34 게임 종료 1회 upgrade라 이후 본 zone 영구 비활성.
    /// </summary>
    /// <remarks>
    /// 3일차 게이트 5c 결정 #27/#28/#34 구현 네 번째 파생.
    /// abstract 4개: Cost (Settings.Jail.JailUpgradeBaseCost = 50) / IsUnlockedAtStart (IsJailFull) / ApplyUpgrade (JailZone.IncreaseCapacity) / RaiseProgressionEvent (NotifyJailUpgraded).
    /// 이벤트 구독: GameManager.OnJailFull += HandleUnlock (Awake) / -= (OnDestroy).
    /// _jailZone null fallback LogError — 게임 종료 트리거는 발행되지만 capacity 미변경 위험이라 LogWarning보다 강한 수준.
    /// </remarks>
    public class JailUpgradeZone : UpgradeZone
    {
        [Tooltip("JailZone 컴포넌트 ref. ApplyUpgrade 시 IncreaseCapacity() 호출. GameManager._jailZone과 동일 GameObject 드래그. 3일차 게이트 5c.")]
        [SerializeField] private JailZone _jailZone;

        protected override int Cost => GameManager.Instance.Settings.Jail.JailUpgradeBaseCost;

        protected override string DisplayName => "Jail";

        protected override bool IsUnlockedAtStart() => GameManager.Instance != null && GameManager.Instance.IsJailFull;

        protected override void ApplyUpgrade()
        {
            if (_jailZone != null)
            {
                _jailZone.IncreaseCapacity();
            }
            else
            {
                Debug.LogError("[JailUpgradeZone] _jailZone not assigned. IncreaseCapacity no-op — 게임 종료 트리거는 발행되지만 capacity 미변경.", this);
            }
        }

        protected override void RaiseProgressionEvent()
        {
            if (GameManager.Instance != null) GameManager.Instance.NotifyJailUpgraded();
        }

        protected override void Awake()
        {
            base.Awake();
            if (GameManager.Instance != null) GameManager.Instance.OnJailFull += HandleUnlock;
        }

        private void OnDestroy()
        {
            if (GameManager.Instance != null) GameManager.Instance.OnJailFull -= HandleUnlock;
        }
    }
}
