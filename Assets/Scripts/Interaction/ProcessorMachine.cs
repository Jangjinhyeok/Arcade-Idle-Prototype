using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Pool;

namespace PrisonLife
{
    /// <summary>
    /// Processor 부모 컨트롤러. 3일차 게이트 3 — 옛 ProcessorZone.cs 분할 결과 (InteractionZone 미상속, 일반 MonoBehaviour).
    /// _inputStack/_outputStack 보유 + UniTask 변환 루프 실행. 자식 ProcessorInputZone/OutputZone는 trigger 책임만 — 일괄 트윈은 부모 메서드(PushOresFromPlayer/PullHandcuffsToPlayer)에 위임.
    /// </summary>
    /// <remarks>
    /// 2일차 ProcessorZone 책임 분리(InteractionZone 상속 + 변환 루프 + transfer)에서 변환 루프만 본 클래스에 남김.
    /// transfer 책임은 자식 Zone 진입 1회 트리거 + 부모 Push/Pull 메서드로 이전.
    /// 차단 가드: head에 Handcuff 있으면 PushOres 거부, head에 Ore 있으면 PullHandcuffs 거부 — 혼재 회피 (게이트 2 Mine handcuff 가드와 대칭).
    /// 변환 루프 일시 정지: _outputStack.IsFull 시 매 frame yield로 폴링 대기 — 자리 생기면 자동 재개. 신규 PollIntervalSeconds 필드 미도입.
    /// 풀 자기 회수: spawn된 handcuff는 Desk → Prisoner 흐름에서 ReturnToPool 호출 시 _pool.Release로 회수.
    /// Capacity 단일 source: Awake에서 settings.Processor.InputCapacity/OutputCapacity로 코드 주입 (R3 정합). Inspector _capacity는 settings null 시 fallback.
    /// </remarks>
    public class ProcessorMachine : MonoBehaviour
    {
        [Tooltip("입력 StackContainer — ProcessorInputZone에서 전송되는 ore 적재. Awake에서 AcceptedType=Ore + Settings.Processor.InputCapacity 코드 주입.")]
        [SerializeField] private StackContainer _inputStack;

        [Tooltip("출력 StackContainer — 변환된 handcuff 적재. Awake에서 AcceptedType=Handcuff + Settings.Processor.OutputCapacity 코드 주입.")]
        [SerializeField] private StackContainer _outputStack;

        [Tooltip("Stackable 컴포넌트가 부착된 handcuff prefab (Mat_Silver). Assets/Prefabs/Pickups/Handcuff.prefab 드래그.")]
        [SerializeField] private GameObject _handcuffPrefab;

        private ObjectPool<Stackable> _pool;

        private void Awake()
        {
            if (_inputStack == null) Debug.LogError("[ProcessorMachine] _inputStack not assigned.", this);
            if (_outputStack == null) Debug.LogError("[ProcessorMachine] _outputStack not assigned.", this);
            if (_handcuffPrefab == null)
            {
                Debug.LogError("[ProcessorMachine] _handcuffPrefab not assigned. Pool init skipped — Processor will no-op.", this);
                return;
            }

            // AcceptedType 코드 주입 (2일차 패턴 그대로).
            if (_inputStack != null) _inputStack.AcceptedType = StackableType.Ore;
            if (_outputStack != null) _outputStack.AcceptedType = StackableType.Handcuff;

            // Capacity 코드 주입 — Settings 단일 source (R3 정합). null 시 Inspector 값 fallback.
            var settings = GameManager.Instance != null ? GameManager.Instance.Settings : null;
            if (settings != null)
            {
                if (_inputStack != null) _inputStack.SetCapacity(settings.Processor.InputCapacity);
                if (_outputStack != null) _outputStack.SetCapacity(settings.Processor.OutputCapacity);
            }

            _pool = new ObjectPool<Stackable>(
                createFunc: CreateHandcuff,
                actionOnGet: OnGetHandcuff,
                actionOnRelease: OnReleaseHandcuff,
                actionOnDestroy: OnDestroyHandcuff,
                collectionCheck: false,
                defaultCapacity: 10,
                maxSize: 50);

            ConvertLoopAsync(this.destroyCancellationToken).Forget();
        }

        private void OnDestroy()
        {
            if (_pool != null)
            {
                _pool.Dispose();
                _pool = null;
            }
        }

        // --- Public Push/Pull (자식 Zone 진입 1회 호출 진입점) ---

        /// <summary>ProcessorInputZone.OnPlayerEnter에서 1회 호출. player.Stack의 ore 전량을 _inputStack으로 일괄 트윈. handcuff 혼재 시 거부.</summary>
        public void PushOresFromPlayer(PlayerController player)
        {
            if (player == null || player.Stack == null) return;
            if (_inputStack == null || _inputStack.IsFull) return;
            if (player.Stack.IsEmpty) return;

            // 결정 4와 대칭 — head에 handcuff 1+ 있으면 거부 (혼재 회피).
            if (player.Stack.ContainsType(StackableType.Handcuff))
            {
                Debug.LogWarning("[ProcessorMachine] Player head contains Handcuff — push aborted. Empty handcuffs (deliver to Desk) first.", this);
                return;
            }

            int available = _inputStack.Capacity - _inputStack.Count;
            if (available <= 0) return;

            int popCount = Mathf.Min(player.Stack.Count, available);
            var ores = player.Stack.RemoveRange(popCount);
            int added = _inputStack.AddBatch(ores);

            // 거부분(반환값 이후 인덱스) — capacity 또는 type race로 떨어진 인스턴스 풀 회수.
            for (int i = added; i < ores.Count; i++)
            {
                if (ores[i] != null) ores[i].ReturnToPool();
            }
        }

        /// <summary>ProcessorOutputZone.OnPlayerEnter에서 1회 호출. _outputStack의 handcuff 전량을 player.Stack으로 일괄 트윈. ore 혼재 시 거부 (head 잔여 capacity만큼만).</summary>
        public void PullHandcuffsToPlayer(PlayerController player)
        {
            if (player == null || player.Stack == null) return;
            if (_outputStack == null || _outputStack.IsEmpty) return;
            if (player.Stack.IsFull) return;

            // 결정 4 — head에 ore 1+ 있으면 거부 (혼재 회피, Mine handcuff 가드와 대칭).
            if (player.Stack.ContainsType(StackableType.Ore))
            {
                Debug.LogWarning("[ProcessorMachine] Player head contains Ore — handcuff pull aborted. Empty ores into Processor first.", this);
                return;
            }

            int available = player.Stack.Capacity - player.Stack.Count;
            if (available <= 0) return;

            int popCount = Mathf.Min(_outputStack.Count, available);
            var handcuffs = _outputStack.RemoveRange(popCount);
            int added = player.Stack.AddBatch(handcuffs);

            for (int i = added; i < handcuffs.Count; i++)
            {
                if (handcuffs[i] != null) handcuffs[i].ReturnToPool();
            }
        }

        /// <summary>MinerNPC.DepositOre에서 호출. 단일 ore를 _inputStack에 직접 Add. capacity 초과 또는 type 미일치 시 ReturnToPool. 3일차 게이트 5d Sub-Q 1.</summary>
        public void PushOreFromMiner(Stackable ore)
        {
            if (ore == null) return;
            if (_inputStack == null) { ore.ReturnToPool(); return; }
            if (_inputStack.IsFull) { ore.ReturnToPool(); return; }
            int slot = _inputStack.Add(ore);
            if (slot < 0) ore.ReturnToPool();
        }

        /// <summary>DeskNPC.AtOutput 분기에서 호출. _outputStack에서 max개까지 일괄 Pop. 빈 stack / max ≤ 0 시 false. 4일차 Desk + DeskNPC 시스템.
        /// 호출자(DeskNPC)가 받은 handcuffs를 자체 _stack.AddBatch로 carry — 거부분(AddBatch 반환값 이후)은 호출자 책임으로 ReturnToPool.</summary>
        public bool TryRequestHandcuffsForDeskNPC(int max, out List<Stackable> handcuffs)
        {
            handcuffs = null;
            if (_outputStack == null || _outputStack.IsEmpty || max <= 0) return false;
            int popCount = Mathf.Min(_outputStack.Count, max);
            handcuffs = _outputStack.RemoveRange(popCount);
            return handcuffs != null && handcuffs.Count > 0;
        }

        // --- ObjectPool 사이클 콜백 4개 (2일차 ProcessorZone 패턴 그대로) ---

        private Stackable CreateHandcuff()
        {
            var go = Instantiate(_handcuffPrefab, transform);
            var stackable = go.GetComponent<Stackable>();
            if (stackable == null)
            {
                Debug.LogError("[ProcessorMachine] _handcuffPrefab has no Stackable component on root. Pool will return null.", this);
                Destroy(go);
                return null;
            }
            return stackable;
        }

        private void OnGetHandcuff(Stackable hc)
        {
            if (hc == null) return;
            hc.gameObject.SetActive(true);
            hc.transform.SetParent(transform, worldPositionStays: false);
            hc.transform.localPosition = Vector3.zero; // _outputStack.Add 직후 슬롯 위치로 트윈.
            hc.OriginPool = _pool;
        }

        private void OnReleaseHandcuff(Stackable hc)
        {
            if (hc == null) return;
            if (this == null) return; // Play stop 시 ProcessorMachine 먼저 Destroy 박힌 case (DeskNPC.OnDisable cleanup race).
            hc.gameObject.SetActive(false);
            hc.transform.SetParent(transform, worldPositionStays: false);
        }

        private void OnDestroyHandcuff(Stackable hc)
        {
            if (hc != null) Destroy(hc.gameObject);
        }

        // --- UniTask 변환 루프 (2일차 ProcessorZone.ConvertLoopAsync 동일 로직) ---

        private async UniTaskVoid ConvertLoopAsync(CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (this == null || _inputStack == null || _outputStack == null) return;

                var settings = GameManager.Instance != null ? GameManager.Instance.Settings : null;
                if (settings == null)
                {
                    await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate, ct);
                    continue;
                }

                int oreNeeded = settings.Processor.OreConsumedPerHandcuff;
                if (oreNeeded < 1)
                {
                    Debug.LogWarning("[ProcessorMachine] OreConsumedPerHandcuff < 1, clamping to 1.", this);
                    oreNeeded = 1;
                }

                if (_inputStack.Count >= oreNeeded && !_outputStack.IsFull && _pool != null)
                {
                    // 입력 광석 소비 (즉시).
                    for (int i = 0; i < oreNeeded; i++)
                    {
                        var consumed = _inputStack.Remove();
                        if (consumed != null) consumed.ReturnToPool();
                    }

                    // 변환 시간 대기.
                    await UniTask.Delay(
                        TimeSpan.FromSeconds(settings.Processor.TickInterval),
                        DelayType.DeltaTime,
                        PlayerLoopTiming.LastPostLateUpdate,
                        ct);

                    // Delay 후 재가드 (이 사이에 Destroy 가능).
                    if (this == null || _outputStack == null || _pool == null) return;

                    // 수갑 spawn → 출력 적재.
                    var hc = _pool.Get();
                    if (hc == null) continue;
                    int slot = _outputStack.Add(hc);
                    if (slot < 0) hc.ReturnToPool(); // 출력 IsFull 동시 race — 풀 회수.
                }
                else
                {
                    // 조건 미충족 (input 부족 또는 output 가득) — 매 frame 폴링 대기.
                    await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate, ct);
                }
            }
        }
    }
}
