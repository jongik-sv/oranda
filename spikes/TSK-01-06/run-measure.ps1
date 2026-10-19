# TSK-01-06 측정 일괄 실행. 빌드 후(Release) 측정 잠금 안에서 실행한다.
#   dotnet build GridSpike -c Release ; ./run-measure.ps1 [-Seconds 10] [-Out results]
param([double]$Seconds = 10, [string]$Out = "$PSScriptRoot/results")
$ErrorActionPreference = 'Stop'
$exe = "$PSScriptRoot/GridSpike/bin/Release/net10.0/GridSpike.exe"
New-Item -ItemType Directory -Force $Out | Out-Null
$runs = @(
    "--mode mem --out $Out/mem.txt",
    "--mode offscreen --text formatted --seconds $Seconds --out $Out/offscreen-formatted.txt",
    "--mode offscreen --text layout --seconds $Seconds --out $Out/offscreen-layout.txt",
    "--mode offscreen --text glyph --seconds $Seconds --out $Out/offscreen-glyph.txt",
    "--mode window --render angle --text formatted --seconds $Seconds --out $Out/window-angle-formatted.txt",
    "--mode window --render angle --text glyph --seconds $Seconds --out $Out/window-angle-glyph.txt",
    "--mode window --render software --text glyph --seconds $Seconds --out $Out/window-software-glyph.txt",
    "--mode window --grid tdg --render software --seconds $Seconds --out $Out/window-software-tdg.txt",
    "--mode window --grid tdg --render angle --seconds $Seconds --out $Out/window-angle-tdg.txt"
)
foreach ($a in $runs) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Start-Process -Wait -FilePath $exe -ArgumentList $a
    "{0,6:F1}s  {1}" -f $sw.Elapsed.TotalSeconds, $a
}
