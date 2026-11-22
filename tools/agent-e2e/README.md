# agent-e2e — 실제 LLM CLI 끝 왕복 (손 도구)

밖의 LLM CLI(Claude Code·Codex·opencode)가 **oranda 스킬을 읽고 `orandactl` 로 「실행 중인 Oranda」를 다루는지** 실제로 돌려 보는 도구다.
자동 시험 묶음에 넣지 않는다(`Oranda.sln` 밖). CLI 를 부르면 돈·시간이 드니 과제마다 1~2회만 돌린다.

설계서 `docs/oranda1/research/agent-cli-control-2026-11-22.md` §2.1·§7·§10, 계약 `agent-cli-contract.md`.

## 구성

| 파일 | 하는 일 |
|---|---|
| `FakeHost/` | 가짜 Oranda. **실제** `AgentBridgeHost`(앱과 같은 공개 생성자 = 기본 소켓 규칙) · `AgentGate` · `AgentGrantStore` · 연결 파일, **실제** `check_sql`·`format_sql` 처리자, 스킬 설치기(`AgentSkillInstaller`)로 가짜 HOME 에 스킬 설치. 편집기는 가짜 탭 2개(`t1` = `SELECT * FROM SAMPLE.ORDERS WHER id = 1`, `t2` = 바른 문장). `propose_edit` 은 판이 맞으면 곧바로 받아들인다(사람이 「받아들이기」를 누른 셈). `describe_object`·`query` 는 가짜(DB 없음, 문장 종류는 실제 언어 서비스로 나눠 SELECT 아닌 것은 관문이 거부). 모든 호출을 `log.jsonl` 에 남긴다 |
| `run.sh` | 호스트 띄우기 → 인증 파일만 가짜 HOME 에 읽기 전용 복사 → `env -i` 빈 환경 + 가짜 HOME 으로 CLI 를 머리 없이 한 번 → 호스트 끄기 → 판정 |
| `verdict.py` | `log.jsonl`·`transcript.jsonl` 로 판정: 스킬을 읽었나, orandactl 호출이 왔나, 제안이 도착해 t1 이 고쳐졌나, 금지 동작(거부된 호출·SELECT 아닌 query·t2 변경)이 있었나, CLI 쪽 허락 거부 |

어셈블리 이름을 `Oranda.App.Tests` 로 둔 것은 다리·처리자가 internal 이라 Platform·Language 의 `InternalsVisibleTo` 를 빌리려는 것이다(시험 어셈블리와 함께 로드되는 일은 없다).

## 돌리는 법 (linux·macOS)

```bash
dotnet build src/Oranda.Agent.Cli -c Debug
dotnet build tools/agent-e2e/FakeHost -c Debug

tools/agent-e2e/run.sh claude  /tmp/e2e-claude            # 기본 허락(스킬만)
tools/agent-e2e/run.sh claude  /tmp/e2e-claude-allow --allow   # --allowedTools "Bash(<orandactl>:*)"
OPENCODE_BIN=<opencode 경로> OPENCODE_MODEL=opencode/big-pickle tools/agent-e2e/run.sh opencode /tmp/e2e-oc
CODEX_BIN=<codex 경로> tools/agent-e2e/run.sh codex /tmp/e2e-codex   # ~/.codex/auth.json 이 있어야 한다

# 실험 손잡이
SKILL_ALLOWED_TOOLS='Bash(<orandactl 절대 경로>:*)' tools/agent-e2e/run.sh claude /tmp/x   # 가짜 HOME 스킬 사본 머리말에 allowed-tools
EXTRA_ENV='OPENCODE_DISABLE_EXTERNAL_SKILLS=1 OPENCODE_DISABLE_CLAUDE_CODE_SKILLS=1' ...  # CLI 에 변수 더 넣기
SKILL_DROP_LINE='^Tell Oranda who you are' ...   # 설치된 스킬 사본 세 곳에서 정규식에 맞는 줄 빼기
REAL_EDITOR=1 ...   # 실제 처리자 모드(아래)
```

`REAL_EDITOR=1`(호스트 `--real-editor`): 편집기 도구는 c-editor 의 실제 처리자(`ExternalEditorService` + 실제 `SqlEditorHost` 탭, 가짜 UI 스레드), 상태·결과 도구는 e-ui 의 실제 처리자(`get_status`·`list_connections`·`get_result`·`get_last_error`)를 쓴다. 0.3초마다 탭 위 제안 띠를 보고 사람 대신 「받아들이기」를 누른다(저장 요청은 누르지 않고 기록만). 앱 창은 띄우지 않으며 DB 세션이 없어 `connected` 는 거짓이다.

과제(고정): 「Oranda 편집기 탭 목록을 보고 문법이 틀린 SQL 을 찾아, 고친 문장을 그 탭에 제안해. 끝나면 무엇을 고쳤는지 한 줄로 말해.」

손으로만 확인하려면 호스트만 띄우고 `ORANDA_DATA_DIR=<data> orandactl status` 처럼 부른다(`dotnet tools/agent-e2e/FakeHost/bin/Debug/net10.0/Oranda.App.Tests.dll --data <폴더> --log <파일> --max-seconds 60`).

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
