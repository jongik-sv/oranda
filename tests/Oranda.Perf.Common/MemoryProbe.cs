using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Oranda.Perf.Common;

/// <summary>프로세스 메모리를 한 번 읽은 값(MB). 읽을 수 없는 칸은 null.</summary>
/// <param name="PrivateMb">「전용 메모리」 판정 값. Windows = 전용 작업 집합(작업 관리자의 「메모리(활성 개인 작업 집합)」), macOS = phys_footprint.</param>
/// <param name="CommitMb">Windows 전용 바이트(커밋). 다른 OS 는 null.</param>
/// <param name="WorkingSetMb">작업 집합 전체(공유 포함).</param>
public sealed record MemorySample(double? PrivateMb, double? CommitMb, double? WorkingSetMb);

public static partial class MemoryProbe
{
    /// <summary>실행 중인 프로세스의 메모리를 읽는다. 프로세스가 없거나 읽기 실패면 null 칸으로 돌려준다(예외를 던지지 않는다).</summary>
    public static MemorySample Read(int pid)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return ReadWindows(pid);
            }

            if (OperatingSystem.IsMacOS())
            {
                return ReadMacOs(pid);
            }

            using var p = Process.GetProcessById(pid);
            return new MemorySample(null, null, p.WorkingSet64 / 1048576.0);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return new MemorySample(null, null, null);
        }
    }

    // ---- Windows ----

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2
    {
        public uint Cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(nint process, ref ProcessMemoryCountersEx2 counters, uint size);

    private static MemorySample ReadWindows(int pid)
    {
        using var p = Process.GetProcessById(pid);
        var c = new ProcessMemoryCountersEx2 { Cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>() };
        if (!GetProcessMemoryInfo(p.Handle, ref c, c.Cb))
        {
            return new MemorySample(null, null, p.WorkingSet64 / 1048576.0);
        }

        return new MemorySample(c.PrivateWorkingSetSize / 1048576.0, c.PrivateUsage / 1048576.0, c.WorkingSetSize / 1048576.0);
    }

    // ---- macOS ----

    private static MemorySample ReadMacOs(int pid)
    {
        var text = RunTool("footprint", $"-p {pid.ToString(CultureInfo.InvariantCulture)}");
        var footprint = text is null ? null : ParseFootprint(text);
        using var p = Process.GetProcessById(pid);
        return new MemorySample(footprint, null, p.WorkingSet64 / 1048576.0);
    }

    [GeneratedRegex(@"Footprint:\s*([0-9]+(?:\.[0-9]+)?)\s*(B|KB|MB|GB)\b", RegexOptions.CultureInvariant)]
    private static partial Regex FootprintLine();

    /// <summary>macOS <c>footprint -p PID</c> 출력에서 합계 줄(<c>Footprint: 131 MB</c>)을 MB 로 읽는다. 없으면 null.</summary>
    public static double? ParseFootprint(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var m = FootprintLine().Match(output);
        if (!m.Success)
        {
            return null;
        }

        var value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch
        {
            "B" => value / 1048576.0,
            "KB" => value / 1024.0,
            "GB" => value * 1024.0,
            _ => value,
        };
    }

    private static string? RunTool(string file, string arguments)
    {
        var psi = new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return p.ExitCode == 0 ? text : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
