using UnityEngine;
using UnityEngine.AI;

namespace PrisonLife
{
    /// <summary>
    /// Miner NPC. 4일차 Mine 단일화 — MineZone 본체 trigger 진입 → OnTriggerStay accumulator 자동 누적 → ore spawn 시 PushOre(ore) 콜.
    /// </summary>
    /// <remarks>
    /// FSM 3상태: Spawning → MovingToOre → MiningOre (영구 대기, MineZone trigger 안 정지).
    /// 이전 OreNode/TickMine/FindAvailableOreNode/TryClaim/_targetOreNode 모두 폐기 (4일차 Mine 단일화).
    /// 이동 목표: _mineZone.transform.position 고정. NavMeshAgent.remainingDistance ≤ ARRIVAL_DISTANCE 도달 시 MiningOre 전환 + isStopped = true.
    /// MiningOre 상태에서 NavMeshAgent 정지 + MineZone trigger 안 머묾 → MineZone.OnTriggerStay가 본 NPC를 IInteractionUser로 식별하여 자동 누적.
    /// 채굴 완료 시점 MineZone이 PushOre(ore) 호출 → ore가 _processor.PushOreFromMiner 경유 _inputStack 슬롯으로 트윈 (StackContainer.Add).
    /// Drill/Tractor 효과 무시 (MineZone.ResolveDuration/ResolveAmount는 PlayerController만 분기).
    /// 위험: NavMeshAgent + isTrigger 조합 trigger 미발동 케이스 — Play 검증 실패 시 MineZone에 명시적 Tick 호출 박는 방안으로 전환 예정.
    /// </remarks>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NavMeshAgent))]
    public class MinerNPC : NPCBase
    {
        private enum MinerState { Spawning, MovingToOre, MiningOre }

        private const float ARRIVAL_DISTANCE = 0.5f;

        [Tooltip("NavMeshAgent. Inspector 주입 우선, 비어있으면 Awake에서 GetComponent fallback.")]
        [SerializeField] private NavMeshAgent _agent;

        [Tooltip("Animator. char03_X.prefab Animator 컴포넌트 ref. CharacterAnimator Controller + IsMoving + IsMining 파라미터.")]
        [SerializeField] private Animator _animator;

        private MineZone _mineZone;
        private ProcessorMachine _processor;
        private MinerSpawner _spawner;
        private MinerState _state = MinerState.Spawning;

        private void Awake()
        {
            if (_agent == null) _agent = GetComponent<NavMeshAgent>();
        }

        /// <summary>Spawner가 풀 Get 직후 호출. mineZone/processor/spawner 주입 + 즉시 MovingToOre 진입.</summary>
        public bool Initialize(MineZone mineZone, ProcessorMachine processor, MinerSpawner spawner)
        {
            if (mineZone == null || processor == null || spawner == null)
            {
                Debug.LogError("[MinerNPC] Initialize received null mineZone/processor/spawner.", this);
                return false;
            }
            if (_agent == null || !_agent.isOnNavMesh)
            {
                Debug.LogError("[MinerNPC] NavMeshAgent missing or not on NavMesh.", this);
                return false;
            }

            _mineZone = mineZone;
            _processor = processor;
            _spawner = spawner;

            var settings = GameManager.Instance != null ? GameManager.Instance.Settings : null;
            if (settings != null) _agent.speed = settings.NPC.WorkerMoveSpeed;
            _agent.stoppingDistance = 0f;
            _agent.isStopped = false;

            _state = MinerState.MovingToOre;
            _agent.SetDestination(_mineZone.transform.position);
            return true;
        }

        private void Update()
        {
            // Animator 갱신. IsMining 우선순위 (Mining > Walk > Idle).
            if (_animator != null && _agent != null)
            {
                _animator.SetBool("IsMoving", _agent.velocity.sqrMagnitude > 0.01f);
                _animator.SetBool("IsMining", _state == MinerState.MiningOre);
            }

            switch (_state)
            {
                case MinerState.Spawning:
                    // Initialize 호출 직전 transient — Initialize 즉시 MovingToOre로 전환.
                    break;

                case MinerState.MovingToOre:
                    if (_mineZone == null || _agent == null) break;
                    if (_agent.pathPending) break;
                    if (_agent.remainingDistance > ARRIVAL_DISTANCE) break;
                    _state = MinerState.MiningOre;
                    _agent.isStopped = true;
                    if (_agent.hasPath) _agent.ResetPath();
                    break;

                case MinerState.MiningOre:
                    // 명시적 Tick 호출 — NavMeshAgent + isTrigger 호환 위험 회피 (5d 핫픽스 정합).
                    // 단 reach 안 active OrePlacement 무 시 새 active 위치로 재이동 (MovingToOre 전환).
                    if (_mineZone == null) break;
                    if (_mineZone.HasActiveInReach(transform.position))
                    {
                        _mineZone.Tick(this, transform.position, Time.deltaTime);
                    }
                    else if (_mineZone.TryFindNearestActivePlacement(transform.position, out var nextPos))
                    {
                        _state = MinerState.MovingToOre;
                        if (_agent != null && _agent.isOnNavMesh)
                        {
                            _agent.isStopped = false;
                            _agent.SetDestination(nextPos);
                        }
                    }
                    // else: 모든 OrePlacement cooldown 중 — 그 자리에서 대기. 다음 frame 재시도.
                    break;
            }
        }

        /// <summary>MineZone.SpawnAndPushOre의 user is MinerNPC 분기에서 호출. 단일 ore를 _processor.PushOreFromMiner로 전달 — _inputStack 슬롯으로 자연 포물선 트윈.
        /// Miner는 ore를 carry 안 함. push 후 즉시 다음 trigger frame에 MineZone이 다시 누적 → 주기적 push 반복.</summary>
        public void PushOre(Stackable ore)
        {
            if (ore == null) return;
            if (_processor != null)
            {
                _processor.PushOreFromMiner(ore);
            }
            else
            {
                ore.ReturnToPool();
            }
        }

        private void OnDisable()
        {
            // accumulator stale entry 정리 (명시적 Tick 패턴이라 trigger Exit 이벤트 무).
            if (_mineZone != null) _mineZone.ReleaseUser(this);

            _state = MinerState.Spawning;
            _mineZone = null;
            _processor = null;
            _spawner = null;
            if (_agent != null && _agent.isOnNavMesh) _agent.ResetPath();
        }
    }
}
