# 설치 파일·Portable zip 안의 exe·dll Authenticode 서명 검사 (TSK-10-08).
# -RequireSigned: 무서명·손상 파일이 하나라도 있으면 실패. 없으면 개수만 경고한다.
# -RequireTrusted: 이 PC 가 신뢰하는 서명(Valid)만 통과. 자체 서명 시험 인증서는 UnknownError 라서 이 옵션 없이 검사한다.
param(
    [string]$Dir = 'artifacts/release',
    [switch]$RequireSigned,
    [switch]$RequireTrusted
)
$ErrorActionPreference = 'Stop'
$files = @(Get-ChildItem $Dir -File -Filter '*Setup*.exe')
$zip = Get-ChildItem $Dir -File -Filter '*Portable.zip' | Select-Object -First 1
$tmp = $null
if ($zip) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("oranda-sig-" + [guid]::NewGuid().ToString('N'))
    Expand-Archive $zip.FullName $tmp
    $files += Get-ChildItem $tmp -Recurse -File -Include '*.exe', '*.dll'
}
try {
    $rows = foreach ($f in $files) {
        $s = Get-AuthenticodeSignature $f.FullName
        $rel = if ($tmp -and $f.FullName.StartsWith($tmp)) { $zip.Name + ':' + $f.FullName.Substring($tmp.Length + 1) } else { $f.Name }
        [pscustomobject]@{ File = $rel; Status = "$($s.Status)"; Timestamped = [bool]$s.TimeStamperCertificate }
    }
    $unsigned = @($rows | Where-Object { $_.Status -in 'NotSigned', 'HashMismatch', 'NotSupportedFileFormat' })
    $untrusted = @($rows | Where-Object { $_.Status -ne 'Valid' -and $_ -notin $unsigned })
    $noStamp = @($rows | Where-Object { $_ -notin $unsigned -and -not $_.Timestamped })
    Write-Host "서명 검사: 파일 $($rows.Count)개, 무서명 $($unsigned.Count)개, 신뢰 안 됨 $($untrusted.Count)개, 타임스탬프 없음 $($noStamp.Count)개"
    $unsigned | Select-Object -First 20 | ForEach-Object { Write-Host "  무서명: $($_.File) ($($_.Status))" }
    if ($unsigned.Count -gt 20) { Write-Host "  … 외 $($unsigned.Count - 20)개" }
    $fail = $false
    if ($unsigned.Count) {
        if ($RequireSigned -or $RequireTrusted) { Write-Error "무서명 파일 $($unsigned.Count)개" -ErrorAction Continue; $fail = $true }
        else { Write-Warning "무서명 파일 $($unsigned.Count)개 — 스마트 앱 컨트롤·WDAC 가 켜진 PC 에서 실행되지 않을 수 있다" }
    }
    if ($RequireTrusted -and $untrusted.Count) {
        $untrusted | Select-Object -First 20 | ForEach-Object { Write-Host "  신뢰 안 됨: $($_.File) ($($_.Status))" }
        Write-Error "신뢰되지 않는 서명 $($untrusted.Count)개" -ErrorAction Continue; $fail = $true
    }
    if ($fail) { exit 1 }
}
finally {
    if ($tmp) { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}
