using System.Collections.Generic;
using UnityEngine;

namespace PrisonLife
{
    /// <summary>
    /// 모든 상호작용 구역의 추상 베이스. ADR-001 §2.1.
    /// trigger-stay 누적 패턴(R1: 사용자별 분리 타이머)을 5개 파생(Mine/Processor/Desk/Upgrade/Hire)이 공유한다.
    /// </summary>
    /// <remarks>
    /// **abstract 메서드 협약 (2일차 시그니처 변경, ADR-001 §2.1 정정 예정):**
    /// - <see cref="OnAccumulatorTick"/>: **시각 갱신만** 담당. 아이템 spawn/소비, 이벤트 발행, 재화 결제 등 도메인 작업 금지.
    /// - <see cref="IsAccumulatorComplete"/>: 임계 판정만. side-effect 금지(상태 변경·이벤트 발행 금지).
    /// - <see cref="OnInteractionComplete"/>: **모든 도메인 작업의 유일한 진입점**. 아이템 spawn·소비·결제는 여기서만.
    /// 협약 위반 시 동시 진입(Player+NPC) 또는 다중 NPC 케이스에서 중복 작업이 발생할 수 있다.
    /// </remarks>
    [RequireComponent(typeof(Collider))]
    public abstract class InteractionZone : MonoBehaviour
    {
        /// <summary>R1: 사용자별 누적 타이머. Player와 NPC가 동일 Zone에 동시 진입해도 합산 가속 없음.</summary>
        protected readonly Dictionary<IInteractionUser, float> _accumulators = new Dictionary<IInteractionUser, float>();

        // TODO(Day-4): Multi-NPC 도입 시 int _agentCount로 승격. 2일차 시나리오는 1 Player + 1 Prisoner라 bool로 충분.
        protected bool _isPlayerInside;
        protected bool _isAgentInside;

        /// <summary>플레이어 또는 NPC 1명 이상이 현재 Zone 내부에 체류 중인지 여부.</summary>
        public bool IsOccupied => _isPlayerInside || _isAgentInside;

        // --- Unity 메시지 (private 고정 — 파생은 후크만 오버라이드) ---

        private void OnTriggerEnter(Collider other)
        {
            var user = other.GetComponentInParent<IInteractionUser>();
            if (user == null) return;

            _accumulators[user] = 0f;

            if (user is PlayerController player)
            {
                _isPlayerInside = true;
                OnPlayerEnter(player);
            }
            else if (user is NPCBase agent)
            {
                _isAgentInside = true;
                OnAgentEnter(agent);
            }
        }

        private void OnTriggerStay(Collider other)
        {
            // known-todos: GetComponentInParent 매 프레임 호출. 4일차 코드 리뷰에서 Dictionary<Collider, IInteractionUser> 캐싱 검토.
            var user = other.GetComponentInParent<IInteractionUser>();
            if (user == null) return;
            if (!_accumulators.ContainsKey(user)) return; // Enter 누락 race 가드.

            float dt = Time.deltaTime;
            _accumulators[user] += dt;
            float accumulated = _accumulators[user];

            OnAccumulatorTick(user, accumulated, dt);

            if (IsAccumulatorComplete(user, accumulated))
            {
                OnInteractionComplete(user);
                ResetAccumulator(user);
            }

            if (user is PlayerController player) OnPlayerStay(player, dt);
            else if (user is NPCBase agent) OnAgentStay(agent, dt);
        }

        private void OnTriggerExit(Collider other)
        {
            var user = other.GetComponentInParent<IInteractionUser>();
            if (user == null) return;

            _accumulators.Remove(user);

            if (user is PlayerController player)
            {
                _isPlayerInside = false;
                OnPlayerExit(player);
            }
            else if (user is NPCBase agent)
            {
                _isAgentInside = false;
                OnAgentExit(agent);
            }
        }

        // --- 플레이어 후크 (파생에서 필요 시 오버라이드) ---
        protected virtual void OnPlayerEnter(PlayerController player) { }
        protected virtual void OnPlayerStay(PlayerController player, float deltaTime) { }
        protected virtual void OnPlayerExit(PlayerController player) { }

        // --- NPC 후크 (파생에서 필요 시 오버라이드) ---
        protected virtual void OnAgentEnter(NPCBase agent) { }
        protected virtual void OnAgentStay(NPCBase agent, float deltaTime) { }
        protected virtual void OnAgentExit(NPCBase agent) { }

        // --- 누적기 (파생 필수 구현) ---

        /// <summary>매 OnTriggerStay tick에서 호출. **시각 갱신만** — 도메인 작업(spawn/소비/이벤트/결제) 금지.</summary>
        protected abstract void OnAccumulatorTick(IInteractionUser user, float accumulated, float deltaTime);

        /// <summary>해당 user의 누적값이 완료 임계점에 도달했는지 판정. side-effect 금지.</summary>
        protected abstract bool IsAccumulatorComplete(IInteractionUser user, float accumulated);

        /// <summary>완료 시 1회 호출. **모든 도메인 작업의 유일한 진입점**. user별 적재 대상 분기 가능.</summary>
        protected abstract void OnInteractionComplete(IInteractionUser user);

        /// <summary>
        /// 특정 사용자의 누적 타이머를 0으로 리셋한다.
        /// OnAccumulatorTick(user, 0f, 0f)을 호출해 게이지 UI 즉시 0 표시.
        /// 파생 OnAccumulatorTick은 deltaTime==0이어도 시각 갱신만 수행하므로 부수 효과 없음.
        /// </summary>
        protected void ResetAccumulator(IInteractionUser user)
        {
            if (!_accumulators.ContainsKey(user)) return;
            _accumulators[user] = 0f;
            OnAccumulatorTick(user, 0f, 0f);
        }
    }
}
