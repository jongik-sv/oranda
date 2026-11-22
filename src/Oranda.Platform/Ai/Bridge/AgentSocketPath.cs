using System.Text;

namespace Oranda.Platform.Ai.Bridge;

/// <summary>
/// Unix 소켓 경로 고르기(설계 agent-cli-control §3.4, A4). sun_path 한도(macOS 104바이트, Linux 108바이트, 끝 NUL 포함)를 넘는 경로는
/// 소켓을 만들 수 없으므로 임시 폴더 아래 짧은 경로 <c>&lt;tmp&gt;/oranda-&lt;uid&gt;/agent[-pid].sock</c> 를 쓴다(폴더는 다리가 0700 으로 만든다).
/// </summary>
internal static class AgentSocketPath
{
    /// <summary>NUL 을 뺀 경로 최대 바이트.</summary>
    public static int LimitFor(bool macOS) => macOS ? 103 : 107;

    /// <summary>기본 경로가 한도 안이면 그대로, 넘으면 짧은 경로. 짧은 경로도 넘으면 기본 경로를 돌려준다(여는 쪽이 실패를 알린다).</summary>
    public static string Choose(string preferred, string tempDirectory, string userKey, int? instance, int limit)
    {
        if (Fits(preferred, limit))
        {
            return preferred;
        }

        var file = instance is { } pid ? $"agent-{pid}.sock" : "agent.sock";
        var shortPath = Path.Combine(tempDirectory, "oranda-" + userKey, file);
        return Fits(shortPath, limit) ? shortPath : preferred;
    }

    private static bool Fits(string path, int limit) => Encoding.UTF8.GetByteCount(path) <= limit;
}
