# Third-Party Assets

이 저장소는 외부 자산 중 **재배포가 허용되지 않는 항목을 제외한 상태**로 공개되어 있다.
클론 직후에도 코드·씬·프리팹은 모두 열리지만, 아래 §1의 두 패키지는 직접 설치해야
캐릭터와 바위 메시가 보인다. (설치하지 않아도 게임 로직과 플레이는 정상 동작한다 —
해당 오브젝트가 보이지 않을 뿐이다.)

---

## §1. 저장소에서 제외된 패키지 — 직접 설치 필요

Unity Asset Store EULA는 자산을 완성된 제품에 **포함(embedded/incorporated)** 하는 것만
허용하며, 최종 사용자가 자산을 개별적으로 추출·다운로드할 수 있는 형태의 배포는 허용하지
않는다. 공개 저장소에 원본 `.fbx` / `.png` / `.mat`를 커밋하면 이 예외 조항에 해당하므로
아래 두 패키지는 제외했다.

| 패키지 | 퍼블리셔 | 배치 경로 | 용도 |
|---|---|---|---|
| Low Poly Character Pack | Floreswa (Unity Asset Store, 무료) | `Assets/Floreswa/` | Player / 고객 NPC / 작업자 NPC / Desk NPC 의 시각 표현 |
| Stylized Rocks (Lite) | 미상 — §3 참조 | `Assets/JC_StylizedRocks_Lite/` | 채굴 구역 배경 바위 |

### 설치 방법

1. Unity Asset Store에서 해당 패키지를 내려받는다.
2. Unity 에디터에서 Import 한다.
3. 위 표의 **배치 경로와 폴더명이 정확히 일치**해야 한다. 프리팹이 GUID로 참조하므로
   폴더명이 다르면 Missing 참조가 남는다.

경로가 맞으면 `Assets/Prefabs/Characters/*.prefab` 의 Missing 참조가 자동으로 복구된다.

---

## §2. 저장소에 포함된 외부 자산

| 자산 | 출처 | 라이선스 | 위치 |
|---|---|---|---|
| Idle / Slow Run / Melee Attack (애니메이션 3종) | Adobe Mixamo | Mixamo License — 프로젝트 내 사용 무료 | `Assets/Animations/*.fbx` |
| TextMesh Pro Essential Resources | Unity Technologies | Unity Companion License | `Assets/TextMesh Pro/` |
| LiberationSans SDF | Liberation Fonts | SIL Open Font License 1.1 | `Assets/TextMesh Pro/Fonts/` |
| EmojiOne 스프라이트 | JoyPixels (EmojiOne) | 귀속 표기 조건 — `EmojiOne Attribution.txt` 참조 | `Assets/TextMesh Pro/Sprites/` |

`Assets/Animations/AC_Character.controller` (Animator Controller) 는 자체 제작이며,
위 Mixamo 클립 3종을 Idle ↔ Walk ↔ Mining 상태 머신으로 연결한다.

---

## §3. 출처 확인 불가 항목

`JC_StylizedRocks_Lite` 는 패키지 내에 LICENSE / README 파일이 없고, Unity Asset Store에서
동일 명칭의 퍼블리셔·패키지를 특정하지 못했다. 재배포 조건을 확인할 수 없으므로 §1과 같이
제외 처리했다. 원본 출처를 아는 경우 본 문서에 추가할 것.

---

## §4. 패키지 의존성 (`Packages/manifest.json`)

파일이 저장소에 벤더링되지 않고 UPM이 자동으로 가져오므로 별도 설치가 필요 없다.

| 패키지 | 버전 | 라이선스 |
|---|---|---|
| `com.cysharp.unitask` | 2.5.10 | MIT |
| `com.unity.ai.navigation` | 1.1.7 | Unity Companion License |
| `com.unity.textmeshpro` | 3.0.7 | Unity Companion License |
| `com.unity.ugui` | 1.0.0 | Unity Companion License |

---

## §5. 자체 제작 자산

- `Assets/Materials/` — Standard Shader 기반 단색 Material 9종
- `Assets/Prefabs/` — 전 프리팹
- `Assets/Scenes/Main.unity` + 라이트맵 / NavMesh 베이크 산출물
- `Assets/Settings/GameSettings.asset` — 밸런스 데이터 ScriptableObject

AI로 생성한 자산(3D 모델 / 텍스처 / 사운드 / 메시)은 **0건**이다.
