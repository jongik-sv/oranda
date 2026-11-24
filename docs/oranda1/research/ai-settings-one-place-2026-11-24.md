# AI 설정 한곳으로 모으기 — 회차 보고

- 작성일: 2026-11-24
- 가지: `dev-jji-win144`(앞 회차 `4c42ff44` 위). 푸시·릴리스 없음.
- 사용자 불만: 「한곳에서 하도록 해야지 전혀 다른 메뉴에서 설정하잖아.」 AI 설정이 **도구 > AI 연결 도우미…**, **도구 > 설정 > AI 모델**, **도구 > 외부 에이전트 연결…** 세 곳에 흩어져 있었다. 도우미가 만든 프로필은 설정 탭에서 따로 고쳐야 해서 직원들이 어디로 가야 할지 몰랐다.

## 1. 결과 한눈에

- 집 = 설정 창 「AI」 탭(이름을 「AI 모델」 → 「AI」 로). 메뉴는 **도구 > AI 설정…** 한 줄이 이 탭으로 연다.
- 탭 순서: 「연결 상태」 카드 → 「AI 를 쓸 접속」 → 「외부 에이전트」 → 접힌 「고급」(예전 「AI 모델」 쪽 그대로).
- 도우미는 이 탭에서 연 **자식 창**(설정 창 위)으로 돈다. 닫거나 끝내면 탭이 저장소의 값으로 「고급」·카드·접속 목록을 다시 읽어 방금 고른 값이 보인다.
- 첫 실행 묻기·실패 띠의 「AI 연결 도우미 열기」·팔레트의 옛 명령은 모두 설정 창을 「AI」 탭으로 열고 그 위에 도우미를 바로 띄운다.
- AI 기능이 꺼져 있거나 주 업무가 「조회만」이어도 탭을 숨기지 않고 까닭과 「기능 켜기」·「주 업무 바꾸기」 단추를 보인다.

## 2. 한 일

| 갈래 | 내용 | 파일 |
|---|---|---|
| 탭 모델 | `AiSettingsHubPage`: 카드 상태 넷(막힘·연결 안 함·연결됨·문제), 쓰는 대상 이름, 마지막 연결 시험 줄, 「연결 시험」(도우미의 시험 단계 모델을 그대로 씀), 도우미 열기·맡겨 둔 도우미, 다시 읽기, 「기능 켜기」·「주 업무 바꾸기」, 외부 에이전트 상태·「연결…」·「스킬 설치…」, 「AI 를 쓸 접속」(도우미 `AccessStepViewModel` 그대로, 바로 저장) | `src/Oranda.App/Features/Ai/Settings/AiSettingsHubPage.cs` |
| 탭 화면 | `AiSettingsHubView`(코드로 만든 화면, 글은 뷰모델이 한/영). 「고급」 = `Expander`(처음 접힘) 안의 `AiModelSettingsView` | `AiSettingsHubView.cs` |
| 부품 조립 | 앱 서비스에서 런처·접속·외부 에이전트 링크·연결 창·스킬 설치·시험 맥락을 모은다 | `AiSettingsHubServicesFactory.cs` |
| 설정 창 | 「AI」 탭 = 허브 화면, 늘 보임(`AiTabVisible` 없앰), `AiTabIndex = 10`, `AiModel => AiHub.Model`(도우미 뒤 새 객체로 바뀜 → 프로필 저장 알림을 다시 건다), 「저장」 뒤 카드 새로 고침, 「기능」 탭으로 옮기며 AI 튜닝 켜 두기. 설정 창이 열린 뒤 맡겨 둔 도우미 띄우기, 지금 열린 설정 창(`SettingsWindow.Current`) | `src/Oranda.App/Shell/Settings/SettingsViewModel.cs`, `SettingsWindow.axaml(.cs)` |
| 런처 | `OpenOwnedAsync(owner, …)`(탭 단추 = 설정 창 위). `OpenAsync`(첫 실행·실패 띠·옛 명령)는 설정 창 「AI」 탭을 거친다(이미 열려 있으면 그 창, 아니면 `ShowSettings` + 맡겨 두기). 맥락 만들기를 `CreateContext` 로 꺼내 탭의 연결 시험이 같이 쓴다 | `src/Oranda.App/Features/AiSetup/Wizard/AiSetupLauncher.cs` |
| 메뉴 | 새 명령 `oranda.ai.settings.open`「AI 설정…」(도구, `externalAgent@44`, AI 기능과 무관하게 보임). `oranda.ai.setup.open`·`oranda.agent.external.connect` 는 메뉴 자리를 빼고 팔레트로만. 에이전트 `run_command` 등급표에 새 id(「oranda.ai.」 규칙으로 거부) | `AiSetupEntryCommands.cs`, `AiSetupContracts.cs`, `ExternalAgentConnectCommands.cs`, `CommandGradeTable.cs` |
| 연결 시험 기록 | `AiSetupSettings.LastTest`(`AiConnectionTestRecord`: 시각·성공·대상 이름·답 시간·실패 종류, 비밀 없음). 읽기는 관대하게(깨지면 기록 없음). 도우미의 시험 단계와 탭의 「연결 시험」 이 남긴다 | `src/Oranda.Core/Settings/AppSettings.cs`, `src/Oranda.Core/Ai/Setup/TolerantAiSetupSettingsConverter.cs`, `src/Oranda.App/Features/AiSetup/Test/AiSetupTestStepViewModel.cs` |
| 글 | 탭 이름 「AI」(영 AI), 메뉴 「AI 설정…」(영 AI Settings…), 「설정 › AI 모델에서」→「설정 › AI 에서」, 「설정의 「AI 모델」 에서」→「설정의 「AI」 탭에서」, 메뉴 꺼짐 까닭 「설정의 「AI」 탭에서 AI 를 먼저 연결하세요」, 첫 실행 묻기 「나중에 도구 메뉴의 「AI 설정…」에서도」. 한·영 글자 표·리터럴 표 함께 | `strings-ko.json`, `strings-en.json`, `literals-en.json`, `AiEntryRule.cs`, `AiTuningCustomCli.cs`, `PartitionAdvisorStart*`, `RoutingAiDriver.cs`, `AiSetupEntryText.cs` |
| 매뉴얼 | `ai/models.md`(「설정 창의 AI 탭」 절 새로·카드 상태 표·막혔을 때·「고급」 절), `ai/setup.md`(여는 곳 = 「AI」 탭, 도우미는 탭 위에서 열리고 돌아옴), `ai/external-agents.md`(연결 켜는 곳), `ai/overview.md`·`chat.md`·`commands.md`·`settings/options.md`·`schema/table-editor.md` 의 경로, 범위표(`site/scope.json`)·매뉴얼 색인 머리 | `site/src/content/docs/**`, `site/scope.json`, `src/Oranda.Core/Ai/Manual/OrandaManualIndex.cs` |
| 그림 | 새 그림 `ai-settings-tab`·`ai-settings-start`·`ai-settings-blocked`, 다시 찍음 `settings-features`·`settings-execution`·`settings-grid`(왼쪽 탭 목록에 「AI」 가 늘 보이게 바뀜) | `site/src/assets/screens/`, `tests/Oranda.App.Tests/Manual/ManualAiScreens.cs` |

## 3. 설계에서 벗어난 것

| 항목 | 기본 설계 | 한 것 | 까닭 |
|---|---|---|---|
| 도우미 자리 | 탭 안에서 화면이 단계 화면으로 바뀜 | 탭에서 연 **자식 창**(주인 = 설정 창). 닫히면 탭이 값을 다시 읽음 | ① 설정 창은 열 때 모든 범주를 재어 본문 크기를 고정한다(`d2b2b73`, 폭 상한 760). 왼쪽 범주 목록을 빼면 단계 자리는 약 570 폭으로, 도우미 최소 폭 600(단계 화면 렌더 시험 기준)보다 좁다. 로그인·서버 단계가 잘린다. ② 설정 창은 「저장/닫기」 로 미확정 사본을 들고 있는 대화상자인데, 도우미는 단계마다 설정·접속·이어 하기를 바로 쓴다. 한 창 안에서 섞으면 「닫기(저장 안 함)」 의 뜻이 흐려진다. ③ 두 화면의 기본·취소 단추(설정 「저장」·「닫기」, 도우미 「다음」·「닫기」)가 Enter·Esc 를 두고 부딪힌다. 자식 창이면 셋 다 그대로이고, 닫히면 탭으로 돌아와 고른 값이 보여 사용자 눈에는 「한곳」 이다 |
| 탭 이름 | 「AI」 또는 「AI 연결」 | 「AI」 | 메뉴 「AI 설정…」 과 맞고 짧다(왼쪽 범주 목록 폭) |
| 「기능 켜기」 | — | 같은 설정 창 「기능」 탭으로 옮기며 「AI 튜닝」 을 켜 둔다(「저장」 해야 적용, 재시작 안내는 그 탭). 조회만이면 그냥 「기능」 탭으로 | 기능·주 업무는 이미 설정 창 「기능」 탭이 맡는다. 같은 창 안에서 옮기면 길이 하나다 |
| 카드가 보는 값 | — | **저장된** 설정 | 「연결 시험」 은 저장된 대상으로 한다. 「고급」 에서 바꾼 값은 「저장」 뒤 카드에 들어간다(매뉴얼에 적음) |
| 옛 명령 | 메뉴에서 뺌 | 명령은 남기고 메뉴 자리만 뺌(팔레트) | 실패 띠·외부 에이전트 상태 띠·매뉴얼 범위표·에이전트 등급표가 id 를 쓴다 |
| 매뉴얼 그림 | 다시 찍기 | 이 변경이 바꾼 그림만 넣었다(새 3장 + 설정 창 3장) | 이 PC(Windows)에서 찍으면 글꼴이 기존 그림(macOS)과 달라 AI 장 전부가 바뀐다. 내용이 그대로인 그림은 되돌렸다 — macOS 에서 다시 찍기를 사용자 확인표에 둔다 |

## 4. 시험

| 프로젝트 | 결과 |
|---|---|
| `Oranda.Core.Tests` | 통과 3,549 · 실패 0 |
| `Oranda.App.Tests` | 통과 7,378 · 실패 0 · 건너뜀 69(실제 DB·실제 CLI 시험 등 기존 건너뜀). 첫 전체 실행에서 `GridFontSettingsTests.Zoom_commands_follow_the_focused_area` 가 파일 접근 거부(UnauthorizedAccessException)로 한 번 실패했고, 따로·다시 전체로 돌리면 통과 — 이번 변경과 무관한 흔들림 |
| `Oranda.Platform.Tests` | 통과 1,196 · 실패 0 · 건너뜀 36 |
| `Oranda.Agent.Cli.Tests` | 통과 187 · 실패 0 |
| `Oranda.Language.Tests` | 통과 1,550 · 실패 0 |
| `Oranda.Architecture.Tests` | 통과 31 · 실패 0 |

- 새 시험: `tests/Oranda.App.Tests/Features/Ai/AiSettingsHubTests.cs` 10개 — 카드 네 모양(연결 안 함·Claude Code·프로필·지워진 프로필·기능 꺼짐·조회만), 마지막 시험 줄(성공·실패 경고), 탭에서 연 도우미 뒤 「고급」 이 새 값이고 설정 창 「저장」 이 옛 값으로 덮지 않음, 맡겨 둔 도우미 한 번·닫혀도 풀림, 「연결 시험」 이 저장된 대상으로 돌고 기록됨·기타 CLI 는 시험 없음, 「기능 켜기」 → 「기능」 탭·AI 튜닝 켜짐, 「AI 를 쓸 접속」 이 도우미와 같은 값으로 바로 저장, 화면(고급 접힘·도우미 단추·주인 = 탭이 있는 창·고급 펼침), 설정 창을 도우미로 열면 크기를 정한 뒤 그 창 위에 띄움, 영어 글.
- `AiSetupWindowTests.Launcher_opens_over_the_window_the_settings_tab_gives`, `AiSetupEntryTests`(메뉴 한 줄·옛 명령 팔레트만·「AI 설정…」 이 「AI」 탭으로 엶), Core `AiSetupSettingsTests`(마지막 시험 왕복·깨진 값).
- 고친 시험: `AiCommandsTests`(탭을 숨기지 않음·메뉴 꺼짐 까닭 글), `LocalizationTests`(「AI」 는 고유 이름 예외), `CommandGradeTableTests`(거부 133→134), `ExternalAgentConnectTests`(연결 명령은 메뉴에 없고 「AI 설정…」 이 도구 메뉴에), `MenuWidthTests`(AI 구성: 항목·SQL 탭 −1, 모든 구성 팔레트 +1, `AiMenuItems` 3→2), `RoleSurfaceMatrixTests`(명령 314→315).
- 매뉴얼 그림 생성기는 `ORANDA_MANUAL_CAPTURE=1`·`ORANDA_RENDER_DIR` 로 Windows 에서 돌렸다.

## 5. 사용자 확인표 (macOS 손 확인)

1. **설정 창 크기**: **도구 > AI 설정…** 으로 열었을 때 「AI」 탭이 잘리지 않는지, 「고급」 을 펼쳤을 때 본문만 스크롤되고 창이 출렁이지 않는지(`d2b2b73` 고정 크기).
2. **도우미 자식 창**: 탭의 「연결 도우미 시작」·「다시 연결(도우미)」 이 설정 창 위(가운데)에 뜨는지, 도우미를 닫으면 설정 창으로 돌아와 카드·「고급」 이 방금 고른 값인지. 첫 실행 묻기의 「AI 연결」 과 실패 띠의 「AI 연결 도우미 열기」 가 설정 창 + 도우미 순서로 뜨는지.
3. **설치·로그인**: 도우미 갈래 A(설치 → 브라우저 로그인 → 연결 시험)를 실제 claude 로 한 번. 끝난 뒤 카드의 「마지막 연결 시험」 줄.
4. **파일 고르기 대화상자**: 도우미 갈래 B(회사 AI 서버)의 설정 파일(`.oranda-ai`) 고르기 대화상자가 도우미 창 위에서 열리는지(이제 주인 창이 설정 창 → 도우미 창으로 둘 겹친다). 고른 뒤 도우미·설정 창 순서가 그대로인지.
5. **외부 에이전트**: 탭의 「연결…」·「스킬 설치…」 창이 설정 창 위에 뜨는지, 연결 뒤 「외부 에이전트」 묶음 상태 줄이 바뀌는지.
6. **매뉴얼 그림**: `ai-settings-tab`·`ai-settings-start`·`ai-settings-blocked`·`settings-features`·`settings-execution`·`settings-grid` 를 macOS 에서 다시 찍어 글꼴을 다른 그림과 맞춘다(`ORANDA_MANUAL_CAPTURE=1 ORANDA_RENDER_DIR=… dotnet test --filter "FullyQualifiedName~ManualAiScreens.Ai_settings|FullyQualifiedName~ManualSettingsScreens"`).

## 6. 열린 것 (1회차 끝, 아래 7절에서 모두 결정됨)

1. 「고급」 안 「AI 연결 도우미 열기」 링크 → 지움(결정 4).
2. 도우미 뒤 저장 안 한 고급 변경 → 버리기 전에 묻기(결정 5).
3. CLI 연결 시험 → 넓힘(결정 6).
4. 바인드 결정 ①~③ → 「권장대로」 확정, 8절.

## 7. 2회차 — 확인된 시안 반영 (사용자 「권장대로」)

사용자 지적 「두 개의 연결에 아무런 연관성이 없어 보인다」: 안의 AI 와 밖의 에이전트는 **같은 접속별 허용 수준**(`AgentGrantPolicy.LevelFor` = min(접속 실효 수준, 외부 상한))을 쓰는데, 연결 창의 접속 고르기·「조회 허용」과 도우미의 켬/끔 단계가 따로 놀아 같은 값을 두 곳에서 고친다는 것이 보이지 않았다. Mac 세션 HTML 시안을 사용자가 확인했고 그대로 만들었다.

### 7.1 한 일 (커밋 `c70158e0`·`c1cc1244`·`b3c06d72`·`44f4c083`·`bf8652b3`)

| 갈래 | 내용 | 파일 |
|---|---|---|
| 공유 값 | `AiConnectionAccess`: 접속마다 안의 AI 수준(실효 수준)·밖의 에이전트 수준(min(접속, 외부 상한))·상한이 낮췄는지·고를 수 있는 수준. 저장 규칙(환경 기본값 = 「기본값 따름」), 운영 수준을 직접 올릴 때 이름 재입력(접속 편집과 같은 규칙) | `src/Oranda.Core/Ai/Access/AiConnectionAccess.cs` |
| 공유 서비스 | `SharedAiAccessService`: 읽기·쓰기 한 길. 어디서 저장하든(접속 편집·도우미·탭) 접속 저장 사건으로 바뀜을 알림 → 탭 ① 이 곧바로 따라 바뀜 | `src/Oranda.App/Features/AiSetup/Access/SharedAiAccessService.cs` |
| 공유 표 | `AccessRow`(수준 단추 5개·운영 한도·이름 재입력 칸, 예전 켬/끔 유지)·`AccessTableView`(이름·환경 표·단추·뜻풀이 한 줄). 탭 ① 과 도우미 「AI 를 쓸 접속」 단계가 **같은 컨트롤** | `AccessRow.cs`, `AccessText.cs`, `AccessView.cs`, `AccessStepViewModel.cs` |
| 탭 배치 | 연결 전·막힘이면 맨 위 큰 칸 + 접힌 세 묶음, 연결되면 번호 배지 ① 「AI 가 볼 수 있는 접속」 ② 「Oranda 안의 AI」(상태 알약·마지막 시험·연결 시험·다시 연결 (도우미)·실효 수준 회색 띠·접힌 고급 설정) ③ 「밖의 AI 에이전트」(상태·스킬 상태·스킬 설치…·연결…·min 값 회색 띠·「자기 프로그램과 자기 로그인」 안내) | `AiSettingsHubPage.cs`, `AiSettingsHubView.cs`, `AiSettingsHubServicesFactory.cs` |
| 연결 시험 넓힘 | 시스템 CLI(Codex·Antigravity·opencode)·기타 CLI 에 짧은 질문 하나(앱 AI 라우터 그대로, 90초). 실패는 대상 이름을 넣은 쉬운 말(찾지 못함·로그인 안 됨·제시간에 답 없음) | `AiCliConnectionTest.cs` |
| 버리기 전에 묻기 | 도우미가 끝났는데 고급 설정에 저장 안 한 변경이 있으면 「저장 안 한 고급 설정 변경이 있습니다. 버리고 도우미가 고른 값을 쓸까요?」(버리기·그대로 두기) | `AiSettingsHubPage.RunWizardAsync`, `AiSettingsHubView` |
| 고급 링크 | 고급 설정 안 「AI 연결 도우미 열기」 삭제 | `AiModelSettingsView.axaml`, `AiModelSettingsPage.cs` |
| 연결 창 | 「조회 허용」 확인란 없앰. 표 「에이전트가 볼 수 있는 접속」 = 확인란 + 이름·환경·실효 수준(글자만), 운영 줄 「(외부 상한 · 고르려면 접속 이름을 다시 입력)」, 링크 「수준 바꾸기: 설정 > AI 의 접속 표 열기」(설정 창 위에서 열었으면 닫기만, 팔레트에서 열었으면 설정 「AI」 탭을 엶), 파란 띠 「이 창에서는 「이번 연결에 쓸 접속」만 고릅니다…」. 접속 설정을 쓰지 않는다 | `ExternalAgentConnectViewModel.cs`, `ExternalAgentConnectWindow.cs`, `ExternalAgentConnectCommands.cs`, `ExternalAgentText.cs` |
| 상태 표시줄 띠 | 띠를 눌러 나오는 목록 맨 아래 「설정 › AI 의 「밖의 AI 에이전트」 열기」 → 탭 ③ 을 펼쳐 화면에 들임 | `ExternalAgentStatusView.cs`, `AiSettingsNavigation.cs` |
| 글 | 모두 뷰모델이 한/영(코드로 만든 화면). 스킬 시작 안내·`orandactl` 연결 없음 안내를 「도구 > AI 설정… → 밖의 AI 에이전트 → 연결…」 로 | `OrandaSkillDocument.cs`, `CliText.cs` |
| 매뉴얼·그림 | `ai/models.md` 「설정 창의 AI 탭」(①②③·수준 표·연결 전·막힘)·「고급 설정」, `ai/setup.md` 「AI 를 쓸 접속」(같은 표), `ai/external-agents.md`(연결 창은 고르기만·「조회 허용」 없어짐), 그림 `ai-settings-tab`(두 연결이 ① 을 함께 쓰는 그림)·`ai-settings-start`·`ai-settings-blocked`·`ai-setup-access` | `site/src/content/docs/**`, `site/src/assets/screens/` |

### 7.2 시안과 다른 것

| 항목 | 시안 | 한 것 | 까닭 |
|---|---|---|---|
| 운영 외부 수준 | 「prod-erp 메타데이터 (외부 상한)」 | 지금 빌드는 「조회 실행」, 「(외부 상한)」 은 상한이 실제로 낮출 때만 | 운영 외부 상한은 `AgentGrantPolicy.ProductionRuns`(D5 확정, 켬)를 따라 「조회+행 값」이다. min(조회 실행, 조회+행 값) = 조회 실행. 스위치를 끈 빌드에서만 메타데이터(외부 상한)가 된다. 값은 정책 함수 그대로 쓰고 두 경우를 시험으로 고정했다(`AiConnectionAccessTests`) |
| ③ 의 운영 SELECT 스위치 | 「있으면 둔다」 | 두지 않음 | 사용자 설정이 아니라 코드 상수(`ProductionRuns`)다 |
| 운영 줄 「조회+행 값」 점선 | 점선 | 꺼진 단추(투명 바탕·옅은 테두리) | Avalonia 단추 테두리에 점선 꼴이 없다. 눌리지 않고 옆에 「운영은 「조회 실행」까지」 를 둔다 |
| 상태 표시줄 띠를 누르면 ③ 으로 | 누르면 곧바로 | 누르면 나오는 목록(최근 호출·끊기·연장·스킬 다시 설치)의 맨 아래 링크 | 띠를 누르는 동작은 이미 최근 호출 목록을 여는 데 쓰인다. 곧바로 옮기면 그 목록·「끊기」 가 없어진다 |
| 탭의 「연결 시험」 대상 | — | 저장된 설정의 대상 | 카드는 저장된 값을 본다(1회차와 같음) |

### 7.3 사용자 확인표 추가 (macOS)

7. **수준 단추**: ① 에서 개발 접속을 「조회+행 값」 으로 바꾼 뒤, 연결 창(③ 「연결…」)을 열면 그 줄이 「조회 + 행 값」 으로 보이는지. 연결 창이 열린 채 접속 편집에서 바꾸면 탭 ① 이 따라 바뀌는지.
8. **운영 이름 재입력**: 운영 접속을 「끔」 에서 「계획」 으로 올리면 이름 칸이 나오고, 이름을 맞게 쳐야 저장되는지.
9. **CLI 연결 시험**: Codex 또는 Antigravity 를 고른 PC 에서 「연결 시험」 → 성공/로그인 필요/시간 초과 글.
10. **버리기 질문**: 고급 설정을 바꾸고 저장하지 않은 채 「다시 연결 (도우미)」 → 끝낸 뒤 질문 창이 설정 창 위에 뜨는지.
11. **상태 표시줄 띠**: 외부 에이전트 연결 뒤 띠 → 목록 맨 아래 링크 → 설정 「AI」 탭 ③ 이 펼쳐져 보이는지.
12. **그림**: `ai-settings-tab`·`ai-settings-start`·`ai-settings-blocked`·`ai-setup-access` 도 macOS 에서 다시 찍기(위 6 의 명령에 `ManualAiSetupScreens.Setup_access` 를 더한다).

## 8. 바인드 결정 ①~③ (같은 날, 사용자 「권장대로」, 커밋은 9절)

- ① 형식 지정: `{"value": …, "type": "varchar2|number|date|timestamp"}`. 줄이 없거나 형식을 아직 고르지 않은 줄(빈 값·VARCHAR2)이면 그 형식, 사람이 다른 형식을 골라 둔 줄이면 `bind_invalid`(말없이 바꾸지 않음). 형식이 없으면 예전 규칙. 스킬은 날짜를 `type: date` 로 보내라고 적는다.
- ② 운영 감사: `set_binds`·`run_tab --binds-json`·`query --binds-json` 모두 운영 접속이면 이름·형식만(`binds={PAK_PST_DD:date}`). 탭 도구는 접속을 모르는 호출(없는 탭·허가 밖 탭)도 이름만 — 운영일 수 있어서. 개발·시험은 예전대로.
- ③ 이름 노출: 탭 글을 읽을 수 없는 허가면 `set_binds`·`run_tab --binds-json` 을 `get_tab` 과 같은 거부(운영+클라우드 `environment` 등)로 이름 검사보다 먼저 막는다. 있는 이름·없는 이름이 같은 답. 예전 「보낸 이름만 돌려준다」 는 없어졌다.
- 계약 [agent-cli-contract.md](agent-cli-contract.md) §9.4, 매뉴얼 `ai/external-agents.md`, 스킬 가이드에 적었다.
- 「형식을 아직 고르지 않은 줄」 판정(빈 값 + VARCHAR2)은 내가 정한 것이다: 바인드 목록 줄에는 「형식 없음」 이 없고, 실행이 처음 보는 이름으로 만든 줄은 VARCHAR2·빈 값이다. 사람이 VARCHAR2 를 일부러 고르고 값을 비워 둔 줄도 같은 모양이라 그 줄은 에이전트 형식으로 바뀐다.

## 9. 2회차 시험·커밋

| 갈래 | 커밋 |
|---|---|
| 공유 수준(비UI) | `c70158e0`(공유 값·서비스) · `c1cc1244`(도우미 단계·연결 창을 한 길로) |
| 탭 화면(시안) | `b3c06d72`(탭 ①②③·공유 표·CLI 연결 시험·버리기 질문·고급 링크 삭제·띠 링크) · `44f4c083`(연결 창: 고르기만·링크) · `bf8652b3`(매뉴얼·스킬·CLI 안내·그림) |
| 바인드 ①~③ | `a84b68b2`(① 형식) · `baa4b6a7`(② 운영 감사) · `4e784918`(③ 읽기 관문) · `baac1146`(계약·매뉴얼·스킬) |

| 프로젝트 | 결과(마지막 전체 실행, 바인드 포함) |
|---|---|
| `Oranda.Core.Tests` | 통과 3,560 · 실패 0 |
| `Oranda.App.Tests` | 통과 7,389 · 실패 0 · 건너뜀 69 |
| `Oranda.Platform.Tests` | 통과 1,196 · 실패 0 · 건너뜀 36 |
| `Oranda.Agent.Cli.Tests` | 통과 187 · 실패 0 |
| `Oranda.Language.Tests` | 통과 1,550 · 실패 0 |
| `Oranda.Architecture.Tests` | 통과 31 · 실패 0 |

- 새·고친 시험: `AiSettingsHubTests`(15개: 상태별 큰 칸·접힘, ②·③ 회색 띠와 ① 에서 바꾸면 같이 바뀜, 외부 상한 표시, 대상별 실패 글, 도우미 뒤 값·버리기 질문, CLI 시험 기록, 스킬 상태, 연결됨 화면의 번호·단추 10개·운영 한도, 연결 전 화면, 설정 창 위 도우미, 영어), `AiConnectionAccessTests`(운영 기본 = 조회 실행 / 외부 = 스위치에 따른 min), `SharedAiAccessServiceTests`·`SharedAiAccessWiringTests`, `ExternalAgentConnectTests`(고르기만·수준 글자·「조회 허용」 없음), `ExternalAgentStatusTests`(목록 맨 아래 링크), `AiSetupEntryTests`(고급 링크 없음), `ExternalBindsAuditTests`(운영 이름·형식만, 개발 값 유지, 접속 모름 = 이름만, query 도 같음), `SetBindsHandlerTests`(형식 있는 값·형식 충돌·잘못된 형식, 읽을 수 없는 허가 = environment 로 같은 답, run_tab 날짜 형식), `OrandaSkillDocumentTests`(날짜 type date 안내).
- 주의: 2회차 중 한 번 시험 프로젝트 빌드가 분석기 오류(xUnit2017)로 실패한 채 `--no-build` 로 옛 DLL 을 돌려 「통과」 를 본 일이 있다. 고친 뒤 다시 빌드해 실패 13건을 찾아 고쳤고, 위 수치는 새로 빌드한 뒤의 전체 실행이다.

