using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace PrisonLife
{
    /// <summary>
    /// 카메라 cinematic 연출 controller. 게이트 6.5 신설 — 시각 polish 단계.
    /// GameManager progression 이벤트 3건 (OnFirstMoneyEarned / OnDrillUpgraded / OnJailFull) 발행 시 카메라가 해금된 zone으로 초점 이동 + 머물름 + 복귀.
    /// timeScale 변경 무 (handoff §12 NICE 강등 폴리시) — 다른 시스템(NavMesh, UniTask 변환 루프 등) 정상 동작 유지.
    /// </summary>
    /// <remarks>
    /// 시퀀스 (총 3.0초): MOVE 1.0s → HOLD 1.0s → RETURN 1.0s.
    /// 컨트롤 토글: PlayerController.enabled = false + CameraFollow.enabled = false (입력/follow 차단).
    /// finally 블록에서 enabled 복원 보장 (cancellation 시).
    /// _isPlaying 가드 — 중복 발동 시 신규 이벤트 무시 (대기 안 함).
    /// HandleDrillUpgraded는 Tractor + MinerSpawn 동시 해금 — 공용 중간점 _drillUpgradedFocus 1회 초점 (게이트 6.5 Sub-Q 옵션 2 락).
    /// SpeechBubble / UpgradeZone billboard는 LateUpdate yaw 추적 — cinematic 중 카메라 yaw 변경 시 회전 따라감 (의도된 동작).
    /// 잔존 known-todo: Joystick UI 입력 차단 안 함 — cinematic 중 드래그 가능, 종료 직후 즉시 Player 이동 발생 위험. blocker 아님 (NICE polish deferred).
    /// </remarks>
    [DisallowMultipleComponent]
    public class CameraDirector : MonoBehaviour
    {
        private const float MOVE_DURATION = 1.0f;
        private const float HOLD_DURATION = 1.0f;
        private const float RETURN_DURATION = 1.0f;

        [Tooltip("Main Camera Transform. 연출 시 position/rotation 트윈 대상.")]
        [SerializeField] private Transform _camera;

        [Tooltip("기존 카메라 follow 스크립트. 연출 중 enabled=false 토글, 종료 시 복원.")]
        [SerializeField] private CameraFollow _cameraFollow;

        [Tooltip("PlayerController ref. 연출 중 enabled=false 토글 (입력 차단), 종료 시 복원.")]
        [SerializeField] private PlayerController _player;

        [Tooltip("OnFirstMoneyEarned 발행 시 카메라 초점 — DrillUpgradeZone Transform.")]
        [SerializeField] private Transform _drillUpgradeZone;

        [Tooltip("OnDrillUpgraded 발행 시 카메라 초점 — Tractor/MinerSpawn 공용 중간점 Transform (사용자 Empty 신규 + Position (7, 0, 5) 권장). 게이트 6.5 Sub-Q 옵션 2.")]
        [SerializeField] private Transform _drillUpgradedFocus;

        [Tooltip("OnJailFull 발행 시 카메라 초점 — JailUpgradeZone Transform.")]
        [SerializeField] private Transform _jailUpgradeZone;

        [Tooltip("OnJailUpgraded 발행 시 카메라 초점 — Jail 전체 GameObject Transform 권장. 4일차 wall swap cinematic.")]
        [SerializeField] private Transform _jailFocus;

        [Tooltip("Jail upgrade 전 wall GameObject (1/2). cinematic wall swap 단계에서 SetActive(false). 4일차 신규.")]
        [SerializeField] private GameObject _wallBeforeUpgradeJail0;

        [Tooltip("Jail upgrade 전 wall GameObject (2/2). cinematic wall swap 단계에서 SetActive(false). 4일차 신규.")]
        [SerializeField] private GameObject _wallBeforeUpgradeJail1;

        [Tooltip("Jail upgrade 후 wall GameObject. scene 시작 시 SetActive(false), cinematic wall swap 단계에서 SetActive(true). 4일차 신규.")]
        [SerializeField] private GameObject _wallUpgradeJail;

        [Tooltip("GameClearedUI ref. PlayJailUpgradedCinematic 종료 (player return 후) 시 HandleGameCleared 호출. 4일차 신규.")]
        [SerializeField] private GameClearedUI _gameClearedUI;

        private bool _isPlaying;

        private void Awake()
        {
            if (_camera == null)
            {
                Debug.LogError("[CameraDirector] _camera not assigned. Cinematic 비활성. Inspector에서 Main Camera Transform 드래그 필수.", this);
            }

            if (GameManager.Instance != null)
            {
                GameManager.Instance.OnFirstMoneyEarned += HandleFirstMoneyEarned;
                GameManager.Instance.OnDrillUpgraded += HandleDrillUpgraded;
                GameManager.Instance.OnJailFull += HandleJailFull;
                GameManager.Instance.OnJailUpgraded += HandleJailUpgraded;
            }
            else
            {
                Debug.LogError("[CameraDirector] GameManager.Instance null at Awake. 이벤트 구독 실패 — Script Execution Order 확인.", this);
            }
        }

        private void OnDestroy()
        {
            if (GameManager.Instance != null)
            {
                GameManager.Instance.OnFirstMoneyEarned -= HandleFirstMoneyEarned;
                GameManager.Instance.OnDrillUpgraded -= HandleDrillUpgraded;
                GameManager.Instance.OnJailFull -= HandleJailFull;
                GameManager.Instance.OnJailUpgraded -= HandleJailUpgraded;
            }
        }

        // --- 핸들러 3개 ---

        private void HandleFirstMoneyEarned() => PlayCinematic(_drillUpgradeZone, this.destroyCancellationToken).Forget();
        private void HandleDrillUpgraded() => PlayCinematic(
            _drillUpgradedFocus,
            this.destroyCancellationToken,
            returnPosProvider: () => _cameraFollow != null ? _cameraFollow.GetFollowPosition() : (_camera != null ? _camera.position : Vector3.zero)).Forget();
        private void HandleJailFull() => PlayCinematic(_jailUpgradeZone, this.destroyCancellationToken).Forget();
        private void HandleJailUpgraded() => PlayJailUpgradedCinematic(this.destroyCancellationToken).Forget();

        // --- Cinematic 본체 ---

        private async UniTaskVoid PlayCinematic(Transform target, CancellationToken ct, Func<Vector3> returnPosProvider = null)
        {
            if (target == null)
            {
                Debug.LogWarning("[CameraDirector] target null. Cinematic skip.", this);
                return;
            }
            if (_isPlaying) return;
            if (_camera == null) return;

            _isPlaying = true;

            // 컨트롤 토글 — 입력/follow 비활성. timeScale 무관 (다른 시스템 정상 동작).
            bool prevPlayer = _player != null && _player.enabled;
            bool prevFollow = _cameraFollow != null && _cameraFollow.enabled;
            if (_player != null) _player.enabled = false;
            if (_cameraFollow != null) _cameraFollow.enabled = false;

            Vector3 originalPos = _camera.position;
            Quaternion originalRot = _camera.rotation;
            Vector3 targetPos = target.position + new Vector3(0f, 5f, -3f);    // 위에서 약간 뒤 (단순 offset).
            Quaternion targetRot = Quaternion.LookRotation(target.position - targetPos, Vector3.up);

            // RETURN 도착 위치 — returnPosProvider 박힘 시 (HandleDrillUpgraded) follow 추적 위치로 Lerp 보간 (A-2),
            // null 시 (HandleFirstMoneyEarned / HandleJailFull) originalPos로 RETURN (기존 A-1 동작).
            bool useFollowResume = returnPosProvider != null;

            try
            {
                await LerpCameraAsync(originalPos, targetPos, originalRot, targetRot, MOVE_DURATION, ct);
                await UniTask.Delay((int)(HOLD_DURATION * 1000), cancellationToken: ct);
                Vector3 returnPos = useFollowResume ? returnPosProvider() : originalPos;
                await LerpCameraAsync(targetPos, returnPos, targetRot, originalRot, RETURN_DURATION, ct);
            }
            catch (OperationCanceledException) { /* finally로 복원 */ }
            finally
            {
                // useFollowResume 시 originalPos 강제 set 박지 않음 — CameraFollow가 다음 LateUpdate에 즉시 자기 위치 박음 (RETURN 끝 위치 ≈ follow 위치라 시각적 무 jitter).
                if (!useFollowResume && _camera != null)
                {
                    _camera.position = originalPos;
                    _camera.rotation = originalRot;
                }
                if (_player != null) _player.enabled = prevPlayer;
                if (_cameraFollow != null) _cameraFollow.enabled = prevFollow;
                _isPlaying = false;
            }
        }

        /// <summary>JailUpgrade 전용 cinematic — 4단계: ① jail focus zoom → ② wall swap (BeforeUpgrade SetActive(false) + Upgrade SetActive(true)) → ③ player follow 위치로 return → ④ GameClearedUI 발동.
        /// timeScale=0은 GameClearedUI.HandleGameCleared 마지막 line에서 박힘 — cinematic 동안 정상 동작.</summary>
        private async UniTaskVoid PlayJailUpgradedCinematic(CancellationToken ct)
        {
            if (_camera == null || _jailFocus == null)
            {
                Debug.LogWarning("[CameraDirector] _camera or _jailFocus null. PlayJailUpgradedCinematic skip — GameClearedUI 직접 발동.", this);
                if (_gameClearedUI != null) _gameClearedUI.HandleGameCleared();
                return;
            }

            // 4일차 결함 보정 — OnJailFull cinematic 박는 도중 OnJailUpgraded 발행 박을 case (Player가 jail upgrade zone 위에서 cinematic 박힌 상태로 결제 완료).
            // 즉시 _isPlaying skip 박음 → GameClearedUI 미발동 → game clear 안 박음. wait 후 진행 박아 cinematic + game clear 보장.
            if (_isPlaying)
            {
                try
                {
                    await UniTask.WaitUntil(() => !_isPlaying, cancellationToken: ct);
                }
                catch (OperationCanceledException) { return; }
            }

            _isPlaying = true;

            bool prevPlayer = _player != null && _player.enabled;
            bool prevFollow = _cameraFollow != null && _cameraFollow.enabled;
            if (_player != null) _player.enabled = false;
            if (_cameraFollow != null) _cameraFollow.enabled = false;

            Vector3 originalPos = _camera.position;
            Quaternion originalRot = _camera.rotation;
            Vector3 jailTarget = _jailFocus.position + new Vector3(0f, 5f, -3f);
            Quaternion jailRot = Quaternion.LookRotation(_jailFocus.position - jailTarget, Vector3.up);

            try
            {
                // 1. jail로 zoom (MOVE 1.0s).
                await LerpCameraAsync(originalPos, jailTarget, originalRot, jailRot, MOVE_DURATION, ct);

                // 2. wall swap — Hold 0.5s 후 swap, 그 후 0.5s 더 hold (사용자 인식 시간).
                await UniTask.Delay((int)(HOLD_DURATION * 500), cancellationToken: ct);
                if (_wallBeforeUpgradeJail0 != null) _wallBeforeUpgradeJail0.SetActive(false);
                if (_wallBeforeUpgradeJail1 != null) _wallBeforeUpgradeJail1.SetActive(false);
                if (_wallUpgradeJail != null) _wallUpgradeJail.SetActive(true);
                await UniTask.Delay((int)(HOLD_DURATION * 500), cancellationToken: ct);

                // 3. player follow 위치로 return (RETURN 1.0s).
                Vector3 returnPos = _cameraFollow != null ? _cameraFollow.GetFollowPosition() : originalPos;
                await LerpCameraAsync(jailTarget, returnPos, jailRot, originalRot, RETURN_DURATION, ct);

                // 4. GameClearedUI 발동 (cancellation 안 박힌 정상 종료 시만).
                if (_gameClearedUI != null) _gameClearedUI.HandleGameCleared();
                else Debug.LogWarning("[CameraDirector] _gameClearedUI not assigned. cinematic 종료 후 GameClearedUI 발동 무.", this);
            }
            catch (OperationCanceledException) { /* 카메라 복원만, GameClearedUI 발동 안 박음 */ }
            finally
            {
                if (_player != null) _player.enabled = prevPlayer;
                if (_cameraFollow != null) _cameraFollow.enabled = prevFollow;
                _isPlaying = false;
            }
        }

        private async UniTask LerpCameraAsync(Vector3 fromPos, Vector3 toPos, Quaternion fromRot, Quaternion toRot, float duration, CancellationToken ct)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                ct.ThrowIfCancellationRequested();
                if (_camera == null) return;
                float p = elapsed / duration;
                _camera.position = Vector3.Lerp(fromPos, toPos, p);
                _camera.rotation = Quaternion.Slerp(fromRot, toRot, p);
                elapsed += Time.deltaTime;
                await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate, ct);
            }
            if (_camera != null)
            {
                _camera.position = toPos;
                _camera.rotation = toRot;
            }
        }
    }
}
