using Oranda.Tibero.Driver;

namespace Oranda.Tibero;

/// <summary>
/// 이 프로세스가 Tibero 드라이버(TDP.NET)를 이미 읽어 묶었는지(D-046-1). 읽기를 일으키지 않는다 — 드라이버는 첫 Tibero 접속·접속 시험 때
/// 사용자가 지정한 위치에서 읽는다(<see cref="TiberoDriverHost"/>). 새 코드는 <c>IDbmsDriverStatus</c>(Dbms.Tibero) 를 쓴다.
/// </summary>
public static class TiberoDriverInfo
{
    /// <summary>읽은 드라이버 어셈블리 버전(예: 1.1.35.0). 아직 안 읽었거나 쓸 수 없으면 null.</summary>
    public static string? Version => TiberoDriverHost.Shared.Loaded?.Version.ToString();

    /// <summary>드라이버를 읽어 묶었다.</summary>
    public static bool IsLinked => TiberoDriverHost.Shared.Loaded is not null;
}
