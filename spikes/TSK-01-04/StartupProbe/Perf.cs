using System.Diagnostics;
using System.Globalization;

namespace Oranda.App;

/// <summary>
/// 시작 단계 표식. ORANDA_PERF=1 일 때만 stdout 에
/// <c>ORANDA_PERF stage=&lt;이름&gt; ms=&lt;프로세스 StartTime 기준 ms&gt;</c> 를 쓴다
/// (l0107-ci 의 App 훅과 같은 규약. 단계 이름 appbuilder·window-created·first-render 는 공통, 나머지는 이 스파이크 추가분).
/// </summary>
internal static class Perf
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("ORANDA_PERF") == "1";
    public static readonly bool ExitWhenReady = Environment.GetEnvironmentVariable("ORANDA_PERF_EXIT") == "1";
    public static readonly bool NoInterFont = Environment.GetEnvironmentVariable("ORANDA_PROBE_FONT") == "none";
    public static readonly bool EagerOracle = Environment.GetEnvironmentVariable("ORANDA_PROBE_ORACLE") == "eager";

    private static DateTime? _start;

    public static void Mark(string stage)
    {
        if (!Enabled)
        {
            return;
        }

        _start ??= Process.GetCurrentProcess().StartTime.ToUniversalTime();
        var ms = (DateTime.UtcNow - _start.Value).TotalMilliseconds;
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"ORANDA_PERF stage={stage} ms={ms:F1}"));
        Console.Out.Flush();
    }

    public static void Memory()
    {
        if (!Enabled)
        {
            return;
        }

        using var p = Process.GetCurrentProcess();
        p.Refresh();
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"ORANDA_PERF mem private={p.PrivateMemorySize64} ws={p.WorkingSet64}"));
        Console.Out.Flush();
    }
}
