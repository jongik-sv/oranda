using System.Security.Principal;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Storage;

namespace Oranda.Agent.Cli;

/// <summary>
/// <c>--pipe</c> 를 생략했을 때 쓰는 고정 이름(첫 앱의 통로). Windows 는 SID 해시 파이프 이름,
/// macOS·Linux 는 데이터 폴더의 <c>run/agent.sock</c>(앱의 <c>AgentBridgeHost</c> 와 같은 <see cref="AgentPipeName.Unix"/>).
/// </summary>
internal static class DefaultPipe
{
    public static string? Resolve() => Resolve(Environment.GetEnvironmentVariable, AppDataLocation.CurrentOs);

    public static string? Resolve(Func<string, string?> getEnvironmentVariable, AppOs os)
    {
        if (os != AppOs.Windows)
        {
            return AgentPipeName.Unix(AppDataLocation.Resolve(getEnvironmentVariable, os));
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var sid = WindowsIdentity.GetCurrent().User?.Value;
        return sid is null ? null : AgentPipeName.Windows(sid);
    }
}
