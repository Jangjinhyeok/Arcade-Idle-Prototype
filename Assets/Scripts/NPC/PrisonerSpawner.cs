using UnityEngine;
using UnityEngine.Pool;

namespace PrisonLife
{
    /// <summary>
    /// PrisonerNPC ObjectPool 관리 + 시간 폴링 spawn (3일차 게이트 5a 결정 #22).
    /// Update에서 interval / maxConcurrent / Queue 가드 통과 시 SpawnOne 호출 → pool.Get → desk.TryEnqueue → prisoner.Initialize.
    /// _aliveCount는 ObjectPool actionOnGet/Release 콜백에서 관리 (OnDisable 의존 회피, 누락 케이스 무).
    /// </summary>
    /// <remarks>
    /// 게이트 5a 변경 (handoff §4 #22 락):
    /// - DeskZone.OnHandcuffStocked 구독 폐기 (HandleHandcuffStocked 메서드 폐기).
    /// - _deskPoint 필드 폐기 — desk.GetQueueSlot(slotIndex)로 PrisonerNPC가 동적 조회.
    /// - Update 시간 폴링 가드 3단(interval / maxConcurrent / Queue) → SpawnOne.
    /// - Pool 콜백 OnGet/Release에 _aliveCount ++/-- 박음, underflow 가드.
    /// Sub-Q 1-B — Awake에서 _lastSpawnTime을 interval만큼 과거로 박아 첫 frame 즉시 spawn.
    /// 5b 진입 시: Update 가드에 jail.RemainingCapacity > 0 추가 (Queue 가드 직전).
    /// </remarks>
    public class PrisonerSpawner : MonoBehaviour
    {
        [Tooltip("DeskZone 컴포넌트 ref. HasQueueSlot/TryEnqueue/RemoveFromQueue 호출 대상.")]
        [SerializeField] private DeskZone _desk;

        [Tooltip("PrisonerNPC 컴포넌트 보유한 prefab. Assets/Prefabs/Characters/Prisoner.prefab 드래그.")]
        [SerializeField] private GameObject _prisonerPrefab;

        [Tooltip("Prisoner 스폰 위치. 보통 (-10, 0, -2). NavMesh 위에 있어야 함.")]
        [SerializeField] private Transform _spawnPoint;

        [Tooltip("Money block 누적 + Player 픽업 사이트. Desk 옆 MoneyPileZone GameObject 드래그. 미부착 시 PrisonerNPC가 즉시 AddMoney fallback (block visual 없음).")]
        [SerializeField] private MoneyPileZone _moneyPile;

        [Tooltip("SpeechBubble billboard yaw 정렬용. Main Camera Transform 드래그. 미부착 시 SpeechBubble 회전 미적용 (LogWarning 1회).")]
        [SerializeField] private Transform _cameraTransform;

        [Tooltip("JailZone 컴포넌트 ref. PrisonerNPC.Initialize 7번째 인자 + Update 가드 (jailZone.RemainingCapacity > 0). 3일차 게이트 5b 결정 #22 + 5-A.")]
        [SerializeField] private JailZone _jailZone;

        private ObjectPool<PrisonerNPC> _pool;
        private int _aliveCount;
        private float _lastSpawnTime;

        private void Awake()
        {
            if (_desk == null) { Debug.LogError("[PrisonerSpawner] _desk not assigned. Spawner will no-op.", this); return; }
            if (_prisonerPrefab == null) { Debug.LogError("[PrisonerSpawner] _prisonerPrefab not assigned. Spawner will no-op.", this); return; }
            if (_spawnPoint == null) { Debug.LogError("[PrisonerSpawner] _spawnPoint not assigned. Spawner will no-op.", this); return; }
            // _deskPoint 검증 폐기 (게이트 5a 결정 #23 — desk.GetQueueSlot으로 동적 조회).

            if (_prisonerPrefab.GetComponent<PrisonerNPC>() == null)
            {
                Debug.LogError("[PrisonerSpawner] _prisonerPrefab has no PrisonerNPC component on root. Spawner will no-op.", this);
                return;
            }

            if (_moneyPile == null)
            {
                Debug.LogWarning("[PrisonerSpawner] _moneyPile not assigned. Prisoner will fall back to immediate AddMoney without block visual.", this);
            }
            if (_cameraTransform == null)
            {
                Debug.LogWarning("[PrisonerSpawner] _cameraTransform not assigned. SpeechBubble billboard rotation disabled.", this);
            }
            if (_jailZone == null)
            {
                Debug.LogWarning("[PrisonerSpawner] _jailZone not assigned. Update 가드는 spawn 차단 (RemainingCapacity 호출 불가). Inspector slot 드래그 필수.", this);
            }

            _pool = new ObjectPool<PrisonerNPC>(
                createFunc: CreatePrisoner,
                actionOnGet: OnGetPrisoner,
                actionOnRelease: OnReleasePrisoner,
                actionOnDestroy: OnDestroyPrisoner,
                collectionCheck: false,
                defaultCapacity: 3,
                maxSize: 10);

            // _desk.OnHandcuffStocked 구독 폐기 (게이트 5a 결정 #22 — Update 폴링으로 대체).

            // Sub-Q 1-B — 첫 spawn 즉시. _lastSpawnTime을 interval만큼 과거로 박아 게임 시작 첫 frame에 spawn 진입.
            var settings = GameManager.Instance != null ? GameManager.Instance.Settings : null;
            float interval = (settings != null) ? settings.NPC.SpawnIntervalSeconds : 2f;
            _lastSpawnTime = Time.time - interval;
        }

        private void OnDestroy()
        {
            // _desk.OnHandcuffStocked -= 폐기 (event 자체 사라짐, 게이트 5a).
            if (_pool != null)
            {
                _pool.Dispose();
                _pool = null;
            }
        }

        // --- Update 폴링 + SpawnOne (게이트 5a 결정 #22) ---

        private void Update()
        {
            if (_pool == null) return;                                                   // Awake 실패 가드.
            if (_desk == null) return;
            var settings = GameManager.Instance != null ? GameManager.Instance.Settings : null;
            if (settings == null) return;

            if (Time.time - _lastSpawnTime < settings.NPC.SpawnIntervalSeconds) return;  // ① interval.
            if (_aliveCount >= settings.NPC.MaxConcurrent) return;                       // ② maxConcurrent.
            if (_jailZone == null || _jailZone.RemainingCapacity <= 0) return;           // ③ jail cap. sub-Q 6 옵션 a fallback과 이중 보장 (3일차 게이트 5b).
            if (!_desk.HasQueueSlot()) return;                                           // ④ Queue.

            SpawnOne(settings);
        }

        private void SpawnOne(GameSettingsSO settings)
        {
            var prisoner = _pool.Get();             // actionOnGet에서 _aliveCount++.
            if (prisoner == null)
            {
                Debug.LogWarning("[PrisonerSpawner] Pool returned null prisoner. Skipping spawn.", this);
                return;
            }

            // Update 가드는 통과했지만 1프레임 race 방어.
            if (!_desk.TryEnqueue(prisoner, out int slotIndex))
            {
                Debug.LogWarning("[PrisonerSpawner] desk.TryEnqueue failed despite HasQueueSlot pre-guard — race. Releasing.", this);
                _pool.Release(prisoner);            // actionOnRelease에서 _aliveCount--.
                return;
            }

            int demand = Random.Range(settings.NPC.HandcuffDemandMin, settings.NPC.HandcuffDemandMax + 1);
            bool ok = prisoner.Initialize(_desk, this, slotIndex, _moneyPile, demand, _cameraTransform, _jailZone);
            if (!ok)
            {
                // Initialize 실패 — 옵션 B 분기(검증 통과 전 거부, _desk 미할당)에서 OnDisable의 RemoveFromQueue 미작동.
                // 명시적 RemoveFromQueue 호출로 dangling cleanup 보장. idempotent — 옵션 A 분기에서도 무해.
                _desk.RemoveFromQueue(prisoner);
                _pool.Release(prisoner);
                return;
            }

            _lastSpawnTime = Time.time;
        }

        // --- ObjectPool 사이클 콜백 4개 ---

        private PrisonerNPC CreatePrisoner()
        {
            var go = Instantiate(_prisonerPrefab, transform);
            var prisoner = go.GetComponent<PrisonerNPC>();
            if (prisoner == null)
            {
                Debug.LogError("[PrisonerSpawner] Instantiated prefab has no PrisonerNPC component. Pool will return null.", this);
                Destroy(go);
                return null;
            }
            return prisoner;
        }

        private void OnGetPrisoner(PrisonerNPC prisoner)
        {
            if (prisoner == null) return;
            prisoner.gameObject.SetActive(true);
            prisoner.transform.position = _spawnPoint.position;
            prisoner.transform.rotation = _spawnPoint.rotation;
            _aliveCount++;
        }

        private void OnReleasePrisoner(PrisonerNPC prisoner)
        {
            if (prisoner == null) return;
            prisoner.gameObject.SetActive(false); // OnDisable 자동 호출 → state/timer/SpeechBubble 리셋 + ResetPath + desk.RemoveFromQueue dangling cleanup.
            prisoner.transform.SetParent(transform, worldPositionStays: false);
            _aliveCount--;
            if (_aliveCount < 0)
            {
                Debug.LogError("[PrisonerSpawner] _aliveCount underflow. Reset to 0 — pool 콜백 호출 균형 깨짐.", this);
                _aliveCount = 0;
            }
        }

        private void OnDestroyPrisoner(PrisonerNPC prisoner)
        {
            if (prisoner != null) Destroy(prisoner.gameObject);
        }

        // --- 외부 노출 (PrisonerNPC가 Leaving 완료 시 호출) ---

        /// <summary>Prisoner가 스폰 위치 도달 후 풀에 회수 요청. PrisonerNPC.Update의 Leaving 분기에서 호출.</summary>
        public void Release(PrisonerNPC prisoner)
        {
            if (_pool == null || prisoner == null) return;
            _pool.Release(prisoner);
        }
    }
}
