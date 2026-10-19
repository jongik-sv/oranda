using System.Diagnostics;
using System.Globalization;

namespace Oranda.App;

/// <summary>
/// 시작 측정 표식 (TSK-01-07). 환경 변수 ORANDA_PERF 가 없으면 분기 한 번뿐이다.
/// 출력: "ORANDA_PERF stage=&lt;이름&gt; ms=&lt;프로세스 시작 후 경과&gt;" 한 줄.
/// ORANDA_PERF_FILE 이 있으면 그 파일에 추가하고(WinExe 는 stdout 이 막힐 수 있음), 없으면 stdout.
/// </summary>
internal static class PerfMarks
{
    private static readonly string? FilePath = Environment.GetEnvironmentVariable("ORANDA_PERF_FILE");
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("ORANDA_PERF") == "1" || !string.IsNullOrEmpty(FilePath);

    public static void Mark(string stage)
    {
        if (!Enabled)
        {
            return;
        }

        var ms = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        var line = string.Create(CultureInfo.InvariantCulture, $"ORANDA_PERF stage={stage} ms={ms:F1}");
        if (!string.IsNullOrEmpty(FilePath))
        {
            File.AppendAllText(FilePath, line + Environment.NewLine);
        }
        else
        {
            Console.Out.WriteLine(line);
        }
    }
}
