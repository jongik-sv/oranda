# Core 서비스 계약 (TSK-02-03)

> 근거: TRD 3·4·6·7·4.10, docs/design/data-model.md(TSK-02-01), decisions D-004.
> 이 문서가 Core 계약의 정본이다. 코드 = `src/Oranda.Core/**`(Catalog/·Shell/ 제외). 계약 시험 = `tests/Oranda.Core.Tests/Contracts/**`.
> 카탈로그(`Oranda.Core.Catalog`)는 [catalog.md](catalog.md), 명령·도구 창·키맵(`Oranda.Core.Shell`)은 UI 계약 문서가 정본이다.

---

## 1. 영역별 계약 요약

모든 영역은 폴더 = 네임스페이스(`Oranda.Core.<영역>`)다. Core 는 Avalonia·ODP.NET·SQLite 를 참조하지 않는다(Architecture 시험).

| 영역(네임스페이스) | 인터페이스 | 주요 모델 | 구현 위치 |
|---|---|---|---|
| `Connection` | `IConnectionImporter` | `ConnectionProfile`(확장), `ConnectionsDocument`, `NlsSettings`, `ServerInfo`(확장), `ServerCapabilities`, enum `AuthMode`·`ConnectionEnvironment`·`LicensePack`, `ImportedConnection` | Platform |
| `Sessions` | `ISessionManager`, `ISession` | enum `SessionPurpose`(Work·Monitor)·`SessionState`, `UnsupportedServerException` | Oracle |
| `Execution` | `IExecutionService`, `IExecutionPolicy`, `IExecutionInteraction`, `ISqlPlusCommandProcessor` | `SqlStatement`, enum `StatementKind`, `BindValue`, `ExecutionOptions`, `ExecutionResult`, `DbError`, `PolicyContext`, `PolicyDecision`, enum `ScriptErrorMode`·`ScriptErrorAction`·`PolicyRule` | Oracle(실행), Core(정책), App(대화) |
| `Grid` | `IResultSet`, `IRowEditor`, `ILobAccessor` | `ResultColumn`, `ResultPage`(열 지향), `LobValue`, `EditCapability`, `TableKeyInfo`, `RowChange`, `ApplyResult` | Oracle, Language |
| `Metadata` | `IMetadataProvider`, `IDatabaseInfoProvider` | `DbObjectRef`, `ObjectQuery`, `TableInfo`, `ColumnInfo`, `ConstraintInfo`, `IndexInfo`, `DdlOptions`, `ProcedureInfo`, `ArgumentInfo`, `ObjectDetail`, `DetailSection`, `CompileError`, `MetaSource`, `MetaObject` | Oracle |
| `Monitoring` | `IMonitorService` | `SessionKey`, `SessionFilter`, `SessionRow`, `BlockingTree`, `SessionDetail`, `TraceOptions`, `TopSqlCriteria`, `TopSqlRow`, `SqlDetail` | Oracle |
| `Plans` | `IExecutionPlanService` | `CursorRef`, `ExecutionPlan`, `PlanStep`, enum `PlanRisk` | Oracle |
| `Transfer` | `IDataTransferService` | `ExportOptions`, `ImportOptions`, `ColumnMapping`, `TransferSummary` | Oracle |
| `Language` | `ILanguageService`, `ICompletionProvider`, `ISymbolLocator` | `SplitOptions`, `Diagnostic`, `CompletionContext`, `CompletionItem`, `CompletionResult`, `SymbolReference` | Language |
| `Formatting` | `ISqlFormatter` | `FormatResult`, `FormatterProfile`, `FormatterOptions` 외 옵션 record·enum(data-model 2.3) | Language |
| `Security` | `ICredentialStore`, `ISecretRedactor` | `SecureCredential`(메모리 전용, Dispose 시 0 으로 지움) | Platform, Language |
| `Storage` | `IAppDataPaths`, `ISettingsStore`, `IConnectionStore`, `IFormatterProfileStore`, `IWorkspaceStore`, `IHistoryStore`, `IMetadataCache` | `StoredDocument<T>`, `HistoryQuery` | Platform |
| `Settings` | `ISettingsMigration` | `AppSettings` 외 섹션 record, enum `Theme`, `SchemaVersions`, `SettingsFileKinds` | Platform |
| `Workspaces` / `History` | — | `Workspace`, `WorkspaceTab`, `TextPosition` / `HistoryEntry`, enum `HistorySource`·`HistoryStatus` | — |
| `Events` | `IEventBus` | `OrandaEvent`(바탕) + TRD 7장 이벤트 9종 | Core `EventBus` |
| `Extensibility` | `IEditorContext`, `IOutboundConsent` | `EditorSnapshot`, `TextEdit`, `OutboundRequest`, enum `ConsentDecision` | App |
| `Composition` | `IStartupTask` | `OrandaServiceAttribute`, `OrandaServiceAssemblyAttribute`, enum `ServiceLifetimeKind`·`ServicePlatform`·`StartupPhase` | — |
| `Text` / `Serialization` | — | `TextRange`(0부터 오프셋) / `OrandaJsonContext`, `UtcDateTimeOffsetConverter` | — |

### 1.1 TRD 6장과 다른 점

| TRD 6장 | 계약 | 이유 |
|---|---|---|
| `ExecuteAsync(s, stmt, binds, ct)` | `ExecuteAsync(s, stmt, binds, ExecutionOptions? options, ct)`(스크립트도 같음) | 정책 관문·이력에 출처(`HistorySource`)와 카탈로그 라이선스 요구가 필요. null = 편집기 실행 |
| `IRowEditor.Analyze(SqlStatement select, …)` | 매개변수 이름 `query` | CA1716(예약어 `Select`) |
| `IMetadataProvider` 3개 메서드 | + `ListSchemasAsync`, `GetProceduresAsync`, `GetObjectDetailAsync`, `GetSourceAsync`, `GetCompileErrorsAsync` | TRD 4.7 「스키마 목록·패키지 멤버·인자」, SCH 상세, PL/SQL 편집(TSK-08-01) |
| `IMonitorService` 4개 메서드 | + `GetSessionDetailAsync`, `SetTraceAsync`, `GetSqlDetailAsync` | SESS 상세·트레이스, SQLM 전문·바인드 |
| `ISession` | + `Purpose`, `DbKey` | 전용 세션 구분, 메타데이터 캐시 키(data-model 3.3) |
| (없음) | `IExecutionInteraction`, `ISqlPlusCommandProcessor`, `ILobAccessor`, `IExecutionPlanService`, `IDataTransferService`, `IDatabaseInfoProvider`, `ICompletionProvider`, `ISymbolLocator`, `IConnectionImporter`, 저장소 인터페이스 7종 | 한 인터페이스를 여러 Task 가 나눠 구현하지 않게 기능 단위로 나눔 |
| `ServerInfo` | 끝에 `DbUniqueName`, `SessionUser`, `IsAutonomous`, `Edition`, `ManagementPackAccess`, `IsRac` (기본값 있음) | TRD 4.1 접속 점검 추가 항목. 기존 위치 매개변수 호출은 그대로 컴파일 |

### 1.2 실행 흐름에서의 역할

```
편집기 ─ ILanguageService.GetStatementAt ─▶ SqlStatement
       ─ IExecutionService.ExecuteAsync(options.Source=Editor)
            ├ 치환 변수 → IExecutionInteraction.AskSubstitutionsAsync
            ├ IExecutionPolicy.EvaluateAsync → Confirm 이면 IExecutionInteraction.ConfirmAsync, Deny 면 Status=Denied
            ├ Kind=SqlPlus 면 ISqlPlusCommandProcessor 로
            └ 드라이버 실행 → ExecutionResult(ResultSet = IResultSet)
       이벤트: PolicyDecisionMade, StatementExecuted, TransactionStateChanged, DbmsOutputReceived
```

---

## 2. 이벤트 버스 (TRD 7)

| 항목 | 규칙 |
|---|---|
| 구독 | `Subscribe<TEvent>(object owner, Action<TEvent> handler)` → `IDisposable`. 버스는 `owner` 를 약하게 잡고 처리기는 owner 수명에 묶는다(ConditionalWeakTable). owner 가 수거되면 자동 해지 |
| 발행 | `Publish(e)` — 발행 스레드에서 동기 호출. UI 스레드 전달은 구독자가 선택. 처리기 예외는 다른 구독자에게 전파하지 않고 로그 |
| 형식 | 모든 이벤트는 `OrandaEvent`(`OccurredAt` UTC) 를 잇는 sealed record |
| 구현 | **결정: Core 에 기본 구현 `EventBus` 를 둔다**(TRD 3.2 「Core = 이벤트 버스」). 지금은 빈 틀이며 TSK-03-01 이 채운다. 시험은 `Oranda.Testing.FakeEventBus` |

| 이벤트 | 필드 |
|---|---|
| `SessionOpened` | `SessionId, Profile, Server, Purpose` |
| `SessionClosed` | `SessionId` |
| `SessionBroken` | `SessionId, HadPendingTransaction, Error` |
| `TransactionStateChanged` | `SessionId, Pending` |
| `StatementExecuted` | `SessionId, Statement, Result, Options` |
| `DbmsOutputReceived` | `SessionId, Lines` |
| `MetadataInvalidated` | `DbKey, Objects`(빈 목록 = 그 DB 전체) |
| `PolicyDecisionMade` | `SessionId?, Statement, Decision` |
| `SettingsChanged` | `Section`(`AppSettings` 섹션의 JSON 이름) |

---

## 3. 자동 수집 (서비스 등록)

기능 Task 는 공유 진입점(App 조립 코드)에 줄을 더하지 않는다.

| 장치 | 위치 | 규칙 |
|---|---|---|
| `[OrandaService(typeof(I…))]` | 구현 클래스 | 등록할 서비스 형식. 여러 개 달 수 있음. `Lifetime`(기본 Singleton), `Platform`(Any·Windows·MacOS). internal 클래스 허용 |
| `[assembly: OrandaServiceAssembly]` | `Oranda.Oracle`·`Oranda.Language`·`Oranda.Platform` 의 어셈블리 표지 파일 | 앱 조립은 이 특성이 붙은 어셈블리만 훑는다 |
| `IStartupTask` | 구현 클래스 + `[OrandaService(typeof(IStartupTask))]` | 첫 프레임 뒤·유휴 시간에 한 번 실행(보존 정책, ANTLR 워밍업, 이벤트 구독 시작). 시작 경로에서는 돌지 않는다(D-006) |
| `ISettingsMigration` | 같은 방식 | 파일 종류·버전 단계별 변환기 자동 수집(data-model 4.1) |
| `IConnectionImporter` | 같은 방식 | 가져오기 원본 형식 자동 수집 |

- 스캔 실행 코드(어셈블리 훑기 → DI 등록)는 App(UI 셸 계약)이 갖는다. Core 에는 특성·인터페이스만 있다.
- Core 자신의 구현(`EventBus`, `ExecutionPolicy`)은 스캔 대상이 아니다. 앱 조립이 직접 등록한다(Core 에 DI 패키지 의존을 두지 않기 위해).
- `Architecture.Tests/ServiceRegistrationTests` 가 「표지 특성이 있음」과 「등록 클래스가 서비스 형식을 구현함」만 검사한다. 등록 목록은 단정하지 않는다.
- 빈 틀 구현 클래스는 이미 특성을 달고 있고 메서드는 `NotImplementedException("TSK-xx-xx")` 를 던진다. 기능 Task 는 몸통만 채운다.

---

## 4. 공용 시험 헬퍼 (`tests/Oranda.Testing`)

Core 만 참조하는 일반 라이브러리. 시험 프로젝트가 `ProjectReference` 로 쓴다(현재 Core.Tests·Language.Tests·Platform.Tests).

| 형식 | 계약 | 쓰임 |
|---|---|---|
| `FakeSession` | `ISession` | 상태·미커밋 여부를 시험이 직접 바꿈, Commit/Rollback 횟수 기록 |
| `FakeSessionManager` | `ISessionManager` | 열 때마다 `FakeSession`, `OpenFailure` 로 실패 흉내 |
| `FakeResultSet` | `IResultSet` | 메모리 행 → 페이지, `FromText` 간편 생성 |
| `FakeExecutionService` | `IExecutionService` | 요청 기록(`Calls`), `Responder` 로 결과 지정 |
| `FakeExecutionInteraction` | `IExecutionInteraction` | 확인·치환·오류 처리 답을 미리 정함 |
| `FakeEventBus` | `IEventBus` | 발행 기록(`Published`, `PublishedOf<T>`), 처리기를 강하게 잡음 |
| `FakeMetadataProvider` | `IMetadataProvider` | 사전으로 답함 |
| `InMemoryCredentialStore` | `ICredentialStore` | 메모리 저장 |
| `TestData` | — | 기본 `ConnectionProfile`·`ServerInfo` |

기능 Task 가 필요한 가짜를 더할 때는 `tests/Oranda.Testing/` 에 **새 파일**로 더한다(기존 파일 수정은 그 파일 담당과 겹치지 않는 범위에서).

---

## 5. 시험 위치 규칙

| 대상 | 시험 프로젝트 |
|---|---|
| Core 순수 로직 | `tests/Oranda.Core.Tests` |
| Language | `tests/Oranda.Language.Tests` |
| Platform(저장소·자격 증명·가져오기) | `tests/Oranda.Platform.Tests`(이번에 추가) |
| Oracle, DB 없이 | `tests/Oranda.Oracle.Tests` |
| Oracle, 실제 DB | `tests/Oranda.Oracle.IntegrationTests`(`[Trait("Category","Integration")]`, D-005) |

---

## 기능 Task 편집 지점

각 Task 는 아래 「소유 파일」과 「시험 파일」만 고친다. 표의 파일은 모두 빈 틀로 이미 있다. 한 파일은 한 Task 만 갖는다(같은 클래스를 나눠 구현할 때는 partial 파일로 나눴다). 폴더로 적은 곳은 그 폴더 안 새 파일도 그 Task 소유다.

| Task ID | 소유 파일 | 시험 파일 | 구현할 인터페이스 |
|---|---|---|---|
| TSK-03-01 앱 셸·설정 저장 | `src/Oranda.Core/Events/EventBus.cs`, `src/Oranda.Platform/Storage/AppDataPaths.cs`, `src/Oranda.Platform/Storage/JsonDocumentFile.cs`, `src/Oranda.Platform/Storage/SettingsStore.cs` | `tests/Oranda.Core.Tests/Events/EventBusTests.cs`, `tests/Oranda.Platform.Tests/Storage/SettingsStoreTests.cs` | `IEventBus`, `IAppDataPaths`, `ISettingsStore` |
| TSK-03-02 자격 증명 저장소 | `src/Oranda.Platform/Credentials/DpapiCredentialStore.cs`, `src/Oranda.Platform/Credentials/KeychainCredentialStore.cs` | `tests/Oranda.Platform.Tests/Credentials/CredentialStoreTests.cs` | `ICredentialStore` |
| TSK-03-03 세션 관리·접속 점검 | `src/Oranda.Oracle/Sessions/**`(빈 틀 `OracleSessionManager.cs`), `src/Oranda.Oracle/Connection/ServerProbe.cs`, `src/Oranda.Oracle/Connection/ConnectionProfileExtensions.cs` | `tests/Oranda.Oracle.IntegrationTests/Sessions/SessionManagerTests.cs`, `tests/Oranda.Oracle.IntegrationTests/Connection/**`, `tests/Oranda.Oracle.IntegrationTests/Integration/ServerProbeIntegrationTests.cs` | `ISessionManager`, `ISession` |
| TSK-03-04 접속 관리자 | `src/Oranda.Platform/Storage/ConnectionStore.cs` | `tests/Oranda.Platform.Tests/Storage/ConnectionStoreTests.cs` | `IConnectionStore` |
| TSK-03-05 접속 가져오기·명령줄 | `src/Oranda.Platform/Import/**`(`SqlDeveloperImporter.cs`, `TnsNamesImporter.cs`, `OrangeListImporter.cs`, `CommandLineConnect.cs`) | `tests/Oranda.Platform.Tests/Import/ConnectionImportTests.cs` | `IConnectionImporter` |
| TSK-04-01 L1 렉서·문장 분리 | `src/Oranda.Language/LanguageService.cs`, `src/Oranda.Language/Lexing/**` | `tests/Oranda.Language.Tests/Lexing/StatementSplitterTests.cs` | `ILanguageService.Split`·`GetStatementAt` |
| TSK-04-02 SQL 편집기 화면 | `src/Oranda.Platform/Storage/WorkspaceStore.cs` | `tests/Oranda.Platform.Tests/Storage/WorkspaceStoreTests.cs` | `IWorkspaceStore` (+ App 의 `IEditorContext`) |
| TSK-04-03 L2 ANTLR 분석 | `src/Oranda.Language/LanguageService.Analysis.cs`, `src/Oranda.Language/Parsing/**` | `tests/Oranda.Language.Tests/Parsing/PlSqlAnalyzerTests.cs` | `ILanguageService.AnalyzeAsync` (워밍업은 `IStartupTask`) |
| TSK-04-04 메타데이터 제공자·캐시 | `src/Oranda.Oracle/Metadata/OracleMetadataProvider.cs`, `src/Oranda.Platform/Storage/OrandaDatabase.cs`, `src/Oranda.Platform/Storage/SqliteMetadataCache.cs`, `src/Oranda.Platform/Migrations/OrandaDb/**`(새로 만듦) | `tests/Oranda.Oracle.IntegrationTests/Metadata/MetadataProviderTests.cs`, `tests/Oranda.Platform.Tests/Storage/MetadataCacheTests.cs` | `IMetadataProvider`(ListSchemas·ListObjects·GetTable·GetProcedures), `IMetadataCache` |
| TSK-04-05 문맥 자동완성 | `src/Oranda.Language/LanguageService.Completion.cs`, `src/Oranda.Language/Completion/**` | `tests/Oranda.Language.Tests/Completion/CompletionTests.cs` | `ILanguageService.GetCompletionContext`, `ICompletionProvider` |
| TSK-04-06 정의로 이동·참조 찾기 | `src/Oranda.Language/Navigation/**` | `tests/Oranda.Language.Tests/Navigation/SymbolLocatorTests.cs` | `ISymbolLocator` |
| TSK-04-07 코드 조각·키맵 프리셋 | `src/Oranda.Language/Snippets/**` | `tests/Oranda.Language.Tests/Snippets/SnippetLibraryTests.cs` | — (키맵은 UI 계약) |
| TSK-05-01 포매터 엔진 기본 | `src/Oranda.Language/Formatting/SqlFormatter.cs` (+ 같은 폴더 새 파일, `LayoutRules.cs` 제외) | `tests/Oranda.Language.Tests/Formatting/FormatterBasicTests.cs` | `ISqlFormatter` |
| TSK-05-02 포매터 확장 옵션 | `src/Oranda.Language/Formatting/LayoutRules.cs` | `tests/Oranda.Language.Tests/Formatting/FormatterLayoutTests.cs` | (`ISqlFormatter` 규칙 추가) |
| TSK-05-03 포매터 프로필 화면 | `src/Oranda.Platform/Storage/FormatterProfileStore.cs` | `tests/Oranda.Platform.Tests/Storage/FormatterProfileStoreTests.cs` | `IFormatterProfileStore` |
| TSK-06-01 실행 엔진·정책 관문 | `src/Oranda.Oracle/Execution/**`(빈 틀 `OracleExecutionService.cs`), `src/Oranda.Core/Execution/ExecutionPolicy.cs` | `tests/Oranda.Oracle.IntegrationTests/Execution/ExecutionServiceTests.cs`, `tests/Oranda.Core.Tests/Execution/ExecutionPolicyTests.cs` | `IExecutionService`, `IExecutionPolicy`, `IResultSet` |
| TSK-06-02 SQL 실행 화면 | (UI 전용) | (UI 계약) | App 의 `IExecutionInteraction` |
| TSK-06-03 가상 결과 그리드 | `src/Oranda.Core/Grid/ResultPageCache.cs`, `src/Oranda.Core/Grid/SelectionCopyFormatter.cs` | `tests/Oranda.Core.Tests/Grid/ResultPageCacheTests.cs`, `tests/Oranda.Core.Tests/Grid/SelectionCopyFormatterTests.cs` | — (`IResultSet` 소비) |
| TSK-06-04 그리드 편집 | `src/Oranda.Language/Editing/**` | `tests/Oranda.Language.Tests/Editing/RowEditorTests.cs` | `IRowEditor` |
| TSK-06-05 LOB 편집기·행 보기 | `src/Oranda.Oracle/Grid/**` | `tests/Oranda.Oracle.IntegrationTests/Grid/LobAccessorTests.cs` | `ILobAccessor` |
| TSK-06-06 실행 이력·SQL*Plus | `src/Oranda.Platform/Storage/SqliteHistoryStore.cs`, `src/Oranda.Platform/History/**`, `src/Oranda.Language/Redaction/**`, `src/Oranda.Oracle/SqlPlus/**` | `tests/Oranda.Platform.Tests/Storage/HistoryStoreTests.cs`, `tests/Oranda.Language.Tests/Redaction/SecretRedactorTests.cs`, `tests/Oranda.Oracle.IntegrationTests/SqlPlus/SqlPlusCommandTests.cs` | `IHistoryStore`, `ISecretRedactor`, `ISqlPlusCommandProcessor`, `IStartupTask`(기록기) |
| TSK-06-07 내보내기·가져오기 | `src/Oranda.Oracle/Transfer/**` | `tests/Oranda.Oracle.IntegrationTests/Transfer/DataTransferTests.cs` | `IDataTransferService` |
| TSK-07-01 실행 계획 | `src/Oranda.Oracle/Plans/**` | `tests/Oranda.Oracle.IntegrationTests/Plans/ExecutionPlanTests.cs` | `IExecutionPlanService` |
| TSK-07-02 스키마 브라우저 | (UI 전용) | (UI 계약) | — (`IMetadataProvider` 소비) |
| TSK-07-03 객체 상세·DDL | `src/Oranda.Oracle/Metadata/OracleMetadataProvider.Details.cs` | `tests/Oranda.Oracle.IntegrationTests/Metadata/ObjectDetailTests.cs` | `IMetadataProvider.GetDdlAsync`·`GetObjectDetailAsync` |
| TSK-07-04 객체 관리·DB 정보·DDL 일괄 | `src/Oranda.Oracle/Schema/**` | `tests/Oranda.Oracle.IntegrationTests/Schema/DatabaseInfoTests.cs` | `IDatabaseInfoProvider` |
| TSK-08-01 PL/SQL 소스 편집·컴파일 | `src/Oranda.Oracle/Metadata/OracleMetadataProvider.Source.cs` | `tests/Oranda.Oracle.IntegrationTests/Metadata/PlSqlSourceTests.cs` | `IMetadataProvider.GetSourceAsync`·`GetCompileErrorsAsync` |
| TSK-08-02 PL/SQL 실행 대화상자 | `src/Oranda.Language/PlSql/**` | `tests/Oranda.Language.Tests/PlSql/CallBlockBuilderTests.cs` | — (`IExecutionService`·`GetProceduresAsync` 소비) |
| TSK-09-01 세션 모니터 | `src/Oranda.Oracle/Monitoring/OracleMonitorService.cs` | `tests/Oranda.Oracle.IntegrationTests/Monitoring/SessionMonitorTests.cs` | `IMonitorService`(세션·블로킹·상세·Kill·트레이스) |
| TSK-09-02 SQL 모니터 | `src/Oranda.Oracle/Monitoring/OracleMonitorService.TopSql.cs` | `tests/Oranda.Oracle.IntegrationTests/Monitoring/TopSqlTests.cs` | `IMonitorService.GetTopSqlAsync`·`GetSqlDetailAsync` |
| TSK-10-01 설치·업데이트·충돌 보고 | `src/Oranda.Platform/Diagnostics/**` | `tests/Oranda.Platform.Tests/Diagnostics/CrashReportWriterTests.cs` | (+ App 의 `IOutboundConsent`) |
| TSK-10-02 다국어·접근성 | (UI 전용) | (UI 계약) | — |
| TSK-10-03 AI 확장 지점 시험 패널 | (UI 전용) | (UI 계약) | — (`IEditorContext`·`IMetadataProvider`·`IExecutionService` 소비) |

UI 도구 창 → docs/design/ui-contract.md, 카탈로그 → docs/design/catalog.md

### 공유 파일 (계약 Task 소유, 기능 Task 는 고치지 않음)

| 파일 | 바꿔야 하면 |
|---|---|
| `src/Oranda.Core/**` 의 인터페이스·모델(위 표에 없는 파일) | 계약 변경 요청 → 이 문서와 `Contracts/*Tests` 를 함께 고친다 |
| `src/Oranda.Core/Serialization/OrandaJsonContext.cs` | 새 루트 형식은 data-model.md 변경과 함께 |
| `src/Oranda.*/…Assembly.cs`(표지 파일), `tests/Oranda.Testing/*`(기존 파일) | 새 가짜는 새 파일로 |
| 각 csproj 의 `PackageReference` 추가 | 그 프로젝트 소유 Task 가 한 줄 추가 + `Directory.Packages.props` 한 줄 추가(기존 줄 수정 금지) |

---

## 6. 결정

| 결정 | 이유 |
|---|---|
| 이벤트 버스 기본 구현을 Core 에 둔다(`EventBus`, TSK-03-01 이 채움) | TRD 3.2. 모든 계층이 같은 버스를 쓰고, Core 는 외부 의존이 없다 |
| 실행 요청에 `ExecutionOptions` 매개변수를 더한다 | 출처·라이선스 없이는 정책 관문(SAFE-07)과 이력 `source` 를 채울 수 없다 |
| 카탈로그 라이선스 요구는 `LicensePack`(프로필 보유 선언과 같은 순서 열거형)으로 넘긴다. `CatalogLicense` → `LicensePack` 바꾸기는 실행하는 쪽(TSK-06-01·09-xx)이 한다 | 비교가 `required <= profile.LicensePack` 한 줄이 된다 |
| 한 인터페이스를 여러 Task 가 구현하면 partial 클래스 파일로 나눈다(`LanguageService`, `OracleMetadataProvider`, `OracleMonitorService`) | 「한 파일 = 한 Task」 |
| 빈 틀 구현 클래스는 특성을 단 채 `NotImplementedException` | 기능 Task 가 등록 코드를 건드리지 않고 몸통만 채운다 |
| `tests/Oranda.Platform.Tests` 추가 | Platform 구현 Task 9개의 시험 자리 |
| 시각 JSON 은 `UtcDateTimeOffsetConverter` 로 `…Z` 형식 | data-model 1.3. 기본 직렬화는 `+00:00` 을 쓴다 |
