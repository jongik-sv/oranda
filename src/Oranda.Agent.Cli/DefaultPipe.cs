using System.Security.Principal;
using Oranda.Core.Ai.Protocol;

namespace Oranda.Agent.Cli;

/// <summary><c>--pipe</c> 를 생략했을 때 쓰는 고정 이름(첫 앱의 파이프). Windows 만 SID 로 계산할 수 있다.</summary>
internal static class DefaultPipe
{
    public static string? Resolve()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var sid = WindowsIdentity.GetCurrent().User?.Value;
        return sid is null ? null : AgentPipeName.Windows(sid);
    }
}
