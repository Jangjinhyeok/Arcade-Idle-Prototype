using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Pool;

namespace PrisonLife
{
    /// <summary>
    /// 감옥 입구 + grid slot 관리 + jail object pool. 3일차 게이트 5b 결정 #25, #26.
    /// PrisonerNPC가 MovingToJail 도달 시 TryConvertToJailObject 호출 → 다음 빈 grid slot에 jail object spawn (Prisoner.prefab 재활용, NavMeshAgent 비활성, scale-up 트윈 0.3s).
    /// IncreaseCapacity는 JailUpgradeZone (게이트 5c) 호출.
    /// </summary>
    /// <remarks>
    /// 두 풀 격리: jail object pool (본 클래스) ≠ prisoner pool (PrisonerSpawner). prefab 동일하지만 instance 별개 — dangling 우려 무.
    /// jail object FSM: Initialize 호출 안 함 → _state = Spawning 유지 + Update의 Spawning 분기 무동작. NavMeshAgent.enabled = false (handoff §4 #26).
    /// scale-up 트윈: UniTaskVoid + LastPostLateUpdate (StackContainer.TweenToSlotAsync 패턴 복제, 결정 #26 + 4).
    /// 게이트 5b 결정 1 (단순화): JailZone Transform.position 자체를 entrance destination으로 사용. 사용자가 GameObject (8, 0, -8) 배치.
    /// 게이트 5b sub-Q 6 옵션 a: jail full race 시 호출자(PrisonerNPC) 책임으로 spawner.Release 처리.
    /// </remarks>
    [DisallowMultipleComponent]
    public class JailZone : MonoBehaviour
    {
        [Tooltip("Jail object용 prefab — Prisoner.prefab 재활용 (handoff §4 #26). PrisonerNPC 컴포넌트 보유 필수, 단 Initialize 호출 안 됨.")]
        [SerializeField] private GameObject _jailObjectPrefab;

        [Tooltip("PrisonerNPC.MovingToJail destination 위치 Transform. JailEntryZone (BoxCollider isTrigger) 부착 자식 GameObject 권장. null 시 JailZone.transform 폴백. 4일차 신규.")]
        [SerializeField] private Transform _entryAnchor;

        [Tooltip("Active count / capacity 표시 billboard root GameObject. LateUpdate에서 카메라 yaw 정렬. null 가능 — 박지 않으면 표시 무.")]
        [SerializeField] private GameObject _countBillboardRoot;

        [Tooltip("Active count / capacity 표시 TMP_Text. \"{ActiveCount} / {CurrentCapacity}\" 형태. TryConvertToJailObject + IncreaseCapacity 시 갱신.")]
        [SerializeField] private TMP_Text _countText;

        [Tooltip("count billboard yaw 정렬용 카메라 Transform. 비어있으면 Awake에서 Camera.main 폴백.")]
        [SerializeField] private Transform _cameraTransform;

        /// <summary>PrisonerNPC가 jail 진입 박을 entry trigger 위치. null 시 JailZone.transform 폴백.</summary>
        public Transform EntryAnchor => _entryAnchor != null ? _entryAnchor : transform;

        private ObjectPool<PrisonerNPC> _jailObjectPool;
        private Transform[] _gridSlots;
        private int _activeJailObjectCount;
        private int _currentCapacity;

        /// <summary>현재 활성 jail object 수가 capacity 도달 시 1회 발행. 게이트 5c GameManager.OnJailFull 구독 진입점.</summary>
        public event Action OnJailFull;

        /// <summary>IncreaseCapacity 직후 1회 발행. JailUpgradeZone capacity 변경 통지.</summary>
        public event Action OnCapacityChanged;

        public int RemainingCapacity => _currentCapacity - _activeJailObjectCount;
        public bool IsFull => _activeJailObjectCount >= _currentCapacity;
        public int CurrentCapacity => _currentCapacity;
        public int ActiveCount => _activeJailObjectCount;

        private void Awake()
        {
            if (_jailObjectPrefab == null)
            {
                Debug.LogError("[JailZone] _jailObjectPrefab not assigned. Drag Assets/Prefabs/Characters/Prisoner.prefab. Jail will no-op.", this);
                return;
            }
            if (_jailObjectPrefab.GetComponent<PrisonerNPC>() == null)
            {
                Debug.LogError("[JailZone] _jailObjectPrefab has no PrisonerNPC component on root. Jail will no-op.", this);
                return;
            }

            var settings = GameManager.Instance != null ? GameManager.Instance.Settings : null;
            if (settings == null)
            {
                Debug.LogError("[JailZone] GameManager.Settings null. Jail will no-op.", this);
                return;
            }

            int rows = settings.Jail.GridRows;
            int cols = settings.Jail.GridCols;
            int maxCap = settings.Jail.MaxCapacity;
            if (rows * cols < maxCap)
            {
                Debug.LogError($"[JailZone] _gridRows × _gridCols ({rows * cols}) < _maxCapacity ({maxCap}). Grid 부족 — Settings 정합 확인.", this);
                return;
            }

            _currentCapacity = settings.Jail.InitialCapacity;

            // Grid slot 동적 생성 — JailZone position 기준 중심 정렬.
            float spacing = settings.Jail.GridSpacing;
            float originX = -(cols - 1) * spacing * 0.5f;
            float originZ = -(rows - 1) * spacing * 0.5f;
            _gridSlots = new Transform[rows * cols];
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    var slot = new GameObject($"GridSlot_{r}_{c}").transform;
                    slot.SetParent(transform, worldPositionStays: false);
                    slot.localPosition = new Vector3(originX + c * spacing, 0f, originZ + r * spacing);
                    _gridSlots[r * cols + c] = slot;
                }
            }

            _jailObjectPool = new ObjectPool<PrisonerNPC>(
                createFunc: CreateJailObject,
                actionOnGet: OnGetJailObject,
                actionOnRelease: OnReleaseJailObject,
                actionOnDestroy: OnDestroyJailObject,
                collectionCheck: false,
                defaultCapacity: 20,
                maxSize: 100);

            // count billboard 초기화 — _cameraTransform fallback + 초기 텍스트 박음.
            if (_cameraTransform == null)
            {
                var mainCam = Camera.main;
                if (mainCam != null) _cameraTransform = mainCam.transform;
            }
            RefreshCountBillboard();
        }

        private void OnDestroy()
        {
            if (_jailObjectPool != null) { _jailObjectPool.Dispose(); _jailObjectPool = null; }
        }

        // --- 외부 노출 (PrisonerNPC.MovingToJail 도달 시 호출) ---

        /// <summary>다음 빈 grid slot에 jail object spawn. atomic 5단계 — IsFull 가드 / slotIndex 산출 / pool.Get + 위치 + 트윈 / count++ + OnJailFull / true.
        /// false 반환 시 호출자(PrisonerNPC) 책임으로 spawner.Release 즉시 호출 (sub-Q 6 옵션 a fallback).
        /// 호출자는 true 반환 시에도 본인 spawner.Release(this) 호출 — 두 풀 격리, jail object는 별개 instance.</summary>
        public bool TryConvertToJailObject(PrisonerNPC prisoner)
        {
            if (_jailObjectPool == null || _gridSlots == null) return false;

            // ① RemainingCapacity 가드.
            if (IsFull)
            {
                // 정상 race — cap 도달 후 in-flight prisoner (PrisonerSpawner spawn 가드는 이미 차단됐지만 alive prisoner들이 거래 + 이동 진행 중).
                // 호출자(PrisonerNPC) 측 spawner.Release fallback으로 회수. LogWarning 폐기 — console clean 보장 (3일차 게이트 5c 핫픽스).
                return false;
            }

            // ② 다음 빈 grid slot 인덱스 = _activeJailObjectCount.
            int slotIndex = _activeJailObjectCount;
            if (slotIndex < 0 || slotIndex >= _gridSlots.Length)
            {
                Debug.LogError($"[JailZone] slotIndex out of range: {slotIndex}. _activeJailObjectCount or _gridSlots desync.", this);
                return false;
            }
            var slot = _gridSlots[slotIndex];

            // ③ 풀 Get + position 설정 + scale-up 트윈.
            var jailObj = _jailObjectPool.Get();
            if (jailObj == null) return false;
            jailObj.transform.SetParent(slot, worldPositionStays: false);
            jailObj.transform.localPosition = Vector3.zero;
            jailObj.transform.localRotation = Quaternion.identity;

            var settings = GameManager.Instance.Settings;
            ScaleUpAsync(jailObj.transform, settings.Jail.ScaleUpDuration, this.destroyCancellationToken).Forget();

            // ④ count++ + IsFull 도달 시 OnJailFull 발행 (1회).
            _activeJailObjectCount++;
            if (_activeJailObjectCount == _currentCapacity)
            {
                OnJailFull?.Invoke();
            }

            // ⑤ count billboard 갱신.
            RefreshCountBillboard();

            return true;
        }

        /// <summary>JailUpgradeZone (게이트 5c) 호출. capacity ×CapacityMultiplier + MaxCapacity clamp + OnCapacityChanged 발행.</summary>
        public void IncreaseCapacity()
        {
            var settings = GameManager.Instance != null ? GameManager.Instance.Settings : null;
            if (settings == null) return;
            int next = _currentCapacity * settings.Jail.CapacityMultiplier;
            _currentCapacity = Mathf.Min(next, settings.Jail.MaxCapacity);
            OnCapacityChanged?.Invoke();
            RefreshCountBillboard();
        }

        private void RefreshCountBillboard()
        {
            if (_countText == null) return;
            _countText.text = $"{_activeJailObjectCount} / {_currentCapacity}";
        }

        private void LateUpdate()
        {
            if (_countBillboardRoot == null || !_countBillboardRoot.activeSelf) return;
            if (_cameraTransform == null) return;
            float yaw = _cameraTransform.eulerAngles.y;
            _countBillboardRoot.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        // --- 트윈 ---

        private async UniTaskVoid ScaleUpAsync(Transform t, float duration, CancellationToken ct)
        {
            if (t == null) return;
            Vector3 target = Vector3.one;
            t.localScale = Vector3.zero;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                ct.ThrowIfCancellationRequested();
                if (t == null) return;
                float p = elapsed / duration;
                t.localScale = Vector3.Lerp(Vector3.zero, target, p);
                elapsed += Time.deltaTime;
                await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate, ct);
            }
            if (t != null) t.localScale = target;
        }

        // --- ObjectPool 사이클 콜백 4개 (jail object 별도 풀, prisoner 풀과 격리) ---

        private PrisonerNPC CreateJailObject()
        {
            var go = Instantiate(_jailObjectPrefab, transform);
            var prisoner = go.GetComponent<PrisonerNPC>();
            if (prisoner == null) { Debug.LogError("[JailZone] Instantiated prefab has no PrisonerNPC component.", this); Destroy(go); return null; }

            // SpeechBubble 비활성 (jail object는 거래 X).
            foreach (var canvas in prisoner.GetComponentsInChildren<Canvas>(true)) canvas.gameObject.SetActive(false);

            // 미세 옵션 — Instantiate 직후 NavMeshAgent.enabled = false. prefab Awake "agent not on NavMesh" warning 회피.
            var agent = prisoner.GetComponent<NavMeshAgent>();
            if (agent != null) agent.enabled = false;

            return prisoner;
        }

        private void OnGetJailObject(PrisonerNPC jailObj)
        {
            if (jailObj == null) return;
            jailObj.gameObject.SetActive(true);

            // handoff §4 #26 — NavMeshAgent 비활성 (jail object는 정적). CreateJailObject에서 이미 비활성, 풀 재 Get 시 안전 위해 재 disable.
            var agent = jailObj.GetComponent<NavMeshAgent>();
            if (agent != null) agent.enabled = false;
        }

        private void OnReleaseJailObject(PrisonerNPC jailObj)
        {
            if (jailObj == null) return;
            jailObj.gameObject.SetActive(false);
            jailObj.transform.SetParent(transform, worldPositionStays: false);
            jailObj.transform.localScale = Vector3.one; // 다음 Get 시 ScaleUpAsync가 다시 0→1 박음.
            var agent = jailObj.GetComponent<NavMeshAgent>();
            if (agent != null) agent.enabled = true; // 안전 복원 (현 흐름엔 Release 발생 안 함).
        }

        private void OnDestroyJailObject(PrisonerNPC jailObj)
        {
            if (jailObj != null) Destroy(jailObj.gameObject);
        }
    }
}
