#!/usr/bin/env bash
# PRD 7.2 성능 예산 전 항목 측정 (macOS arm64). 사용: scripts/perf/run-macos.sh [출력폴더]
# 준비: AC 전원, 저전력 모드 끔, 다른 빌드·Spotlight 색인 없음, 측정 중 화면을 건드리지 않는다.
#   최초 실행(디스크 캐시 없음)은 매 회 sudo purge 를 쓴다 → 미리 `sudo -v` 로 암호를 입력해 두거나 NOPASSWD 로 둔다.
#   DB 항목: ORANDA_IT_ADMIN_PASSWORD (+ ORANDA_IT_HOST/PORT/SERVICE/ADMIN_USER).
# 주의: 게시 폴더는 리포 안에서 실행한다(임시 폴더에서는 실행 파일이 SIGKILL 되는 경우가 있었다, docs/perf/macos-2026-10-20.md).
set -euo pipefail
OUT="${1:-artifacts/perf-mac}"
RID=osx-arm64
APP="artifacts/publish/$RID/Oranda.App"
UI="artifacts/perfui/$RID/Oranda.Perf.Ui"
mkdir -p "$OUT"

dotnet publish src/Oranda.App -c Release -r $RID --self-contained -p:PublishReadyToRun=true -o "artifacts/publish/$RID"
dotnet publish tests/Oranda.Perf.Ui -c Release -r $RID --self-contained -p:PublishReadyToRun=true -o "artifacts/perfui/$RID"
dotnet build tests/Oranda.Perf -c Release
PERF=tests/Oranda.Perf/bin/Release/net10.0/Oranda.Perf

# 1. 시작(두 번째 실행 이후): 고정 데이터 폴더, 앞 2회 버림
DATA="$(mktemp -d "$PWD/artifacts/perf-data.XXXXXX")"
"$PERF" --exe "$APP" --runs 12 --discard 2 --data-dir "$DATA" --idle-s 2 --out "$OUT/startup-warm"
rm -rf "$DATA"

# 2. 최초 실행(디스크 캐시 없음): 매 회 sudo purge 뒤 1회 측정, 3회
for i in 1 2 3; do
  sudo purge
  "$PERF" --exe "$APP" --runs 1 --timeout-s 120 --out "$OUT/startup-cold-$i"
done

# 3. 유휴 메모리(접속·탭 없음): footprint(phys_footprint) 로 읽는다
"$PERF" --exe "$APP" --runs 5 --discard 1 --idle-s 10 --out "$OUT/idle-memory"

# 4. 창 시나리오(진짜 앱, 진짜 창). 3번씩.
for r in 1 2 3; do
  for s in format typing diagnostics; do "$UI" "$s" --out "$OUT/ui" --label "r$r"; done
done
if [ -n "${ORANDA_IT_ADMIN_PASSWORD:-}" ]; then
  for r in 1 2 3; do
    for s in completion firstrow cancel memory grid; do "$UI" "$s" --out "$OUT/ui" --label "r$r"; done
  done
else
  echo "ORANDA_IT_ADMIN_PASSWORD 가 없어 DB 시나리오를 건너뛴다." >&2
fi

# 5. 설치 파일 크기: CI 의 macos 작업이 만든 .pkg 크기(크기 검사 단계 로그)를 쓴다. 로컬에서 만들려면:
#    vpk pack --packId Oranda --packVersion 0.1.0 --packDir artifacts/publish/$RID --mainExe Oranda.App --outputDir "$OUT/release" (서명 없음)
ls -l "$OUT/release" 2>/dev/null || true
