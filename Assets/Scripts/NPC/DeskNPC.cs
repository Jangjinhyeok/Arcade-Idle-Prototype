using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace PrisonLife
{
    /// <summary>
    /// Desk Cashier NPC. 4일차 Desk + DeskNPC 시스템 — DeskNPCSpawnUpgradeZone 결제 후 1명 영구 spawn.
    /// FSM 5상태: Spawning → MovingToOutput → AtOutput → MovingToDesk → AtDesk → MovingToOutput (loop).
    /// </summary>
    /// <remarks>
    /// 동작:
    ///   ProcessorOutput 도달 → handcuff 일괄 픽업 (max = Settings.Desk.DeskNPCStackMax, 비어있으면 0.5s polling 재시도)
    ///   → DeskAnchor 도달 → AtDesk에서 _depositInterval (0.2s) 주기로 1개씩 desk._deskStack에 Add (트윈)
    ///   → carry stack empty → MovingToOutput loop.
    /// IInteractionUser 마커는 NPCBase 상속으로 자동 박힘 — DeskZone OnAgentEnter/Exit에서 agent is DeskNPC 분기 박음.
    /// Trigger 의존 무 (NavMeshAgent + isTrigger 호환 위험 회피, MinerNPC 패턴 정합) — Processor.TryRequestHandcuffsForDeskNPC + Desk.DepositHandcuffFromDeskNPC 명시적 호출.
    /// 영구 보존 — 1회 Spawner.Get 후 풀 회수 안 박음. OnDisable cleanup은 SetActive(false) 또는 OnDestroy 진입 시 안전망.
    /// </remarks>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NavMeshAgent))]
    public class DeskNPC : NPCBase
    {
        private enum DeskNPCState { Spawning, MovingToOutput, AtOutput, MovingToDesk, AtDesk }

        private const float ARRIVAL_DISTANCE = 0.5f;
        private const float OUTPUT_POLLING_INTERVAL = 0.5f;

        [Tooltip("NavMeshAgent. Inspector 주입 우선, 비어있으면 Awake에서 GetComponent fallback.")]
        [SerializeField] private NavMeshAgent _agent;

        [Tooltip("Animator. Floreswa 캐릭터 Animator ref. CharacterAnimator Controller + IsMoving 파라미터.")]
        [SerializeField] private Animator _animator;

        [Tooltip("DeskNPC head carry StackContainer. ProcessorOutput에서 픽업한 handcuff를 Desk까지 carry. capacity = Settings.Desk.DeskNPCStackMax (8).")]
        [SerializeField] private StackContainer _stack;

        private ProcessorMachine _processor;
        private DeskZone _desk;
        private DeskNPCSpawner _spawner;
        private Transform _outputAnchor;
        private Transform _deskAnchor;
        private DeskNPCState _state = DeskNPCState.Spawning;
        private float _depositTimer;
        private float _pollingTimer;

        private void Awake()
        {
            if (_agent == null) _agent = GetComponent<NavMeshAgent>();
        }

        /// <summary>Spawner가 풀 Get + 위치 배치 직후 호출. 참조 주입 + AcceptedType 코드 주입 + capacity Settings 주입 + 즉시 MovingToOutput 진입.</summary>
        public bool Initialize(ProcessorMachine processor, DeskZone desk, DeskNPCSpawner spawner,
                                Transform outputAnchor, Transform deskAnchor)
        {
            if (processor == null || desk == null || spawner == null || outputAnchor == null || deskAnchor == null)
            {
                Debug.LogError("[DeskNPC] Initialize received null processor/desk/spawner/outputAnchor/deskAnchor.", this);
                return false;
            }
            if (_agent == null || !_agent.isOnNavMesh)
            {
                Debug.LogError("[DeskNPC] NavMeshAgent missing or not on NavMesh.", this);
                return false;
            }
            if (_stack == null)
            {
                Debug.LogError("[DeskNPC] _stack not assigned. Inspector에서 head StackContainer 드래그 필수.", this);
                return false;
            }

            _processor = processor;
            _desk = desk;
            _spawner = spawner;
            _outputAnchor = outputAnchor;
            _deskAnchor = deskAnchor;

            // AcceptedType + Capacity 코드 주입 (R3 정합).
            _stack.AcceptedType = StackableType.Handcuff;
            var settings = GameManager.Instance != null ? GameManager.Instance.Settings : null;
            if (settings != null)
            {
                _stack.SetCapacity(settings.Desk.DeskNPCStackMax);
                _agent.speed = settings.NPC.WorkerMoveSpeed;
            }
            _agent.stoppingDistance = 0f;
            _agent.isStopped = false;

            // 4일차 결함 보정 — spawn 직후 desk._deskStack 박힌 상태 가능 (Player가 결제 박은 후). Output 출장보다 Desk 대기 우선 박음.
            // AtDesk 도달 후 carry/desk 상태 검사 박아 MovingToOutput 분기 또는 cashier 대기 결정.
            _state = DeskNPCState.MovingToDesk;
            _agent.SetDestination(_deskAnchor.position);
            _depositTimer = 0f;
            _pollingTimer = 0f;
            return true;
        }

        private void Update()
        {
            if (_animator != null && _agent != null)
            {
                _animator.SetBool("IsMoving", _agent.velocity.sqrMagnitude > 0.01f);
            }

            switch (_state)
            {
                case DeskNPCState.Spawning:
                    // Initialize 즉시 MovingToOutput 전환 — 본 분기 stub.
                    break;

                case DeskNPCState.MovingToOutput:
                    if (_agent == null) break;
                    if (_agent.pathPending) break;
                    if (_agent.remainingDistance > ARRIVAL_DISTANCE) break;
                    _state = DeskNPCState.AtOutput;
                    _pollingTimer = 0f;
                    break;

                case DeskNPCState.AtOutput:
                    if (_processor == null || _stack == null) break;
                    _pollingTimer += Time.deltaTime;
                    if (_pollingTimer < OUTPUT_POLLING_INTERVAL) break;
                    _pollingTimer = 0f;
                    var settings = GameManager.Instance != null ? GameManager.Instance.Settings : null;
                    int max = settings != null ? settings.Desk.DeskNPCStackMax : 8;
                    if (_processor.TryRequestHandcuffsForDeskNPC(max, out var handcuffs))
                    {
                        int added = _stack.AddBatch(handcuffs);
                        // 거부분 (capacity 초과 또는 type 미일치) — 호출자 책임으로 풀 회수.
                        if (handcuffs != null)
                        {
                            for (int i = added; i < handcuffs.Count; i++)
                            {
                                if (handcuffs[i] != null) handcuffs[i].ReturnToPool();
                            }
                        }
                        _state = DeskNPCState.MovingToDesk;
                        if (_agent != null && _agent.isOnNavMesh) _agent.SetDestination(_deskAnchor.position);
                    }
                    // 실패 — _outputStack 비어있음. 0.5s 후 재시도 (AtOutput 유지).
                    break;

                case DeskNPCState.MovingToDesk:
                    if (_agent == null) break;
                    if (_agent.pathPending) break;
                    if (_agent.remainingDistance > ARRIVAL_DISTANCE) break;
                    _state = DeskNPCState.AtDesk;
                    _depositTimer = 0f;
                    // NavMeshAgent + isTrigger 호환 위험 회피 — trigger 진입 의존 안 박고 명시적 점유 toggle.
                    if (_desk != null) _desk.SetDeskNPCInside(true);
                    break;

                case DeskNPCState.AtDesk:
                    if (_stack == null || _desk == null) break;
                    if (_stack.IsEmpty)
                    {
                        // 4일차 결함 보정 — NeedsHandcuffRefill 시 Output 출장. 룰:
                        //   ① desk 빈 → 출장
                        //   ② front prisoner RequestQuantity > desk count → 출장 (deadlock 회피)
                        //   ③ queue 빈 + desk 1+ → cashier 대기
                        if (_desk.NeedsHandcuffRefill)
                        {
                            _state = DeskNPCState.MovingToOutput;
                            // 점유자 toggle off — Output 출장 동안 PrisonerNPC 거래 차단.
                            _desk.SetDeskNPCInside(false);
                            if (_agent != null && _agent.isOnNavMesh) _agent.SetDestination(_outputAnchor.position);
                        }
                        // else: AtDesk 유지 — cashier 역할 (점유자 토글 → PrisonerNPC 거래 활성).
                        break;
                    }
                    // carry 1+ — desk full 시 deposit 차단 (carry hold). PrisonerNPC 거래 진행 시 desk count 감소 → 다음 frame 진열 재개.
                    if (_desk.IsDeskStackFull) break;
                    _depositTimer += Time.deltaTime;
                    var depositSettings = GameManager.Instance != null ? GameManager.Instance.Settings : null;
                    float depositInterval = depositSettings != null ? depositSettings.Desk.DeskNPCDepositInterval : 0.2f;
                    if (_depositTimer < depositInterval) break;
                    _depositTimer = 0f;
                    var hc = _stack.Remove();
                    if (hc == null) break;
                    if (!_desk.DepositHandcuffFromDeskNPC(hc))
                    {
                        // desk full race (IsDeskStackFull 가드 이후 다른 user가 채운 케이스) — handcuff 풀 회수.
                        hc.ReturnToPool();
                    }
                    break;
            }
        }

        private void OnDisable()
        {
            // 점유자 toggle off — DeskNPC 비활성 시 PrisonerNPC 거래 차단 (cashier 역할 종료).
            if (_desk != null) _desk.SetDeskNPCInside(false);

            // carry stack 잔여 handcuff 풀 회수 (강제 비활성 케이스).
            if (_stack != null)
            {
                while (!_stack.IsEmpty)
                {
                    var hc = _stack.Remove();
                    if (hc != null) hc.ReturnToPool();
                }
            }

            _state = DeskNPCState.Spawning;
            _depositTimer = 0f;
            _pollingTimer = 0f;
            _processor = null;
            _desk = null;
            _spawner = null;
            _outputAnchor = null;
            _deskAnchor = null;
            if (_agent != null && _agent.isOnNavMesh) _agent.ResetPath();
        }
    }
}
