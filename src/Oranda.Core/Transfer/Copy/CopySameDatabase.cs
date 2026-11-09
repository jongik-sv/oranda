using Oranda.Core.Connection;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 원본과 대상이 같은 DB 인가, 같은 DB 일 때 대상 정리가 원본 행을 지우는 규칙은 무엇인가. 사전 점검과 실행기(<c>CopyRunner</c>)가 같은 판정을 쓴다.
/// </summary>
public static class CopySameDatabase
{
    /// <summary>
    /// 원본과 대상이 같은 DB 인가. <see cref="Sessions.ISession.DbKey"/> 는 <c>{DB_UNIQUE_NAME}/{CON_NAME}/{SESSION_USER}</c> 다(앞 두 마디가 같으면 같은 DB).
    /// </summary>
    /// <remarks>
    /// 이름만으로는 복제 DB·기본 이름 컨테이너(FREE/FREEPDB1 등)를 같은 DB 로 오판한다. 그래서 호스트·인스턴스 이름도 본다.
    /// RAC 는 노드마다 호스트·인스턴스가 달라 DB 이름만 본다. 호스트·인스턴스를 모르면 같다고 본다(막는 쪽).
    /// </remarks>
    public static bool IsSameDatabase(string sourceDbKey, ServerInfo source, string targetDbKey, ServerInfo target)
    {
        ArgumentNullException.ThrowIfNull(sourceDbKey);
        ArgumentNullException.ThrowIfNull(targetDbKey);

        static string Db(string key)
        {
            var last = key.LastIndexOf('/');
            return last < 0 ? key : key[..last];
        }

        static bool SameOrUnknown(string? a, string? b) =>
            a is null || b is null || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        if (!string.Equals(Db(sourceDbKey), Db(targetDbKey), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 서버 정보는 DB 이름이 같을 때만 본다(옮기기 전 CopyRunner 와 같은 순서: DB 이름이 다르면 서버 정보를 보지 않고 false).
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        return source.IsRac || target.IsRac
               || (SameOrUnknown(source.HostName, target.HostName) && SameOrUnknown(source.InstanceName, target.InstanceName));
    }

    /// <summary>
    /// 같은 DB 일 때 대상 정리가 원본 행을 지우는 규칙(규칙 사이 비교, <see cref="CopyOwners.TargetsReadAsSource"/>). 다른 DB 면 빈 목록.
    /// </summary>
    public static IReadOnlyList<(string Key, string Table)> SameSourceTargets(CopySessions sessions, CopyJob job)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        return IsSameDatabase(sessions.Source.DbKey, sessions.Source.Server, sessions.Target.DbKey, sessions.Target.Server)
            ? CopyOwners.TargetsReadAsSource(job)
            : [];
    }
}
