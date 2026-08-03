# ADR-001: Core Architecture (InteractionZone / StackContainer / GameSettingsSO)

| Field | Value |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-04-28 |
| **Deciders** | 장진혁 (solo) |
| **Engine** | Unity 2022.3.62f2 LTS |
| **Supersedes** | (none) |

---

## 1. Context

본 프로젝트는 5일(유효 작업 시간 약 30시간) 안에 Mine / Processor / Desk / Upgrade / Hire를 포함한 9개 MUST 시스템을 완성해야 한다. 각 시스템을 독립적으로 구현하면 코드량이 5배 이상 증가하고 일정을 초과할 위험이 높다. 이를 방지하기 위해 코드 재사용을 극대화하는 핵심 추상화 3개(InteractionZone, StackContainer, GameSettingsSO)를 1일차에 설계하고, 이후 모든 시스템이 이 세 추상화를 기반으로 조립되도록 아키텍처를 고정한다. 이 결정은 본 프로젝트의 락된 결정 §6과 직접 연결된다.

---

## 2. Decision

### 2.1 InteractionZone 추상화

모든 상호작용 구역(Mine, Processor, Desk, Upgrade, Hire)은 동일한 trigger-stay 누적 패턴을 공유한다. 플레이어(또는 NPC)가 Trigger Collider 안에 머무는 동안 타이머가 누적되고, 임계값 도달 시 콜백이 발생한다. 이 공통 로직을 추상 베이스 클래스 한 곳에 집중한다.

**클래스 시그니처:**

```csharp
public abstract class InteractionZone : MonoBehaviour
{
    // --- 공통 상태 ---
    protected float _accumulatorTimer;
    protected bool _isPlayerInside;
    protected bool _isAgentInside;

    // --- Unity 메시지 (내부 진입/퇴장 분기) ---
    private void OnTriggerEnter(Collider other);
    private void OnTriggerStay(Collider other);
    private void OnTriggerExit(Collider other);

    // --- 플레이어 후크 ---
    protected virtual void OnPlayerEnter(PlayerController player);
    protected virtual void OnPlayerStay(PlayerController player, float deltaTime);
    protected virtual void OnPlayerExit(PlayerController player);

    // --- NPC 후크 ---
    protected virtual void OnAgentEnter(NPCBase agent);
    protected virtual void OnAgentStay(NPCBase agent, float deltaTime);
    protected virtual void OnAgentExit(NPCBase agent);

    // --- 누적기 ---
    protected abstract void OnAccumulatorTick(float deltaTime);
    protected abstract bool IsAccumulatorComplete();
    protected abstract void OnInteractionComplete();

    // --- 유틸리티 ---
    protected void ResetAccumulator();
    public bool IsOccupied { get; }
}
```

`OnTriggerEnter/Stay/Exit`는 `private` 구현부에서 `Collider`의 컴포넌트 태그(또는 `GetComponent<PlayerController>()` / `GetComponent<NPCBase>()`)로 분기하여 플레이어 후크와 NPC 후크를 각각 호출한다. 파생 클래스는 Unity 메시지를 직접 오버라이드하지 않고 후크 메서드만 오버라이드한다.

**공통 로직 vs 파생 후크:**

| 계층 | 담당 로직 |
|------|----------|
| 베이스 | 타이머 누적, `IsAccumulatorComplete` 폴링, `OnInteractionComplete` 호출, `ResetAccumulator` |
| 파생 | `OnAccumulatorTick` 구현(게이지 UI 갱신 등), `OnInteractionComplete` 구현(아이템 이동, 재화 소모 등), `OnPlayerEnter/Exit` 오버라이드(필요 시) |

**5개 파생 클래스 책임:**

| 파생 클래스 | 책임 |
|---|---|
| `MineZone` | 광석을 `ObjectPool`에서 꺼내 Player `StackContainer`에 추가. 스택 풀이면 누적 중단. |
| `ProcessorZone` | Player `StackContainer`에서 광석 소모 → 가공품 생성 → Player 또는 출력 `StackContainer`에 적재. |
| `DeskZone` | Player `StackContainer`의 가공품을 Desk `StackContainer`로 이전. 가공품 적재 시 고객 NPC spawn 트리거. |
| `UpgradeZone` | 플레이어 보유 재화 소모 → `GameSettingsSO` 파라미터 갱신(채굴 속도·스택 한도). 게이지 UI 표시. |
| `HireZone` | 플레이어 보유 재화 소모 → Worker NPC를 `ObjectPool`에서 꺼내 Mine-Processor 왕복 경로 할당. |

> **2일차 정정 메모 (2026-04-30):** `OnAccumulatorTick` / `IsAccumulatorComplete` / `OnInteractionComplete` 시그니처에 `IInteractionUser user` 인자 추가됨. 사유: user별 stack 분기 캡슐화 — 현재 시그니처는 user 정보 없으면 `_accumulators` Dictionary 역참조 필요(베이스 캡슐화 깨짐). 3일차 Worker 도입 시 `user is Worker` 분기 자연스러움. 정식 supersede ADR은 3일차 폴리시 단계로 디퍼.

---

### 2.2 StackContainer 재사용 전략

광석·가공품·재화는 모두 동일한 적재 컴포넌트로 처리된다. Player 등, Desk 상단, Processor 입출력 네 사이트 모두 동일한 `StackContainer` 컴포넌트를 사용한다.

**컴포넌트 시그니처:**

```csharp
public class StackContainer : MonoBehaviour
{
    public int Capacity { get; }
    public int Count { get; }
    public bool IsFull { get; }
    public bool IsEmpty { get; }

    public event Action OnFull;
    public event Action OnEmpty;
    public event Action<int> OnCountChanged;

    public int Add(Stackable item);
    public bool TryRemove(out Stackable item);
    public bool TryPeek(out Stackable item);
    public void Clear();
}
```

**등 뒤 적재 위치 보간 로직:**

각 슬롯의 로컬 오프셋은 `stackIndex * stackOffset` 벡터로 계산한다. 아이템이 Add될 때 UniTask + CancellationToken을 통해 현재 월드 위치에서 목표 슬롯 위치까지 이동시키며, `Mathf.Sin(t * Mathf.PI) * arcHeight`를 Y축에 더해 포물선 궤적을 구현한다. 매 프레임 `Vector3.Lerp`로 이미 적재된 아이템들의 로컬 위치를 목표 오프셋으로 보간하여 스택이 흔들리는 시각 효과를 제공한다.

**사이트 별 차이점:**

- **Player back**: 컨테이너가 이동하므로 슬롯 오프셋은 로컬 좌표 기준. 스택 한도 도달 시 World Space "MAX" TextMeshPro 활성화. `Capacity`는 `GameSettingsSO`에서 읽음.
- **Desk top**: 정적 격자 배치. 슬롯 오프셋이 2D 격자(row × col) 형태로 계산됨. 이동 보간 없이 즉시 배치해도 무방.
- **Processor I/O**: 입력(광석)과 출력(가공품) 두 개의 독립적인 `StackContainer` 인스턴스를 가짐. 두 인스턴스 모두 동일 컴포넌트이며 Inspector에서 분리 참조.

**`Stackable` 마커 컴포넌트:**

```csharp
public class Stackable : MonoBehaviour
{
    public StackableType Type; // enum: Ore, Handcuff, Money
}
```

어떤 `GameObject`가 스택에 들어갈 수 있는지 식별하는 마커 역할을 한다. 시각적 Mesh(`MeshRenderer`, `MeshFilter`)와 논리 데이터(타입 식별)를 분리하여 `StackContainer`가 시각 표현에 의존하지 않도록 한다.

---

### 2.3 GameSettingsSO 구조

모든 밸런스 수치는 단일 `ScriptableObject` 인스턴스에 집중한다. 하드코딩된 수치는 금지한다.

**필드 그룹 및 목록:**

```
[Header("Player")]
playerMoveSpeed          : float   (units/sec)
playerStackCapacity      : int     (개)

[Header("Mine")]
mineTickInterval         : float   (sec/tick)
orePerTick               : int     (개/tick)

[Header("Processor")]
processorTickInterval    : float   (sec/tick)
oreConsumedPerHandcuff   : int     (개)

[Header("Desk")]
deskTransferTickInterval : float   (sec/tick)
deskStackCapacity        : int     (개)

[Header("NPC")]
prisonerMoveSpeed        : float   (units/sec)
workerMoveSpeed          : float   (units/sec)

[Header("Upgrade")]
drillUpgradeCost         : int     (Money)
drillSpeedMultiplier     : float   (배율)
drillStackBonus          : int     (개)
hireCost                 : int     (Money)

[Header("UI")]
maxTextShowDelay         : float   (sec)
moneyFloatArcHeight      : float   (units)
```

인스펙터 `[Tooltip]` 원칙: Inspector에서 바로 읽을 수 있도록 **한글로 필드 설명을 기재**한다. 예: `[Tooltip("플레이어 이동 속도 (units/sec). 기본값은 3일차 튜닝 시 결정.")]`.

**런타임 접근 패턴:** `GameManager`가 `[SerializeField] private GameSettingsSO _settings`로 직접 참조를 보유하고, 다른 컴포넌트는 `GameManager.Instance.Settings` 프로퍼티를 통해 접근한다. `Resources.Load`는 경로 하드코딩과 빌드 누락 위험이 있어 배제한다. 싱글톤은 `GameManager` 1개에 한정하며 다른 시스템에서는 `[SerializeField]`로 직접 주입한다.

---

### 2.4 Namespace 정책 (1일차 시작 시점 결정)

모든 게임 코드는 단일 flat `namespace PrisonLife` 래퍼로 일관 통합한다. UniTask(`Cysharp.Threading.Tasks`)·TMPro·UnityEngine 등 외부 namespace와 분리 명확성 확보. nested(`PrisonLife.Stack` 등) 분할은 cross-namespace `using` 비용이 시그널 대비 크다고 판단하여 채택하지 않는다.

---

## 3. Consequences

**긍정적 결과:**

- `InteractionZone` 베이스 클래스 1회 작성으로 Mine / Processor / Desk / Upgrade / Hire 5개 시스템 구현 시간을 단축한다. trigger-stay 타이머·게이지·콜백 로직의 중복 제거로 버그 발생 지점도 1곳으로 집중된다.
- `StackContainer` 1개 컴포넌트를 Player, Desk, Processor 총 4개 사이트에 재사용하여 스택 시각화·이벤트 코드를 단일 구현으로 유지한다.
- `GameSettingsSO` 단일 진입점으로 인해 밸런스 튜닝이 Inspector 하나만 열면 끝난다. 3일차 수치 조정 시 소스 코드 수정이 불필요하다.
- `InteractionZone`을 상속하는 새로운 Zone(예: 보관소 확장 Zone)을 추가할 때 베이스 수정 없이 파생 클래스만 작성하면 된다.

**부정적 결과 / 제약:**

- trigger-stay 의존도 증가로 인해 Physics 타이밍 버그(예: `OnTriggerStay` 호출 누락) 발생 시 5개 시스템이 동시에 영향을 받아 원인 추적이 어려울 수 있다.
- 베이스 클래스 인터페이스를 변경해야 할 경우(예: `OnAccumulatorTick` 시그니처 변경) 5개 파생 클래스를 동시에 수정해야 한다.
- `StackContainer`가 Desk의 격자 배치와 Player의 선형 배치를 모두 지원해야 하므로 슬롯 오프셋 계산 로직에 조건 분기가 추가된다.

**회피된 대안:**

각 시스템(Mine, Processor, Desk, Upgrade, Hire)별 독립 구현 — 코드량 5배 이상, trigger 타이머 로직 5개 중복, 5일 일정 초과 위험이 명백하여 채택하지 않음.

---

## 4. Compliance

### 락된 결정 9개 매핑표

| 락된 결정 | 본 ADR 매핑 | 비고 |
|---|---|---|
| Player movement (`Rigidbody.MovePosition`, Kinematic) | 직접 무관 | `PlayerController` 구현 시 적용. ADR 범위 밖 |
| Camera (핸드 롤 follow 스크립트) | 직접 무관 | `CameraFollow` 단일 컴포넌트. ADR 범위 밖 |
| Object pooling (`UnityEngine.Pool.ObjectPool<T>`) | §2.1 MineZone·HireZone에서 광석·NPC 풀 참조 | 2022.3 안정 지원 API |
| NPC pathing (`NavMeshAgent` + 빌트인 NavMesh) | §2.1 `NPCBase`가 `NavMeshAgent`를 래핑, InteractionZone 후크와 연동 | 2022.3 안정 지원 API |
| Balance data (단일 `GameSettingsSO`) | §2.3 전체가 이 결정의 구체화 | `ScriptableObject` 2022.3 안정 지원 |
| UI scaling (CanvasScaler, 720x1280, Match=0.5) | 직접 무관 | UI 컴포넌트 설정 시 적용. ADR 범위 밖 |
| Reusable abstraction (`InteractionZone` 베이스) | §2.1 전체가 이 결정의 구체화 | 본 ADR의 핵심 |
| Stack system (`StackContainer` 다중 사이트 재사용) | §2.2 전체가 이 결정의 구체화 | 본 ADR의 핵심 |
| Tweening (`Vector3.Lerp` + `Mathf.Sin` 포물선) | §2.2 등 뒤 적재 보간 로직에 적용 | 3rd-party 트위닝 라이브러리 미사용 |
| Audio (Cut) | 직접 무관 | 본 프로젝트 범위 외 |

> **각주:** `GameManager` 싱글톤은 본 프로젝트 한정 예외이며 락된 결정 §6의 "단일 `GameSettingsSO` ScriptableObject" 결정과 충돌하지 않음 (SO 자체는 단일, 접근 경로만 싱글톤 경유).

### Unity 2022.3.62f2 API 사용 확인

본 ADR이 참조하는 모든 API는 Unity 2022.3 LTS에서 안정적으로 지원된다:

- `Rigidbody.MovePosition` — 2022.3 안정
- `UnityEngine.Pool.ObjectPool<T>` — 2021.1 도입, 2022.3 안정
- `UnityEngine.AI.NavMeshAgent` — 구버전부터 안정
- `ScriptableObject` — 구버전부터 안정
- `MonoBehaviour.OnTriggerEnter/Stay/Exit` — 구버전부터 안정
- `Vector3.Lerp`, `Mathf.Sin`, `Mathf.SmoothDamp` — 구버전부터 안정
- `TextMeshPro` — `com.unity.textmeshpro` 패키지, 2022.3 지원

`Awaitable` (Unity 2023.1+), UI Toolkit 런타임 개선 (2023+) 등 2022.3 미지원 API는 본 ADR에서 참조하지 않는다.

---

## 5. Open Questions

1일차 구현 시작 전 아래 항목을 확정해야 한다.

**Q1. NPC의 InteractionZone 누적 타이머 공유 여부**
Worker NPC가 MineZone에 진입할 때 플레이어와 동일한 `_accumulatorTimer`를 공유하면 동시 채굴 시 타이머가 빠르게 차오르고, 분리하면 각자 독립적으로 진행된다. 어느 쪽이 의도된 게임플레이인지 결정이 필요하다.
→ **Resolution (2026-04-29):** 분리. `Dictionary<IInteractionUser, float>` 형태로 사용자별 누적 타이머를 별도 보관. Zone은 단일이되 Player/NPC 진행도 독립.

**Q2. `Stackable`의 타입 정보 범위**
`Stackable` 마커가 빈 컴포넌트(`MonoBehaviour`만 상속)이면 최소화되지만, `StackContainer`가 타입을 검증(예: DeskZone은 Handcuff만 받음)하려면 `StackableType` enum 필드가 필요하다. 타입 검증이 런타임에 필요한지 결정이 필요하다.
→ **Resolution (2026-04-29):** `StackableType` enum(`Ore`, `Handcuff`, `Money`) 보유. Processor 입출력 필터링과 향후 자원 확장성 확보.

**Q3. `GameSettingsSO` 런타임 접근 경로의 최종 확인**
§2.3에서 `GameManager.[SerializeField]` 주입을 채택했으나, Worker NPC 및 각 Zone이 `GameManager.Instance`에 의존하는 형태가 허용되는지(싱글톤 1개 예외 범위 내인지) 확인이 필요하다. 대안으로 각 Zone에 `[SerializeField] private GameSettingsSO _settings`를 직접 주입하는 방식도 가능하다.
→ **Resolution (2026-04-29):** `GameManager.Instance.Settings` 싱글톤 예외 채택. `GameManager` MonoBehaviour 하나만 싱글톤이고, 모든 시스템은 `GameManager.Instance.Settings` 경유. Editor 시점 데이터 표시 등 한정된 경우만 `[SerializeField]` SO 직접 참조 허용.

---

## Resolutions (2026-04-29)

§5 Open Questions의 3개 결정을 본 섹션에 통합 정리한다. 본 결정은 1일차 구현부터 적용된다.

**R1 — InteractionZone 누적 타이머: 분리**
- 베이스 클래스에 `Dictionary<IInteractionUser, float> _accumulators` 보유.
- `IInteractionUser`는 `PlayerController`와 `NPCBase`가 구현하는 마커 인터페이스.
- Player와 NPC가 동일 Zone에 동시 진입해도 각자 독립적인 진행도를 가짐. 동시 채굴 시 합산 가속 없음.
- 영향: §2.1 베이스 시그니처에 `IInteractionUser` 인터페이스 추가, `_accumulatorTimer` 단일 필드는 Dictionary 기반으로 대체. 1일차 구현 시 반영.

**R2 — `Stackable`은 `StackableType` enum 보유**
- `enum StackableType { Ore, Handcuff, Money }`.
- `StackContainer` 또는 파생 Zone(예: `DeskZone`)이 입력 타입을 검증할 수 있음.
- 향후 자원 추가(예: 불도저 NICE 항목) 시 enum 확장만으로 대응.
- 영향: §2.2 `Stackable` 시그니처 확정. 별도 추상화 추가 없음.

> **2일차 확장 메모 (2026-04-30):** `Stackable`에 `IObjectPool<Stackable> OriginPool { get; set; }` 프로퍼티 + `ReturnToPool()` 메서드 추가됨. ObjectPool 사이클 지원 — 마커 컴포넌트 정의(R2)에서 약간 확장. `createFunc`로 발급한 인스턴스가 자기 풀로 회수 가능(Mine·Processor·MoneySpawner 3개 풀이 동일 시그니처 활용). 4일차 cleanup 후보 — 정식 supersede ADR 검토.

**R3 — `GameSettingsSO` 접근 경로: `GameManager.Instance.Settings` 싱글톤 예외**
- `GameManager` MonoBehaviour 1개에 한해 싱글톤 패턴 허용.
- 모든 런타임 시스템(Zone, NPC, UI)은 `GameManager.Instance.Settings`로 접근.
- Editor 도구 또는 한정된 디버그 시각화에 한해 `[SerializeField] private GameSettingsSO _settings` 직접 주입 허용.
- 영향: §2.3에서 채택한 `[SerializeField]` 주입 방식보다 싱글톤 경유로 일원화. 본 프로젝트 "싱글톤 남발 금지" 규칙의 명시적 예외 1건으로 처리.

**R4 — Money 보유 및 변경 통지: `GameManager` 직접 보유, `WalletController` 미생성**
- `GameManager`가 `Money` 필드를 직접 보유하고 `event Action<int> OnMoneyChanged`를 발행. Money UI(`MoneyHUD`)는 이 이벤트를 구독하여 갱신.
- 사유: 재화는 단일 시스템(Money 1개 변수)이며 별도 컴포넌트 추상화의 가치보다 중계 비용이 큼. 추가 재화 도입 시점에 `WalletController`로 분리하는 것을 검토(현재 단계 기준 미생성).
- 영향: ADR-002 §2 `Scripts/Economy/`의 `WalletController.cs` 항목은 미생성으로 확정. ADR-001 §2 본문은 본 ADR에서 다루는 추상화 외 deferred 결정이므로 수정하지 않음.

**R5 (2026-04-29) — 비동기 처리: UniTask 채택, Coroutine 미사용**
- 비동기 처리는 UniTask v2.5.10 채택. ADR-003 참조.
- R1의 `Dictionary<IInteractionUser, float>` 누적 타이머는 그대로 유지하되, Processor 변환 시간·Stack 위치 보간·Money 흡수 트윈 등 시간 기반 비동기 흐름은 UniTask + `destroyCancellationToken` 사용. Coroutine 사용 안 함.
- 영향: §2.2 등 뒤 적재 보간 본문 한 줄 표현 교체("코루틴을 통해" → "UniTask + CancellationToken을 통해"). §2.1 InteractionZone 베이스 시그니처, §2.3 GameSettingsSO, §3 Consequences, §4 Compliance 표는 무영향.

**R6 (2026-05-01) — 3일차 게이트 5 supersede 누적**

게이트 5a~5d 진행 중 박힌 결정 변경/신규/핫픽스 7건을 본 섹션에 통합 박음. 정식 별도 supersede ADR은 후속 작업으로 디퍼.

| # | 정정 항목 | 게이트 | 결과 |
|---|---|---|---|
| 1 | 결정 #8 supersede | 5a | StackContainer site 4 → 7. BackStack(Player money 전용) + ProcessorInput/Output + DeskStack + MoneyPileStack 추가. visualCap 분리 + AddBatch/RemoveRange/ContainsType 메서드 신규. |
| 2 | 결정 #14 supersede | 5a | PrisonerSpawner 시간 기반 spawn (2초 interval, maxConcurrent 3) + DeskZone.OnHandcuffStocked 이벤트 폐기. Update 폴링 + Queue 시스템 (DeskZone List<PrisonerNPC>) 채택. |
| 3 | 결정 #17 supersede | 5d | Mine 6슬롯 cooldown respawn → 8x8 grid + OreNode 동적 생성. MineZone이 grid 관리자 (ObjectPool 단일, 64 OreNode 공유) + Tractor 1×4 호출. |
| 4 | 결정 #20 보강 | 5a/5b | PrisonerNPC FSM 4상태 → 6상태 (기존 7상태 lock supersede). Spawning / MovingToQueueSlot / MovingToDesk / WaitingAtDesk / MovingToJail / EnteringJail. |
| 5 | 결정 #22~#34 신규 | 5a~5d | 5a Queue + spawn 가드(maxConcurrent + jail cap + queue 가드 3단) / 5b JailZone grid + scale-up 트윈 / 5c UpgradeZone 베이스 + 4 파생 + GameManager progression(IsFirstMoneyEarned/IsDrillUpgraded/IsTractorUpgraded/IsJailFull/IsJailUpgraded) / 5d OreNode + Tractor 1×4 + MinerNPC + MinerSpawner. |
| 6 | 5d 핫픽스 누적 | 5d | MinerNPC trigger 의존 폐기 + ore 직접 트윈 연출 (OreNode 위치 → Processor InputStack 포물선 0.25s) + FSM 5→3상태 단순화 (MovingToProcessor/DepositOre 폐기). `_carriedOre` / `_headAnchor` 필드 폐기. ARRIVAL_DISTANCE 0.5 → 0.1 + stoppingDistance 0 + OreNode collider size (spacing×1.2, 2, spacing×1.2). |
| 7 | Material 9 → 10 | 5d | Mat_Blue 신규 추가 (Miner.prefab body 차별화, Sub-Q 7-A). 시각적 Prisoner(Mat_Orange)/Miner(Mat_Blue) 구분. |
| 8 | 게이트 6.5 신설 — 시각 polish | 6.5 | (1) CameraDirector 신규 (cinematic 3건 — OnFirstMoneyEarned / OnDrillUpgraded / OnJailFull, MOVE/HOLD/RETURN 각 1.0s). (2) Animator Controller 신규 (Idle/Walk/Mining + IsMoving/IsMining + Any State 패턴, Has Exit Time false + Loop Time ✓). (3) PlayerController/PrisonerNPC/MinerNPC Animator 동기화 (FixedUpdate/Update SetBool 호출). (4) Drill/Tractor visual 토글 + Mine 영역 진입/이탈 자동 처리 (UpdateVisuals — MineZone.OnPlayerInsideChanged 이벤트 구독). (5) IsMining 분기 — Mine 영역 안 + 정지 + Drill 미결제 시만 true. (6) DeskStack Y축 누적 정합 (ProcessorOutput 패턴 일치, Inspector _slotOffset 변경). (7) GameManager.OnTractorUpgraded 이벤트 부활 — 결정 6 부분 되돌림 (PlayerController.UpdateVisuals 트리거 전용, OreNode 폴링과 병행). (8) MineZone.OnPlayerInsideChanged 이벤트 신규 + idempotent guard. (9) mesh 자산 자체 도입 (Floreswa 5 캐릭터 + Mixamo 3 animation, Apply Root Motion ✗). |

영향:
- §2.1 InteractionZone 베이스 패턴 보존 (5a~5d 모두 파생 또는 미상속 동등 패턴 — JailZone/MineZone/OreNode/MinerSpawner는 미상속, UpgradeZone 4 파생은 파생).
- §2.2 StackContainer 7 사이트 사용 (visualCap 분리 + 일괄 트윈 메서드 추가).
- §2.3 GameSettingsSO 단일 진입점 보존 + 신규 그룹 2종 (Jail / Upgrade 확장 — 영상 정합 비용/효과 박음).
- §3 Consequences 부정적 결과 보강: 64 OreNode 매 frame 비용 + Tractor 1×4 _prevTractorNodes 매 frame 갱신 — 단순화 우위로 채택.
- 게이트 6.5 추가 영향: PlayerController/MineZone에 이벤트 통신 패턴 도입 (SetMining 메서드 폐기 + OnPlayerInsideChanged + UpdateVisuals 패턴). GameManager 이벤트 5개 → 6개 (OnTractorUpgraded 추가). Mat 9 → 10 (5d) + 외부 캐릭터 자산 도입 — 관련 표는 4일차 빌드 직전 일괄 갱신.
