using UnityEngine;
using UnityEngine.EventSystems;

namespace PrisonLife
{
    /// <summary>
    /// Floating Virtual Joystick. ADR-002 §2 Scripts/Player. GDD §2.
    /// 화면 어느 위치든 드래그 시작 지점으로 사용 가능. PlayerController가 Direction/Magnitude를 매 프레임 폴링한다.
    /// </summary>
    /// <remarks>
    /// 본 컴포넌트는 Canvas 자식 RectTransform(화면 전체 stretch + Image Raycast Target=true, alpha=0)에 부착한다.
    /// Background / Handle 두 자식은 PointerDown 시점에 터치 위치로 이동·노출되고 PointerUp에서 비활성화된다.
    /// Legacy Input(UnityEngine.EventSystems) 기반 — New Input System 의존 없음(constraints §6).
    /// </remarks>
    [RequireComponent(typeof(RectTransform))]
    public class JoystickInput : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        [Tooltip("Background 자식 RectTransform. PointerDown 시 터치 위치로 이동, Pivot=(0.5,0.5).")]
        [SerializeField] private RectTransform _background;

        [Tooltip("Handle 자식 RectTransform. Background 중심에서 오프셋 이동, Pivot=(0.5,0.5).")]
        [SerializeField] private RectTransform _handle;

        [Tooltip("Handle이 Background 중심에서 이동 가능한 최대 반경 (px). 보통 Background width/2 값을 입력.")]
        [SerializeField] private float _handleRange = 80f;

        /// <summary>현재 입력 방향 (정규화 Vector2). 드래그 없으면 Vector2.zero.</summary>
        public Vector2 Direction { get; private set; }

        /// <summary>현재 입력 강도 (0~1). 드래그 거리/HandleRange 비율.</summary>
        public float Magnitude { get; private set; }

        private RectTransform _rootRect;
        private Camera _eventCamera;

        private void Awake()
        {
            _rootRect = (RectTransform)transform;

            // Screen Space - Overlay는 eventCamera=null로 충분. Camera/World Space는 Canvas worldCamera 사용.
            var canvas = GetComponentInParent<Canvas>();
            _eventCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera
                : null;

            HideStick();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_background == null || _handle == null) return;

            _background.gameObject.SetActive(true);
            _handle.gameObject.SetActive(true);

            // 터치 좌표를 root rect 로컬 좌표로 변환 → Background 중심을 그 위치로 이동.
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _rootRect, eventData.position, _eventCamera, out var localPoint))
            {
                _background.anchoredPosition = localPoint;
            }
            _handle.anchoredPosition = Vector2.zero;

            Direction = Vector2.zero;
            Magnitude = 0f;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (_background == null || _handle == null) return;

            // 터치 좌표를 Background rect 로컬 좌표로 변환 → 그대로 Background 중심 기준 오프셋.
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _background, eventData.position, _eventCamera, out var offset))
            {
                return;
            }

            float distance = offset.magnitude;
            if (distance > _handleRange)
            {
                offset = offset / distance * _handleRange;
                distance = _handleRange;
            }

            _handle.anchoredPosition = offset;
            Magnitude = _handleRange > 0f ? distance / _handleRange : 0f;
            Direction = distance > 0f ? offset / distance : Vector2.zero;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            HideStick();
        }

        private void HideStick()
        {
            if (_background != null) _background.gameObject.SetActive(false);
            if (_handle != null) _handle.gameObject.SetActive(false);
            Direction = Vector2.zero;
            Magnitude = 0f;
        }
    }
}
