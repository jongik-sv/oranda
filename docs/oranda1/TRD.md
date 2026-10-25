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

공용 형은 거르지 않는다: 꼬리표는 등록 항목(명령·패널·상태 칸·시작 작업)에만 붙는다. 그래서 세션 모니터를 꺼도 `MonitorAccessState`·`MonitorTableBinder`·`MonitorProfileDialog`(조사 4.1 「다」 A1)를 Lock·SQL 모니터·튜닝·블로커·접속 후 권한 키(`ConnectionManager/ConnectionManagerCommands.cs:69`)가 그대로 쓰고, 계획 모델·뷰(조사 4.1 「다」 T1)도 Lock 모니터·SQL 모니터·튜닝·MyBatis 가 그대로 쓴다. 실행 컨트롤러가 쓰는 컴파일 오류 형(`Features/Execution/ExecutionController*.cs`)도 그대로다.

### 10.3 설정 스키마

```jsonc
// settings.json
"features": {
  "roles": ["sqlDev", "tuner"],   // null 또는 키 없음 = Role 을 고른 적 없음 → 모든 기능
  "enabled": ["trace"],           // 직접 켬
  "disabled": ["flashback"]       // 직접 끔
}
```

- `FeaturesSettings { IReadOnlyList<string>? Roles; IReadOnlyList<string> Enabled = []; IReadOnlyList<string> Disabled = []; }`. 목록 비교는 순서 무시 값 비교로 `Equals`/`GetHashCode` 를 직접 쓴다(`SettingsStore.ChangedSections` 가 레코드 `!=` 로 섹션 바뀜을 보기 때문, `src/Oranda.Platform/Storage/SettingsStore.cs:115`).
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

1. 바탕: `Roles == null` 이면 모든 기능, 아니면 고른 Role 들의 기본 켬 합집합(`Roles == []` 이면 빈 집합 = 공통만).
2. `+ Enabled − Disabled`.
3. `ReadOnlyRole = Roles` 가 정확히 `["viewer"]`. 참이면 `WritesOnly` 기능을 끈다(손으로 고친 설정 파일도 같다).
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
- 시작 화면(`Shell/StartPage.cs:210`): `myBatis` 가 꺼지면 「작업 공간(.md)이나 SQL 파일을 열면 문장마다 탭이 생깁니다.」 Role 을 고른 적 없으면 「Role 을 고르면 메뉴가 단순해집니다 — Role 고르기…」 링크(`oranda.tools.roles`).

#### 10.7.6 설정 창 「기능」 탭 (TSK-07-04, D-011)

- `Shell/Settings/SettingsWindow.axaml` 의 「일반」(`:75`) 다음에 「기능」 탭. 위: 「모든 기능 쓰기(Role 고르지 않음)」/「Role 로 고르기」 라디오, Role 확인란 6개(이름 + 한 줄 설명). 아래: 「기능 세부」 묶음별 확인란 25개, 상태 표시(「Role 기본」 「직접 켬」 「직접 끔」), `Requires` 못 채움은 흐림 + 「{0} 이(가) 켜져 있어야 합니다」, 조회 전용만이면 `WritesOnly` 흐림 + 「조회 전용만 고른 상태에서는 켤 수 없습니다. 다른 Role 을 함께 고르세요.」, 「Role 기본값으로 되돌리기」.
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
| 설정 탭 | 기능, Role, 기능 세부, 모든 기능 쓰기(Role 고르지 않음), Role 로 고르기, Role 기본, 직접 켬, 직접 끔, Role 기본값으로 되돌리기, 재시작하면 적용됩니다: {0}, {0} 이(가) 켜져 있어야 합니다, 조회 전용만 고르면 DML·DDL·PL/SQL 실행을 막습니다, 조회 전용만 고른 상태에서는 켤 수 없습니다. 다른 Role 을 함께 고르세요., Diagnostics·Tuning Pack 보유 선언은 접속 프로필에서 합니다 |
| 명령 | `Cmd.oranda.tools.roles` = Role·기능 고르기… |
| 실행 거부 | 10.7.4 문구 |
| 블로커 칸 | Lock 모니터 기능이 꺼져 있습니다. 설정 > 기능에서 켤 수 있습니다., 설정 열기 |
| 대체 경로 | [읽기 전용] {0}, PL/SQL 편집기 기능이 꺼져 있어 읽기 전용으로 열었습니다, 10.7.5 의 MyBatis 안내 |
| 시작 화면 | 10.7.5 의 두 문구, Role 고르기… |

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
