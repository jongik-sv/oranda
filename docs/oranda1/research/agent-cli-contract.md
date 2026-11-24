# 외부 에이전트 연결 — 공유 계약 (A1)

- 작성일: 2026-11-22 (회차 agent-cli-1122, 레인 a-contract)
- 설계서: [agent-cli-control-2026-11-22.md](agent-cli-control-2026-11-22.md). 이 문서는 설계서 §3.3·§4.1·§5 를 **코드로 정한 값**만 적는다. 다른 레인은 이 계약을 바꾸지 않는다(모자라면 `계약 요청:`).

## 1. 타입·네임스페이스

| 타입 | 파일 | 누가 쓰나 |
|---|---|---|
| `Oranda.Core.Ai.Schemas.AppToolNames` · `AppFields` · `AppToolValues` · `AppTools` (`All` 23 · `External` 35 · `Find`) | `src/Oranda.Core/Ai/Schemas/AppTools.cs` | B·C·D·E |
| `AgentToolDefinition` 의 `Host`(`AgentToolHost` Db/App/Lang) · `Safety`(`AgentToolSafety` Read/Ui/Propose/Confirm/New/Execute) | `src/Oranda.Core/Ai/Schemas/AgentTools.cs` | 게이트(A3)·B `tools` |
| `AgentFields.RowValues`(`row_values`) · `AgentFields.Parts`(`parts`) | 같은 파일 | D(`query`·`describe_object`)·E(`get_result`) |
| `Oranda.Core.Ai.Gate.IAppToolHandler` · `AppToolInvocation` · `AppToolArgs` · `AppTextRange` · `AppToolOutputs` · `AppToolArgumentException` | `src/Oranda.Core/Ai/Gate/IAppToolHandler.cs` | C·D(Lang)·E |
| `AgentToolOutput.ErrorCode` · `AgentToolOutput.Error(code, message)` | `src/Oranda.Core/Ai/Gate/IAgentToolHandler.cs` | 모든 처리자 |
| `AgentGrantView` · `AgentGrantConnection` · `AgentGrantTarget` · `AgentGrantRequest` · `AgentGrantIssue` · `AgentGrantChanged` · `IAgentGrants` · `AgentGrantPolicy` | `src/Oranda.Core/Ai/Gate/AgentGrant.cs` | C·D·E(가짜 허가), E1 연결 창(`IAgentGrants`), A2 구현 |
| `Oranda.Core.Ai.Protocol.AgentErrorCodes` · `AgentCliExit` | `src/Oranda.Core/Ai/Protocol/AgentErrorCodes.cs` | 모두, B 종료 코드 |
| `AgentRpc.ErrorCodes.GrantExpired`(-32006) · `AgentRpc.ErrorCodes.ToErrorCode` | `src/Oranda.Core/Ai/Protocol/AgentRpc.cs` | A4 다리·B 클라이언트 |
| `AgentEndpointFile` · `AgentEndpoint` · `AgentEndpointConnection` | `src/Oranda.Core/Ai/Protocol/AgentEndpointFile.cs` | A4 쓰기·B1 읽기 |
| `Oranda.Core.Storage.AppDataLocation` · `AppOs`, `IAppDataPaths.AgentsDirectory` | `src/Oranda.Core/Storage/AppDataLocation.cs` | A4·B1 (`AppDataPaths` 가 위임) |

## 2. 처리자 규칙

- Host `Db` = 기존 `IAgentToolHandler`(게이트 + DB 세션). Host `App`·`Lang` = `IAppToolHandler`(DB 세션 없음). 등록 = `[OrandaService(typeof(IAppToolHandler))]`.
- 다리·게이트(A3·A4)가 하는 일: 인자 정리(스키마 키만), `connection` 풀기(값 = 별칭, 없으면 허용 접속이 하나일 때만 그 접속 → `AppToolInvocation.Connection`), 정의의 최소 수준·운영 제한, 감사, 80,000자 한도, `connection` 이 있는 호출의 행 값 필드 가림.
- 처리자가 하는 일: **탭이 접속을 정하는 도구**(`get_tab`·`get_binds`·`get_result`·`get_last_error`·`list_tabs`)는 `Grant.LevelFor`·`CanReadTabText`·`IsLocked` 로 스스로 판단. `row_values` 표의 민감 열은 처리자가 `"***"`(`AppToolValues.Masked`). 실패는 `AppToolOutputs.Deny(code, 영어 한 문장)`. 인자는 `AppToolArgs` 로 읽고 본문을 `AppToolOutputs.Guard` 로 감싼다.
- `range` 인자 = 글 하나: `document|selection|statement|caret` 또는 `"시작:길이"`(`AppTextRange`). 게이트 인자 검사가 `oneOf` 를 읽지 않기 때문이다.
- 글 인자(`text`·`new_text`·`sql`)는 스키마 `maxLength` 200,000(`AppToolValues.MaxTextChars`). 게이트의 기존 인자 한도 20,000자는 A3 가 Host App/Lang 에 대해 `maxLength` 로 바꾼다.

## 3. 도구 23개

수준 `—` = 정의에 최소 수준 없음(Off). 처리자가 허가로 판단한다.

| 도구 | Host | 등급 | 최소 수준 | 필수 인자 | 출력 칸 |
|---|---|---|---|---|---|
| `get_status` | App | R | — | — | `app_version, pid, label, expires_at, clients[], connections[{alias, environment, level, dbms, connected}], tool_count` |
| `list_connections` | App | R | — | — | `connections[{alias, environment, dbms, level, read_only, current_schema?, connected}]` |
| `list_tabs` | App | R | — | — | `tabs[{tab_id, title, workspace, kind, connection?, dirty, read_only, version, active, file_name?, locked}]` |
| `get_tab` | App | R | — | — | `tab_id, version, text, truncated, range{start,length}, caret{offset,line,col}, selection{start,length,block}, statement?{start,length,kind}, connection?, binds[]` |
| `new_tab` | App | N | — | `text` | `tab_id, version` |
| `propose_edit` | App | P | — | `tab, base_version, new_text` | `proposal_id, status, version?, final_text?` |
| `get_proposal` | App | R | — | `proposal_id` | `propose_edit` 과 같음 |
| `focus_tab` | App | U | — | `tab` | `ok` |
| `save_tab` | App | C | — | `tab` | `status, file_name?` |
| `get_binds` | App | R | — | `tab` | `binds[{name, type, has_value, value?}]` |
| `reveal_object` | App | U | Metadata | `object` | `found, opened` |
| `check_sql` | Lang | R | — | (`sql` 또는 `tab`) | `statements[{index, kind, start, length, line}], diagnostics[{severity, message, line, col, start, length, code?}], binds[]` |
| `format_sql` | Lang | R | — | (`sql` 또는 `tab`) | `text, changed, failure?` |
| `find_objects` | Db | R | Metadata | `name_like` | `objects[{owner, name, type, status, comment?}], truncated` |
| `list_schemas` | Db | R | Metadata | — | `schemas[]` |
| `get_ddl` | Db | R | Metadata | `object` | `owner, name, type, sql_text`(DDL 글은 리터럴 가림을 타는 `sql_text` 칸) |
| `get_source` | Db | R | Metadata | `object` | `owner, name, type, sql_text, members[]`(소스 글도 `sql_text` 칸, A3 에서 바꿈) |
| `resolve_names` | Db | R | Metadata | `names` | `names[{input, found, owner?, name?, type?, via_synonym?}]` |
| `get_procedure_args` | Db | R | Metadata | `object` | `members[{member, overload, args[{name, type, in_out, default}], returns?}]` |
| `query` | Db | X | Query(운영 불가) | `sql` | `columns[{name, type}], row_values?[[…]], row_count, truncated, elapsed_ms, sql_id` |
| `get_result` | App | R | — | `tab` | `columns[{name, type}], row_values?, fetched, more, statement_kind, elapsed_ms` |
| `get_last_error` | App | R | — | — | `code?, message, statement?, line?, col?, at` |
| `get_history` | Lang | R | Metadata | — | `entries[{at, source, status, elapsed_ms, sql_text, error_code?}]` |

- 상태 값: `propose_edit` = `pending|accepted|modified|rejected|stale|timeout`, `save_tab` = `saved|rejected|timeout` (`AppToolValues.ProposalStatus`·`SaveStatus`). `stale`·`rejected` 는 오류가 아니라 성공 결과다(종료 코드 0).
- 목록 결과는 맨 위를 배열이 아니라 객체로 둔다(`{"tabs":[…]}` 처럼). 다음에 칸을 더해도 깨지지 않는다.
- 행 값 칸 이름은 `rows` 가 아니라 `row_values` 다. 기존 `benchmark_queries` 결과의 `rows` 가 행 수(숫자)라 `rows` 를 행 값 목록(`RowValueFields`)에 넣으면 Query 수준에서 행 수가 지워진다. 열 이름 칸은 `name`(`ColumnIdentityFields` 안).
- SQL 글이 들어가는 칸은 `sql_text`(`SqlTextFields` 안)로 둬서 리터럴 가림을 탄다. 기존 도구에는 `describe_object` 의 선택 인자 `parts`(`columns|keys|indexes`)만 더했다.

## 4. 오류 코드 → `orandactl` 종료 코드

오류 JSON = `{"error":"…","code":"…"}`. `AgentCliExit.For(code)`.

| 종료 | 뜻 | 코드 |
|---|---|---|
| 0 | 성공(제안 거절·낡음 포함) | — |
| 1 | 도구 실패 | `tool_failed` `tool_exception` `internal_error` `not_implemented` `canceled` `busy` `result_too_large` `tab_not_found` `proposal_not_found` `no_result`, 모르는 코드 |
| 2 | 사용법 | `usage` `unknown_tool` `invalid_arguments` `path_invalid`(§8.4) `bind_invalid`(§9) |
| 3 | 앱에 닿지 못함·연결 끝남 | `oranda_not_running` `no_endpoint` `oranda_run_invalid` `grant_expired` `run_revoked` `run_required` `hello_required` `oranda_shutting_down` `unsupported_protocol` `bridge_error` `sandbox_blocked`(CLI 가 연결 파일 읽기·접속에서 EPERM·EACCES 를 받음. 샌드박스 밖에서 다시 부를 것) |
| 4 | 거부 | `connection` `connection_required` `level` `environment` `scope` `sql_id_scope` `sql_not_single_query` `sql_schema_unresolved` `sql_unsafe` `no_statement` `explain_plan_disabled` `literal_mask_unavailable` `session_mismatch` `unsafe_result` `locked` `read_only` `not_allowed` `rate_limited` |
| 5 | 창을 고르지 못함 | `multiple_instances` `instance_not_found` |

JSON-RPC 오류(hello 단계)는 `AgentRpc.ErrorCodes.ToErrorCode` 로 위 글자로 바꾼다. 만료는 `-32006` → `grant_expired`.

## 5. 연결 파일

경로 `AppDataLocation.EndpointFile(dataDir, pid)` = `<데이터 폴더>/run/agents/<pid>.json`. 데이터 폴더 = `AppDataLocation.Resolve(getEnv, os)`(`ORANDA_DATA_DIR` → Windows `%APPDATA%\Oranda`, macOS `$HOME/Library/Application Support/Oranda`, Linux `$XDG_DATA_HOME/Oranda` 또는 `$HOME/.local/share/Oranda`).

```json
{
  "v": 1,
  "pid": 23140,
  "started_at": "2026-11-22T09:12:03+09:00",
  "app_version": "0.1.32",
  "label": "기본 작업 공간",
  "endpoint": { "kind": "pipe", "name": "oranda-agent-3f9c0a1b2c3d4e5f" },
  "token": "q2V8…(base64url 43자)",
  "expires_at": "2026-11-22T17:12:03+09:00",
  "connections": [
    { "alias": "dev-erp", "environment": "Development", "level": "QueryRows" },
    { "alias": "prd-erp", "environment": "Production", "level": "Metadata" }
  ]
}
```

- `endpoint.kind` = `pipe`(Windows, 이름) 또는 `unix`(소켓 절대 경로). `expires_at` 이 null 이면 앱을 끌 때까지.
- `AgentEndpointFile.TryParse` 는 JSON 이 틀리거나 `v ≠ 1`·`pid ≤ 0`·통로 종류/이름이 비거나·토큰이 없으면 null(건너뜀). `ToString()` 은 토큰을 빼고 보인다.

## 6. 허가 읽기 모델 (A2 가 구현)

- `AgentGrantView(GrantId, Connections, CreatedAt, ExpiresAt, Clients, AllowUnboundTabText)` — 변하지 않는 사본. `ResolveConnection(alias, out code)`·`FindConnection(alias|profileId)`·`LevelFor(profileId)`·`IsLocked(profileId)`·`CanReadTabText(profileId)`(운영 탭은 언제나 거짓)·`IsExpired(now)`.
- `AgentGrantConnection.Level` = `AgentGrantPolicy.LevelFor(profile, cap)` = min(접속 실효 수준, 상한, 환경 상한). 환경 상한 = 운영 Metadata, 그 밖 QueryRows. 기본 기한 8시간.
- `IAgentGrants`: `Active`·`Create(AgentGrantRequest)`→`AgentGrantIssue(Grant, Token)`·`Resolve(token)`·`Extend(id, by)`·`AddClient(id, name)`·`Revoke(id)`·`Changed` 사건.

## 7. A2~A4 에서 더한 것

| 타입 | 파일 | 뜻 |
|---|---|---|
| `AgentGrantStore` (`IAgentGrants` + `IAgentGrantSessions`) | `src/Oranda.Core/Ai/Gate/AgentGrantStore.cs` | 메모리 허가 저장소. 창마다 허가 하나(다시 켜면 옛 것 폐기), 기한 타이머, 접속 저장(`ConnectionProfileSaved`) 때 수준 다시 계산·하위 세션 폐기. 앱 조립(`AppComposition.RegisterAi`)이 하나만 등록 |
| `IAgentGrantSessions` · `AgentGrantLookup` | 같은 파일 | 다리·게이트용: `Lookup(token)`(만료 구분)·`Find(id)`·`SessionFor(id, profileId, client)`·`AddClient`·`AttachWriter`·`RevokeAll` |
| `IAgentEndpointWriter` | 같은 파일 | 연결 파일 쓰기(`Publish`·`Update`·`Remove`). 다리가 열릴 때 붙인다. 안 붙었으면 `Create` 가 `InvalidOperationException` |
| `IAgentExternalGate` · `AgentExternalToolActivity` | `src/Oranda.Core/Ai/Gate/AgentGate.External.cs` | 외부 관문(= `AgentGate`). `ListTools(grant)`·`RunsWithoutDatabase(tool)`·`CallToolAsync(grant, client, tool, args)`·`ExternalToolCompleted` 사건(허가 id·클라이언트·접속·도구·판단·크기·메모·시각, Db·App·Lang 모두) |
| `AgentRunScope.External` | `src/Oranda.Core/Ai/Gate/AgentSession.cs` | 외부 하위 세션 표시(게이트가 `AppTools.External` 의 Db 도구를 찾는다) |
| `AgentEndpointFiles` · `AgentSocketPath` | `src/Oranda.Platform/Ai/Bridge/` | 연결 파일 0600·임시 이름 뒤 바꿔 넣기·시작 때 죽은 pid/같은 pid 파일 청소, 소켓 경로가 sun_path 한도(macOS 103·Linux 107 바이트)를 넘으면 `<tmp>/oranda-<uid>/agent[-pid].sock` |

- 하위 세션 = 접속 × 클라이언트마다 하나. 실행 id `허가 id:별칭`, 시작 문장 없음, 보낼 범위 = 접속 전체, 운영은 빈 리터럴 사전 + 가림 필수(결과의 `sql_text` 를 가린다). 개발·시험은 리터럴을 가리지 않는다.
- 다리: hello 토큰이 내장 실행이 아니면 허가로 푼다(만료 = `-32006`). 끝난 허가는 열린 연결에서도 `TokenInvalid`. App·Lang 도구는 DB 대기열(`_callGate`) 밖에서 처리한다. 다리를 닫으면 모든 허가를 폐기하고 파일을 지운다.
- 게이트: `query` 는 문장을 실제로 실행하는 도구로 본다(사용자 함수 확인). 커서 실측 대체가 없으므로 DB 링크·확인 못 한 함수는 `sql_unsafe`. 외부 하위 세션의 `get_ddl`·`get_source`·`get_procedure_args` 는 `object` 이름 풀기(표·뷰·동의어만)를 거치지 않고 그대로 넘긴다 — 처리자가 스스로 푼다(`Resolved` 비어 있음).
- App·Lang 도구: 스키마 `maxLength`·`enum`·`boolean` 을 검사한다(Db 도구 글 인자 한도는 기존 20,000자). `connection` 은 값이 있으면 허가 안 별칭이어야 하고, `reveal_object` 만 꼭 필요하다(나머지는 고르기용). 접속이 정해진 호출은 그 수준으로 정의 최소 수준·운영 제한·행 값 가림을 건다.

## 8. 2단계 계약 (C0, 회차 agent-cli-1122 2단계, 레인 w-contract2)

- 정본: [agent-cli-phase2-plan.md](agent-cli-phase2-plan.md) §2. **동작은 바뀌지 않는다** — 새 도구는 목록에만 나오고 처리자가 붙기 전까지 `not_implemented`, 운영 스위치 둘은 꺼짐.
- 새 도구 5개는 `AppTools.All` 맨 뒤(`All` 28 · `External` 40). 운영 판단은 정의(`DevTestOnly`)가 아니라 처리자가 `AgentGrantPolicy` 로 한다.

### 8.1 새 도구 5개

| 도구 | Host | 등급 | 최소 수준 | 필수 인자 | 선택 인자 | 출력 칸 | 처리 레인 |
|---|---|---|---|---|---|---|---|
| `run_tab` | App | X | —(처리자: 탭 접속 `MayExecute`) | `tab` | `base_version`, `range`(`document`\|`statement`, 기본 document = 한 문장 탭만), `timeout_s`(기본 30·최대 60) | `status`(`succeeded`\|`canceled`\|`failed`\|`stale`), `result_index`, `statement_kind`, `fetched`, `more`, `elapsed_ms`, `sql_id?`, `error?{code,message}` | w-run-export |
| `export_result` | App | N | — | `tab`, `format`(`xlsx`\|`csv`), `out`(절대 경로) | `index`, `sheet` | `path`, `rows`(숫자), `bytes`, `elapsed_ms`, `status`(`written`\|`canceled`) | w-run-export |
| `tune_start` | App | N | Plan(목록용, 처리자가 탭 수준 확인) | `tab` | `range`(`statement`) | `started`, `chat` | m-skill-docs |
| `list_commands` | App | R | — | — | `category`, `grade`(`now`\|`confirm`\|`deny`) | `commands[{id, title, category, menu_path?, grade, enabled}]` | m-ui-cmd |
| `run_command` | App | U | — | `id` | `tab` | `status`(`done`\|`confirmed`\|`rejected`\|`timeout`\|`disabled`), `grade` | m-ui-cmd |

- 이름 = `AppToolNames.RunTab`·`ExportResult`·`TuneStart`·`ListCommands`·`RunCommand`.
- 새 인자 = `AppFields.Format`·`Out`·`Sheet`·`Id`·`Grade`·`Category`. 재사용 = `Tab`·`BaseVersion`·`Range`·`TimeoutSeconds`·`Index`.
- 고정 값 = `AppToolValues.RunStatus`·`ExportStatus`·`ExportFormats`·`CommandGrades`·`CommandStatus`(각각 `All` 목록 있음). `range` 값은 기존 `AppToolValues.Scopes`.
- `export_result` 의 `rows` 는 행 수(숫자)라 행 값 가림(`RowValueFields`)에 걸리지 않는다. 행 값은 LLM 에 가지 않는다.

### 8.2 CLI·스킬 명령 줄

| CLI | 도구 |
|---|---|
| `tab run --tab ID [--base-version N] [--range document\|statement] [--timeout 30]` | `run_tab` |
| `result export --tab ID --format xlsx\|csv --out PATH [--index N] [--sheet S]` | `export_result` |
| `tune start --tab ID` | `tune_start` |
| `ui commands [--category C] [--grade now\|confirm\|deny]` | `list_commands` |
| `ui run <id> [--tab ID]` | `run_command` |
| `docs [검색어...]` | 도구 없음 — CLI 자체 명령(앱 없이 됨). `CommandTable` 에는 없고 스킬 줄(`OrandaSkillCommands`)에만 있다. 파서·본체는 m-skill-docs |

### 8.3 허가의 로컬 모델 칸과 운영 스위치 (`AgentGrant.cs`)

- `AgentGrantRequest.LocalModel`(기본 false) → `AgentGrantView.LocalModel`(init 속성). `AgentGrantStore.Create` 가 옮기고 연장·클라이언트 추가 사본에도 남는다. 진짜 로컬 모델인지는 확인하지 못한다.
- 스위치(`static readonly bool` — const 면 시험 `if` 에서 CS0162 로 -warnaserror 가 깨진다. C0 = **false**, 켜는 일 = m-gate-prod G3): `AgentGrantPolicy.ProductionRuns`(D5: 운영에서도 실행) · `AgentGrantPolicy.LocalModelCoversProduction`(D5: 로컬 모델이면 운영도 가리지 않음).

| 정책 함수 | 값 | 부르는 곳 |
|---|---|---|
| `EnvironmentCap(env)` | 운영 = `ProductionRuns ? QueryRows : Metadata`, 그 밖 QueryRows | `DefaultCap`·`LevelFor` |
| `ExecutesIn(env)` | 개발·시험 ‖ `ProductionRuns` | `QueryToolHandler`(운영 거부), `MayExecute` |
| `MayExecute(connection)` | 수준 ≥ Query && `ExecutesIn` | `run_tab`·`export_result`(새 처리자), m-gate-prod 게이트 운영 갈래 |
| `UsesLocalModel(grant, env)` | `grant.LocalModel` && (개발·시험 ‖ `LocalModelCoversProduction`) | 하위 세션 `AiContext.LocalAi`(m-gate-prod G1) |
| `MasksSensitive(grant, env)` | !`UsesLocalModel` | 결과 그리드 민감 열·비밀 바인드 |
| `MasksLiterals(grant, env)` | 운영 && !`UsesLocalModel` | 이력 `sql_text`·마지막 오류 글·하위 세션 리터럴 사전 |
| `CanReadProductionTabText(grant)` | `UsesLocalModel(grant, 운영)` | `AgentGrantView.CanReadTabText`(E-18 유지) |

- 허가 사본 도우미: `AgentGrantView.MasksSensitive(profileId)`(접속 모르면 가림) · `AgentGrantView.MasksLiterals(profileId)`(접속 모르면 거짓). `IExternalTabAccess.MasksSensitive(profileId)`(기본 참, `GrantTabAccess` 는 허가에 맡김).
- C0 가 바꿔 낀 자리(동작 같음): `AgentGrant.cs` `CanReadTabText`, `QueryToolHandler` 운영 거부·민감 열(`ReadAsync(..., maskSensitive: !Session.Context.LocalAi)`), `HistoryToolHandler` 리터럴, `ExternalResultHandlers` 민감 열·오류 글, `ExternalEditorModels` 운영 거부 글, `ExternalEditorService` 비밀 바인드.
- C0 가 건드리지 않은 자리(m-gate-prod): `AgentGate.External.cs` 의 `DevTestOnly && Production`(두 곳)과 `AgentGrantStore` 하위 세션(운영 리터럴 사전·`LocalAi`).

- 행 수 칸: `AgentToolOutput.RowCount`(long?, 가져오거나 파일로 쓴 행 수) · `AppToolOutputs.OkWithRows(result, rows)` · `AgentToolOutput.RowsNote(rows)` = `rows=N`(감사 사유 글). 처리자(`query`·`run_tab`·`export_result`)가 채우고, 게이트가 감사 사유에 남기는 배선은 m-gate-prod(G5). C0 에서 게이트는 이 값을 읽지 않는다.

### 8.4 새 오류 코드 6개

| 코드 | 종료 | 뜻 |
|---|---|---|
| `file_exists` | 4 | `export_result` 의 `out` 이 이미 있다(덮지 않음) |
| `path_invalid` | 2 | 상대 경로·폴더 없음·확장자가 `format` 과 다름 |
| `command_not_found` | 1 | `run_command` 모르는 id |
| `command_denied` | 4 | 등급표 「거부」·표에 없는 명령 |
| `command_disabled` | 4 | 지금 문맥에서 못 쓰는 명령(`Enablement` 거짓) |
| `tab_busy` | 1 | 그 탭 세션이 실행 중 |

- 상수 = `AgentErrorCodes.FileExists`·`PathInvalid`·`CommandNotFound`·`CommandDenied`·`CommandDisabled`·`TabBusy`. 모두 `AgentErrorCodes.All` 에 있다.

### 8.5 매뉴얼 색인 껍데기 (`src/Oranda.Core/Ai/Manual/OrandaManual.cs`)

- `OrandaManual.BaseUrl` = `https://jongik-sv.github.io/oranda`, `OrandaManual.Entry(Title, Description, Url, Section)`, `All`(C0 = 빈 목록), `Search(query, limit = 10)`·`Search(query, entries, limit)`(낱말이 모두 제목·설명·장 이름에 있는 줄, 대소문자 무시, 빈 검색어 = 전부).
- 표 채우기·site 와 맞춤 시험·`orandactl docs` = m-skill-docs(S1·S2).

## 9. 탭 바인드 값 채우기 (`set_binds`, 2026-11-23)

- 까닭: 외부 에이전트가 바인드 하나(YYYYMMDD 글)가 있는 탭을 만들고 `sql check` 는 지났지만 `tab run` 이 「바인드 값 없음」으로 막혔다. `orandactl` 은 바인드를 읽기만(`tab binds` = `get_binds`) 할 수 있었고, `query --binds-json` 은 결과를 탭 그리드에 보이지 않아 사용자에게 바인드 창에 값을 치라고 부탁해야 했다. 설계서 §5.3 「이번에 두지 않는 도구」의 `set_binds` 를 넣는다.
- 보고: [agent-cli-set-binds-2026-11-23.md](agent-cli-set-binds-2026-11-23.md).

### 9.1 도구·인자

| 도구 | Host | 등급 | 최소 수준 | 필수 인자 | 선택 인자 | 출력 칸 |
|---|---|---|---|---|---|---|
| `set_binds` | App | X(`run_tab` 과 같음) | —(처리자: 탭 접속 `MayExecute`) | `tab`, `binds`(객체) | `base_version` | `status`(`set`\|`stale`), `version`, `binds[{name, type, has_value, value?}]`(`get_binds` 와 같은 모양·같은 가림) |
| `run_tab`(인자 더함) | App | X | — | `tab` | 기존 + `binds`(객체) | 그대로 |

- 이름·값 = `AppToolNames.SetBinds` · `AppFields.Binds`(`query` 와 같은 `binds`) · `AppToolValues.BindStatus.Set`·`Stale`(`All`). `AppTools.All` 맨 뒤(`All` 29 · `External` 41). 두 도구의 `binds` 스키마 글은 같다(`AppTools.BindsArg`).
- CLI: `tab binds set --tab ID --binds-json J [--base-version N]` → `set_binds`, `tab run … [--binds-json J]` → `run_tab` 의 `binds`. `tab binds --tab ID`(읽기)는 그대로.

### 9.2 규칙 (`ExternalBindFill`, `SetBindsHandler`, `RunTabHandler`)

- 관문 = `run_tab` 과 같은 `ExternalRunSupport.Resolve`: 탭 없음 `tab_not_found`, 접속 없는 탭 `connection_required`, 허가 밖 접속(잠김) `locked`, 수준 < Query `level`, `MayExecute` 거짓 `environment`. `set_binds` 는 DB 를 쓰지 않으므로 접속이 열려 있을 필요는 없다.
- `base_version` 이 지금 판과 다르면 넣지 않고 `{status: stale, version}`(종료 0 — `run_tab`·`propose_edit` 의 낡음과 같은 상태 글). `run_tab` 은 기존처럼 `RunStatus.Stale`.
- 이름 = 탭이 아는 바인드(글이 참조하는 이름 ∪ 바인드 창 줄, `get_binds` 가 보이는 이름). 앞 `:` 는 빼고 대문자로(`BindVariableRow.Normalize`). 모르는 이름·대소문자만 다른 같은 이름 두 번은 `bind_invalid`.
- 값 = 글·숫자·참거짓·null. 바인드 창 글로 바꾼다(숫자는 JSON 글자, 참거짓 `TRUE`/`FALSE`, null = 빈 글 = NULL). 줄이 있으면 **그 줄의 형식·방향을 따른다**, 없으면 글 = VARCHAR2, 숫자 = NUMBER, 참거짓 = BOOLEAN 줄을 만든다(`query` 의 `binds` 와 같은 규칙). 사람이 칸을 떠날 때와 같이 `BindValueFormatter.Normalize`(DATE `20261101` → `2026-11-01 00:00:00`, NUMBER 쉼표 제거) 뒤 `Validate`·`BindInput.ToBindValue` 로 검사한다. 맞지 않으면 `bind_invalid`. OUT·REF CURSOR 줄에 준 값도 `bind_invalid`. 객체·배열 값은 `invalid_arguments`.
- 하나라도 틀리면 아무것도 넣지 않는다. 거부 글에 값은 싣지 않는다(이름·형식만).
- `run_tab` 순서: 관문 → `stale` → **값 검사**(`bind_invalid` 는 접속·바쁨보다 먼저) → 접속·바쁨·문장 고르기·문장 가드 → **목록에 넣기** → `ExportBinds.Resolve`(실행 값은 언제나 목록에서) → 실행. 그래서 거부된 실행은 목록을 바꾸지 않는다.
- 결과의 `binds`: 탭 글을 읽을 수 있는 허가면 `get_binds` 와 같은 전체 목록, 읽을 수 없는 허가(운영·클라우드 모델)면 보낸 이름만(값 없음). 값은 `CanReadBindValues`(행 값 수준)일 때만, 비밀 이름 `***`.
- 사람이 보는 것: 바인드 창의 값(그 탭 목록 — 작업 공간·.sql 바인드 블록에 저장됨), `set_binds` 뒤 탭 띠 「… 가 바인드 값 N개를 채웠습니다」, 메시지 창 「[에이전트] … 바인드 값을 채웠습니다 · :NAME」(이름만).
- 감사·이력: 게이트의 인자 요약이 `binds={…}`(값 100자까지)를 남긴다 — `query --binds-json` 과 같다. 실행 이력의 바인드 값은 `run_tab` 이 목록 값으로 실행하므로 사람 실행과 같은 규칙(`SaveBindValues` 설정·비밀 이름 `***`)을 따른다.
- 그대로인 것: 실행 정책·SELECT 가드·운영 가림(`get_binds` 가림 포함)·`query` 의 `binds`.

### 9.3 오류 코드

| 코드 | 종료 | 뜻 |
|---|---|---|
| `bind_invalid` | 2 | `binds` 가 탭 바인드 목록과 맞지 않음: 모르는 이름, 목록 형식에 맞지 않는 값, OUT·REF CURSOR, 같은 이름 두 번. 아무것도 넣지 않음 |

- 상수 = `AgentErrorCodes.BindInvalid`(`All` 에 있음). 종료 2 인 까닭: `path_invalid` 처럼 에이전트가 보낸 값이 틀렸고 고쳐 다시 부르면 된다(허가 거부가 아니다).
- `run_tab` 에 `binds` 없이 목록에도 값이 없으면 기존처럼 `invalid_arguments`(글이 `--binds-json` 을 알려 준다).

### 9.4 결정 ①~③ (2026-11-24, 사용자 「권장대로」)

- ① 형식 지정: 값 자리에 `{"value": …, "type": "varchar2|number|date|timestamp"}`(대소문자 무시). 줄이 없거나 「형식을 아직 고르지 않은 줄」(값이 비고 형식이 VARCHAR2 그대로 — 실행이 처음 보는 이름으로 만든 줄)이면 그 형식으로 만들거나 바꾼다. 사람이 다른 형식을 골라 둔 줄이면 `bind_invalid`(말없이 바꾸지 않음). 객체에 다른 칸이 있거나 형식이 넷 밖이면 `invalid_arguments`. 형식이 없으면 지금 규칙(글 VARCHAR2·숫자 NUMBER). 스킬은 날짜를 `type: date` 로 보내라고 적는다. 스키마 설명(`AppTools.BindsArg`)에도 적었다.
- ② 감사: 게이트 인자 요약(`AgentGate.SummarizeArguments`)이 운영 접속의 `binds` 를 값 없이 `{이름:형식}` 으로 적는다(`binds={PAK_PST_DD:date}`, 형식 = 준 type 또는 값의 꼴 varchar2·number·boolean·null). 외부 앱 도구(`set_binds`·`run_tab`)는 감사 접속이 운영이거나 **접속을 모를 때**(없는 탭·허가 밖 탭 — 운영일 수 있다) 이름만. `query --binds-json`(Db 도구)도 운영 세션이면 같은 규칙(같은 함수). 개발·시험은 예전대로 값까지(100자).
- ③ 이름 노출: `set_binds` 와 `binds` 가 있는 `run_tab` 은 탭 글을 읽을 수 없는 허가(`GrantTabAccess.CanReadText` 거짓)면 `get_tab` 과 같은 거부(`GrantTabAccess.Refusal`: 운영+클라우드 `environment`, 끔 `level`, 접속 없는 탭을 막은 허가 `not_allowed`)를 이름 검사보다 먼저 돌려준다(`ExternalBindFill.RefuseUnreadable`). 있는 이름·없는 이름이 같은 답이라 `bind_invalid` 로 바인드 이름을 더듬을 수 없다. `binds` 없는 `run_tab` 은 그대로.
- 9.2 의 「읽을 수 없는 허가면 보낸 이름만 돌려준다」 는 ③ 으로 없어졌다(그 허가는 거부된다).
