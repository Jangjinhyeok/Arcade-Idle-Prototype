using UnityEngine;
using UnityEngine.Pool;

namespace PrisonLife
{
    /// <summary>
    /// Money block 누적 + Player BackStack 일괄 픽업 사이트. 3일차 게이트 4 — 옛 MoneySpawner.cs 폐기 + 신규 작성.
    /// PrisonerNPC.TryAcquireHandcuff에서 SpawnMoney(RequestQuantity) 호출 → block N개 spawn (visual cap 8 도달 시 data만 누적).
    /// Player 영역 진입 1회 트리거로 _pileStack 전량 → player.BackStack 일괄 트윈 + GameManager.AddMoney(blockCount * MoneyPerBlock) 호출.
    /// </summary>
    /// <remarks>
    /// _pileStack: capacity Inspector 박음 (4일차 visualCap 폐기 — capacity가 시각·기능 max 통일).
    /// 트윈 패턴: 옛 MoneySpawner의 단일 트윈 → 게이트 4 StackContainer.AddBatch 일괄 트윈으로 단순화.
    /// 결정 4 — AddMoney 호출은 트윈 시작 직전 1회 (전량). Money UI 갱신과 visual transfer 약간 lag 허용.
    /// 풀 자기 회수: SpawnMoney에서 발급된 money block은 OnPlayerEnter 일괄 픽업 시 player.BackStack으로 SetParent — BackStack에서 추후 Pop·UpgradeZone 결제(4일차) 시 ReturnToPool 호출 시 _pool.Release로 자기 회수.
    /// </remarks>
    public class MoneyPileZone : InteractionZone
    {
        [Tooltip("Stackable 컴포넌트가 부착된 Money block prefab (Mat_Green). Assets/Prefabs/Pickups/Money.prefab 드래그.")]
        [SerializeField] private GameObject _moneyPrefab;

        [Tooltip("Money block 누적 StackContainer — 자식 \"MoneyPileStack\". Inspector capacity 박음 (4일차 visualCap 폐기 후 capacity 통일). Awake에서 AcceptedType=Money 코드 주입.")]
        [SerializeField] private StackContainer _pileStack;

        private ObjectPool<Stackable> _pool;

        private void Awake()
        {
            if (_moneyPrefab == null)
            {
                Debug.LogError("[MoneyPileZone] _moneyPrefab not assigned. Pool init skipped — Pile will no-op.", this);
                return;
            }
            if (_pileStack == null)
            {
                Debug.LogError("[MoneyPileZone] _pileStack not assigned. Pile will no-op.", this);
                return;
            }

            // AcceptedType 코드 주입.
            _pileStack.AcceptedType = StackableType.Money;

            _pool = new ObjectPool<Stackable>(
                createFunc: CreateMoney,
                actionOnGet: OnGetMoney,
                actionOnRelease: OnReleaseMoney,
                actionOnDestroy: OnDestroyMoney,
                collectionCheck: false,
                defaultCapacity: 10,
                maxSize: 50);
        }

        private void OnDestroy()
        {
            if (_pool != null)
            {
                _pool.Dispose();
                _pool = null;
            }
        }

        // --- 외부 노출 (PrisonerNPC.TryAcquireHandcuff 호출 진입점) ---

        /// <summary>count만큼 money block을 _pileStack에 spawn. visual cap 초과는 StackContainer.Add가 자동으로 SetActive(false) 처리.</summary>
        public void SpawnMoney(int count)
        {
            if (_pool == null || _pileStack == null) return;
            if (count <= 0) return;

            for (int i = 0; i < count; i++)
            {
                var money = _pool.Get();
                if (money == null) break;
                int slot = _pileStack.Add(money);
                if (slot < 0) money.ReturnToPool(); // capacity (int.MaxValue) 초과 또는 type 미일치 — 사실상 발생 안 함.
            }
        }

        // --- 자식 트리거 (Player 영역 진입 1회 픽업) ---

        protected override void OnPlayerEnter(PlayerController player)
        {
            if (_pileStack == null || _pileStack.IsEmpty) return;
            if (player == null || player.BackStack == null) return;
            if (player.BackStack.IsFull) return; // BackStack capacity = int.MaxValue라 사실상 false. 안전 가드.

            int available = player.BackStack.Capacity - player.BackStack.Count;
            if (available <= 0) return;

            int blockCount = Mathf.Min(_pileStack.Count, available);
            var blocks = _pileStack.RemoveRange(blockCount);
            int added = player.BackStack.AddBatch(blocks);

            // 결정 4 — 트윈 시작 직전 1회 AddMoney (전량). Money UI 즉시 갱신.
            var settings = GameManager.Instance != null ? GameManager.Instance.Settings : null;
            int moneyPerBlock = (settings != null) ? settings.MoneyPile.MoneyPerBlock : 10;
            if (added > 0) GameManager.Instance?.AddMoney(added * moneyPerBlock);

            for (int i = added; i < blocks.Count; i++)
            {
                if (blocks[i] != null) blocks[i].ReturnToPool();
            }
        }

        // --- abstract 3개 stub (누적 미사용, 진입 트리거 패턴) ---

        protected override void OnAccumulatorTick(IInteractionUser user, float accumulated, float deltaTime) { }
        protected override bool IsAccumulatorComplete(IInteractionUser user, float accumulated) => false;
        protected override void OnInteractionComplete(IInteractionUser user) { }

        // --- ObjectPool 사이클 콜백 4개 (옛 MoneySpawner 패턴 복제) ---

        private Stackable CreateMoney()
        {
            var go = Instantiate(_moneyPrefab, transform);
            var stackable = go.GetComponent<Stackable>();
            if (stackable == null)
            {
                Debug.LogError("[MoneyPileZone] _moneyPrefab has no Stackable component on root. Pool will return null.", this);
                Destroy(go);
                return null;
            }
            return stackable;
        }

        private void OnGetMoney(Stackable money)
        {
            if (money == null) return;
            money.gameObject.SetActive(true);
            money.transform.SetParent(transform, worldPositionStays: false);
            money.transform.localPosition = Vector3.zero;
            money.OriginPool = _pool;
        }

        private void OnReleaseMoney(Stackable money)
        {
            if (money == null) return;
            money.gameObject.SetActive(false);
            money.transform.SetParent(transform, worldPositionStays: false);
        }

        private void OnDestroyMoney(Stackable money)
        {
            if (money != null) Destroy(money.gameObject);
        }
    }
}
