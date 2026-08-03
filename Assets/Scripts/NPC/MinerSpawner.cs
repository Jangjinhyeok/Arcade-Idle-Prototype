using UnityEngine;
using UnityEngine.Pool;

namespace PrisonLife
{
    /// <summary>
    /// MinerNPC ObjectPool 관리 + 초기 1회 spawn (3일차 게이트 5d 결정 #33).
    /// MinerSpawnZone.ApplyUpgrade가 SetActive(true) 호출 → OnEnable에서 SpawnInitial(Settings.NPC.MinerSpawnCount = 3) 실행.
    /// time-based 추가 spawn 없음 — handoff §4 #33 lock.
    /// </summary>
    /// <remarks>
    /// PrisonerSpawner 패턴 복제 (ObjectPool 사이클 콜백 4개 + Release 외부 메서드).
    /// _initialSpawnDone 가드 — SetActive(true) 다회 호출 시 중복 SpawnInitial 차단 (방어코드, 사용자 박음).
    /// 게임 시작 시 SetActive(false) 시작 — MinerSpawnZone 결제 시 활성. 5c MinerSpawnZone Inspector slot에 본 GameObject 드래그 필요.
    /// </remarks>
    public class MinerSpawner : MonoBehaviour
    {
        [Tooltip("MinerNPC 컴포넌트 보유한 prefab. Assets/Prefabs/Characters/Miner.prefab 드래그 (Prisoner 패턴 복제, body Mat_Blue).")]
        [SerializeField] private GameObject _minerPrefab;

        [Tooltip("Miner 스폰 위치. Mine 근처 NavMesh 위 Empty Transform.")]
        [SerializeField] private Transform _spawnPoint;

        [Tooltip("MineZone ref — MinerNPC.Initialize 인자.")]
        [SerializeField] private MineZone _mineZone;

        [Tooltip("ProcessorMachine ref — MinerNPC.Initialize 인자. PushOreFromMiner 호출 대상.")]
        [SerializeField] private ProcessorMachine _processor;

        private ObjectPool<MinerNPC> _pool;
        private bool _initialSpawnDone;

        private void OnEnable()
        {
            if (_pool == null) InitPool();
            if (_pool == null) return;
            if (_initialSpawnDone) return;            // 중복 SpawnInitial 차단 (사용자 박음 방어코드).

            var settings = GameManager.Instance != null ? GameManager.Instance.Settings : null;
            int count = settings != null ? settings.NPC.MinerSpawnCount : 3;
            SpawnInitial(count);
            _initialSpawnDone = true;
        }

        private void OnDestroy()
        {
            if (_pool != null) { _pool.Dispose(); _pool = null; }
        }

        private void InitPool()
        {
            if (_minerPrefab == null) { Debug.LogError("[MinerSpawner] _minerPrefab not assigned. Spawner no-op.", this); return; }
            if (_spawnPoint == null) { Debug.LogError("[MinerSpawner] _spawnPoint not assigned. Spawner no-op.", this); return; }
            if (_mineZone == null) { Debug.LogError("[MinerSpawner] _mineZone not assigned. Spawner no-op.", this); return; }
            if (_processor == null) { Debug.LogError("[MinerSpawner] _processor not assigned. Spawner no-op.", this); return; }
            if (_minerPrefab.GetComponent<MinerNPC>() == null)
            {
                Debug.LogError("[MinerSpawner] _minerPrefab has no MinerNPC component on root. Spawner no-op.", this);
                return;
            }

            _pool = new ObjectPool<MinerNPC>(
                createFunc: CreateMiner,
                actionOnGet: OnGetMiner,
                actionOnRelease: OnReleaseMiner,
                actionOnDestroy: OnDestroyMiner,
                collectionCheck: false,
                defaultCapacity: 3,
                maxSize: 10);
        }

        private void SpawnInitial(int count)
        {
            for (int i = 0; i < count; i++)
            {
                var miner = _pool.Get();
                if (miner == null) continue;
                bool ok = miner.Initialize(_mineZone, _processor, this);
                if (!ok) _pool.Release(miner);
            }
        }

        // --- ObjectPool 사이클 콜백 4개 (PrisonerSpawner 패턴 복제) ---

        private MinerNPC CreateMiner()
        {
            var go = Instantiate(_minerPrefab, transform);
            var miner = go.GetComponent<MinerNPC>();
            if (miner == null)
            {
                Debug.LogError("[MinerSpawner] Instantiated prefab has no MinerNPC component. Pool will return null.", this);
                Destroy(go);
                return null;
            }
            return miner;
        }

        private void OnGetMiner(MinerNPC miner)
        {
            if (miner == null) return;
            miner.gameObject.SetActive(true);
            miner.transform.position = _spawnPoint.position;
            miner.transform.rotation = _spawnPoint.rotation;
        }

        private void OnReleaseMiner(MinerNPC miner)
        {
            if (miner == null) return;
            miner.gameObject.SetActive(false);
            miner.transform.SetParent(transform, worldPositionStays: false);
        }

        private void OnDestroyMiner(MinerNPC miner)
        {
            if (miner != null) Destroy(miner.gameObject);
        }

        /// <summary>외부 호출 — MinerNPC가 자체 종료 시 풀 회수 요청 (현 흐름엔 호출 안 됨).</summary>
        public void Release(MinerNPC miner)
        {
            if (_pool == null || miner == null) return;
            _pool.Release(miner);
        }
    }
}
