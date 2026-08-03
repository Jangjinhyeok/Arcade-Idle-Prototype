using UnityEngine;

namespace PrisonLife
{
    /// <summary>
    /// DeskNPCSpawn — DeskNPCSpawner GameObject SetActive(true). 4일차 Desk + DeskNPC 시스템.
    /// IsDrillUpgraded 도달 시 해금. 결제 1회로 영구 적용.
    /// </summary>
    /// <remarks>
    /// MinerSpawnZone 패턴 복제 — UpgradeZone 5번째 파생.
    /// abstract 5개: Cost (DeskNPCSpawnCost = 50) / DisplayName ("Cashier") / IsUnlockedAtStart (IsDrillUpgraded) / ApplyUpgrade (_deskNPCSpawner.SetActive(true)) / RaiseProgressionEvent (빈, 별도 progression 이벤트 무).
    /// 이벤트 구독: GameManager.OnDrillUpgraded += HandleUnlock (Awake) / -= (OnDestroy).
    /// _deskNPCSpawner는 신규 GameObject SetActive(false) 시작 — 본 결제 시 활성화 → DeskNPCSpawner.OnEnable이 SpawnInitial 1회 호출.
    /// </remarks>
    public class DeskNPCSpawnUpgradeZone : UpgradeZone
    {
        [Tooltip("DeskNPCSpawner GameObject ref. ApplyUpgrade 시 SetActive(true). Inspector 드래그.")]
        [SerializeField] private GameObject _deskNPCSpawner;

        protected override int Cost => GameManager.Instance.Settings.Upgrade.DeskNPCSpawnCost;

        protected override string DisplayName => "Cashier";

        protected override bool IsUnlockedAtStart() => GameManager.Instance != null && GameManager.Instance.IsDrillUpgraded;

        protected override void ApplyUpgrade()
        {
            if (_deskNPCSpawner != null)
            {
                _deskNPCSpawner.SetActive(true);
            }
            else
            {
                Debug.LogWarning("[DeskNPCSpawnUpgradeZone] _deskNPCSpawner not assigned. ApplyUpgrade no-op. Inspector slot 드래그 필요.", this);
            }
        }

        protected override void RaiseProgressionEvent()
        {
            // 빈 — DeskNPCSpawn 별도 progression 이벤트 무 (MinerSpawnZone 패턴 정합).
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
