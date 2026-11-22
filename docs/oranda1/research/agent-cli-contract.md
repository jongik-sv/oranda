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
| `get_source` | Db | R | Metadata | `object` | `owner, name, type, text, members[]` |
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
| 2 | 사용법 | `usage` `unknown_tool` `invalid_arguments` |
| 3 | 앱에 닿지 못함·연결 끝남 | `oranda_not_running` `no_endpoint` `oranda_run_invalid` `grant_expired` `run_revoked` `run_required` `hello_required` `oranda_shutting_down` `unsupported_protocol` `bridge_error` |
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
