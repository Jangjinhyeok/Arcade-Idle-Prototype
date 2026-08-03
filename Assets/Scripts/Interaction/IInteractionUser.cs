namespace PrisonLife
{
    /// <summary>
    /// InteractionZone 누적 타이머의 키 식별자. ADR-001 §2.1 R1 결정.
    /// </summary>
    /// <remarks>
    /// PlayerController와 NPCBase가 구현. 본 인터페이스는 다중 상속 회피용 마커이며,
    /// InteractionZone의 Dictionary&lt;IInteractionUser, float&gt; _accumulators 키 역할만 담당한다.
    /// 메서드를 추가하지 않는다 — 추가가 필요해지면 별도 인터페이스(IStackHolder 등)로 분리한다.
    /// </remarks>
    public interface IInteractionUser
    {
    }
}
