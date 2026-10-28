using System.Security.Cryptography;
using System.Text;

namespace Oranda.Core.Ai.Protocol;

/// <summary>
/// <c>orandactl</c> 과 앱의 <c>AgentBridgeHost</c> 가 만나는 통로의 이름 규칙(연구서 §4.1, 조정 결정 2026-10-28). 순수 함수만 둔다 —
/// 현재 사용자 SID·uid·홈 폴더를 읽는 일, 폴더 권한(0700), 소켓 권한은 호출하는 쪽(ai-bridge) 몫이다(Core 는 플랫폼 API 를 부르지 않는다).
/// 첫 앱은 고정 이름(<paramref name="instance"/> = null)을 열고, 고정 이름이 이미 쓰이면 다음 앱은 <c>&lt;고정 이름&gt;-&lt;pid&gt;</c> 를 연다.
/// </summary>
public static class AgentPipeName
{
    private const string Prefix = "oranda-agent-";

    /// <summary>Windows 명명 파이프 이름(<c>\\.\pipe\</c> 뒤 부분). 사용자 SID 문자열의 SHA-256 앞 16자리 16진수라 사용자마다 다르고 SID 는 드러나지 않는다.</summary>
    /// <param name="userSid">현재 사용자 SID 문자열(<c>S-1-5-21-…</c>).</param>
    /// <param name="instance">첫 앱이면 null, 아니면 그 앱의 프로세스 id.</param>
    public static string Windows(string userSid, int? instance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(userSid.Trim().ToUpperInvariant()));
        var name = Prefix + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        return instance is { } pid ? $"{name}-{pid}" : name;
    }

    /// <summary>
    /// macOS Unix 도메인 소켓의 절대 경로: <c>&lt;appSupportDir&gt;/run/agent.sock</c>, 다음 앱은 <c>agent-&lt;pid&gt;.sock</c>.
    /// <paramref name="appSupportDir"/> 는 이미 펼친 <c>~/Library/Application Support/Oranda</c> 절대 경로다.
    /// sun_path 한도(104바이트)를 넘으면 호출하는 쪽이 짧은 경로로 바꿔야 한다.
    /// </summary>
    public static string Unix(string appSupportDir, int? instance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appSupportDir);
        var file = instance is { } pid ? $"agent-{pid}.sock" : "agent.sock";
        return Path.Combine(appSupportDir, "run", file);
    }
}
