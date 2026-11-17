using System.Diagnostics;
using System.Globalization;

namespace Oranda.App.Features.TableEdit;

/// <summary>
/// 새 테이블 저장분을 쥔 앱 인스턴스 표기(<c>table_draft.open_by</c>, 설계 18.7). <c>&lt;프로세스 id&gt;@&lt;시작 시각 유닉스 초&gt;</c> —
/// 프로세스 id 가 다시 쓰여도 시작 시각이 달라 끝난 인스턴스로 본다. 앱 두 개가 같은 <c>oranda.db</c> 를 써도 살아 있는 쪽의 저장분은 건드리지 않는다.
/// </summary>
public static class TableDraftInstance
{
    /// <summary>시작 시각 비교 허용 오차(초). OS 가 돌려주는 시작 시각은 부팅 시각 계산에 따라 조금 흔들릴 수 있다.</summary>
    private const long ToleranceSeconds = 2;

    /// <summary>이 프로세스.</summary>
    public static string Current { get; } = CreateCurrent();

    /// <summary>그 인스턴스가 아직 돌고 있나. 이 프로세스면 참, 모르는 꼴·없는 프로세스·시작 시각이 다르면 거짓.</summary>
    public static bool IsAlive(string? instance)
    {
        if (instance is null || !TryParse(instance, out var pid, out var started))
        {
            return false;
        }

        if (instance == Current)
        {
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return Math.Abs(StartSeconds(process) - started) <= ToleranceSeconds;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false; // 없는 프로세스·읽을 수 없는 프로세스(다른 사용자 = 이 앱이 아님)
        }
    }

    internal static string Format(int pid, long startSeconds)
        => pid.ToString(CultureInfo.InvariantCulture) + "@" + startSeconds.ToString(CultureInfo.InvariantCulture);

    private static bool TryParse(string instance, out int pid, out long started)
    {
        started = 0;
        var at = instance.IndexOf('@', StringComparison.Ordinal);
        pid = 0;
        return at > 0
            && int.TryParse(instance.AsSpan(0, at), NumberStyles.None, CultureInfo.InvariantCulture, out pid)
            && long.TryParse(instance.AsSpan(at + 1), NumberStyles.None, CultureInfo.InvariantCulture, out started);
    }

    private static string CreateCurrent()
    {
        using var process = Process.GetCurrentProcess();
        long start;
        try
        {
            start = StartSeconds(process);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            start = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        return Format(Environment.ProcessId, start);
    }

    private static long StartSeconds(Process process) => new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds();
}
