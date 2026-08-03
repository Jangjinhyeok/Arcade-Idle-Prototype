using UnityEngine;

namespace PrisonLife
{
    /// <summary>
    /// Mine zone visual placement marker (배치용). 4일차 Mine 단일화 결정.
    /// Mesh + material은 prefab에서 처리 — 본 컴포넌트는 식별 마커만.
    /// Trigger / accumulator / claim 은 MineZone 본체로 이전 — 배치점은 시각 표시 전용.
    /// </summary>
    [DisallowMultipleComponent]
    public class OrePlacement : MonoBehaviour
    {
    }
}
