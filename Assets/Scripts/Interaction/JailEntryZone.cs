using UnityEngine;

namespace PrisonLife
{
    /// <summary>
    /// JailZone 진입 trigger box. PrisonerNPC가 "jail에 발을 들이는" 시점 감지.
    /// 4일차 신규 — 이전엔 JailZone Transform 자체로 destination 박았으나 별도 entry box 도입.
    /// </summary>
    /// <remarks>
    /// JailZone 자식 GameObject로 배치 (Inspector에서 JailZone._entryAnchor 슬롯에 본 GameObject Transform 드래그).
    /// 자체 BoxCollider isTrigger 박음 — Layer 이동 prisoner 충돌 가능 박음.
    /// OnTriggerEnter 시 PrisonerNPC.OnEnterJailEntry() 호출 — atomic 변환 + spawner.Release.
    /// NavMeshAgent + isTrigger 호환 위험 폴백 — PrisonerNPC.MovingToJail 분기에서 거리 검사 도달 박은 경우 DoJailConversion 직접 호출 (idempotent 가드 박힘).
    /// </remarks>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BoxCollider))]
    public class JailEntryZone : MonoBehaviour
    {
        private void Awake()
        {
            var col = GetComponent<BoxCollider>();
            if (col != null && !col.isTrigger)
            {
                Debug.LogWarning("[JailEntryZone] BoxCollider.isTrigger = false. trigger 발동 안 박힐 가능성 — Inspector에서 isTrigger 체크.", this);
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            var prisoner = other.GetComponentInParent<PrisonerNPC>();
            if (prisoner == null) return;
            prisoner.OnEnterJailEntry();
        }
    }
}
