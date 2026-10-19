# UI 셸 계약 — TSK-02-05

> 근거: TRD 4.10·4.11·6장 끝 문단(확장 지점은 Core 에 둔다) / [ui-shell.md](ui-shell.md) / [commands.md](commands.md) / [data-model.md](data-model.md) 2.4·2.5.
> 이 문서를 계약으로 쓰는 Task: TSK-03-01(셸 구현), 도구 창·명령·상태 표시줄 구간을 가진 기능 Task 전부.
> 계약 전용이다. 실행 로직(메뉴 트리·도킹·문맥 키 계산·키 입력 처리)은 TSK-03-01, 키맵 겹치기·저장은 TSK-04-07.

## 1. 계약 요약

### 1.1 Core (`src/Oranda.Core/Shell/`, 네임스페이스 `Oranda.Core.Shell`, Avalonia 미참조)

| 형식 | 파일 | 뜻 |
|---|---|---|
| `ICommandRegistry` | `Commands.cs` | 명령 목록·찾기(ID·별칭)·`CanExecute`·`ExecuteAsync`·실행 중 등록(`Register` → `IDisposable`)·`CommandsChanged` |
| `CommandDefinition` | `Commands.cs` | `Id`·`Title`(리소스 키, 기본 `Cmd.<Id>`)·`Category`·`Icon`·`When`(기본 키 범위)·`Enablement`·`Placements`·`Aliases`·`ShowInPalette`. **기본 단축키는 없다**(키맵에만) |
| `CommandCategory` | `Commands.cs` | 범주 13종 = 메뉴 묶음(commands.md 3장) |
| `ICommandHandler`, `CommandContext`, `IContextKeys` | `Commands.cs` | 처리기 1메서드 `ExecuteAsync(CommandContext, CancellationToken)`. `CommandContext.CommandId` = 정식 ID(별칭 아님). `IContextKeys.Evaluate(식)` 구현은 TSK-03-01 |
| `IToolPanelRegistry` | `ToolPanels.cs` | 도구 창 목록(탑재 순서)·`Find`·실행 중 등록(확장 패널)·`PanelsChanged` |
| `ToolPanelDefinition`, `ToolPanelKind`, `DockLocation` | `ToolPanels.cs` | ID·이름(기본 `Panel.<Id>`)·kind·기본 위치·ViewModel 형식·기본 크기·여러 개·순서·아이콘·개발 빌드 전용 |
| `IStatusBarItem`, `StatusBarItemDefinition`, `IStatusBarRegistry` | `StatusBar.cs` | 상태 표시줄 구간(ui-shell.md 1.2). `Order`·`CollapseRank`(0 = 숨기지 않음, 클수록 먼저 숨김) |
| `OrandaCommandAttribute`, `OrandaToolPanelAttribute`, `OrandaStatusBarItemAttribute` | `Attributes.cs` | 자동 등록 특성(2장) |
| `Keymap`, `KeyBinding` | `Keymap.cs` | 키맵 파일(data-model 2.4) + `Mac`(string[]?, commands.md 4장). `KeyBinding.KeysFor(macOS)` = `Mac ?? Keys` |

### 1.2 App (`src/Oranda.App/Shell/`, 네임스페이스 `Oranda.App.Shell…`)

| 형식 | 파일 | 뜻 |
|---|---|---|
| `ShellContributions.Scan(params Assembly[])` | `ShellContributions.cs` | 특성을 단 클래스를 모은다. 특성을 달았는데 계약 인터페이스(`ICommandHandler`·`IStatusBarItem`)를 구현하지 않으면 예외 |
| `CommandRegistry` | `CommandRegistry.cs` | `ICommandRegistry` 구현 틀. 처리기는 처음 실행할 때 팩터리(기본 `Activator`, TSK-03-01 이 DI 로 바꿈)로 만든다. ID·별칭 겹침은 예외 |
| `ToolPanelRegistry`, `StatusBarRegistry` | `ToolPanelRegistry.cs` | 구현 틀. 탑재 순서 = 도구 창: `DockLocation` 값(Document→Left→Bottom→Right) → `Order` → ID / 구간: `Order` → ID. 릴리스 빌드는 `DevBuildOnly` 제외 |
| `ToolPanelViewModelBase` | `ToolPanelViewModelBase.cs` | 도구 창 ViewModel 바탕(`ViewModelBase` 상속) |
| `KeymapLoader` | `Keymaps/KeymapLoader.cs` | `PresetIds`, `LoadPreset(id)`, `Parse(Stream)`, `Serialize(Keymap)`. schemaVersion ≠ 1 이면 `InvalidDataException`(상향 변환은 `ISettingsMigration` 이 앞에서) |
| `KeyChord` | `Keymaps/KeyChord.cs` | 키 표기 ↔ Avalonia `KeyGesture`. `Primary` = Windows Ctrl / macOS Cmd(`Meta`). `IsNormalized` = 수식키 순서 검사 |
| `FocusScopes` | `Keymaps/FocusScopes.cs` | `when` 포커스 키 → 포커스 자리(commands.md 2.2), `Overlaps` = 충돌 판정(2.3) |
| `LayoutDocument`, `WindowPlacement`, `DockNode`, `WindowStateKind`, `DockNodeKind`, `DockOrientation` | `Layout/LayoutDocument.cs` | `layout.json` DTO(data-model 2.5) |
| `ShellJsonContext` | `ShellJsonContext.cs` | `Keymap`·`LayoutDocument` 소스 생성 직렬화(data-model 1.3 옵션) |

- App 코드에서 `KeyBinding` 은 `Avalonia.Input.KeyBinding` 과 이름이 겹친다 → `using KeyBinding = Oranda.Core.Shell.KeyBinding;` 로 쓴다.
- 키맵 프리셋 4종은 `docs/design/keymaps/*.json` 을 **복사하지 않고 링크**해 임베디드 리소스(`Oranda.App.Keymaps.{id}.json`)로 넣는다(`Oranda.App.csproj`). 정본이 하나라 문서와 앱이 어긋나지 않는다. 프리셋을 고치는 Task(TSK-04-07)는 그 JSON 만 고친다.

## 2. 자동 등록 방법

셸 파일·공유 진입점에 줄을 더하지 않는다. 특성만 달면 셸이 시작 때 `ShellContributions.Scan` 으로 모은다.

### 2.1 도구 창

```csharp
[OrandaToolPanel("oranda.panel.history", ToolPanelKind.Tool, DockLocation.Bottom, Order = 40)]
public sealed class HistoryViewModel : ToolPanelViewModelBase { }
```

- 같은 폴더·네임스페이스에 `HistoryView.axaml(.cs)`(UserControl)를 둔다. View 는 이름 규칙(`…ViewModel` → `…View`)으로 `ViewLocator` 가 찾는다. 공유 시험이 「모든 등록 도구 창에 View 가 있다」를 검사한다.
- ID·기본 위치·순서는 ui-shell.md 2.2 표. 문서 종류는 `ToolPanelKind.Document` + `DockLocation.Document`(공유 시험이 짝을 검사), 여러 개면 `AllowMultiple = true`.
- 개발 빌드 전용은 `DevBuildOnly = true`.

### 2.2 명령

```csharp
[OrandaCommand("oranda.exec.runStatement", CommandCategory.Sql,
    When = "sqlDocFocus", Enablement = "hasSession && !sessionBusy",
    Placements = ["menu:sql/run@10", "toolbar:main/run@10", "context:editor/run@10"])]
public sealed class RunStatementCommand : ICommandHandler { … }
```

- 한 처리기가 여러 명령을 맡으면 특성을 여러 번 달고 `context.CommandId` 로 나눈다.
- ID 를 바꿔야 하면 `Aliases = ["옛 ID"]`.
- 확장(1.0 이후)은 `ICommandRegistry.Register`·`IToolPanelRegistry.Register` 로 실행 중에 붙이고, 반환값 Dispose 로 뗀다.

### 2.3 상태 표시줄 구간

```csharp
[OrandaStatusBarItem("oranda.status.transaction", 3)]          // 안전 정보: CollapseRank 0
public sealed class TransactionStatusViewModel : ViewModelBase, IStatusBarItem { public bool IsVisible => …; }
```

ui-shell.md 1.2 순서 그대로 `Order` 1~7. 좁을 때 숨김 순서(7 → 5 → 4)는 `CollapseRank` = 3·2·1, 나머지는 0. 구간 틀 파일은 구간을 가진 Task 가 자기 폴더에 만든다.

### 2.4 스캔 대상과 시작 시간

- 대상 어셈블리: `Oranda.App`(기능 폴더 전부). 확장 어셈블리가 생기면 셸이 목록에 더한다.
- 레지스트리를 만들고 셸에 붙이는 일(App 시작 배선, DI)은 **TSK-03-01** 이 한다. 이 Task 는 시작 경로를 바꾸지 않았다(D-006 시작 시간). TSK-03-01 은 스캔을 첫 프레임 뒤로 미루거나 스캔 비용을 계측해 판단한다.
- 처리기·도구 창 ViewModel 은 처음 쓸 때 만든다(지연 생성).

## 3. 시험

| 시험 파일 | 검사 |
|---|---|
| `tests/Oranda.App.Tests/Shell/Contract/RegistryContractTests.cs` | 레지스트리가 Core 계약 구현 / 시험 어셈블리의 가짜 도구 창·명령·상태 구간이 스캔으로 수집 / 탑재 순서 / ID·별칭 겹침 예외 / 개발 빌드 전용 제외 / 실행 중 등록 해제 / App 의 모든 등록 도구 창이 ID 규칙·바탕 형식·View 짝을 지킴 |
| `tests/Oranda.App.Tests/Shell/Contract/KeymapLoaderTests.cs` | 프리셋 4종 파싱, 명령 ID·`when` 값 형식, 키 표기 정규 순서·두 플랫폼 파싱, **포커스 자리가 겹치는 같은 키 충돌 0(Windows·macOS 각각)**, `mac` 필수 규칙(Ctrl+Break·Option+문자), macOS 예약 키 회피, `Primary` 변환, 왕복·버전 거부 |
| `tests/Oranda.App.Tests/Shell/ShellTestHelpers.cs` | **공용 시험 헬퍼**: `AppContributions`, `CreatePanelRegistry()`, `CreateCommandRegistry()`, `RequirePanel(id)`, `FakeContextKeys` |

- 공유 시험은 틀의 존재·탑재 순서·규칙만 단정한다. 도구 창 개수·명령 개수·빈 틀 목록은 단정하지 않는다(기능 Task 가 늘려도 깨지지 않게).
- 기능 Task 는 자기 시험 파일에서 `ShellTestHelpers.RequirePanel("oranda.panel.…")` 로 자기 도구 창 등록을 확인할 수 있다.

## 기능 Task 편집 지점

- 규칙: 한 Task = 한 폴더 `src/Oranda.App/Features/<폴더>/` (안의 파일 전부 그 Task 소유) + 시험 파일 하나. 두 Task 가 한 파일을 나눠 갖지 않는다. 파일을 더 만들 때도 자기 폴더 안에 만든다.
- 「명령 처리기」 = `<폴더>Commands.cs`(빈 자리 표시 클래스). 처리기 클래스는 이 파일이나 같은 폴더의 새 파일에 둔다.
- 결과 그리드는 도구 창이 아니라 SQL 편집기 문서의 아래 영역이다(ui-shell.md 1장). 그래서 SQL 편집기 문서(TSK-04-02)와 결과 영역(TSK-06-03)을 별도 ViewModel·View 로 나눴다. 편집기 문서 View 가 결과 영역 View 를 품는 연결은 TSK-04-02 가 자기 파일에서 한다.

| Task | 소유 파일 (View / ViewModel / 명령 핸들러) | 시험 파일 |
|---|---|---|
| TSK-03-01 앱 셸 | `src/Oranda.App/Shell/**`(1.2 의 계약 틀 포함), `Shell/ShellCommands.cs`, `src/Oranda.App/App.axaml(.cs)`, `ViewLocator.cs`, `ViewModels/**`, `Views/**` | `tests/Oranda.App.Tests/MainViewModelTests.cs` |
| TSK-03-04 접속 관리자 화면 | `Features/ConnectionManager/ConnectionManagerCommands.cs` | `tests/Oranda.App.Tests/Features/ConnectionManager/ConnectionManagerTests.cs` |
| TSK-03-05 접속 가져오기·명령줄 접속 | `Features/ConnectionImport/ConnectionImportCommands.cs` | `tests/Oranda.App.Tests/Features/ConnectionImport/ConnectionImportTests.cs` |
| TSK-04-02 SQL 편집기 화면 | `Features/SqlEditor/SqlEditorCommands.cs`<br>`Features/SqlEditor/SqlEditorViewModel.cs` · `SqlEditorView.axaml(.cs)` (`oranda.panel.sqlEditor`)<br>`Features/SqlEditor/OutlineViewModel.cs` · `OutlineView.axaml(.cs)` (`oranda.panel.outline`) | `tests/Oranda.App.Tests/Features/SqlEditor/SqlEditorTests.cs` |
| TSK-04-05 문맥 자동완성 | `Features/Completion/CompletionCommands.cs` | `tests/Oranda.App.Tests/Features/Completion/CompletionTests.cs` |
| TSK-04-06 정의로 이동·객체 정보·참조 찾기 | `Features/Navigation/NavigationCommands.cs` | `tests/Oranda.App.Tests/Features/Navigation/NavigationTests.cs` |
| TSK-04-07 코드 조각·템플릿·키맵 프리셋·단축키 사용자 정의 | `Features/Templates/TemplatesCommands.cs`<br>`Features/Templates/TemplatesViewModel.cs` · `TemplatesView.axaml(.cs)` (`oranda.panel.templates`)<br>`docs/design/keymaps/*.json`(프리셋 정본) | `tests/Oranda.App.Tests/Features/Templates/TemplatesTests.cs` |
| TSK-05-03 포매터 프로필 화면 | `Features/FormatterProfiles/FormatterProfilesCommands.cs`<br>`Features/FormatterProfiles/FormatterProfilesViewModel.cs` · `FormatterProfilesView.axaml(.cs)` (`oranda.panel.formatterProfiles`) | `tests/Oranda.App.Tests/Features/FormatterProfiles/FormatterProfilesTests.cs` |
| TSK-06-02 SQL 실행 화면·운영 안전장치 표시 | `Features/Execution/ExecutionCommands.cs`<br>`Features/Execution/OutputViewModel.cs` · `OutputView.axaml(.cs)` (`oranda.panel.output`)<br>`Features/Execution/MessagesViewModel.cs` · `MessagesView.axaml(.cs)` (`oranda.panel.messages`) | `tests/Oranda.App.Tests/Features/Execution/ExecutionTests.cs` |
| TSK-06-03 가상 결과 그리드 | `Features/ResultGrid/ResultGridCommands.cs`<br>`Features/ResultGrid/SqlResultPaneViewModel.cs` · `SqlResultPaneView.axaml(.cs)` (도구 창 아님) | `tests/Oranda.App.Tests/Features/ResultGrid/ResultGridTests.cs` |
| TSK-06-04 그리드 편집 | `Features/GridEdit/GridEditCommands.cs` | `tests/Oranda.App.Tests/Features/GridEdit/GridEditTests.cs` |
| TSK-06-05 LOB 편집기·행 보기 | `Features/LobRowView/LobRowViewCommands.cs`<br>`Features/LobRowView/LobEditorViewModel.cs` · `LobEditorView.axaml(.cs)` (`oranda.panel.lobEditor`)<br>`Features/LobRowView/RowViewerViewModel.cs` · `RowViewerView.axaml(.cs)` (`oranda.panel.rowView`) | `tests/Oranda.App.Tests/Features/LobRowView/LobRowViewTests.cs` |
| TSK-06-06 실행 이력·이름 붙인 스크립트 | `Features/HistoryScripts/HistoryScriptsCommands.cs`<br>`Features/HistoryScripts/ScriptsViewModel.cs` · `ScriptsView.axaml(.cs)` (`oranda.panel.scripts`)<br>`Features/HistoryScripts/HistoryViewModel.cs` · `HistoryView.axaml(.cs)` (`oranda.panel.history`) | `tests/Oranda.App.Tests/Features/HistoryScripts/HistoryScriptsTests.cs` |
| TSK-06-07 내보내기·가져오기 | `Features/DataTransfer/DataTransferCommands.cs` | `tests/Oranda.App.Tests/Features/DataTransfer/DataTransferTests.cs` |
| TSK-07-01 실행 계획 | `Features/ExecutionPlan/ExecutionPlanCommands.cs`<br>`Features/ExecutionPlan/PlanViewModel.cs` · `PlanView.axaml(.cs)` (`oranda.panel.plan`) | `tests/Oranda.App.Tests/Features/ExecutionPlan/ExecutionPlanTests.cs` |
| TSK-07-02 스키마 브라우저 | `Features/SchemaBrowser/SchemaBrowserCommands.cs`<br>`Features/SchemaBrowser/SchemaBrowserViewModel.cs` · `SchemaBrowserView.axaml(.cs)` (`oranda.panel.schemaBrowser`) | `tests/Oranda.App.Tests/Features/SchemaBrowser/SchemaBrowserTests.cs` |
| TSK-07-03 객체 상세·DDL 보기 | `Features/ObjectDetail/ObjectDetailCommands.cs`<br>`Features/ObjectDetail/ObjectDetailViewModel.cs` · `ObjectDetailView.axaml(.cs)` (`oranda.panel.objectDetail`) | `tests/Oranda.App.Tests/Features/ObjectDetail/ObjectDetailTests.cs` |
| TSK-07-04 객체 관리·검색·DB 정보·DDL 일괄 추출 | `Features/DbTools/DbToolsCommands.cs`<br>`Features/DbTools/DbInfoViewModel.cs` · `DbInfoView.axaml(.cs)` (`oranda.panel.dbInfo`)<br>`Features/DbTools/ObjectSearchViewModel.cs` · `ObjectSearchView.axaml(.cs)` (`oranda.panel.objectSearch`)<br>`Features/DbTools/DdlExtractViewModel.cs` · `DdlExtractView.axaml(.cs)` (`oranda.panel.ddlExtract`) | `tests/Oranda.App.Tests/Features/DbTools/DbToolsTests.cs` |
| TSK-08-01 PL/SQL 소스 편집·컴파일 | `Features/PlsqlEditor/PlsqlEditorCommands.cs`<br>`Features/PlsqlEditor/PlsqlEditorViewModel.cs` · `PlsqlEditorView.axaml(.cs)` (`oranda.panel.plsqlEditor`)<br>`Features/PlsqlEditor/CompileErrorsViewModel.cs` · `CompileErrorsView.axaml(.cs)` (`oranda.panel.compileErrors`) | `tests/Oranda.App.Tests/Features/PlsqlEditor/PlsqlEditorTests.cs` |
| TSK-08-02 PL/SQL 실행 대화상자 | `Features/PlsqlRun/PlsqlRunCommands.cs` | `tests/Oranda.App.Tests/Features/PlsqlRun/PlsqlRunTests.cs` |
| TSK-09-01 세션 모니터 | `Features/SessionMonitor/SessionMonitorCommands.cs`<br>`Features/SessionMonitor/SessionMonitorViewModel.cs` · `SessionMonitorView.axaml(.cs)` (`oranda.panel.sessionMonitor`) | `tests/Oranda.App.Tests/Features/SessionMonitor/SessionMonitorTests.cs` |
| TSK-09-02 SQL 모니터 | `Features/SqlMonitor/SqlMonitorCommands.cs`<br>`Features/SqlMonitor/SqlMonitorViewModel.cs` · `SqlMonitorView.axaml(.cs)` (`oranda.panel.sqlMonitor`) | `tests/Oranda.App.Tests/Features/SqlMonitor/SqlMonitorTests.cs` |
| TSK-10-01 설치·업데이트·충돌 보고·사용자 문서 | `Features/Help/HelpCommands.cs` | `tests/Oranda.App.Tests/Features/Help/HelpTests.cs` |
| TSK-10-02 다국어·접근성 | `Features/Localization/LocalizationCommands.cs` | `tests/Oranda.App.Tests/Features/Localization/LocalizationTests.cs` |
| TSK-10-03 AI 확장 지점 시험 패널 | `Features/ExtensionTest/ExtensionTestCommands.cs`<br>`Features/ExtensionTest/ExtensionTestViewModel.cs` · `ExtensionTestView.axaml(.cs)` (`oranda.panel.extensionTest`, 개발 빌드만) | `tests/Oranda.App.Tests/Features/ExtensionTest/ExtensionTestTests.cs` |

`Features/…` = `src/Oranda.App/Features/…`. 위 표의 파일은 모두 지금 있다(빈 틀).

## 4. 결정

| 결정 | 이유 |
|---|---|
| 특성 이름은 `OrandaCommand`·`OrandaToolPanel`·`OrandaStatusBarItem` | commands.md 2.1 표기와 맞추고, `Avalonia`·BCL 의 `Command` 이름과 겹치지 않게 |
| `Keymap`·`KeyBinding` 은 `Oranda.Core.Shell` 에 둔다 | 셸 계약 파일을 한 폴더에 모은다. data-model 6.1 은 `Oranda.Core.Commands` 로 적혀 있어 문서 수정 요청 |
| 키맵 프리셋은 링크한 임베디드 리소스 | 정본 하나. 복사본이 어긋날 일이 없다 |
| 직렬화는 App 의 `ShellJsonContext`(소스 생성) | Core `OrandaJsonContext` 는 TSK-02-03 몫. 그 컨텍스트가 생기면 `Keymap` 등록을 더하고 이쪽은 `LayoutDocument` 만 남겨도 된다 |
| `Dock.Avalonia` 패키지는 아직 넣지 않는다 | 계약은 도킹 형식을 쓰지 않는다. TSK-03-01 이 착수 때 그 시점 12.1.x 최신으로 `Directory.Packages.props` 에 넣는다(ui-shell.md 3장 조건 3) |
| App 시작 배선(스캔·레지스트리 생성)은 하지 않음 | 시작 시간(D-006) 측정과 함께 TSK-03-01 이 한다 |
