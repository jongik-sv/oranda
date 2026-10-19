# TSK-01-04 시작 단계 측정. 게시본을 N회 띄워 ORANDA_PERF 표식을 모으고 중앙값을 낸다.
# 사용: pwsh spikes/TSK-01-04/measure.ps1 -Cases r2r,jit,... [-Runs 5] [-Warmup 1]
# 사례 = 게시 변형(out/<변형>) + 실행 환경 변수. 결과 = results/runs.csv(실행별), results/summary.md(중앙값)
param(
    [string[]]$Cases = @('jit', 'r2r', 'r2r-composite', 'r2r-trim', 'r2r-nointer', 'r2r-eager-oracle'),
    [int]$Runs = 5,
    [int]$Warmup = 1
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$resDir = Join-Path $here 'results'
New-Item -ItemType Directory -Force $resDir | Out-Null

# 사례 정의: 변형 폴더, 추가 환경 변수
$defs = @{
    'jit'              = @{ dir = 'jit'; env = @{} }
    'r2r'              = @{ dir = 'r2r'; env = @{} }
    'r2r-composite'    = @{ dir = 'r2r-composite'; env = @{} }
    'r2r-trim'         = @{ dir = 'r2r-trim'; env = @{} }
    'r2r-nointer'      = @{ dir = 'r2r'; env = @{ ORANDA_PROBE_FONT = 'none' } }
    'r2r-eager-oracle' = @{ dir = 'r2r'; env = @{ ORANDA_PROBE_ORACLE = 'eager' } }
    'r2r-trim-nointer' = @{ dir = 'r2r-trim'; env = @{ ORANDA_PROBE_FONT = 'none' } }
    'r2r-mainonly'     = @{ dir = 'r2r'; env = @{ ORANDA_PROBE_EXIT_AT_MAIN = '1' } }
    'jit-mainonly'     = @{ dir = 'jit'; env = @{ ORANDA_PROBE_EXIT_AT_MAIN = '1' } }
    'r2r-composite-nointer' = @{ dir = 'r2r-composite'; env = @{ ORANDA_PROBE_FONT = 'none' } }
    'r2r-software'     = @{ dir = 'r2r'; env = @{ ORANDA_PROBE_RENDER = 'software' } }
    'r2r-software-nointer' = @{ dir = 'r2r'; env = @{ ORANDA_PROBE_RENDER = 'software'; ORANDA_PROBE_FONT = 'none' } }
    'r2r-composite-software-nointer' = @{ dir = 'r2r-composite'; env = @{ ORANDA_PROBE_RENDER = 'software'; ORANDA_PROBE_FONT = 'none' } }
    'hello-floor'      = @{ dir = 'hello'; exe = 'HelloFloor.exe'; env = @{} }
    'hello-winexe'     = @{ dir = 'hello-winexe'; exe = 'HelloFloor.exe'; env = @{} }
    'hello-manifest'   = @{ dir = 'hello-manifest'; exe = 'HelloFloor.exe'; env = @{} }
    'hello-deps'       = @{ dir = 'hello-deps'; exe = 'HelloFloor.exe'; env = @{} }
    'r2r-composite-nosta' = @{ dir = 'r2r-composite-nosta'; env = @{} }
    'r2r-redirection'  = @{ dir = 'r2r'; env = @{ ORANDA_PROBE_COMPOSITION = 'redirection' } }
    'r2r-composite-redirection' = @{ dir = 'r2r-composite'; env = @{ ORANDA_PROBE_COMPOSITION = 'redirection' } }
    'r2r-usenls'       = @{ dir = 'r2r'; env = @{ DOTNET_SYSTEM_GLOBALIZATION_USENLS = '1' } }
    'r2r-invariant'    = @{ dir = 'r2r'; env = @{ DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1' } }
    'r2r-composite-usenls' = @{ dir = 'r2r-composite'; env = @{ DOTNET_SYSTEM_GLOBALIZATION_USENLS = '1' } }
    'r2r-composite-usenls-redirection' = @{ dir = 'r2r-composite'; env = @{ DOTNET_SYSTEM_GLOBALIZATION_USENLS = '1'; ORANDA_PROBE_COMPOSITION = 'redirection' } }
    'r2r-nopgo'    = @{ dir = 'r2r'; env = @{ DOTNET_TieredPGO = '0' } }
    'aot'              = @{ dir = 'aot'; env = @{} }
}
$stages = 'module-init', 'main', 'appbuilder', 'app-init', 'app-xaml', 'setup', 'oracle-loaded', 'window-created', 'window-opened', 'first-render', 'first-frame', 'input-ready'

function Invoke-Once($exe, $envs) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new($exe)
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.WorkingDirectory = Split-Path $exe
    $psi.Environment['ORANDA_PERF'] = '1'
    $psi.Environment['ORANDA_PERF_EXIT'] = '1'
    foreach ($k in $envs.Keys) { $psi.Environment[$k] = $envs[$k] }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($psi)
    $r = @{}
    while ($null -ne ($line = $p.StandardOutput.ReadLine())) {
        if ($line -match '^ORANDA_PERF stage=(\S+) ms=([\d.]+)') {
            $r[$Matches[1]] = [double]$Matches[2]
            if ($Matches[1] -eq 'input-ready') { $r['wall-input-ready'] = $sw.Elapsed.TotalMilliseconds }
        }
        elseif ($line -match '^ORANDA_PERF mem private=(\d+) ws=(\d+)') {
            $r['privateMB'] = [math]::Round([double]$Matches[1] / 1MB, 1)
            $r['wsMB'] = [math]::Round([double]$Matches[2] / 1MB, 1)
        }
    }
    if (-not $p.WaitForExit(30000)) { $p.Kill(); throw "timeout: $exe" }
    return $r
}

function Median($xs) {
    $s = @($xs | Where-Object { $null -ne $_ } | Sort-Object)
    if ($s.Count -eq 0) { return $null }
    if ($s.Count % 2) { return $s[[int][math]::Floor($s.Count / 2)] }
    return ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2
}

$csv = Join-Path $resDir 'runs.csv'
$cols = @('case', 'run') + $stages + @('wall-input-ready', 'privateMB', 'wsMB')
if (-not (Test-Path $csv)) { ($cols -join ',') | Set-Content $csv }

$summary = @()
foreach ($c in $Cases) {
    $d = $defs[$c]
    $exeName = if ($d.exe) { $d.exe } else { 'Oranda.App.exe' }
    $exe = Join-Path $here "out/$($d.dir)/$exeName"
    if (-not (Test-Path $exe)) { Write-Warning "없음: $exe"; continue }
    for ($i = 0; $i -lt $Warmup; $i++) { Invoke-Once $exe $d.env | Out-Null }
    $all = @()
    for ($i = 1; $i -le $Runs; $i++) {
        $r = Invoke-Once $exe $d.env
        $all += $r
        (@($c, $i) + ($cols[2..($cols.Count - 1)] | ForEach-Object { $r[$_] })) -join ',' | Add-Content $csv
        Start-Sleep -Milliseconds 500
    }
    $row = [ordered]@{ case = $c }
    foreach ($k in $cols[2..($cols.Count - 1)]) { $row[$k] = Median ($all | ForEach-Object { $_[$k] }) }
    $summary += [pscustomobject]$row
    Write-Host ("{0,-18} main={1} setup={2} window={3} first-render={4} first-frame={5} input-ready={6} wall={7} private={8}MB" -f `
            $c, $row['main'], $row['setup'], $row['window-created'], $row['first-render'], $row['first-frame'], $row['input-ready'], `
            ([math]::Round($row['wall-input-ready'], 1)), $row['privateMB'])
}

$md = Join-Path $resDir 'summary.md'
$head = '| 사례 | ' + (($cols[2..($cols.Count - 1)]) -join ' | ') + ' |'
$sep = '|' + ('---|' * ($cols.Count - 1))
$lines = @("<!-- $(Get-Date -Format s) runs=$Runs warmup=$Warmup (ms = 프로세스 StartTime 기준, 중앙값) -->", $head, $sep)
foreach ($s in $summary) {
    $vals = $cols[2..($cols.Count - 1)] | ForEach-Object { $v = $s.$_; if ($null -eq $v) { '-' } else { [math]::Round($v, 1) } }
    $lines += "| $($s.case) | " + ($vals -join ' | ') + ' |'
}
$lines | Add-Content $md
