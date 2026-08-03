using UnityEngine;

namespace PrisonLife
{
    /// <summary>
    /// Processor 출력 trigger Zone. 3일차 게이트 3 — 옛 ProcessorZone.cs 분할 결과.
    /// Player 진입 1회 트리거로 부모 ProcessorMachine.PullHandcuffsToPlayer 호출. 영역 머무는 동안 추가 동작 없음.
    /// 누적 패턴 미사용 (abstract 3개 stub) — InteractionZone 베이스의 trigger 분기 + Player 후크만 활용.
    /// </summary>
    /// <remarks>
    /// 부모-자식 참조: [SerializeField] private ProcessorMachine _parent (Inspector 명시 주입).
    /// 일괄 트윈 책임: 부모 ProcessorMachine.PullHandcuffsToPlayer가 _outputStack.RemoveRange + player.Stack.AddBatch 수행.
    /// 차단 가드 (부모 측): head에 ore 1+ 있으면 거부 + LogWarning. Mine handcuff 가드와 대칭.
    /// </remarks>
    public class ProcessorOutputZone : InteractionZone
    {
        [Tooltip("부모 ProcessorMachine 참조. Inspector 필수 명시 주입 — GetComponentInParent 사용 안 함 (prefab 분리 안전).")]
        [SerializeField] private ProcessorMachine _parent;

        private void Awake()
        {
            if (_parent == null)
            {
                Debug.LogError("[ProcessorOutputZone] _parent (ProcessorMachine) not assigned. Zone will no-op.", this);
            }
        }

        protected override void OnPlayerEnter(PlayerController player)
        {
            // 영역 진입 1회만 — Stay 동안 추가 동작 없음.
            if (_parent != null) _parent.PullHandcuffsToPlayer(player);
        }

        // --- abstract 3개 stub (누적 사용 안 함, 진입 트리거 패턴) ---

        protected override void OnAccumulatorTick(IInteractionUser user, float accumulated, float deltaTime) { }
        protected override bool IsAccumulatorComplete(IInteractionUser user, float accumulated) => false;
        protected override void OnInteractionComplete(IInteractionUser user) { }
    }
}
