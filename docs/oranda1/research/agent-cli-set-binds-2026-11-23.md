# 외부 에이전트 — 탭 바인드 값 채우기(`set_binds`·`tab run --binds-json`) 회차 보고

- 작성일: 2026-11-23
- 가지: `dev-jji-win144`(시작 = `origin/main` `04a6ad5a` "docs(release): 0.1.32 notes"). 푸시·릴리스 없음.
- 계약: [agent-cli-contract.md](agent-cli-contract.md) §9. 설계서 [agent-cli-control-2026-11-22.md](agent-cli-control-2026-11-22.md) §5.3 「이번에 두지 않는 도구」의 `set_binds` 줄을 이 회차로 고쳤다.

## 1. 문제

외부 에이전트(Claude Code + oranda 스킬)가 바인드 하나(YYYYMMDD 날짜 글)가 있는 SQL 로 탭을 만들었다. `sql check` 는 지났지만 `tab run` 이 「탭 바인드 목록에 값이 없다」로 막혔다.

- `orandactl` 은 바인드를 읽기만 했다(`tab binds` = `get_binds`).
- `query --binds-json` 은 값을 받지만 결과가 탭 그리드에 보이지 않는다(에이전트에게 행이 가는 길).
- 그래서 에이전트가 사용자에게 「바인드 변수 창에 값을 넣어 달라」고 부탁해야 했다.

## 2. 한 일

| 갈래 | 내용 | 파일 |
|---|---|---|
| 계약 | 새 도구 `set_binds`(App, 등급 X = `run_tab` 과 같음, 정의 최소 수준 Off·처리자 판단). `run_tab` 에 `binds` 인자. 두 도구의 `binds` 스키마 글은 같다. `AppToolValues.BindStatus`(`set`·`stale`). 새 오류 코드 `bind_invalid`(종료 2). `AppTools.All` 29 · `External` 41 | `src/Oranda.Core/Ai/Schemas/AppTools.cs`, `src/Oranda.Core/Ai/Protocol/AgentErrorCodes.cs` |
| CLI | `tab binds set --tab ID --binds-json J [--base-version N]` → `set_binds`. `tab run … [--binds-json J]` → `run_tab` 의 `binds`. `tab binds --tab ID`(읽기)는 그대로. `--binds-json` 옵션 정의는 `query` 와 한 개를 함께 쓴다 | `src/Oranda.Agent.Cli/CommandTable.cs` |
| 채우기 | `ExternalBindFill`: 검사(`Plan`)와 넣기(`Apply`)를 나눴다. 이름·형식·값 규칙은 계약 §9.2 | `src/Oranda.App/Features/Ai/External/Run/ExternalBindFill.cs` |
| 처리자 | `SetBindsHandler`(새), `RunTabHandler`(binds 받음) | `src/Oranda.App/Features/Ai/External/Run/SetBindsHandler.cs`, `RunTabHandler.cs` |
| 모양 재사용 | `get_binds` 의 이름 모으기·가림·JSON 을 `ExternalEditorService.KnownBindNames`·`DescribeBinds`, `GetBindsToolHandler.BindsJson` 로 꺼내 `set_binds` 결과가 같은 모양·같은 가림을 쓴다(동작 불변) | `Editor/ExternalEditorService.cs`, `Editor/EditorToolHandlers.cs` |
| 글 | 띠·메시지 창 글(한/영), 거부 글(영어). `MissingBinds` 글이 이제 「사용자에게 부탁」이 아니라 `--binds-json` 을 알려 준다 | `Run/ExternalRunText.cs` |
| 스킬 | 명령 표에 `tab binds set`·`tab run --binds-json`, 「탭 실행」 절에 바인드 줄: 값은 에이전트가 넘기고 바인드 창에 치라고 하지 않는다, 애매한 값(날짜·기간·코드·형식)만 사용자에게 확인, `bind_invalid`, 셸 따옴표가 깨지면 `printf … \| orandactl call set_binds -` | `src/Oranda.Platform/Ai/Skills/OrandaSkillCommands.cs`, `OrandaSkillGuides.cs` |
| 매뉴얼 | 「탭 실행과 파일 저장」에 바인드 값 넘기기·형식 정리·거부·허가·감사 | `site/src/content/docs/ai/external-agents.md` |
| 하니스 | FakeHost 실제 모드에 `SetBindsHandler`, `set_binds` 사건 기록, 시나리오 `bind-run` ⑨ | `tools/agent-e2e/FakeHost/RealRun.cs`, `RealEditor.cs`, `tools/agent-e2e/scenarios/bind-run/`, `README.md` |

## 3. 동작 (기본 설계 그대로, 정한 것)

1. **`set_binds`**: 탭 「바인드 변수」 목록에 값을 넣는다. 실행하지 않는다. 사람이 그 창에서 보고, 작업 공간·.sql 바인드 블록에 사람이 친 값처럼 저장된다.
2. **`tab run --binds-json`**: 같은 길로 먼저 채우고 실행한다. 실행 값은 언제나 목록에서 만든다(`ExportBinds.Resolve`) — 사람 F5 와 같은 값·같은 이력.
3. **허가**: 관문은 `run_tab` 과 같은 `ExternalRunSupport.Resolve`(잠긴 탭 `locked`, 수준 < Query `level`, 실행 못 하는 환경 `environment`, 접속 없는 탭 `connection_required`). `base_version` 이 다르면 넣지 않고 `stale`(종료 0). 실행 정책·SELECT 가드·운영 가림은 건드리지 않았다.
4. **형식**: 줄이 있으면 그 줄의 형식·방향을 따른다. 사람이 칸을 떠날 때와 같은 정리(`BindValueFormatter.Normalize`: DATE `20261101` → `2026-11-01 00:00:00`, NUMBER `1,000` → `1000`) 뒤 `Validate`·`ToBindValue` 로 검사. 줄이 없으면 글 = VARCHAR2, 숫자 = NUMBER, 참거짓 = BOOLEAN(`query` 의 `binds` 와 같은 규칙).
5. **거부**: 형식에 맞지 않는 값·**모르는 이름(거부로 정함 — 오타가 조용히 버려지면 실행이 막히는 까닭을 에이전트가 모른다)**·OUT/REF CURSOR·같은 이름 두 번 = `bind_invalid`(종료 2). 하나라도 틀리면 아무것도 넣지 않는다. 거부 글에 값은 싣지 않는다.
6. **`run_tab` 순서**: 관문 → `stale` → 값 검사 → 접속·바쁨·문장·가드 → 목록에 넣기 → 실행. 거부된 실행(가드 거부 등)은 목록을 바꾸지 않는다.
7. **감사·이력**: 게이트의 인자 요약이 `binds={…}`(값 100자)를 남긴다 — `query --binds-json` 과 같다(새 배선 없음, 스키마에 `binds` 를 둔 것으로 같은 요약을 탄다). 실행 이력은 사람 실행과 같은 규칙(`SaveBindValues`, 비밀 이름 `***`).
8. **사람이 보는 것**: 바인드 창의 값, `set_binds` 뒤 탭 띠 「Claude Code 가 바인드 값 N개를 채웠습니다」, 메시지 창 「[에이전트] … 바인드 값을 채웠습니다 · :BASE_DT」(이름만).

## 4. 설계에서 벗어난 것

| 항목 | 기본 설계 | 한 것 | 까닭 |
|---|---|---|---|
| `set_binds` 결과 모양 | `get_binds` 와 같은 모양 | `get_binds` 의 `binds` 배열(같은 모양·같은 가림) + `status`(`set`\|`stale`)·`version` | `base_version` 낡음은 이 계약에서 오류가 아니라 상태 글(종료 0, `run_tab`·`propose_edit`)이라 `stale` 을 돌려줄 칸이 필요했다. `binds` 칸은 그대로라 `get_binds` 를 읽는 코드가 그대로 읽는다 |
| 낡은 판 「거부」 | 거부 | `stale` 상태(종료 0), 아무것도 넣지 않음 | 위와 같다. 오류 코드로 하면 `run_tab` 의 낡음(0)과 종료 코드가 갈린다 |
| 운영·클라우드 허가의 결과 `binds` | — | 보낸 이름만(값 없음) | 그 허가는 탭 글을 읽을 수 없다(`get_binds` 거부). 전체 목록을 주면 글이 참조하는 다른 바인드 이름이 새어 나간다 |

## 5. 시험

| 프로젝트 | 범위 | 결과 |
|---|---|---|
| `Oranda.Core.Tests` | 전체 | 통과 3,548 · 실패 0 · 건너뜀 0 |
| `Oranda.Agent.Cli.Tests` | 전체 | 통과 187 · 실패 0 |
| `Oranda.Platform.Tests` | 전체 | 통과 1,196 · 실패 0 · 건너뜀 36 |
| `Oranda.Language.Tests` | 전체 | 통과 1,550 · 실패 0 |
| `Oranda.Architecture.Tests` | 전체 | 통과 31 · 실패 0 |
| `Oranda.App.Tests` | 전체 | 통과 7,364 · 실패 0 · 건너뜀 69(실제 DB 통합 시험 등 기존 건너뜀) |
| agent-e2e selftest | `REAL=1 selftest.sh bind-run`(LLM 없음) | PASS — 값 없이 실행 `invalid_arguments` 2 → 모르는 이름 `bind_invalid` 2 → `--binds-json` 실행 succeeded(3행) → `tab binds` 에 값 |

새 시험:

- `tests/Oranda.App.Tests/Features/Ai/AgentCli/AgentCliBindsRoundTripTests.cs` — 실제 `orandactl` → 실제 다리·관문·허가 → 실제 `get_binds`·`set_binds`·`run_tab` 처리자 → 실제 바인드 목록·실행 경로(엔진·세션·가드만 가짜). 읽기 → 값 없이 실행(종료 2) → NUMBER 줄에 글(`bind_invalid` 2, 아무것도 안 바뀜) → 모르는 이름(2) → 잠긴 탭(`locked` 4) → 낡은 판(`stale` 0, 안 넣음) → `tab binds set`(get_binds 모양, 목록에 값) → `tab run --binds-json`(succeeded, 출처 Agent, 실행 값 = 목록 값, NUMBER 정리) → 감사 줄 `binds={…}`·탭 접속.
- `tests/Oranda.App.Tests/Features/Ai/External/SetBindsHandlerTests.cs` — 처리자 9개: 새 줄·모양·띠·메시지(값 없음), 줄 형식 따르기(DATE·NUMBER·새 NUMBER 줄), 형식 불일치(`bind_invalid`, 앞의 맞는 값도 안 넣음, 값 글 안 실음), 모르는 이름·두 번·OUT·객체 값, 잠김·낡음·수준·접속 없음, Query 수준·운영 클라우드 가림, `run_tab` + binds 실행 값, binds 없는 `run_tab` 글, 틀린 값·낡음·가드 거부·잠김에서 안 넣음.
- 계약·CLI·스킬: `AppToolContract2Tests`(set_binds 등급 = run_tab, 스키마, `bind_invalid` → 2, 순서), `AppToolContractTests`(29·41), `CommandParserTests`(tab binds / tab binds set / tab run --binds-json, 틀린 JSON = usage), `CommandTableTests`(41), `OrandaSkillDocumentTests`(명령 머리·바인드 줄 글).

먼저 있던 실패: 없음(위 6개 프로젝트 모두 실패 0). 실제 DB(Oracle·Tibero) 통합 시험 프로젝트는 돌리지 않았다(운영 DB 접속 금지, 이번 변경은 가짜 엔진으로 덮음).

## 6. 열린 것

1. **형식을 에이전트가 정하는 길이 없다.** 줄이 없는 이름은 글 → VARCHAR2 다. DATE 열을 `:D` 로 비교하는 SQL 에 `"20261101"` 을 주면 VARCHAR2 로 들어가 세션 NLS 로 암묵 변환된다(이번 사례처럼 SQL 이 `TO_DATE(:D,'YYYYMMDD')`·`TO_CHAR(…) = :D` 면 문제없다). `{"D": {"value": "…", "type": "date"}}` 같은 꼴을 더할지는 다음 회차 판단(지금은 객체 값 = `invalid_arguments`).
2. **감사의 바인드 값**: `query` 와 같게 운영 접속에서도 `binds={…}` 가 감사 줄(로컬 저장소)에 값 그대로 남는다. 에이전트가 보낸 값이라 LLM 에 새로 드러나는 것은 없지만, 운영 탭 감사에서 리터럴을 빼던 방침(`run_tab` 감사에 문장 리터럴 없음)과 맞출지 정해야 한다.
3. **모르는 이름 거부가 이름의 있고 없음을 알린다**: 운영·클라우드 허가(탭 글을 못 읽음)에서 `bind_invalid` 로 그 이름이 탭에 없다는 것은 알 수 있다. 값·글은 드러나지 않는다.
4. **실제 LLM 왕복**: 하니스 `bind-run` 은 selftest(LLM 없음)만 돌렸다. `run.sh claude …` 로 스킬 글만 보고 `--binds-json` 을 고르고 날짜만 확인하는지 재는 일은 남았다(설치된 스킬 사본은 앱의 「스킬 설치…」로 다시 써야 새 글이 들어간다).
5. **Windows 하니스**: `verdict.py` 가 cp949 콘솔에서 한글 답을 찍다 죽는다(`PYTHONIOENCODING=utf-8` 로 돌림). 하니스는 손대지 않았다.

## 7. 뒤 이야기 (2026-11-24)

- 열린 것 1~3(형식 지정·운영 감사의 바인드 값·이름의 있고 없음)은 사용자가 「권장대로」 정했고 반영했다: [ai-settings-one-place-2026-11-24.md](ai-settings-one-place-2026-11-24.md) 8절, 계약 §9.4.
- 외부 에이전트 연결 창의 「조회 허용」 은 없어졌다(수준은 설정 「AI」 탭 ① 한곳). 그래서 3절의 「조회 허용으로 운영을 올리면 null 로 저장」 이야기는 더 이상 해당하지 않는다.
