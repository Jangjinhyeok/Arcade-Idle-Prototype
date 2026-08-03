# ADR-002: Folder Structure (Assets / Scripts / Third-Party Isolation)

| Field | Value |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-04-29 |
| **Deciders** | 장진혁 (solo) |
| **Engine** | Unity 2022.3.62f2 LTS |
| **Related ADR** | `001-core-architecture.md` |
| **Supersedes** | (none) |

---

## 1. Assets/ 트리

```
Assets/
├── Animations/          — Animator Controller 및 Animation Clip (.anim)
├── Materials/           — 단색 Material 인스턴스 (Lit/Unlit, Built-in RP)
├── Models/              — 자체 제작 메시 (.fbx, .obj). AI 생성 모델은 _ThirdParty/ 이하로
├── Prefabs/             — 런타임 인스턴스화 대상 Prefab
│   ├── Characters/      — Player.prefab, PrisonerNPC.prefab, WorkerNPC.prefab
│   ├── Environment/     — Mine.prefab, Processor.prefab, Desk.prefab, UpgradeZone.prefab
│   └── Pickups/         — OrePiece.prefab, Handcuff.prefab, MoneyPickup.prefab
├── Scenes/              — Main.unity (단일 씬 빌드)
├── Scripts/             — 모든 C# 소스. 하위 구조는 §2 참조
├── Settings/            — ScriptableObject 인스턴스 (.asset). 예: GameSettingsSO.asset
├── Textures/            — 자체 제작 텍스처. AI 생성 텍스처는 _ThirdParty/ 이하로
└── _ThirdParty/         — 외부·AI 생성 자산 격리. 하위 구조는 §3 참조
```

`Plugins/`는 Asset Store 무료 패키지가 자동 배치하는 경로이므로 목록에서 제외했다. 해당 폴더가 생성되면 의존성 유지를 위해 그대로 둔다(§3 참조).

---

## 2. Scripts/ 세부 구조

```
Assets/Scripts/
├── Core/                — 앱 생명주기 및 전역 싱글톤
│   │                      GameManager.cs (싱글톤, R3 결정)
│   └── ...              CameraFollow.cs
├── Player/              — 플레이어 입력 처리 및 이동
│   │                      PlayerController.cs (Rigidbody Kinematic)
│   └── ...              JoystickInput.cs (Legacy Input 기반 가상 조이스틱)
├── Interaction/         — InteractionZone 추상화 + 5개 파생 (ADR-001 §2.1)
│   │                      InteractionZone.cs (abstract MonoBehaviour, 베이스)
│   │                      IInteractionUser.cs (마커 인터페이스, R1 결정 — 아래 비고 참조)
│   │                      MineZone.cs
│   │                      ProcessorZone.cs
│   │                      DeskZone.cs
│   │                      UpgradeZone.cs
│   └── ...              HireZone.cs
├── Stack/               — 적재 시스템 (ADR-001 §2.2)
│   │                      StackContainer.cs (MonoBehaviour, 4개 사이트 공용)
│   │                      Stackable.cs (MonoBehaviour, 마커 컴포넌트)
│   └── ...              StackableType.cs (enum, R2 결정 — 순수 C# 파일)
├── NPC/                 — NavMeshAgent 래핑 + NPC 상태 머신
│   │                      NPCBase.cs (abstract MonoBehaviour, IInteractionUser 구현)
│   │                      PrisonerNPC.cs
│   └── ...              WorkerNPC.cs
├── Economy/             — 재화 보유·수집·소모 로직
│   └── ...              MoneyPickup.cs (풀링 대상), WalletController.cs
├── UI/                  — Canvas/UGUI MonoBehaviour. 비고: 데이터 소유 금지, 이벤트 구독만
│   └── ...              MoneyHUD.cs, MaxStackIndicator.cs
├── Settings/            — ScriptableObject 클래스 정의 (인스턴스는 Assets/Settings/ 에 저장)
│   └── ...              GameSettingsSO.cs (ScriptableObject, 순수 데이터 컨테이너)
└── Util/                — static 헬퍼 및 확장 메서드 (MonoBehaviour 아님)
    └── ...              MathHelper.cs (포물선 보간 공용 계산), TagConstants.cs
```

**`IInteractionUser` 배치 사유:** `Scripts/Interaction/`에 배치한다. 이 인터페이스는 InteractionZone 베이스 클래스의 `Dictionary<IInteractionUser, float>` 누적 타이머(R1 결정)를 위해 존재하며, Interaction 시스템의 공개 계약이다. `Core/`는 앱 생명주기·싱글톤 전용으로 유지하고, 도메인 인터페이스를 해당 도메인 폴더 안에 두는 것이 응집도 원칙에 부합한다.

**폴더별 비고:**

| 폴더 | 비고 |
|---|---|
| `Settings/` | SO 클래스 정의만 포함. 인스턴스(`.asset`)는 `Assets/Settings/`에 분리 보관. |
| `Util/` | 모든 클래스는 `static`. MonoBehaviour 없음. 씬 오브젝트에 붙이지 않음. |
| `Economy/` | `WalletController`는 `GameManager`가 소유하는 컴포넌트 또는 내부 로직으로 구현 가능. 1일차 구현 시 결정. |

---

## 3. 외부 자산 / 무료 에셋 격리 정책

```
Assets/_ThirdParty/
├── AI-Generated/        — AI 도구별 서브폴더로 분리
│   ├── Midjourney/      — Midjourney로 생성한 텍스처·스프라이트
│   └── ChatGPT/         — ChatGPT(DALL-E) 등 기타 AI 생성 자산
├── Models/              — 직접 다운로드한 무료 3D 모델 (Asset Store 비경유)
└── Textures/            — 직접 다운로드한 무료 텍스처
```

- **언더스코어 prefix 의도:** Project 창 알파벳 정렬 시 최상단에 노출되어 자체 코드(`Scripts/`, `Prefabs/` 등)와 시각적으로 즉각 구분된다.
- **Asset Store 무료 패키지:** Unity Package Manager 또는 Import 시 `Assets/Plugins/`에 자동 배치된다. 의존성이 깨질 수 있으므로 `_ThirdParty/`로 이동하지 않는다. 그대로 둔다.
- **AI 생성 자산 보관 규칙:** `_ThirdParty/AI-Generated/[tool]/` 하위에 배치. 예: Midjourney로 생성한 보관소 바닥 텍스처 → `_ThirdParty/AI-Generated/Midjourney/StorageFloor_01.png`.
- **자산 추가 시 의무:** 외부 또는 AI 생성 자산을 추가할 때마다 `docs/third-party-assets.md`에 출처·라이선스를 등록해야 한다. 출처가 불명이거나 재배포 조건을 확인할 수 없는 자산은 커밋하지 않는다.

---

## 4. 의도적 제외 / 안 만드는 폴더

| 폴더 | 사용 여부 | 사유 |
|---|---|---|
| `Editor/` | 필요 시 생성, 1일차에는 만들지 않음 | 소규모 디버그 툴(예: GameSettingsSO 핫리로드 버튼)이 필요해질 수 있으나 1일차 기준 불필요. 필요 시점에 `Assets/Editor/` 생성. |
| `Tests/` | 미사용 | 본 프로젝트 방침에 따라 자동화 테스트 미작성. 수동 플레이 검증으로 대체. |
| `Resources/` | 미사용 | ADR-001 R3에서 `Resources.Load` 배제 결정. 경로 하드코딩 및 빌드 누락 위험. 모든 SO 참조는 `GameManager` Inspector 직접 주입 경유. |
| `StreamingAssets/` | 미사용 | 런타임 파일 I/O 없음. 단일 씬 빌드이며 외부 데이터 파일 불필요. |
| `Localization/` (또는 i18n) | 미사용 | 한국어 단일 빌드. 다국어 지원 범위 외(§9 참조). |

---

## 5. 비주얼 자산 정책 (1일차.5 추가, 2026-04-29)

Unity 빌트인 primitive + Standard Shader Material 색 구분만 사용.
Material 9개로 통합. 기존 아케이드 아이들 게임의 코어 루프 분석에서
도출한 색 구분 정책을 따른다.

`Assets/Materials/`는 본 정책 산출물 보관 위치. 이름 규칙
`Mat_{용도}.mat`. 2일차 시작 시점에 9개 `.mat` 일괄 생성한다.
