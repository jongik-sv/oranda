# PRD 7.2 성능 예산 전 항목 측정 (Windows). 사용: scripts/perf/run-windows.ps1 [-Out artifacts/perf-win] [-SkipPublish] [-SkipInstaller]
# 준비: 조용한 PC(다른 빌드 없음, 로컬 콘솔 세션, 60 Hz 이상). DB 항목은 ORANDA_IT_ADMIN_PASSWORD (+ ORANDA_IT_HOST/PORT/SERVICE/ADMIN_USER) 필요.
# 결과: <Out>/*.md, *.json (항목별 n·중앙값·p95·최대·판정). 모든 값은 Release·ReadyToRun 게시본에서 잰다.
param(
    [string]$Out = 'artifacts/perf-win',
    [switch]$SkipPublish,
    [switch]$SkipInstaller,
    [int]$StartupRuns = 12,
    [int]$ColdRuns = 5
)
$ErrorActionPreference = 'Stop'
$rid = 'win-x64'
$app = "artifacts/publish/$rid/Oranda.App.exe"
$ui = "artifacts/perfui/$rid/Oranda.Perf.Ui.exe"
New-Item -ItemType Directory -Force $Out | Out-Null

if (-not $SkipPublish) {
    dotnet publish src/Oranda.App -c Release -r $rid --self-contained -p:PublishReadyToRun=true -o "artifacts/publish/$rid"
    dotnet publish tests/Oranda.Perf.Ui -c Release -r $rid --self-contained -p:PublishReadyToRun=true -o "artifacts/perfui/$rid"
    dotnet build tests/Oranda.Perf -c Release
}
$perf = 'tests/Oranda.Perf/bin/Release/net10.0/Oranda.Perf.exe'

# 1. 시작(두 번째 실행 이후): 고정 데이터 폴더를 쓰고 앞 2회는 버린다.
$data = Join-Path $env:TEMP ('oranda-perf-data-' + [guid]::NewGuid().ToString('N'))
& $perf --exe $app --runs $StartupRuns --discard 2 --data-dir $data --idle-s 2 --out "$Out/startup-warm"
try { if (Test-Path $data) { Remove-Item -Recurse -Force $data } } catch { Write-Warning "임시 데이터 폴더를 못 지웠다: $data" }
# 2. 최초 실행 근사: 실행마다 게시 폴더를 새로 복사(백신 검사·디스크 캐시 없음에 가깝다). 제한 시간 넉넉히.
& $perf --exe $app --runs $ColdRuns --cold-copy --timeout-s 120 --out "$Out/startup-cold"
# 3. 유휴 메모리(접속·탭 없음): 입력 가능 뒤 10초 유휴
& $perf --exe $app --runs 5 --discard 1 --idle-s 10 --out "$Out/idle-memory"

# 4. 창 시나리오(진짜 앱, 진짜 창). 흔들림을 보려고 3번씩 돈다(--label r1..r3).
foreach ($r in 1..3) {
    foreach ($s in 'format', 'typing', 'diagnostics') { & $ui $s --out "$Out/ui" --label "r$r" }
}
if ($env:ORANDA_IT_ADMIN_PASSWORD) {
    foreach ($r in 1..3) {
        foreach ($s in 'completion', 'firstrow', 'cancel', 'memory', 'grid') { & $ui $s --out "$Out/ui" --label "r$r" }
    }
} else {
    Write-Warning 'ORANDA_IT_ADMIN_PASSWORD 가 없어 DB 시나리오(completion, firstrow, cancel, memory, grid)를 건너뛴다.'
}

# 5. 설치 파일 크기 (Velopack). vpk 가 없으면 건너뛴다.
if (-not $SkipInstaller) {
    if (Get-Command vpk -ErrorAction SilentlyContinue) {
        $rel = "$Out/release"
        vpk pack --packId Oranda --packVersion 0.1.0 --packDir "artifacts/publish/$rid" --mainExe Oranda.App.exe --outputDir $rel
        Get-ChildItem $rel -File | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table | Out-String | Set-Content "$Out/installer-size.txt"
        & "$PSScriptRoot/../release/check-installer-size.ps1" -Dir $rel
    } else {
        Write-Warning 'vpk 가 없어 설치 파일 크기를 건너뛴다(dotnet tool install -g vpk --version 1.2.161).'
    }
}
