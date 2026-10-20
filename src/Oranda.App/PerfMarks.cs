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
        Append(FilePath, line);
    }

    /// <summary>
    /// 표식 한 줄을 쓴다. 표식은 진단용이므로 쓰기에 실패하면 그 표식만 버린다 (TSK-01-10, DEF-03:
    /// 측정 도구가 같은 파일을 쓰기 공유 없이 열면 IOException 이 나서 처리되지 않은 예외로 앱이 종료됐다).
    /// </summary>
    internal static void Append(string? filePath, string line)
    {
        try
        {
            if (!string.IsNullOrEmpty(filePath))
            {
                File.AppendAllText(filePath, line + Environment.NewLine);
            }
            else
            {
                Console.Out.WriteLine(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 표식을 못 남겨도 앱은 계속 쓴다.
        }
    }
}
