# ADR-003: UniTask Adoption (Coroutine 대체용 비동기 라이브러리)

| Field | Value |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-04-29 |
| **Deciders** | 장진혁 (solo) |
| **Engine** | Unity 2022.3.62f2 LTS |
| **Related ADR** | `001-core-architecture.md`, `002-folder-structure.md` |
| **Supersedes** | (none) |

---

## 1. Context

본 프로젝트는 5일(약 30시간) 일정으로 개발한 Unity 2022.3.62f2 프로토타입이다. 비동기 흐름이 필요한 지점은 다음 4개로 식별된다.

1. `StackContainer` 등 뒤 적재 시 포물선 보간 트윈 (ADR-001 §2.2)
2. Money pickup 흡수 트윈 (Desk 앞 → Player `StackContainer`)
3. `ProcessorZone` 변환 타이머 (광석 소모 → 가공품 생성의 시간 기반 진행)
4. 일부 NPC 시퀀스 (고객 NPC의 Desk 도착 후 가공품 픽업 대기 등)

ADR-001 §2.2는 본래 "코루틴을 통한 이동"을 명시했다. 코루틴(`IEnumerator` + `StartCoroutine`)은 Unity 표준이고 `null` 반환만 잘 다루면 충분히 동작하지만, 다음 한계가 1일차에서 드러났다.

- **GC 부담**: `IEnumerator` 인스턴스 박싱이 매 시퀀스마다 발생. 모바일 30 FPS 최저 보장 마진을 갉아먹는다.
- **취소 처리 부재**: `MonoBehaviour` 비활성화/파괴 시 자동 정지되지만, ObjectPool 회수 시점이나 부분 취소(예: 흡수 도중 풀 회수)에 대한 표준 신호가 없다. 수동 플래그를 늘리면 코드가 빠르게 더러워진다.
- **다단계 시퀀스 가독성**: "흡수 → 적재 → 이벤트 발행" 같은 chained flow에서 콜백 중첩 또는 상태 플래그 의존이 누적된다.

본 프로젝트의 "No third-party libraries" 원칙과 충돌 여지가 있어, 명시적 ADR로 도입 범위와 사용 패턴을 고정한다. 모바일 하이퍼캐주얼 장르에서 UniTask가 사실상 업계 표준인 점도 채택 근거에 포함된다.

---

## 2. Decision

### 2.1 도입 라이브러리 및 버전 핀

**Cysharp UniTask v2.5.10**을 도입한다. UPM Git URL 방식으로 `Packages/manifest.json`에 다음 의존성을 추가한다.

```json
"com.cysharp.unitask": "https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask#2.5.10"
```

버전 핀 사유: v2.5.10은 2024-10 릴리스이며 Unity 2022.3 LTS 호환이 공식 검증되었다 (출처: <https://github.com/Cysharp/UniTask/releases/tag/2.5.10>). `master` 또는 floating ref 사용은 빌드 재현성을 해치므로 금지한다.

### 2.2 사용 범위 한정

UniTask는 **코루틴 대체 용도로만** 사용한다.

- 시간 기반 비동기 시퀀스: `UniTask.Delay`, `UniTask.Yield`, `UniTask.WaitUntil`
- 다단계 트윈: `StackContainer` 등 뒤 적재 포물선 보간, Money pickup 흡수
- `ProcessorZone` 변환 타이머의 비동기 흐름

**ADR-001 R1의 `Dictionary<IInteractionUser, float>` 누적 타이머는 그대로 유지한다.** 누적 타이머는 매 프레임 폴링 기반(`OnTriggerStay` 내 `Time.deltaTime` 누적)이므로 UniTask 도입과 무관하다.

**UniRx 도입 금지.** UniTask는 UniRx와 함께 거론되는 경우가 많지만 본 프로젝트는 UniTask만 도입한다. 이벤트는 C# 표준 `event` / `Action`으로 통일한다(ADR-001 §2.2 `StackContainer` 이벤트 시그니처와 일관). UniTask의 `AsyncReactiveProperty`, `Channel`, `AsyncEnumerable` 등 고급 기능도 도입 금지.

기타 3rd-party 라이브러리 도입 금지 원칙은 그대로 유지한다.

### 2.3 사용 패턴

**CancellationToken 표준:** Unity 2022.2+ `MonoBehaviour`에 내장된 `destroyCancellationToken` 프로퍼티를 사용한다. 확장 메서드 `GetCancellationTokenOnDestroy()`는 매 호출마다 보조 컴포넌트 탐색·할당 비용이 발생하므로 **사용 금지**한다.

```csharp
private async UniTaskVoid AbsorbAsync()
{
    await UniTask.Delay(500, cancellationToken: destroyCancellationToken);
    // ...
}
```

**반환 타입 규칙:**
- Fire-and-forget: `UniTaskVoid`
- void 비동기: `UniTask`
- 결과 반환: `UniTask<T>`
- `async void` **금지**

**PlayerLoop 초기화:** 본 프로젝트는 단일 씬 빌드이므로 UniTask 기본 `BeforeSceneLoad` 자동 초기화로 충분하다. `AfterAssembliesLoaded` 등 커스텀 PlayerLoop 초기화는 **하지 않는다.**

---

## 3. Consequences

**긍정적 결과:**
- `IEnumerator` 박싱 제거로 GC 압박 감소. 모바일 60 FPS 목표 마진 확보.
- `destroyCancellationToken`으로 파괴 시점 자동 취소가 표준화. ObjectPool 회수와 시퀀스 취소가 자연스럽게 연동.
- `async/await` 다단계 시퀀스 가독성 확보. Money pickup 흡수 → StackContainer 적재 → 이벤트 발행 같은 흐름이 한 함수에 평탄하게 표현됨.

**부정적 결과 / 제약:**
- 외부 의존성 1개 추가. `Packages/manifest.json` 변경 + 빌드 재현 시 GitHub fetch 필요. 저장소 클론 후 최초 1회 fetch가 필요하지만 Unity가 자동 처리한다.
- async/await 디버깅 학습 비용. 본 개발자가 .NET async에 익숙하므로 무시 가능.

**회피된 대안:**

| 대안 | 기각 사유 |
|---|---|
| 코루틴 유지 | GC 박싱·취소 처리 단점이 5일 일정 후반부 디버깅 비용으로 전이될 위험. |
| `System.Threading.Tasks.Task` | Unity 메인 스레드 동기화 비용 큼. ThreadPool 사용 시 Unity API 호출 불가. 모바일 GC 부담이 코루틴보다도 큼. |
| `Awaitable` (Unity 2023.1+) | Unity 2023.1+ 도입 기능. 본 프로젝트의 락된 엔진 버전(2022.3.62f2)에서 사용 불가. |

---

## 4. Compliance

본 프로젝트의 "No third-party libraries" 원칙의 **명시적 예외 1건**으로 처리한다. 다음 조건을 모두 충족한다.

- **라이선스: MIT.** UniTask 공식 README에서 확인. 재배포 자유, 상업 이용 자유, attribution만 요구.
- 코루틴 대체 단일 용도로 한정(§2.2).
- DOTween, LeanTween, UniRx 등 다른 3rd-party 라이브러리 도입 금지 원칙은 그대로 유지.
- 본 ADR로 추적 가능.

"No third-party tweening libraries" 원칙은 UniTask와 무관하다 — UniTask는 트위닝 라이브러리가 아니라 비동기 흐름 라이브러리다. 락된 결정 "Tweening: `Vector3.Lerp` + `Mathf.Sin` for parabolic arcs"는 그대로 유지된다. 트윈 보간 곡선 자체는 여전히 `Vector3.Lerp`와 `Mathf.Sin`으로 계산하고, UniTask는 보간 루프의 시간 진행을 담당할 뿐이다.

### Unity 2022.3.62f2 API 호환 확인

- `MonoBehaviour.destroyCancellationToken` — Unity 2022.2 도입, 2022.3 LTS 안정.
- `UniTask` v2.5.10 — Unity 2022.3 LTS 공식 호환 (release notes 확인).
- `Awaitable` (Unity 2023.1+) — 본 ADR에서 참조하지 않음.

---

## 5. Migration Impact

**ADR-001 영향:** §2.2 한 줄 표현 교체 + §5 Resolutions에 R5 신규 추가. 그 외 본문 무영향. 구체적으로:

- §2.2 등 뒤 적재 보간 본문의 "코루틴을 통해 현재 월드 위치에서 목표 슬롯 위치까지 이동시키며" → "UniTask + CancellationToken을 통해 현재 월드 위치에서 목표 슬롯 위치까지 이동시키며"로 한 군데만 교체.
- §5 Resolutions 아래 **R5** 신규: "비동기 처리는 UniTask 채택. ADR-003 참조. R1의 `Dictionary<IInteractionUser, float>` 누적 타이머는 그대로 유지하되, 시간 기반 트리거가 발생하는 시점의 비동기 흐름(Processor 변환 시간 등)은 UniTask 사용."
- §2.1 InteractionZone 베이스 시그니처, §2.3 GameSettingsSO, §3 Consequences, §4 Compliance 표는 모두 변경 없음.

**ADR-002 영향: 무영향 확인.** UniTask는 UPM 의존성으로 `Packages/manifest.json`을 통해 설치되며, `Assets/_ThirdParty/`나 `Assets/Plugins/`에 파일이 들어가지 않는다. ADR-002 §1 Assets 트리, §2 Scripts 세부 구조, §3 외부 자산 격리 정책 모두 변경 없음.

**1일차 본 작업 영향:** Player 이동, Joystick 입력, 카메라 follow 등 동기 로직은 UniTask 도입 무관. `StackContainer` 트윈 보간과 `ProcessorZone` 변환 타이머 시점부터 UniTask 사용을 시작한다.
