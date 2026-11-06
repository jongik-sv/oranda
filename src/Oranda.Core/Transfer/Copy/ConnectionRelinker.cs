using Oranda.Core.Connection;

namespace Oranda.Core.Transfer.Copy;

public enum RelinkKind { ById, ByName, NotFound, BlockedProduction }

public sealed record RelinkResult(ConnectionProfile? Profile, RelinkKind Kind);

/// <summary>작업 파일의 접속 참조를 이 PC 의 접속 프로필에 다시 잇는다(설계 6.3).</summary>
public static class ConnectionRelinker
{
    /// <summary>
    /// 같은 ID·같은 이름 → 같은 이름 → 없음 순서. 이름은 대소문자를 구분하지 않는다.
    /// 대상(<paramref name="isTarget"/>)이 운영 접속이면 프로필을 돌려주지 않고 <see cref="RelinkKind.BlockedProduction"/> 이다.
    /// </summary>
    public static RelinkResult Resolve(ConnectionRef reference, IReadOnlyList<ConnectionProfile> profiles, bool isTarget)
    {
        ConnectionProfile? found = null;
        var kind = RelinkKind.NotFound;
        if (reference.IdHint is { } id && id != Guid.Empty)
        {
            found = Pick(profiles.Where(p => p.Id == id && SameName(p.Name, reference.Name)), isTarget);
            if (found is not null) kind = RelinkKind.ById;
        }
        if (found is null)
        {
            found = Pick(profiles.Where(p => SameName(p.Name, reference.Name)), isTarget);
            if (found is not null) kind = RelinkKind.ByName;
        }
        if (found is null) return new RelinkResult(null, RelinkKind.NotFound);
        if (isTarget && !CopyTargetGuard.IsAllowedTarget(found)) return new RelinkResult(null, RelinkKind.BlockedProduction);
        return new RelinkResult(found, kind);
    }

    // 대상이면 허용되는 접속을 먼저 고른다. 허용되는 것이 하나도 없을 때만 첫 후보(운영)를 돌려 차단한다.
    private static ConnectionProfile? Pick(IEnumerable<ConnectionProfile> candidates, bool isTarget)
    {
        var list = candidates.ToList();
        return isTarget ? list.FirstOrDefault(CopyTargetGuard.IsAllowedTarget) ?? list.FirstOrDefault() : list.FirstOrDefault();
    }

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
