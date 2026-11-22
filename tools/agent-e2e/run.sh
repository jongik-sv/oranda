#!/usr/bin/env bash
# X2 손 도구: 가짜 Oranda 호스트를 띄우고, 가짜 HOME 에 oranda 스킬을 깐 뒤, 실제 LLM CLI 를 머리 없이 한 번 돌리고 판정한다.
# 사용법: tools/agent-e2e/run.sh <claude|codex|opencode|agy> [작업 폴더] [--allow]
#   --allow : CLI 의 셸 허락을 orandactl 하나로만 미리 준다(claude = --allowedTools "Bash(<orandactl>:*)", codex = danger-full-access, agy = 없음).
# linux·macOS(bash 3.2: 빈 배열은 ${a[@]+"${a[@]}"} 로 편다).
#             빼면 CLI 기본 허락 방식 그대로 돌려 「스킬만으로 되는가」를 본다.
# SCENARIO=<이름> : scenarios/<이름>/ 의 과제(task.txt, {WORK} = 작업 폴더)·준비 데이터(fixture.json)·기대 판정(expect.json)을 쓴다.
#             / 로 시작하면 그 폴더를 시나리오로 쓴다. 없으면 1단계 고정 과제(문법 틀린 SQL 고치기).
# 결과: <작업 폴더>/{log.jsonl, transcript.*, verdict.txt}. 사용자 실제 HOME·설정은 건드리지 않는다(인증 파일만 읽어서 복사).
set -euo pipefail

cli="${1:?claude|codex|opencode|agy}"
out="${2:-$(mktemp -d -t agent-e2e-XXXXXX)}"
allow="${3:-}"
repo="$(cd "$(dirname "$0")/../.." && pwd)"
os="$(uname -s)"   # Darwin = macOS: 인증이 키체인에 있다
ctl="$repo/src/Oranda.Agent.Cli/bin/Debug/net10.0/orandactl"
host_dll="$repo/tools/agent-e2e/FakeHost/bin/Debug/net10.0/Oranda.App.Tests.dll"
task='Oranda 편집기 탭 목록을 보고 문법이 틀린 SQL 을 찾아, 고친 문장을 그 탭에 제안해. 끝나면 무엇을 고쳤는지 한 줄로 말해.'

[[ -x "$ctl" ]] || dotnet build "$repo/src/Oranda.Agent.Cli" -c Debug -v q
[[ -f "$host_dll" ]] || dotnet build "$repo/tools/agent-e2e/FakeHost" -c Debug -v q

mkdir -p "$out"
out="$(cd "$out" && pwd)"
home="$out/home"; data="$out/data"; work="$out/work"
scenario_dir=""
host_scenario=()
if [[ -n "${SCENARIO:-}" ]]; then
  # 이름이면 scenarios/<이름>, / 로 시작하면 그 폴더(저장소 밖 임시 시나리오).
  if [[ "$SCENARIO" == /* ]]; then scenario_dir="$SCENARIO"; else scenario_dir="$repo/tools/agent-e2e/scenarios/$SCENARIO"; fi
  [[ -f "$scenario_dir/task.txt" ]] || { echo "모르는 시나리오: $SCENARIO" >&2; exit 2; }
  task="$(python3 -I -c 'import sys; print(open(sys.argv[1], encoding="utf-8").read().strip().replace("{WORK}", sys.argv[2]))' "$scenario_dir/task.txt" "$work")"
  host_scenario=(--scenario "$scenario_dir/fixture.json")
fi
rm -f "$out/ready" "$out/log.jsonl"
mkdir -p "$home" "$data" "$work"

# 인증만 읽기 전용 복사(원본은 건드리지 않는다).
# macOS: Claude Code·agy 는 로그인을 키체인에 둔다 → 가짜 HOME 에 실제 키체인 폴더 링크(지울 때 `rm -rf "$out/home"` — 링크 뒤에 / 를 붙이지 말 것).
if [[ "$os" == Darwin ]]; then
  mkdir -p "$home/Library"
  ln -s "$HOME/Library/Keychains" "$home/Library/Keychains"
fi
case "$cli" in
  claude)
    if [[ "$os" != Darwin ]]; then
      mkdir -p "$home/.claude"
      install -m 600 "$HOME/.claude/.credentials.json" "$home/.claude/.credentials.json"
    fi
    ;;
  codex)
    # auth.json 은 복사하지 않는다: ChatGPT 로그인이면 사본이 refresh token 을 돌려 실제 사본이 로그아웃된다(맥 실측 위험).
    # 실제 CODEX_HOME 을 쓰고 --ephemeral 로 세션 기록을 남기지 않는다.
    ;;
  opencode)
    [[ -f "$HOME/.local/share/opencode/auth.json" ]] && { mkdir -p "$home/.local/share/opencode"; install -m 600 "$HOME/.local/share/opencode/auth.json" "$home/.local/share/opencode/auth.json"; }
    ;;
esac

dotnet "$host_dll" --data "$data" --log "$out/log.jsonl" --home "$home" --orandactl "$ctl" --ready "$out/ready" --max-seconds 900 ${REAL_EDITOR:+--real-editor} ${host_scenario[@]+"${host_scenario[@]}"} &
host_pid=$!
trap 'kill -TERM $host_pid 2>/dev/null || true' EXIT
for _ in $(seq 1 60); do [[ -f "$out/ready" ]] && break; sleep 0.5; done
[[ -f "$out/ready" ]] || { echo "가짜 호스트가 뜨지 않았습니다" >&2; exit 1; }

# CLI 쪽에는 ORANDA_DATA_DIR 만 준다(토큰·소켓은 연결 파일에서 찾는다). 바깥 Claude Code 세션 표시는 지운다.
# env -i: 바깥 셸(이 스크립트를 띄운 Claude Code 세션 등)의 CLAUDE_CODE_*·AI_AGENT·토큰이 새지 않게 빈 환경에서 시작한다.
env_common=(env -i HOME="$home" XDG_CONFIG_HOME="$home/.config" XDG_DATA_HOME="$home/.local/share" XDG_CACHE_HOME="$home/.cache"
  ORANDA_DATA_DIR="$data" PATH="$PATH" LANG="${LANG:-C.UTF-8}" TERM=dumb USER="${USER:-}")
# macOS 의 사용자 임시 폴더(TMPDIR)는 사용자마다 달라 그대로 넘긴다.
[[ -n "${TMPDIR:-}" ]] && env_common+=(TMPDIR="$TMPDIR")
# 공급자 API 키는 있으면 넘긴다(env -i 가 지우면 opencode 등이 모델을 못 찾는다: provider.no-route).
for key in GEMINI_API_KEY GOOGLE_GENERATIVE_AI_API_KEY OPENAI_API_KEY ANTHROPIC_API_KEY OPENROUTER_API_KEY; do
  [[ -n "${!key:-}" ]] && env_common+=("$key=${!key}")
done
# 개발 빌드 orandactl 은 프레임워크 의존 apphost 라, .NET 이 기본 위치(/usr/share/dotnet)가 아니면 DOTNET_ROOT 가 있어야 뜬다(없으면 exit 131).
if [[ -z "${DOTNET_ROOT:-}" ]] && command -v dotnet > /dev/null; then
  DOTNET_ROOT="$(dirname "$(readlink -f "$(command -v dotnet)")")"
fi
[[ -n "${DOTNET_ROOT:-}" ]] && env_common+=(DOTNET_ROOT="$DOTNET_ROOT")
# 더 넣을 변수(예: EXTRA_ENV="OPENCODE_DISABLE_EXTERNAL_SKILLS=1").
[[ -n "${EXTRA_ENV:-}" ]] && read -r -a more <<< "$EXTRA_ENV" && env_common+=("${more[@]}")

# 실험: 설치된 스킬 사본(세 곳)에서 정규식에 맞는 줄을 뺀다(예: SKILL_DROP_LINE='^Tell Oranda who you are').
if [[ -n "${SKILL_DROP_LINE:-}" ]]; then
  for skill in "$home/.claude/skills/oranda/SKILL.md" "$home/.agents/skills/oranda/SKILL.md" "$home/.gemini/config/skills/oranda/SKILL.md"; do
    python3 -I -c 'import re, sys; p, rx = sys.argv[1], re.compile(sys.argv[2]); l = open(p, encoding="utf-8").read().split("\n"); open(p, "w", encoding="utf-8").write("\n".join(x for x in l if not rx.search(x)))' "$skill" "$SKILL_DROP_LINE"
  done
fi

# 실험: 설치된 스킬 사본(세 곳) 끝에 글 한 덩어리를 붙인다(예: SKILL_APPEND='Pass JSON bodies with printf ... |').
if [[ -n "${SKILL_APPEND:-}" ]]; then
  for skill in "$home/.claude/skills/oranda/SKILL.md" "$home/.agents/skills/oranda/SKILL.md" "$home/.gemini/config/skills/oranda/SKILL.md"; do
    [[ -f "$skill" ]] && printf '\n%s\n' "$SKILL_APPEND" >> "$skill"
  done
fi

# 가짜 docs: 시나리오 실행인데 orandactl 에 로컬 `docs` 명령이 아직 없으면(S2 머지 전) site 머리말로 만든 매뉴얼 장 목록을 스킬 사본 끝에 붙인다.
# FAKE_DOCS=0 이면 붙이지 않는다. 붙였는지는 <작업 폴더>/fake_docs 로 남긴다(판정이 표에 적는다).
rm -f "$out/fake_docs"
if [[ -n "$scenario_dir" && "${FAKE_DOCS:-1}" != 0 ]] && ! "$ctl" docs export > /dev/null 2>&1; then
  python3 -I "$repo/tools/agent-e2e/manual_index.py" "$repo" > "$out/manual-index.md"
  for skill in "$home/.claude/skills/oranda/SKILL.md" "$home/.agents/skills/oranda/SKILL.md" "$home/.gemini/config/skills/oranda/SKILL.md"; do
    [[ -f "$skill" ]] && cat "$out/manual-index.md" >> "$skill"
  done
  touch "$out/fake_docs"
fi

# 실험(설계 §10-6, 결정 E-13): 설치된 Claude 스킬 머리말 둘째 줄에 allowed-tools 를 넣어 본다(가짜 HOME 의 사본만 고친다).
if [[ -n "${SKILL_ALLOWED_TOOLS:-}" ]]; then
  python3 -I -c 'import sys; p, v = sys.argv[1], sys.argv[2]; l = open(p, encoding="utf-8").read().split("\n"); l.insert(2, "allowed-tools: " + v); open(p, "w", encoding="utf-8").write("\n".join(l))' \
    "$home/.claude/skills/oranda/SKILL.md" "$SKILL_ALLOWED_TOOLS"
fi
start=$(date +%s)
set +e
case "$cli" in
  claude)
    extra=()
    [[ "$allow" == "--allow" ]] && extra=(--allowedTools "Bash($ctl:*)")
    (cd "$work" && "${env_common[@]}" claude -p "$task" --model sonnet --max-turns 20 --strict-mcp-config \
      --output-format stream-json --verbose ${extra[@]+"${extra[@]}"}) > "$out/transcript.jsonl" 2> "$out/stderr.txt"
    ;;
  codex)
    codex_bin="${CODEX_BIN:-codex}"
    extra=(--sandbox read-only)
    [[ "$allow" == "--allow" ]] && extra=(--sandbox danger-full-access)
    # stdin 을 닫지 않으면 「Reading additional input from stdin...」 에서 끝없이 기다린다(맥 실측).
    (cd "$work" && "${env_common[@]}" CODEX_HOME="${CODEX_HOME:-$HOME/.codex}" "$codex_bin" exec --ephemeral --skip-git-repo-check ${extra[@]+"${extra[@]}"} --json "$task" < /dev/null) > "$out/transcript.jsonl" 2> "$out/stderr.txt"
    ;;
  opencode)
    oc_bin="${OPENCODE_BIN:-opencode}"
    # opencode 2.x 는 탭들이 공유 백그라운드 서비스(실제 HOME)를 쓴다 → 있으면 --standalone 으로 자기 서버를 띄운다.
    oc_extra=()
    "$oc_bin" run --help 2>&1 | grep -q -- --standalone && oc_extra=(--standalone)
    # 무료 모델이 응답 없이 멈추는 일이 있다(1회 실측, 호출 0으로 15분+) → CLI_TIMEOUT(기본 600초)에서 끊는다.
    (cd "$work" && "${env_common[@]}" timeout "${CLI_TIMEOUT:-600}" "$oc_bin" run ${oc_extra[@]+"${oc_extra[@]}"} ${OPENCODE_MODEL:+--model "$OPENCODE_MODEL"} --format json "$task") > "$out/transcript.jsonl" 2> "$out/stderr.txt"
    ;;
  agy)
    # Antigravity CLI: 명령을 먼저 샌드박스에서 돌리고, 머리 없는(-p) 모드에서는 사람 확인이 필요한 명령을 거부한다(denied_actions).
    agy_bin="${AGY_BIN:-agy}"
    # --allow 는 받지 않는다: orandactl 하나만 허락하는 손잡이가 없고(permissions.allow command(<경로>) 로는 거부가 안 풀렸다, 맥 1회),
    # --dangerously-skip-permissions 는 모든 도구를 허락해 판정의 뜻이 달라진다.
    (cd "$work" && "${env_common[@]}" timeout 300 "$agy_bin" -p "$task" --print-timeout 280s --output-format stream-json < /dev/null) > "$out/transcript.jsonl" 2> "$out/stderr.txt"
    ;;
  *) echo "모르는 CLI: $cli" >&2; exit 2 ;;
esac
cli_exit=$?
set -e
elapsed=$(( $(date +%s) - start ))

kill -TERM $host_pid 2>/dev/null || true
wait $host_pid 2>/dev/null || true
trap - EXIT

python3 -I "$repo/tools/agent-e2e/verdict.py" "$out" "$cli" "$cli_exit" "$elapsed" ${scenario_dir:+"$scenario_dir"} | tee "$out/verdict.txt"
