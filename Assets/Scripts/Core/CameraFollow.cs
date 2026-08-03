using UnityEngine;

namespace PrisonLife
{
    /// <summary>
    /// 핸드 롤 follow 카메라. ADR-001 §1 (Cinemachine 미사용). GDD §2 / §6.5.
    /// </summary>
    /// <remarks>
    /// LateUpdate에서 _target.position + _offset로 위치만 갱신. 회전은 씬 셋업 시 Inspector에 박은 값 유지(핸드오프 §11i, ADR-001 "회전 고정").
    /// FOV·회전은 본 컴포넌트 책임 외 — Main Camera Inspector에서 직접 설정.
    /// SmoothDamp는 3일차 폴리시 항목 — 2일차엔 즉응 추적으로 디버깅 단순화.
    /// </remarks>
    [DisallowMultipleComponent]
    public class CameraFollow : MonoBehaviour
    {
        [Tooltip("추적 대상 Transform. Player GameObject 드래그 주입(자동 탐색 금지).")]
        [SerializeField] private Transform _target;

        [Tooltip("타겟 기준 오프셋 (월드 좌표). §6.5 수치: (-10, 18, -12).")]
        [SerializeField] private Vector3 _offset = new Vector3(-10f, 18f, -12f);

        private void LateUpdate()
        {
            if (_target == null) return;
            transform.position = _target.position + _offset;
        }

        /// <summary>현재 follow 추적 위치 (target.position + offset). CameraDirector A-2 경로 RETURN 보간 도착점 산출용.
        /// _target null 시 카메라 현재 위치 fallback (cinematic 중 NRE 회피).</summary>
        public Vector3 GetFollowPosition() => _target != null ? _target.position + _offset : transform.position;
    }
}
