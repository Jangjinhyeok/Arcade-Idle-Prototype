using UnityEngine;

namespace PrisonLife
{
    /// <summary>
    /// 플레이어 컨트롤러. ADR-001 §1 (Rigidbody.MovePosition + Kinematic). GDD §2.
    /// </summary>
    /// <remarks>
    /// FixedUpdate에서 JoystickInput 폴링 + 카메라 yaw 보정 후 MovePosition 호출.
    /// Stack 시각 보간은 StackContainer.LateUpdate가 담당 — 여기서는 이동만 처리(위험 d 분리).
    /// IInteractionUser는 R1 누적 타이머 키 역할 마커만 — 멤버 없음.
    /// 게이트 6.5 추가:
    /// - Animator IsMoving + IsMining 분기. IsMining = _isInsideMineZone AND !isMoving AND !IsDrillUpgraded.
    /// - Drill/Tractor object 자동 토글 (UpdateVisuals — _mineZone.OnPlayerInsideChanged + GameManager.OnDrillUpgraded/OnTractorUpgraded 이벤트 구독).
    /// - SetMining 메서드 폐기 — MineZone이 OnPlayerInsideChanged 이벤트로 통신.
    /// IsKinematic 토글은 Inspector에서 체크 필요 — 코드에서 강제하지 않음.
    /// </remarks>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public class PlayerController : MonoBehaviour, IInteractionUser
    {
        private const float DEADZONE = 0.1f;
        private const float ROTATION_SPEED_DEFAULT = 360f;
        private const float MOVE_SPEED_FALLBACK = 5f;

        [Tooltip("화면 입력 소스. UI Canvas 자식 Joystick GameObject 드래그.")]
        [SerializeField] private JoystickInput _joystick;

        [Tooltip("yaw 보정에 사용할 카메라 Transform. Main Camera 드래그 (Camera.main 매 프레임 쿼리 회피).")]
        [SerializeField] private Transform _cameraTransform;

        [Tooltip("Inspector 주입 우선. 비어 있으면 Awake에서 GetComponent fallback.")]
        [SerializeField] private Rigidbody _rigidbody;

        [Tooltip("회전 속도 (deg/sec). 3일차 GameSettingsSO 이동 예정. Quaternion.RotateTowards 단위.")]
        [SerializeField] private float _rotationSpeed = ROTATION_SPEED_DEFAULT;

        [Tooltip("플레이어 등 뒤 head StackContainer (광석·수갑 적재). Inspector 주입 권장. 비어있으면 GetComponentInChildren fallback이 적용되나 BackStack과 혼선 위험 — Inspector 명시 권장.")]
        [SerializeField] private StackContainer _stack;

        [Tooltip("플레이어 등 뒤 BackStack — money 전용. data 무한 + visual cap 8. Inspector 필수 명시 (fallback 없음). MoneyPileZone 픽업 + UpgradeZone 결제 트윈 도착·출발 사이트.")]
        [SerializeField] private StackContainer _backStack;

        [Tooltip("Animator. char01_X.prefab Animator 컴포넌트 ref. CharacterAnimator Controller + IsMoving + IsMining 파라미터. 게이트 6.5 신설.")]
        [SerializeField] private Animator _animator;

        [Tooltip("Drill 결제 후 Mine 영역 진입 시 활성. 사용자 GUI에서 Player 자식 Drill GameObject 드래그. 게이트 6.5 추가.")]
        [SerializeField] private GameObject _drillObject;

        [Tooltip("Tractor 결제 후 Mine 영역 진입 시 활성. 사용자 GUI에서 Player 자식 Tractor GameObject 드래그. 게이트 6.5 추가.")]
        [SerializeField] private GameObject _tractorObject;

        [Tooltip("MineZone ref. OnPlayerInsideChanged 이벤트 구독 — UpdateVisuals 트리거 + Animator IsMining 분기. 게이트 6.5 추가.")]
        [SerializeField] private MineZone _mineZone;

        private bool _isInsideMineZone;

        /// <summary>플레이어 등 뒤 head StackContainer. Mine/Processor/Desk가 OnInteractionComplete에서 광석·수갑 적재·소비 대상으로 참조.</summary>
        public StackContainer Stack => _stack;

        /// <summary>플레이어 등 뒤 BackStack (money 전용). MoneyPileZone이 픽업 대상으로, UpgradeZone(4일차)이 결제 출발 대상으로 참조. data 무한, visual cap만 적용.</summary>
        public StackContainer BackStack => _backStack;

        private void Awake()
        {
            if (_rigidbody == null) _rigidbody = GetComponent<Rigidbody>();
            if (_stack == null)
            {
                _stack = GetComponentInChildren<StackContainer>(true);
                if (_stack != null && _stack == _backStack) _stack = null;
            }
            if (_stack == null)
            {
                Debug.LogError("[PlayerController] _stack not assigned. Mine/Processor/Desk 적재 no-op. Drag Player/HeadStack into Inspector.", this);
            }
            if (_backStack == null)
            {
                Debug.LogError("[PlayerController] _backStack not assigned. MoneyPile pickup no-op. Drag Player/BackAnchor/BackStack into Inspector.", this);
            }
            else
            {
                _backStack.AcceptedType = StackableType.Money;
            }

            // 게이트 6.5 — MineZone + GameManager 이벤트 구독.
            if (_mineZone != null)
            {
                _mineZone.OnPlayerInsideChanged += HandleMineZoneInsideChanged;
            }
            else
            {
                Debug.LogWarning("[PlayerController] _mineZone not assigned. Drill/Tractor 시각 + Mining animation 비활성. Inspector slot 드래그 필요.", this);
            }

            if (GameManager.Instance != null)
            {
                GameManager.Instance.OnDrillUpgraded += HandleDrillUpgraded;
                GameManager.Instance.OnTractorUpgraded += HandleTractorUpgraded;
            }

            // 초기 Drill/Tractor 비활성 — UpdateVisuals 첫 호출 (안전 차원).
            UpdateVisuals();
        }

        private void OnDestroy()
        {
            if (_mineZone != null) _mineZone.OnPlayerInsideChanged -= HandleMineZoneInsideChanged;
            if (GameManager.Instance != null)
            {
                GameManager.Instance.OnDrillUpgraded -= HandleDrillUpgraded;
                GameManager.Instance.OnTractorUpgraded -= HandleTractorUpgraded;
            }
        }

        private void FixedUpdate()
        {
            if (_joystick == null || _cameraTransform == null || _rigidbody == null) return;

            float magnitude = _joystick.Magnitude;
            bool isMoving = magnitude >= DEADZONE;

            // Animator IsMoving + IsMining 분기 (게이트 6.5).
            // IsMining 조건: Mine 영역 안 + 정지 + Drill 미결제 + Tractor 미결제 + reach 안 active OrePlacement 1+ 존재.
            // 4일차 결함 보정 — reach 안 ore 무 시 Mining animation 차단 (Player가 Mine 안 가운데 있어도 멀리 active만 있으면 채굴 안 박음, animation 정합).
            // Drill 결제 후엔 Drill object가 자동 채굴, Player Mining animation 안 함.
            // Tractor 결제 후엔 Tractor 콜라이더가 sweep 채굴 박음 — Player가 트랙터 밀고 다니므로 곡괭이 mining 애니 부적합.
            if (_animator != null)
            {
                _animator.SetBool("IsMoving", isMoving);
                bool drillUpgraded = GameManager.Instance != null && GameManager.Instance.IsDrillUpgraded;
                bool tractorUpgraded = GameManager.Instance != null && GameManager.Instance.IsTractorUpgraded;
                bool hasReachable = _mineZone != null && _mineZone.HasActiveInReach(transform.position);
                bool isMining = _isInsideMineZone && !isMoving && !drillUpgraded && !tractorUpgraded && hasReachable;
                _animator.SetBool("IsMining", isMining);
            }

            if (!isMoving) return;

            // 카메라 yaw 분해 → 입력 (x,y) 평면을 월드 forward/right로 회전.
            Vector2 dir2D = _joystick.Direction;
            float yaw = _cameraTransform.eulerAngles.y;
            Quaternion yawRot = Quaternion.Euler(0f, yaw, 0f);
            Vector3 worldDir = yawRot * new Vector3(dir2D.x, 0f, dir2D.y);

            // 속도: GameSettingsSO 우선, 미설정 시 fallback (Awake 타이밍 race 안전장치).
            float speed = MOVE_SPEED_FALLBACK;
            if (GameManager.Instance != null && GameManager.Instance.Settings != null)
            {
                speed = GameManager.Instance.Settings.Player.MoveSpeed;
            }

            // 이동: Kinematic Rigidbody는 MovePosition으로 한 프레임씩 갱신. Trigger/Collider 모두 정상 동작.
            Vector3 delta = worldDir * (magnitude * speed * Time.fixedDeltaTime);
            _rigidbody.MovePosition(_rigidbody.position + delta);

            // 회전: RotateTowards (deg/sec 단위, _rotationSpeed와 단위 정합). Slerp는 0~1 weight라 단위 불일치.
            Quaternion targetRot = Quaternion.LookRotation(worldDir, Vector3.up);
            Quaternion newRot = Quaternion.RotateTowards(
                _rigidbody.rotation, targetRot, _rotationSpeed * Time.fixedDeltaTime);
            _rigidbody.MoveRotation(newRot);
        }

        /// <summary>MineZone.SpawnAndPushOre의 user is PlayerController 분기에서 호출. 4일차 Mine 단일화 — duration 도달 시 MineZone이 ore spawn + 본 메서드로 전달. Stack가드 (handcuff 혼재 / IsFull) 시 풀 회수.</summary>
        public void PushOre(Stackable ore)
        {
            if (ore == null) return;
            if (_stack == null || _stack.IsFull || _stack.ContainsType(StackableType.Handcuff))
            {
                ore.ReturnToPool();
                return;
            }
            int slot = _stack.Add(ore);
            if (slot < 0) ore.ReturnToPool();
        }

        // --- 게이트 6.5 신규 — 이벤트 핸들러 + UpdateVisuals ---

        private void HandleMineZoneInsideChanged(bool inside)
        {
            _isInsideMineZone = inside;
            UpdateVisuals();
        }

        private void HandleDrillUpgraded() => UpdateVisuals();
        private void HandleTractorUpgraded() => UpdateVisuals();

        /// <summary>Drill/Tractor object SetActive 토글. _mineZone.OnPlayerInsideChanged + GameManager.OnDrillUpgraded/OnTractorUpgraded 이벤트 시점에 호출.
        /// 조건: drill = IsDrillUpgraded AND _isInsideMineZone / tractor = IsTractorUpgraded AND _isInsideMineZone.</summary>
        private void UpdateVisuals()
        {
            if (GameManager.Instance == null) return;
            bool drillActive = GameManager.Instance.IsDrillUpgraded && _isInsideMineZone;
            bool tractorActive = GameManager.Instance.IsTractorUpgraded && _isInsideMineZone;
            if (_drillObject != null) _drillObject.SetActive(drillActive);
            if (_tractorObject != null) _tractorObject.SetActive(tractorActive);
        }
    }
}
