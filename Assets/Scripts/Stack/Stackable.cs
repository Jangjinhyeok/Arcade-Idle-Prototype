using UnityEngine;
using UnityEngine.Pool;

namespace PrisonLife
{
    /// <summary>
    /// 스택 컨테이너에 적재 가능한 GameObject 식별 마커. ADR-001 §2.2.
    /// </summary>
    /// <remarks>
    /// 본 컴포넌트는 타입 식별만 담당하고 시각 표현(MeshRenderer/MeshFilter)은 보유하지 않는다.
    /// StackContainer는 Stackable 참조만으로 적재 가부와 타입 검증을 수행한다.
    /// 풀링 대상(OrePiece, Handcuff, MoneyPickup) Prefab의 루트 GameObject에 부착한다.
    ///
    /// **2일차 확장**: ObjectPool 사이클 지원을 위해 OriginPool/ReturnToPool 추가.
    /// ADR-001 R2 마커 정의에서 약간 확장 — 정식 정정은 2일차 종결 시 ADR-001에 메모(시그니처 변경 메모와 함께).
    /// </remarks>
    [DisallowMultipleComponent]
    public class Stackable : MonoBehaviour
    {
        [Tooltip("이 Stackable이 표현하는 자원 타입. Inspector에서 Prefab별로 1회 지정한다.")]
        [SerializeField] private StackableType _type;

        /// <summary>적재 가능 자원 타입. Prefab 인스턴스화 후 변경 금지(런타임 변경 시 StackContainer 검증 깨짐).</summary>
        public StackableType Type => _type;

        /// <summary>
        /// 본 인스턴스를 발급한 ObjectPool. Mine/Processor 등 spawn 측이 actionOnGet에서 주입한다.
        /// null이면 ReturnToPool은 Destroy 폴백 — pool 미보유 케이스(테스트·디버그) 안전.
        /// setter public 사유: Get 직후 외부에서 주입해야 하므로 init-only 안 됨.
        /// </summary>
        public IObjectPool<Stackable> OriginPool { get; set; }

        /// <summary>
        /// 풀로 회수. OriginPool 미주입 시 GameObject 파괴 폴백.
        /// 호출자: Processor 변환 시 광석 소비, Prisoner 구매 시 Desk 수갑 소비, Money 흡수 완료 시 등.
        /// 4일차 — Play stop race (Pool 박은 owner가 먼저 Destroy 박은 case actionOnRelease 박은 게 destroyed transform accessor MissingReferenceException) try-catch swallow.
        /// </summary>
        public void ReturnToPool()
        {
            if (OriginPool == null) { Destroy(gameObject); return; }
            try { OriginPool.Release(this); }
            catch (MissingReferenceException) { /* Play stop race — Pool owner Destroyed. swallow. */ }
            catch (System.NullReferenceException) { /* same */ }
        }
    }
}
