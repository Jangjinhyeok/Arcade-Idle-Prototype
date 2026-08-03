using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;

namespace PrisonLife
{
    /// <summary>
    /// Mine 영역 단일 채굴 zone. 4일차 결정 — Mine 단일화 (이전 OreNode 64개 분산 trigger 폐기).
    /// 8x8 grid OrePlacement 시각 인스턴스 동적 생성 + 단일 ObjectPool&lt;Stackable&gt; 보유 + 본체 BoxCollider isTrigger 채굴 책임.
    /// Player + MinerNPC 동일 zone 진입 → OnTriggerStay accumulator 누적 → duration 도달 시 amount개 ore spawn → user.PushOre 호출.
    /// </summary>
    /// <remarks>
    /// OreNode/TickMine/TryClaim 폐기 — 자원 무한 (OrePlacement 단위 cooldown만 박힘, 4일차 결함 1).
    /// 채굴 분기:
    ///   Player (Drill 유무 무관) → ProcessMining 누적 패턴, amount = Settings.Mine.SimultaneousMineCount (1)
    ///   Player + IsTractorUpgraded → ProcessMining skip — TractorMiner가 콜라이더 sweep로 닿는 OrePlacement 즉시 mine 박음 (MineSinglePlacement 직접 호출).
    ///   MinerNPC → 누적 패턴, amount 1 고정.
    /// Drill 분기 (Player 한정):
    ///   IsDrillUpgraded → duration = Settings.Upgrade.DrillUpgradeMineDuration (0.5)
    ///   그 외 → duration = Settings.Mine.MineDurationPerOre (1.0)
    /// MinerNPC는 Drill/Tractor 효과 무시 — duration = Settings.Mine.MinerNPCMineDurationPerOre, amount 1 고정 (5d 패턴 정합).
    /// 시각 효과 (4일차 결함 1):
    ///   1 cycle 도달 시 user 가까운 active OrePlacement amount개 SetActive(false) → _hidden 등록 → Settings.Mine.OrePlacementRespawnSeconds (5.0s) 후 자동 SetActive(true).
    ///   ore spawn 위치 = 사라진 OrePlacement 위치 (그 자리에서 ore 튀어나오는 시각 정합).
    ///   active OrePlacement 부족 시 (전부 cooldown 중) actualAmount 동적 감소 + 다음 cycle 즉시 재시도.
    /// PrisonerNPC 등 ore 미수신 user는 OnTriggerStay 분기에서 skip.
    /// NavMeshAgent + isTrigger 호환 위험: MinerNPC가 trigger 진입 미발동 시 채굴 정지. Play 검증에서 실패 시 MineZone에 명시적 Tick 호출 박는 방안으로 전환.
    /// </remarks>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BoxCollider))]
    public class MineZone : MonoBehaviour
    {
        [Tooltip("배치용 prefab (mesh + material 시각 인스턴스). Stackable 미부착. BoxCollider isTrigger 부착 박힘 (TractorMiner sweep 감지용 — 5일차 Tractor 변경). Assets/Prefabs/Pickups/OrePlacement.prefab 드래그.")]
        [SerializeField] private GameObject _orePlacementPrefab;

        [Tooltip("Stack용 prefab (Stackable 컴포넌트 보유). Assets/Prefabs/Pickups/Ore.prefab 드래그. ObjectPool에서 spawn.")]
        [SerializeField] private GameObject _oreStackPrefab;

        [Tooltip("Player ref. OnTriggerEnter/Exit 시 OnPlayerInsideChanged 이벤트 발행 + 채굴 amount/duration 분기. 미부착 시 Player 채굴 비활성 (LogWarning 1회).")]
        [SerializeField] private PlayerController _player;

        private struct HiddenPlacement
        {
            public GameObject Placement;
            public float Elapsed;
        }

        private ObjectPool<Stackable> _orePool;
        private readonly List<GameObject> _orePlacements = new List<GameObject>();
        private readonly Dictionary<IInteractionUser, float> _userAccumulators = new Dictionary<IInteractionUser, float>();
        private readonly List<HiddenPlacement> _hidden = new List<HiddenPlacement>();
        private readonly List<(GameObject p, float sqr)> _candidates = new List<(GameObject, float)>();
        private readonly List<Vector3> _justHiddenPositions = new List<Vector3>();
        private bool _isPlayerInside;

        /// <summary>Player의 Mine 영역 진입/이탈 시 발행. PlayerController.UpdateVisuals 트리거 (Drill/Tractor object 토글 + Animator IsMining 분기). 게이트 6.5 도입 — 4일차 단일화 후 그대로 유지.</summary>
        public event Action<bool> OnPlayerInsideChanged;

        private void Awake()
        {
            if (_orePlacementPrefab == null)
            {
                Debug.LogError("[MineZone] _orePlacementPrefab not assigned. 배치 시각 무 — Inspector slot 드래그 필수.", this);
            }
            if (_oreStackPrefab == null)
            {
                Debug.LogError("[MineZone] _oreStackPrefab not assigned. Mine no-op.", this);
                return;
            }
            if (_player == null)
            {
                Debug.LogWarning("[MineZone] _player not assigned. Player 채굴 분기 비활성. Inspector slot 드래그 필요.", this);
            }

            var settings = GameManager.Instance != null ? GameManager.Instance.Settings : null;
            if (settings == null)
            {
                Debug.LogError("[MineZone] GameManager.Settings null. Mine no-op.", this);
                return;
            }

            _orePool = new ObjectPool<Stackable>(
                createFunc: CreateOre,
                actionOnGet: OnGetOre,
                actionOnRelease: OnReleaseOre,
                actionOnDestroy: OnDestroyOre,
                collectionCheck: false,
                defaultCapacity: 64,
                maxSize: 128);

            SpawnPlacements(settings.Mine.GridRows, settings.Mine.GridCols, settings.Mine.GridSpacing);
        }

        private void OnDestroy()
        {
            if (_orePool != null)
            {
                _orePool.Dispose();
                _orePool = null;
            }
        }

        private void SpawnPlacements(int rows, int cols, float spacing)
        {
            if (_orePlacementPrefab == null) return;
            float originX = -(cols - 1) * spacing * 0.5f;
            float originZ = -(rows - 1) * spacing * 0.5f;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    var go = Instantiate(_orePlacementPrefab, transform);
                    go.transform.localPosition = new Vector3(originX + c * spacing, 0f, originZ + r * spacing);
                    go.transform.localRotation = Quaternion.identity;
                    go.name = $"OrePlacement_{r}_{c}";
                    _orePlacements.Add(go);
                }
            }
        }

        // --- Trigger 채굴 ---

        private void OnTriggerEnter(Collider other)
        {
            var user = other.GetComponentInParent<IInteractionUser>();
            if (user == null) return;
            if (!IsOreReceiver(user)) return;

            // accumulator 초기화 (재진입 시 누적 리셋).
            _userAccumulators[user] = 0f;

            // Player 진입 — OnPlayerInsideChanged 1회 발행.
            if (user is PlayerController && !_isPlayerInside)
            {
                _isPlayerInside = true;
                OnPlayerInsideChanged?.Invoke(true);
            }
        }

        private void OnTriggerStay(Collider other)
        {
            var user = other.GetComponentInParent<IInteractionUser>();
            if (user == null) return;
            // MinerNPC는 NavMeshAgent + isTrigger 호환 위험 회피 — MineZone.Tick 명시적 호출 박는 패턴 (5d 핫픽스 정합). OnTriggerStay 분기에서 skip.
            if (user is MinerNPC) return;
            if (!IsOreReceiver(user)) return;

            // 4일차 결함 보정 — Player 다중 Collider 박은 case 한 Collider Exit 박은 후 다른 Collider 여전히 trigger 안 박는 시점에
            // _isPlayerInside 박은 게 false 박음 → Tractor object 사라짐. OnTriggerStay 박은 게 idempotent recovery 박음.
            if (user is PlayerController && !_isPlayerInside)
            {
                _isPlayerInside = true;
                OnPlayerInsideChanged?.Invoke(true);
            }

            ProcessMining(user, other.transform.position, Time.deltaTime);
        }

        /// <summary>MinerNPC.Update의 MiningOre 분기에서 호출. NavMeshAgent + isTrigger 호환 위험 회피용 명시적 진입점 (5d 핫픽스 정합).
        /// Player는 OnTriggerStay 자동 박음 — 본 메서드 호출 안 박음.</summary>
        public void Tick(IInteractionUser user, Vector3 userPos, float deltaTime)
        {
            if (user == null) return;
            if (!IsOreReceiver(user)) return;
            ProcessMining(user, userPos, deltaTime);
        }

        /// <summary>MinerNPC가 풀 회수 / 비활성 시 호출. _userAccumulators dict stale entry 정리.</summary>
        public void ReleaseUser(IInteractionUser user)
        {
            if (user == null) return;
            _userAccumulators.Remove(user);
        }

        /// <summary>userPos 기준 reach 안 active OrePlacement 존재 여부. MinerNPC.MiningOre가 매 frame 검사 — 무 시 새 active 위치로 재이동 트리거.</summary>
        public bool HasActiveInReach(Vector3 userPos)
        {
            var settings = GameManager.Instance != null ? GameManager.Instance.Settings : null;
            if (settings == null) return false;
            return CountActiveInReach(userPos, settings) > 0;
        }

        /// <summary>userPos 기준 가장 가까운 active OrePlacement world 위치. reach 무관 (전체 검색). MinerNPC가 reach 안 active 무 시 호출 — 멀리 active로 재이동 destination 산출.</summary>
        public bool TryFindNearestActivePlacement(Vector3 userPos, out Vector3 worldPos)
        {
            worldPos = Vector3.zero;
            GameObject best = null;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < _orePlacements.Count; i++)
            {
                var p = _orePlacements[i];
                if (p == null || !p.activeSelf) continue;
                float sqr = (p.transform.position - userPos).sqrMagnitude;
                if (sqr < bestSqr) { bestSqr = sqr; best = p; }
            }
            if (best == null) return false;
            worldPos = best.transform.position;
            return true;
        }

        private void ProcessMining(IInteractionUser user, Vector3 userPos, float deltaTime)
        {
            if (_orePool == null) return;

            var settings = GameManager.Instance != null ? GameManager.Instance.Settings : null;
            if (settings == null) return;

            // Tractor 모드 — Player + IsTractorUpgraded 시 ProcessMining skip. TractorMiner가 콜라이더 sweep로 MineSinglePlacement 직접 호출.
            if (user is PlayerController && GameManager.Instance != null && GameManager.Instance.IsTractorUpgraded)
            {
                _userAccumulators[user] = 0f;
                return;
            }

            float duration = ResolveDuration(user, settings);
            if (duration <= 0f) return;

            // reach 안 active 무 시 accum 0 reset.
            // 결함 보정: Player가 reach 밖에서 누적 spam 박은 후 reach 안 active로 이동 시 즉시 채굴 박는 결함 차단.
            if (CountActiveInReach(userPos, settings) == 0)
            {
                _userAccumulators[user] = 0f;
                return;
            }

            float accum = _userAccumulators.TryGetValue(user, out var prev) ? prev : 0f;
            accum += deltaTime;

            if (accum >= duration)
            {
                int amount = ResolveAmount(user, settings);
                int actualAmount = HidePlacementsNearUser(userPos, amount, _justHiddenPositions);

                if (actualAmount > 0)
                {
                    accum -= duration;
                    for (int i = 0; i < actualAmount; i++)
                    {
                        SpawnOreAt(_justHiddenPositions[i], user);
                    }
                }
                else
                {
                    // race (cycle 도달 frame에 마지막 active hide된 경우 등) — accum 0 reset.
                    accum = 0f;
                }
            }

            _userAccumulators[user] = accum;
        }

        /// <summary>TractorMiner.OnTriggerStay에서 호출. 콜라이더 진입 OrePlacement 즉시 hide + ore spawn + push to user.
        /// 이미 비활성(cooldown 중) 또는 _orePlacements에 미등록 GameObject는 skip — 외부 호출 안전.
        /// duration 누적 무관 — 진입 즉시 1 hit (4일차 후속 결정).</summary>
        public void MineSinglePlacement(GameObject placement, IInteractionUser user)
        {
            if (placement == null || user == null) return;
            if (_orePool == null) return;
            if (!placement.activeSelf) return;
            if (!_orePlacements.Contains(placement)) return;

            Vector3 spawnPos = placement.transform.position;
            placement.SetActive(false);
            _hidden.Add(new HiddenPlacement { Placement = placement, Elapsed = 0f });
            SpawnOreAt(spawnPos, user);
        }

        /// <summary>user 위치 기준 reach 안 active OrePlacement 개수. ProcessMining 매 frame reset 분기 + HidePlacementsNearUser race 가드용.</summary>
        private int CountActiveInReach(Vector3 userPos, GameSettingsSO settings)
        {
            float reachSqr = settings.Mine.MineReachRadius * settings.Mine.MineReachRadius;
            int count = 0;
            for (int i = 0; i < _orePlacements.Count; i++)
            {
                var p = _orePlacements[i];
                if (p == null || !p.activeSelf) continue;
                if ((p.transform.position - userPos).sqrMagnitude > reachSqr) continue;
                count++;
            }
            return count;
        }

        private void Update()
        {
            if (_hidden.Count == 0) return;
            var settings = GameManager.Instance != null ? GameManager.Instance.Settings : null;
            if (settings == null) return;
            float respawn = settings.Mine.OrePlacementRespawnSeconds;
            float dt = Time.deltaTime;

            // 인덱스 역순 — 도달 시 RemoveAt 안전. struct value type 재할당 박음.
            for (int i = _hidden.Count - 1; i >= 0; i--)
            {
                var h = _hidden[i];
                h.Elapsed += dt;
                if (h.Elapsed >= respawn)
                {
                    if (h.Placement != null) h.Placement.SetActive(true);
                    _hidden.RemoveAt(i);
                }
                else
                {
                    _hidden[i] = h;
                }
            }
        }

        private void OnTriggerExit(Collider other)
        {
            var user = other.GetComponentInParent<IInteractionUser>();
            if (user == null) return;

            _userAccumulators.Remove(user);

            if (user is PlayerController && _isPlayerInside)
            {
                _isPlayerInside = false;
                OnPlayerInsideChanged?.Invoke(false);
            }
        }

        // --- 채굴 분기 헬퍼 ---

        private static bool IsOreReceiver(IInteractionUser user)
        {
            // Player + MinerNPC만 채굴 수신. 그 외 (PrisonerNPC 등) 진입은 무시.
            return user is PlayerController || user is MinerNPC;
        }

        private static float ResolveDuration(IInteractionUser user, GameSettingsSO settings)
        {
            if (user is PlayerController && GameManager.Instance != null && GameManager.Instance.IsDrillUpgraded)
            {
                return settings.Upgrade.DrillUpgradeMineDuration;
            }
            if (user is MinerNPC)
            {
                return settings.Mine.MinerNPCMineDurationPerOre;
            }
            return settings.Mine.MineDurationPerOre;
        }

        private static int ResolveAmount(IInteractionUser user, GameSettingsSO settings)
        {
            // Tractor amount 분기 폐기 — Tractor 모드는 ProcessMining에서 skip됨 (콜라이더 sweep 패턴).
            return settings.Mine.SimultaneousMineCount;
        }

        private void SpawnOreAt(Vector3 spawnPos, IInteractionUser user)
        {
            var ore = _orePool.Get();
            if (ore == null) return;

            ore.transform.SetParent(transform, worldPositionStays: false);
            ore.transform.position = spawnPos;
            ore.transform.localRotation = Quaternion.identity;

            switch (user)
            {
                case PlayerController player:
                    player.PushOre(ore);
                    break;
                case MinerNPC miner:
                    miner.PushOre(ore);
                    break;
                default:
                    ore.ReturnToPool();
                    break;
            }
        }

        /// <summary>user 위치 기준 Settings.Mine.MineReachRadius 안 active OrePlacement count개 SetActive(false) + _hidden 등록 + 사라진 위치 outPositions에 박음. 실제 사라진 개수 반환.
        /// 반경 안 active 부족 시 actualAmount 동적 감소 — caller (ProcessMining)가 0 반환 시 accum 차감 skip 박아 다음 frame 즉시 재시도.</summary>
        private int HidePlacementsNearUser(Vector3 userPos, int count, List<Vector3> outPositions)
        {
            outPositions.Clear();
            if (_orePlacements.Count == 0 || count <= 0) return 0;

            var settings = GameManager.Instance != null ? GameManager.Instance.Settings : null;
            float reachSqr = settings != null
                ? settings.Mine.MineReachRadius * settings.Mine.MineReachRadius
                : float.MaxValue;

            _candidates.Clear();
            for (int i = 0; i < _orePlacements.Count; i++)
            {
                var p = _orePlacements[i];
                if (p == null) continue;
                if (!p.activeSelf) continue;
                float sqr = (p.transform.position - userPos).sqrMagnitude;
                if (sqr > reachSqr) continue;       // 반경 밖 — 채굴 도달 무.
                _candidates.Add((p, sqr));
            }
            if (_candidates.Count == 0) return 0;

            _candidates.Sort((a, b) => a.sqr.CompareTo(b.sqr));

            int take = Mathf.Min(count, _candidates.Count);
            for (int i = 0; i < take; i++)
            {
                var p = _candidates[i].p;
                outPositions.Add(p.transform.position);
                p.SetActive(false);
                _hidden.Add(new HiddenPlacement { Placement = p, Elapsed = 0f });
            }
            return take;
        }

        // --- Pool 콜백 4개 ---

        private Stackable CreateOre()
        {
            var go = Instantiate(_oreStackPrefab, transform);
            var s = go.GetComponent<Stackable>();
            if (s == null)
            {
                Debug.LogError("[MineZone] _oreStackPrefab has no Stackable component on root. Pool will return null.", this);
                Destroy(go);
                return null;
            }
            return s;
        }

        private void OnGetOre(Stackable ore)
        {
            if (ore == null) return;
            ore.gameObject.SetActive(true);
            ore.OriginPool = _orePool;
        }

        private void OnReleaseOre(Stackable ore)
        {
            if (ore == null) return;
            ore.gameObject.SetActive(false);
            ore.transform.SetParent(transform, worldPositionStays: false);
        }

        private void OnDestroyOre(Stackable ore)
        {
            if (ore != null) Destroy(ore.gameObject);
        }
    }
}
