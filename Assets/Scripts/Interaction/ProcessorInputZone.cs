using UnityEngine;

namespace PrisonLife
{
    /// <summary>
    /// Processor 입력 trigger Zone. 3일차 게이트 3 — 옛 ProcessorZone.cs 분할 결과.
    /// Player 진입 1회 트리거로 부모 ProcessorMachine.PushOresFromPlayer 호출. 영역 머무는 동안 추가 동작 없음.
    /// 누적 패턴 미사용 (abstract 3개 stub) — InteractionZone 베이스의 trigger 분기 + Player 후크만 활용.
    /// </summary>
    /// <remarks>
    /// 부모-자식 참조: [SerializeField] private ProcessorMachine _parent (Inspector 명시 주입). GetComponentInParent 미사용 — prefab 분리 안전.
    /// 일괄 트윈 책임: 부모 ProcessorMachine.PushOresFromPlayer가 player.Stack.RemoveRange + _inputStack.AddBatch 수행.
    /// 본 클래스는 trigger 진입 위임만 담당 — Single Responsibility 회복.
    /// </remarks>
    public class ProcessorInputZone : InteractionZone
    {
        [Tooltip("부모 ProcessorMachine 참조. Inspector 필수 명시 주입 — GetComponentInParent 사용 안 함 (prefab 분리 안전).")]
        [SerializeField] private ProcessorMachine _parent;

        private void Awake()
        {
            if (_parent == null)
            {
                Debug.LogError("[ProcessorInputZone] _parent (ProcessorMachine) not assigned. Zone will no-op.", this);
            }
        }

        protected override void OnPlayerEnter(PlayerController player)
        {
            // 영역 진입 1회만 — Stay 동안 추가 동작 없음 (사용자 요구: "1회만").
            if (_parent != null) _parent.PushOresFromPlayer(player);
        }

        // --- abstract 3개 stub (누적 사용 안 함, 진입 트리거 패턴) ---

        protected override void OnAccumulatorTick(IInteractionUser user, float accumulated, float deltaTime) { }
        protected override bool IsAccumulatorComplete(IInteractionUser user, float accumulated) => false;
        protected override void OnInteractionComplete(IInteractionUser user) { }
    }
}
