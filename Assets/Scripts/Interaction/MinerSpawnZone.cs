using UnityEngine;

namespace PrisonLife
{
    /// <summary>
    /// MinerSpawn — MinerSpawner GameObject SetActive(true). 5d MinerSpawner.OnEnable에서 SpawnInitial(Settings.NPC.MinerSpawnCount = 3) 호출 예정.
    /// IsDrillUpgraded 도달 시 해금. 결제 1회로 영구 적용.
    /// </summary>
    /// <remarks>
    /// 3일차 게이트 5c 결정 #27/#28 구현 세 번째 파생.
    /// abstract 4개: Cost (MinerSpawnCost = 50) / IsUnlockedAtStart (IsDrillUpgraded) / ApplyUpgrade (_minerSpawner.SetActive(true)) / RaiseProgressionEvent (빈, MinerSpawn 별도 progression 이벤트 무).
    /// 이벤트 구독: GameManager.OnDrillUpgraded += HandleUnlock (Awake) / -= (OnDestroy).
    /// _minerSpawner는 5d 본체 작성 후 Inspector 드래그 — 5c 시점엔 null 상태 LogWarning 1회 (의도된 동작).
    /// </remarks>
    public class MinerSpawnZone : UpgradeZone
    {
        [Tooltip("MinerSpawner GameObject ref. ApplyUpgrade 시 SetActive(true). 5d MinerSpawner 본체 작성 후 Inspector 드래그. 5c 시점엔 null fallback LogWarning. 3일차 게이트 5c.")]
        [SerializeField] private GameObject _minerSpawner;

        protected override int Cost => GameManager.Instance.Settings.Upgrade.MinerSpawnCost;

        protected override string DisplayName => "Miners";

        protected override bool IsUnlockedAtStart() => GameManager.Instance != null && GameManager.Instance.IsDrillUpgraded;

        protected override void ApplyUpgrade()
        {
            if (_minerSpawner != null)
            {
                _minerSpawner.SetActive(true);
            }
            else
            {
                Debug.LogWarning("[MinerSpawnZone] _minerSpawner not assigned. ApplyUpgrade no-op. 5d MinerSpawner 작성 후 Inspector slot 드래그 필요.", this);
            }
        }

        protected override void RaiseProgressionEvent()
        {
            // 빈 — MinerSpawn 별도 progression 이벤트 무.
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
