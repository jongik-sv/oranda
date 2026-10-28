using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Oranda.Platform.Ai.Bridge;

/// <summary>현재 OS 사용자를 가리키는 값. 파이프 이름을 사용자마다 달리하는 데 쓴다(비밀 아님).</summary>
internal static class CurrentUser
{
    /// <summary>Windows 는 사용자 SID 글, 그 밖은 uid 숫자 글.</summary>
    public static string GetKey()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("현재 사용자 SID 를 얻지 못했습니다.");
        }

        return GetUid().ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();
}
