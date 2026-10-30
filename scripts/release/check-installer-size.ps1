# 설치 파일 100 MB 이하 검사(D-035).
param([string]$Dir = 'artifacts/release', [long]$MaxBytes = 104857600)
$bad = Get-ChildItem $Dir -File -Include '*Setup*.exe', '*.pkg' -Recurse | Where-Object { $_.Length -gt $MaxBytes }
$bad | ForEach-Object { Write-Error "$($_.Name) $($_.Length) bytes > $MaxBytes" }
if ($bad) { exit 1 }
