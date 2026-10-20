# Windows 설치 파일 생성. Velopack(vpk) 1.2.161. 사용: pack-windows.ps1 -Version 1.0.0 [-SignParams "/f cert.pfx /p ..."] [-FeedUrl https://...]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$SignParams = $env:ORANDA_SIGN_PARAMS,
    [string]$FeedUrl,
    [string]$OutDir = 'artifacts/release'
)
$ErrorActionPreference = 'Stop'
dotnet publish src/Oranda.App -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o artifacts/publish/win-x64
if ($FeedUrl) { vpk download http --url $FeedUrl --outputDir $OutDir }  # 델타 생성용 이전 버전
# --runtime 이 없으면 vpk 가 대상 아키텍처를 x86 으로 가정한다(경고만 낸다). 게시본의 *.pdb 는 csproj 가 빼고, vpk 도 기본으로 뺀다(--exclude).
$args = @('pack', '--packId', 'Oranda', '--packVersion', $Version, '--runtime', 'win-x64', '--packDir', 'artifacts/publish/win-x64', '--mainExe', 'Oranda.App.exe', '--outputDir', $OutDir)
if ($SignParams) { $args += @('--signParams', $SignParams) }  # 비밀값(Authenticode 인증서)은 사람이 CI 비밀로 제공
else { Write-Warning '서명 없이 패키징한다(ORANDA_SIGN_PARAMS 없음). 배포용 아님.' }
vpk @args
& "$PSScriptRoot/check-installer-size.ps1" -Dir $OutDir
