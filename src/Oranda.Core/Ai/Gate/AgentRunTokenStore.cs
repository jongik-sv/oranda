using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Oranda.Core.Ai.Protocol;

namespace Oranda.Core.Ai.Gate;

/// <summary>
/// 메모리 실행 토큰 저장소(<see cref="IAgentRunTokens"/>). 토큰 원문은 보관하지 않고 SHA-256 해시를 키로 둔다 —
/// 조회가 원문 비교 시간에 기대지 않고, 메모리 덤프에도 원문이 남지 않는다. 스레드 안전. 앱 종료와 함께 사라진다(토큰은 프로세스 수명보다 짧다).
/// 한 <see cref="AgentSession"/> 에 토큰을 여러 번 발급해도 되지만 하나를 폐기하면 그 세션 전체가 폐기된다.
/// </summary>
public sealed class AgentRunTokenStore : IAgentRunTokens
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>발급된 토큰 수(시험·진단용).</summary>
    public int Count => _sessions.Count;

    public string Issue(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.IsRevoked)
        {
            throw new InvalidOperationException("이미 폐기된 세션에는 토큰을 발급할 수 없다.");
        }

        while (true)
        {
            var token = AgentRunToken.Generate();
            if (_sessions.TryAdd(Key(token), session))
            {
                return token;
            }
        }
    }

    public AgentSession? Resolve(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        return _sessions.TryGetValue(Key(token), out var session) && !session.IsRevoked ? session : null;
    }

    public void Revoke(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        if (_sessions.TryRemove(Key(token), out var session))
        {
            // 같은 세션에 발급된 다른 토큰을 먼저 모두 지운 뒤 폐기한다(폐기 도중 예외·끼어들기가 있어도 형제 토큰이 남아 조회되지 않게).
            foreach (var pair in _sessions)
            {
                if (ReferenceEquals(pair.Value, session))
                {
                    _sessions.TryRemove(pair.Key, out _);
                }
            }

            session.Revoke();
        }
    }

    private static string Key(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
