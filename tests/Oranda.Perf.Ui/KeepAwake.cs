using System.Runtime.InteropServices;

namespace Oranda.Perf.Ui;

/// <summary>
/// 측정 중 화면이 꺼지거나 절전에 들지 않게 한다(Windows). 화면 잠금(자리 비움 시간 초과)은 막지 못한다 — 잠기면 창이 가려진 것으로 처리되어
/// 프레임이 초당 1번으로 느려진다. 그래서 <see cref="UiHost"/> 가 시작할 때 프레임 속도를 확인하고 느리면 중단한다.
/// macOS 는 스크립트가 <c>caffeinate -dimsu</c> 로 감싼다.
/// </summary>
internal static partial class KeepAwake
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);

    public static void Enable()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);
        }
    }
}
