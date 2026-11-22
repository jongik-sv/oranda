# agent-cli-1122 2단계 레인 분해 초안

- 작성: 판단 올리기 서브에이전트(opus/high), 2026-11-22. 기준 코드 = dev-jji-win144 411fe66d(읽기만)
- 입력: `phase2/request.md`(D1~D5), `phase2/mac-locations.md`(맥 조사, 다시 조사하지 않고 빠진 곳만 확인), 설계서 §5·§6·§8·§9·§10A, 계약 문서, decompose.md §2~§4
- 조정자 지시 반영: D5 확정(운영도 자동 실행, 로컬 모델이면 운영도 가리지 않음). 「맥에 일을 많이」: 맥 = F2·F3·F4·F5(레인 3개), win144 = C0 + F1 + DB IT 대행, linux = 실제 CLI 왕복
- **운영 허용은 한 곳에 모은다**: `AgentGrantPolicy` 의 스위치 둘(§2.2). 꺼도 다른 레인 코드는 바뀌지 않는다

## 1. 붙일 자리 (파일:줄)

맥 조사(mac-locations.md)에 있는 줄은 그대로 쓰고, 빠진 곳만 더 확인했다.

### F1 실행·내보내기

| 무엇 | 자리 | 메모 |
|---|---|---|
| 내보내기 서비스 | `src/Oranda.Core/Transfer/IDataTransferService.cs:14` `ExportAsync(ISession, SqlStatement, binds, ExportOptions, Stream, IProgress, ct)`, `:22` `ExportRowsAsync`(이미 가져온 행) | 「전체 행」은 `ExportAsync`(서버에서 다시 읽음). xlsx 쓰기 = `Core/Transfer/TransferWriters.cs` |
| 내보낼 문장 묶음 | `src/Oranda.App/Features/DataTransfer/DataTransferCommands.cs:18` `ExportSource(Session, Query, Binds)`(+`Columns`·`ColumnOrder`·`BindProblem`), `:61` `ActiveExportSource`(활성 결과만), `:80` `ShowResultExportAsync`(대화상자) | **탭 id·결과 번호로 ExportSource 를 만드는 함수는 없음** → F1 이 만든다 |
| 편집기 실행 경로 | `src/Oranda.App/Features/Execution/ExecutionController.Run.cs:24` `RunAsync(mode, newResultTab, tab)`, `:135` `PrepareSingleStatementAsync(tab)`(접속 확인·세션 바쁨·문장 하나 고르기) | 계획 「실행하고 실제 계획」이 이미 이 길을 쓴다(`ExecutionPlanCommands.cs:234`) |
| 준비된 문장 실행 | `ExecutionController.Body.cs:73` `RunPreparedAsync(tab, stmt, binds)` — 결과를 그 탭 그리드에 보인다. **`:99` 출처가 `HistorySource.Editor` 로 고정** | F1 이 선택 인자 `HistorySource source = Editor` 를 더한다(MyBatis 호출 `MyBatisRunner.cs:77` 은 그대로) |
| 명령 id | `ExecutionCommands.cs:34~56`(`oranda.exec.*`·`oranda.txn.*`) | F5 등급표 입력 |
| 정책 규칙 0 | `src/Oranda.Core/Execution/IExecutionPolicy.cs:41` 에이전트 출처 = Query 만 | 출처 Agent 로 실행하면 이중 방어가 공짜로 붙는다 |
| 문장 검사(재사용 대상) | `src/Oranda.Language/Ai/Handlers/External/QueryToolHandler.cs:49`(운영 거부), `:64` 한 문장 Query, `:190` `CheckStatementAsync`(잠금 절·DB 링크·금지 호출·스키마 풀림), `:53~61` 게이트가 준 사용자 함수 판정(`ReadOnlyExecution`·`Substitutions`) | App 도구(탭 실행)에는 게이트가 SQL 인자를 못 본다 → 검사를 함수로 뽑아 공유(§2 `IExternalSelectGuard`) |
| 결과 그리드 읽기 | `src/Oranda.App/Features/Ai/External/Results/ExternalResultHandlers.cs:65`(`ResultTabsViewModel` 고르기), `:72` 민감 열, `:188~192` 운영 오류 글 가림 | |

### F2 튜닝 자동

| 무엇 | 자리 |
|---|---|
| 앱 안 AI 튜닝 | `src/Oranda.App/Features/Ai/Tuning/AiTuningLauncher.cs:41` 클래스, `:66` `For(services)`, `:76` `RunAsync(ct)` = **활성 편집기 커서 문장** 기준, `:101` `RunCommandAsync`, `:609` `BuildStartInfo` |
| 메뉴 명령 | `src/Oranda.App/Features/Ai/AiCommands.cs:51·55` (`oranda.ai.tune`·`tuneStatement`) |
| CLI 튜닝 명령 | `src/Oranda.Agent.Cli/CommandTable.cs:70~82`(`tune context…awr-plan`, 기존 도구 10개) |
| 스킬 본문 | `src/Oranda.Platform/Ai/Skills/OrandaSkillDocument.cs:29` `Render`, 명령 줄 `OrandaSkillCommands.cs:18` `All`(CommandTable 과 손으로 맞춤) |

- `tune start` = 탭을 앞으로(`focus_tab` 경로) → `AiTuningLauncher.For(sp).RunAsync`. 탭 지정 시작 API 는 **없음**(활성 탭 전용).

### F3 매뉴얼

| 무엇 | 자리 |
|---|---|
| 문서 | `site/src/content/docs/**` 36개(start·install·connect·editor·run·plan·schema·transfer·ai·tools·settings·help·manual), 머리말 `title`·`description` |
| 주소 | `site/astro.config.mjs:9-10` base `https://jongik-sv.github.io/oranda` |
| 색인 | **없음**. 앱은 실행 중 `site/` 를 갖지 않는다 → 코드 안 색인 표 + 시험이 `site/` 와 맞는지 확인 |
| orandactl | Core 만 참조(`AgentCliReferenceTests`) → 색인은 **Core** 에 둬야 CLI·스킬이 같이 쓴다 |

### F4 가림(로컬 모델) + D5 운영 갈래

| 무엇 | 자리 |
|---|---|
| 허가 요청·보기 | `src/Oranda.Core/Ai/Gate/AgentGrant.cs:18` `AgentGrantView`, `:137` `AgentGrantRequest(Targets, Lifetime, AllowUnboundTabText, Label)` — **로컬 모델 칸 없음** |
| 운영 판정이 흩어진 곳 | `AgentGrant.cs:102` `CanReadTabText` 운영 거부, `:191` `DefaultCap` 운영=Metadata, `AgentGrantStore.cs:312~319` 하위 세션(운영이면 리터럴 사전·가림 필수, `:316` `AiContext` 에 LocalAi 없음), `AgentGate.External.cs:207·357` `DevTestOnly && Production`, `QueryToolHandler.cs:49`, `HistoryToolHandler.cs:104`, `ExternalResultHandlers.cs:188`, `ExternalEditorModels.cs:57`, 연결 창 `ExternalAgentConnectViewModel.cs:44·63·74·194·214`·`ExternalAgentConnectWindow.cs:85` |
| 가림이 흩어진 곳 | 게이트 `AgentGate.cs:240·287·377`(세션 `Context.LocalAi` 를 이미 읽음), 처리자 스스로 가림 `QueryToolHandler.cs:129`, `ExternalResultHandlers.cs:72`, `ExternalEditorService.cs:246`(바인드 값) |
| LocalAi 규칙 | `AiContext.cs:33`(`src/Oranda.Core/Ai/Contracts/`), `AgentSession.cs:39·127`, `AiAccessPolicy.cs:65` `DataLevel` |
| 내장 AI 공용 | `AgentGate.cs:112·119·272` `DevTestOnly` 는 **내장 실행도 지난다 → 건드리지 않는다**(D5 는 외부 허가만) |

### F5 UI 제어

| 무엇 | 자리 |
|---|---|
| 명령 정의 | `src/Oranda.Core/Shell/Commands.cs:24` `CommandDefinition(Id, Title=리소스 키, Category, Enablement, Placements, Aliases)`. 메뉴 경로 = `Placements` 의 `menu:<경로>@<순서>`(`:46`, 해석 `App/Shell/Menus/MenuModel.cs:10·171`) |
| 레지스트리 | `Commands.cs:77` `ICommandRegistry.Commands/Find/CanExecute/ExecuteAsync(id, keys, parameter)`, 구현 `src/Oranda.App/Shell/CommandRegistry.cs:19` |
| 안전 등급 | **없음**(정의에 칸 없음). 명령 문자열 id 는 App 전체에 200개 남짓(맥 집계 등록 79 + 동적 등록) |
| 문맥 키 | `ExecuteAsync` 에 `IContextKeys` 가 필요 → 셸의 지금 키를 UI 스레드에서 얻는 길 확인 필요(§6) |

## 2. C0 공유 계약 (win144, 단독 머지, 1시간 안)

다른 레인이 기다리는 것만 넣는다. 동작은 **지금과 같게**(스위치 꺼짐) 두고, 켜는 일은 m-gate-prod 가 한다.

### 2.1 새 도구 5개 (`AppTools.cs` 에 덧붙임, `External` 35 → 40)

| 도구 | Host | 등급 | 최소 수준 | 필수 인자 | 선택 인자 | 출력 칸 | 처리 레인 |
|---|---|---|---|---|---|---|---|
| `run_tab` | App | X | —(처리자가 탭 접속 수준 ≥ Query 확인) | `tab` | `base_version`, `range`(document\|statement, 기본 document = 탭이 한 문장일 때만), `timeout_s`(30/60) | `status`(succeeded\|canceled\|failed\|stale), `result_index`, `statement_kind`, `fetched`, `more`, `elapsed_ms`, `sql_id?`, `error?{code,message}` | w-run-export |
| `export_result` | App | N | — (행 값 수준 QueryRows 필요 없음: 행은 LLM 에 안 감) | `tab`, `format`(xlsx\|csv), `out`(절대 경로) | `index`, `sheet?` | `path`, `rows`(숫자), `bytes`, `elapsed_ms`, `status`(written\|canceled) | w-run-export |
| `tune_start` | App | N | Plan | `tab` | `range`(statement) | `started`, `chat`(창 이름) | m-skill-docs |
| `list_commands` | App | R | — | — | `category`, `grade`(now\|confirm\|deny) | `commands[{id, title, category, menu_path?, grade, enabled}]` | m-ui-cmd |
| `run_command` | App | U(정의 값) — 실제 판단은 등급표 | — | `id` | `tab`(먼저 앞으로) | `status`(done\|confirmed\|rejected\|timeout\|disabled), `grade` | m-ui-cmd |

- `docs` 는 도구가 아니라 **CLI 자체 명령**(앱 없이 됨, §5 E-27). 색인 타입만 C0 에 빈 껍데기로: `src/Oranda.Core/Ai/Manual/OrandaManual.cs`(`Entry(Title, Description, Url, Section)` · `All` 빈 목록 · `Search(q)`).
- `AppFields` 더함: `format`·`out`·`sheet`·`id`·`grade`·`category`. `AppToolValues` 더함: `RunStatus`·`ExportStatus`·`ExportFormats`·`CommandGrades`·`CommandStatus`.

### 2.2 허가·운영 스위치 (`AgentGrant.cs`)

| 더할 것 | 값(C0) | 뜻 |
|---|---|---|
| `AgentGrantRequest.LocalModel` / `AgentGrantView.LocalModel` (bool, 기본 false) | false | 「로컬 모델 — 가리지 않음」(D4). 진짜 로컬인지는 확인 못 함 |
| `AgentGrantPolicy.ProductionRuns` (const bool) | **false**(C0) → m-gate-prod 가 true | D5: 운영에서 `query`·`run_tab`·`export_result`·계획 도구 |
| `AgentGrantPolicy.LocalModelCoversProduction` (const bool) | **false** → m-gate-prod 가 true | D5: 로컬 모델이면 운영도 가리지 않음 |
| `EnvironmentCap(env)` | 운영 = ProductionRuns ? QueryRows : Metadata | 기존 `DefaultCap` 이 부름 |
| `MayExecute(AgentGrantConnection)` | 수준 ≥ Query && (개발·시험 \|\| ProductionRuns) | `query`·`run_tab`·`export_result` 가 부름 |
| `UsesLocalModel(grant, env)` | grant.LocalModel && (개발·시험 \|\| LocalModelCoversProduction) | 하위 세션 `AiContext.LocalAi` 값 |
| `MasksSensitive(grant, env)` = !UsesLocalModel | | 처리자 민감 열·비밀 바인드 |
| `MasksLiterals(grant, env)` = 운영 && !UsesLocalModel | | 이력·오류 글·하위 세션 리터럴 |
| `CanReadProductionTabText(grant)` = UsesLocalModel(운영) | | E-18 유지: 클라우드 모델은 운영 탭 글 거부 |

- C0 가 **호출 자리만 바꿔 끼움**(동작 같음, 기존 시험 그대로 통과): `AgentGrant.cs:102`, `QueryToolHandler.cs:49·129`, `HistoryToolHandler.cs:104`, `ExternalResultHandlers.cs:72·188`, `ExternalEditorModels.cs:57`, `ExternalEditorService.cs:246`, `AgentGrantStore.cs` Create 에서 `LocalModel` 옮기기 한 줄.
- **C0 는 `AgentGate*.cs` 를 건드리지 않는다**(it-fix 가 고치는 중). `AgentGate.External.cs:207·357` 와 `AgentGrantStore.cs:312~319` 교체는 m-gate-prod.

### 2.3 새 오류 코드 (`AgentErrorCodes.cs`·`AgentCliExit.For`)

| 코드 | 종료 | 뜻 |
|---|---|---|
| `file_exists` | 4 | `export_result` 의 `out` 이 이미 있다(덮지 않음) |
| `path_invalid` | 2 | 상대 경로·폴더 없음·확장자가 형식과 다름 |
| `command_not_found` | 1 | `run_command` 모르는 id |
| `command_denied` | 4 | 등급표 「거부」·표에 없는 명령 |
| `command_disabled` | 4 | `Enablement` 거짓(지금 문맥에서 못 씀) |
| `tab_busy` | 1 | 그 탭 세션이 실행 중 |

### 2.4 CommandTable·스킬 명령 줄

| CLI | 도구 |
|---|---|
| `tab run --tab ID [--base-version N] [--range document\|statement] [--timeout 30]` | `run_tab` |
| `result export --tab ID --format xlsx\|csv --out PATH [--index N]` | `export_result` |
| `tune start --tab ID` | `tune_start` |
| `ui commands [--category C] [--grade now\|confirm\|deny]` | `list_commands` |
| `ui run <id> [--tab ID]` | `run_command` |
| `docs [검색어...]` | (로컬, 도구 없음) — C0 는 줄만, 본체 m-skill-docs |

- `OrandaSkillCommands.cs:18` 에 같은 줄 6개(설명 한 줄). 스킬 본문의 절차 글은 m-skill-docs.
- CLI 시험(`Oranda.Agent.Cli.Tests`)의 「모든 외부 도구가 명령으로 닿음」 시험이 40개로 맞게 C0 에서 함께.

## 3. 레인 표

| 레인 | 기기 | 소유(글롭) | 금지 | 할 일 (id 제목 크기) | 선행 | 시험 범위 |
|---|---|---|---|---|---|---|
| **w-contract2** | win144 | `src/Oranda.Core/Ai/Schemas/AppTools.cs`, `src/Oranda.Core/Ai/Protocol/AgentErrorCodes.cs`, `src/Oranda.Core/Ai/Gate/AgentGrant.cs`, `src/Oranda.Core/Ai/Manual/**`(새), `src/Oranda.Agent.Cli/CommandTable.cs`, `src/Oranda.Platform/Ai/Skills/OrandaSkillCommands.cs` + §2.2 호출 자리 한 줄씩 | `src/Oranda.Core/Ai/Gate/AgentGate*.cs`, 화면 | C0-1 도구 5개 정의·필드·값(S) · C0-2 허가 칸·정책 함수·호출 자리 교체(S) · C0-3 오류 코드·종료 코드(S) · C0-4 CLI·스킬 명령 줄 + 도구 40 시험(S) | 없음 | Core.Tests/Ai·Agent.Cli.Tests·Language.Tests/Ai/Handlers/External·App.Tests/Features/Ai/External 필터 1번 |
| **w-run-export** | win144 | `src/Oranda.App/Features/Ai/External/Run/**`(새), `src/Oranda.App/Features/Ai/External/Editor/**`(띠 표시), `src/Oranda.App/Features/Execution/ExecutionController.Body.cs`(출처 인자), `src/Oranda.App/Features/DataTransfer/DataTransferCommands.cs`(탭·결과로 ExportSource), `src/Oranda.Language/Ai/Handlers/External/QueryToolHandler.cs`·`ExternalSelectGuard.cs`(새), `tests/Oranda.Oracle.IntegrationTests/Ai/External*`(새 파일만) | `AgentGate*.cs`, `AgentGrant*.cs`, Connect/**, Skills/**, `AiToolsGateIntegrationTests.cs`(it-fix 소유) | R1 `IExternalSelectGuard` 구현: QueryToolHandler 검사 뽑기(+사용자 함수 판정 `IUserFunctionSafetyChecker` 직접)(M) · R2 `run_tab`: 버전 확인→`PrepareSingleStatementAsync`→가드→`RunPreparedAsync(source: Agent)`→시간 상한 취소(M) · R3 사람이 보는 표시(에이전트 띠·메시지 줄·결과 탭 「AI·」)(S) · R4 `export_result`: 결과→ExportSource→가드 다시→`ExportAsync` 새 파일·임시 이름 뒤 바꿔 넣기·진행·취소 띠(M) · R5 Oracle·Tibero IT: 탭 실행·xlsx 전체 행·거부 문장(S) · R6 맥 레인 DB IT 대행(운영 라벨 접속 실행·가림)(S) | C0 | App.Tests/Features/Ai/External·Execution·DataTransfer 필터, Language.Tests External, DB IT 는 `--filter` 로 이 파일만, 끝에 전체 1번 |
| **m-gate-prod** | 맥 A | `src/Oranda.Core/Ai/Gate/AgentGate.External.cs`, `AgentGrantStore.cs`, `AgentGrant.cs`의 스위치 두 줄(C0 뒤 소유 넘김), `src/Oranda.App/Features/Ai/External/Connect/**`, `tests/Oranda.Core.Tests/Ai/Gate/*Grant*`·`*External*` | Run/**, Editor/**, Skills/**, `AgentGate.cs`(내장 실행 공용 — 필요하면 요청) | G1 하위 세션 `LocalAi = UsesLocalModel`, 운영 리터럴 사전 = `MasksLiterals`(S) · G2 외부 게이트 운영 갈래를 `MayExecute` 로(DevTestOnly 내장 경로는 그대로)(S) · G3 스위치 둘 켬 + 운영 실행·가림 단위 시험 표(M) · G4 연결 창: 「로컬 모델(가리지 않음)」 체크+경고, 운영 행 빨간 표시·경고 문구·기본 체크 해제·이름 다시 입력 유지·「조회 허용」 운영에도(M) · G5 운영 외부 호출 감사 보강(§3.2)(S) | C0, it-fix 머지(Gate 파일) | Core.Tests/Ai/Gate·App.Tests External/Connect Headless. DB IT 는 w-run-export R6 에 맡김 |
| **m-ui-cmd** | 맥 B | `src/Oranda.App/Features/Ai/External/Commands/**`(새: 등급표·처리자), `tests/Oranda.App.Tests/Features/Ai/External/Commands/**` | `src/Oranda.Core/Shell/**`(정의에 칸 더하지 않음), 다른 External 폴더 | U1 등급표(id → now/confirm/deny, 접두어 규칙 + 예외, 모르는 id = deny)(M) · U2 `list_commands`: 제목 리소스 풀기·`menu:` 경로·enabled(S) · U3 `run_command`: 탭 앞으로→UI 스레드 `ExecuteAsync`, confirm 은 띠 한 번(S·M) · U4 등급표 빠짐 시험: 등록 명령 전부가 표에 있거나 deny 로 떨어짐(S) | C0 | App.Tests External/Commands Headless 필터, 끝에 App.Tests 1번 |
| **m-skill-docs** | 맥 C | `src/Oranda.Platform/Ai/Skills/**`(OrandaSkillCommands 는 C0 뒤), `src/Oranda.Core/Ai/Manual/**`(C0 뒤), `src/Oranda.Agent.Cli/`의 `docs` 로컬 명령 파일(새 `DocsCommand.cs`), `src/Oranda.App/Features/Ai/External/Tune/**`(새), `site/src/content/docs/ai/external-agents.md` | `CommandTable.cs`(C0 뒤 줄 더하려면 요청), Gate, Run/** | S1 매뉴얼 색인 생성기(스크립트) + `OrandaManual.All` 표 + 시험(site 와 맞음)(M) · S2 `orandactl docs` + 스킬 「모르는 기능 = docs」 규칙·장 목록(S) · S3 스킬 튜닝 절차(context→plan→stats/index/histogram→후보→bench→가장 나은 것 새 탭+보고)(S) · S4 `tune_start` 처리자(S) · S5 스킬 실행·내보내기·ui·운영 경고 글(S) · S6 매뉴얼 장 갱신(S) | C0 | Platform.Tests/Ai/Skills·Agent.Cli.Tests·App.Tests External/Tune |
| **l-e2e** | linux | `tools/agent-e2e/**` | `src/**` | E1 FakeHost 에 `run_tab`·`export_result`(가짜 그리드 행, 실제 파일 쓰기)·`list_commands`·`run_command`(가짜 등급표)·`docs` 붙임(M) · E2 시나리오 4개: nl2sql→새 탭→실행→xlsx / 튜닝 끝까지 / 기능 질문→링크 / 메뉴 명령(M) · E3 claude·opencode 실제 왕복, verdict 판정 늘림(S) · E4 맥에 codex·agy 한 번씩 부탁(S) | C0(E1 가짜), 실제 처리자 모드는 각 레인 머지 뒤 | FakeHost 빌드, run.sh 과제마다 1~2회 |

- 맥은 레인 3개(워크트리 3개). 맥 세션이 안에서 나눠 돌린다. 맥은 DB 시험 금지 → DB IT 는 R6.
- Perf 수치 항목: 이번 요구에는 **벽시계 판정 없음**. 시간 상한(30/60초)·취소는 기능 시험으로 확인한다. Perf 시험 금지를 brief 에 적는다.
- 마감 항목(조정자): Z1 설계서 §10B(2단계 바뀐 것·실측) + 계약 문서 §8(S), Z2 최종 통합 전체 시험 1번(win144)(S).

### 3.1 D1 자동 실행 안전 규칙 (`run_tab`·`query`·`export_result` 공통)

| 규칙 | 구체안 |
|---|---|
| 문장 | 한 문장, 실효 분류 Query(SELECT/WITH), 분류 불확실 거부. `FOR UPDATE`·DB 링크·금지 호출·확인 못 한 사용자 함수 = `sql_unsafe`. DML·DDL·PL/SQL·COMMIT = `sql_not_single_query`. 가드(`IExternalSelectGuard`) + 정책 규칙 0(출처 Agent) 이중 |
| 문장 출처 | `run_tab` 은 탭 글 그대로(LLM 이 보낸 글 아님). `base_version` 을 주면 다를 때 `stale`(사람이 고친 글을 실행하지 않음). 탭이 두 문장 이상이면 `range=statement`(커서 문장) 를 줘야 함 |
| 접속 | 탭 접속이 허가 안이고 `MayExecute`. 접속 없는 탭·잠긴 탭 거부. 운영 = D5 로 허용(스위치 한 곳) |
| 세션 | `run_tab`·`export_result` = 그 탭의 작업 세션(사람이 F5 한 것과 같은 그리드). 바쁘면 `tab_busy`, 기다리지 않음. `query` = 기존 모니터 세션 |
| 행 상한 | LLM 에 가는 행 = `query`·`get_result` 200(기본 50) 그대로. `run_tab` 은 그리드 첫 페이지까지만 가져오고 행을 돌려주지 않음. `export_result` = 행 한도 없음, 앱이 파일에 직접(행은 LLM 에 안 감) |
| 시간 상한 | `run_tab` 30초 기본·60초 최대 → 넘으면 실행 취소(`canceled`). `export_result` 는 시간 상한 없음, 띠 「취소」와 CLI 끊김(ct)으로 멈춤 |
| 파일 | 절대 경로, 부모 폴더 있음, 확장자 = 형식, 이미 있으면 `file_exists`. 임시 이름에 쓰고 끝나면 바꿔 넣기, 실패·취소면 임시 파일 지움 |
| 이력·감사 | 이력 출처 `HistorySource.Agent`(R2 출처 인자). 감사 한 줄: 클라이언트·탭·접속·판단·행 수·파일 경로(내보내기) |
| 사람이 보는 표시 | 그 탭 에이전트 띠 「Claude Code 가 이 문장을 실행했습니다 · 12행 · [취소]」, 메시지 창 한 줄 「[에이전트] …」, 결과 탭 제목 앞 `AI·`. 내보내기는 띠 「파일로 저장 중 … [취소]」→「저장함 · 경로 · N행 [폴더 열기]」 |
| 입력 중 사고 | 탭에 모달을 띄우지 않음. 실행 중에도 사람이 글을 고칠 수 있으나 실행 문장은 시작 때 사본 |

### 3.2 D5 를 켜며 더하는 안전장치 (확정 항목)

| 장치 | 내용 | 레인 |
|---|---|---|
| 운영 기본 체크 해제 | 지금처럼 꺼짐. 체크 때 접속 이름 다시 입력(SAFE-08) 유지 | m-gate-prod G4 |
| 운영 경고 | 운영 행 빨간 표시 + 「운영 접속 — LLM 이 SELECT 를 자동 실행합니다」. 로컬 모델과 함께면 「운영 값이 가리지 않고 나갑니다」 한 줄 더 | G4 |
| 상한 | 운영도 `query` 200행·30/60초 같음. `run_tab` 같음. **export 는 행 한도 없음(D5)**, 띠·취소로 통제 | R2·R4 |
| 가림 | 클라우드 모델 = 운영 민감 열 `***`·리터럴 가림·오류 글 가림·운영 탭 글 거부 유지. 로컬 모델 = 모두 보임 | 정책 함수(C0) → G1·G3 |
| 감사 | 운영 외부 호출은 판단과 관계없이 전부 남김: 문장(가림 뒤)·클라이언트·행 수·내보낸 경로 | G5(감사 칸 더하기가 필요하면 Core 요청) |
| 상태 띠 | 운영이 허가에 들면 상태 표시줄 띠 색을 경고색 | G4 |
| 스킬 | 「운영은 SELECT 도 자동 실행된다 — 개발 접속을 먼저」, 접근 통제 솔루션 결재 대상일 수 있음 한 줄 | S5 |
| 되돌리기 | 스위치 둘을 false 로 → 운영 = Metadata 상한·LocalAi 거짓으로 돌아감. 다른 레인 코드 변경 없음 | — |

### 3.3 F5 안전 등급 기준

| 등급 | 기준 | 예(접두어·id) |
|---|---|---|
| now (바로) | 화면만 바뀜·창(대화상자)을 열기만 하고 끝은 사람이 함·새 탭·읽기 화면 | `oranda.panel.*`, `oranda.view.*`, `oranda.window.*`, `oranda.nav.*`(back·forward·describeObject·quickOpenObject·goToDefinition), `oranda.file.newSqlEditor`·`newPlsqlEditor`·`open`(대화상자), `oranda.grid.sort*`·`filter*`·`toggle*`·`hideColumn`·`showAllColumns`·`freezeColumns`·`rowView`·`viewValue`·`find`·`fetchAll`·`countRows`, `oranda.grid.export`·`exportExcel`(대화상자만), `oranda.monitor.sessions`·`locks`·`sql`·`refresh`·`sessionDetail`, `oranda.exec.cancel`·`goToError`·`toggleDbmsOutput`·`toggleStatistics`, `oranda.help.*`, `oranda.edit.find`·`goToLine`·`fold*`·`unfold*` |
| confirm (띠 한 번) | 디스크에 씀·탭/작업 공간 닫기(고친 글 잃을 수 있음)·연결 끊기 | `oranda.file.save*`·`saveWorkspace*`·`close`·`closeAll`·`closeWorkspace`, `oranda.conn.disconnect`·`reconnect`, `oranda.monitor.locks.exportCsv` |
| deny (거부) | 글 바꿈(→ `propose_edit`)·DB 실행(→ `run_tab`)·DB 변경·커밋·세션 죽이기·클립보드·앱 종료·AI 띄우기·비밀번호·표에 없는 id | `oranda.edit.*`(위 now 빼고), `oranda.format.document`·`statement`, `oranda.exec.run*`, `oranda.txn.*`, `oranda.grid.edit.*`, `oranda.grid.flashback.restore*`, `oranda.grid.copy*`, `oranda.monitor.kill*`·`disconnect*`·`trace*`, `oranda.conn.changePassword`·`import`·`export`, `oranda.mybatis.deleteStatement`·`renameStatement`·`applyAutoFix`·`formatAll`, `oranda.ai.*`(→ `tune_start`), `oranda.file.exit`, 동적 등록 명령 |

- 운영에서도 같은 표(D5 확정 시 맥 기본안). 인자(`parameter`) 는 이번에 넘기지 않음(`--tab` 만).

## 4. 의존 그래프·머지 순서

```text
it-fix(진행 중) ─┐
C0 w-contract2 ──┼─→ R1 → R2 → R3 ; R1 → R4 → R5 → R6
                 ├─→ G1~G5 (Gate 파일은 it-fix 머지 뒤)
                 ├─→ U1 → U2 → U3 → U4
                 ├─→ S1 → S2 ; S3·S4·S5·S6 병렬
                 └─→ E1(가짜) → E2 ; {R2,R4,U3,S2,S4} 머지 → E3 → E4
G3(스위치 켬) → R6(운영 DB IT)
모든 레인 → Z1 → Z2
```

1. **C0 단독 먼저**(다른 일 묶지 않음). it-fix 가 `AgentGrantStore.cs` 를 고치면 it-fix 먼저, 아니면 C0 먼저.
2. it-fix 머지 → m-gate-prod 의 Gate 일 시작(그 전엔 G4 화면부터).
3. 기능 레인은 한 덩어리씩 바로 머지: R1, U1, S1 이 가장 먼저 나옴.
4. G3(스위치 켬) 뒤 R6(운영 라벨 접속 IT, win144).
5. l-e2e E3 은 실제 처리자 머지 뒤. 마지막 Z1·Z2.

### 4.1 공유 위험

| 같은 파일 | 고치는 레인 | 대응 |
|---|---|---|
| `AgentGrant.cs` | C0, m-gate-prod | C0 가 칸·함수 다 넣음. 뒤엔 m-gate-prod 가 스위치 두 줄만 |
| `AgentGrantStore.cs`·`AgentGate.External.cs` | it-fix, m-gate-prod(C0 한 줄) | it-fix 머지 뒤. C0 의 Store 한 줄이 겹치면 it-fix 쪽에 요청으로 넣음 |
| `AgentGate.cs`(내장 공용) | it-fix | 이번 분해에서 아무도 안 고침. 필요하면 요청 |
| `CommandTable.cs`·`OrandaSkillCommands.cs` | C0 만 | 뒤에 줄이 더 필요하면 m-skill-docs 가 받아 한 번에 |
| `AppTools.cs`·`AgentErrorCodes.cs` | C0 만 | 이후 `계약 요청:` → 조정자가 w-contract2 를 다시 엶 |
| `QueryToolHandler.cs` | C0(두 줄), w-run-export | C0 머지 뒤 w-run-export 소유 |
| `ExternalResultHandlers.cs`·`ExternalEditorModels.cs`·`ExternalEditorService.cs` | C0(호출 교체), w-run-export(Editor 띠) | C0 뒤 w-run-export 소유. Results 는 이번에 다른 레인 안 고침 |
| `ExecutionController.Body.cs` | w-run-export | 선택 인자만, 기본값으로 기존 호출 불변 |
| 문자열 표 `StringCatalog.cs` | — | 각 레인 자기 폴더 `*Text.cs` 상수(1단계 방식) |
| 스킬 본문 | m-skill-docs | 다른 레인은 글을 요청으로 |
| 감사 저장소 칸(G5) | m-gate-prod | 스키마 바꿔야 하면 Platform 저장소 소유를 m-gate-prod 로 명시 |

## 5. 설계 결정 목록

| id | 결정 | 기본안 / 상태 | 까닭 |
|---|---|---|---|
| D1 | SELECT 자동 실행(E-11 뒤집기) | **확정** | 사용자 결정 |
| D2 | 튜닝 자동으로 끝까지 | **확정** | 사용자 결정 |
| D3 | 스킬이 매뉴얼 링크로 기능 질문에 답 | **확정** | 사용자 결정 |
| D4 | 민감 열 가림, 로컬 모델이면 모두 보임 | **확정** | 사용자 결정. 로컬인지 확인 불가 → 사용자가 허가에서 고름 |
| D5 | 운영에서도 자동 실행, 로컬 모델이면 운영도 안 가림 | **확정**(사용자, 맥 기본안대로 켬) | 「주로 운영계에서 작업」. 앞 조정 확정(운영 유지)을 뒤집음. §3.2 장치 |
| E-22 | 운영 허용 위치 | `AgentGrantPolicy` 스위치 둘 + 정책 함수, 처리자는 함수만 부름 | 끄기 쉬움, 흩어진 판정 9곳을 하나로 |
| E-23 | 내장 AI 의 `DevTestOnly` | 건드리지 않음 | D5 는 외부 허가만. 내장 경로는 다른 결정 |
| E-24 | 탭 실행 경로 | `PrepareSingleStatementAsync` + `RunPreparedAsync(source: Agent)` | 사람 F5 와 같은 그리드·세션·취소·정책, 출처만 Agent |
| E-25 | 탭 실행 문장 검사 | Language 의 `ExternalSelectGuard`(query 검사 뽑음) + 정책 규칙 0 | 게이트는 App 도구의 SQL 을 못 봄. 게이트 파일(it-fix)과 겹치지 않음 |
| E-26 | 내보내기 | `ExportAsync` 로 다시 읽어 전체 행, 앱이 직접 씀, 새 파일만, 행 한도 없음, 띠+취소, N 등급(확인 없음) | D1 시나리오. 행이 LLM 에 안 감. 덮지 않으니 되돌릴 일 없음 |
| E-27 | 매뉴얼 | Core 색인 표(생성 스크립트+맞춤 시험), CLI 로컬 `docs`, 도구 없음 | 앱이 꺼져도 됨, Core 만 참조 규칙. MCP 만 쓰는 사용자는 스킬 목록으로 |
| E-28 | 매뉴얼 색인 범위 | 쪽 제목·설명·URL(+h2 제목은 `docs` 검색에만) | 스킬 길이 억제 |
| E-29 | 튜닝 자동 | 스킬 절차가 주(기존 도구 10개), `tune_start` 는 선택(앱 안 AI 를 띄움) | 밖의 LLM 이 끝까지 하는 게 D2. 앱 안 튜닝은 사람이 이어 볼 때 |
| E-30 | UI 명령 등급 | App 쪽 별도 표, `CommandDefinition` 에 칸 안 더함, 모르는 id = 거부 | Core 공개 타입·79개 정의 파일을 안 건드림. 새 명령은 안전 쪽 |
| E-31 | 명령 인자 | 이번엔 `--tab` 만 | 인자 타입이 명령마다 다름 |
| E-32 | 운영 탭 글 | 클라우드 = 거부 유지(E-18), 로컬 모델 = 허용 | 실행은 탭 글을 LLM 에 보내지 않아도 됨 |
| E-33 | `run_tab` 결과 | 행을 돌려주지 않음, `get_result` 로 읽음 | 출력 모양 하나(가림 규칙 한 곳) |
| E-34 | 바쁜 세션 | `tab_busy` 바로 돌려줌 | 사람 실행을 기다리며 다리를 잡지 않음 |

## 6. 확인 못 한 것

1. `ExportAsync` 가 정책 관문(규칙 0)·`HistorySource` 를 지나는지 — 내보내기의 다시 실행이 이력에 어떻게 남는지 못 봄. R4 에서 확인, 안 지나면 가드를 앞에 반드시.
2. `ExportSource` 를 탭·결과 번호로 만드는 기존 함수(결과 영역이 `ActiveExportSource` 를 채우는 곳) 위치 — 못 찾음.
3. 셸의 지금 `IContextKeys` 를 처리자가 얻는 길(`ExecuteAsync` 인자) — 못 봄. U3 에서.
4. 명령 제목 리소스 키 → 글 변환 함수, 등록 명령 정확한 수(맥 79 vs 문자열 200 남짓, 동적 등록 포함).
5. `AiTuningLauncher.RunAsync` 를 다리 스레드에서 부를 때 UI 스레드 요구·확인 창(보내기 전 확인)이 뜨는지 — D2 「끝까지 자동」과 부딪힐 수 있음.
6. 감사 저장소(`AgentAuditEntry`)에 행 수·파일 경로 칸이 있는지(크기 칸만 봄). G5 가 스키마 변경을 부를 수 있음.
7. it-fix 가 `AgentGrantStore.cs` 를 고치는지 — 범위를 모름. C0 머지 전 조정자가 확인.
8. DB 접근 통제 도구가 에이전트 SELECT 를 결재 대상으로 잡는지(1단계 §10 #8 그대로). 운영 자동 실행에서 더 중요.
9. 탭 작업 세션이 운영에서 열린 트랜잭션을 가진 채 에이전트 SELECT 가 돌 때 사람 화면(미커밋 경고 등)에 영향이 있는지.

## 조정자가 바로 쓸 레인 brief 요약

**w-contract2 (win144, 먼저 단독 머지, 1시간)**
- 목표: §2 계약만. 동작 변화 없음(스위치 false)
- 소유: AppTools.cs·AgentErrorCodes.cs·AgentGrant.cs·Core/Ai/Manual/**(빈 껍데기)·CommandTable.cs·OrandaSkillCommands.cs + §2.2 호출 자리 한 줄씩
- 금지: AgentGate*.cs, 화면, 새 처리자 본체
- 할 일: 도구 5개 정의·필드·값 / 허가 `LocalModel`·정책 함수·스위치 둘 / 오류 코드 6개·종료 코드 / CLI 6줄·스킬 6줄 / 외부 도구 40 시험
- 시험: Core·Agent.Cli·Language External·App External 필터 1번. Perf 금지
- 끝: 머지 요청 → 다른 레인 착수 신호

**w-run-export (win144, C0 뒤)**
- 목표: F1 `run_tab`·`export_result` + DB IT, 맥 레인 DB IT 대행
- 소유: App/Features/Ai/External/Run/**·Editor/**, ExecutionController.Body.cs(출처 인자), DataTransferCommands.cs, QueryToolHandler.cs·ExternalSelectGuard.cs, Oracle IT 의 새 External* 파일
- 금지: Gate·Grant·Connect·Skills, AiToolsGateIntegrationTests.cs(it-fix)
- 규칙: §3.1 그대로(한 문장 SELECT/WITH, base_version stale, 30/60초, 출처 Agent, 띠·메시지·`AI·` 표시, 새 파일만·임시 이름)
- 순서: R1 가드 → R2 탭 실행 → R3 표시 → R4 내보내기 → R5 IT → R6 운영 IT(G3 뒤)
- 확인할 것: §6 #1·#2·#9

**m-gate-prod (맥 A)**
- 목표: F4 로컬 모델 + D5 운영 갈래 켜기 + 연결 창
- 소유: AgentGate.External.cs·AgentGrantStore.cs·AgentGrant.cs 스위치 두 줄·External/Connect/**·관련 Core 시험
- 금지: AgentGate.cs(내장 공용), Run/**·Editor/**·Skills/**
- 선행: C0, Gate 파일은 it-fix 머지 뒤(그 전에 G4 화면)
- 할 일: 하위 세션 LocalAi·리터럴 사전을 정책 함수로 / 외부 게이트 운영 판정 `MayExecute` / 스위치 켬+운영 단위 시험 / 연결 창 로컬 모델 체크·운영 경고·경고색 띠 / 운영 감사 보강
- DB 시험 금지(R6 에 부탁). §3.2 표가 정본

**m-ui-cmd (맥 B)**
- 목표: F5 `list_commands`·`run_command` + 등급표
- 소유: App/Features/Ai/External/Commands/**와 그 시험
- 금지: Core/Shell/**(정의 칸 추가 금지), 다른 External 폴더
- 할 일: §3.3 기준으로 접두어 규칙+예외 표(모르는 id = deny) / 제목·메뉴 경로·enabled 목록 / `--tab` 앞으로→UI 스레드 실행, confirm 은 띠 한 번 / 등록 명령 전부가 표에 걸리는 시험
- 확인할 것: §6 #3·#4. Headless 까지, 실앱 SendKeys 금지

**m-skill-docs (맥 C)**
- 목표: F3 매뉴얼 색인·`docs`, F2 스킬 튜닝 절차·`tune_start`, 스킬 글 전체·매뉴얼 장
- 소유: Platform/Ai/Skills/**, Core/Ai/Manual/**(C0 뒤), Agent.Cli 의 새 DocsCommand.cs, External/Tune/**, site/.../ai/external-agents.md
- 금지: CommandTable.cs(요청으로), Gate, Run/**
- 할 일: site 머리말 → 색인 생성 스크립트·표·맞춤 시험 / `orandactl docs` / 튜닝 절차 글(context→plan→stats/index/histogram→후보→bench→새 탭+보고) / `tune_start` / 실행·내보내기·ui·운영 경고 글 / 매뉴얼 갱신
- 확인할 것: §6 #5(자동 튜닝 중 확인 창)

**l-e2e (linux)**
- 목표: 실제 LLM CLI 왕복으로 네 시나리오 확인
- 소유: tools/agent-e2e/** 만. src/** 금지
- 할 일: FakeHost 에 새 도구 가짜·실제 처리자 모드 / 시나리오 4개(nl2sql→새 탭→실행→xlsx, 튜닝 끝까지, 기능 질문→링크, 메뉴 명령) / claude·opencode 실제 왕복·verdict 늘림 / codex·agy 는 맥에 한 번 부탁
- 선행: C0(가짜), 실제 처리자 모드는 R2·R4·U3·S2·S4 머지 뒤
- 과제마다 1~2회만(비용)

---

## 부록: 요청 원문 요약
### 2단계 요청 (사용자 → 맥 세션 → 조정, 2026-11-22 22:45)

사용자 말: 「Oranda 에서 되는 것은 Oranda 를 통해 LLM 에서도 되게. 버튼 눌러 확인할 일은 스킬로 바로. 특정 메뉴에 들어가 특정 동작을 하는 등 컴포넌트 제어도.」
시나리오: nl2sql(llm-wiki 프로젝트 스킬)로 만든 SQL → 새 SQL 창 → 자동 실행 → 전체 행 → xlsx 저장 / 로그 분석 프로젝트의 SQL → Oranda 정보로 자동 튜닝 / 모르는 Oranda 기능을 물으면 매뉴얼 링크로 답 / 여러 정보를 조합

사용자 결정(명시): D1 SELECT 는 자동 실행(E-11 뒤집기). D2 튜닝은 자동으로 끝까지. D3 스킬이 매뉴얼 링크를 갖고 기능을 물으면 알려 줌. D4 민감 열은 가림, 로컬 모델이면 모두 보임.
조정 확정: 운영 접속은 지금 정책 그대로(자동 실행·가림 해제 없음). 「로컬 모델」은 사용자가 허가에서 고름(진짜 로컬인지는 확인 불가).

맥 세션 기본안:
F1 실행·내보내기 (Core/Ai·App/External·Agent.Cli): `tab run --tab ID`(탭의 SELECT/WITH 를 앱 결과 그리드에서 실행, 게이트 문장 검사 재사용, DML·DDL·PLSQL 거부), `result export --tab ID --format xlsx|csv --out PATH`(기존 DataTransferService.ExportAsync 로 전체 행을 앱이 직접 씀, 행은 LLM 에 안 감, 새 파일만·덮지 않음·띠로 알림). query 에 --all-to-file 은 안 넣음
F2 튜닝 자동 (스킬 글 + 선택 도구): 스킬에 튜닝 절차(context → plan → stats/index/histogram → 후보 → tune bench → 가장 나은 것을 새 탭 + 보고). 선택 도구 `tune start`(SQL 로 새 탭을 열고 앱 안 AI 튜닝 AiTuningLauncher 시작)
F3 매뉴얼 (Platform/Ai/Skills + site): 스킬 렌더 때 site/src/content/docs 의 제목→URL(https://jongik-sv.github.io/oranda/...) 색인. `orandactl docs [검색어]` = 링크 목록만
F4 가림 (Core/Ai/Gate): 외부 허가에 「로컬 모델(가리지 않음)」 선택 + 켜기 화면 체크·경고. 게이트의 기존 LocalAi 경로 재사용. 기본 = 가림
F5 UI 제어 (Core/Shell/Commands·App): `ui commands`(ICommandRegistry 목록: id·제목·메뉴 경로·안전 등급), `ui run <id> [args]`(화면·열기·토글 = 바로, 파일 쓰기 = 띠 확인, DB 변경·커밋·삭제 = 거부). 명령마다 안전 등급 표를 새로 만듦

## D5 (사용자 → 맥, 22:58) — 사용자 직접 확인: 맥 기본안대로 켬 (확정)
「운영 접속에서도 되면 좋겠다. 주로 운영계에서 작업한다.」 → 앞의 「운영 = 정책 유지」 를 뒤집음.
맥 기본안: 운영에서도 SELECT 자동 실행(tab run·query·result export) 허용. 문장 검사 그대로(SELECT/WITH 1문장, FOR UPDATE·DB 링크·사용자 함수 차단, DML·DDL·PL/SQL·커밋 금지). 운영 한도 timeout 30s(최대 60s), query 최대 200행, export 는 앱이 직접 쓰고 행 한도 없음·취소 가능. 가림: 클라우드 모델이면 운영도 민감 열 *** 와 리터럴 가림 유지, 「로컬 모델」 허가면 운영도 모두 보임(AgentSession.cs:39·AgentGate.cs:287 규칙 그대로). 허가 화면: 운영은 빨간 표시+「운영 접속 — LLM 이 SELECT 를 자동 실행합니다」 경고, 기본 체크 안 함. 감사: 운영 외부 호출 전부(문장·클라이언트·행 수). ui run 은 운영에서도 같은 등급표. 문서에 접근 통제 솔루션 주의 한 줄.
