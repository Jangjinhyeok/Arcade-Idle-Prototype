namespace PrisonLife
{
    /// <summary>
    /// Stackable 마커가 식별하는 자원 타입 식별자. ADR-001 §2.2 R2 결정.
    /// </summary>
    /// <remarks>
    /// 1일차 기준 3종(Ore, Handcuff, Money)으로 시작.
    /// 향후 확장(예: 불도저 NICE 항목, 추가 가공 자원)은 enum 끝에 append-only로 추가한다.
    /// 기존 값의 정수 값을 변경하면 Inspector 직렬화 데이터(Stackable.Type 필드)가 깨지므로 절대 재정렬 금지.
    /// </remarks>
    public enum StackableType
    {
        Ore = 0,
        Handcuff = 1,
        Money = 2,
    }
}
