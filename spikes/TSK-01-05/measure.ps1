# TSK-01-05 최종 측정. 측정 잠금을 잡고, 빌드는 미리 끝낸 out/rel 을 쓴다.
# 사용: pwsh -File measure.ps1 [-OutDir results]
param([string]$OutDir = "results")
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $here 'out/rel/EditorSpike.exe'
$lock = "$env:USERPROFILE/.coord/oranda-wp01/locks/measure"
$out = Join-Path $here $OutDir
New-Item -ItemType Directory -Force $out | Out-Null
$log = Join-Path $out 'measure-log.txt'
function Log($m) { $line = "$(Get-Date -Format 'HH:mm:ss') $m"; Add-Content $log $line; Write-Output $line }
function Cpu() { [int]((Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 2 -MaxSamples 3).CounterSamples.CookedValue | Measure-Object -Average).Average }

$locked = $false
for ($i = 0; $i -lt 30; $i++) {
    try { New-Item -ItemType Directory $lock -ErrorAction Stop | Out-Null; $locked = $true; break }
    catch { Log "lock busy, retry $i"; Start-Sleep 60 }
}
if (-not $locked) { Log "잠금 없이 측정" }
try {
    # 다른 레인 빌드로 CPU 가 바쁘면 최대 10분 기다린다(그래도 바쁘면 그대로 측정하고 기록).
    for ($w = 0; $w -lt 20; $w++) { $c = Cpu; Log "cpu before = $c%"; if ($c -lt 35) { break }; Start-Sleep 30 }
    $runs = @(
        @{ name = 'typing-window'; args = @('typing-window', '500', '50') },
        @{ name = 'typing-headless'; args = @('typing', '300', '50') },
        @{ name = 'bench-lexer'; args = @('bench-lexer') },
        @{ name = 'antlr-cold';  args = @('antlr', '5') },
        @{ name = 'bench-antlr'; args = @('bench-antlr') }
    )
    foreach ($r in $runs) {
        $c0 = Cpu
        Log "start $($r.name) cpu=$c0%"
        $f = Join-Path $out "$($r.name).txt"
        $p = Start-Process -FilePath $exe -ArgumentList $r.args -WorkingDirectory $out -RedirectStandardOutput $f -NoNewWindow -PassThru
        try { $p.PriorityClass = 'AboveNormal' } catch {}
        if (-not $p.WaitForExit(15 * 60 * 1000)) { $p.Kill($true); Log "$($r.name) TIMEOUT 15min" }
        $c1 = Cpu
        Log "end $($r.name) exit=$($p.ExitCode) cpu=$c1%"
    }
}
finally {
    if ($locked) { Remove-Item $lock -Force; Log "lock released" }
}
