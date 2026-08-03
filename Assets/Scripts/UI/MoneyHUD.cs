using TMPro;
using UnityEngine;

namespace PrisonLife
{
    /// <summary>
    /// 우상단 Money UI. GameManager.OnMoneyChanged 이벤트 구독 → TextMeshPro 갱신.
    /// 단순 구조 — 트윈/애니메이션 없음. 3일차 폴리시 후보.
    /// </summary>
    /// <remarks>
    /// 2일차 결정: Script Execution Order에서 PrisonLife.GameManager를 -100 설정.
    /// GameManager.Awake가 모든 MoneyHUD.OnEnable보다 먼저 실행됨을 보장 — Hierarchy 순서와 무관.
    /// </remarks>
    [RequireComponent(typeof(TextMeshProUGUI))]
    public class MoneyHUD : MonoBehaviour
    {
        [Tooltip("Money 표시용 TextMeshProUGUI. Inspector 주입 우선, 비어있으면 Awake에서 GetComponent fallback.")]
        [SerializeField] private TextMeshProUGUI _text;

        private void Awake()
        {
            if (_text == null) _text = GetComponent<TextMeshProUGUI>();
            if (_text == null)
            {
                Debug.LogError("[MoneyHUD] TextMeshProUGUI not found on GameObject. HUD will no-op.", this);
            }
        }

        private void OnEnable()
        {
            if (_text == null) return;

            if (GameManager.Instance != null)
            {
                GameManager.Instance.OnMoneyChanged += HandleMoneyChanged;
                _text.text = $"${GameManager.Instance.Money}"; // 초기 동기화 — 활성화 시점의 Money 즉시 표시.
            }
            else
            {
                Debug.LogWarning("[MoneyHUD] GameManager.Instance null at OnEnable. Resolution: Edit → Project Settings → Script Execution Order에서 PrisonLife.GameManager를 -100으로 설정. 또는 MoneyHUD에 Start 폴백 추가.", this);
            }
        }

        private void OnDisable()
        {
            if (GameManager.Instance != null)
            {
                GameManager.Instance.OnMoneyChanged -= HandleMoneyChanged;
            }
        }

        private void HandleMoneyChanged(int newMoney)
        {
            if (_text != null) _text.text = $"${newMoney}";
        }
    }
}
