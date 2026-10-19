# 측정 잠금을 잡고(mkdir) measure.ps1 을 돌린 뒤 푼다. 1분 간격 최대 30회 재시도, 넘으면 잠금 없이 측정.
param([string[]]$Cases, [int]$Runs = 5, [int]$Warmup = 1)
$lock = 'C:/Users/jongik.jang/.coord/oranda-wp01/locks/measure'
$got = $false
for ($i = 0; $i -lt 30; $i++) {
    try { New-Item -ItemType Directory $lock -ErrorAction Stop | Out-Null; $got = $true; break } catch { Start-Sleep 60 }
}
Write-Host ("lock={0} at {1}" -f $got, (Get-Date -Format s))
try {
    $cpu = (Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 2 -MaxSamples 3).CounterSamples.CookedValue
    Write-Host ("cpu before = {0}" -f (($cpu | ForEach-Object { [math]::Round($_) }) -join ','))
    & (Join-Path $PSScriptRoot 'measure.ps1') -Cases $Cases -Runs $Runs -Warmup $Warmup
}
finally {
    if ($got) { Remove-Item $lock -Force; Write-Host "lock released $(Get-Date -Format s)" }
}
