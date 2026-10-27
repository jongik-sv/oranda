# TRD - Oranda MyBatis 작업 공간

> version: 1.0
> date: 2026-10-23
> 상위 TRD = `docs/TRD.md`(고치지 않음). 이 문서는 MyBatis 작업 공간(`docs/oranda1/PRD.md`)의 기술 결정만 다룬다.

## 1. 기술 스택

- 상위와 같음: .NET 10 SDK 10.0.402, Avalonia 12.1.4, CommunityToolkit.Mvvm, AvaloniaEdit(편집기), ODP.NET Core 23.26.301, 로컬 데이터 SQLite(Microsoft.Data.Sqlite)
- xml 읽기: System.Xml.XmlReader(`DtdProcessing.Ignore`, `XmlResolver = null`) + IXmlLineInfo 로 요소 시작 위치를 얻고, 원문 문자열을 직접 훑어 범위를 잡는다
- 새 외부 패키지는 쓰지 않는다

## 2. 계층과 위치

| 계층 | 위치 | 내용 |
|------|------|------|
| Language | `src/Oranda.Language/MyBatis/` | 매퍼 원문 모델(MyBatisMapperDocument), 동적 SQL 계산기(MyBatisEvaluator), test 식 해석기(OgnlLite), 파라미터 뽑기 |
| Core | `src/Oranda.Core/MyBatis/` | 시나리오 저장소 인터페이스(IMyBatisScenarioStore)와 모델 |
| Platform/App 데이터 | 로컬 SQLite | 시나리오 저장 구현 |
| App | `src/Oranda.App/Features/MyBatisWorkspace/` | 작업 공간 ViewModel, 「문장」·「파라미터 · 시나리오」 도구 창, 펼친 SQL·조합 검사 패널, 저장 확인·바꾸기 대화 상자, 명령 |

규칙: Language·Core 는 Avalonia·ODP.NET 을 참조하지 않는다. 실행은 IExecutionService(정책 관문)만 쓴다. UI 스레드에서 DB 를 부르지 않는다.

## 3. 데이터 모델

### 3.1 매퍼 원문 모델 (메모리)

- MyBatisMapperDocument: 원문 문자열, 인코딩·BOM·줄 끝·끝 줄바꿈 정보, namespace, 요소 목록, 원문 해시(SHA-256)
- MyBatisElement: 종류(select·insert·update·delete·sql·resultMap·기타), id, 속성 목록, 요소 범위(시작·끝 오프셋), 안쪽 본문 범위
- 편집: 바꾸기 목록(범위 → 새 글)을 뒤에서부터 적용해 새 원문을 만든다. 새 문장은 `</mapper>` 바로 앞, 앞 문장 들여쓰기를 따른다

### 3.2 계산 결과 (메모리)

- EvalResult: 펼친 SQL, 바인드 목록(이름·타입 힌트·값), 치환 목록(이름·값), 블록 목록(원문 범위·켬/끔·근거 식), 계산 못 한 식 목록(원문 범위·이유)
- 파라미터 정보: 이름(점 경로), 쓰인 곳(test·#{}·${}·foreach·bind), 타입 힌트(jdbcType·javaType)

### 3.3 시나리오 (SQLite 로컬 데이터)

- 표 `mybatis_scenario`: id, mapper_path(정규화한 절대 경로), namespace, statement_id, name, values_json, sort_order, created_at, updated_at
- 문장 id 를 바꾸면 statement_id 를 같이 바꾼다. 매퍼 파일을 옮기면 namespace + statement_id 로 다시 찾는다
- values_json 에 비밀 이름(pass·pwd·secret·token 이 든 이름) 값은 넣지 않는다

## 4. 인터페이스(계약)

- `IMyBatisMapperDocument Parse(string text)` / `string Apply(IReadOnlyList<MapperEdit> edits)` / `MapperWriteCheck Check(string newText, string diskHash)`
- `EvalResult Evaluate(MyBatisElement statement, IMyBatisFragmentResolver fragments, IReadOnlyDictionary<string, object?> values, IReadOnlyDictionary<TextRange, bool>? overrides)`
- `IReadOnlyList<MyBatisParameter> ExtractParameters(MyBatisElement statement, IMyBatisFragmentResolver fragments)`
- `IMyBatisScenarioStore`: List·Save·Rename·Delete·MoveStatement(mapperPath, oldId, newId)
- 조합 검사: `IEnumerable<ConditionCombination> Combinations(EvalResult allOn, int fullLimit = 8)`

## 5. 저장·파일 감시

- 원자적 쓰기: 같은 폴더 임시 파일에 쓰고 바꾸기(File.Replace / rename). 원래 인코딩·BOM·줄 끝 유지
- 쓰기 전 디스크 해시를 다시 읽어 연 때의 해시와 비교한다. 다르면 3방향 문장 단위 병합(연 때 원문 · 디스크 · 내 것)으로 안 고친 문장은 디스크 것, 양쪽 고친 문장만 묻는다
- FileSystemWatcher(디바운스 300 ms)로 밖의 변경을 알린다

## 6. 화면 배선

- 문서 종류 `MyBatis` 를 작업 공간 문서 종류에 더한다. 도구 창 보임 규칙은 TSK-13-41(문서별 도구 창) 규칙에 「MyBatis 문서」 조건을 더한다
- 명령은 `[OrandaCommand]` 특성(자동 수집)으로 등록한다: `oranda.mybatis.runScenario`(F9), `oranda.mybatis.save`(Ctrl+S, MyBatis 문서일 때), `oranda.mybatis.checkCombinations`, `oranda.mybatis.copyExpanded`, `oranda.mybatis.convertWorkspace`, `oranda.mybatis.openFolder`, `oranda.mybatis.findStatement`
- 편집기 강조: AvaloniaEdit 에 MyBatis 모드(XML 태그·속성, `#{}`·`${}`, SQL 구간은 기존 SQL 강조 위임)

## 7. 비기능 요구사항 (NFR)

- 매퍼 1MB 파싱 200 ms 이하, 계산 50 ms 이하(문장 500줄), 조합 256개 구문 분석 3 s 이하
- 계산기와 식 해석기는 반사(reflection)·동적 코드 생성을 쓰지 않는다
- 시험: Language 단위 시험(왕복·태그·식 골든), App Headless 시험(화면·명령), Oracle 통합 시험(실행·실행 계획) 1건 이상

## 8. 제약 사항

- 상위 문서를 고치지 않는다. 결정은 `docs/oranda1/decisions.md` 에 남긴다
- 공유 파일(SqlEditorHost·WorkspaceFiles·도킹 규칙)은 계약 Task 가 정한 편집 지점에서만 고친다

## 9. 수락 기준 (기술)

- MB-01 왕복 시험: BOM·CRLF·CDATA·주석·엔티티·한글·DOCTYPE 표본에서 무변경 저장 결과가 원본과 바이트까지 같다
- MB-02 골든 시험: 표본 문장 × 값 묶음의 펼친 SQL 이 MyBatis 3.5 실제 출력(공백 정규화)과 같다
- 7절 성능 수치를 시험 PC 에서 잰 값이 기준 이하다
- `dotnet build Oranda.sln -warnaserror` 와 `dotnet test` 가 통과한다

## 10. Role 기반 메뉴·기능 구성 (2026-10-25 추가, PRD 12, WP-07)

근거 = `docs/oranda1/research/role-features.md`(이하 「조사」). 줄 번호는 기준 커밋 a507d81 의 `src/Oranda.App/` 기준.

### 10.1 계층과 위치

| 계층 | 위치 | 내용 |
|---|---|---|
| Core | `src/Oranda.Core/Features/` | 기능 id·Role id 상수, `FeatureCatalog`(기능 목록·Role 기본값 표·꼬리표 규칙 데이터), `IFeatureGate` |
| Core | `src/Oranda.Core/Settings/FeaturesSettings.cs` | 설정 `features` 섹션 레코드(값 같음 비교) |
| Platform | `src/Oranda.Platform/Storage/SettingsStore.cs` | `features` 섹션 바뀜 알림·정규화 |
| App | `src/Oranda.App/Shell/Features/` | `FeatureGate`(해석·변경 알림·`feature.<id>` 문맥 키), 꼬리표 찾기(`FeatureMap`) |
| App | 각 거름 자리 | 명령·도구 창 레지스트리, 시작 작업, 상태 칸, 실행 정책, 설정 창, 대체 경로 |

규칙: Core 는 App 형을 참조하지 않는다(네임스페이스는 문자열로 적는다). 이미 있는 거르기 선례 = 개발 빌드 전용 패널(`Shell/ToolPanelRegistry.cs:20`)·`devBuild` 키(`Shell/ContextKeyService.cs:29-33`).

### 10.2 기능 꼬리표 (네임스페이스 기반, D-004)

명령(`[OrandaCommand]` 처리기 형)·도구 창(ViewModel 형)·상태 칸(항목 형)·시작 작업(구현 형)이 어느 기능에 드는지는 다음 순서로 정한다. 처음 맞는 규칙에서 끝난다. 어느 것에도 맞지 않으면 공통(늘 켬).

1. id 덮어쓰기(명령 id·패널 id): 아래 둘째 표
2. 형 전체 이름 덮어쓰기: 아래 둘째 표
3. 네임스페이스 접두 `Oranda.App.Features.<폴더>` 의 가장 긴 일치: 아래 첫째 표

| 네임스페이스 접두(`Oranda.App.Features.` 다음) | 기능 id |
|---|---|
| `GridEdit` | `gridEdit` |
| `LobRowView` | `lobRowView` |
| `DataTransfer` | `dataExport` |
| `DedicatedSession` | `dedicatedSession` |
| `Flashback` | `flashback` |
| `ChangeJournal` | `changeJournal` |
| `TableSpec` | `tableSpec` |
| `MyBatisLog` | `myBatisLog` |
| `PlsqlEditor` | `plsqlEditor` |
| `PlsqlWorkspace` | `plsqlEditor` (PL/SQL 편집 탭이 이 작업 공간 문서 패널 안에 산다. 저장·복원만 `plsqlWorkspace`, 10.7.3) |
| `PlsqlRun` | `plsqlRun` |
| `MyBatisWorkspace` | `myBatis` |
| `SqlMonitor` | `sqlMonitor` |
| `Tuning.Assistant` | `tuningAssistant` |
| `Tuning.Cursors` | `childCursors` |
| `Tuning.Stats` | `tableStats` |
| `Tuning.Trace` | `trace` |
| `Tuning.Report` | `tuningReport` |
| `SessionMonitor` | `sessionMonitor` |
| `LockMonitor` | `lockMonitor` |
| `BlockerAlert` | `blockerAlert` |
| `DbTools` | `dbTools` |
| `ColumnReorder` | `columnReorder` |

| 덮어쓰기 대상 | 기능 id | 이유 |
|---|---|---|
| 명령 `oranda.tools.import` | `dataImport` | 조회 전용은 내보내기만(조사 3절 D3) |
| 명령 `oranda.plan.compare`·`oranda.plan.pinForCompare`·`oranda.plan.displayCursorOptions` | `planAdvanced` | 예상·실제 계획·계획 창은 공통(조사 6.8, 4.1 「다」 T1) |
| 명령 `oranda.plsql.saveWorkspaceFile`·`oranda.plsql.addToWorkspace` | `plsqlWorkspace` | 작업 공간 파일 저장 |
| 명령 `oranda.plsql.openSource` | 공통 | PL/SQL 을 꺼도 「소스 열기」는 남고 읽기 전용 SQL 탭으로 연다(D-007) |
| 형 `Oranda.Platform.Journal.ChangeJournalRecorder` | `changeJournal` | Platform 어셈블리의 시작 작업 |
| 형 `Oranda.App.Features.GridEdit.SaveStatusViewModel` | 공통 | 저장 알림 칸 — 내보내기·튜닝 보고서·대체 열기 안내도 실어 gridEdit 와 무관(TSK-07-09) |

공용 형은 거르지 않는다: 꼬리표는 등록 항목(명령·패널·상태 칸·시작 작업)에만 붙는다. 그래서 세션 모니터를 꺼도 `MonitorAccessState`·`MonitorTableBinder`·`MonitorProfileDialog`(조사 4.1 「다」 A1)를 Lock·SQL 모니터·튜닝·블로커·접속 후 권한 키(`ConnectionManager/ConnectionManagerCommands.cs:69`)가 그대로 쓰고, 계획 모델·뷰(조사 4.1 「다」 T1)도 Lock 모니터·SQL 모니터·튜닝·MyBatis 가 그대로 쓴다. 실행 컨트롤러가 쓰는 컴파일 오류 형(`Features/Execution/ExecutionController*.cs`)도 그대로다.

### 10.3 설정 스키마

```jsonc
// settings.json
"features": {
  "allFeatures": false,           // true = 「모든 기능 쓰기」(D-013, roles 는 보지 않음)
  "roles": ["sqlDev", "tuner"],   // null 또는 키 없음 = Role 을 고른 적 없음 → enabled/disabled 도 비면 R1(D-013), 아니면 옛 「모든 기능 쓰기」
  "enabled": ["trace"],           // 직접 켬
  "disabled": ["flashback"]       // 직접 끔
}
```

- `FeaturesSettings { bool AllFeatures; IReadOnlyList<string>? Roles; IReadOnlyList<string> Enabled = []; IReadOnlyList<string> Disabled = []; }`(`AllFeatures` 는 D-013 에서 더함, 정규화가 `AllFeatures` 면 `Roles` 를 null 로 둔다). 목록 비교는 순서 무시 값 비교로 `Equals`/`GetHashCode` 를 직접 쓴다(`SettingsStore.ChangedSections` 가 레코드 `!=` 로 섹션 바뀜을 보기 때문, `src/Oranda.Platform/Storage/SettingsStore.cs:115`).
- 정규화(`SettingsStore.Normalize`): 중복 제거, 같은 id 가 `enabled`·`disabled` 둘 다면 `enabled` 에서 뺀다. 모르는 id 는 지우지 않는다(다음 버전 기능 id 를 이전 버전이 지우지 않게). `SchemaVersion` 은 올리지 않는다(새 섹션은 키가 없으면 기본값, 선례 `AppSettings.cs:233`).
- 섹션 이름 `features` = `SettingsChanged.Section`.

### 10.4 계약 (`IFeatureGate`, Core)

```csharp
public interface IFeatureGate
{
    bool IsEnabled(string featureId);                         // 지금 실제로 켜졌나(10.5)
    bool IsCommandEnabled(string commandId, Type handlerType);
    bool IsPanelEnabled(string panelId, Type viewModelType);
    bool IsTypeEnabled(Type registeredType);                  // 상태 칸·시작 작업
    bool ReadOnlyRole { get; }                                // Role 이 조회 전용 하나뿐(D-006)
    IReadOnlyCollection<string> PendingRestart { get; }       // 재시작해야 반영되는 기능 id
    event EventHandler? Changed;                              // 다시 해석한 뒤. 스레드 보장 없음(구독자가 UI 로 넘김)
}
```

`FeatureCatalog`(데이터): 기능마다 `Id`, 묶음(개발 보조·PL/SQL·MyBatis·튜닝·운영), 이름 문구 키, `StartupBound`(시작 때 돎: `myBatis`·`plsqlWorkspace`·`blockerAlert`·`changeJournal`), `WritesOnly`(조회 전용만일 때 켤 수 없음: `gridEdit`·`dataImport`·`plsqlEditor`·`plsqlWorkspace`·`plsqlRun`·`columnReorder`), `Requires`(`plsqlWorkspace` → `plsqlEditor`). Role 마다 기본 켬 기능 목록(PRD 12.2). 10.2 의 두 표.

### 10.5 해석 규칙

1. 바탕(`FeatureCatalog.EffectiveRoles`, D-013): `AllFeatures` 면 모든 기능, `Roles` 가 있으면 고른 Role 들의 기본 켬 합집합(`Roles == []` 이면 빈 집합 = 공통만), 아무것도 고른 적 없으면(`Roles == null`·`Enabled`/`Disabled` 빔) R1 SQL 개발자, `Roles == null` 인데 직접 켬·끔이 있으면 옛 「모든 기능 쓰기」 저장이라 모든 기능.
2. `+ Enabled − Disabled`.
3. `ReadOnlyRole = ` 1단계의 Role 목록이 정확히 `["viewer"]`. 참이면 `WritesOnly` 기능을 끈다(손으로 고친 설정 파일도 같다).
4. `Requires` 를 못 채운 기능을 끈다.
5. `StartupBound` 기능: 실제 켬 = 「앱 시작 때 켬」 그리고 「지금 켬」. 둘이 다르면 `PendingRestart` 에 넣는다. 그래서 실행 중에 MyBatis 를 켜도 배선이 없는 MyBatis 명령이 나타나지 않고, 끄면 메뉴는 바로 사라지되 감시·타이머는 다음 실행에 멈춘다.

### 10.6 적용 시점과 순서 (조사 5.2-5, 레지스트리가 설정보다 먼저 생김)

지금 순서(`App.axaml.cs:56-91`): `AppComposition.Create` → `ShellController` 생성(이때 명령·도구 창 레지스트리 생성, `:64`) → 스레드 풀에서 `shell.LoadAsync`(설정·레이아웃 읽기, `:65`) → `shell.Initialize`(도킹 짓기, `:88`) → `window.Attach`(메뉴·상태 칸, `:90`) → 첫 프레임 뒤 시작 작업(`:69`).

정한 방식 = 조사 5.2-5 의 (b) 「레지스트리에 거르기 조건을 두고 설정을 읽은 뒤 다시 계산」.

- 레지스트리는 전체 항목을 들고 `IFeatureGate` 로 거른 스냅숏을 내놓는다. `Commands`·`Panels`·`Find` 모두 거른 결과다(`Find` 가 null 이면 `ShellCommandRunner`·`DockWorkspace.OpenDocument` 가 조용히 아무 일도 안 하는 기존 동작, `Shell/ShellCommandRunner.cs:15`, `Shell/Docking/DockWorkspace.cs:355`).
- `FeatureGate` 는 생성 때 「설정 읽기 전」 상태 = 모든 기능 켬이다. `ShellController.LoadAsync` 가 설정을 읽은 직후, 레이아웃을 짓기 전에 게이트를 한 번 해석시키고(이 값이 「앱 시작 때 켬」), 레지스트리가 다시 계산한다. 따라서 `Initialize`(도킹)·`Attach`(메뉴·상태 칸)·시작 작업은 모두 해석된 값을 본다. 이 해석은 `Task.Run` 안에서 일어나므로 이벤트 구독자는 UI 스레드로 넘긴다.
- 실행 중 바뀜: `SettingsChanged("features")` → 게이트 다시 해석 → `Changed` → 레지스트리 `CommandsChanged`/`PanelsChanged`(주 창은 이 사건으로 메뉴를 다시 짓는다, `Views/MainWindow.axaml.cs:168-169`) → `feature.<id>` 문맥 키 갱신 → 화면 안 버튼은 각자 `CommandsChanged` 또는 게이트 `Changed` 로 다시 본다(계획 창 선례 `Features/ExecutionPlan/PlanViewModel.cs:307`).
- 명령의 `When` 으로 숨기지 않는다(주 메뉴만 숨는다, `Shell/Menus/MenuVisibility.cs:20`).
- 도구 창을 레지스트리에서 빼면 저장된 레이아웃에서 빠진다(`Shell/Layout/LayoutPlanner.cs:81-90`). 다시 켜면 기본 위치로 돌아온다(D-010). 실행 중 끈 기능의 이미 열린 도구 창·문서는 닫지 않고(고친 내용 보호) 다음 실행에 빠진다.

### 10.7 거르는 자리

#### 10.7.1 명령·도구 창 (TSK-07-02)

- `CommandRegistry`·`ToolPanelRegistry` 에 게이트를 넣는다(`Shell/Composition/AppComposition.cs:67-68`). `Register` 로 실행 중 더한 항목도 같은 규칙.
- 메뉴·툴바·우클릭(`Views/MainWindow.axaml.cs:209,242,266`)·팔레트(`Shell/Palette/CommandPaletteViewModel.cs:93`)·시작 화면 명령(`Shell/StartPage.cs:47`)·단축키(`Shell/Keymaps/KeyDispatcher.cs:129`)·문서 탭 메뉴(`Shell/Docking/DockTabMenu.cs:14`)가 레지스트리 하나를 보므로 따라온다. 빈 최상위 메뉴(예: R6 의 「모니터」「튜닝」)는 그리지 않는다.
- 키맵 파일의 꺼진 명령 묶음은 지우지 않는다(다시 켜면 그대로).

#### 10.7.2 상태 칸·시작 작업 (TSK-07-03)

- 상태 칸: `ViewModels/MainViewModel.cs:27` 에서 `IsTypeEnabled(i.ItemType)` 이 거짓이면 만들지 않는다. 블로커 칸(`Features/BlockerAlert/BlockerStatusItem.cs:28,36-46`)을 만들지 않으면 감시기·15초 폴링·모니터 세션(`BlockerWatcher.cs:65,272`)도 없다.
- 시작 작업: 지금은 `services.GetService<IEnumerable<IStartupTask>>()`(`App.axaml.cs:148`)가 모든 작업을 **만든 뒤** 돈다. 만들기만 해도 생성자 인자(`MapperWatchService` 등)가 생기므로 형 단계에서 거른다: `ServiceContainer` 에 「구현 형 조건을 받아 IEnumerable 을 만드는」 길을 두고, 지연 어셈블리 훑기 뒤(`App.axaml.cs:139-140`)에도 같은 조건을 쓴다. 건너뛴 작업은 `Trace` 로 남긴다(성능 비교 TSK-07-07 이 센다).
- MyBatis: `MyBatisWorkspaceWiring` 을 돌리지 않으면 `SqlEditorHost` 의 훅이 `MyBatisHooks.Empty` 로 남는다(`Features/SqlEditor/SqlEditorHost.cs:56`).
- 블로커 칸 클릭(`BlockerStatusItem.cs:122`): `lockMonitor` 가 꺼져 있으면 Lock 모니터 대신 안내 플라이아웃과 「설정 열기」(`oranda.tools.roles`). Lock 모니터 임계치 이벤트 구독(`BlockerWatcher.cs:92,121,168`)은 그대로 둔다(발행자가 없으면 무해).

#### 10.7.3 PL/SQL 작업 공간 나누기 (TSK-07-03)

`PlsqlWorkspacePersistence.RunAsync`(`Features/PlsqlWorkspace/PlsqlWorkspacePersistence.cs:70-80`)는 라우터 생성(PL/SQL 편집 탭이 열리는 문서 자리, `plsqlEditor` 에 필요)과 복원·자동 저장 타이머·종료 저장(`plsqlWorkspace`)을 한 번에 한다. 형은 `plsqlEditor` 에 매달고(10.2), 안에서 `IsEnabled("plsqlWorkspace")` 가 거짓이면 라우터만 만들고 복원·타이머·종료 저장을 하지 않는다.

#### 10.7.4 조회 전용 실행 차단 (TSK-07-05, D-006)

- 정책 관문 `ExecutionPolicy`(`src/Oranda.Core/Execution/ExecutionPolicy.cs`)에 규칙 `PolicyRule.ReadOnlyRole` 을 더한다. 자리는 1번 읽기 전용 프로필 바로 뒤, 라이선스 앞. 허용 기준은 프로필 읽기 전용과 같은 `IsReadOnlySafe`(질의·트랜잭션 제어·SQL*Plus·EXPLAIN). 분류가 불확실한 문장은 DML 로 본다(기존 보수 원칙).
- 게이트는 생성자 인자(`IFeatureGate?`, 없으면 끔)로 받는다. 앱 조립의 `AddType`(`Shell/Composition/AppComposition.cs:51`)은 게이트가 등록돼 있으면 그대로 넘긴다. 실행 명령의 대체 등록(`Features/Execution/ExecutionCommands.cs:117-119`)도 게이트를 넘긴다.
- 실행 엔진은 거부면 드라이버를 부르지 않는다(`src/Oranda.Oracle/Execution/OracleExecutionService.cs:146-153`). 앱 기능의 DB 실행은 대부분 이 엔진을 거친다(PlsqlRun·DbTools·ColumnReorder·Flashback·SessionMonitor·LockMonitor·LobRowView·Tuning/Trace·ExecutionPlan 등). 엔진을 거치지 않는 쓰기 경로가 있는지 시험으로 찾아 막거나 결과서에 적는다(그리드 편집 적용·가져오기 포함).
- 문맥 키 `readOnlySession`(`Features/Execution/ExecutionState.cs:242,620`) = 프로필 읽기 전용 또는 `ReadOnlyRole`. 이 키를 보는 쓰기 명령(`!readOnlySession`)이 흐려지고 이유 문구가 붙는다(`Shell/ContextKeyReasons.cs:13`).
- 거부 문구: 「조회 전용 Role 에서는 {0} 을(를) 실행할 수 없습니다. 설정 > 기능에서 다른 Role 을 함께 고르면 풀립니다.」

#### 10.7.5 대체 경로와 화면 안 요소 (TSK-07-06)

- PL/SQL 을 끈 상태의 소스 열기(D-007): 분기는 `SchemaObjectOpener`(`Features/SchemaBrowser/SchemaObjectOpener.cs:44-60`) 한 곳. `PanelFor` 가 PL/SQL 작업 공간을 고르는 경우 `plsqlEditor` 가 꺼져 있으면 서버 소스(기존 메타데이터 공급자)를 새 SQL 편집기 탭에 넣고 탭을 읽기 전용(`tab.IsReadOnly`, `Features/SqlEditor/EditorContext.cs:51`)으로, 제목 「[읽기 전용] OWNER.NAME」. 같은 객체 탭이 이미 있으면 앞으로 낸다. 다른 진입점(`Navigation/ObjectNavigationCommands.cs`, `DbTools/ObjectSearchViewModel.cs`, `ChangeJournal/ChangeJournalViewModel.cs`, `oranda.plsql.openSource` 처리기 `PlsqlEditor/PlsqlEditorCommands.cs:24`)은 이 분기를 부른다.
- MyBatis 를 끈 상태의 매퍼 열기(D-008): 훅이 `Empty` 면 기존 `WorkspaceFiles.ReadMyBatis`(`Features/SqlEditor/WorkspaceFiles.MyBatis.cs:16-40`) 펼침으로 일반 SQL 작업 공간 탭을 만든다. 저장은 작업 공간(.md)으로만 하고 매퍼 파일에는 쓰지 않는다. 조사가 「추측」으로 남긴 경로이므로 훅 없음 상태에서 열기·끌어 놓기(`DroppedWorkspaceFiles.Pick`)·최근 파일·명령줄 열기를 시험으로 확인하고, 다르게 가는 곳만 고친다. 처음 한 번 상태 줄에 「MyBatis 기능이 꺼져 있어 매퍼를 일반 SQL 탭으로 펼쳐 열었습니다(매퍼 파일에는 저장하지 않습니다)」.
- 계획 창(`Features/ExecutionPlan/`): 그래프 탭·SQL Monitor 보고서 탭·비교 영역은 `planAdvanced` 가 꺼지면 숨는다. 표 통계·자식 커서·보고서 단추는 이미 「명령 있을 때만」 보인다(`PlanViewModel.Boost.cs:128,513-518`) — 레지스트리 거르기로 따라오는지, `CommandsChanged` 로 바로 바뀌는지 시험한다. SQL 모니터의 같은 단추(`SqlMonitor/SqlMonitorViewModel.Tuning.cs:283`)도 같다.
- 시작 화면(`Shell/StartPage.cs`): `myBatis` 가 꺼지면 「작업 공간(.md)이나 SQL 파일을 열면 문장마다 탭이 생깁니다.」 아무것도 고른 적 없으면(D-013) 「지금 SQL 개발자 메뉴입니다 — 바꾸기…」 카드(`oranda.tools.roles`)와 흐린 한 줄 「튜닝·모니터·MyBatis·PL/SQL 메뉴는 다른 Role 이나 「모든 기능 쓰기」로 켭니다.」.

#### 10.7.6 설정 창 「기능」 탭 (TSK-07-04, D-011)

- `Shell/Settings/SettingsWindow.axaml` 의 「일반」(`:75`) 다음에 「기능」 탭. 위: 「모든 기능 쓰기」/「Role 로 고르기」 라디오(설정 없음이면 「Role 로 고르기」 + SQL 개발자 골라짐과 안내 띠, D-013), Role 확인란 6개(이름 + 한 줄 설명). 아래: 「기능 세부」 묶음별 확인란 25개, 상태 표시(「Role 기본」 「직접 켬」 「직접 끔」), `Requires` 못 채움은 흐림 + 「{0} 이(가) 켜져 있어야 합니다」, 조회 전용만이면 `WritesOnly` 흐림 + 「조회 전용만 고른 상태에서는 켤 수 없습니다. 다른 Role 을 함께 고르세요.」, 「Role 기본값으로 되돌리기」.
- 조회 전용만 고르면 탭 위에 「조회 전용만 고르면 DML·DDL·PL/SQL 실행을 막습니다」. 튜너를 고르면 「Diagnostics·Tuning Pack 보유 선언은 접속 프로필에서 합니다」(D-012).
- 저장 = 다른 탭과 같은 `SettingsService.UpdateAsync`. 저장 뒤 `PendingRestart` 가 비어 있지 않으면 「재시작하면 적용됩니다: {0}」 띠.
- 명령 `oranda.tools.roles`(도구 메뉴 「Role·기능 고르기…」)는 설정 창을 「기능」 탭으로 연다.
- 화면 품질: ko/en 전환, 라이트·다크, 키보드만으로 모든 확인란 조작(탭 순서·접근 이름), 설정 창 최소 크기에서 글 잘림 없음(스크롤), Headless 렌더 시험. 실앱 눈 확인은 사람 몫.

### 10.8 화면 문구 예약

시험(`LocalizationTests`)이 한국어 리터럴마다 영어 짝을 요구하고 문구 파일(`Resources/strings-ko.json`·`strings-en.json`·`literals-en.json`·`Shell/Resources/CommandTitles*.json`)을 여러 레인이 같이 고치면 충돌하므로, WP-07 이 쓰는 문구는 계약 Task(TSK-07-01)가 모두 먼저 넣는다. 기능 Task 는 이 파일을 고치지 않는다. 빠진 문구가 나오면 그 레인 결과에 적고 통합테스트 Task 가 한 번에 넣는다.

| 묶음 | 문구(ko) |
|---|---|
| Role 이름·설명 | PRD 12.1 표 6개 + 각 한 줄 설명 |
| 기능 이름 | PRD 12.2 표 25개, 묶음 이름 5개(개발 보조·PL/SQL·MyBatis·튜닝·운영) |
| 설정 탭 | 기능, Role, 기능 세부, 모든 기능 쓰기, 기본 Role 안내 띠(D-013), Role 로 고르기, Role 기본, 직접 켬, 직접 끔, Role 기본값으로 되돌리기, 재시작하면 적용됩니다: {0}, {0} 이(가) 켜져 있어야 합니다, 조회 전용만 고르면 DML·DDL·PL/SQL 실행을 막습니다, 조회 전용만 고른 상태에서는 켤 수 없습니다. 다른 Role 을 함께 고르세요., Diagnostics·Tuning Pack 보유 선언은 접속 프로필에서 합니다 |
| 명령 | `Cmd.oranda.tools.roles` = Role·기능 고르기… |
| 실행 거부 | 10.7.4 문구 |
| 블로커 칸 | Lock 모니터 기능이 꺼져 있습니다. 설정 > 기능에서 켤 수 있습니다., 설정 열기 |
| 대체 경로 | [읽기 전용] {0}, PL/SQL 편집기 기능이 꺼져 있어 읽기 전용으로 열었습니다, 10.7.5 의 MyBatis 안내 |
| 시작 화면 | 10.7.5 의 문구(지금 {0} 메뉴입니다, 바꾸기…, 빠진 메뉴 안내) |

### 10.9 기능 Task 편집 지점 (WP-07)

한 파일은 한 Task 만 고친다. 계약 Task 가 만든 파일은 계약 머지 뒤 아래 소유 Task 로 넘어간다. 실제 코드와 다르면 계약 Task 가 `docs/oranda1/design.md` 에 같은 표를 옮겨 적으며 고친다. 경로의 `Shell/`·`Features/`·`Views/`·`ViewModels/` 는 `src/Oranda.App/` 아래다.

| Task | 소유 파일 | 시험 파일 |
|---|---|---|
| TSK-07-01 계약 | `src/Oranda.Core/Features/**`, `src/Oranda.Core/Settings/AppSettings.cs`, `src/Oranda.Core/Settings/FeaturesSettings.cs`, `src/Oranda.Platform/Storage/SettingsStore.cs`, `Shell/Features/FeatureGate.cs`(전부 켬 틀)·`Shell/Features/FeatureCommandIds.cs`, `Shell/Composition/AppComposition.cs`(게이트 등록 한 줄), 문구 파일 4종, `tests/Oranda.Testing/FakeFeatureGate.cs`, `docs/design/commands.md`(행 하나), `docs/oranda1/design.md` | `tests/Oranda.Core.Tests/Features/FeatureCatalogTests.cs`, 아래 시험 파일 빈 틀 전부 |
| TSK-07-02 레지스트리 | `Shell/Features/**`, `Shell/CommandRegistry.cs`, `Shell/ToolPanelRegistry.cs`, `Shell/ShellContributions.cs`, `Shell/ShellController.cs`, `Shell/ContextKeyService.cs`, `Shell/Composition/AppComposition.cs`, `Shell/Docking/**`, `Shell/Layout/**`, `Shell/Menus/**`, `Shell/Palette/**`, `Shell/Keymaps/**`, `Views/MainWindow.axaml*` | `tests/Oranda.App.Tests/Features/Roles/FeatureGateTests.cs`, `RegistryFilterTests.cs` |
| TSK-07-03 시작 | `App.axaml.cs`, `Shell/Composition/ServiceContainer*.cs`, `ViewModels/MainViewModel.cs`, `Features/BlockerAlert/**`, `Features/PlsqlWorkspace/PlsqlWorkspacePersistence.cs` | `tests/Oranda.App.Tests/Features/Roles/StartupFilterTests.cs` |
| TSK-07-04 설정 창 | `Shell/Settings/SettingsWindow.axaml*`, `Shell/Settings/SettingsViewModel.cs`, `Shell/Settings/Features*.cs`·`Shell/Settings/Features*.axaml*`(새 파일) | `tests/Oranda.App.Tests/Features/Roles/FeatureSettingsViewTests.cs` |
| TSK-07-05 조회 전용 | `src/Oranda.Core/Execution/ExecutionPolicy.cs`, `src/Oranda.Core/Execution/IExecutionPolicy.cs`, `Features/Execution/ExecutionState.cs`, `Features/Execution/ExecutionCommands.cs` | `tests/Oranda.Core.Tests/Execution/ReadOnlyRolePolicyTests.cs`, `tests/Oranda.App.Tests/Features/Roles/ReadOnlyRoleExecutionTests.cs` |
| TSK-07-06 대체 경로 | `Features/SchemaBrowser/SchemaObjectOpener.cs`, `Features/Navigation/ObjectNavigationCommands.cs`, `Features/DbTools/ObjectSearchViewModel.cs`, `Features/ChangeJournal/ChangeJournalViewModel.cs`, `Features/PlsqlEditor/PlsqlEditorCommands.cs`, `Features/SqlEditor/WorkspaceFiles*.cs`, `Features/SqlEditor/SqlEditorHost.Files.cs`, `Features/SqlEditor/DroppedWorkspaceFiles.cs`, `Features/ExecutionPlan/**`, `Features/SqlMonitor/SqlMonitorViewModel.Tuning.cs`, `Shell/StartPage.cs` | `tests/Oranda.App.Tests/Features/Roles/FeatureFallbackTests.cs` |
| TSK-07-07 성능 비교 | `tests/Oranda.Perf/**`, `tests/Oranda.Perf.Common/**`, `tests/Oranda.Perf.Ui/**`, `tests/Oranda.Perf.Tests/**`, `scripts/perf/**`, `docs/oranda1/itest/TSK-07-07.md` | `tests/Oranda.Perf.Tests/FeaturePresetTests.cs` |
| TSK-07-08 통합테스트 | `tests/Oranda.App.Tests/Integration/Roles/**`, `tests/Oranda.Oracle.IntegrationTests/Roles/**`, 빠진 문구(문구 파일 4종), `docs/oranda1/itest/TSK-07-08.md` | 같은 폴더 |

### 10.10 성능 비교 방법 (TSK-07-07)

- 같은 PC·같은 Release 게시본·같은 측정 조건(`docs/perf/README.md` 「측정 조건」). 구성은 데이터 폴더의 `settings.json` `features` 로만 바꾼다: V0 설정 없음(전부 켬) · V1 R1 · V2 R3 · V3 R4+R5 · V4 R6.
- `tests/Oranda.Perf` 에 「실행 전 데이터 폴더에 features 구성을 쓰는」 선택지를 더한다. 잰다: 시작 표식(`PerfMarks` app-init → composed → settings-loaded → shell-attached, 첫 프레임), 접속 0·탭 0 유휴 30초 전용 메모리, 프로세스 스레드 수·핸들 수, 돈 시작 작업 수(TSK-07-03 의 Trace), 상태 칸 수. 접속 시나리오(`tests/Oranda.Perf.Ui` DB 시나리오, 26ai 상주 컨테이너): 미커밋 DML 뒤 1분 동안 앱이 연 DB 세션 수(V0·V1 = 블로커 켬, V4 = 끔).
- 판정 통계 = 중앙값(시작·메모리), 구성마다 12회 실행·앞 2회 버림.
- 어셈블리 분리 판단 기준(D-003): V0 와 V1~V4 중 가장 작은 구성의 유휴 메모리 차가 15 MB 이상 또는 10 % 이상이면 「분리 검토」 결론과 후보(Oranda.App.MyBatis·Tuning·Dba)를 적고, 아니면 「분리하지 않음」.

### 10.11 수락 기준 (기술)

- `features` 섹션이 없을 때 거른 뒤 명령·도구 창·상태 칸·시작 작업 집합이 거르기 전과 같다(시험으로 단정).
- 모든 등록 항목(명령·도구 창·상태 칸·시작 작업)이 10.2 규칙으로 기능 하나 또는 공통에 매핑되고, 카탈로그의 모든 기능 id 에 등록 항목이 하나 이상 있다(Tuning 루트처럼 처리기가 없는 네임스페이스는 예외로 적는다).
- 꺼진 명령은 `Commands`·`Find`·`ExecuteAsync`(false) 모두에서 없다. 단축키로 불러도 실행되지 않는다.
- 설정 읽기 전에 만든 레지스트리가 `Initialize`·`Attach` 전에 해석된 값으로 다시 계산된다(시작 순서 시험).
- 조회 전용 거부는 드라이버 호출 전에 끝난다(가짜 드라이버 호출 0회).
- `dotnet build Oranda.sln -warnaserror` 와 `dotnet test` 가 통과한다.

## 11. 사용 복잡도 줄이기 (2026-10-25 추가, PRD 13, WP-09)

설계 근거 = `docs/oranda1/research/usage-complexity.md`(기준 413a33d), 결정 D-015~D-023. 경로의 `Shell/`·`Features/`·`Views/` 는 `src/Oranda.App/` 아래다. 기본 Role 해석(`FeatureCatalog.EffectiveRoles`·`IsUnchosen`·`allFeatures`)은 TSK-07-11(D-013)이 만든 것을 쓰고 고치지 않는다.

### 11.1 바뀌는 곳

| 층 | 바뀌는 것 | Task |
|---|---|---|
| 명령 제목 | `Shell/Resources/CommandTitles.json`·`CommandTitles-en.json` 이름 바꾸기(PRD 13.4) | TSK-09-01 |
| 문구 | `Resources/strings-ko/en.json` 의 `Menu.*`(하위 메뉴 제목 예약)·`Shell.settings.*`(「기본값」) | TSK-09-01 |
| 명령 배치 | `[OrandaCommand(Placements = …)]` 의 `menu:` 경로(파일·편집·보기·SQL), `When`(MyBatis 5개), `ShowInPalette`(작업 공간 저장소 2개) | TSK-09-02 |
| 메뉴 모델 | `Shell/Menus/MenuModel.cs` 오른쪽 클릭 하위 메뉴·결과 메뉴 옮겨 싣기 하위 메뉴, `MenuControls.cs`(그리기·macOS 네이티브), 그리드 명령 배치 | TSK-09-03 |
| 툴바 | `MenuModelBuilder.BuildToolbar` 가 `When` 을 싣고 툴바 단추가 `MenuVisibility` 를 따름, 커밋·롤백 `When` | TSK-09-04 |
| 레이아웃 | `Shell/Layout/LayoutPlanner.cs` 기본 배치, `Shell/Docking/DockWorkspace*.cs` 자동 열기 | TSK-09-05 |
| 키맵 | `docs/design/keymaps/*.json`(임베디드 리소스로 링크됨) | TSK-09-06 |
| 내보내기 | `Features/DataTransfer/ExportDialog*`·`ExcelExportDialog*`·`*Commands.cs` | TSK-09-07 |
| 기능 탭 | `Shell/Settings/FeatureSettingsPage.cs`·`FeatureSettingsView.axaml*`, `src/Oranda.Core/Features/FeatureTexts.cs`, 새 `src/Oranda.Core/Features/PrimaryWork.cs` | TSK-09-08 |
| 시작 화면 | `Shell/StartPage.cs`, `Shell/Settings/SettingsService.cs`(새 설치 판정), 첫 접속 SQL 탭 | TSK-09-09 |

### 11.2 메뉴 배치 경로 규칙

- 주 메뉴(지금 규칙 그대로, `MenuModel.cs:355-381`): `menu:<최상위>/<하위 메뉴…>/<항목 키>@n`. 마지막 마디는 항목 키, 그 앞 마디는 하위 메뉴 이름이고 제목 키는 `Menu.<최상위>.<하위 메뉴>`(깊으면 점으로 잇는다). 하위 메뉴 순서 = 안에 든 항목의 가장 작은 `@n`. 구분선 = `@n / 100` 이 바뀔 때.
- 탐색: 지금은 `menu:navigate/…` 를 편집 메뉴 아래쪽으로 옮긴다(`MenuModel.cs:182-185`). TSK-09-02 는 탐색 명령의 배치를 `menu:edit/go/<키>@n` 으로 직접 바꾼다. 옮기는 코드(`NavigateOffset`)는 남는 `navigate` 배치가 없으면 TSK-09-03 이 지운다.
- 오른쪽 클릭(새 규칙, TSK-09-03): `context:<표면>/<묶음>@n` 은 지금처럼 묶음(구분선 단위). `context:<표면>/<묶음>/<하위 메뉴>@n` 이면 그 묶음 안의 하위 메뉴 하나로 모은다. 하위 메뉴 제목 키 = `Menu.context.<표면>.<하위 메뉴>`. 하위 메뉴 순서 = 안에 든 항목의 가장 작은 `@n`, 안쪽 순서·구분선 규칙은 주 메뉴와 같다. 둘째 마디가 묶음 이름이 아니라 순서 번호로 묶는 편집기 관례(`MenuModel.cs:305-319`)는 그대로 둔다.
- 결과·데이터베이스 메뉴 옮겨 싣기(`MenuModel.cs:203-221`): 오른쪽 클릭의 하위 메뉴를 그대로 하위 메뉴로 옮긴다(제목 키는 같은 `Menu.context.<표면>.<하위 메뉴>`). 묶음 사이 구분선은 지금처럼.
- macOS 네이티브 메뉴(`MenuControls.cs:340-450`)와 Avalonia 메뉴가 같은 모델을 그린다. 오른쪽 클릭 하위 메뉴도 두 곳 다 그린다. 하위 메뉴 안 항목이 `When` 으로 모두 숨으면 하위 메뉴 머리도 숨긴다(지금 주 메뉴 하위 메뉴 규칙과 같다).
- 하위 메뉴 이름(TSK-09-01 이 ko·en 으로 예약, 키 = 경로):

| 키 | ko | 쓰는 Task |
|---|---|---|
| `Menu.edit.case` | 대소문자 | 09-02 |
| `Menu.edit.lines` | 줄·주석 | 09-02 |
| `Menu.edit.format` | 포맷 | 09-02 |
| `Menu.edit.marks` | 북마크·접기 | 09-02 |
| `Menu.edit.go` | 이동 | 09-02 |
| `Menu.edit.assist` | 입력 도우미 | 09-02 |
| `Menu.file.workspace` | 작업 공간 | 09-02 |
| `Menu.file.connection` | 접속 | 09-02 |
| `Menu.file.tabs` | 탭 | 09-02 |
| `Menu.file.mybatis` | MyBatis | 09-02 |
| `Menu.sql.plsql` | PL/SQL | 09-02 |
| `Menu.sql.errorMode` | 스크립트 오류 시 (이번 실행) | 09-01 이 제목만 바꿈(지금 키 그대로) |
| `Menu.context.grid.copyAs` | 복사 형식 | 09-03 |
| `Menu.context.grid.filter` | 거르기 | 09-03 |
| `Menu.context.grid.edit` | 행 편집 | 09-03 |
| `Menu.context.grid.flashback` | 플래시백 | 09-03 |
| `Menu.context.grid.columns` | 열 | 09-03 |

  지금 키 이름이 다르면(예: 스크립트 오류 시 하위 메뉴의 실제 키) TSK-09-01 이 실제 키를 찾아 이 표를 `docs/oranda1/design.md` 에 고쳐 옮긴다. 최근 작업 공간 하위 메뉴(`Menu.file.recentWorkspaces`, `RecentFilesService.cs:33`)를 「작업 공간 ▸」 안으로 옮기면 키가 `Menu.file.workspace.recentWorkspaces` 로 바뀌므로 공급자 상수도 같이 바꾼다(09-02).

### 11.3 툴바 `When` (TSK-09-04, D-022)

- `MenuModelBuilder.BuildToolbar`(`MenuModel.cs:252-272`)가 명령의 `When` 을 노드에 싣는다. 툴바를 그리는 곳은 주 메뉴와 같은 `MenuVisibility` 평가로 단추의 `IsVisible` 을 문맥 키가 바뀔 때마다 고친다. 단추 사이 구분선은 양옆 단추가 모두 숨으면 숨는다. 넘침 메뉴(`ToolbarOverflow.cs`)도 같은 규칙.
- `When` 이 없는 명령(툴바의 거의 모두)은 지금과 같다. 지금 `When` 이 있으면서 툴바에 놓인 명령이 있으면 이 변경으로 숨게 되므로, TSK-09-04 는 그런 명령을 모두 찾아 결과에 적고 의도와 다르면 시험으로 막는다.
- `oranda.txn.commit`·`oranda.txn.rollback` 에 `When = "!readOnlySession || txnPending"`. Enablement 는 그대로.

### 11.4 Role 고르기 화면 (TSK-09-08, D-017)

- 새 Core 형 `PrimaryWork`(순수 로직, 시험 쉬움): `FromSettings(FeaturesSettings) → (Primary: All|SqlDev|Tuner|Dba|Viewer|Custom, PlsqlAddon, MyBatisAddon, CustomRoles)` 와 `Apply(현재 설정, 고른 값) → FeaturesSettings`.

| 설정 | 화면 |
|---|---|
| `allFeatures: true` | 모든 기능, 덧붙임 흐림 |
| roles 없음 + 직접 켬·끔 있음(옛 「모든 기능 쓰기」 저장, `EffectiveRoles` = null) | 모든 기능, 덧붙임 흐림. 저장하면 `allFeatures: true` 로 옮겨 적어도 해석은 같다 |
| 아무것도 고른 적 없음(`IsUnchosen`, D-013) | SQL 개발(기본 Role 표시) + TSK-07-11 의 기본 Role 안내 띠(`FeatureTexts.DefaultRoleNotice`), 덧붙임 끔. 건드리지 않고 저장하면 설정 그대로(D-013) |
| roles = {sqlDev·tuner·dba·viewer 중 정확히 1개} ∪ ({plsqlDev, myBatisDev} 의 부분집합) | 그 주 업무 + 덧붙임 |
| roles = ["viewer"] 에 덧붙임이 섞임 | Custom(그대로 두기) |
| 그 밖(주 업무 0개 또는 2개 이상, `[]`, 모르는 id) | Custom 줄 「지금 조합: {Role 이름 목록}(그대로 두기)」 |

- 저장: 라디오·덧붙임을 건드리지 않았으면 `roles`·`allFeatures` 를 그대로 둔다(Custom·고른 적 없음 보존). 「모든 기능」 = `allFeatures: true`, roles 는 null(지금 `FeatureSettingsPage` 의 저장 규칙 `AllFeatures = roles is null` 과 같다). 그 밖 = `allFeatures: false`, roles = [주 업무 id] + 덧붙임 id. `enabled`·`disabled` 는 지금처럼 새 Role 기본값과 비교해 최소로 다시 적는다(TSK-07-04 규칙).
- 「조회만」 이면 덧붙임 흐림 + 이유 「덧붙이면 조회 전용 차단이 풀립니다」. 「모든 기능」 이면 덧붙임 흐림 + 「모든 기능에 이미 들어 있습니다」.
- 고급 접힘(`Expander`, 기본 접힘, `ProfileEditorView.axaml:214` 와 같은 모양): 머리 「기능 하나씩 켜고 끄기(고급) — 켜진 기능 {0}/{1} · 직접 바꾼 것 {2}개」. 안쪽은 TSK-07-04 의 묶음별 확인란·배지·되돌리기 그대로.
- TSK-07-10 의 「Role 0개」 안내 줄은 화면에서 Role 0개 상태가 새로 생기지 않으므로 Custom(`[]`) 일 때만 보인다.
- 팩 선언 안내 줄(D-012) 끝에 「접속 관리자 열기」 링크(`oranda.conn.manage` 실행).

### 11.5 새 설치 판정·시작 화면 카드 (TSK-09-09, D-018)

- `SettingsService.LoadAsync` 가 읽을 때 settings.json 이 없었는지를 `IsFirstRun`(앱 실행 동안 고정)으로 둔다. 다른 프로세스가 뒤에 파일을 만들어도 바뀌지 않는다.
- 시작 화면 카드 표시 = `IsFirstRun && IsUnchosen(features)`. 카드: 제목 「주로 하는 일은?」, 단추 5개(주 업무, 11.4 와 같은 이름·한 줄 설명), 덧붙임 확인란 2개, 「나중에」. 단추 → `PrimaryWork.Apply` → `SettingsService.UpdateAsync` → 카드 닫힘(게이트가 즉시 반영, 재시작 필요 기능은 기존 띠). 「나중에」 → 설정을 그대로 저장(파일이 생김) → 카드 닫힘 → D-013 한 줄 카드.
- `IsFirstRun` 이 아니고 `IsUnchosen` 이면 D-013 한 줄 카드(TSK-07-11 이 만든 것)를 그대로 쓴다.
- 첫 접속 흐름: 접속 프로필 0개이면 시작 화면 첫 줄을 「접속 추가…」(접속 관리자를 새 접속 상태로 여는 기존 명령)로 바꾸고 강조한다. 접속 성공 이벤트(`SessionOpened` 등 지금 있는 것)를 구독해 열린 문서가 0개이면 그 세션으로 새 SQL 편집기를 연다. 복원된 탭이 있거나 전용 세션·모니터 접속이면 열지 않는다.

### 11.6 기본 레이아웃 (TSK-09-05, D-019)

- `LayoutPlanner.CreateDefault`: 아래 묶음에 넣는 도구 창 = `oranda.panel.results`·`oranda.panel.messages`·`oranda.panel.output`(목록 상수). 나머지 아래 도구 창(plan·history·changeJournal·compileErrors, MyBatis 조합은 문서 범위 규칙 그대로)과 오른쪽 도구 창 전부는 `HiddenTools` 에 둔다. `Migrations` 에 `rightToolsOpen` 을 남긴다.
- `Reconcile` 는 바꾸지 않는다(저장된 배치 유지). 새로 등록된 도구 창의 기본 자리 규칙도 그대로.
- 자동 열기: 지금 경로를 그대로 쓰되 숨은 창에서도 통하는지 시험한다 — 실행 계획(`ExecutionPlanCommands.cs:107` ShowTool), 컴파일 오류(`PlsqlEditorCommands.cs:253`, `ExecutionController.Body.cs:289` ActivatePanel), 바인드 변수(`ExecutionController.Binds.cs:33,79` ActivatePanel). `DockWorkspace.ActivateTool` 이 숨은 창을 보이지 않으면 `DockWorkspace` 쪽에서 고친다(명령 파일은 고치지 않는다). 숨은 창이 처음 열릴 때 자리 = 그 창의 기본 위치(D-010 과 같은 규칙).

### 11.7 내보내기 대화상자 (TSK-09-07, D-020)

- `ExportDialogViewModel` 을 기준으로 합치고 `ExcelExportDialogViewModel` 의 범위·내보낸 뒤 열기·Excel 전용 선택을 옮긴다. 창은 하나(`ExportDialogWindow`), `ExcelExportDialogWindow` 는 지운다(시험·다른 호출자가 없을 때).
- 여는 길: `oranda.grid.export` = 마지막에 쓴 형식(앱 실행 동안 기억, 처음은 CSV), Excel 내보내기 명령 = Excel 골라진 상태. 저장 위치는 칸 + 「찾아보기…」(저장 대화상자 따로 띄우지 않음). 끝난 뒤 알림은 지금처럼 `SaveFeedback`(열기·폴더 열기).
- 형식별 쓰기 코드(CSV·TSV·Excel·JSON·INSERT)는 바꾸지 않는다. 기존 Excel 내보내기 시험(`tests/Oranda.App.Tests/Features/DataTransfer/ExcelExportTests.cs`)이 같은 결과 파일을 단정하게 유지한다.

### 11.8 키맵 (TSK-09-06, D-021)

- `default.json:59` `oranda.mybatis.runScenario` 의 `keys` 를 `["Primary+Enter", "F9"]` 로(mac 은 지금 그대로).
- 같은 키 `Primary+Enter` 가 `oranda.exec.runStatement`(when `sqlDocFocus`)에도 있다. MyBatis 문서에서 어느 쪽이 이기는지 `KeyDispatcher`·`KeyBindingTable` 규칙을 시험으로 확인한다. 현재 문장 실행이 이기면 그 바인딩의 when 에 `&& !mybatisDocFocus` 를 더한다(키맵 파일에서). 디스패처 규칙은 바꾸지 않는 것이 먼저다.
- orange5·plsqldev·sqldev 도 같은 규칙. 프리셋이 그 키를 다른 명령에 쓰면 그대로 두고 결과에 적는다.

### 11.9 문구 파일 소유 (D-023)

| 파일 | 소유 |
|---|---|
| `Shell/Resources/CommandTitles.json`·`CommandTitles-en.json` | TSK-09-01, 그 뒤 ux-menu 레인(09-02)만 |
| `Resources/strings-ko/en.json` 의 `Menu.*`·`Shell.settings.*` | TSK-09-01, 그 뒤 ux-menu 레인만 |
| `Resources/strings-ko/en.json` 의 `Export.*`(새 접두) | TSK-09-07 |
| `Resources/strings-ko/en.json` 의 `Features.*`·`Role.*`(`FeatureTexts` 키) | TSK-09-08, 09-09 (같은 레인 차례로) |
| `Resources/strings-ko/en.json` 의 `Layout.*`(필요할 때만) | TSK-09-05 |
| `Resources/literals-en.json` | 계약 Task 만 파일 끝에 붙인다. 다른 Task 는 자기 화면의 기존 문구 바로 뒤에 넣는다(끝에 붙이면 병렬 머지 충돌) |
| 빠진 문구 | 레인 결과에 적고 TSK-09-10 이 넣는다 |

strings 파일은 키 이름 순으로 놓여 있으므로 자기 접두 자리에 넣으면 다른 레인과 줄이 붙지 않는다. TSK-07-11(fx-default, 52601b9 에 머지됨)이 고친 `Features.*` 자리(strings-ko 83~100줄 부근)는 이제 ux-role 만 고친다.

### 11.10 기능 Task 편집 지점 (WP-09)

한 파일은 한 레인만 고친다. 같은 레인 안 Task 는 차례로 한다. 계약 Task 가 실제 코드에 맞춰 이 표를 `docs/oranda1/design.md` `## 기능 Task 편집 지점 (WP-09)` 에 옮긴다. 시험 경로의 뿌리는 `tests/Oranda.App.Tests/`.

| 레인 / Task | 소유 파일 | 시험 파일 | 고치지 않는 것 |
|---|---|---|---|
| ux-menu / TSK-09-01 계약 | 11.9 의 ux-menu 문구, `Shell/Settings/SettingsWindow.axaml`(실행 탭 안내 한 줄), 이름을 특성 `Title =` 으로 박은 명령 파일의 그 줄(`Features/SessionMonitor/**`, `Features/LockMonitor/**`, `Features/Tuning/Stats/TableStatsCommands.cs` 등), 세션 종료 확인 창 문구, `docs/design/commands.md`(제목 열), `docs/oranda1/design.md` | `Features/Localization/LocalizationTests.cs`, 옛 제목을 단정한 시험 | 배치·When·로직 |
| ux-menu / TSK-09-02 | 아래 「ux-shell」·「ux-export」·「ux-role」 소유를 뺀 모든 `Features/**/*Commands.cs`·`*CommandHandler.cs` 의 `[OrandaCommand]` 특성(특히 `SqlEditor/SqlEditorCommandHandler.cs`, `Navigation/*Commands.cs`, `ConnectionManager/ConnectionManagerCommands.cs`, `ConnectionImport/*Commands.cs`, `PlsqlEditor/PlsqlEditorCommands.cs`, `PlsqlRun/PlsqlRunCommands.cs`, `MyBatisWorkspace/**/*Commands.cs`, `Templates/TemplatesCommands.cs`, `LobRowView/LobRowViewCommands.cs`, `Execution/ExecutionCommands.cs`, `ExecutionPlan/ExecutionPlanCommands.cs`, `Completion/*`, `FormatterProfiles/*`, `MyBatisLog/*`, `DedicatedSession/*Commands.cs`, `RecentFiles/**`), `Shell/ShellCommands.cs` | `Features/SqlEditor/SqlEditorWorkspaceMenuTests.cs`, 새 `Shell/MenuGroupingTests.cs` | `Shell/Menus/**`, 그리드 3파일, `DataTransfer/**`, 기존 메뉴 Headless 시험(ux-shell 소유 — 깨지면 고치지 말고 결과에 적고 조정자에게 알린다) |
| ux-shell / TSK-09-03 | `Shell/Menus/**`, `Features/ResultGrid/ResultGridCommands.cs`, `Features/GridEdit/GridEditCommands.cs`, `Features/Flashback/FlashbackCommands.cs` | `Shell/Headless/MenuStateHeadlessTests.cs`, `MenuBarTargetTests.cs`, `NativeMenuTests.cs`, `Shell/MenuVisibilityTests.cs`, `Shell/TuningMenuTests.cs`, 새 `Shell/ContextSubmenuTests.cs` | 다른 `*Commands.cs`, 문구 파일 |
| ux-shell / TSK-09-04 | `Shell/Menus/**`, `Features/Execution/ExecutionCommands.cs` 의 커밋·롤백 두 줄(TSK-09-02 머지 뒤) | 새 `Shell/ToolbarWhenTests.cs`, `Integration/Roles/RoleSurfaceMatrixTests.cs`(툴바 정답표, TSK-07-11 머지 뒤) | 문구 파일 |
| ux-layout / TSK-09-05 | `Shell/Layout/**`, `Shell/Docking/**`, 문구 `Layout.*` | `Shell/LayoutPlannerTests.cs`, `Shell/Headless/NamedLayoutTests.cs`, 새 `Shell/Headless/DefaultLayoutAutoOpenTests.cs` | `*Commands.cs`, `[OrandaToolPanel]` 특성, `Features/Execution/**` |
| ux-layout / TSK-09-06 | `docs/design/keymaps/*.json`, (꼭 필요할 때만) `Shell/Keymaps/**` | `Shell/Contract/KeymapLoaderTests.cs`, 새 `Shell/MyBatisRunKeyTests.cs` | `*Commands.cs` |
| ux-export / TSK-09-07 | `Features/DataTransfer/**`(가져오기 `Import*` 제외), 문구 `Export.*` | `Features/DataTransfer/**` | `CommandTitles*.json`, `Shell/Menus/**` |
| ux-role / TSK-09-08 | `Shell/Settings/FeatureSettingsPage.cs`, `Shell/Settings/FeatureSettingsView.axaml*`, `src/Oranda.Core/Features/FeatureTexts.cs`, 새 `src/Oranda.Core/Features/PrimaryWork.cs`, 문구 `Features.*`·`Role.*` | `Features/Roles/FeatureSettingsViewTests.cs`, `Integration/Roles/RoleMenuFlowTests.cs`, `RoleEdgeCaseTests.cs`, `RoleRenderTests.cs`, 새 `tests/Oranda.Core.Tests/Features/PrimaryWorkTests.cs` | `FeatureCatalog.cs`(해석 규칙)·`FeaturesSettings.cs`·`SettingsStore.cs`·`FeatureGate.cs` |
| ux-role / TSK-09-09 | `Shell/StartPage.cs`, `Shell/Settings/SettingsService.cs`, 첫 접속 SQL 탭 구독(새 파일 `Shell/FirstConnectEditor.cs` 권장), `FeatureTexts.cs`·문구(시작 화면 키) | `Shell/Headless/StartPageHostTests.cs`, 새 `Shell/Headless/FirstRunCardTests.cs` | `ConnectionManager/ConnectionManagerCommands.cs`(ux-menu) |
| ux-itest / TSK-09-10 | `tests/Oranda.App.Tests/Integration/Ux/**`(새), 빠진 문구(문구 파일 전부), `docs/oranda1/itest/TSK-09-10.md` | 같은 폴더 | 제품 코드(결함은 defect Task 로) |

### 11.11 메뉴 너비 재는 법 (TSK-09-10)

- 앱을 실제 `FeatureGate` 로 조립하는 시험 도우미(`Integration/Roles/RoleApp.cs` 를 그대로 쓰거나 `Integration/Ux/` 에 같은 방식의 도우미)로 Role 구성마다 주 메뉴·그리드 오른쪽 클릭 모델을 읽는다.
- 센다: (1) 메뉴 항목 총수 — 조사 1.1 주1 규칙(명령이 놓인 메뉴 자리 수 + 오른쪽 클릭에서 옮겨 실은 명령 + 보기 > 도구 창 토글, 구분선·열 때 채우는 줄 제외, 한 명령이 두 메뉴면 2), (2) 최상위 메뉴 수, (3) 한 단계 줄 수가 15를 넘는 메뉴 수(최상위·하위 메뉴 모두, PRD 13.3 정의, `When` 숨김 미적용), (4) 최상위 메뉴별 한 단계 줄 수, (5) 그리드 오른쪽 클릭 한 단계 줄 수, (6) 툴바 단추 수(미커밋 없음/있음), (7) 기본 배치에서 SQL 탭과 함께 보이는 도구 창 수. 보조 열로 SQL 탭 활성 상태의 보이는 항목 수.
- 구성: 모든 기능(`allFeatures: true`)·아무것도 고르지 않음(D-013 = R1)·R1~R6·R1+R4·Role 0개(`roles: []`).
- 결과서 표: 구성 × 항목, 「조사(413a33d)」·「지금」·「목표(PRD 13.3·13.5)」·「차이 이유」. 목표를 못 채운 칸은 이유와 후속 후보를 적는다.

### 11.12 수락 기준 (기술)

- 명령 id 집합·키맵 바인딩(09-06 추가 제외)·팔레트 결과(숨긴 2줄 제외)가 WP-09 전과 같다(시험으로 단정).
- 오른쪽 클릭 하위 메뉴가 Avalonia·macOS 네이티브 두 그리기에서 같은 모양이다(모델 시험 + Headless).
- 툴바 `When` 이 문맥 키 변화에 따라 단추를 숨기고 보인다(Headless).
- `PrimaryWork` 왕복: 모든 기존 `features` 모양(`null`·`allFeatures`·Role 1~6개 조합·`[]`·모르는 id)을 화면 상태로 읽고 건드리지 않고 저장하면 같은 `features` 가 나온다.
- 기본 배치·레이아웃 초기화 결과의 보이는 도구 창 집합을 단정하고, 저장된 배치(`rightToolsOpen` 있음·없음)를 읽으면 지금과 같다.
- `dotnet build Oranda.sln -warnaserror` 와 `dotnet test` 가 통과한다.

## 12. 스크립트 창 검색·스크립트 추가 (2026-10-26 추가, PRD 14, WP-10)

설계 근거 = PRD 14, 결정 D-024~D-028. 경로의 `Features/`·`Shell/`·`Resources/` 는 `src/Oranda.App/` 아래다. 검색기와 생성기는 Avalonia 를 참조하지 않는 순수 클래스로 두어 화면 없이 시험한다(`Features/HistoryScripts/` 안 새 파일).

### 12.1 바뀌는 곳

| Task | 소유 파일 | 시험 파일 | 고치지 않는 것 |
|---|---|---|---|
| TSK-10-01 검색 | 새 `Features/HistoryScripts/ScriptSearch.cs`(검색기)·`ScriptSearchModels.cs`(질의·결과 모델), `ScriptsView.axaml` 의 거르기 줄·결과 목록 자리, `ScriptsView.axaml.cs`·`ScriptsViewModel.cs` 의 검색 부분, 문구 `Scripts.search.*`(strings-ko/en)·literals-en.json | 새 `tests/Oranda.App.Tests/Features/HistoryScripts/ScriptSearchTests.cs`, `ScriptsSearchViewTests.cs`(Headless) | 새 스크립트 부분, 주 메뉴·키맵·`CommandTitles*.json` |
| TSK-10-02 추가 | 새 `Features/HistoryScripts/NewScriptCreator.cs`(생성기·이름 검사), 새 `NewScriptDialog.axaml*`, `ScriptsView.axaml` 의 툴바 단추·트리 오른쪽 클릭 줄, `ScriptsView.axaml.cs`·`ScriptsViewModel.cs` 의 추가 부분, 문구 `Scripts.new.*`·literals-en.json | 새 `NewScriptCreatorTests.cs`, `ScriptsNewScriptViewTests.cs`(Headless) | 검색 부분, 주 메뉴 |
| TSK-10-03 통합 확인 (WP-10·WP-11·WP-12 범위) | `tests/Oranda.App.Tests/Integration/Scripts/**`, `Integration/GridRows/**`(새), 빠진 문구, `docs/oranda1/itest/TSK-10-03.md` | 같은 폴더 | 제품 코드(결함은 defect Task 로) |

TSK-10-01 과 TSK-10-02 는 같은 파일(`ScriptsView.axaml`·`ScriptsView.axaml.cs`·`ScriptsViewModel.cs`·literals-en.json)을 만진다. 서로 자기 몫(위 표의 자리)만 바꾸고, 고친 범위를 결과에 적는다. 나중에 머지하는 쪽이 rebase 충돌을 푼다. 문구 키는 접두로 나눈다(`Scripts.search.*`, `Scripts.new.*`). literals-en.json 은 자기 화면의 기존 문구 바로 뒤에 넣는다(파일 끝에 붙이지 않는다, D-023).

### 12.2 검색기 (TSK-10-01, D-024·D-025)

- 호출: `ScriptSearch.RunAsync(ScriptSearchQuery query, IReadOnlyList<string> roots, IProgress<ScriptSearchProgress>? progress, CancellationToken ct)` → `ScriptSearchResult`. `ScriptSearchQuery` = 입력 글(`Text`)과 `IncludeContent`. `roots` 는 `ScriptFolderList` 의 연결 폴더.
- 결과 모델: `ScriptSearchResult` = 파일 목록(`ScriptSearchFile`: 경로·이름 일치 여부·`ScriptSearchLine` 목록), `FileCount`·`MatchCount`, `LimitReached`, `SkippedLarge`, `SkippedUnreadable`. `ScriptSearchLine` = 줄 번호(1부터)·미리보기 글·일치 구간 목록(강조용 시작·길이).
- 열거: 연결 폴더를 하위까지 훑는다. 숨김 폴더(`FileAttributes.Hidden`, 트리가 쓰는 것과 같은 판정)는 들어가지 않는다. 파일 판정은 트리와 같은 함수(`ScriptsViewModel` 의 스크립트 파일 판정)를 한 곳에서 공유해 트리와 결과가 어긋나지 않게 한다. 링크 순환을 막는다(이미 본 실제 경로는 건너뜀).
- 이름 검색: 입력을 공백으로 나눈 낱말 모두가 파일 이름(확장자 포함)에 대소문자 무시(`OrdinalIgnoreCase`)로 들어 있으면 일치. 낱말이 없으면(공백만) 검색하지 않는다. 한 파일은 일치 1곳으로 센다.
- 내용 검색(`IncludeContent`): 입력 전체(앞뒤 공백 제거)를 하나의 글자로, `OrdinalIgnoreCase` 로 줄마다 찾는다. 정규식을 쓰지 않는다. 한 줄에 여러 번 있으면 그 줄을 1곳으로 세고 강조 구간은 모두 담는다. 내용을 켜면 이름이 맞는 파일(줄 없음)도 함께 결과에 든다.
- 파일 읽기: 편집기와 같은 방법 — `Features/SqlEditor/FileEncoding.Decode`(BOM 판별, UTF-8 엄격 → 안 되면 CP949)로 바이트를 글로 바꾼다. 앞 8 KB 에 NUL 바이트가 있으면 이진 파일로 보고 내용 검색에서 조용히 뺀다. 읽기 오류(권한·잠김)는 `SkippedUnreadable` 로 센다.
- 한도(D-025): `MatchCount` 가 1,000 에 닿으면 즉시 멈추고 `LimitReached`. 2 MB(2×1024×1024 바이트)를 넘는 파일은 내용을 읽지 않고 `SkippedLarge` 로 센다(이름 검색은 한다). 한도 값은 상수(`ScriptSearch.MaxMatches`·`MaxFileBytes`)로 두어 시험이 줄여 쓸 수 있게 한다.
- 미리보기: 일치한 줄을 앞뒤 공백을 줄이고 200자 안으로 자른다. 일치가 잘린 쪽 밖이면 일치 둘레가 보이게 잘라 「…」 를 붙인다. 강조 구간은 잘린 글 기준으로 다시 잰다.
- 취소·입력 지연: `ScriptsViewModel` 이 입력이 바뀔 때마다 이전 `CancellationTokenSource` 를 취소하고 250 ms 뒤에 `Task.Run` 으로 검색한다. 입력이 비면 검색을 취소하고 트리로 돌아간다. 취소는 파일 사이와 큰 파일의 줄 루프 안(일정 줄마다)에서 확인한다. 진행은 `IProgress` 로 파일 20개마다 UI 스레드에 알려 상태 줄 「검색 중… n개 파일」 과 목록을 갱신한다(컬렉션을 통째로 바꾸지 않고 묶음 단위로 더한다).
- 화면: 거르기 줄은 기존 거르기 줄 모양(`Classes` 는 세션 모니터·SQL 모니터와 같은 것)에 칸 + 「내용」 토글. 결과 목록은 트리 자리를 대신하는 묶음 리스트(파일 행 + 줄 행, 줄 번호 열 오른쪽 정렬, 미리보기 고정폭 글꼴, 일치 부분은 기존 강조 브러시). 상태 줄(`panel-status`)에 「n개 파일에서 m곳」, 한도면 「(한도 도달)」, 건너뜀이 있으면 그 수. 처음·검색 중·결과 없음·오류 상태는 PRD 14 SC-08.
- 키: Ctrl+F 는 스크립트 창이 포커스일 때만 거르기 칸으로(창 안 `KeyDown`, 키맵 파일·명령 추가 없음). Esc 는 검색을 지우고 트리로. Enter·두 번 누름은 기존 파일 열기 경로를 부른다. 내용 결과면 열린 편집기에서 그 줄로 이동한다(편집기 호스트의 줄 이동 API 를 쓰고, 없으면 열기 직후 캐럿 이동; 구현 레인이 지금 경로를 확인해 결과에 적는다).
- 접근성: `AutomationProperties.Name` 을 거르기 칸·내용 토글·결과 목록에 단다. 탭 순서 = 칸 → 토글 → 결과 목록.

### 12.3 새 스크립트 생성기 (TSK-10-02, D-026·D-027)

- 폴더 결정: `NewScriptCreator.ResolveFolder(ScriptNode? selected, IReadOnlyList<string> roots)` → 폴더 경로 또는 이유. 선택이 폴더면 그 폴더, 파일이면 `Path.GetDirectoryName`, 선택이 없으면 `roots.Count == 1` 일 때 그 폴더, 아니면 「폴더를 먼저 고르세요」(단추 `IsEnabled=false` + 툴팁).
- 기본 이름: `SuggestName(folder)` = 「새 스크립트.sql」, 폴더에 같은 이름(대소문자 무시)이 있으면 「새 스크립트 2.sql」, 「새 스크립트 3.sql」…
- 이름 검사: `Validate(folder, input)` → 최종 파일 이름 또는 오류 글. 앞뒤 공백을 자르고, 비었거나 `Path.GetInvalidFileNameChars()` 와 `\`·`/` 를 포함하거나 끝이 점·공백이거나 Windows 예약 이름(`CON`·`PRN`·`AUX`·`NUL`·`COM1`~`COM9`·`LPT1`~`LPT9`, 확장자 앞 부분 기준)이면 오류. 경로 구분자가 든 이름은 하위 폴더를 만들지 않고 오류. 확장자가 없으면 `.sql` 을 붙인다. 같은 폴더에 같은 이름(대소문자 무시)이 이미 있으면 오류 「이미 있는 이름입니다」. 오류는 대화상자 안 오류 줄(위험 브러시)에 보이고 확인 단추는 꺼진다.
- 쓰기: `Create(folder, fileName, content)` 는 `FileMode.CreateNew`(`FileShare.None`)로 연다. 이미 있으면 `IOException` 을 「이미 있는 이름입니다」 오류로 바꿔 돌려주고 기존 파일은 건드리지 않는다(검사와 쓰기 사이 경쟁 포함). 인코딩은 UTF-8(BOM 없음, `FileEncoding.Encode(text, "utf-8")`), 내용이 없으면 빈 파일. 채우기를 켜면 활성 편집기의 글을 그대로(줄 끝 변환 없이) 쓴다.
- 채우기 가능 여부: 활성 편집기가 있고 글이 비어 있지 않을 때만 체크를 켤 수 있다. 활성 편집기 글은 기존 `SqlEditorHost` 의 활성 문서 접근을 쓰고 새 공개 API 를 만들지 않는다(없으면 구현 레인이 가장 작은 접근자를 추가하고 결과에 적는다).
- 만든 뒤: 그 폴더 노드만 다시 읽는다(전체 트리를 다시 읽지 않고 폴더 노드의 기존 새로 고침 경로를 쓴다). 새 파일 노드를 선택하고 상위를 펼친 뒤 기존 파일 열기 경로로 편집기에서 연다. 검색 결과 목록이 보이는 중에 만들면 검색을 지우고 트리로 돌아간 뒤 같은 동작을 한다.
- 화면: 툴바 단추(`ButtonIcon` 아이콘, 동사 이름 「새 스크립트」, 툴팁에 「선택한 폴더에 새 .sql 파일을 만들고 편집기에서 엽니다 (DB 에 쓰지 않음)」), 트리 `ContextMenu` 의 「새 스크립트…」. 이름 입력 창은 기존 입력 대화상자 패턴을 재사용하되 체크박스 한 줄이 들어갈 수 없으면 작은 `Window` 를 새로 만든다. 주 메뉴·팔레트·키맵에 명령을 더하지 않는다(D-028). 따라서 `CommandTitles*.json` 은 고치지 않고, 문구는 `Scripts.new.*`(strings-ko/en)·literals-en.json 에 둔다.

### 12.4 시험 방침

- 검색기 단위(`ScriptSearchTests`, 임시 폴더): 이름 낱말 AND·대소문자 무시, 내용 일반 글자(정규식 문자 포함)·대소문자 무시·한 줄 여러 일치, 파일별 묶음·줄 번호·미리보기 자르기·강조 구간, 숨김 폴더·확장자 필터, 인코딩(UTF-8·BOM·UTF-16·CP949 한글), 이진 파일 제외, 한도(`MaxMatches`·`MaxFileBytes` 를 줄여 도달·건너뜀), 빈 입력, 폴더 없음·권한 오류, 취소(긴 검색 도중 취소 시 부분 결과를 쓰지 않음), 링크 순환.
- 생성기 단위(`NewScriptCreatorTests`): 폴더 결정 4경우, 기본 이름 번호 매기기, 이름 검사 표(빈·잘못된 글자·예약 이름·구분자·확장자 보태기·대소문자 다른 겹침), `CreateNew` 경쟁(이미 있는 파일 내용 보존), 채우기 내용 일치, 인코딩.
- Headless(`ScriptsSearchViewTests`·`ScriptsNewScriptViewTests`): 입력 → 250 ms 뒤 결과, 연속 입력 취소, Esc 로 트리 복귀(펼침·선택 보존), Ctrl+F 포커스, 두 번 누름 열기·줄 이동, 단추 꺼짐 + 툴팁, 이름 창 오류 줄·확인 꺼짐, 만든 뒤 선택·열림. 상태 화면(처음·검색 중·결과 없음·오류·한도)을 라이트·다크 PNG 로 떠서 직접 본다(`~/.coord/scripts-find/shots/<레인>/`, 커밋하지 않는다).
- 성능: 파일 5,000개(각 수 KB) 폴더에서 입력 멈춤 → 첫 결과, 한도 도달 시간을 재서 결과에 적는다. 검색이 입력·스크롤을 막지 않음을 Headless 로 확인한다.
- 문구: LocalizationTests 가 하드코딩을 잡는다(XAML → literals-en.json, 패널 문구 → strings-ko/en).

### 12.5 수락 기준 (기술)

- 명령 id 집합·키맵·팔레트 결과가 WP-10 전과 같다(새 명령이 없음을 시험으로 단정).
- 검색기가 UI 스레드 밖에서 돌고, 앞 검색 취소 뒤 오래된 결과가 화면에 반영되지 않는다.
- 새 스크립트가 어떤 경우에도 기존 파일을 덮어쓰지 않는다(`CreateNew` 경쟁 시험).
- `dotnet build Oranda.sln -warnaserror` 와 `dotnet test`(Integration·Perf 제외)가 통과한다.

## 13. 결과 격자 여러 행 삭제·행 복사 (2026-10-26 추가, PRD 15, WP-11)

설계 근거 = PRD 15, 결정 D-029~D-031. 경로의 `Features/`·`Shell/` 은 `src/Oranda.App/` 아래다.

### 13.1 바뀌는 곳

| Task | 소유 파일 | 시험 파일 | 고치지 않는 것 |
|---|---|---|---|
| TSK-11-01 | `Features/GridEdit/GridEditCommands.cs`(삭제·되돌리기·복제 명령), `Features/GridEdit/GridEditSession.cs`(`DeleteRow`·`ResetRow` 여러 행 짝), `Features/GridEdit/GridEditBinding.cs`(`ToEditRow`), `Features/GridEdit/GridRowTransfer.cs`, `Features/ResultGrid/ResultGridModel.Selection.cs`·`ResultGridModel.Copy.cs`(선택 행 목록 API), `Features/ResultGrid/ResultGridCommands.cs`(`copyRows` 계열 확인), `docs/design/keymaps/*.json`(단축키), `Shell/Resources/CommandTitles*.json`(이름에 개수) | 새 `tests/Oranda.App.Tests/Features/GridEdit/MultiRowDeleteTests.cs`, `MultiRowCopyTests.cs`, Headless 메뉴 이름 시험, Oracle 통합 시험(가능하면) | 단일 행 삭제의 의미(미리보기 DML 모양), 반영 경로(`ApplyAsync`), `Delete` 키 단독 동작, `Shell/Menus/**` |

### 13.2 설계

- 선택 행 목록: `ResultGridModel` 에 「고른 행 번호」를 화면 순서로 돌려주는 API 하나를 둔다(셀 범위가 걸친 행, 행 머리·Shift·Ctrl 로 고른 행의 합집합, 중복 제거). 정렬·거르기가 걸려 있으면 화면 순서의 원래 행 번호다. 행 복사·삭제·되돌리기·복제가 같은 API 를 쓴다. 구현 레인이 먼저 지금 동작을 시험으로 재서(여러 행을 고르는 방법별로 활성 행만 쓰이는지, 복사가 화면 순서인지) 안 되는 것을 결과에 적는다.
- 삭제: `GridEditSession.DeleteRows(IReadOnlyList<long> rows)` 가 행마다 지우되 `Changed` 이벤트는 마지막에 한 번만 낸다(1만 행에서 격자 갱신 1회). 새 행(`_inserts`)은 목록에서 바로 지우고 뒤 행 번호 이동을 한 번에 처리한다(행마다 당기는 O(n²) 를 피하려고 큰 번호부터 모아 한 번에 정리). 기존 행은 삭제 표시(`_deletes`)만 하고 반영 때 `RowDmlBuilder` 가 DELETE 를 만든다. 되돌리기 `ResetRows` 도 같다. 지금 `DeleteRow` 가 거부하는 행은 같은 규칙으로 거부하고 거부된 수를 `Notice` 로 알린다(일부만 거부돼도 나머지는 표시한다).
- 명령 이름: 이름에 개수를 넣는 방법은 명령의 동적 제목 경로가 있으면 그것을, 없으면 메뉴를 열 때 갱신하는 제목 필드를 쓴다. 하나면 「행 삭제」, 둘 이상이면 「행 {0}개 삭제」. 영어 「Delete Row」·「Delete {0} Rows」. 문구는 `CommandTitles*.json`(TSK-11-01 하나가 고친다).
- 행 복사: `copyRows`·`copyRowsWithHeaders`·`copyAsInsert` 가 선택 행 목록 API 의 화면 순서를 쓴다. 이미 그렇다면 시험만 더한다.
- 복제: `duplicateRow` 가 선택 행 전체를 `GridRowTransfer.DuplicateRowsAsync` 로 복제하는지 확인한다. 복제된 행의 위치는 지금 규칙을 따른다.
- 편집 모드 자동 켜기(D-031): 삭제·복제 명령의 `Enablement` 를 「편집 모드」 에서 「편집 가능한 표면」 으로 넓히고, 실행 때 편집 모드가 꺼져 있으면 F9 와 같은 경로를 먼저 거친 뒤 실행한다. 편집할 수 없는 결과(`Capability.CanEdit == false`)·`readOnlySession`(R6)이면 지금처럼 꺼짐 + 이유 툴팁을 유지한다.
- 단축키(D-030): 지금 `default.json` 에 `oranda.grid.edit.deleteRow` = Alt+Delete(mac `Primary+Alt+Back`), `oranda.grid.edit.duplicateRow` = Primary+Shift+D(모두 `gridFocus`)이다. 여기에 행 삭제 `Primary+Delete`, 행 복제 `Primary+D`(둘 다 `when: gridFocus`)를 더한다. `Primary+D` 는 `oranda.edit.duplicateLine`(`editorFocus`)과 문맥이 달라 겹치지 않지만, 4개 프리셋(default·orange5·plsqldev·sqldev)에서 `gridFocus` 의 `Primary+D`·`Primary+Delete` 사용 여부를 먼저 확인하고 겹치면 그 프리셋은 두고 결과에 적는다. `Delete` 키 단독 처리(`ResultGridControl.Keyboard.cs`)는 지금 그대로다. 사용자 키맵 파일은 건드리지 않는다.
- 성능: 1만 행 선택·삭제·되돌리기·복사에서 UI 스레드가 오래 막히지 않게 한다(위 `Changed` 1회, 선택 행 목록은 구간 합치기). 큰 복사는 글 만들기를 `Task.Run` 으로 하고 클립보드는 UI 스레드에서 쓴다. 측정값을 결과에 적는다.

### 13.3 시험 방침

- 단위·Headless: 셀 범위 3행 삭제, Ctrl 로 떨어진 행, 새 행 + 기존 행 섞어 삭제, 되돌리기(고른 모든 행), 미리보기 DML 에 DELETE n개, 행 복사 클립보드 글(머리글 유무·화면 순서·정렬·거르기 반영), 복제 n개, 편집 모드 자동 켜기, 편집 불가·R6·`readOnlySession` 막힘과 이유 툴팁, 메뉴 이름의 개수(1개·n개, ko·en), 1만 행 시간.
- Oracle 통합(가능하면, 자기 클래스만): 여러 행 삭제 반영 → 행 수 확인, 일부 행을 되돌린 뒤 반영 → 남은 행 수. 시험 준비용 DML 은 통합 시험 안에서만 둔다(제품 경로에 쓰기 SQL 을 더하지 않는다. 이 기능의 DELETE 는 사용자가 편집 모드에서 반영을 누를 때 기존 경로로 나간다).
- 라이트·다크 PNG(오른쪽 클릭 메뉴에 개수 표시)를 떠서 직접 본다(`~/.coord/scripts-find/shots/<레인>/`, 커밋하지 않는다).

### 13.4 수락 기준 (기술)

- 단일 행 삭제·되돌리기·복제·복사의 결과(미리보기 DML·클립보드 글)가 이 Task 전과 같다(회귀 시험).
- 명령 id 집합이 같다(새 id 없음). 키맵은 위 단축키 둘만 늘어난다.
- `dotnet build Oranda.sln -warnaserror` 와 `dotnet test`(Integration·Perf 제외)가 통과한다.

## 14. 같은 DB 여러 세션·워크스페이스별 세션 (2026-10-26 추가, PRD 16, WP-14)

설계 근거 = PRD 16, 결정 D-034~D-038. 경로의 `Features/` 는 `src/Oranda.App/Features/` 아래다. 구현 = 맥북 세션(WP-14). win144 레인 sc-multisess 가 세션 계층 초안을 원격 브랜치 `wip-multisess` 에 남겼다(14.3).

### 14.1 설계 (sc-multisess 초안 1~10, 사용자 승인 「권장안대로」)

1. **ConnectionService**: 「접속」(콤보 번호 1,2…) = 프로필의 기본 작업 세션(`Sessions`, 지금 그대로). 같은 프로필에 「추가 세션」 을 더 열 수 있다(`OpenSessionAsync(profileId)`). 이미 연 접속의 자격 증명(메모리에만, 프로필의 세션이 모두 닫히면 지움)으로 열어 비밀번호를 다시 묻지 않는다. 추가 세션도 `SessionPurpose.Work` 라 실행 상태(`ExecutionState` 의 세션별 상태)·앱 종료 미커밋 확인을 그대로 탄다. 프로필당 8개(기본 + 추가 7) 상한, 넘으면 안내. `SessionsOf(profileId)` = 순서 목록(첫째가 기본). 접속(기본 세션)을 끊었는데 추가 세션이 남아 있으면 처음 연 추가 세션이 같은 자리(콤보 번호)의 접속이 된다(세션은 하나씩 닫는다) — 「세션」 창의 세션 닫기. 툴바 「접속 끊기」 는 접속이면 그 DB 의 세션을 모두 끊는다(추가 세션부터, 미커밋인 세션마다 확인, D-039). 끊긴 추가 세션은 다시 열 자리가 없어 목록에서 빼고 닫는다.
2. **워크스페이스 → 세션**: `SessionRouter` 안에 (워크스페이스, 프로필) → 세션 연결표. 같은 워크스페이스의 탭은 같은 세션(접속이 비어 있는 탭은 묶을 때·활성이 될 때·실행 전에 워크스페이스가 쓰는 프로필로 채운다, D-039). 여러 워크스페이스가 같은 세션에 연결될 수 있다(다 대 일, 트랜잭션도 공유).
3. **모드 2가지만 파일에 기억**: 공유(`shared`, 접속의 기본 세션) / 자기 세션(`own`). 값이 없으면 설정 「워크스페이스마다 새 세션」(`WorkspaceSettings.SessionPerWorkspace`, 기본 켬)을 따른다. 세션 번호는 남기지 않는다.
4. **자기 세션 모드의 첫 실행**: 기본 세션을 아직 아무 워크스페이스도 쓰지 않으면 그것을 차지하고(워크스페이스 하나만 쓰는 사람은 지금과 똑같이 DB 세션 1개, 스키마 브라우저 등 도구 창과 트랜잭션도 지금처럼 같음), 다른 워크스페이스가 쓰고 있으면 추가 세션을 연다. 복원·탭 전환 때는 열지 않는다(첫 실행 때만). 아직 세션이 없는 자기 세션 워크스페이스는 상태가 「첫 실행 때 새 세션」(`ExecutionState.FocusPending`)이라 남의 실행 때문에 `sessionBusy` 로 막히지 않는다. 그동안 읽기용 기능(완성·계획 등)은 접속의 세션을 쓴다. 쓰기·커밋하는 기능(가져오기)은 `ForWriteAsync` 로 먼저 세션을 고른다. 예상 계획의 PLAN_TABLE 기록과 10053 ALTER SESSION 은 첫 실행 전이면 기본 세션에 남는다(커밋·롤백 없음, 남은 위험).
5. **콤보**(SQL·MyBatis 워크스페이스가 활성일 때): 프로필의 세션들 「1 - user@db」·「1 - user@db (2)」… + 「새 세션」. 기존 세션을 고르면 공유 모드로 그 세션에 연결, 「새 세션」 은 즉시 열어 자기 세션 연결. 바꿀 때 이전 세션 미커밋이면 TSK-12-04 확인 창을 그대로. PL/SQL 문서가 활성이면 지금처럼 접속(기본 세션)만 보인다.
6. **세션이 닫히면**(끊김·접속 끊기) 그 세션을 쓰던 워크스페이스는 「접속 안 됨」 → 실행 때 기존 「다시 접속 / 지금 접속으로 / 취소」 창. 접속이 열려 있으면 「다시 접속」 은 새로 접속하지 않고 표시만 지우고 다음 실행이 세션을 다시 고른다(자기 세션이면 새로 연다). 그 프로필의 세션이 하나도 없으면 지금의 「접속 안 됨」 과 같다. 따라가기는 끊긴 접속을 같은 자리에 다시 연 경우(`ReopenedAs`)만이고 그 세션을 함께 쓰던 워크스페이스가 모두 따른다. 접속을 끊어 올라온 추가 세션은 따르지 않는다(D-039, 그 세션에 남의 미커밋이 있을 수 있다).
7. **워크스페이스를 닫을 때**: 그 워크스페이스만 쓰던 추가 세션은 미커밋 확인(세션 단위 한 번, 기존 창 재사용) 뒤 닫는다. 실행 중이면 알리고 닫지 않는다. 기본 세션·다른 워크스페이스와 공유하는 세션은 닫지 않는다. 기본 세션을 그 워크스페이스만 쓰고 미커밋이면 묻는다(다음 워크스페이스가 몰래 물려받지 않게, D-039). 콤보로 다른 세션으로 옮겨 아무도 안 쓰게 된 추가 세션은 닫지 않는다: 사용자가 「세션 닫기」·접속 끊기로 닫을 때까지 남아 콤보·「세션」 창에서 다시 고를 수 있다(2026-10-26 사용자 결정, 상한 8 이 쌓임을 막는다).
8. **공유 세션이 실행 중일 때 막힌 워크스페이스**: 실행을 누르면 안내 막대 「이 세션은 다른 워크스페이스에서 실행 중 — [새 세션에서 실행] [기다리기]」(D-036). 새 세션에서 실행 = 「새 세션」 과 같은 경로 후 그 실행을 다시 보낸다. 「새 세션」·첫 실행 열기는 작업 공간별 여는 중 표지로 한 번만 돌고(두 번 눌러도 세션 하나·실행 한 번, 단추는 누르는 즉시 꺼짐), 여는 사이 콤보로 고른 세션은 덮지 않는다(막 연 세션을 닫는다). 이 워크스페이스가 시작한 실행 중에는 함께 쓰는 세션이어도 옮기지 않는다(D-033·D-039).
9. **상태 줄**: 「워크스페이스 세션 · 연결됨」, 「공유 세션 · 다른 워크스페이스 1개와 함께 · 연결됨」, 「새 세션 · 첫 실행 때 연결」. 세션 모니터 구분용으로 콤보 툴팁·세션 도구 창에 SID.
10. **PL/SQL 작업 공간**은 자동 자기 세션 대상에서 뺀다(탭마다 접속을 이미 고름, `IConnectionDocument`). MyBatis 작업 공간은 SQL 작업 공간과 같은 규칙(`SqlWorkspaceViewModel`). 조회 전용 Role·`readOnlySession`·ExecutionPolicy 는 세션 프로필 기준이라 세션마다 그대로 적용.
11. **「세션」 창에서 고르기·콤보 표시**(TSK-14-06): 줄 두 번 누르기·Enter = 우클릭 「이 세션 쓰기」(활성 SQL·MyBatis 작업 공간을 그 세션에 묶는다, `SessionsViewModel.ActivateAsync`). 이미 쓰는 세션이거나 PL/SQL 문서가 활성이면 아무것도 안 한다. 툴바 콤보는 고른 줄을 UI 대기열 한 박자 뒤에 묶고(`ConnectionComboViewModel.Selected` → `ChooseAsync`), 목록을 다시 만들 때는 먼저 선택을 푼다: 상자의 선택 변경 안에서 `Entries` 를 비웠다 채우면 Avalonia ComboBox 가 고른 줄(SelectedItem)을 들고도 보이는 칸(SelectionBoxItem)이 비어 빈 「접속 안 됨」 을 보였다(줄 툴팁에 쓰는 워크스페이스가 있어 세션을 옮길 때마다 목록이 다시 만들어진다).
12. **쓰기 경로의 세션·실행 표시**(TSK-14-08, D-040): 격자 반영은 결과를 만든 세션이 지금 그 탭의 세션(`SessionFor(tab)`)일 때만 하고 다르면 「다시 조회」 안내로 거절한다(`GridEditSession.CurrentSession`). 이력 「다시 실행」 은 활성 탭과 같은 접속이면 그 작업 공간의 세션(`ForWriteAsync`), 다른 접속이면 그 접속의 기본 세션이다. 가져오기는 세션에 미커밋이 있으면 먼저 묻고(커밋/롤백/취소), 「마지막에 커밋」 실패·취소는 시작 SAVEPOINT(`ORANDA_IMPORT`)까지만 되돌린다. 격자 반영·가져오기·LOB 저장·이력 다시 실행·PL/SQL 실행 창은 실제로 쓴 세션 ID 로 `BeginRun(sessionId, owner)`/`EndRun` 하고, 실행 표시는 세션별 실행 수라 겹친 실행 하나가 끝나도 바쁨이 남는다. 열 순서·무효 객체 컴파일·휴지통·PL/SQL 실행 창(스키마 브라우저에서)은 `SessionRouter.ForTool`: 활성 SQL 작업 공간이 같은 접속이면 그 세션. 세션 닫기(`ConnectionService.CloseAsync`)는 Dispose 전에 2초 제한 ROLLBACK 을 보낸다(끊긴 세션 제외).
13. **보이는 세션 = 실행 세션**(TSK-14-10, D-042): 첫 실행 때 새 세션을 열 차례인 워크스페이스(`IsPendingOwn`)는 콤보가 접속 세션 줄을 고르지 않고 「새 세션(첫 실행 때) — user@db」, 상태 줄 1번 구간이 「… · 새 세션(첫 실행 때)」(툴팁에 SID 없음)을 보인다. 그때 콤보·「세션」 창(더블클릭·Enter)으로 접속 세션을 고르면 명시적 공유로 그 세션에 묶는다. 「세션」 창: 지금 활성 워크스페이스가 쓰는 세션에 「◀ 이 워크스페이스」 표지(선택과 따로, `InUseSession`), 아직 실행 전이라 묶이지 않았지만 첫 실행 때 쓸 접속 세션은 「사용 예정 · 워크스페이스 N개(첫 실행 때)」(`PlannedWorkspacesOn`), 「이 세션 쓰기」 는 효과 있을 때만(전용 세션 탭·탭 없는 워크스페이스면 꺼짐, `CanUseSession`). 순번 「(n)」 은 세션을 열 때 정해 닫아도 밀리지 않는다(그 DB 세션이 모두 닫히면 다시 1부터, 「새 세션」 줄은 다음 번호). 닫힌 콤보의 화살표·휠은 줄을 넘기지 않고(화살표는 목록을 펼침) 펼친 목록에서 고를 때만 묶는다. 설정 「워크스페이스마다 새 세션」 을 바꾸면 표시를 다시 맞춘다. 툴바·메뉴·「세션」 창의 커밋·롤백은 그 세션을 워크스페이스 둘 이상이 쓰면 함께 확정되는 워크스페이스 이름을 보이고 한 번 묻는다(자동 커밋·미커밋 확인 창의 커밋은 묻지 않음). 문서 영역을 나눈 화면에서 활성 문서는 마지막으로 포커스를 가진 문서다(도구 창을 눌러도 남고, 다른 영역의 보이는 탭을 눌러 포커스만 옮겨도 바뀜을 알린다, `DockWorkspace.ActiveDocument`).
14. **한 연결 = 잠금 하나**(TSK-14-09, D-041): 세션 연결에 보내는 모든 명령(실행·결과 페이지·OUT 커서·커밋·롤백·Keep Alive·메타데이터·DDL·계획·튜닝·모니터·LOB 읽기)은 `OracleSession.CommandGate` 를 쥔 동안에만 보낸다. 결과 페이지·메타데이터 목록은 페이지·행마다 잡고 재진입하지 않는다. 사용자가 연 도구 창은 기다리되 취소 가능, 자동 완성·매개변수 힌트는 `SessionWait.Brief`(100 ms) 안에 못 잡으면 서버에 보내지 않음(`SessionBusyException`), 메타데이터 백그라운드 갱신·Keep Alive 는 바쁘면 건너뜀.
15. **실행 중 표지**(TSK-14-13, D-043): 실행 컨트롤러가 실행을 시작한 SQL·MyBatis 탭(`SqlEditorViewModel.Run`, `Shell/RunMarker.cs`)과 PL/SQL 실행 창을 연 PL/SQL 탭에 표지를 켜고 끝·오류·취소 모두에서 끈다(겹친 실행은 센다). 탭 머리 앞 주황 점(깜빡이지 않음, 툴팁 「실행 중 · 경과 n초」), 그 탭이 든 작업 공간의 문서 탭 머리 아이콘 자리에도 점(`IRunningDocument` → `PanelDocument.IsRunning`, 툴팁 = 탭마다 「이름 실행 중 · 경과 n초」), SQL 탭 목록 메뉴에도 점, 결과 영역 위 띠 「실행 중 · 경과 n초」(진행 막대와 같은 150 ms 지연). 경과 초는 실행 중인 표지만 1초 타이머 하나로 센다. 다른 탭을 보고 있어도 남는다. 끝났을 때 「끝남」 표지는 두지 않는다(점이 사라지는 것이 끝남 신호). 결과 그리드 다시 조회·행 수 세기 등 결과 영역에서 바로 도는 실행은 결과 창의 진행 막대만 쓴다.

### 14.2 바뀌는 곳

| Task | 소유 파일 | 시험 |
|---|---|---|
| TSK-14-01 | `Features/ConnectionManager/ConnectionService.cs`(`IConnectionService` 기본 구현 멤버로 넓혀 시험 가짜 2개를 고치지 않는다) | `tests/Oranda.App.Tests/Features/ConnectionManager/WorkspaceSessionTests.cs` 앞부분 |
| TSK-14-02 | `Features/ConnectionManager/SessionRouter.cs`·새 `SessionRouter.Workspaces.cs`, `Features/Execution/ExecutionState.cs`(`FocusPending`), `ExecutionController.cs`·`ExecutionController.Run.cs`(`PrepareSession`·`UseConnection` 의존), `ExecutionCommands.cs`(배선), `ConnectionManagerCommands.cs`(끊기 대상), `Features/SqlEditor/SqlWorkspaceViewModel.cs`·`SqlWorkspaceKind.cs`·`SqlEditorHost.Persistence.cs`, `src/Oranda.Core/Workspaces/Workspace.cs`(`Session`), `src/Oranda.Core/Settings/AppSettings.cs`(`SessionPerWorkspace`) | `WorkspaceSessionTests.cs` 뒷부분 |
| TSK-14-03 | `Features/ConnectionManager/ConnectionCombo.cs`(항목 = 세션, 「새 세션」), `Features/Execution/ExecutionState.cs`·`ExecutionStatusItems.cs`(상태 줄 글), 안내 막대(결과 영역 또는 편집기 위, 기존 안내 막대 모양), 설정 창 「워크스페이스」 쪽 확인란, 문구 파일 | Headless 콤보·상태 줄·안내 막대·설정, 라이트·다크 PNG |
| TSK-14-04 | 새 `Features/Sessions/`(도구 창 ViewModel·View·명령), 도킹 기본 배치(숨김) | Headless 도구 창·우클릭 명령 |
| TSK-14-05 | (제품 코드 없음) | `tests/Oranda.App.Tests/Integration/WorkspaceSessionFlowTests.cs` + 결과서 |

### 14.3 wip-multisess 초안 (win144 sc-multisess, 2026-10-26)

들어 있는 것(빌드 `-warnaserror` 통과, 단위 16개·관련 클래스 143개·통합 2개(26ai·11.2) 통과):
- `IConnectionService`: `SessionsOf(Guid)`, `AllSessions`, `IsOpen(ISession)`, `OpenSessionAsync(Guid, CancellationToken)`(인터페이스 기본 구현 = 하나뿐/실패). `ConnectionService.MaxSessionsPerProfile = 8`, `_extra` 목록, `_secrets`(프로필별 `SecureCredential`, 접속 성공 때 기억·마지막 세션 닫힐 때 지움), `Switch(extra)` = 그 접속으로, `Remove` 의 승격, 끊김·닫힘 이벤트가 추가 세션도 본다.
- `SessionRouter`(partial): `SessionFor`(전용 → 연결표 → 접속), `MissingProfileOf`(닫힌 연결 = 접속 안 됨), `LabelOf`(「(2)」), `AllSessions`·`SessionsOf`, `ModeOf`, `BoundSession`, `WorkspacesOn`, `IsPendingOwn`, `PrepareAsync`(첫 실행 묶기), `NewSessionAsync`(「새 세션」), `UseConnection`, `ReconnectAsync`(접속이 열려 있으면 표시만 지움), `Select`·`SelectAsync`(고른 세션에 묶고 모드 기억, 추가 세션도 받음), `Refocus`(→ `FocusPending`), 닫기 가드(`SqlCloseReason.Workspace`)·`CloseConfirmFor`(문서 탭 ×)·`WorkspaceClosed` 처리, `SessionPerWorkspace`·`ConfirmUncommitted` 속성(조립이 설정·`ConfirmConnectionUncommittedAsync` 를 넣음).
- `ExecutionState.FocusPending(profile)`·`FocusedPending`, `ExecutionController.FocusedSession` 이 그때 null(남의 세션을 커밋·취소하지 않음).
- `ExecutionDeps.PrepareSession`·`UseConnection`, `EnsureConnectionAsync` 가 끝에서 `PrepareSession` 을 부른다. `WorkSessions` = `AllSessions`(종료 확인이 추가 세션도 묻는다).
- `Workspace.Session`(`own`/`shared`/null), `WorkspaceSessionMode`·`WorkspaceSessionModes`, `SqlWorkspaceViewModel.SessionMode`, `WorkspaceSettings.SessionPerWorkspace`(기본 true).
- 시험: `WorkspaceSessionTests`(16), `WorkspaceSessionFlowTests`(통합 2: SID 다름·A 5초 중 B 58ms(26ai)/112ms(11.2)·트랜잭션 격리·함께 쓰기 SID 같음·새 세션 SID 다름), `StubSessionManager.PublishOpened`.

남은 것(맥북):
- 화면 전부: 콤보 항목(추가 세션·「새 세션」, 지금은 추가 세션을 쓰는 탭이면 콤보가 「전용 세션 · …」 빈 글로 보인다), 상태 줄 글(지금 `FocusPending` 은 「연결됨」), 안내 막대(D-036, 지금은 막힌 쪽 실행이 지금처럼 조용히 꺼짐), 설정 확인란, 세션 도구 창(D-034), 문구 파일(ko·en).
- 접속 관리자 「같은 DB 에 세션 하나 더」 진입점은 시안대로 콤보·세션 도구 창으로 대신할지 확정.
- 자동 저장 표지(`SqlWorkspaceViewModel` 의 변경 감지)에 `SessionMode` 를 넣을지(지금은 모드만 바꾸면 자동 저장이 돌지 않을 수 있다).
- 탭이 0개인 작업 공간을 닫으면 가드가 돌지 않아 미커밋 확인 없이 추가 세션을 닫는다(드라이버가 롤백) — 드문 경우, 막을지 결정.
- PL/SQL 문서가 콤보로 추가 세션을 고르지 못하게(목록에서 빼기) — 콤보 작업 때.
- 초안 상태에서 App 단위 시험 전체(Integration·Perf 제외) 3,703개 중 3,702개 통과 — 실패 1개(`ExecutionTests.Bind_panel_focuses_the_value_of_a_requested_variable_and_the_name_of_a_new_row`)는 따로 돌리면 통과하는 포커스 시험(부하 때 흔들림, sc-itest 정리 대상)이다. 다른 시험 프로젝트는 빌드만 확인했다.
- 상태(2026-10-26, 맥북): 위 목록은 TSK-14-01~03(세션 계층·워크스페이스 연결·콤보·상태 줄·안내 막대·설정·문구)과 TSK-14-04(세션 도구 창)에서 반영했다. 접속 관리자 진입점은 넣지 않는다(사용자 확정). 안내 막대는 다른 워크스페이스가 시작한 실행 때문에 바쁠 때만 실행 명령을 켜 두고(`sessionBusyElsewhere`) 누르면 막대를 띄우며, 그때 취소 명령은 꺼진다(남의 실행을 취소하지 않는다).

### 14.4 시험 방침

- 단위·Headless: 워크스페이스 둘 = 세션 둘, 같은 워크스페이스 탭 = 같은 세션, 워크스페이스 하나 = DB 세션 1개, 실행 중 다른 워크스페이스 실행 가능(`sessionBusy` 세션 기준·`FocusPending`), 설정 끄면 지금 동작, 워크스페이스 선택이 설정보다 먼저, 함께 쓰기·새 세션 옮기기, 아무도 안 쓰게 된 추가 세션은 남고 다시 고를 수 있음, 닫힌 세션 → 접속 안 됨 → 다시 고르기, 세션 넷을 콤보·「세션」 창(두 번 누르기·Enter)으로 오갈 때 콤보 상자(SelectedItem·SelectionBoxItem)가 고른 세션(앱 셸·MyBatis 작업 공간 포함, `SessionSwitchTests`), 워크스페이스 닫기 미커밋 확인(세션 단위 한 번·공유 세션은 안 물음), 복원 때 세션 안 엶·파일 왕복, 종료 확인 라벨, 상한 8, 비밀번호 다시 안 물음.
- Oracle 통합(podman 26ai·11.2): 같은 프로필 두 세션 SID 다름, A 5초 실행 중 B 1초 안, A 미커밋 INSERT 가 B 에 안 보임, 함께 쓰기 SID 같음·트랜잭션 공유, 새 세션으로 옮기면 SID 다름, 공유 세션 실행 중 다른 워크스페이스 `sessionBusy`. 11.2 는 `dbms_session.sleep` 이 없고 `dbms_lock` 권한을 SYSTEM 이 줄 수 없어 `dbms_utility.get_time` 바쁜 반복으로 5초를 만든다.
- 시작 경로에서 Oracle 어셈블리를 깨우지 않는다(`Startup_does_not_load_oracle_or_antlr_assemblies`): 연결표·설정 읽기는 라우터가 생길 때(접속 서비스와 함께)만.

### 14.5 수락 기준 (기술)

- 설정 기본값에서 워크스페이스 하나만 쓰면 DB 세션 수·동작이 이 Task 전과 같다(회귀 시험).
- 설정을 끄면 이 Task 전과 같다.
- `dotnet build Oranda.sln -warnaserror` 와 `dotnet test`(Integration·Perf 제외)가 통과한다.
