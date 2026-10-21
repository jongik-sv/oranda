# Windows 설치 파일 생성. Velopack(vpk) 1.2.161. 사용: pack-windows.ps1 -Version 1.0.0 [-SignParams "..."] [-AzureSignFile metadata.json] [-FeedUrl https://...]
# 서명(TSK-10-08): -SignParams 는 vpk 에 딸린 signtool.exe 매개변수, -AzureSignFile 은 Azure Trusted Signing 메타데이터. 방법은 README 「코드 서명」.
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$SignParams = $env:ORANDA_SIGN_PARAMS,
    [string]$AzureSignFile = $env:ORANDA_AZURE_SIGN_FILE,
    [string]$FeedUrl,
    [string]$OutDir = 'artifacts/release'
)
$ErrorActionPreference = 'Stop'
dotnet publish src/Oranda.App -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o artifacts/publish/win-x64
if ($FeedUrl) { vpk download http --url $FeedUrl --outputDir $OutDir }  # 델타 생성용 이전 버전
# --runtime 이 없으면 vpk 가 대상 아키텍처를 x86 으로 가정한다(경고만 낸다). 게시본의 *.pdb 는 csproj 가 빼고, vpk 도 기본으로 뺀다(--exclude).
$args = @('pack', '--packId', 'Oranda', '--packVersion', $Version, '--runtime', 'win-x64', '--packDir', 'artifacts/publish/win-x64', '--mainExe', 'Oranda.App.exe', '--outputDir', $OutDir)
# vpk 는 패키지 안의 exe·dll 과 Setup.exe·Update.exe 를 모두 서명한다. composite R2R 이 Microsoft·Oracle dll 의 원래 서명을 지우므로 이 서명이 유일하다.
$signed = $true
if ($SignParams) { $args += @('--signParams', $SignParams) }  # 비밀값(Authenticode 인증서)은 사람이 CI 비밀로 제공
elseif ($AzureSignFile) { $args += @('--azureTrustedSignFile', $AzureSignFile) }
else { $signed = $false; Write-Warning '서명 없이 패키징한다(ORANDA_SIGN_PARAMS·ORANDA_AZURE_SIGN_FILE 없음). 배포용 아님.' }
vpk @args
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& "$PSScriptRoot/check-signatures.ps1" -Dir $OutDir -RequireSigned:$signed
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& "$PSScriptRoot/check-installer-size.ps1" -Dir $OutDir
