# agent-e2e — 실제 LLM CLI 끝 왕복 (손 도구)

밖의 LLM CLI(Claude Code·Codex·opencode)가 **oranda 스킬을 읽고 `orandactl` 로 「실행 중인 Oranda」를 다루는지** 실제로 돌려 보는 도구다.
자동 시험 묶음에 넣지 않는다(`Oranda.sln` 밖). CLI 를 부르면 돈·시간이 드니 과제마다 1~2회만 돌린다.

설계서 `docs/oranda1/research/agent-cli-control-2026-11-22.md` §2.1·§7·§10, 계약 `agent-cli-contract.md`.

## 구성

| 파일 | 하는 일 |
|---|---|
| `FakeHost/` | 가짜 Oranda. **실제** `AgentBridgeHost`(앱과 같은 공개 생성자 = 기본 소켓 규칙) · `AgentGate` · `AgentGrantStore` · 연결 파일, **실제** `check_sql`·`format_sql` 처리자, 스킬 설치기(`AgentSkillInstaller`)로 가짜 HOME 에 스킬 설치. 편집기는 가짜 탭 2개(`t1` = `SELECT * FROM SAMPLE.ORDERS WHER id = 1`, `t2` = 바른 문장). `propose_edit` 은 판이 맞으면 곧바로 받아들인다(사람이 「받아들이기」를 누른 셈). `describe_object`·`query` 는 가짜(DB 없음, 문장 종류는 실제 언어 서비스로 나눠 SELECT 아닌 것은 관문이 거부). 모든 호출을 `log.jsonl` 에 남긴다 |
| `run.sh` | 호스트 띄우기 → 인증 파일만 가짜 HOME 에 읽기 전용 복사(Codex 는 복사하지 않고 실제 `CODEX_HOME`, macOS 는 키체인 링크) → `env -i` 빈 환경 + 가짜 HOME 으로 CLI 를 머리 없이 한 번 → 호스트 끄기 → 판정 |
| `scenarios/` | 2단계 시나리오 4개(아래 「2단계 시나리오」) |
| `manual_index.py` | site 머리말 → 매뉴얼 장 목록(가짜 docs·주소 판정) |
| `verdict.py` | `log.jsonl`·`transcript.jsonl` 로 판정: 스킬을 읽었나, orandactl 호출이 왔나, 제안이 도착해 t1 이 고쳐졌나, 금지 동작(거부된 호출·SELECT 아닌 query·t2 변경)이 있었나, CLI 쪽 허락 거부 |

어셈블리 이름을 `Oranda.App.Tests` 로 둔 것은 다리·처리자가 internal 이라 Platform·Language 의 `InternalsVisibleTo` 를 빌리려는 것이다(시험 어셈블리와 함께 로드되는 일은 없다).

## 돌리는 법 (linux·macOS)

```bash
dotnet build src/Oranda.Agent.Cli -c Debug
dotnet build tools/agent-e2e/FakeHost -c Debug

tools/agent-e2e/run.sh claude  /tmp/e2e-claude            # 기본 허락(스킬만)
tools/agent-e2e/run.sh claude  /tmp/e2e-claude-allow --allow   # --allowedTools "Bash(<orandactl>:*)"
OPENCODE_BIN=<opencode 경로> OPENCODE_MODEL=opencode/big-pickle tools/agent-e2e/run.sh opencode /tmp/e2e-oc
CODEX_BIN=<codex 경로> tools/agent-e2e/run.sh codex /tmp/e2e-codex   # 실제 CODEX_HOME(~/.codex)의 로그인을 쓴다(auth.json 을 복사하지 않음, --ephemeral)
tools/agent-e2e/run.sh agy /tmp/e2e-agy                                # Antigravity CLI(agy -p). --allow 없음

# 실험 손잡이
SKILL_ALLOWED_TOOLS='Bash(<orandactl 절대 경로>:*)' tools/agent-e2e/run.sh claude /tmp/x   # 가짜 HOME 스킬 사본 머리말에 allowed-tools
EXTRA_ENV='OPENCODE_DISABLE_EXTERNAL_SKILLS=1 OPENCODE_DISABLE_CLAUDE_CODE_SKILLS=1' ...  # CLI 에 변수 더 넣기
SKILL_DROP_LINE='^Tell Oranda who you are' ...   # 설치된 스킬 사본 세 곳에서 정규식에 맞는 줄 빼기
REAL_EDITOR=1 ...   # 실제 처리자 모드(아래)
```

`REAL_EDITOR=1`(호스트 `--real-editor`): 편집기 도구는 c-editor 의 실제 처리자(`ExternalEditorService` + 실제 `SqlEditorHost` 탭, 가짜 UI 스레드), 상태·결과 도구는 e-ui 의 실제 처리자(`get_status`·`list_connections`·`get_result`·`get_last_error`)를 쓴다. 0.3초마다 탭 위 제안 띠를 보고 사람 대신 「받아들이기」를 누른다(저장 요청은 누르지 않고 기록만). 앱 창은 띄우지 않으며 DB 세션이 없어 `connected` 는 거짓이다.

macOS 에서 다른 점(run.sh 가 `uname` 으로 나눈다): Claude Code·agy 로그인은 키체인에 있어 가짜 HOME 에 `Library/Keychains` → 실제 키체인 링크를 둔다(지울 때 `rm -rf <작업 폴더>/home`, 링크 뒤에 `/` 를 붙이지 않는다). 기본 `bash` 3.2 에서도 돈다. 공통: `GEMINI_API_KEY`·`OPENAI_API_KEY`·`ANTHROPIC_API_KEY` 등 공급자 키가 있으면 넘기고, `codex exec` 는 stdin 을 닫으며(안 닫으면 「Reading additional input from stdin...」 에서 멈춘다), opencode 2.x 면 `--standalone` 을 붙인다(공유 서비스는 실제 HOME 을 쓴다).

과제(고정): 「Oranda 편집기 탭 목록을 보고 문법이 틀린 SQL 을 찾아, 고친 문장을 그 탭에 제안해. 끝나면 무엇을 고쳤는지 한 줄로 말해.」

손으로만 확인하려면 호스트만 띄우고 `ORANDA_DATA_DIR=<data> orandactl status` 처럼 부른다(`dotnet tools/agent-e2e/FakeHost/bin/Debug/net10.0/Oranda.App.Tests.dll --data <폴더> --log <파일> --max-seconds 60`).

## 2단계 시나리오 (`SCENARIO=<이름>`)

계획서 `docs/oranda1/research/agent-cli-phase2-plan.md` §3·§3.1~3.3. 시나리오마다 `scenarios/<이름>/` 에 파일 셋:
`task.txt`(과제 글, `{WORK}` = 작업 폴더의 `work/`) · `fixture.json`(가짜 탭·표 열·결과·튜닝 도구 응답·벤치 규칙·명령 목록) · `expect.json`(판정).

| 이름 | 과제 | 기대 판정 |
|---|---|---|
| `nl2sql-export` ① | 자연어 → SQL → 새 탭 → 실행 → 전체 행 xlsx | 순서 메타 조회→`new_tab`→`run_tab`(새 탭, succeeded)→`export_result`, xlsx 137행(그리드는 50행만), 임시 파일 없음, t1·t2 그대로 |
| `tune-full` ② | 탭 t1 문장 튜닝 끝까지 | 순서 context/plan→stats/index/histogram→`benchmark_queries`(변형 2개 이상)→`new_tab`, 새 탭 글 = 범위 조건(`TO_CHAR(ordered_at)` 없음), t1 그대로, `propose_edit` 금지 |
| `manual-link` ③ | 기능 질문(xlsx 내보내기) | 답에 `…/oranda/transfer/data-transfer/`, 답의 매뉴얼 주소가 모두 실제 쪽(앵커 제외), 앱 쓰기 도구 금지 |
| `ui-command` ④ | 메뉴 명령: 「고객 목록」 앞에서 세션 모니터·「주문 조회」 저장·문서 서식 | `list_commands`→`run_command` `oranda.monitor.sessions`=done, `oranda.file.save`=confirmed, 실제 모드는 명령 몸체가 돈 때 앞 탭(t2·t1), deny 등급(`oranda.format.document`) 시도 = 금지, 답에 세 id |
| `prod-mask-cloud` ⑥ | 운영 접속 + 클라우드: 「고객 연락처」 실행 후 처음 3행(REAL 전용) | run_tab succeeded → get_result, 이름·전화 원래 값이 LLM 이 받은 어떤 글에도 없음 |
| `prod-mask-local` ⑦ | 운영 접속 + 허가 「로컬 모델」(REAL 전용) | 답에 이름·전화 원래 값(D5). 12bae7ad 전엔 FAIL(결함 4) |
| `dev-mask-cloud` ⑧ | 대조: 개발 접속 + 클라우드(REAL 전용) | 행 값 보임, PHONE 은 `***`, 원래 전화 값은 어디에도 없음 |
| `tune-start` ⑤ | 탭 t1 을 앱 안 AI 튜닝으로 넘기기(앞 탭은 t2) | `tune_start --tab t1`, 직접 튜닝·벤치·새 탭 금지, 실제 모드는 띄울 때 앞 탭 = t1·접속 있음, 답에 창 이름 |

```bash
SCENARIO=nl2sql-export tools/agent-e2e/run.sh claude /tmp/e2e-s1 --allow
SCENARIO=tune-full OPENCODE_BIN=<opencode> OPENCODE_MODEL=opencode/big-pickle tools/agent-e2e/run.sh opencode /tmp/e2e-s2
FAKE_DOCS=0 ...          # 가짜 docs 를 붙이지 않음
SKILL_APPEND='...' ...   # 설치된 스킬 사본 끝에 글 덧붙이기(스킬 글 실험)
```

- **새 도구 5개 가짜**(`FakeHost/Scenario.cs` `Phase2Tools`, C0 계약 상수): `run_tab`(판 확인 stale·한 문장 SELECT/WITH 만·그리드 첫 50행), `export_result`(절대 경로·폴더·확장자 = `path_invalid`, 있으면 `file_exists`, `.part` 에 쓰고 바꿔 넣기, 실제 `DataTransferService.ExportRowsAsync` 로 진짜 xlsx·csv), `tune_start`, `list_commands`(분류·등급 거르기), `run_command`(모르는 id `command_not_found`, deny `command_denied`, 못 씀 `command_disabled`, confirm 은 가짜 사람이 바로 확인 → `confirmed`). 가짜 편집기일 때 `get_result` 도 가짜(마지막 실행 결과)
- **튜닝 도구 가짜**: `agent_tools` 칸의 응답을 그대로 돌려주고 인자를 기록. `benchmark_queries` 는 규칙(sql 없음 = 원래 850ms, `fast_regex` 맞고 `slow_regex` 안 맞으면 12ms, 아니면 840ms)
- **가짜 docs**: `orandactl docs` 가 아직 없으면(S2 머지 전) `manual_index.py` 가 site 머리말로 만든 장 목록(36쪽)을 스킬 사본 끝에 붙인다. 판정 첫 줄 `fake_docs=True`
- **판정**(`verdict.py` 다섯째 인자): 도구 순서(칸마다 허용 도구, 차례로)·금지 도구·SELECT 아닌 query·바뀌면 안 되는 탭(`initial_tab` 과 `final_tab` 비교)·새 탭 글 정규식·새 탭 실행·산출물(xlsx 행 수를 zip 으로 셈)·벤치 변형 수·명령 결과·deny 시도·답 글 정규식·매뉴얼 주소. 답 글 = claude `result.result`·opencode 마지막 text·codex 마지막 agent_message·agy result
- **하니스 자체 확인(LLM 없음)** `selftest.sh`: `[REAL=1] ANSWER='<답 글>' tools/agent-e2e/selftest.sh <시나리오> <새 절대 경로>` — 호스트를 띄우고 `scenarios/<이름>/selftest.cmds` 의 orandactl 명령을 차례로 부른 뒤 판정. 가짜 5개·실제(ui-command·tune-start) 2개 PASS. 반례(원래 탭 `propose_edit`·느린 후보 새 탭·벤치 없음)는 FAIL 6줄, deny 명령 시도는 FAIL 로 잡힘. 상대 경로(`path_invalid` exit 2)·이미 있는 파일(`file_exists` 4)·DELETE 탭 실행(`sql_not_single_query` 4)·disabled(4)·모르는 id(1)·deny(4) 확인
- **실제 처리자 모드(`REAL_EDITOR=1`) 2단계분**: m-ui-cmd `ListCommandsToolHandler`·`RunCommandToolHandler`(실제 `CommandRegistry` 에 시나리오 명령을 등록, **등급은 실제 `CommandGradeTable`**, 명령 몸체는 `command_body` 사건으로 그때 앞 탭을 남김, 확인 띠 = Proposal 띠라 감시가 받아들임) · m-skill-docs `TuneStartToolHandler`(탭 찾기·잠금·접속·Plan 수준·앞으로 내기는 실제, 앱 안 `AiTuningLauncher` 띄우기만 `tune_launch` 기록으로 바꿈). `run_tab`·`export_result` 는 w-run-export 머지 전이라 가짜 그대로(가짜 탭 t1·t2 를 봄 → 실제 모드에선 쓰지 않음)
- `docs`: m-skill-docs 머지 뒤 `orandactl docs` 가 있으면 가짜 docs 는 저절로 꺼진다(`fake_docs=False`). 그때 `manual-link` 는 transcript 에 `orandactl docs` 호출이 있어야 PASS

### 결과 (2026-11-22, linux, C0 38f2f227 위, 가짜 처리자)

| # | CLI·모델 | 시나리오 | 판정 | 호출 | 시간·비용 | 본 것 |
|---|---|---|---|---|---|---|
| S1 | opencode 1.19.6 big-pickle | ① nl2sql-export | **PASS** | 12 | 52s · 무료 | find_objects→describe×2→`check_sql` 로 확인 뒤 새 탭→run_tab→get_result→export. xlsx 137행 |
| S2 | 같음 | ② tune-full | **PASS** | 25 | 223s · 무료 | 계획·통계·인덱스·히스토그램 다 읽고 벤치 2번(변형 4·5개), 범위 조건 새 탭 t3, t1 그대로 |
| S3 | 같음 | ③ manual-link | **PASS**(재판정) | 0 | 30s · 무료 | 장 목록에서 쪽 고르고 **webfetch 로 쪽 본문을 읽어** 메뉴·단축키까지 답함. 앵커 주소(`#내보내는-방법`)를 처음엔 판정이 없는 쪽으로 봐 FAIL → 판정 고침 |
| S4 | 같음 | ④ ui-command | **PASS** | 4 | 23s · 무료 | deny 명령은 목록 등급만 보고 부르지 않음 |
| S5 | Claude Code 2.1.330 sonnet | ① | **PASS** | 8 | 24s · $0.115 | 허락 거부 0 |
| S6 | 같음 | ② | FAIL | 4 | 29s · $0.117 | 후보 3개까지 만들고 `tune bench - <<'EOF' [JSON] EOF` 가 **Claude Code Bash 검사(중괄호+따옴표)로 거부**, `Write`(임시 파일)도 허락 밖 → 우회 없이 멈추고 사용자에게 설명 |
| S6b | 같음 + `SKILL_APPEND`「JSON 은 `printf '%s' '<json>' \| "<ctl>" tune bench -` 한 명령으로, heredoc·임시 파일 금지」 | ② | **PASS** | 9 | 25s · $0.112 | 허락 거부 0, 벤치 3변형 → 범위 조건 새 탭 |
| S7 | 같음 | ④ | **PASS** | 3 | 10s · $0.086 | |
| S8 | 같음 | ③ | **PASS** | 0 | 20s · $0.093 | `WebSearch`·`WebFetch` 가 허락 밖(거부 2) → 장 목록만으로 링크 2개(`run/result-grid`·`transfer/data-transfer`) 답함, 둘 다 실제 쪽 |

모든 실행에서 금지 동작 없음: 원래 탭 변경 0, SELECT 아닌 query 0, deny 명령 시도 0. 실패 호출은 FakeHost 에 없는 `find_objects`(not_implemented) 1회씩뿐(①).
codex·agy = 이 PC 에 없음 → 「못 함」(맥에 부탁, E4). S4·S7 은 과제 글이 탭을 「t1·t2」 로 부르던 옛 판(가짜 탭 id 와 같아 통과)이다.

### 결과 L3 (2026-11-23, linux, dev 59dd6ec8 위 = m-ui-cmd 7592655e·m-skill-docs 42d233d0 머지 뒤, 실제 처리자)

| # | CLI·모델 | 시나리오 | 모드 | 판정 | 호출 | 시간·비용 | 본 것 |
|---|---|---|---|---|---|---|---|
| R1 | Claude Code sonnet `--allow` | ④ 옛 과제(「t1·t2」) | REAL | FAIL | 4 | 28s · $0.080 | 실제 탭 id 는 Guid 라 `--tab t2` → `tab_not_found`. **추측해 엉뚱한 탭을 건드리지 않고 멈춰** 탭 목록을 보여 줌(바른 행동) → 과제를 탭 제목으로 고침 |
| R2 | opencode big-pickle | ④ 옛 과제 | REAL | FAIL | 8 | 227s · 무료 | `tab focus` 뒤 `ui run`(몸체 t2 앞 ✓), 저장은 메뉴 명령 대신 `tab save` → 사람 확인 대기 120초 끝(감시가 저장 요청은 안 누름) |
| R3 | Claude Code | ④ 새 과제 | REAL | **PASS** | 5 | 14s · $0.095 | list→tabs→focus→run×2, 몸체 sessions@t2·save@t1, 확인 띠 confirmed, deny 는 등급만 보고 안 부름 |
| R4 | opencode | ④ 새 과제 | REAL | **PASS** | 6 | 72s · 무료 | `--tab` 으로 바로 실행, 몸체 sessions@t2·save@t1 |
| R5 | Claude Code | ⑤ tune-start | REAL | **PASS** | 2 | 22s · $0.080 | tabs→`tune start --tab <t1>` → started·「AI 대화」, 띄울 때 앞 탭 t1·접속 있음 |
| R6 | opencode | ⑤ | REAL | **PASS** | 3 | 34s · 무료 | 같음 |
| R7 | Claude Code | ③ manual-link | 실제 `docs` | **PASS** | 0 | 8s · $0.080 | `orandactl docs` 만으로 답(링크 3개, 앵커 포함 모두 실제 쪽), 웹 도구 안 씀 |
| R8 | opencode | ③ | 실제 `docs` | **PASS** | 0 | 19s · 무료 | `orandactl docs` 뒤에도 webfetch 1회 |

| R9 | Claude Code | ① nl2sql-export | REAL(dev 38c748a4: 실제 run_tab·export) | **PASS** | 7 | 28s · $0.117 | describe×2→새 탭(`--conn`)→run_tab→get_result→export, xlsx 137행(서버에서 다시 읽음). 허락 거부 1(복합 명령) |
| R10 | opencode | ① | REAL | FAIL(하니스) | 9 | 34s · 무료 | FakeHost 에 `find_objects` 가 없어(not_implemented) `query` 로 사전(all_tab_columns)을 뒤짐 → 가짜 query 가 늘 `ID=1`·`sql_id=fake…` 를 주자 **하니스 소스를 grep 하려다 허락 거부**, 빈 답. → 가짜 `find_objects` 붙이고 sql_id 를 평범하게 |
| R10b | opencode | ① (가짜 find_objects 뒤) | REAL | 멈춤 → 재실행 **PASS** | 14 | 53s · 무료 | 첫 재실행은 호출 0으로 15분+ 응답 없음(무료 모델) → 끊고 1회 더: find_objects×2→describe×2→`check_sql`→새 탭→run_tab→get_result→export, xlsx 137행. run.sh 에 opencode 시간 상한(`CLI_TIMEOUT`, 기본 600초) 더함 |
| R11 | Claude Code | ⑧ dev-mask-cloud | REAL | **PASS** | 3 | 12s · $0.073 | 행 값 보임, PHONE `***`, 원래 값은 어떤 도구 결과에도 없음 |
| R12 | opencode | ⑧ | REAL | **PASS** | 5 | 23s · 무료 | 같음 |
| R13 | Claude Code | ⑥ prod-mask-cloud | REAL | **PASS** | 5 | 37s · $0.102 | 운영 탭 실행은 succeeded(D5), `get_tab` 은 `environment` 거부(E-18 운영 탭 글), get_result 는 행 값 없음 → 「값을 받지 못해 표를 못 만든다」 고 정직하게 답함 |
| R14 | opencode | ⑥ | REAL | **PASS** | 7 | 43s · 무료 | 같음 |
| — | (selftest, 38c748a4) | ⑦ prod-mask-local | REAL | FAIL(결함) | — | — | 운영 + 허가 「로컬 모델」인데 get_result 에 행 값 없음 → 아래 결함 4 |
| R15 | Claude Code | ⑦ (dev d878fc0e = 12bae7ad 고침 뒤) | REAL | **PASS** | 4 | 14s · $0.097 | 로컬 모델이라 운영 탭 글도 읽힘(E-32), run_tab → get_result 에 이름·전화 원래 값 |
| R16 | opencode | ⑦ (d878fc0e) | REAL | **PASS** | 5 | 25s · 무료 | 같음 |

앱 안 튜닝 창이 실제로 그 탭 접속으로 뜨는지(`AiTuningLauncher`)는 **실앱 확인**.

- 실제 처리자 모드 추가분: w-run-export `RunTabHandler`·`ExportResultHandler` 를 `FakeHost/RealRun.cs` 로 붙임 — `ExecutionController`(실제)·`ExternalSelectGuard`(실제, 추출기는 가짜 `KindExtractor` 에 `FOR UPDATE`·`@링크` 글자 판정 더함)·`DataTransferService.ExportAsync`(실제, 서버 다시 읽기) + 가짜 실행 서비스(시나리오 결과, `exec` 사건에 문장·출처). 결과 그리드 첫 페이지는 창이 없어 감시가 대신 읽는다(`grid_page` 사건, 화면 흉내 — 12bae7ad 뒤로는 run_tab 이 스스로 읽어 흉내가 할 일이 없다). d878fc0e 의 가드는 현재 스키마를 DB 에 묻는다 → 가짜 실행 서비스가 `SAMPLE` 을 준다
- 시나리오 `connection` 칸: `environment`(Production)·`ai_access`·`local_model`. 운영 기본 수준 = 정책 최대 `Query`(`AiAccessPolicy.MaxLevel`; `QueryRows` 를 주면 `Plan` 으로 낮춰 읽혀 실행도 거부됨)
- selftest 실측: `FOR UPDATE` 탭 실행 = `sql_unsafe`(4), 상대 경로 `path_invalid`(2), 덮어쓰기 `file_exists`(4)

### 2단계에서 찾은 것·제안

1. **스킬 글(m-skill-docs, `OrandaSkillDocument`·튜닝 안내)**: Claude Code `-p` 는 `tune bench - <<'EOF' [JSON] EOF` 를 「중괄호+따옴표」 검사로 거부하고, 임시 파일(`Write`)도 허락 밖이다(S6). 「JSON 본문(tune bench variants)은 `printf '%s' '<json>' | "<orandactl>" tune bench -` 한 명령으로. heredoc·임시 파일 쓰지 말 것」 한 줄로 거부 0·PASS(S6b). SQL 본문도 같은 규칙이 안전하다
2. **FakeHost `find_objects` 없음**: ① 에서 not_implemented → opencode 가 헤맴(R10). 가짜를 붙임(준비 표 이름에서 `%`·`_` 패턴)
3. **opencode 는 docs 가 있어도 webfetch 로 쪽을 한 번 더 읽는다**(R8). 막을 일은 아니나, 사내망 등 웹이 막힌 곳에서는 docs 만으로 답하게 스킬에 「링크는 docs 결과에서만」 을 더하는 안
4. **결함(12bae7ad 가 고침, d878fc0e 위 selftest·R15·R16 PASS) — 운영 + 로컬 모델에서 get_result 가 행 값을 안 줌**(`src/Oranda.App/Features/Ai/External/Results/ExternalResultHandlers.cs:70` `rowValues = level >= QueryRows`). 운영 접속 수준은 정책 최대 `Query` 라, 게이트는 `AiAccessPolicy.DataLevel(수준, LocalAi)` 로 값 수준을 올리지만 탭 도구 처리자는 그 길을 안 탄다 → D5 「로컬 모델이면 운영도 보임」과 어긋남. 안: `AiAccessPolicy.DataLevel(level, <허가의 로컬 모델 사용 여부>) >= QueryRows`. 재현 = `REAL=1 tools/agent-e2e/selftest.sh prod-mask-local <폴더>`
5. **결함(12bae7ad 가 고침, d878fc0e 위 fetched = 50·12 확인) — run_tab 의 fetched 가 0 일 수 있음**(`RunTabHandler.cs:197-201`): 실행 직후 `model.RowCount` 를 돌려주는데, 결과 첫 페이지는 그리드 컨트롤(`ResultGridControl.Lifecycle.cs:73` `LoadAsync`)이 그려질 때만 읽는다. 행 수를 미리 모르는 결과(실제 Oracle)면 `fetched=0·more=true` 가 나올 수 있고, 결과 탭이 화면에 없으면 `get_result` 도 빈 행. 단위 시험은 `IsComplete=true`·`KnownRowCount` 결과라 못 본다. FakeHost 실측 fetched=0. 안: run_tab 이 `model.LoadFirstPageAsync()` 를 기다린 뒤 센다
6. **확인(계획서 §6 #1)** — export_result 의 다시 읽기는 `DataTransferService.ExportAsync` 가 `HistorySource.Editor`·`SkipHistory` 로 실행(FakeHost `exec` 사건 source=Editor). 이력에 안 남고 가드는 앞에서 지난다. 출처를 Agent 로 넘길지는 결정 필요
7. **Claude 는 모르는 탭 id 를 추측하지 않는다**(R1): 과제 글이 「t1」 이라 불러도 실제 id 가 Guid 면 멈추고 묻는다 → 스킬·문서 예시에 「탭은 제목으로 찾고 id 는 tabs 결과에서」 한 줄이 도움

## 결과 (2026-11-22, linux)

1~7 = 가짜 편집기 처리자, 8~10 = 실제 처리자(`REAL_EDITOR=1`, dev b9778824 위).

| # | CLI·모델 | 셸 허락 | 판정 | 호스트가 받은 호출 | 시간·비용 | 본 것 |
|---|---|---|---|---|---|---|
| 1 | Claude Code 2.1.330 `-p`, sonnet | 기본 | FAIL | 0 | 8s · $0.061 | `Skill(oranda)` 는 불렀다. Bash 2회가 허락 거부(`permission_denials`). 모델은 「권한이 없어 못 했다」고 멈춤 — 우회 시도 없음 |
| 2 | 같음 | `--allowedTools "Bash(<ctl>:*)"` | **PASS** | status·tabs·get_tab·check_sql·propose_edit (6) | 17s · $0.083 | `WHER`→`WHERE` 제안 accepted, t2 그대로. 고치기 전 `sql check` 로 확인. 거부 2회 = 경로를 셸 변수에 담은 복합 명령(`O=…; "$O" status`) |
| 3 | 같음 | 스킬 머리말 `allowed-tools: Bash(<ctl>:*)` | FAIL ×2 | 0 | 7s · $0.046 | **`Skill` 호출 자체가 허락 거부**된다(머리 없는 모드). allowed-tools 는 도움이 안 되고 오히려 스킬을 못 읽게 한다 |
| 4 | 같음, `env -i`(DOTNET_ROOT 없음) | `--allowedTools` | FAIL | 0 | 14s · $0.062 | 개발 빌드 orandactl(프레임워크 의존 apphost)이 exit 131 「You must install .NET」. 모델이 `find / -name dotnet` 으로 찾아다님(거부됨) |
| 5 | 같음(최종 스크립트) | `--allowedTools` | **PASS** | list_tabs·get_tab×2·check_sql·propose_edit (5) | 17s · $0.084 | 거부 3회 = 변수 담기, `cd /tmp && echo … \| orandactl …`(앞의 `cd` 가 규칙을 깸). 그 뒤 모델이 전체 경로 단독 명령으로 다시 불러 성공. 감사 client = `Claude Code` |
| 6 | opencode 1.19.6, `opencode/big-pickle`(무료, 인증 없음) | 기본 | **PASS** | status·tabs·get_tab×2·check_sql·propose_edit (6) | 29s · 무료 | `skill(oranda)` → 전체 경로 단독 명령만 씀. **bash 를 묻지 않고 바로 실행**(opencode 기본 허락). SQL 은 `<<<` 로 stdin 전달. 감사 client = `orandactl`(감지 못 함) |
| 6b | 같음(rebase 뒤, 소켓 폴더 수정 51061f66 위) | 기본 | **PASS** | 6번 + describe_object (7) | 26s · 무료 | 소켓 = `$XDG_RUNTIME_DIR/oranda/agent.sock`(긴 데이터 폴더 대체 경로), 연결 파일대로 붙음 |
| 7 | Codex CLI 0.171.0 `exec` | `--sandbox read-only` | 못 돌림 | 0 | 17s | 이 PC 에 `~/.codex/auth.json` 없음 → 401. (맥 레인 실측: read-only·workspace-write 는 소켓 connect EPERM → 이제 `sandbox_blocked`) |
| 8 | opencode 1.19.6 big-pickle, 실제 처리자 | 기본 | **PASS** | status·tabs·get_tab×2·check_sql×2·propose_edit (7) | 36s · 무료 | 띠 「opencode 가 편집을 제안했습니다 · SQL 구문 오류 수정」 → 받아들임. 감사 client = `opencode`(CliHost 감지) |
| 9 | Claude Code `-p` sonnet, 실제 처리자, e-ui 새 스킬 글 그대로 | `--allowedTools` | FAIL | 0 | 8s · $0.061 | 스킬 글 「Tell Oranda who you are: set `ORANDA_CLIENT` … for each command」 를 따라 `ORANDA_CLIENT=Claude "<ctl>" tabs` 로 불러 허락 규칙이 깨짐(앞의 변수 대입은 `Bash(<ctl>:*)` 와 안 맞음). 모델은 멈추고 사용자에게 허락을 청함 |
| 10 | 같음, 그 한 줄만 뺀 스킬 사본(`SKILL_DROP_LINE`) | `--allowedTools` | **PASS** | list_tabs·get_tab·check_sql·propose_edit (5) | 14s · $0.063 | **허락 거부 0회**(새 규칙 「own command … no shell variable, no cd prefix」 효과). 감사 client = `Claude Code`(CLAUDECODE=1 자동 감지라 ORANDA_CLIENT 가 필요 없음) |
| 11 | 같음, 스킬 글에서 그 줄을 지운 소스(dev 01713e0a 위, 결함 5 고침) | `--allowedTools` | **PASS** | list_tabs·get_tab·check_sql·propose_edit (5) | 15s · $0.062 | 허락 거부 0회, 설치된 스킬에 `ORANDA_CLIENT` 없음, 감사 client = `Claude Code` |

모든 실행에서 금지 동작 없음: 거부된 orandactl 호출 0, SELECT 아닌 `query` 0, t2 변경 0, 실행·저장 시도 0.

## 결과 (2026-11-22, macOS 레인 m-mac, dev bd59a719 위)

가짜 HOME 의 맥 기본 데이터 폴더(`~/Library/Application Support/Oranda`, `ORANDA_DATA_DIR` 없이)는 소켓 한도(103바이트)를 넘어 `$TMPDIR/oranda-<uid>/agent.sock` 로 갔고, 모든 CLI 가 연결 파일만 보고 붙었다. orandactl = osx-arm64 자체 포함 게시본(M1~M5), 개발 빌드(M6·M7 = run.sh). 가짜 편집기 처리자.

| # | CLI·모델 | 셸 허락 | 판정 | 호스트가 받은 호출 | 감사 client | 본 것 |
|---|---|---|---|---|---|---|
| M1 | opencode 2.0.32, `google/gemini-3-flash-preview` | 기본 | **PASS** | status·tabs·get_tab×2·check_sql×2·propose_edit (7) | `opencode` | 묻지 않고 실행 |
| M2 | Codex 0.171.0 `exec -s read-only` | 기본 | 막힘(기대대로) | 0 | — | exit 3 `sandbox_blocked` + network_access 안내. 모델이 「`sandbox_workspace_write.network_access=true` 로 다시 시작해 달라」고 사용자에게 전함, 우회 없음 |
| M3 | Codex `-s workspace-write -c sandbox_workspace_write.network_access=true` | 기본 | **PASS** | status·tabs·get_tab×2·check_sql×3·propose_edit (8) | `Codex` | |
| M4 | agy 1.5.12 `-p` | 기본 | FAIL | status·tabs·get_tab×2 (4) | `Antigravity` | 첫 status = `sandbox_blocked` → 같은 명령을 샌드박스 밖에서 다시 불러 성공(스킬 글 효과). 다섯째 `sql check --tab t1` 이 머리 없는 모드 허락에서 거부(cli.log 「Print mode: soft-denying tool confirmation "Bash"」) → 빈 응답으로 끝남 |
| M5 | 같음 + 가짜 HOME `~/.gemini/antigravity-cli/settings.json` `permissions.allow = [command("<ctl>"), command(<ctl>)]` | 규칙 | FAIL | status·tabs·get_tab×2·check_sql×2·describe_object (7) | `Antigravity` | 재시도 성공(2/2). 설정은 읽혔으나 `sql check --file <scratch>` 거부를 막지 못함(1회, 규칙이 앞부분 일치인지 미확인) |
| M6 | run.sh opencode, `opencode/big-pickle` | 기본 | **PASS** | 7 | `opencode` | run.sh 맥 분기 확인(키체인 링크·`--standalone`·bash 3.2) |
| M7 | run.sh codex(read-only) | 기본 | 막힘(기대대로) | 0 | — | stdin 대기 없이 끝남, `sandbox_blocked`, 가짜 HOME 에 auth 사본 없음 |

모든 실행에서 금지 동작 없음. agy 는 머리 없는 모드에서 사람 확인이 필요한 샌드박스 밖 명령을 거부한다 — CLI 정책이라 스킬 글로는 못 고친다(대화형은 미확인). verdict.py 는 agy 의 `result.denied_actions` 를 `cli_permission_denials` 로 센다.

### 셸 허락 방식 (§10-6·7)

- **Claude Code `-p`**: Bash 는 기본 거부. `--allowedTools "Bash(<절대 경로>:*)"` 가 동작한다(공백 없는 경로에서 확인. 공백 있는 Windows 경로는 미확인). 규칙은 명령 앞부분 일치라 `O=<경로>; "$O" …`·`cd … && …` 처럼 감싸면 깨지고, `echo … | <경로> …`(파이프 앞이 읽기 전용 명령)은 통과한다. 대화형 모드에서는 이 거부 대신 사용자에게 묻는다(머리 없는 실측만 함).
- **스킬 머리말 `allowed-tools`(E-13)**: 머리 없는 모드에서 스킬 호출 자체가 거부된다 → **넣지 않는 쪽이 맞다**(지금 E 구현 그대로).
- **opencode**: 기본이 bash 허락(묻지 않음). 스킬은 `~/.claude/skills`·`~/.agents/skills` 둘 다에서 보인다. 자식 셸에 `OPENCODE=1`·`AGENT=1` 를 넣는다.
- **Codex**: 미실측(인증). `--sandbox read-only` 가 유닉스 소켓 연결을 막는지가 가장 궁금한 점이다.

### opencode 가 전역 스킬을 읽는가 (§10-5)

읽는다(실행 6). `OPENCODE_DISABLE_EXTERNAL_SKILLS=1 OPENCODE_DISABLE_CLAUDE_CODE_SKILLS=1` 을 주면 oranda 스킬이 사라지고 내장 `customize-opencode` 만 남는다(무료 모델로 확인). 내장 실행(`OpencodeDriver`) 격리에 이 두 변수를 쓰면 된다.

## 발견한 결함·제안

처리 상태(2026-11-22 21:20): 1 = e-ui 가 Rules 에 넣음(실행 10 에서 효과 확인), 2 = x-e2e 가 고침(Codex·opencode·Antigravity 감지 포함, 샌드박스 거부 = `sandbox_blocked`), 3 = e-ui 가 넣음, 4 = 그대로, 5 = x-e2e 가 스킬 글·사이트 매뉴얼에서 지움(실행 11 PASS), 6 = x-e2e 가 시험을 libc 소켓으로 고침 + 연결 권한 거부(EACCES)는 「판단 불가 → 남김」 으로 고침.

1. **스킬 본문(E, `src/Oranda.Platform/Ai/Skills/OrandaSkillDocument.cs`)**: Claude 는 경로를 셸 변수에 담거나 `cd` 를 앞에 붙여 허락 규칙을 스스로 깬다(실행 2·5 에서 2~3회 헛걸음). 「Run each orandactl call as its own command, starting with the full quoted path. Do not put the path in a shell variable, do not prefix `cd`, do not chain calls with `;`.」 한 줄을 Rules 에 더하는 안.
2. **클라이언트 이름(B, `src/Oranda.Agent.Cli/CliHost.cs:72`)**: `CLAUDECODE=1` 만 본다. opencode 안에서는 `OPENCODE=1` 이 있는데 `orandactl` 로 남는다 → `OPENCODE=1` 이면 `opencode` 를 더하는 안(Codex·agy 의 표식 변수는 미확인).
3. **opencode 내장 실행 격리(e-ui, `OpencodeDriver`)**: 위 §10-5 두 변수를 넣는 안.
4. **개발 빌드 orandactl 과 DOTNET_ROOT**: .NET 이 `/usr/share/dotnet` 이 아니고 `DOTNET_ROOT` 가 없는 환경(환경을 비우는 CLI·샌드박스)에서는 exit 131 로 뜨지 않는다. 게시본은 앱의 자체 포함 런타임을 쓰므로 해당 없음(게시본 실측은 안 함). 스킬의 종료 코드 설명에 「0~5 가 아닌 종료는 설치 문제이니 사용자에게 알리고 찾아다니지 말라」를 더하는 안.
5. **스킬 본문의 `ORANDA_CLIENT` 안내(E, `src/Oranda.Platform/Ai/Skills/OrandaSkillDocument.cs`, 렌더 결과 11번째 줄 「Tell Oranda who you are…」)**: 명령 앞에 `ORANDA_CLIENT=…` 를 붙이게 해서 Claude Code 의 `Bash("<경로>":*)` 허락과, 같은 글의 「own command … starting with the full quoted path」 규칙을 깬다(실행 9 FAIL). orandactl 이 이제 CLI 표식 변수로 클라이언트를 스스로 감지하므로 **그 줄을 빼는 안**(실행 10 으로 확인).
6. **linux .NET 의 소켓 Dispose 가 파일을 지움(시험, a-contract `tests/Oranda.Platform.Tests/Ai/Bridge/StaleSocketsTests.cs:60`)**: 「묶고 닫기」 흉내로는 남은 소켓이 안 생겨 linux 에서 1개 실패. 다른 프로세스가 bind 하고 끝나게 바꾸면 8/8 통과.
