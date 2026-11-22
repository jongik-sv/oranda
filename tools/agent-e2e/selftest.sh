#!/usr/bin/env bash
# 하니스 자체 확인: 시나리오 준비 데이터로 FakeHost 를 띄우고 orandactl 명령 파일을 차례로 부른 뒤 판정한다(LLM 없음).
# 사용법: [REAL=1] [ANSWER=답 글] selftest.sh <시나리오> <작업 폴더(새것, 절대 경로)> [명령 파일 = scenarios/<시나리오>/selftest.cmds]
# 명령 파일 한 줄 = bash 한 명령. $C = orandactl, $WORK = 작업 폴더/work. 답 글(ANSWER)은 판정의 「마지막 답」 자리에 들어간다.
set -uo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
name="$1"; S="$2"; cmds="${3:-$repo/tools/agent-e2e/scenarios/$1/selftest.cmds}"
mkdir -p "$S/data" "$S/work"
C=$repo/src/Oranda.Agent.Cli/bin/Debug/net10.0/orandactl
dotnet $repo/tools/agent-e2e/FakeHost/bin/Debug/net10.0/Oranda.App.Tests.dll --data "$S/data" --log "$S/log.jsonl" --ready "$S/ready" --max-seconds 120 ${REAL:+--real-editor} \
  --scenario $repo/tools/agent-e2e/scenarios/$name/fixture.json > "$S/host.out" 2>&1 &
pid=$!
for _ in $(seq 1 60); do [[ -f "$S/ready" ]] && break; sleep 0.5; done
export ORANDA_DATA_DIR="$S/data" WORK="$S/work" C
: > "$S/transcript.txt"
while IFS= read -r line; do
  [[ -z "$line" || "$line" == \#* ]] && continue
  echo "\$ $line" | tee -a "$S/transcript.txt"
  bash -c "$line" 2>&1 | head -c 600 | tee -a "$S/transcript.txt"
  echo " [exit ${PIPESTATUS[0]}]" | tee -a "$S/transcript.txt"
done < "$cmds"
kill -TERM $pid; wait $pid
# 답 글 = 명령 파일의 마지막 줄 주석 대신 ANSWER 변수
# transcript.jsonl = 명령 출력 전부(LLM 이 받았을 글) 한 줄 + 답 글 한 줄.
python3 -I -c 'import json,sys; print(json.dumps({"type":"tool_output","text":open(sys.argv[1],encoding="utf-8").read()}, ensure_ascii=False)); print(json.dumps({"type":"result","result":sys.argv[2]}, ensure_ascii=False))' \
  "$S/transcript.txt" "${ANSWER:-}" > "$S/transcript.jsonl"
python3 -I $repo/tools/agent-e2e/verdict.py "$S" selftest 0 0 $repo/tools/agent-e2e/scenarios/$name
