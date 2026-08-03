using System.Collections.Generic;
using UnityEngine;

namespace PrisonLife
{
    /// <summary>
    /// 수갑 진열 InteractionZone. ADR-001 §2.1 세 번째 파생 (3일차 게이트 4 갱신).
    /// Player 진입 1회 트리거로 head의 handcuff 전량 → _deskStack 일괄 transfer (게이트 3 ProcessorInputZone 패턴 복제).
    /// Prisoner는 TryAcquireHandcuffs(int count, out List)로 RequestQuantity만큼 일괄 가져감 — 부족 시 false 반환 (Prisoner WaitingAtDesk 무한 재시도).
    /// transfer마다 OnHandcuffStocked 발행 (transfer된 개수만큼 N번) — PrisonerSpawner가 N명 spawn 시도.
    /// </summary>
    /// <remarks>
    /// 책임 변경 (2일차 → 3일차): 누적 패턴(OnInteractionComplete) → 진입 1회 트리거(OnPlayerEnter). abstract 3개 stub 처리.
    /// 차단 가드: head에 ore 1+ 있으면 transfer 거부 + LogWarning (게이트 3 ProcessorMachine 패턴과 대칭).
    /// Capacity 단일 source: Awake에서 settings.Desk.HandcuffCapacity 코드 주입.
    /// 옛 TryTakeHandcuff(out Stackable) 메서드 폐기 — 새 TryAcquireHandcuffs(int count, out List<Stackable>)로 교체.
    /// </remarks>
    public class DeskZone : InteractionZone
    {
        [Tooltip("Desk 상단 진열 StackContainer — handcuff가 1D 선형으로 진열되는 자식 GameObject \"DeskStack\". Awake에서 AcceptedType=Handcuff + Settings.Desk.HandcuffCapacity 코드 주입.")]
        [SerializeField] private StackContainer _deskStack;

        [Tooltip("Prisoner Queue 슬롯 Transform 배열. slot 0 = DeskPoint(거래 위치), slot 1~2 = 대기 위치 (PrisonerSpawn 방향 일렬). 사용자 editor 직접 배치, 길이 = Settings.NPC.MaxConcurrent. 3일차 게이트 5a 결정 #23.")]
        [SerializeField] private Transform[] _queueSlots;

        private readonly List<PrisonerNPC> _queue = new List<PrisonerNPC>();

        // DeskNPC 점유 분리 플래그 — InteractionZone._isAgentInside는 PrisonerNPC도 토글하므로 거래 활성 조건 분간 박야.
        // OnAgentEnter / OnAgentExit 오버라이드에서 agent is DeskNPC 박힘 시만 토글.
        private bool _isDeskNPCInside;

        /// <summary>거래 활성 조건 — Player 또는 DeskNPC 점유 시 true. PrisonerNPC.MovingToDesk 도달 + WaitingAtDesk 재시도 가드. 4일차 Desk + DeskNPC 시스템.</summary>
        public bool IsOccupiedByPlayerOrDeskNPC => _isPlayerInside || _isDeskNPCInside;

        /// <summary>_deskStack 빈 여부. DeskNPC.AtDesk 분기 — carry 빈 + desk 빈 시만 MovingToOutput 출장 가드.</summary>
        public bool IsDeskStackEmpty => _deskStack == null || _deskStack.IsEmpty;

        /// <summary>_deskStack 가득 여부. DeskNPC.AtDesk 분기 — carry 1+ + desk full 시 진열 차단 (carry hold) 가드.</summary>
        public bool IsDeskStackFull => _deskStack != null && _deskStack.IsFull;

        /// <summary>DeskNPC가 Output 출장 박야 하는 조건. 4일차 결함 보정 — front prisoner RequestQuantity > desk count 시 deadlock 회피용 출장 트리거.
        /// 룰: ① desk 빈 → true (둘 다 비어야 출장이 기존 룰, 단 본 helper 단일화) / ② queue 빈 → false (cashier 대기) / ③ desk count &lt; front RequestQuantity → true (deadlock 회피).</summary>
        public bool NeedsHandcuffRefill
        {
            get
            {
                if (_deskStack == null) return false;
                if (_deskStack.IsEmpty) return true;
                if (_queue == null || _queue.Count == 0) return false;
                var front = _queue[0];
                if (front == null) return false;
                return _deskStack.Count < front.RequestQuantity;
            }
        }

        private void Awake()
        {
            if (_deskStack == null)
            {
                Debug.LogError("[DeskZone] _deskStack not assigned — Desk will no-op.", this);
                return;
            }
            // AcceptedType 코드 주입 (R2 패턴).
            _deskStack.AcceptedType = StackableType.Handcuff;

            // Capacity 코드 주입 — Settings 단일 source (R3 정합). null 시 Inspector 값 fallback.
            var settings = GameManager.Instance != null ? GameManager.Instance.Settings : null;
            if (settings != null) _deskStack.SetCapacity(settings.Desk.HandcuffCapacity);

            // 게이트 5a — Queue 슬롯 가드 + Settings.NPC.MaxConcurrent 정합 검증.
            if (_queueSlots == null || _queueSlots.Length == 0)
            {
                Debug.LogError("[DeskZone] _queueSlots not assigned. Queue 시스템 no-op. Inspector에서 Transform 3개 배열 드래그 필수 (slot 0 = DeskPoint, 1~2 = 대기 위치).", this);
            }
            else if (settings != null && _queueSlots.Length != settings.NPC.MaxConcurrent)
            {
                Debug.LogWarning($"[DeskZone] _queueSlots.Length ({_queueSlots.Length}) != Settings.NPC.MaxConcurrent ({settings.NPC.MaxConcurrent}). Queue 가드와 spawn 가드 불일치 — Inspector 정합 확인.", this);
            }
        }

        protected override void OnPlayerEnter(PlayerController player)
        {
            // 영역 진입 1회만 — Stay 동안 추가 동작 없음 (게이트 3 ProcessorInputZone 패턴).
            if (_deskStack == null || _deskStack.IsFull) return;
            if (player == null || player.Stack == null || player.Stack.IsEmpty) return;

            // 결정 4 패턴 — head에 ore 1+ 있으면 거부 (혼재 회피, ProcessorMachine.PullHandcuffs와 대칭).
            if (player.Stack.ContainsType(StackableType.Ore))
            {
                Debug.LogWarning("[DeskZone] Player head contains Ore — desk transfer aborted. Empty ores into Processor first.", this);
                return;
            }

            int available = _deskStack.Capacity - _deskStack.Count;
            if (available <= 0) return;

            int popCount = Mathf.Min(player.Stack.Count, available);
            var handcuffs = player.Stack.RemoveRange(popCount);
            int added = _deskStack.AddBatch(handcuffs);

            // 거부분(반환값 이후 인덱스) — capacity 또는 type race로 떨어진 인스턴스 풀 회수.
            for (int i = added; i < handcuffs.Count; i++)
            {
                if (handcuffs[i] != null) handcuffs[i].ReturnToPool();
            }
            // 게이트 5a — OnHandcuffStocked event 발행 폐기 (결정 #22). PrisonerSpawner가 시간 폴링으로 spawn 결정.
        }

        // --- Queue 시스템 (게이트 5a 결정 #23) ---

        /// <summary>Queue에 빈 슬롯이 있는지. PrisonerSpawner가 spawn 직전 가드.</summary>
        public bool HasQueueSlot()
        {
            if (_queueSlots == null) return false;
            return _queue.Count < _queueSlots.Length;
        }

        /// <summary>Queue에 prisoner 추가. 성공 시 슬롯 인덱스 반환. full이면 false. PrisonerSpawner.Update가 pool.Get 직후 prisoner.Initialize 직전에 호출.</summary>
        public bool TryEnqueue(PrisonerNPC prisoner, out int slotIndex)
        {
            if (prisoner == null || !HasQueueSlot())
            {
                slotIndex = -1;
                return false;
            }
            _queue.Add(prisoner);
            slotIndex = _queue.Count - 1;
            return true;
        }

        /// <summary>거래 완료 atomic 진입점. PrisonerNPC.TryAcquireHandcuff 성공 직후 호출.
        /// 순서 ① RemoveAt(0) ② 나머지 PromoteSlot(i) ③ prisoner.EnterMovingToJail — 1프레임 내 처리, race 회피.
        /// (5a EnterLeaving → 5b EnterMovingToJail로 supersede 박힘.)</summary>
        public void OnPrisonerDealComplete(PrisonerNPC prisoner)
        {
            if (prisoner == null) return;
            if (_queue.Count == 0 || _queue[0] != prisoner)
            {
                Debug.LogError("[DeskZone] OnPrisonerDealComplete called by non-front prisoner. race 또는 호출자 버그 — dangling 잠재.", this);
                return;
            }
            _queue.RemoveAt(0);
            for (int i = 0; i < _queue.Count; i++)
            {
                if (_queue[i] != null) _queue[i].PromoteSlot(i);
            }
            prisoner.EnterMovingToJail();
        }

        /// <summary>Queue dangling cleanup. PrisonerNPC.OnDisable에서 호출 (Initialize 실패 또는 비정상 Release 회피).
        /// front(_queue[0])든 아니든 단순 제거 + idx 이후 prisoner들 PromoteSlot. EnterLeaving은 호출 안 함 — 본 메서드 호출자(OnDisable 진입자)는 이미 비활성화 흐름.
        /// 정상 흐름이면 OnPrisonerDealComplete가 먼저 RemoveAt(0) → OnDisable 시점엔 idx &lt; 0 early return.
        /// front가 본 메서드 진입하는 건 Initialize 실패 또는 거래 미완료 강제 Release만.</summary>
        public void RemoveFromQueue(PrisonerNPC prisoner)
        {
            if (prisoner == null) return;
            int idx = _queue.IndexOf(prisoner);
            if (idx < 0) return;
            _queue.RemoveAt(idx);
            for (int i = idx; i < _queue.Count; i++)
            {
                if (_queue[i] != null) _queue[i].PromoteSlot(i);
            }
        }

        /// <summary>슬롯 인덱스의 Transform 반환. PrisonerNPC가 Initialize·PromoteSlot에서 destination 조회. 인덱스 범위 외면 null.</summary>
        public Transform GetQueueSlot(int index)
        {
            if (_queueSlots == null) return null;
            if (index < 0 || index >= _queueSlots.Length) return null;
            return _queueSlots[index];
        }

        // --- NPC 후크 — DeskNPC만 점유 박음 (PrisonerNPC 자체 진입은 _isAgentInside 베이스 토글, 본 분기 무관) ---
        // NavMeshAgent + isTrigger 호환 위험으로 OnAgentEnter/Exit는 미발동 가능 — DeskNPC.AtDesk 진입/이탈 시점에 SetDeskNPCInside 명시적 호출 박음 (Mine zone Tick 패턴 정합).

        protected override void OnAgentEnter(NPCBase agent)
        {
            if (agent is DeskNPC) _isDeskNPCInside = true;
        }

        protected override void OnAgentExit(NPCBase agent)
        {
            if (agent is DeskNPC) _isDeskNPCInside = false;
        }

        /// <summary>DeskNPC가 AtDesk 진입(true) / AtDesk 이탈(false) 시점에 명시적 호출. NavMeshAgent + isTrigger 호환 위험 회피 — trigger 진입 의존 안 박음.</summary>
        public void SetDeskNPCInside(bool inside)
        {
            _isDeskNPCInside = inside;
        }

        // --- abstract 3개 stub (누적 미사용, 진입 트리거 패턴) ---

        protected override void OnAccumulatorTick(IInteractionUser user, float accumulated, float deltaTime) { }
        protected override bool IsAccumulatorComplete(IInteractionUser user, float accumulated) => false;
        protected override void OnInteractionComplete(IInteractionUser user) { }

        /// <summary>DeskNPC.AtDesk 분기에서 호출. 단일 handcuff를 _deskStack에 직접 Add (StackContainer.Add 내부 트윈 0.25s 박음).
        /// _deskStack.IsFull 시 false — 호출자(DeskNPC)가 ReturnToPool 책임. 4일차 Desk + DeskNPC 시스템.</summary>
        public bool DepositHandcuffFromDeskNPC(Stackable hc)
        {
            if (hc == null || _deskStack == null) return false;
            if (_deskStack.IsFull) return false;
            int slot = _deskStack.Add(hc);
            return slot >= 0;
        }

        // --- 외부 노출 (Prisoner 호출 진입점) ---

        /// <summary>
        /// Prisoner.TryAcquireHandcuff에서 호출. count만큼 정확히 Pop 가능하면 true + handcuffs list 반환, 부족하면 false (handcuffs = null).
        /// **회수 책임은 호출자(Prisoner)** — 받은 handcuff 전부에 대해 ReturnToPool() 호출 필수.
        /// 결정 1 — 부족 시 false 반환만, partial 거래 안 함 (Prisoner WaitingAtDesk 무한 재시도 패턴 유지).
        /// </summary>
        public bool TryAcquireHandcuffs(int count, out List<Stackable> handcuffs)
        {
            if (_deskStack == null || count <= 0)
            {
                handcuffs = null;
                return false;
            }
            if (_deskStack.Count < count)
            {
                handcuffs = null;
                return false;
            }
            handcuffs = _deskStack.RemoveRange(count);
            return true;
        }
    }
}
