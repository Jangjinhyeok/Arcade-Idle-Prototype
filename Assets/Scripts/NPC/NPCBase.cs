using UnityEngine;

namespace PrisonLife
{
    /// <summary>
    /// NPC 베이스 클래스. Prisoner/Worker NPC의 공통 로직(NavMeshAgent 래핑 + 상태 머신)을 담는다.
    /// 1일차 시점은 InteractionZone 후크 시그니처 컴파일을 위한 abstract stub.
    /// </summary>
    /// <remarks>
    /// 2일차: NavMeshAgent 목적지 시퀀싱 + 상태 머신 (Prisoner: Desk→Pickup→Drop→Cell, Worker: Mine↔Processor 왕복).
    /// IInteractionUser 구현은 InteractionZone Dictionary 키 역할 (R1).
    /// </remarks>
    public abstract class NPCBase : MonoBehaviour, IInteractionUser
    {
        // TODO(Day-3): NavMeshAgent 컴포넌트 참조 + SetDestination 헬퍼 + 상태 머신.
    }
}
