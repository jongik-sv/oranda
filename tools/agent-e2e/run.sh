#!/usr/bin/env bash
# X2 손 도구: 가짜 Oranda 호스트를 띄우고, 가짜 HOME 에 oranda 스킬을 깐 뒤, 실제 LLM CLI 를 머리 없이 한 번 돌리고 판정한다.
# 사용법: tools/agent-e2e/run.sh <claude|codex|opencode> [작업 폴더] [--allow]
#   --allow : CLI 의 셸 허락을 orandactl 하나로만 미리 준다(claude = --allowedTools "Bash(<orandactl>:*)").
#             빼면 CLI 기본 허락 방식 그대로 돌려 「스킬만으로 되는가」를 본다.
# 결과: <작업 폴더>/{log.jsonl, transcript.*, verdict.txt}. 사용자 실제 HOME·설정은 건드리지 않는다(인증 파일만 읽어서 복사).
set -euo pipefail

cli="${1:?claude|codex|opencode}"
out="${2:-$(mktemp -d -t agent-e2e-XXXXXX)}"
allow="${3:-}"
repo="$(cd "$(dirname "$0")/../.." && pwd)"
ctl="$repo/src/Oranda.Agent.Cli/bin/Debug/net10.0/orandactl"
host_dll="$repo/tools/agent-e2e/FakeHost/bin/Debug/net10.0/Oranda.App.Tests.dll"
task='Oranda 편집기 탭 목록을 보고 문법이 틀린 SQL 을 찾아, 고친 문장을 그 탭에 제안해. 끝나면 무엇을 고쳤는지 한 줄로 말해.'

[[ -x "$ctl" ]] || dotnet build "$repo/src/Oranda.Agent.Cli" -c Debug -v q
[[ -f "$host_dll" ]] || dotnet build "$repo/tools/agent-e2e/FakeHost" -c Debug -v q

mkdir -p "$out"
home="$out/home"; data="$out/data"; work="$out/work"
rm -f "$out/ready" "$out/log.jsonl"
mkdir -p "$home" "$data" "$work"

# 인증만 읽기 전용 복사(원본은 건드리지 않는다).
case "$cli" in
  claude)
    mkdir -p "$home/.claude"
    install -m 600 "$HOME/.claude/.credentials.json" "$home/.claude/.credentials.json"
    ;;
  codex)
    [[ -f "$HOME/.codex/auth.json" ]] && { mkdir -p "$home/.codex"; install -m 600 "$HOME/.codex/auth.json" "$home/.codex/auth.json"; }
    ;;
  opencode)
    [[ -f "$HOME/.local/share/opencode/auth.json" ]] && { mkdir -p "$home/.local/share/opencode"; install -m 600 "$HOME/.local/share/opencode/auth.json" "$home/.local/share/opencode/auth.json"; }
    ;;
esac

dotnet "$host_dll" --data "$data" --log "$out/log.jsonl" --home "$home" --orandactl "$ctl" --ready "$out/ready" --max-seconds 900 ${REAL_EDITOR:+--real-editor} &
host_pid=$!
trap 'kill -TERM $host_pid 2>/dev/null || true' EXIT
for _ in $(seq 1 60); do [[ -f "$out/ready" ]] && break; sleep 0.5; done
[[ -f "$out/ready" ]] || { echo "가짜 호스트가 뜨지 않았습니다" >&2; exit 1; }

# CLI 쪽에는 ORANDA_DATA_DIR 만 준다(토큰·소켓은 연결 파일에서 찾는다). 바깥 Claude Code 세션 표시는 지운다.
# env -i: 바깥 셸(이 스크립트를 띄운 Claude Code 세션 등)의 CLAUDE_CODE_*·AI_AGENT·토큰이 새지 않게 빈 환경에서 시작한다.
env_common=(env -i HOME="$home" XDG_CONFIG_HOME="$home/.config" XDG_DATA_HOME="$home/.local/share" XDG_CACHE_HOME="$home/.cache"
  ORANDA_DATA_DIR="$data" PATH="$PATH" LANG="${LANG:-C.UTF-8}" TERM=dumb USER="${USER:-}")
# 개발 빌드 orandactl 은 프레임워크 의존 apphost 라, .NET 이 기본 위치(/usr/share/dotnet)가 아니면 DOTNET_ROOT 가 있어야 뜬다(없으면 exit 131).
[[ -n "${DOTNET_ROOT:-}" ]] && env_common+=(DOTNET_ROOT="$DOTNET_ROOT")
# 더 넣을 변수(예: EXTRA_ENV="OPENCODE_DISABLE_EXTERNAL_SKILLS=1").
[[ -n "${EXTRA_ENV:-}" ]] && read -r -a more <<< "$EXTRA_ENV" && env_common+=("${more[@]}")

# 실험: 설치된 스킬 사본(세 곳)에서 정규식에 맞는 줄을 뺀다(예: SKILL_DROP_LINE='^Tell Oranda who you are').
if [[ -n "${SKILL_DROP_LINE:-}" ]]; then
  for skill in "$home/.claude/skills/oranda/SKILL.md" "$home/.agents/skills/oranda/SKILL.md" "$home/.gemini/config/skills/oranda/SKILL.md"; do
    python3 -I -c 'import re, sys; p, rx = sys.argv[1], re.compile(sys.argv[2]); l = open(p, encoding="utf-8").read().split("\n"); open(p, "w", encoding="utf-8").write("\n".join(x for x in l if not rx.search(x)))' "$skill" "$SKILL_DROP_LINE"
  done
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
      --output-format stream-json --verbose "${extra[@]}") > "$out/transcript.jsonl" 2> "$out/stderr.txt"
    ;;
  codex)
    codex_bin="${CODEX_BIN:-codex}"
    extra=(--sandbox read-only)
    [[ "$allow" == "--allow" ]] && extra=(--sandbox danger-full-access)
    (cd "$work" && "${env_common[@]}" "$codex_bin" exec --skip-git-repo-check "${extra[@]}" --json "$task") > "$out/transcript.jsonl" 2> "$out/stderr.txt"
    ;;
  opencode)
    oc_bin="${OPENCODE_BIN:-opencode}"
    (cd "$work" && "${env_common[@]}" "$oc_bin" run ${OPENCODE_MODEL:+--model "$OPENCODE_MODEL"} --format json "$task") > "$out/transcript.jsonl" 2> "$out/stderr.txt"
    ;;
  *) echo "모르는 CLI: $cli" >&2; exit 2 ;;
esac
cli_exit=$?
set -e
elapsed=$(( $(date +%s) - start ))

kill -TERM $host_pid 2>/dev/null || true
wait $host_pid 2>/dev/null || true
trap - EXIT

python3 -I "$repo/tools/agent-e2e/verdict.py" "$out" "$cli" "$cli_exit" "$elapsed" | tee "$out/verdict.txt"
