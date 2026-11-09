using Oranda.Core.Connection;

namespace Oranda.Core.Sessions;

/// <summary>
/// 지원 서버 버전 관문의 판별 규칙(PRD 11.3, D-06). DBMS 마다 하나씩 구현한다(Oracle = Oranda.Oracle, Tibero = Oranda.Tibero).
/// 구현은 <c>[OrandaService(typeof(IServerVersionPolicy), Dbms = …)]</c> 로 등록하고, 쓰는 쪽은
/// DBMS 별 구현 고르기(<c>IDbmsServiceSelector</c>)로 프로필·세션의 DBMS 에 맞는 것을 꺼낸다.
/// DB 없이 문자열만으로 판별하므로 단위 시험이 가능하다.
/// </summary>
public interface IServerVersionPolicy
{
    /// <summary>이 구현이 맡는 DBMS.</summary>
    Dbms Dbms { get; }

    /// <summary>접속을 허용하는 가장 낮은 버전의 표시 문자열(예: <c>11.2</c>).</summary>
    string MinimumText { get; }

    /// <summary>실제 서버로 시험한 버전(안내용). 이 밖의 버전도 최소 버전 이상이면 허용한다.</summary>
    IReadOnlyList<string> TestedVersions { get; }

    /// <summary>지원하면 true. 아니면 <paramref name="reason"/> 에 사용자에게 보일 이유.</summary>
    bool IsSupported(string? serverVersion, out string reason);
}

/// <summary><see cref="IServerVersionPolicy"/> 의 편의 호출.</summary>
public static class ServerVersionPolicyExtensions
{
    /// <summary>지원하지 않으면 <see cref="UnsupportedServerException"/>.</summary>
    public static void EnsureSupported(this IServerVersionPolicy policy, string? serverVersion)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.IsSupported(serverVersion, out var reason))
        {
            throw new UnsupportedServerException(serverVersion ?? "(알 수 없음)", reason);
        }
    }
}
