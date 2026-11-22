using System.Security.Cryptography;
using System.Text;

namespace Oranda.Platform.Ai.Bridge;

/// <summary>
/// Unix 소켓 경로 고르기(설계 agent-cli-control §3.4, A4). sun_path 한도(macOS 104바이트, Linux 108바이트, 끝 NUL 포함)를 넘는 경로는
/// 소켓을 만들 수 없으므로 짧은 대체 경로를 고른다. 고르는 순서:
/// <list type="number">
/// <item>기본 경로(<c>&lt;데이터 폴더&gt;/run/agent[-pid].sock</c>)가 한도 안이면 그것.</item>
/// <item><c>$XDG_RUNTIME_DIR/oranda/</c>(Linux 의 <c>/run/user/&lt;uid&gt;</c>, 0700 이 보장된 사용자 폴더).</item>
/// <item><c>&lt;tmp&gt;/oranda-&lt;uid&gt;/</c>.</item>
/// <item><c>&lt;tmp&gt;/oranda-&lt;uid&gt;-&lt;무작위&gt;/</c> — 앞 폴더를 다른 사용자가 먼저 만들어 두었거나 0700 으로 맞출 수 없을 때.</item>
/// </list>
/// 대체 폴더는 <paramref name="claim"/> 이 「내 것이고 0700 이다」 를 확인한 것만 쓴다. 고른 경로는 연결 파일에 적히므로 <c>orandactl</c> 은 그대로 따른다.
/// </summary>
internal static class AgentSocketPath
{
    /// <summary>NUL 을 뺀 경로 최대 바이트.</summary>
    public static int LimitFor(bool macOS) => macOS ? 103 : 107;

    /// <summary>
    /// 경로 하나를 고른다. 어느 대체 경로도 쓸 수 없으면 기본 경로를 돌려준다(여는 쪽이 실패를 알린다).
    /// </summary>
    /// <param name="preferred">기본 경로.</param>
    /// <param name="tempDirectory">임시 폴더.</param>
    /// <param name="userKey">uid 글.</param>
    /// <param name="instance">두 번째 앱부터 프로세스 id.</param>
    /// <param name="limit">경로 최대 바이트(<see cref="LimitFor"/>).</param>
    /// <param name="getEnvironmentVariable">환경 변수(<c>XDG_RUNTIME_DIR</c>). null 이면 읽지 않는다.</param>
    /// <param name="claim">폴더를 만들거나 이미 있으면 내 것인지 보고 0700 으로 맞춘다. 쓸 수 있으면 참. null 이면 <see cref="PrivateDirectory.TryEnsure"/>.</param>
    /// <param name="randomSuffix">무작위 꼬리. null 이면 8자리 16진수.</param>
    public static string Choose(
        string preferred,
        string tempDirectory,
        string userKey,
        int? instance,
        int limit,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<string, bool>? claim = null,
        Func<string>? randomSuffix = null)
    {
        if (Fits(preferred, limit))
        {
            return preferred;
        }

        claim ??= PrivateDirectory.TryEnsure;
        randomSuffix ??= () => Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var file = instance is { } pid ? $"agent-{pid}.sock" : "agent.sock";

        var folders = new List<string>(3);
        if (getEnvironmentVariable?.Invoke("XDG_RUNTIME_DIR") is { Length: > 0 } runtime && Path.IsPathRooted(runtime))
        {
            folders.Add(Path.Combine(runtime, "oranda"));
        }

        folders.Add(Path.Combine(tempDirectory, "oranda-" + userKey));
        foreach (var folder in folders)
        {
            var path = Path.Combine(folder, file);
            if (Fits(path, limit) && claim(folder))
            {
                return path;
            }
        }

        // 남이 먼저 만든 폴더 이름을 피한다. 무작위 이름도 남의 것이면 몇 번 더 시도한다.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var folder = Path.Combine(tempDirectory, "oranda-" + userKey + "-" + randomSuffix());
            var path = Path.Combine(folder, file);
            if (Fits(path, limit) && claim(folder))
            {
                return path;
            }
        }

        return preferred;
    }

    private static bool Fits(string path, int limit) => Encoding.UTF8.GetByteCount(path) <= limit;
}
