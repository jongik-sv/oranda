# TSK-01-04 게시 변형 만들기. 결과 = out/<변형>/Oranda.App.exe, 로그 = out/<변형>.log
# 사용: pwsh spikes/TSK-01-04/publish.ps1 [-Only jit,r2r,...]
param([string[]]$Only)

$ErrorActionPreference = 'Stop'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$here = $PSScriptRoot
$proj = Join-Path $here 'StartupProbe/StartupProbe.csproj'
$outRoot = Join-Path $here 'out'

# 공통: Release, self-contained, win-x64 (csproj 에 RID·SelfContained 고정)
$variants = [ordered]@{
    'jit'           = @('-p:PublishReadyToRun=false')
    'r2r'           = @('-p:PublishReadyToRun=true')
    'r2r-composite' = @('-p:PublishReadyToRun=true', '-p:PublishReadyToRunComposite=true')
    'r2r-trim'      = @('-p:PublishReadyToRun=true', '-p:PublishTrimmed=true')
    'r2r-composite-nosta' = @('-p:PublishReadyToRun=true', '-p:PublishReadyToRunComposite=true', '-p:DefineConstants=NOSTA')
    'aot'           = @('-p:PublishAot=true')
}

foreach ($name in $variants.Keys) {
    if ($Only -and $name -notin $Only) { continue }
    $out = Join-Path $outRoot $name
    $log = Join-Path $outRoot "$name.log"
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    New-Item -ItemType Directory -Force $outRoot | Out-Null
    # 변형 사이 증분 산출물이 섞이지 않게(합성 R2R 뒤 일반 R2R 이 건너뛰어진 적 있음) 매번 obj·bin 을 지운다.
    Remove-Item -Recurse -Force (Join-Path $here 'StartupProbe/obj'), (Join-Path $here 'StartupProbe/bin') -ErrorAction SilentlyContinue
    Write-Host "== publish $name"
    & $dotnet publish $proj -c Release -o $out -nologo -clp:NoSummary @($variants[$name]) *> $log
    $code = $LASTEXITCODE
    $warn = Select-String -Path $log -Pattern 'warning (IL\d{4})' -AllMatches |
        ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value }
    $uniq = Select-String -Path $log -Pattern 'warning IL\d{4}.*' | ForEach-Object { $_.Matches[0].Value } | Sort-Object -Unique
    $byCode = $uniq | ForEach-Object { if ($_ -match 'warning (IL\d{4})') { $Matches[1] } } | Group-Object | Sort-Object Name |
        ForEach-Object { "$($_.Name)=$($_.Count)" }
    $size = if (Test-Path $out) { [math]::Round(((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1) } else { 0 }
    $files = if (Test-Path $out) { (Get-ChildItem $out -Recurse -File).Count } else { 0 }
    Write-Host ("   exit={0} sizeMB={1} files={2} ILwarnings(unique)={3} [{4}]" -f $code, $size, $files, $uniq.Count, ($byCode -join ' '))
}
