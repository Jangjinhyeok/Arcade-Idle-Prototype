using TMPro;
using UnityEngine;

namespace PrisonLife
{
    /// <summary>
    /// 게임 종료 화면 controller. handoff §4 #34 lock — JailUpgrade 1회 결제 완료 시 GameManager.OnJailUpgraded 발행 → "Game Cleared" 표시 + Time.timeScale = 0 일시정지.
    /// </summary>
    /// <remarks>
    /// 구조 분리: 본 컴포넌트 부착 GameObject (Canvas root) = 활성 유지 (Awake/OnDestroy 메시지 보장).
    /// _root는 자식 GameObject (ContentRoot — Background + Text 묶음) — SetActive 토글 대상.
    /// 본인 GameObject SetActive(false) 시 OnDestroy 미호출 위험 회피.
    /// 재시작 버튼 폐기 (handoff §12 NICE 강등 — 재시작 = Application.Quit + 재실행). 페이드인 트윈 폐기 (단순화 우위).
    /// 동작 정합: JailUpgradeZone.PerformPurchaseAsync 마지막 RaiseProgressionEvent → NotifyJailUpgraded → OnJailUpgraded → HandleGameCleared → SetActive(true) + timeScale=0.
    /// timeScale=0 이후 PrisonerSpawner.Update / JailZone Update 등 Time.time/deltaTime 의존 시스템 모두 정지 — 의도된 동작.
    /// </remarks>
    public class GameClearedUI : MonoBehaviour
    {
        [Tooltip("Canvas 자식 ContentRoot (Background + Text 묶음). HandleGameCleared 시 SetActive(true) 토글. 본 컴포넌트 부착 GameObject 자체는 활성 유지 (Awake 메시지 보장).")]
        [SerializeField] private GameObject _root;

        [Tooltip("\"Game Cleared!\" 표시 TextMeshProUGUI. 초기 텍스트는 Awake에서 박음.")]
        [SerializeField] private TextMeshProUGUI _text;

        private void Awake()
        {
            if (_root == null)
            {
                Debug.LogError("[GameClearedUI] _root not assigned. Inspector에서 ContentRoot 자식 GameObject 드래그 필수.", this);
            }
            else
            {
                _root.SetActive(false);
            }

            if (_text == null)
            {
                Debug.LogWarning("[GameClearedUI] _text not assigned. 초기 텍스트 표시 안 됨.", this);
            }
            else
            {
                _text.text = "GAME CLEARED";
            }

            // 4일차 변경 — OnJailUpgraded 직접 구독 폐기. CameraDirector.PlayJailUpgradedCinematic 끝 단계에서 HandleGameCleared 명시적 호출 박음.
        }

        /// <summary>CameraDirector cinematic (jail focus + wall swap + player return) 종료 시점에 호출. SetActive(true) + Time.timeScale = 0.</summary>
        public void HandleGameCleared()
        {
            if (_root != null) _root.SetActive(true);
            Time.timeScale = 0f;
        }
    }
}
