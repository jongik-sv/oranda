# Role 구성별 성능 비교 (TSK-07-07, TRD 10.10). 사용: scripts/perf/run-roles.ps1 [-Out artifacts/perf-roles] [-SkipPublish] [-Runs 12] [-Discard 2] [-IdleS 30]
# 구성은 데이터 폴더 settings.json 의 features 로만 바꾼다(Oranda.Perf --features). 구성끼리 번갈아 가며(라운드 로빈) 재서 PC 부하 흔들림이 한 구성에 몰리지 않게 한다.
#   V0 = all (설정 없음)   V1 = sqlDev (R1)   V2 = myBatisDev (R3)   V3 = tuner,dba (R4+R5)   V4 = viewer (R6)
# 결과: <Out>/summary.md, summary.json (구성별 중앙값·p95), <Out>/load.csv (측정 직전 CPU 사용률), <Out>/<구성>/r<N>/perf-startup*.md|json (원자료).
# DB 세션 수(ORANDA_IT_ADMIN_PASSWORD 가 있을 때만): <Out>/ui-sessions/*. 비밀번호는 출력하지 않는다.
# 준비: 조용한 PC(다른 빌드 없음), 로컬 콘솔 세션, 화면 잠금 없음. 사용자의 실제 설정 폴더(%APPDATA% 등)는 건드리지 않는다 — 데이터 폴더는 <Out>/data/<구성> 이다.
param(
    [string]$Out = 'artifacts/perf-roles',
    [switch]$SkipPublish,
    [int]$Runs = 12,
    [int]$Discard = 2,
    [int]$IdleS = 30,
    [int]$SessionSeconds = 60,
    [int]$SessionRuns = 3,
    [string[]]$Configs = @('V0=all', 'V1=sqlDev', 'V2=myBatisDev', 'V3=tuner,dba', 'V4=viewer'),
    # 세션 수 비교 구성: 블로커 감시 켬(V0·V1)·끔(V4), 그리고 쓰기가 되면서 블로커만 끈 V1b.
    [string[]]$SessionConfigs = @('V0=all', 'V1=sqlDev', 'V1b=sqlDev,-blockerAlert', 'V4=viewer'),
    [switch]$FixedOrder,   # 기본은 라운드마다 구성 순서를 한 칸씩 돌린다(첫 자리·끝 자리 효과가 한 구성에 몰리지 않게). 켜면 늘 같은 순서
    [switch]$SummaryOnly, # 이미 잰 <Out> 의 원자료로 summary 만 다시 만든다
    [switch]$SkipStartup,
    [switch]$SkipRolesUi
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

function Split-Config([string]$c) { $i = $c.IndexOf('='); @{ Name = $c.Substring(0, $i); Value = $c.Substring($i + 1) } }

function Get-CpuLoad {
    # 지금 전체 CPU 사용률(3초 평균)과 CPU 를 가장 많이 쓰는 프로세스 이름
    try {
        $s = (Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 1 -MaxSamples 3).CounterSamples
        [math]::Round(($s | Measure-Object CookedValue -Average).Average, 1)
    } catch { -1 }
}

# 1. 시작·유휴 메모리·스레드·핸들·시작 작업 수. 라운드마다 모든 구성을 한 번씩.
if (-not $SkipStartup) {
    $total = $Discard + $Runs
    if (-not $SummaryOnly) { "time,round,position,config,cpu_pct" | Set-Content "$Out/load.csv" }
    foreach ($round in $(if ($SummaryOnly) { @() } else { 1..$total })) {
        $shift = if ($FixedOrder) { 0 } else { ($round - 1) % $Configs.Count }
        for ($pos = 0; $pos -lt $Configs.Count; $pos++) {
            $c = $Configs[($pos + $shift) % $Configs.Count]
            $cfg = Split-Config $c
            $cpu = Get-CpuLoad
            "$((Get-Date).ToString('s')),$round,$($pos + 1),$($cfg.Name),$cpu" | Add-Content "$Out/load.csv"
            $data = Join-Path (Resolve-Path $Out) "data/$($cfg.Name)"
            & $perf --exe $app --runs 1 --discard 0 --data-dir $data --features $cfg.Value --idle-s $IdleS --input-grace-s 20 --timeout-s 90 --out "$Out/$($cfg.Name)/r$round" | Out-Null
            Write-Host "round $round/$total $($cfg.Name) cpu=$cpu%"
        }
    }

    # 요약: 앞 $Discard 라운드를 버리고 구성·항목마다 중앙값과 p95(nearest-rank).
    $metrics = [ordered]@{
        InputReadyMs = '시작 → 입력 가능 (ms)'; FirstRenderMs = '첫 프레임 (ms)'; AppInitMs = 'app-init (ms)'; ComposedMs = 'composed (ms)'
        SettingsLoadedMs = 'settings-loaded (ms)'; ShellAttachedMs = 'shell-attached (ms)'
        PrivateMb = "유휴 전용 작업 집합 (MB, 입력 가능 뒤 ${IdleS}s)"; CommitMb = '유휴 커밋 전용 바이트 (MB)'; WorkingSetMb = '유휴 작업 집합 전체 (MB)'
        Threads = '스레드 수'; Handles = '핸들 수'; TasksRan = '시작 작업 돈 수'; TasksSkipped = '시작 작업 건너뜀 수'; StatusSkipped = '상태 칸 건너뜀 수'
    }
    function Pct([double[]]$v, [double]$p) { $s = $v | Sort-Object; $r = [math]::Max(1, [math]::Ceiling($s.Count * $p / 100)); $s[$r - 1] }
    function Med([double[]]$v) { $s = $v | Sort-Object; $n = $s.Count; if ($n % 2) { $s[($n - 1) / 2] } else { ($s[$n / 2 - 1] + $s[$n / 2]) / 2 } }
    $summary = @()
    foreach ($c in $Configs) {
        $cfg = Split-Config $c
        $rows = foreach ($round in ($Discard + 1)..$total) {
            $f = "$Out/$($cfg.Name)/r$round/perf-startup-runs.json"
            if (Test-Path $f) { $j = Get-Content $f -Raw | ConvertFrom-Json; if ($j -is [array]) { $j[0] } else { $j } }
        }
        $ok = @($rows | Where-Object { $_.Ok })
        foreach ($m in $metrics.Keys) {
            $vals = @($ok | Where-Object { $null -ne $_.$m } | ForEach-Object { [double]$_.$m })
            if ($vals.Count -gt 0) {
                $summary += [pscustomobject]@{ Config = $cfg.Name; Features = $cfg.Value; Metric = $m; Label = $metrics[$m]; N = $vals.Count; Median = [math]::Round((Med $vals), 1); P95 = [math]::Round((Pct $vals 95), 1); Min = ($vals | Measure-Object -Minimum).Minimum; Max = ($vals | Measure-Object -Maximum).Maximum }
            }
        }
    }
    $summary | ConvertTo-Json | Set-Content "$Out/summary.json"
    $header = '| 항목 | ' + (($Configs | ForEach-Object { (Split-Config $_).Name }) -join ' | ') + ' |'
    $sep = '|---|' + (('---|') * $Configs.Count)
    $md = @('# Role 구성별 성능 (중앙값, p95, n)', '', "요약 시각 $((Get-Date).ToString('s')), $env:COMPUTERNAME, 앞 $Discard 라운드 버림, 유휴 ${IdleS}s", '', $header, $sep)
    foreach ($m in $metrics.Keys) {
        $cells = foreach ($c in $Configs) {
            $cfg = Split-Config $c
            $r = $summary | Where-Object { $_.Config -eq $cfg.Name -and $_.Metric -eq $m }
            if ($r) { "$($r.Median) (p95 $($r.P95), n=$($r.N))" } else { 'n/a' }
        }
        $md += "| $($metrics[$m]) | " + ($cells -join ' | ') + ' |'
    }
    $md | Set-Content "$Out/summary.md"
    Get-Content "$Out/summary.md"
}

# 2. 앱 안쪽 값(상태 칸 수, 스레드 풀, 타이머, 관리 힙): Perf.Ui roles. 구성을 돌려 가며 3라운드(구성별 블록으로 재면 PC 상태 흔들림이 한 구성에 몰린다).
if (-not $SkipRolesUi) {
    foreach ($r in 1..3) {
        for ($pos = 0; $pos -lt $Configs.Count; $pos++) {
            $cfg = Split-Config $Configs[($pos + 2 * ($r - 1)) % $Configs.Count]
            & $ui roles --features $cfg.Value --seconds $IdleS --out "$Out/ui-roles" --label "$($cfg.Name)-r$r-p$($pos + 1)" | Out-Null
        }
    }
}

# 3. DB 세션 수: 미커밋 DML 뒤 앱이 연 세션 수(블로커 모니터 세션 포함). 레인 전용 사용자를 만들고 끝나면 지운다.
if ($env:ORANDA_IT_ADMIN_PASSWORD) {
    foreach ($c in $SessionConfigs) {
        $cfg = Split-Config $c
        foreach ($r in 1..$SessionRuns) { & $ui sessions --features $cfg.Value --seconds $SessionSeconds --out "$Out/ui-sessions" --label "$($cfg.Name)-r$r" | Out-Null }
    }
} else {
    Write-Warning 'ORANDA_IT_ADMIN_PASSWORD 가 없어 DB 세션 수 시나리오(sessions)를 건너뛴다.'
}
