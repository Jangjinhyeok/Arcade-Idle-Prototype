using UnityEngine;

namespace PrisonLife
{
    /// <summary>
    /// Tractor GameObject용 trigger sweep — 콜라이더 진입 OrePlacement 즉시 mine.
    /// 4일차 후속 — Tractor 채굴 패턴 grid 1×4 quantize 폐기 → 콜라이더 기반 sweep.
    /// </summary>
    /// <remarks>
    /// 부착 위치: Player 자식 Tractor GameObject (PlayerController._tractorObject).
    /// 필요 컴포넌트:
    ///   - Rigidbody (isKinematic, useGravity false) — Trigger 콜백 수신용 (compound 분리, Player Rigidbody와 격리).
    ///   - BoxCollider (isTrigger true) — sweep 폭 결정.
    /// OrePlacement.prefab에 BoxCollider isTrigger 부착 박혀야 함 (사용자 Inspector 작업).
    /// SetActive(false) 토글되는 OrePlacement는 trigger 비활성 → cooldown 자동 가드.
    /// OnTriggerEnter 대신 OnTriggerStay 박음 — Tractor SetActive(true) 직후 위에 박힌 OrePlacement도 mine.
    /// </remarks>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public class TractorMiner : MonoBehaviour
    {
        [Tooltip("MineZone ref. MineSinglePlacement 호출 대상. Player와 동일한 MineZone 슬롯 드래그.")]
        [SerializeField] private MineZone _mineZone;

        [Tooltip("Player ref. ore push 수신자. Player root 드래그.")]
        [SerializeField] private PlayerController _player;

        private void Awake()
        {
            if (_mineZone == null)
            {
                Debug.LogError("[TractorMiner] _mineZone not assigned. Tractor 채굴 no-op. Inspector slot 드래그 필요.", this);
            }
            if (_player == null)
            {
                Debug.LogError("[TractorMiner] _player not assigned. Tractor ore push no-op. Inspector slot 드래그 필요.", this);
            }
        }

        private void OnTriggerStay(Collider other)
        {
            if (_mineZone == null || _player == null) return;
            var placement = other.GetComponent<OrePlacement>();
            if (placement == null) return;
            _mineZone.MineSinglePlacement(placement.gameObject, _player);
        }
    }
}
