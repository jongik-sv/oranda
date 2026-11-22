# AI 연결 도우미 — 공유 계약 (W1)

- 작성일: 2026-11-23 (회차 ai-onboard-1123, 레인 w-ai-setup-core)
- 요구 = 회차 handoff(맥 인계). 이 문서는 **코드로 정한 값**만 적는다. 다른 레인은 바꾸지 않는다(모자라면 `계약 요청:`).

## 1. 네임스페이스

| 층 | 네임스페이스 | 폴더 | 주인 |
|---|---|---|---|
| Core | `Oranda.Core.Ai.Setup` | `src/Oranda.Core/Ai/Setup/` | w-ai-setup-core |
| Platform | `Oranda.Platform.Ai.Setup` | `src/Oranda.Platform/Ai/Setup/` | l-ai-setup-cli (`IClaudeSetupService` 구현) |
| App 계약 | `Oranda.App.Features.AiSetup.Contract` | `src/Oranda.App/Features/AiSetup/Contract/` | w-ai-setup-core |
| App 단계 | `Oranda.App.Features.AiSetup.<Wizard·Choose·Install·Login·Test·Server·Access·TryIt·AskAdmin>` | 같은 이름 폴더 | 각 레인 |
| App 진입점 | `Oranda.App.Features.AiSetup.Entry` | `src/Oranda.App/Features/AiSetup/Entry/` | w-ai-setup-core (W3) |

- `Features.AiSetup` 은 `FeatureCatalog.NamespaceTags` 에 없다 → **공통(늘 켬)**. `Features.Ai` 와 세그먼트가 달라 aiTuning 을 꺼도 숨지 않는다(시험 `AiSetupContractTests`).

## 2. 단계

| 갈래 `AiSetupPath` | 단계 순서 `AiSetupFlow.StepsFor(path)` |
|---|---|
| (고르기 전 `null`) | `Choose` |
| `ClaudeAccount` | `Choose → Install → Login → Test → Access → TryIt` |
| `CompanyServer` | `Choose → Server → Test → Access → TryIt` |
| `NotSure` | `Choose → AskAdmin` |

이동 규칙(`AiSetupFlow`, 화면 없는 모델):

- `CanGoNext` = 지금 단계 끝남 + 뒤 단계 있음. `CanGoBack` = 첫 단계 아님. 마지막 단계는 `CanFinish`(끝남이면 [끝내기]).
- `ChoosePath(p)`: `Choose` 를 끝남으로. **다른 갈래로 바꾸면 끝남을 모두 비우고 지금 단계 = `Choose`**. 같은 갈래면 끝남·지금 단계를 그대로 둔다(되살린 뒤 `context.ChosenPath` 를 넣어도 이어 하기가 안 깨짐).
- `SetComplete(step, bool)`: 단계 화면의 `IsComplete` 를 옮긴다. 갈래에 없는 단계는 무시.
- `GoBack` 은 끝남을 지우지 않는다.
- 이어 하기: `AiSetupFlow.Restore(LastPath, LastStep)` → 그 단계에서 시작, 앞 단계는 끝남, 지금 단계는 끝나지 않음(다시 확인). 갈래·단계 중 하나라도 없거나 모르는 값이거나 갈래에 없는 단계 = 처음부터. 갈래 B 는 `LastProfileId` 를 `context.CreatedProfileId` 로 되살린다(목록에 없으면 null → 회사 AI 서버 단계가 다시 만들게 한다).
- 창 틀 순서: 단계를 바꿀 때 `LeaveAsync`(떠나는 단계) → `EnterAsync`(들어오는 단계). [끝내기] = 마지막 단계 `FinishAsync` → `LeaveAsync` → 모든 단계 `Dispose`. [닫기] = `LeaveAsync` → `Dispose`(이어 하기 값 저장). 단계의 `NextRequested` 는 그 단계가 끝남일 때만 옮긴다.
- 바뀔 때마다 `Changed` 이벤트.

## 3. 인터페이스

| 타입 | 파일 | 하는 일 | 구현 |
|---|---|---|---|
| `IClaudeSetupService` | `Core/Ai/Setup/IClaudeSetupService.cs` | `FindAsync` → `ClaudeInstallation?`(경로·판) · `InstallAsync(IProgress<string>?)` → `ClaudeInstallResult` · `ManualInstallCommand(AiHostPlatform)`(기본 구현 있음) · `StartLoginAsync` → `ClaudeLoginStart`(주소·끊기 손잡이 `IAsyncDisposable`) · `GetLoginStatusAsync` → `ClaudeLoginStatus`(로그인됨·계정 글·`StatusSupported`) · `TestConnectionAsync` → `AiSetupTestResult`(성공·걸린 시간·오류 종류) | l-ai-setup-cli L2·L3 |
| (위 표 보충) | | `StartLoginAsync(IProgress<string>? browserUrl, ct)`: 늦게 나온 주소는 progress 로. `ClaudeLoginStart.Exited`(`Task<int>?`) = 프로세스 종료 코드(끝남 ≠ 로그인됨, 상태 확인으로 다시 봄) | |
| `ClaudeInstallCommands` | 같은 파일 | `Windows` = `irm https://claude.ai/install.ps1 \| iex`, `Unix` = `curl -fsSL https://claude.ai/install.sh \| bash`, `ManualFor(platform)` | — |
| `IAiSetupLauncher` | `App/Features/AiSetup/Contract/AiSetupContracts.cs` | `OpenAsync(AiSetupEntry, AiSetupErrorKind? reason, ct)`(구현할 것) + `OpenAsync(entry, ct)`(기본 구현) — `Menu`·`FirstRun`·`FailureBand` | l-ai-setup-wizard U1 |
| `IAiSetupStep` | 같은 파일 | `StepId`·`Title`·`IsComplete`(PropertyChanged)·`View`(Control)·`NextRequested`·`EnterAsync`·`LeaveAsync`·`FinishAsync`(기본 구현: 없음)·`Dispose` | 각 단계 레인 |
| `IAiSetupStepFactory` | 같은 파일 | `StepId`·`Create(AiSetupContext)`. 등록 = `[OrandaService(typeof(IAiSetupStepFactory))]`, 창 틀은 `IEnumerable<IAiSetupStepFactory>` | 각 단계 레인 |
| `AiSetupContext` | `App/.../Contract/AiSetupContext.cs` | 부품: `Settings`(SettingsService)·`Entry`·`Reason`·`Text`(IShellText)·`IsEnglish`·`Connections`·`Claude`·`Probe`·`Credentials`·`Bus`·`Services`·`CopyText`·`OpenUrl`·`Platform`·`AppVersion`. 단계 사이 값: `ChosenPath`(+`ChosenPathChanged`)·`CreatedProfileId`·`EnabledConnectionIds` | 창 틀이 만듦 |
| `AiSetupIds` | `AiSetupContracts.cs` | `OpenCommand` = `oranda.ai.setup.open`, `WindowName` = `AiSetupWindow`, `ManualPage` = `ai/setup` | — |
| `AiSetupText` | `App/.../Contract/AiSetupText.cs` | 모두 `english` 를 받음: `T(english, ko, en)`·`WindowTitle`·`Back`·`Next`·`Finish`·`Close`·`StepTitle(step, english)`·`PathTitle(path, english)`. 단계는 `context.IsEnglish` 를 넘긴다. `CurrentIsEnglish` 는 맥락 없는 곳(메뉴·대화상자)만 | — |

규칙: 서비스 실패는 결과로(취소만 `OperationCanceledException`). 결과의 `Detail` 은 짧은 원문(로그·개발자용)이고 **진단 글에 넣지 않는다**(`AiSetupDiagnosticInfo` 에 원문 칸 없음 — 시험으로 고정). 화면 글은 `AiSetupErrorText.For(kind, english)` 로 고른다. 결과 글에 열쇠·주소·계정 비밀을 넣지 않는다. `ClaudeLoginStatus.AccountLabel` 은 화면에만 쓰고 진단 글에 넣지 않는다.

## 4. 오류 종류

| `AiSetupErrorKind` | 뜻 | 드라이버 코드(`AiSetupErrorCodes`) |
|---|---|---|
| `CliNotFound` | claude 없음 | `cli_not_found`(있음) |
| `NotLoggedIn` | 로그인 안 함 | `cli_not_logged_in`(새로, `CliDriver` 구현 = l-ai-setup-cli L3) |
| `Network` | 망에 닿지 못함 | — |
| `Timeout` | 제시간에 답 없음 | — |
| `ServerRejected` | 서버가 거절(열쇠·권한·모델) | — |
| `Unknown` | 그 밖 | 모르는 코드 전부 |

`AiSetupErrorCodes.KindFor(code)` 로 바꾼다. 갈래 B 연결 시험(`IAiProfileProbe.TestConnectionAsync`)의 결과는 `AiSetupErrorCodes.KindFor(AiConnectionTestResult)`(성공 = null, Platform `AiProfileProbe` 실패 글 머리로 가름: 제한 시간 → Timeout, 연결 못 함 → Network, 인증 거부·404·서버 오류·키 문자 → ServerRejected, 그 밖 Unknown). 쉬운 글 = `AiSetupErrorText.For(kind, english)`.

## 5. `.oranda-ai`

```json
{ "format": "oranda-ai", "version": 1, "name": "사내 AI", "baseUrl": "https://ai.example.com/v1", "apiFormat": "openai", "model": "big-model", "localAi": false }
```

- 읽기 = `AiSetupFile.Parse(text)` → `AiSetupFileResult(Profile, Error)`. 예외 없음. 확장자 `AiSetupFile.Extension`, 글 상한 64K 글자.
- 프로필 초안: `Id` = 빈 값(저장하는 쪽이 채움), `Preset` = `Custom`, `Format` = `apiFormat`(`openai`·`anthropic`, 대소문자 무시), `Name` 없으면 주소의 호스트, `Model` 없으면 빈 글(단계가 모델 새로고침), `localAi` 가 JSON `true` 일 때만 `LocalAiConfirmedUrl = baseUrl`.
- 거절(`AiSetupFileError`): `ContainsSecret`(칸 이름에 key·token·password·passwd·pwd·secret·credential·authorization·cookie·bearer — `_`·`-` 빼고 소문자, 깊이 상관없이, 가장 먼저 검사. `maxOutputTokens` 같은 `tokens` 는 비밀 아님) · `WrongFormat` · `UnsupportedVersion`(숫자 1 만) · `BadApiFormat` · `BadUrl`(`AiProfileUrl` 규칙: http/https, user:pw@·쿼리·조각 없음) · `NotReadable`. 쉬운 글 = `AiSetupFile.ErrorText(error, english)`.
- 모르는 칸은 무시. 배포 주체는 미정(형식·예시만).

## 6. 진단 글

| 함수 | 담는 것 |
|---|---|
| `AiSetupDiagnostics.ForTestFailure(info, english)` | Oranda 판·OS·방법·Claude Code 설치됨(판)/설치 안 됨·오류 종류 이름·시각(`yyyy-MM-dd HH:mm:ss zzz`) |
| `AiSetupDiagnostics.ForAdmin(info, english)` | 「무엇을 받으면 되는지」 한 줄(회사 Claude 계정 또는 `.oranda-ai`)·Oranda 판·OS·Claude Code 설치 여부 |

- 입력 = `AiSetupDiagnosticInfo(AppVersion, OsDescription, Path, ClaudeFound, ClaudeVersion, ErrorKind, At)`. 비밀이 들어올 칸이 없고, 판 칸은 `숫자.숫자(.숫자)` 만, OS 칸은 `:`·`/`·`@`·`\`·`=` 가 든 낱말을 빼고 60자까지 남긴다(시험: 가짜 열쇠·주소·IP·메일을 넣어도 안 나옴).

## 7. 설정 칸

`settings.json` 의 `ai.setup`(`AiSettings.Setup`, `AiSetupSettings`):

| 칸 | 형 | 뜻 |
|---|---|---|
| `firstRunPromptClosed` | bool | 첫 실행 묻기를 닫음(도우미 열기·나중에 둘 다). JSON `true` 일 때만 참 |
| `lastPath` | `AiSetupPath?`(이름 문자열) | 이어 하기 갈래 |
| `lastStep` | `AiSetupStepId?`(이름 문자열) | 이어 하기 단계 |
| `lastProfileId` | `Guid?` | 갈래 B 에서 만든 프로필(이어 하기) |

- 판독기 `TolerantAiSetupSettingsConverter`: 키 없음·null·객체가 아닌 값 = 기본값, 모르는 이름·숫자·깨진 id = null. 설정 파일 전체가 손상되지 않는다. `AiSettings.Equals` 에 포함.
- 저장 = `SettingsService.UpdateAsync(s => s with { Ai = s.Ai with { Setup = … } })`.

## 8. 매뉴얼·범위표

- 쪽 = `site/src/content/docs/ai/setup.md`(W1 뼈대, 게시 상태). 새 명령·창·대화상자를 만든 레인은 `site/scope.json` 에 `{"id": "…", "page": "ai/setup"}` 줄을 더한다.
- 쪽의 title·description·`##` 제목을 바꾸면 `src/Oranda.Core/Ai/Manual/OrandaManualIndex.cs` 를 `python3 tools/manual-index/gen.py` 로 다시 만든다(`ManualIndexTests`).
