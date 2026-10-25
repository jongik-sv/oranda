# 게시 폴더에 라이선스·외부 고지 넣기 (TSK-08-01·03). vpk pack 전에 CI 가 부른다. Windows·macOS 공용(pwsh).
# Oracle Free Use Terms 는 배포본마다 라이선스 사본을 넣으라고 한다(THIRD-PARTY-NOTICES.md 부록 A).
# Skia·HarfBuzz 네이티브 고지는 길어서 저장소에 옮기지 않고 NuGet 캐시의 원문을 그대로 넣는다.
# 사용: copy-notices.ps1 -PublishDir artifacts/publish/win-x64 -Os win
param(
    [Parameter(Mandatory)][string]$PublishDir,
    [Parameter(Mandatory)][ValidateSet('win', 'osx')][string]$Os,
    [string]$AssetsFile = 'src/Oranda.App/obj/project.assets.json'
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot/../.."
$out = Join-Path $PublishDir 'licenses'
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $out 'LICENSE.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $out

# 실제로 복원된 버전을 project.assets.json 에서 읽는다(버전을 여기 적어 두면 패키지를 올릴 때 어긋난다).
$assets = Get-Content (Join-Path $root $AssetsFile) -Raw | ConvertFrom-Json
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { $assets.project.restore.packagesPath }
if (-not $cache) { $cache = Join-Path $HOME '.nuget/packages' }
$native = if ($Os -eq 'win') { 'Win32' } else { 'macOS' }
$copied = @()
foreach ($name in "SkiaSharp.NativeAssets.$native", "HarfBuzzSharp.NativeAssets.$native") {
    $lib = $assets.libraries.PSObject.Properties | Where-Object { $_.Name -like "$name/*" } | Select-Object -First 1
    if (-not $lib) { throw "$name 이 $AssetsFile 에 없다(복원 먼저)" }
    $src = Join-Path $cache ($lib.Value.path) 'THIRD-PARTY-NOTICES.txt'
    if (-not (Test-Path $src)) { throw "고지 파일 없음: $src" }
    # 8.3.1.3 기준 HarfBuzzSharp 고지는 SkiaSharp 고지와 같은 파일이다. 같으면 한 벌만 둔다.
    $hash = (Get-FileHash $src).Hash
    if ($hash -in $copied) { Write-Host "고지: $name 은 앞 파일과 같아 건너뜀"; continue }
    $copied += $hash
    $short = $name.Split('.')[0]
    Copy-Item $src (Join-Path $out "$short-THIRD-PARTY-NOTICES.txt")
}
Get-ChildItem $out | ForEach-Object { Write-Host "고지: $($_.Name) $($_.Length) bytes" }
