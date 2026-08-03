# Arcade Idle Prototype

Arcade Idle 하이퍼캐주얼 프로토타입. 5일(유효 약 30시간) 안에 코어 루프를 완성하는 것을 목표로 진행한 단독 프로젝트.

**Unity 2022.3.62f2 LTS** · 720x1280 portrait · Built-in RP

<a href="https://youtu.be/VYkRC-bX1qs">
  <img src="https://img.youtube.com/vi/VYkRC-bX1qs/maxresdefault.jpg" width="480" alt="gameplay">
</a>

▶️ **[전체 플레이 영상 (YouTube)](https://youtu.be/VYkRC-bX1qs)** — 이동 · 채굴 · 가공 · 판매 · 업그레이드 한 사이클

---

## 코어 루프

작업자가 광석을 캐고 가공품으로 만들어 고객에게 판매하며 재화를 쌓고, 업그레이드로 생산 속도를 높이는 무한 성장 구조. 한 사이클 약 20~25초.

```
이동 → 채굴(Mine) → 가공(Processor) → 진열(Desk)
     → NPC 구매 → 재화 드롭 → 수집 → 업그레이드 → 반복
```

모든 상호작용은 구역 진입 기반 자동 실행이다. 입력은 화면 어디서나 드래그로 시작하는 플로팅 조이스틱 하나뿐이다.

---

## 설계 의도

30시간이라는 상한이 먼저 있었고, 그 안에서 9개 시스템을 만들어야 했다. 각 시스템을 독립적으로 구현하면 코드량이 몇 배로 늘어난다고 판단해, **재사용 축이 되는 추상화 3개를 착수 첫날 확정하고 이후 모든 시스템을 그 위에서 조립하는 방식**을 택했다.

| 추상화 | 역할 | 재사용 |
|---|---|---|
| `InteractionZone` | trigger 체류 누적 → 임계 도달 시 콜백 | 직접 파생 5종 + `UpgradeZone` 하위 5종 |
| `StackContainer` | 적재 슬롯 관리와 배치 보간 | 7개 적재 지점 |
| `GameSettingsSO` | 밸런싱 수치 일원화 | 전 시스템 |

### InteractionZone

체류 누적 로직을 추상 베이스에 두고, 파생 클래스는 Unity 메시지를 직접 오버라이드하지 않고 후크 메서드만 구현한다.

```csharp
protected abstract void OnAccumulatorTick(IInteractionUser user, float deltaTime);
protected abstract bool IsAccumulatorComplete(IInteractionUser user);
protected abstract void OnInteractionComplete(IInteractionUser user);
```

후크 시그니처의 `IInteractionUser` 인자는 2일차에 추가했다. 초기 설계에는 없었는데, 사용자별로 적재 대상이 갈리는 시점에서 파생 클래스가 베이스의 내부 Dictionary를 역참조해야 하는 문제가 드러났다. 캡슐화가 깨지고, 이후 NPC를 도입할 때 사용자 타입 분기가 불가능해진다. 첫 파생 클래스를 만들기 전에 고치는 편이 비용이 적다고 보고 시그니처를 변경했다.

베이스를 직접 상속하는 구역은 `DeskZone`, `ProcessorInputZone`, `ProcessorOutputZone`, `MoneyPileZone`, `UpgradeZone` 5종이다. `UpgradeZone`은 다시 결제 대상별로 5종(드릴 / 트랙터 / 보관소 / 작업자 고용 / 데스크 NPC 고용)으로 갈린다. 반면 `MineZone`은 8x8 grid 관리자 역할이 커서 베이스를 상속하지 않고 동등한 체류 패턴을 자체 구현했다 — 상속 강제가 오히려 부담이 되는 지점이라고 판단했다.

### StackContainer

광석·가공품·재화를 타입 구분 없이 같은 컴포넌트로 처리한다. 플레이어 등(운반용·재화용 2개), 데스크 상단, 가공기 입력·출력, 재화 더미, 데스크 NPC까지 7개 지점이 같은 컴포넌트를 쓴다.

사이트마다 배치 규칙만 다르다. 플레이어 등은 이동하므로 로컬 좌표 기준 수직 적재, 데스크는 정적 격자 배치다. 아이템 이동은 UniTask + CancellationToken으로 처리하고 `Mathf.Sin`으로 포물선 궤적을 만든다.

### GameSettingsSO

채굴 속도, 스택 한도, 단가, 쿨타임 등 밸런싱에 해당하는 값은 코드에 두지 않았다. 전부 ScriptableObject에 모아서 에디터에서 조정한다.

---

## 구현 범위

**시스템**
- 플로팅 가상 조이스틱 (Legacy Input 자체 구현)
- 채굴 / 가공 / 진열 / 업그레이드 / 고용 5개 상호작용 구역
- NavMeshAgent 기반 NPC 3종 — 고객 NPC(FSM 7상태), 데스크 직원(FSM 5상태), 채굴 NPC(FSM 3상태)
- `UnityEngine.Pool.ObjectPool` 기반 풀링 (광석, 가공품, 재화, NPC)
- 카메라 추적 + 업그레이드 시점 연출 시퀀스
- 재화 HUD, 스택 한도 표시, 보관소 수용량 표시

**의도적으로 제외한 것**
- 오디오 — 프로젝트 범위 밖, 시간 대비 가치 없음
- 광고 연동 아웃트로 — SDK 연동 영역
- 다중 워커, 2단계 이상 업그레이드 — 아키텍처는 확장 가능하게 두고 구현만 축소

---

## 문제 해결 기록

진행 중 발견한 결함은 원인과 조치를 짝지어 기록했다. 일부:

| 증상 | 원인 | 조치 |
|---|---|---|
| NPC가 상호작용 구역에 진입해도 반응 없음 | NavMeshAgent와 isTrigger 조합에서 trigger 콜백이 안정적으로 발생하지 않음 | 구역 로직을 명시적 Tick 호출로 전환 |
| 특정 조건에서 NPC 전원이 영구 대기 | 수요 카운트와 공급 카운트가 어긋나 서로를 기다리는 상태 | 재공급 판정 규칙을 3가지로 분리해 교착 해소 |
| 오브젝트가 간헐적으로 비활성화됨 | 플레이어의 다중 Collider가 같은 프레임에 진입·이탈 이벤트를 중복 발생 | 체류 콜백에 멱등 복구 로직 추가 |
| 월드 텍스트가 메시에 가려짐 | TMP 기본 머티리얼이 깊이 테스트를 수행 | Distance Field Overlay 셰이더로 교체 |

---

## 설계 문서

- [`docs/architecture/001-core-architecture.md`](docs/architecture/001-core-architecture.md) — 핵심 추상화 3종
- [`docs/architecture/002-folder-structure.md`](docs/architecture/002-folder-structure.md) — 폴더 구조와 배치 원칙
- [`docs/architecture/003-unitask-adoption.md`](docs/architecture/003-unitask-adoption.md) — UniTask 도입 판단

ADR은 작성 시점의 결정을 남긴 문서이며, 이후 구현에서 뒤집힌 결정은 각 문서의 Resolutions 절에 누적 기록했다.

---

## 실행

1. Unity 2022.3.62f2 LTS로 프로젝트를 연다.
2. [`docs/third-party-assets.md`](docs/third-party-assets.md) §1의 외부 패키지 2건을 설치한다.
   재배포가 허용되지 않아 저장소에서 제외했으며, 설치하지 않으면 캐릭터와 바위 메시가 보이지 않는다.
   (게임 로직과 플레이 자체는 설치 없이도 정상 동작한다.)
3. `Assets/Scenes/Main.unity`를 연다.

패키지 의존성은 UniTask(UPM Git URL)뿐이며 Unity가 최초 실행 시 자동으로 가져온다.

---

## 사용 자산

출처·라이선스 전체 정리는 [`docs/third-party-assets.md`](docs/third-party-assets.md) 참조.

| 구분 | 내역 |
|---|---|
| 저장소 포함 | Mixamo 애니메이션 3종, TextMesh Pro Essential Resources |
| 저장소 제외 (직접 설치) | Low Poly Character Pack (Floreswa), Stylized Rocks (Lite) |
| 자체 제작 | Material 9종, 전 프리팹, 씬 및 베이크 산출물, 밸런스 데이터 |

Unity Asset Store EULA는 자산을 완성된 제품에 포함하는 것만 허용하고 최종 사용자가 개별 추출할 수 있는 형태의 배포는 허용하지 않으므로, 해당 패키지 2건은 커밋에서 제외했다.

AI로 생성한 자산(3D 모델 / 텍스처 / 사운드 / 메시)은 0건이다.
