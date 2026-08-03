using UnityEngine;
using UnityEngine.Pool;

namespace PrisonLife
{
    /// <summary>
    /// DeskNPC ObjectPool 관리 + 1회 spawn (4일차 Desk + DeskNPC 시스템).
    /// DeskNPCSpawnUpgradeZone.ApplyUpgrade가 SetActive(true) 호출 → OnEnable에서 SpawnInitial 1명.
    /// time-based 추가 spawn 무 — 영구 1명 cycle.
    /// </summary>
    /// <remarks>
    /// MinerSpawner 패턴 복제. 단 spawn count = 1 고정 (영구 단일 인스턴스).
    /// _initialSpawnDone 가드 — SetActive(true) 다회 호출 시 중복 spawn 차단.
    /// 게임 시작 시 SetActive(false) 시작 — DeskNPCSpawnUpgradeZone 결제 시 활성.
    /// </remarks>
    public class DeskNPCSpawner : MonoBehaviour
    {
        [Tooltip("DeskNPC 컴포넌트 보유한 prefab. Assets/Prefabs/Characters/DeskNPC.prefab 드래그.")]
        [SerializeField] private GameObject _deskNPCPrefab;

        [Tooltip("DeskNPC 스폰 위치. NavMesh 위 Empty Transform.")]
        [SerializeField] private Transform _spawnPoint;

        [Tooltip("ProcessorMachine ref — DeskNPC.Initialize 인자. TryRequestHandcuffsForDeskNPC 호출 대상.")]
        [SerializeField] private ProcessorMachine _processor;

        [Tooltip("DeskZone ref — DeskNPC.Initialize 인자. DepositHandcuffFromDeskNPC 호출 대상.")]
        [SerializeField] private DeskZone _desk;

        [Tooltip("ProcessorOutput 인근 Transform — DeskNPC가 AtOutput 박는 위치.")]
        [SerializeField] private Transform _outputAnchor;

        [Tooltip("Desk 인근 Transform — DeskNPC가 AtDesk 박는 위치 (PrisonerNPC slot 0과 분리).")]
        [SerializeField] private Transform _deskAnchor;

        private ObjectPool<DeskNPC> _pool;
        private bool _initialSpawnDone;

        private void OnEnable()
        {
            if (_pool == null) InitPool();
            if (_pool == null) return;
            if (_initialSpawnDone) return;

            SpawnInitial();
            _initialSpawnDone = true;
        }

        private void OnDestroy()
        {
            if (_pool != null) { _pool.Dispose(); _pool = null; }
        }

        private void InitPool()
        {
            if (_deskNPCPrefab == null) { Debug.LogError("[DeskNPCSpawner] _deskNPCPrefab not assigned. Spawner no-op.", this); return; }
            if (_spawnPoint == null) { Debug.LogError("[DeskNPCSpawner] _spawnPoint not assigned. Spawner no-op.", this); return; }
            if (_processor == null) { Debug.LogError("[DeskNPCSpawner] _processor not assigned. Spawner no-op.", this); return; }
            if (_desk == null) { Debug.LogError("[DeskNPCSpawner] _desk not assigned. Spawner no-op.", this); return; }
            if (_outputAnchor == null) { Debug.LogError("[DeskNPCSpawner] _outputAnchor not assigned. Spawner no-op.", this); return; }
            if (_deskAnchor == null) { Debug.LogError("[DeskNPCSpawner] _deskAnchor not assigned. Spawner no-op.", this); return; }
            if (_deskNPCPrefab.GetComponent<DeskNPC>() == null)
            {
                Debug.LogError("[DeskNPCSpawner] _deskNPCPrefab has no DeskNPC component on root. Spawner no-op.", this);
                return;
            }

            _pool = new ObjectPool<DeskNPC>(
                createFunc: CreateDeskNPC,
                actionOnGet: OnGetDeskNPC,
                actionOnRelease: OnReleaseDeskNPC,
                actionOnDestroy: OnDestroyDeskNPC,
                collectionCheck: false,
                defaultCapacity: 1,
                maxSize: 2);
        }

        private void SpawnInitial()
        {
            var npc = _pool.Get();
            if (npc == null) return;
            bool ok = npc.Initialize(_processor, _desk, this, _outputAnchor, _deskAnchor);
            if (!ok) _pool.Release(npc);
        }

        // --- ObjectPool 사이클 콜백 4개 ---

        private DeskNPC CreateDeskNPC()
        {
            var go = Instantiate(_deskNPCPrefab, transform);
            var npc = go.GetComponent<DeskNPC>();
            if (npc == null)
            {
                Debug.LogError("[DeskNPCSpawner] Instantiated prefab has no DeskNPC component. Pool will return null.", this);
                Destroy(go);
                return null;
            }
            return npc;
        }

        private void OnGetDeskNPC(DeskNPC npc)
        {
            if (npc == null) return;
            npc.gameObject.SetActive(true);
            npc.transform.position = _spawnPoint.position;
            npc.transform.rotation = _spawnPoint.rotation;
        }

        private void OnReleaseDeskNPC(DeskNPC npc)
        {
            if (npc == null) return;
            npc.gameObject.SetActive(false);
            npc.transform.SetParent(transform, worldPositionStays: false);
        }

        private void OnDestroyDeskNPC(DeskNPC npc)
        {
            if (npc != null) Destroy(npc.gameObject);
        }

        /// <summary>외부 호출 — DeskNPC가 자체 종료 시 풀 회수 요청 (현 흐름엔 호출 안 됨, 영구 단일 인스턴스).</summary>
        public void Release(DeskNPC npc)
        {
            if (_pool == null || npc == null) return;
            _pool.Release(npc);
        }
    }
}
