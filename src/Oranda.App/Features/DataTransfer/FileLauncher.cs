using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Oranda.App.Features.DataTransfer;

/// <summary>파일을 운영체제 기본 앱으로 연다(macOS <c>open</c>, Windows ShellExecute, 그 밖은 <c>xdg-open</c>).</summary>
internal static class FileLauncher
{
    /// <summary>열기를 시작했으면 true. 연결된 앱이 없거나 시작하지 못하면 false.</summary>
    internal static bool Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            var info = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(path) { UseShellExecute = true }
                : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false, ArgumentList = { path } };
            using var process = Process.Start(info);
            return process is not null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
