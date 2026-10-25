# 사용자 Role 별 기능 조사

> 작성 2026-10-25 · 기준 커밋 `a507d81` (dev-jji-win144) · 읽기 전용 조사(코드 변경 없음)
> 목적: 설정에서 Role(개발자·튜너·DBA 등)을 고르면 그 Role 의 메뉴·기능만 보이고 나머지는 로드하지 않게 하려는 작업의 사전 조사.
> 표기: 근거는 `파일:줄`(경로는 `src/Oranda.App/` 기준. 다른 프로젝트는 `src/...` 전체 경로로 적음). 「추측」은 코드로 확인하지 못한 판단.

---

## 0. 먼저 알아 둘 구조 (요약)

| 항목 | 지금 동작 | 근거 |
|---|---|---|
| 명령 등록 | `[OrandaCommand]` 특성을 어셈블리 스캔으로 모음. 처리기는 **처음 실행할 때** 생성(지연) | `Shell/ShellContributions.cs:26-58`, `Shell/CommandRegistry.cs:11,54` |
| 메뉴·툴바·우클릭·팔레트·시작 화면 | 모두 `ICommandRegistry.Commands` 하나에서 만든다 | `Views/MainWindow.axaml.cs:209,242,266`, `Shell/Palette/CommandPaletteViewModel.cs:93`, `Shell/StartPage.cs:47` |
| 단축키 | 키맵 → 같은 레지스트리 `ExecuteAsync`. 레지스트리에 없는 명령은 아무 일도 안 함 | `Shell/Keymaps/KeyDispatcher.cs:11,129`, `Shell/CommandRegistry.cs:49` |
| 도구 창·문서 패널 | `[OrandaToolPanel]` 스캔. ViewModel 은 **처음 화면에 보일 때** 생성(지연). 보기 메뉴도 패널 목록에서 만든다 | `Shell/Docking/PanelDockables.cs:9,31`, `Views/MainWindow.axaml.cs:242` |
| 상태 표시줄 칸 | `[OrandaStatusBarItem]` 10개를 **주 창 붙일 때 모두 즉시 생성** | `ViewModels/MainViewModel.cs:27` |
| 시작 작업 | `IStartupTask` 14개. 첫 프레임 뒤(AfterFirstFrame) / 2초 유휴 뒤(Idle) | `App.axaml.cs:132-163` |
| 지연 어셈블리 | Oranda.Oracle·Oranda.Language 는 시작 경로에서 빼지만 **2초 유휴 뒤 무조건 스캔(로드)** | `Shell/Composition/AppComposition.cs:39,54-57`, `App.axaml.cs:136-142` |
| 기능 끄기 선례 | 개발 빌드 전용 패널(`DevBuildOnly`)을 레지스트리 생성 때 거름 + `devBuild` 문맥 키 | `Shell/ToolPanelRegistry.cs:20`, `Shell/Composition/AppComposition.cs:68`, `Shell/ContextKeyService.cs:29-33` |
| 주 메뉴 최상위 | file, edit, view, sql, result, database, monitor, tuning, tools, window, help | `Shell/Menus/MenuModel.cs:141-142` |
| 어셈블리 | **기능 전부가 Oranda.App 한 어셈블리**(Features 37개 폴더). Oracle 쿼리·모니터링·튜닝 서비스는 Oranda.Oracle 한 어셈블리 | `src/Oranda.App/Oranda.App.csproj`, `src/Oranda.Oracle/` |

명령 281개(특성 기준), 도구 창·문서 패널 33개, 상태 칸 10개, 시작 작업 14개.

---

## 1. 기능 목록

열 설명 — **시작 생성**: 즉시 = 앱 시작(DI·셸 초기화·시작 작업)에서 만들어짐 / 지연 = 열거나 실행할 때 생성 / 기본 배치 = 기본 레이아웃에 들어가 있어 그 탭이 활성으로 보이면 생성.
**무게**: 코드 규모(파일·줄, .cs+.axaml), 타이머·폴링·감시·외부 라이브러리.
**DB 권한**: 쿼리에 쓰는 사전 뷰·패키지. 카탈로그 쿼리는 `DBA_*` 우선, 없으면 `ALL_*` 로 대체한다(`src/Oranda.Oracle/Catalog/meta/arguments.sql:4` 의 `requires: dba_arguments, all_arguments` 형식).

### 1.1 공통 기반(셸·접속·편집·실행)

| # | 기능 | 진입점(메뉴 · 명령 id · 단축키) | 구현 위치(규모) | 시작 생성 | 무게 | DB 권한 |
|---|---|---|---|---|---|---|
| C1 | 셸(메뉴·툴바·명령 팔레트·도킹·레이아웃·테마·확대) | 보기/창 메뉴 · `oranda.view.commandPalette`(Ctrl+Shift+P) 외 `Shell/ShellCommands.cs` 18개, 이름 붙인 레이아웃 3개 | `Shell/*` | 즉시 | Avalonia·Dock.Avalonia(공통 바닥) | – |
| C2 | 설정 창 | 도구 > 설정 · `oranda.tools.settings`(Ctrl+,) | `Shell/Settings/SettingsWindow.axaml`, `SettingsViewModel.cs` | 지연 | 작음 | – |
| C3 | 화면 언어 | 도구 > 언어 · `oranda.tools.language` | `Features/Localization` (448줄) | 즉시(UiText) | 작음 | – |
| C4 | 업데이트 | 도움말 > 업데이트 확인 · `oranda.help.checkUpdate`, 상태 칸 8 | `Features/Update` (1.5k줄, Velopack) | 즉시(시작 작업 `UpdateStartup`, 상태 칸) | 시작 때 네트워크 확인 1회 | – |
| C5 | 접속 관리자·세션 전환·끊기·재접속·비밀번호 변경 | 파일 > 접속… · `oranda.conn.manage`(Ctrl+Shift+O), 툴바 | `Features/ConnectionManager` (18파일 5.8k줄) | 상태 칸 1 즉시, 창은 지연 | 접속 유지 타이머(`src/Oranda.Oracle/Sessions/OracleSession.cs:44`) | 접속 점검 `V$SESSION`·`V$VERSION`·`V$PARAMETER`(`ServerProbe`) — 실패해도 기능 축소로 처리 |
| C6 | 접속 가져오기·내보내기 | 파일 > 접속 가져오기/내보내기 · `oranda.conn.import`/`export` | `Features/ConnectionImport` (610줄) | 지연 | 작음 | – |
| C7 | SQL 편집기·SQL 작업 공간(파일·탭·찾기·주석·대소문자·북마크·접기·다중 커서·개요) | 파일·편집 메뉴 · `oranda.file.*`, `oranda.edit.*` 53개, `oranda.view.outline` | `Features/SqlEditor` (36파일 7.2k줄) | 즉시(시작 작업 `SqlEditorDockWiring`, 정적 `SqlEditorHost.Current`, 작업 공간 복원·자동 저장 `SqlEditorHost.Autosave.cs:159`) | AvaloniaEdit, 자동 저장 PeriodicTimer | – |
| C8 | 자동완성·인자 도움말 | 편집 > 자동완성 · `oranda.edit.triggerCompletion`/`parameterHints` | `Features/Completion` (528줄) + `src/Oranda.Language` | Language 는 유휴 2초 뒤 로드, ANTLR 파서 예열(`src/Oranda.Language/Parsing/PlSqlWarmUpTask.cs`) | ANTLR(첫 파싱 약 2초 분을 유휴에 선지불) | 메타데이터 `DBA_/ALL_` |
| C9 | 편집기 진단(SQL 점검 밑줄) | 자동 | `Features/Diagnostics` (556줄) | 즉시(시작 작업 `EditorDiagnosticsStartup`) | 입력 디바운스 타이머(`EditorDiagnostics.cs:97`) | – |
| C10 | 탐색(정의로 이동·객체 설명·참조 찾기·빠른 객체 열기·스키마에서 보기·테이블 레이아웃 팝업) | 탐색 메뉴 · `oranda.nav.*`, `oranda.schema.reveal`(Alt+F1), Ctrl+클릭 | `Features/Navigation` (2.7k줄) | 즉시(시작 작업 `NavigationWiring`) | 작음 | `DBA_/ALL_OBJECTS` 등 |
| C11 | SQL 실행·트랜잭션·DBMS 출력·오류 모드·결과/메시지/출력 창 | SQL 메뉴 · `oranda.exec.runStatement`(Ctrl+Enter), `runScript`(F5), `txn.commit`/`rollback` 등 23개 | `Features/Execution` (28파일 5.2k줄) | 상태 칸 2·3·4·6 즉시, 결과·메시지·출력 창은 기본 배치(아래) | 실행 중에만 250ms 경과 타이머(`ExecutionState.cs:633`) | `DBMS_OUTPUT` |
| C12 | 결과 그리드(복사·찾기·필터·열 고정·관련 행 이동 등) | 결과 메뉴·그리드 우클릭 · `oranda.grid.*` 25개 | `Features/ResultGrid` (37파일 7.3k줄) | 시작 작업 `ResultGridSettingsStartup`(설정만) | 큰 컨트롤(자체 그리기) | FK 따라가기는 `*_CONSTRAINTS` |
| C13 | 바인드 변수 창·바인드 값 붙여 넣기 | SQL > 바인드 변수 · `oranda.exec.bindVariables` | `Features/Binds`, `Features/Execution/BindVariablesViewModel.cs` | 오른쪽 기본 열림(`Shell/Layout/LayoutPlanner.cs:30`) | 작음 | – |
| C14 | 포매터·포맷 프로필 | 편집 > 문서 포맷 · `oranda.format.document`(Ctrl+Shift+F), 도구 > 포맷 프로필 | `Features/FormatterProfiles` (2.5k줄) + Language | 지연 | 중간 | – |
| C15 | 스키마 브라우저(필터·즐겨찾기·이름/SELECT 넣기) | 보기(Alt+1) · `oranda.schema.*` 8개 | `Features/SchemaBrowser` (16파일 3.8k줄) | 왼쪽 기본 배치 | 중간 | `DBA_/ALL_*` |
| C16 | 객체 상세·DDL 보기·데이터 탭 | 스키마 우클릭 · `oranda.schema.openDetail`/`showDdl` | `Features/ObjectDetail` (1.4k줄) | 지연 | 중간 | `DBMS_METADATA`, `DBA_/ALL_*` |
| C17 | 실행 이력·스크립트 폴더·통계 토글·@파일 실행 | `oranda.view.history`(F8), `oranda.view.scripts`(Ctrl+R), `oranda.exec.toggleStatistics`, `oranda.sqlplus.runFile` | `Features/HistoryScripts` (1.2k줄), `src/Oranda.Platform/History/HistoryRecorder.cs` | 이력 기록기 즉시(SQLite), 창은 기본 배치(아래·왼쪽) | SQLite | 통계 토글은 `V$MYSTAT`·`V$STATNAME` |
| C18 | 최근 파일·작업 공간 | 파일 > 최근 · `oranda.file.clearRecent*` | `Features/RecentFiles` (355줄) | 즉시(시작 작업) | 작음 | – |
| C19 | 템플릿·스니펫·키맵 편집·키맵 프리셋·설정 내보내기/가져오기 | 도구 메뉴 · `oranda.view.templates`, `oranda.tools.keymap*`, `exportSettings` | `Features/Templates` (2.4k줄) | 템플릿 창 오른쪽 기본 열림(`LayoutPlanner.cs:30`) | 작음 | – |

### 1.2 개발 보조

| # | 기능 | 진입점 | 구현 위치(규모) | 시작 생성 | 무게 | DB 권한 |
|---|---|---|---|---|---|---|
| D1 | 그리드 편집(행 추가·복제·삭제·DML 미리 보기·적용) | SQL > 편집용 실행 · `oranda.exec.runForEdit`, 그리드 우클릭 `oranda.grid.edit.*` 13개 | `Features/GridEdit` (1.3k줄) | 상태 칸 9 즉시 | 작음 | 대상 표 DML 권한 |
| D2 | LOB 편집기·행 보기 | 그리드 우클릭 · `oranda.grid.rowView`/`viewValue`, `oranda.view.rowView` | `Features/LobRowView` (1.6k줄) | 지연 | 작음 | – |
| D3 | 내보내기(CSV 등·Excel)·가져오기 | 결과 > Excel 로, 도구 > 가져오기 · `oranda.grid.export`/`exportExcel`, `oranda.tools.import` | `Features/DataTransfer` (1.4k줄), `src/Oranda.Oracle/Transfer` (2.7k줄) | 지연 | xlsx 를 직접 씀(외부 라이브러리 없음) | 가져오기는 INSERT 권한 |
| D4 | 전용 세션 SQL 탭 | 파일 > 새 전용 세션 SQL · `oranda.file.newDedicatedSqlEditor` | `Features/DedicatedSession` (558줄) | 지연 | 탭마다 DB 세션 1개 더 | – |
| D5 | 플래시백 조회(AS OF·이력·복원) | 그리드 우클릭 · `oranda.grid.flashback.*` | `Features/Flashback` (839줄) | 지연 | 작음 | `FLASHBACK` 권한, `V$PARAMETER`(undo) |
| D6 | 변경 일지(DDL·데이터 변경 전 스냅숏 기록) | 도구 > 변경 일지 · `oranda.view.changeJournal`, 스키마 우클릭 | `Features/ChangeJournal` (706줄), `src/Oranda.Platform/Journal/ChangeJournalRecorder.cs` | 기록기 즉시(시작 작업, SQLite), 창은 아래 기본 배치(Order 45) | SQLite | – |
| D7 | 테이블 정의서(xlsx·Markdown·HTML) | 도구 > 테이블 정의서 · `oranda.tools.tableSpec`, 스키마 우클릭 | `Features/TableSpec` (534줄) | 지연 | 작음 | `DBA_/ALL_TAB_COLS` 등 |
| D8 | MyBatis 로그 붙여 넣기(일반 SQL 탭에) | 편집 > MyBatis 로그 붙여 넣기 · `oranda.edit.pasteMyBatisLog` | `Features/MyBatisLog` (226줄) | 지연 | 작음 | – |

### 1.3 PL/SQL

| # | 기능 | 진입점 | 구현 위치(규모) | 시작 생성 | 무게 | DB 권한 |
|---|---|---|---|---|---|---|
| P1 | PL/SQL 편집기·컴파일·스펙/바디 전환·서버 차이·컴파일 오류 창 | 파일 > 새 PL/SQL · `oranda.file.newPlsqlEditor`, `oranda.plsql.compile`(F5), 툴바, 스키마 우클릭 `oranda.plsql.openSource` | `Features/PlsqlEditor` (2.1k줄) | 컴파일 오류 창은 아래 기본 배치(Order 50) | 중간 | `DBA_/ALL_SOURCE`·`*_ERRORS`, `V$ACCESS`(사용 중 확인) |
| P2 | PL/SQL 작업 공간(저장·복원) | 파일 > PL/SQL 작업 공간 저장 · `oranda.plsql.saveWorkspaceFile` | `Features/PlsqlWorkspace` (2.5k줄) | **즉시**(시작 작업 `PlsqlWorkspacePersistence`: 라우터 생성 `:70`, 최대 10개 복원, 자동 저장 타이머 `:79`) | 타이머 상시 | – |
| P3 | PL/SQL 실행 대화상자 | `oranda.plsql.run`(PL/SQL 편집기 Ctrl+Enter), 스키마 우클릭 `runFromBrowser` | `Features/PlsqlRun` (822줄) | 지연 | 작음 | `*_ARGUMENTS`, EXECUTE |

### 1.4 MyBatis

| # | 기능 | 진입점 | 구현 위치(규모) | 시작 생성 | 무게 | DB 권한 |
|---|---|---|---|---|---|---|
| M1 | MyBatis 작업 공간(매퍼 편집·시나리오·펼친 SQL·실행·조합 검사·자동 고침·저장·변환·매퍼 폴더·전체 포맷) | 파일 > 매퍼 폴더 열기/MyBatis 로 바꾸기/저장, SQL > 시나리오 실행(F9), 툴바 `toolbar:mybatis/*` · `oranda.mybatis.*` 약 20개 | `Features/MyBatisWorkspace` (**121파일 21.6k줄, 가장 큼**), `src/Oranda.Language/MyBatis`, `src/Oranda.Core/MyBatis`, `src/Oranda.Platform/MyBatis` | **즉시**: 시작 작업 4개(`Hooks/MyBatisWorkspaceWiring.cs:23`, `Conversion/MyBatisConvertReady.cs:11`, `Folder/MyBatisFolderReadyTask.cs:9`, `Save/MapperWatchStartup.cs:10`), 서비스 `MapperWatchService`·`MapperFolderService`·`MyBatisEvalHub`, 상태 칸 10(`Save/MapperSaveStatusItem.cs:23`). 문장·조합 창은 기본 배치(MyBatis 문서일 때만 보임) | 매퍼 열 때 FileSystemWatcher(`Save/MapperFileWatcher.cs:65`, `Folder/MapperFolderService.cs:239`), 디바운스 타이머 여럿 | 실행·계획만(공통 실행 경로) |

### 1.5 튜닝

| # | 기능 | 진입점 | 구현 위치(규모) | 시작 생성 | 무게 | DB 권한 |
|---|---|---|---|---|---|---|
| T1 | 실행 계획(Explain·실제 계획·비교·A-Rows 띠·계획 그래프·SQL Monitor 리포트 탭·힌트/통계 수준 재실행) | SQL/튜닝 메뉴 · `oranda.plan.explain`(Ctrl+E), `oranda.plan.actual`(Ctrl+Shift+E), `oranda.view.plan`(Alt+4), 툴바 | `Features/ExecutionPlan` (14파일 4.0k줄) | 계획 창은 아래 기본 배치(Order 30) | 그래프 뷰 | `DBMS_XPLAN`, `V$SQL_PLAN`, `GV$SQL_PLAN_STATISTICS_ALL`, `V$SQL_MONITOR`·`DBMS_SQLTUNE`(Tuning Pack) |
| T2 | SQL 모니터(Top SQL·검색·바인드·ASH 대기·AWR 계획 이력·계획 관리·Tuning Advisor/베이스라인 스크립트) | 튜닝 > SQL 모니터 · `oranda.monitor.sql`, `oranda.tuning.ashWaits`/`awrHistory`/`planControls` 등 9개, 툴바 | `Features/SqlMonitor` (3.3k줄) | 지연 | 목록·차트 | `GV$SQL`, `GV$SQL_BIND_CAPTURE`, `GV$ACTIVE_SESSION_HISTORY`·`DBA_HIST_*`(Diagnostics Pack), `DBA_SQL_PROFILES`/`PLAN_BASELINES`/`PATCHES`, `DBMS_SPM`, `DBMS_SQLTUNE` |
| T3 | 튜닝 도우미·벤치마크·인덱스/재작성 후보 | 튜닝 > 도우미 · `oranda.tuning.assistant`/`benchmark` 등 | `Features/Tuning/Assistant` | 지연 | 중간 | 계획 권한 |
| T4 | 자식 커서(공유 안 된 이유) | 튜닝 > 자식 커서 · `oranda.tuning.childCursors` | `Features/Tuning/Cursors` | 지연 | 작음 | `GV$SQL_SHARED_CURSOR` |
| T5 | 표 통계·통계 수집 스크립트 | 튜닝 > 통계 · `oranda.tuning.stats`, `schema.tableStats` | `Features/Tuning/Stats` | 지연 | 작음 | `ALL_TAB_STATISTICS`, `DBMS_STATS`(스크립트) |
| T6 | 트레이스 뷰어(TKPROF 식 요약)·10053 옵티마이저 트레이스 | 튜닝 > 트레이스 · `oranda.tuning.traceViewer`/`optimizerTrace` | `Features/Tuning/Trace` | 지연 | 트레이스 파일 읽기 | `V$DIAG_INFO`, `V$DIAG_TRACE_FILE_CONTENTS`, `GV$PROCESS`, ALTER SESSION |
| T7 | 튜닝 HTML 리포트(SELECT 전용 수집기) | 튜닝 > 리포트 · `oranda.tuning.report` | `Features/Tuning/Report` | 지연 | HTML·SVG 생성 | T1·T2 의 뷰 묶음 |

`Features/Tuning` 전체 34파일 10.0k줄.

### 1.6 운영·관리(DBA)

| # | 기능 | 진입점 | 구현 위치(규모) | 시작 생성 | 무게 | DB 권한 |
|---|---|---|---|---|---|---|
| A1 | 세션 모니터(블로킹 트리·상세·Kill·끊기·세션 트레이스) | 모니터 > 세션 · `oranda.monitor.sessions` 외 10개, 툴바 | `Features/SessionMonitor` (1.8k줄) | 지연 | 자동 새로 고침 루프(`SessionMonitorViewModel.cs:618`) | `GV$SESSION`·`GV$SESSTAT`·`GV$SESS_IO`·`GV$SESSION_LONGOPS`·`GV$OPEN_CURSOR` 등, ALTER SYSTEM KILL, `DBMS_MONITOR`. 권한 없으면 안내 띠 + `GRANT SELECT_CATALOG_ROLE`(`SessionMonitor/MonitorAccess.cs:71-72`) |
| A2 | Lock 모니터(이력 띠·임계치 알림·유휴 트랜잭션·데드락 기록·순환 대기·잠긴 행·DDL 락·CSV) | 모니터 > Lock · `oranda.monitor.locks` 외 11개 | `Features/LockMonitor` (9파일 5.1k줄) | 지연 | 열면 자동 새로 고침 기본 켬(`LockMonitorViewModel.cs:377`), 1초 카운트다운(`:1887`), 이력 차트, 토스트 | `GV$LOCK`·`GV$TRANSACTION`·`GV$SESSION`·`DBA_DDL_LOCKS`·`V$DIAG_ALERT_EXT`, ALTER SYSTEM KILL |
| A3 | 블로커 알림(상태 칸 「N 세션 막음」·「미커밋 N분」) | 상태 칸 7(누르면 Lock 모니터) | `Features/BlockerAlert` (897줄) | **즉시**(상태 칸 생성 때 `BlockerWatcher` 생성·이벤트 구독, `BlockerStatusItem.cs:36-46`) | 미커밋이 있을 때만 15초 폴링(`BlockerWatcher.cs:65,272`) + **모니터 세션 1개 더 엶** | `V$SESSION` 계열 |
| A4 | DB 도구: DB 정보(파라미터 보기·변경)·DDL 추출·객체 검색·무효 객체 컴파일·휴지통 비우기/복원 | 데이터베이스 메뉴 · `oranda.db.info`/`ddlExtract`, `oranda.schema.objectSearch`/`compileInvalid`/`recyclebin*`, 툴바 | `Features/DbTools` (8파일 2.0k줄) | 지연 | 중간 | `V$INSTANCE`·`V$DATABASE`·`V$PARAMETER`·`V$OPTION`, `DBA_OBJECTS`·`DBA_SOURCE`·`DBA_SEGMENTS`, `DBMS_METADATA`, ALTER SYSTEM(파라미터 변경, `src/Oranda.Oracle/Schema/SchemaStatements.cs`) |
| A5 | 열 순서 바꾸기(재정의 스크립트) | 스키마 우클릭 · `oranda.schema.reorderColumns` | `Features/ColumnReorder` (882줄) | 지연 | 작음 | `DBMS_REDEFINITION` |

### 1.7 기타

| # | 기능 | 진입점 | 구현 위치 | 시작 생성 | 비고 |
|---|---|---|---|---|---|
| X1 | 확장 시험 창 | `oranda.view.extensionTest` | `Features/ExtensionTest` | 개발 빌드에서만 등록(`DevBuildOnly`) | 이미 꺼지는 선례 |
| X2 | 도움말·정보 | `oranda.help.about` | `Features/Help`, `Shell/ShellCommands.cs` | 지연 | – |

### 1.8 시작할 때 실제로 만들어지는 것(Role 과 관계없이 지금 모두)

- 상태 칸 10개: 접속·세션·트랜잭션·마지막 실행·편집기·보호·**블로커**·업데이트·저장 알림·**MyBatis** (`ViewModels/MainViewModel.cs:27`).
- 시작 작업 14개: App 11(`EditorDiagnosticsStartup`, `MyBatisConvertReady`, `MyBatisFolderReadyTask`, `MyBatisWorkspaceWiring`, `MapperWatchStartup`, `NavigationWiring`, `PlsqlWorkspacePersistence`, `RecentFilesStartup`, `ResultGridSettingsStartup`, `SqlEditorDockWiring`, `UpdateStartup`) + Platform 2(`HistoryRecorder`, `ChangeJournalRecorder`) + Language 1(`PlSqlWarmUpTask`).
- 기본 레이아웃 도구 창(`Shell/Layout/LayoutPlanner.cs:33-73`): 왼쪽 스키마 브라우저·스크립트·MyBatis 문장 / 아래 결과·MyBatis 조합·메시지·출력·계획·이력·변경 일지·컴파일 오류 / 오른쪽 바인드·템플릿. 묶음마다 **활성 탭의 ViewModel 만** 생성되고, SQL·MyBatis 전용 창은 문서 종류에 따라 숨김(`Shell/Docking/DockWorkspace.ScopedTools.cs:33-71`).

---

## 2. Role 후보와 근거

PRD 의 페르소나(`docs/PRD.md:53-58`: P1 애플리케이션 개발자, P2 운영 DBA, P3 데이터 분석·운영 담당(조회만), P4 Orange 사용자)와 oranda1 의 MyBatis 사용자(`docs/oranda1/PRD.md:28`), 그리고 실제 메뉴 묶음(database·monitor·tuning 최상위 메뉴)을 맞춰 아래 6개를 제안한다. 한 사람이 여러 Role 을 겸하는 경우가 많으므로(예: PL/SQL + 튜너) **Role = 기능 묶음 프리셋, 여러 개 선택 가능**을 권한다(열린 질문 6.1).

| Role | 하는 일 | 근거 |
|---|---|---|
| **공통(항상 켬)** | 접속, SQL 편집기·작업 공간, 실행·결과 그리드, 스키마 브라우저, 객체 상세, 탐색·자동완성·포매터, 이력·스크립트, 바인드, 설정·키맵·언어·업데이트, 기본 Explain | 모든 페르소나가 쓰고, 다른 기능이 이것 위에 얹혀 있다(실행 경로·편집기 호스트). 1.1 표 |
| **R1 SQL 개발자** | 쿼리 작성·실행, 결과 편집, 내보내기, 템플릿 | PRD P1. 그리드 편집·LOB·전용 세션·플래시백·변경 일지·테이블 정의서 |
| **R2 PL/SQL 개발자** | 패키지·프로시저 편집·컴파일·실행 | PRD P1 의 「컴파일 오류 위치 이동」. PL/SQL 3개 기능 + DDL 추출·무효 객체 컴파일 |
| **R3 MyBatis(백엔드) 개발자** | 매퍼 편집·시나리오 시험·저장 | oranda1 PRD 3장. 가장 큰 기능(21.6k줄)이고 시작 작업 4개 + 상태 칸을 가진다 |
| **R4 DB 튜너** | 느린 SQL 찾기·계획 분석·통계·트레이스·리포트 | tuning 최상위 메뉴 전체(`menu:tuning/*`), 라이선스 팩(Diagnostics·Tuning) 보유 선언과 연결(`src/Oranda.Core/Connection/ConnectionProfile.cs:26,65`) |
| **R5 운영 DBA** | 세션·Lock 장애 대응, Kill, 파라미터, 휴지통, 재정의 | PRD P2. monitor·database 최상위 메뉴 |
| **R6 조회 전용(분석·운영 담당)** | 운영 DB 조회, Excel/CSV 내보내기 | PRD P3. 편집·DML·PL/SQL·모니터·튜닝을 숨기면 메뉴가 가장 단순 |

---

## 3. 기능 × Role 매트릭스

◎ 필수 · ○ 선택(기본 켬 권장) · △ 선택(기본 끔 권장) · – 불필요. 공통(C1~C19)은 모든 Role 에서 ◎ 이므로 생략.

| 기능 | R1 SQL | R2 PL/SQL | R3 MyBatis | R4 튜너 | R5 DBA | R6 조회 |
|---|---|---|---|---|---|---|
| D1 그리드 편집 | ◎ | ○ | ○ | – | ○ | – |
| D2 LOB·행 보기 | ◎ | ○ | ○ | ○ | ○ | ○ |
| D3 내보내기·가져오기 | ◎ | ○ | ○ | ○ | ○ | ◎(가져오기는 –) |
| D4 전용 세션 탭 | ○ | ○ | △ | ○ | ○ | – |
| D5 플래시백 | ○ | ○ | △ | – | ○ | △ |
| D6 변경 일지 | ○ | ○ | ○ | – | ○ | – |
| D7 테이블 정의서 | ○ | △ | △ | – | ○ | ○ |
| D8 MyBatis 로그 붙여 넣기 | △ | – | ◎ | – | – | – |
| P1 PL/SQL 편집기 | △ | ◎ | – | ○ | ○ | – |
| P2 PL/SQL 작업 공간 | – | ◎ | – | – | △ | – |
| P3 PL/SQL 실행 | △ | ◎ | – | – | ○ | – |
| M1 MyBatis 작업 공간 | – | – | ◎ | – | – | – |
| T1 실행 계획(기본 Explain 은 공통) 고급 탭 | ○ | ○ | ○ | ◎ | ○ | – |
| T2 SQL 모니터 | – | – | – | ◎ | ○ | – |
| T3 튜닝 도우미 | △ | △ | △ | ◎ | – | – |
| T4 자식 커서 | – | – | – | ◎ | ○ | – |
| T5 표 통계 | △ | – | – | ◎ | ○ | – |
| T6 트레이스 | – | △ | – | ◎ | ○ | – |
| T7 튜닝 리포트 | – | – | – | ◎ | △ | – |
| A1 세션 모니터 | – | – | – | ○ | ◎ | – |
| A2 Lock 모니터 | – | – | – | ○ | ◎ | – |
| A3 블로커 알림 | ○ | ○ | ○ | ○ | ◎ | – |
| A4 DB 도구 | △(객체 검색·DDL 추출) | ○ | – | ○(DB 정보) | ◎ | – |
| A5 열 순서 바꾸기 | – | – | – | – | ◎ | – |

메모: A3 블로커 알림은 「내 세션이 남을 막고 있음」을 알리는 **개발자 안전장치**라 개발자 Role 에도 ○ 로 둔다(`Features/BlockerAlert/BlockerWatcher.cs:56-61`). 대신 미커밋이 있을 때 모니터 세션을 하나 더 연다는 점은 열린 질문(6.5).

---

## 4. 끄기 난이도·얽힘

### 4.1 세 단계 분류

**가: 메뉴(레지스트리)에서만 빼면 됨** — 처리기·ViewModel 이 지연 생성이라 숨기면 만들어지지도 않는다. 단, 같은 어셈블리라 코드(메타데이터)는 그대로 올라와 있다(4.3).

| 기능 | 뺄 곳 | 주의 |
|---|---|---|
| T2 SQL 모니터, T3~T7 튜닝, A1 세션 모니터, A2 Lock 모니터, A4 DB 도구, A5 열 순서, D3 내보내기, D4 전용 세션, D5 플래시백, D7 테이블 정의서, D8 MyBatis 로그, P3 PL/SQL 실행, C6 접속 가져오기, C14 포맷 프로필 창, D2 LOB | 명령 + 문서 패널 | 다른 기능이 `ShellCommandRunner.RunAsync(id)` 나 `DockWorkspace.OpenDocument(panelId)` 로 부르는 곳은 명령·패널이 없으면 조용히 아무 일도 안 한다(`Shell/ShellCommandRunner.cs:15`, `Shell/Docking/DockWorkspace.cs:355`). 버튼은 남고 눌러도 무반응이 되므로 그 버튼도 숨겨야 한다(4.2) |
| C19 템플릿 창, D6 변경 일지 창, C17 이력·스크립트 창, P1 컴파일 오류 창, T1 계획 창 | 도구 창(기본 배치) | 도구 창을 레지스트리에서 빼면 저장된 레이아웃에서 자동으로 빠진다(`Shell/Layout/LayoutPlanner.cs:81-90`). 다시 켜면 기본 위치로 돌아오고 사용자가 옮긴 자리는 잃는다 |

**나: DI·시작 초기화에서도 빼야 메모리·자원 이득**

| 기능 | 시작 때 하는 일 | 뺄 곳 |
|---|---|---|
| M1 MyBatis | 시작 작업 4개, 서비스 생성, 상태 칸 10 생성, 호스트에 훅 꽂기 | `Features/MyBatisWorkspace/Hooks/MyBatisWorkspaceWiring.cs:23,40,60-65`, `Conversion/MyBatisConvertReady.cs:11`, `Folder/MyBatisFolderReadyTask.cs:9`, `Save/MapperWatchStartup.cs:10`, `Save/MapperSaveStatusItem.cs:23`, 도구 창 2개 |
| P2 PL/SQL 작업 공간 | 라우터 생성, 최대 10개 복원(PL/SQL 편집기 문서 생성), 자동 저장 타이머 상시 | `Features/PlsqlWorkspace/PlsqlWorkspacePersistence.cs:27,70,72-80` |
| A3 블로커 알림 | 상태 칸 생성 때 감시기 생성·이벤트 구독, 미커밋 시 15초 폴링 + 모니터 세션 | `Features/BlockerAlert/BlockerStatusItem.cs:28,36-46`, `BlockerWatcher.cs:65,121,272` |
| D6 변경 일지 | 기록기가 이벤트 구독·SQLite 기록·보존 정리 | `src/Oranda.Platform/Journal/ChangeJournalRecorder.cs:12-30` |
| C17 실행 이력 | 기록기(공통이라 끄지 않는 것을 권장) | `src/Oranda.Platform/History/HistoryRecorder.cs:20-43` |

시작 작업·상태 칸은 설정을 읽은 **뒤**에 돌기 때문에 거르기 쉽다: 시작 작업은 `App.axaml.cs:146-163`, 상태 칸은 `ViewModels/MainViewModel.cs:27`(창 붙이기는 `shell.Initialize` 뒤, `App.axaml.cs:88-90`). Platform·Language 어셈블리의 시작 작업도 같은 목록으로 들어온다.

**다: 다른 기능과 얽혀 분리 어려움**

| 기능 | 얽힘(파일:줄) | 끌 때 해야 할 일 |
|---|---|---|
| T1 실행 계획 | Lock 모니터(`Features/LockMonitor/LockMonitorCommands.cs:6`, `LockMonitorItems.cs:6`, `LockMonitorViewModel.cs:14`), SQL 모니터(`SqlMonitor/AwrHistoryViewModel.cs:9`, `SqlMonitorView.axaml.cs:7`), 튜닝(`Tuning/Cursors/ChildCursorsCommands.cs:9`, `ChildCursorsViewModel.cs:11`, `Tuning/Report/PlanSvg.cs:5`, `TuningReportCommands.cs:9`, `TuningReportHtml.cs:6`), MyBatis(`MyBatisWorkspace/Run/MyBatisRunCommands.cs:4`)가 계획 트리·뷰를 가져다 쓴다 | 계획 모델·뷰는 공통 라이브러리로 두고 「계획 창·고급 탭(비교·재실행)」만 Role 로 숨긴다. 기본 Explain 은 공통 |
| T1 → 튜닝 | 계획 창이 표 통계·자식 커서를 연다(`Features/ExecutionPlan/PlanViewModel.Boost.cs:133-137`) | 튜닝을 끄면 이 버튼을 숨겨야 함(안 숨기면 눌러도 무반응) |
| A1 세션 모니터 | 권한 안내 띠·표 묶개·모니터 접속 고르기(`MonitorAccessState`, `MonitorTableBinder`, `MonitorProfileDialog`)를 Lock 모니터(`LockMonitorViewModel.cs:15,346`), SQL 모니터(`SqlMonitorViewModel.cs:12`, `SqlMonitorView.axaml.cs:8,29,37`, `PlanControlsViewModel.cs:8,110-114`), 튜닝(`Tuning/Cursors/ChildCursorsViewModel.cs:12`, `Tuning/Stats/TableStatsViewModel.cs:9`), 블로커(`BlockerStatusItem.cs:12`, `BlockerWatcher.cs:6`)가 함께 쓴다. 접속 직후 권한 키 설정도 여기 있다(`ConnectionManager/ConnectionManagerCommands.cs:69`) | 같은 어셈블리 안이라 메뉴 숨김에는 문제 없음. 어셈블리를 나눌 때는 이 공용 부분을 「모니터 공통」으로 먼저 떼야 함 |
| A2 Lock 모니터 ↔ A3 블로커 | 블로커가 Lock 모니터 임계치 이벤트를 구독(`BlockerWatcher.cs:92,121,168`)하고 칸을 누르면 Lock 모니터를 연다(`BlockerStatusItem.cs:122`) | Lock 모니터를 끄면 칸 클릭 동작을 바꾸거나 막아야 함. 이벤트 구독은 그냥 둬도 무해(발행자가 없음) |
| T2 SQL 모니터 ↔ 튜닝·Lock | `SqlMonitor/SqlMonitorViewModel.Tuning.cs`, `SqlWaitsViewModel.cs` 가 Lock 모니터·튜닝 기능을 부르고, 튜닝이 SQL 모니터를 부른다(`Tuning/Cursors/ChildCursorsCommands.cs:11`, `Tuning/Report/TuningReportCommands.cs:12`) | T2 와 T3~T7 은 한 묶음(「튜닝」)으로 켜고 끄는 편이 간단 |
| P1 PL/SQL 편집기 | 실행 컨트롤러가 컴파일 오류 형식을 씀(`Features/Execution/ExecutionController*.cs:5`, 7개 파일), 스키마 브라우저가 소스를 PL/SQL 편집기로 연다(`SchemaBrowser/SchemaObjectOpener.cs:8-9`), 객체 탐색(`Navigation/ObjectNavigationCommands.cs:3`), 객체 검색(`DbTools/ObjectSearchViewModel.cs:11`), 변경 일지(`ChangeJournal/ChangeJournalViewModel.cs:11`), SQL 편집기 명령(`SqlEditor/SqlEditorCommandHandler.cs:2`), 메뉴 정렬(`Shell/Menus/MenuControls.cs`), 시작 화면(`Shell/StartPage.cs`) | PL/SQL 을 끄면 「소스 열기」를 객체 상세(DDL 보기)로 돌리는 분기가 필요. 컴파일 오류 형식은 공통에 남김 |
| M1 MyBatis ↔ SQL 편집기 | SQL 편집기 호스트에 훅 자리(`Features/SqlEditor/SqlEditorHost.cs:56`, 기본값 `MyBatisHooks.Empty`), `SqlWorkspaceViewModel`의 문서 종류 MyBatis, `WorkspaceFiles.MyBatis.cs`, 실행 경로(`Execution/ExecutionController.Body.cs:65`), 도구 창 범위(`Shell/Docking/DockWorkspace.ScopedTools.cs:53-57`), 시작 화면 문구(`Shell/StartPage.cs:210`). SQL 편집기 파일 중 MyBatis 를 언급하는 곳 141줄 | **훅 구조라 끊을 자리는 이미 있다**: `MyBatisWorkspaceWiring` 을 돌리지 않으면 훅이 `Empty` 로 남는다. 이때 .xml 을 열면 예전 방식(매퍼를 일반 SQL 탭으로 펼침, `WorkspaceFiles.MyBatis.cs:16-21`)으로 열릴 것으로 보인다(추측: 훅 없음 경로를 실제로 시험하지 않음) |

### 4.2 메뉴 외에 함께 숨겨야 할 화면 요소

- 툴바: `toolbar:main/sessions@510`(세션 모니터), `toolbar:main/sqlMonitor@520`, `toolbar:main/dbInfo@530`, `toolbar:main/newPlsql@15`, `toolbar:main/explain@230`, `toolbar:mybatis/*` — 레지스트리에서 거르면 같이 사라진다(`Views/MainWindow.axaml.cs:266`).
- 우클릭 메뉴: `context:schema/plsql@50`, `context:schema/tableStats@72`, `context:schema/recyclebin@60`, `context:editor/tuningAssistant@1030`, `context:grid/flashback@*` 등 — 같은 레지스트리(`Views/MainWindow.axaml.cs:209`).
- 화면 안 버튼(레지스트리를 거치지 않음): 계획 창의 표 통계·자식 커서(`PlanViewModel.Boost.cs:134,137`), 블로커 칸 클릭(`BlockerStatusItem.cs:122`), 시작 화면 문구(`Shell/StartPage.cs:210`). 문서 탭 우클릭의 MyBatis 변환은 레지스트리의 `context:documentTab` 자리라 같이 걸러진다(`Shell/Docking/DockTabMenu.cs:14`). 위 버튼들은 「기능 켜짐」 문맥 키(예: `feature.tuning`)를 보고 숨기는 방식이 맞다.
- 주의: 명령의 `When` 식으로 숨기면 **주 메뉴만** 숨고 툴바·우클릭·팔레트는 숨지 않는다(`Shell/Menus/MenuVisibility.cs:20`). 그래서 Role 거르기는 `When` 이 아니라 레지스트리 단계에서 하는 편이 맞다.

### 4.3 메모리 이득에 대한 판단

- 모든 기능이 Oranda.App 한 어셈블리에 있고, 시작 때 `ShellContributions.Scan` 이 어셈블리 전체에 `GetTypes()` 를 부른다(`Shell/ShellContributions.cs:33`). 형식 메타데이터는 Role 과 상관없이 올라온다. 하지만 메서드는 처음 부를 때 JIT 되고 처리기·ViewModel·View 는 지연 생성이므로, **지금도 안 연 기능은 메모리를 많이 쓰지 않는다**(추측: 기능별 메모리를 잰 자료는 없음).
- 측정치: macOS 접속 0·탭 0 유휴 메모리 중앙값 131.6 MB(`docs/perf/macos-2026-10-20.md:26`). 대부분은 Avalonia·Dock·AvaloniaEdit 와, 2초 뒤 로드되는 ODP.NET(Oranda.Oracle)·ANTLR(Oranda.Language)로 보이며 이들은 모든 Role 이 쓴다(추측).
- 그래서 Role 거르기의 실제 이득은 (1) 메뉴·툴바 단순화, (2) 시작 작업·상태 칸·타이머·파일 감시·추가 DB 세션을 줄이는 것(4.1 「나」)이다. 메모리를 수십 MB 단위로 줄이려면 **기능을 별도 어셈블리로 떼고(예: Oranda.App.MyBatis, Oranda.App.Tuning, Oranda.App.Dba) 켜진 Role 의 어셈블리만 로드**해야 한다. 이를 위한 장치는 이미 있다: 지연 어셈블리 등록(`Shell/Composition/AppComposition.cs:39,54-57`), 여러 어셈블리를 받는 스캔(`Shell/ShellContributions.cs:26`). 먼저 `tests/Oranda.Perf`(`docs/perf/README.md:19`)로 「MyBatis·튜닝·DBA 를 끈 빌드」와 지금 빌드를 견줘 이득을 재고 나서 어셈블리 분리를 정할 것을 권한다.

---

## 5. 현재 설정 체계와 Role 설정 자리

### 5.1 현재 체계

| 항목 | 위치 |
|---|---|
| 설정 파일 | `{데이터 폴더}/settings.json` (`src/Oranda.Platform/Storage/AppDataPaths.cs:34`). 데이터 폴더는 `ORANDA_DATA_DIR` 또는 OS 기본(`AppDataPaths.cs:18,57`) |
| 설정 모델 | `AppSettings` 레코드(`src/Oranda.Core/Settings/AppSettings.cs:13-46`): Ui·Editor·KeymapId·FormatterProfileId·Execution·Grid·History·MetadataCache·Monitor·Workspace·SchemaBrowser·Update·TableLayout·MyBatis |
| 읽기·쓰기 | `SettingsService`(`Shell/Settings/SettingsService.cs:14,38-63`) → `SettingsStore`(`src/Oranda.Platform/Storage/SettingsStore.cs`: 손상 백업·상향 변환·미래 버전 읽기 전용). 저장하면 섹션별 `SettingsChanged` 발행 |
| 설정 창 | `SettingsWindow.axaml` 탭: 일반(:75)·편집기(:118)·포맷(:146)·실행(:209)·그리드(:231)·이력(:255)·모니터(:275)·업데이트(:285). 모델 `SettingsViewModel`(`Shell/Settings/SettingsViewModel.cs:39,165`) |
| 읽는 시점 | `AppComposition.Create`(`App.axaml.cs:56`) → `ShellController` 생성(`:64`, 이때 명령 레지스트리·도킹·도구 창 레지스트리가 생성됨, `Shell/ShellController.cs:72-78`) → 설정·레이아웃 읽기(`:65`, `ShellController.cs:176-181`) → `Initialize`(`:88`) → 창 붙이기·상태 칸(`:90`) → 시작 작업(`:69`, 첫 프레임 뒤) |

### 5.2 Role 설정을 넣을 자리 후보

1. **설정 모델**: `AppSettings` 에 새 섹션 `Features`(예: `Roles: ["sqlDev","tuner"]`, `Enabled: [...]`, `Disabled: [...]`)를 더한다. 섹션 이름이 곧 `SettingsChanged.Section` 이라 구독도 쉽다(`AppSettings.cs:12`). 키가 없는 옛 파일은 기본값(전부 켬)으로 읽히게 한다(같은 처리 선례 `AppSettings.cs:233`).
2. **설정 창**: 「일반」 탭(`SettingsWindow.axaml:75-116`)에 Role 선택(확인란 여러 개)과 「기능 세부」 펼침을 둔다. 탭을 따로 만들 만큼 크면 「기능」 탭 추가.
3. **첫 실행**: 시작 화면(`Shell/StartPage.cs`)에서 Role 을 한 번 묻는 안(선택).
4. **기능 꼬리표 붙이기**: 특성(`OrandaCommand`·`OrandaToolPanel`·`OrandaStatusBarItem`)에 `Feature` 속성을 더하는 안과, **네임스페이스(`Oranda.App.Features.<폴더>`)로 기능 묶음을 정하는 안**이 있다. 뒤쪽은 281개 특성을 고치지 않아도 되고 시작 작업·서비스에도 똑같이 쓸 수 있다.
5. **거를 곳(코드 자리)**:
   - 명령: `CommandRegistry` 생성 때(`Shell/Composition/AppComposition.cs:67`) — 메뉴·툴바·우클릭·팔레트·단축키·시작 화면이 한꺼번에 따라온다.
   - 도구 창: `ToolPanelRegistry` 생성 때(`AppComposition.cs:68`, 거르기 선례 `Shell/ToolPanelRegistry.cs:20`).
   - 상태 칸: `MainViewModel.cs:27`.
   - 시작 작업: `App.axaml.cs:148-153`.
   - 화면 안 버튼: 문맥 키 `feature.<묶음>` 을 `ContextKeyService` 에 넣는다(`devBuild` 선례 `Shell/ContextKeyService.cs:29-33`).
   - 시점 문제: 레지스트리 두 개는 설정을 읽기 **전**에 만들어진다(`App.axaml.cs:64` vs `:65`). 따라서 (a) `AppCompositionOptions` 에 켜진 기능 목록을 넣고 조립 전에 settings.json 의 이 섹션만 동기로 읽거나, (b) 레지스트리에 「거르기 조건」을 넣고 설정을 읽은 뒤 다시 계산해 `CommandsChanged`/`PanelsChanged` 를 낸다(주 창은 이미 이 사건으로 메뉴를 다시 짓는다, `Views/MainWindow.axaml.cs:168`). (b) 이면 설정 창에서 Role 을 바꿀 때 메뉴는 재시작 없이 바뀐다. 시작 작업·상태 칸·어셈블리 로드는 재시작 후 반영.

---

## 6. 열린 질문(사용자가 정할 것)

1. **Role 선택 방식**: 하나만 고르나, 여러 개를 겹쳐 고르나(권장: 여러 개). Role 위에 「기능별 켜기/끄기」 세부 조정도 허용하나?
2. **Role 목록 확정**: 2장의 6개(SQL 개발자·PL/SQL 개발자·MyBatis 개발자·튜너·DBA·조회 전용)로 갈지, 개발자 셋을 「개발자」 하나로 묶을지.
3. **기본값**: 처음 설치·업데이트 직후 Role 이 없으면 전부 켬(지금과 같음)인가, 첫 실행에서 묻는가?
4. **목표가 메뉴 단순화인가 메모리인가**: 메모리가 목표면 어셈블리 분리(4.3)가 필요하고 일이 크다. 먼저 끈 빌드로 측정한 뒤 정할지.
5. **블로커 알림(A3)**: 개발자 Role 에서도 켤지. 켜면 미커밋 때 모니터 세션을 하나 더 연다.
6. **PL/SQL 을 끈 Role 에서 스키마 브라우저 「소스 열기」**: 객체 상세(DDL 보기)로 돌릴지, 메뉴를 숨길지.
7. **MyBatis 를 끈 Role 에서 .xml 열기**: 예전처럼 일반 SQL 탭으로 펼칠지(4.1 「다」), 막을지.
8. **실행 계획(T1) 범위**: 기본 Explain·실제 계획은 공통으로 두고 비교·힌트 재실행·SQL Monitor 리포트 탭만 튜너용으로 둘지.
9. **조회 전용 Role(R6)**: 메뉴만 숨길지, 접속 프로필의 읽기 전용(`readOnlySession`)처럼 DML 실행 자체를 막을지. 후자는 Role 이 아니라 보안 정책 성격이라 따로 정하는 편이 맞다.
10. **Role 을 바꾼 뒤 재시작**: 메뉴만 즉시 반영하고 시작 작업·상태 칸은 「재시작하면 적용」 안내로 충분한가?
11. **꺼진 기능의 레이아웃**: 다시 켤 때 기본 위치로 돌아와도 되는가(사용자가 옮긴 자리는 잃음, `LayoutPlanner.cs:81-90`)?
12. **라이선스 팩 연동**: 튜너 Role 을 켜도 프로필에 Diagnostics/Tuning Pack 보유 선언이 없으면 ASH·AWR·SQL Monitor 를 지금처럼 막는다. Role 과 팩 선언을 묶을지(예: 튜너 선택 시 팩 선언 안내).
