using System;
using UnityEngine;

namespace PrisonLife
{
    /// <summary>
    /// 모든 밸런스 수치의 단일 진입점. ADR-001 §2.3 + R3.
    /// </summary>
    /// <remarks>
    /// 인스턴스(.asset)는 Assets/Settings/GameSettings.asset에 보관 (ADR-002 §2 비고).
    /// 런타임 접근은 GameManager.Instance.Settings.[Group].[Field] 형태로 통일 (R3).
    /// 그룹 필드는 nested [Serializable] 클래스로 분리하여 Inspector에서 헤더 단위로 접힘.
    /// </remarks>
    [CreateAssetMenu(fileName = "GameSettings", menuName = "PrisonLife/Game Settings", order = 1)]
    public class GameSettingsSO : ScriptableObject
    {
        [SerializeField] private PlayerSettings _player = new PlayerSettings();
        [SerializeField] private MineSettings _mine = new MineSettings();
        [SerializeField] private ProcessorSettings _processor = new ProcessorSettings();
        [SerializeField] private DeskSettings _desk = new DeskSettings();
        [SerializeField] private NPCSettings _npc = new NPCSettings();
        [SerializeField] private JailSettings _jail = new JailSettings();
        [SerializeField] private UpgradeSettings _upgrade = new UpgradeSettings();
        [SerializeField] private UISettings _ui = new UISettings();
        [SerializeField] private MoneyPileSettings _moneyPile = new MoneyPileSettings();

        public PlayerSettings Player => _player;
        public MineSettings Mine => _mine;
        public ProcessorSettings Processor => _processor;
        public DeskSettings Desk => _desk;
        public NPCSettings NPC => _npc;
        public JailSettings Jail => _jail;
        public UpgradeSettings Upgrade => _upgrade;
        public UISettings UI => _ui;
        public MoneyPileSettings MoneyPile => _moneyPile;
    }

    [Serializable]
    public class PlayerSettings
    {
        [Tooltip("플레이어 이동 속도 (units/sec). 기본값은 3일차 튜닝 시 결정.")]
        [SerializeField] private float _moveSpeed = 5f;

        public float MoveSpeed => _moveSpeed;
    }

    [Serializable]
    public class MineSettings
    {
        [Tooltip("Mine grid 행 수. MineZone.Awake에서 _gridRows × _gridCols 만큼 OrePlacement 시각 인스턴스 동적 생성 (배치용 mesh, trigger 무). 4일차 단일화 후 시각만 유지. handoff §4 #30 lock.")]
        [SerializeField] private int _gridRows = 8;

        [Tooltip("Mine grid 열 수. handoff §4 #30 lock.")]
        [SerializeField] private int _gridCols = 8;

        [Tooltip("OrePlacement 인스턴스 간 간격 (units). Mine grid 영역 = (cols-1) × _gridSpacing × (rows-1) × _gridSpacing. 4일차 단순화 — single spacing X/Z 동일.")]
        [SerializeField] private float _gridSpacing = 0.8f;

        [Tooltip("Player (Drill 미결제) ore 1개 채굴 누적 시간 (sec). default 1.0 (영상 정합). Drill 결제 후 MineZone이 IsDrillUpgraded 분기 → Settings.Upgrade.DrillUpgradeMineDuration(0.5) 사용. MinerNPC는 _minerNPCMineDurationPerOre 별도 필드 박음.")]
        [SerializeField] private float _mineDurationPerOre = 1.0f;

        [Tooltip("MinerNPC ore 1개 채굴 누적 시간 (sec). Player와 분리 — Drill/Tractor 효과 무관 고정값. 5일차 분리 (handoff §4 후속 결정).")]
        [SerializeField, Min(0.05f)] private float _minerNPCMineDurationPerOre = 1.0f;

        [Tooltip("기본 동시 spawn ore 수 (Player + MinerNPC). Tractor 결제 전 default 1. Tractor 결제 후엔 MineZone에서 ProcessMining skip + TractorMiner 콜라이더 sweep로 mine.")]
        [SerializeField] private int _simultaneousMineCount = 1;

        [Tooltip("OrePlacement 사라짐 → 재출현까지 걸리는 시간 (sec). 4일차 결함 1 — 채굴 시 user 가까운 OrePlacement 1~4개 SetActive(false), 본 시간 경과 후 SetActive(true) 자동 부활.")]
        [SerializeField] private float _orePlacementRespawnSeconds = 5f;

        [Tooltip("user 위치 기준 채굴 도달 반경 (units). 본 반경 안의 active OrePlacement만 hide 후보. 4일차 결함 보정 — player 가까운 ore 부족 시 멀리 active로 점프 박는 결함 차단. 기본 2.0 (gridSpacing 0.8 × ~2.5).")]
        [SerializeField] private float _mineReachRadius = 2f;

        public int GridRows => _gridRows;
        public int GridCols => _gridCols;
        public float GridSpacing => _gridSpacing;
        public float MineDurationPerOre => _mineDurationPerOre;
        public float MinerNPCMineDurationPerOre => _minerNPCMineDurationPerOre;
        public int SimultaneousMineCount => _simultaneousMineCount;
        public float OrePlacementRespawnSeconds => _orePlacementRespawnSeconds;
        public float MineReachRadius => _mineReachRadius;
    }

    [Serializable]
    public class ProcessorSettings
    {
        [Tooltip("광석 1개를 수갑 1개로 변환하는 누적 시간 (sec/tick).")]
        [SerializeField] private float _tickInterval = 1f;

        [Tooltip("수갑 1개 생성에 소비되는 광석 개수.")]
        [SerializeField] private int _oreConsumedPerHandcuff = 1;

        [Tooltip("ProcessorInputZone 광석 수용 한도. 기능 max — 도달 시 추가 transfer 거부.")]
        [SerializeField] private int _inputCapacity = 8;

        [Tooltip("ProcessorOutputZone 수갑 수용 한도. 기능 max — 도달 시 변환 루프 일시 정지.")]
        [SerializeField] private int _outputCapacity = 10;

        public float TickInterval => _tickInterval;
        public int OreConsumedPerHandcuff => _oreConsumedPerHandcuff;
        public int InputCapacity => _inputCapacity;
        public int OutputCapacity => _outputCapacity;
    }

    [Serializable]
    public class DeskSettings
    {
        [Tooltip("Desk 수갑 진열 한도. 기능 max — 도달 시 transfer 차단. DeskZone.Awake에서 _deskStack에 SetCapacity 코드 주입.")]
        [SerializeField] private int _handcuffCapacity = 8;

        [Tooltip("DeskNPC carry stack 한도 (개). DeskNPC가 ProcessorOutput에서 1회 픽업 시 받는 handcuff 최대 개수. 4일차 Desk + DeskNPC 시스템.")]
        [SerializeField, Min(1)] private int _deskNPCStackMax = 8;

        [Tooltip("DeskNPC AtDesk 진열 1개당 주기 (sec). DeskNPC.AtDesk 분기에서 _depositTimer 도달 시 _stack.Remove → _deskStack.Add 트윈 1회.")]
        [SerializeField, Min(0.05f)] private float _deskNPCDepositInterval = 0.2f;

        public int HandcuffCapacity => _handcuffCapacity;
        public int DeskNPCStackMax => _deskNPCStackMax;
        public float DeskNPCDepositInterval => _deskNPCDepositInterval;
    }

    [Serializable]
    public class NPCSettings
    {
        [Tooltip("Prisoner NavMeshAgent.speed (units/sec).")]
        [SerializeField] private float _prisonerMoveSpeed = 3f;

        [Tooltip("Worker NavMeshAgent.speed (units/sec). NICE 강등 — 3일차 미사용, 4일차 잔여 시간에 검토.")]
        [SerializeField] private float _workerMoveSpeed = 4f;

        [Tooltip("Prisoner spawn 폴링 interval (sec). PrisonerSpawner.Update가 Time.time - _lastSpawnTime >= 본 값일 때 1명 spawn 시도. 3일차 게이트 5a 결정 #22.")]
        [SerializeField] private float _spawnIntervalSeconds = 2f;

        [Tooltip("동시 NavMesh 위 prisoner 한도 (명). DeskZone Queue 슬롯 수와 정합. Queue full = max 도달 동의어로 가드 단일화. 3일차 게이트 5a 결정 #22 + Sub-Q 1-A.")]
        [SerializeField] private int _maxConcurrent = 3;

        [Tooltip("Prisoner 1명이 요청하는 수갑 최소 개수 (inclusive). PrisonerSpawner의 spawn 시점에 Random.Range로 결정 (3일차 게이트 5a — HandleHandcuffStocked 폐기, Update 폴링으로 대체).")]
        [SerializeField] private int _handcuffDemandMin = 1;

        [Tooltip("Prisoner 1명이 요청하는 수갑 최대 개수 (inclusive). Random.Range exclusive 호출 시 +1 보정 필요.")]
        [SerializeField] private int _handcuffDemandMax = 3;

        [Tooltip("MinerSpawnZone 1회 결제 시 spawn되는 MinerNPC 수. 영상 재확인 락 — handoff §9 \"3명\" 그대로 (3일차 게이트 5c Sub-Q 7).")]
        [SerializeField] private int _minerSpawnCount = 3;

        public float PrisonerMoveSpeed => _prisonerMoveSpeed;
        public float WorkerMoveSpeed => _workerMoveSpeed;
        public float SpawnIntervalSeconds => _spawnIntervalSeconds;
        public int MaxConcurrent => _maxConcurrent;
        public int HandcuffDemandMin => _handcuffDemandMin;
        public int HandcuffDemandMax => _handcuffDemandMax;
        public int MinerSpawnCount => _minerSpawnCount;
    }

    [Serializable]
    public class JailSettings
    {
        [Tooltip("JailZone 초기 capacity (명). 활성 jail object 수 한도. JailUpgradeZone 1회 결제 시 ×CapacityMultiplier로 증가. 3일차 게이트 5b 결정 #25.")]
        [SerializeField] private int _initialCapacity = 20;

        [Tooltip("JailUpgrade 1회 결제 시 capacity 곱 배율. 20 → 40 → 80 ... handoff §4 #25 락.")]
        [SerializeField] private int _capacityMultiplier = 2;

        [Tooltip("Grid slot 최대 한도 (명). Awake에서 _gridRows × _gridCols ≥ MaxCapacity 정합 검증. 초과 capacity 요청은 MaxCapacity로 clamp.")]
        [SerializeField] private int _maxCapacity = 100;

        [Tooltip("Grid 행 수. JailZone.Awake에서 _gridRows × _gridCols 만큼 Empty 동적 생성. 3일차 게이트 5b 결정 2-A.")]
        [SerializeField] private int _gridRows = 10;

        [Tooltip("Grid 열 수. _gridRows × _gridCols ≥ MaxCapacity 정합 필수.")]
        [SerializeField] private int _gridCols = 10;

        [Tooltip("Grid slot 간 간격 (units). Grid 영역 = _gridRows × _gridSpacing. 0.8 권장 (10×10 grid → 8.0 units 영역).")]
        [SerializeField] private float _gridSpacing = 0.8f;

        [Tooltip("JailUpgradeZone 1회 결제 비용 (Money). handoff §4 #34 게임 종료 = 1회 upgrade라 점진 증가 룰 미적용 (고정값). 3일차 게이트 5b 결정 3-A.")]
        [SerializeField] private int _jailUpgradeBaseCost = 50;

        [Tooltip("Jail object scale-up 트윈 길이 (sec). transform.localScale Lerp 0 → 1. 3일차 게이트 5b 결정 #26 + 4 (옵션 B).")]
        [SerializeField] private float _scaleUpDuration = 0.3f;

        public int InitialCapacity => _initialCapacity;
        public int CapacityMultiplier => _capacityMultiplier;
        public int MaxCapacity => _maxCapacity;
        public int GridRows => _gridRows;
        public int GridCols => _gridCols;
        public float GridSpacing => _gridSpacing;
        public int JailUpgradeBaseCost => _jailUpgradeBaseCost;
        public float ScaleUpDuration => _scaleUpDuration;
    }

    [Serializable]
    public class UpgradeSettings
    {
        [Tooltip("Drill 업그레이드 1회 비용 (Money). 영상 확인 락 (3일차 게이트 5c, handoff §9).")]
        [SerializeField] private int _drillUpgradeCost = 20;

        [Tooltip("Drill 업그레이드 후 Mine.MineDurationPerOre override 값 (sec). 영상 1.0 → 0.5. 게이트 5d OreNode가 본 값 폴링 (drill 업그레이드 완료 플래그 동시 체크).")]
        [SerializeField] private float _drillUpgradeMineDuration = 0.5f;

        [Tooltip("Tractor 업그레이드 1회 비용 (Money). handoff §9 임의 결정 락.")]
        [SerializeField] private int _tractorUpgradeCost = 50;

        [Tooltip("MinerSpawn 1회 결제 비용 (Money). handoff §9 임의 결정 락. ApplyUpgrade에서 MinerSpawner.gameObject.SetActive(true) (게이트 5d MinerSpawner 본체 작성).")]
        [SerializeField] private int _minerSpawnCost = 50;

        [Tooltip("DeskNPCSpawn 1회 결제 비용 (Money). 4일차 Desk + DeskNPC 시스템. ApplyUpgrade에서 DeskNPCSpawner.SetActive(true).")]
        [SerializeField, Min(0)] private int _deskNPCSpawnCost = 50;

        public int DrillUpgradeCost => _drillUpgradeCost;
        public float DrillUpgradeMineDuration => _drillUpgradeMineDuration;
        public int TractorUpgradeCost => _tractorUpgradeCost;
        public int MinerSpawnCost => _minerSpawnCost;
        public int DeskNPCSpawnCost => _deskNPCSpawnCost;
    }

    [Serializable]
    public class UISettings
    {
        [Tooltip("스택 가득 참 시 \"MAX\" 텍스트 표시 지연 (sec). 0이면 즉시 표시.")]
        [SerializeField] private float _maxTextShowDelay = 1f;

        public float MaxTextShowDelay => _maxTextShowDelay;
    }

    [Serializable]
    public class MoneyPileSettings
    {
        [Tooltip("Money block 1개당 단가 (Money 데이터 기준). Player MoneyPile 픽업 시 GameManager.AddMoney(blockCount * MoneyPerBlock) 호출.")]
        [SerializeField] private int _moneyPerBlock = 10;

        public int MoneyPerBlock => _moneyPerBlock;
    }
}
