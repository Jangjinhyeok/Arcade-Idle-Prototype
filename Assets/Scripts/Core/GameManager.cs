using System;
using UnityEngine;

namespace PrisonLife
{
    /// <summary>
    /// 앱 생명주기 진입점 + 단일 싱글톤. ADR-001 §2.3 R3 + R4.
    /// </summary>
    /// <remarks>
    /// 본 프로젝트의 유일한 싱글톤 (technical-preferences.md "싱글톤 남발 금지" 명시 예외).
    /// 모든 런타임 시스템은 GameManager.Instance.Settings 경유로 GameSettingsSO에 접근한다.
    /// Money는 R4에 따라 본 클래스가 직접 보유하고 OnMoneyChanged 이벤트로 변경을 통지한다.
    /// 단일 씬 빌드이므로 DontDestroyOnLoad는 호출하지 않는다.
    ///
    /// 3일차 게이트 5c progression 확장:
    /// - 신규 이벤트 4개 (OnFirstMoneyEarned / OnDrillUpgraded / OnJailFull / OnJailUpgraded).
    /// - 신규 상태 플래그 4개 (UpgradeZone Awake 초기 비활성 검사용).
    /// - JailZone.OnJailFull 직접 구독 + GameManager.OnJailFull 재발행 (sub-Q 4 옵션 a + 9 옵션 a-1).
    /// - AddMoney 첫 Money > 0 도달 시 OnFirstMoneyEarned 1회 발행 (idempotent).
    /// - NotifyDrillUpgraded / NotifyJailUpgraded — UpgradeZone에서 호출하는 진입점 (event 외부 .Invoke 불가 회피).
    /// </remarks>
    public class GameManager : MonoBehaviour
    {
        /// <summary>전역 인스턴스. Awake에서 활성화, OnDestroy에서 해제.</summary>
        public static GameManager Instance { get; private set; }

        [Tooltip("게임 전체 밸런스 데이터. Inspector에서 Assets/Settings/GameSettings.asset을 드래그한다.")]
        [SerializeField] private GameSettingsSO _settings;

        [Tooltip("JailZone ref. Awake에서 OnJailFull 구독 + GameManager.OnJailFull 재발행. 3일차 게이트 5c sub-Q 4 옵션 a + 9 옵션 a-1.")]
        [SerializeField] private JailZone _jailZone;

        /// <summary>모든 시스템의 GameSettingsSO 진입점 (R3).</summary>
        public GameSettingsSO Settings => _settings;

        /// <summary>현재 보유 재화 (R4). 변경은 AddMoney/TrySpendMoney만을 통해 이루어진다.</summary>
        public int Money { get; private set; }

        // --- progression 상태 플래그 4개 (게이트 5c) ---

        /// <summary>AddMoney 첫 호출 + Money > 0 도달 시 true. DrillUpgradeZone Awake 초기 비활성 검사용.</summary>
        public bool IsFirstMoneyEarned { get; private set; }

        /// <summary>DrillUpgradeZone 결제 완료 시 true. TractorUpgradeZone + MinerSpawnZone Awake 초기 비활성 검사용 + OreNode가 MineDurationPerOre 폴링 시 분기 (게이트 5d).</summary>
        public bool IsDrillUpgraded { get; private set; }

        /// <summary>TractorUpgradeZone 결제 완료 시 true. 게이트 5d OreNode가 SimultaneousMineCount 1 → 4 분기 시 폴링. 이벤트 발행 안 함 (결정 6 OnTractorUnlocked 폐기 정합).</summary>
        public bool IsTractorUpgraded { get; private set; }

        /// <summary>JailZone.OnJailFull 발행 시 true. JailUpgradeZone Awake 초기 비활성 검사용.</summary>
        public bool IsJailFull { get; private set; }

        /// <summary>JailUpgradeZone 결제 완료 시 true. 게이트 6 GameClearedUI 트리거.</summary>
        public bool IsJailUpgraded { get; private set; }

        // --- 이벤트 ---

        /// <summary>Money 값이 변경된 직후 1회 발행. 인자는 변경 후 값. MoneyHUD가 구독한다.</summary>
        public event Action<int> OnMoneyChanged;

        /// <summary>AddMoney 첫 호출 + Money > 0 도달 시 1회 발행. DrillUpgradeZone 해금 트리거.</summary>
        public event Action OnFirstMoneyEarned;

        /// <summary>DrillUpgradeZone 결제 완료 시 NotifyDrillUpgraded 경유로 1회 발행. TractorUpgradeZone + MinerSpawnZone 동시 해금 트리거.</summary>
        public event Action OnDrillUpgraded;

        /// <summary>TractorUpgradeZone 결제 완료 시 NotifyTractorUpgraded 경유로 1회 발행. PlayerController.UpdateVisuals 트리거 전용. OreNode는 매 frame IsTractorUpgraded 폴링 (병행). 게이트 6.5 — 결정 6 부분 되돌림.</summary>
        public event Action OnTractorUpgraded;

        /// <summary>JailZone.OnJailFull 구독 후 GameManager 측에서 재발행 (sub-Q 4 옵션 a). JailUpgradeZone 해금 트리거.</summary>
        public event Action OnJailFull;

        /// <summary>JailUpgradeZone 결제 완료 시 NotifyJailUpgraded 경유로 1회 발행. 게이트 6 GameClearedUI 트리거.</summary>
        public event Action OnJailUpgraded;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            if (_settings == null)
            {
                Debug.LogError("[GameManager] _settings is not assigned. Drag Assets/Settings/GameSettings.asset into the Inspector slot.", this);
            }
            if (_jailZone == null)
            {
                Debug.LogWarning("[GameManager] _jailZone not assigned. OnJailFull will not propagate. JailUpgradeZone unlock 차단.", this);
            }
            else
            {
                _jailZone.OnJailFull += HandleJailFull;
            }
        }

        private void OnDestroy()
        {
            if (_jailZone != null) _jailZone.OnJailFull -= HandleJailFull;
            if (Instance == this) Instance = null;
        }

        /// <summary>재화 획득 진입점. Money pickup 흡수 완료 시점에 호출. 첫 Money > 0 도달 시 OnFirstMoneyEarned 1회 발행.</summary>
        public void AddMoney(int amount)
        {
            if (amount <= 0) return;
            Money += amount;
            OnMoneyChanged?.Invoke(Money);

            // 게이트 5c — 첫 Money 도달 1회 트리거. idempotent (IsFirstMoneyEarned 가드).
            if (!IsFirstMoneyEarned && Money > 0)
            {
                IsFirstMoneyEarned = true;
                OnFirstMoneyEarned?.Invoke();
            }
        }

        /// <summary>재화 소비 진입점. UpgradeZone 결제 시점에 호출. 잔액 부족 시 false.</summary>
        public bool TrySpendMoney(int cost)
        {
            if (cost < 0) return false;
            if (Money < cost) return false;
            Money -= cost;
            OnMoneyChanged?.Invoke(Money);
            return true;
        }

        /// <summary>DrillUpgradeZone.RaiseProgressionEvent에서 호출. idempotent. TractorUpgradeZone + MinerSpawnZone 동시 해금 트리거.</summary>
        public void NotifyDrillUpgraded()
        {
            if (IsDrillUpgraded) return;
            IsDrillUpgraded = true;
            OnDrillUpgraded?.Invoke();
        }

        /// <summary>TractorUpgradeZone.ApplyUpgrade에서 호출. idempotent. OnTractorUpgraded 이벤트 발행 (게이트 6.5 — 결정 6 부분 되돌림, PlayerController.UpdateVisuals 트리거 전용). OreNode는 매 frame IsTractorUpgraded 플래그 폴링 (병행).</summary>
        public void NotifyTractorUpgraded()
        {
            if (IsTractorUpgraded) return;
            IsTractorUpgraded = true;
            OnTractorUpgraded?.Invoke();
        }

        /// <summary>JailUpgradeZone.RaiseProgressionEvent에서 호출. idempotent. 게이트 6 게임 종료 트리거.</summary>
        public void NotifyJailUpgraded()
        {
            if (IsJailUpgraded) return;
            IsJailUpgraded = true;
            OnJailUpgraded?.Invoke();
        }

        /// <summary>JailZone.OnJailFull 구독 핸들러. JailUpgradeZone unlock 트리거. 내부 사용 — 외부 호출 안 함.</summary>
        private void HandleJailFull()
        {
            if (IsJailFull) return;
            IsJailFull = true;
            OnJailFull?.Invoke();
        }
    }
}
