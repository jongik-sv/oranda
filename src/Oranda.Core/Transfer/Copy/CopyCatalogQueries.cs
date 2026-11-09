using Oranda.Core.Catalog;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Sessions;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 사전 점검이 쓰는 카탈로그 조회. 모두 <see cref="IBulkExecutionService.StreamRowsAsync"/> 로 읽고(<see cref="HistorySource.DataCopy"/>, 이력 없음),
/// 하나씩 끝까지 읽어 목록으로 만든 뒤 돌려준다: 열린 열거 안에서 같은 세션에 다른 조회를 보내면 세션 잠금 때문에 교착한다.
/// 값은 모두 바인드로 넘기고, 글은 항상 <c>SELECT</c> 로 시작한다(대량 읽기가 첫 낱말을 본다). 값 해석은 <see cref="IBulkValueCodec"/>.
/// </summary>
public static class CopyCatalogQueries
{
    /// <summary>점검 조회 공통 옵션.</summary>
    public static readonly ExecutionOptions Options = new() { Source = HistorySource.DataCopy, SkipHistory = true };

    /// <summary>
    /// 사전 점검 SELECT 글을 세션 DBMS 의 카탈로그(<c>Catalog/copy/*.sql</c>, id <c>copy.*</c>)에서 꺼낸다(<see cref="ISqlCatalog.Get"/>).
    /// Oracle 은 옮기기 전 C# 상수와 글자·바인드 이름이 같다(<c>CopyCatalogTextTests</c>). 버전 변형이 없다(11.2 ~ 26ai 가 한 파일).
    /// </summary>
    public static string Sql(ISqlCatalog catalog, ServerInfo server, string id)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Get(id, server).Sql;
    }

    /// <summary>문장을 보내 모든 행을 읽는다. <paramref name="binds"/> 는 글이 쓰는 이름만 담는다(남는 이름은 ORA-01036).</summary>
    public static async Task<List<object?[]>> QueryAsync(IBulkExecutionService bulk, ISession s, string sql,
                                                         IReadOnlyDictionary<string, BindValue> binds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bulk);
        var rows = new List<object?[]>();
        await foreach (var row in bulk.StreamRowsAsync(s, CopyCommonStatements.Query(sql), binds, Options, ct).ConfigureAwait(false))
        {
            rows.Add(row);
        }

        return rows;
    }

    public static Task<List<object?[]>> QueryAsync(IBulkExecutionService bulk, ISession s, string sql, CancellationToken ct,
                                                   params (string Name, string Value)[] binds)
        => QueryAsync(bulk, s, sql, Binds(binds), ct);

    public static Dictionary<string, BindValue> Binds(params (string Name, string Value)[] binds)
        => binds.ToDictionary(b => b.Name, b => new BindValue(BindDataType.Varchar2, b.Value), StringComparer.Ordinal);

    /// <summary>접속 계정·롤·시스템 권한. 세션마다 한 번 읽는다.</summary>
    public static async Task<SessionAccess> ReadAccessAsync(IBulkExecutionService bulk, ISqlCatalog catalog, IBulkValueCodec codec, ISession s,
                                                            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(s);
        var user = codec.ToText((await QueryAsync(bulk, s, Sql(catalog, s.Server, "copy.session_user"), ct).ConfigureAwait(false)).FirstOrDefault()?[0]) ?? s.Profile.User;
        var roles = (await QueryAsync(bulk, s, Sql(catalog, s.Server, "copy.session_roles"), ct).ConfigureAwait(false)).Select(r => codec.ToText(r[0])).OfType<string>();
        var privs = (await QueryAsync(bulk, s, Sql(catalog, s.Server, "copy.session_privs"), ct).ConfigureAwait(false)).Select(r => codec.ToText(r[0])).OfType<string>();
        return new SessionAccess(user, roles.ToHashSet(StringComparer.OrdinalIgnoreCase), privs.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 이 세션이 <paramref name="owner"/>.<paramref name="name"/> 에 가진 객체 권한(SELECT·INSERT·UPDATE·DELETE).
    /// 소유자면 모두, 아니면 <c>ALL_TAB_PRIVS</c> 에서 본인·PUBLIC·켜진 롤에 준 권한과 <c>… ANY TABLE</c>(시퀀스는 <c>… ANY SEQUENCE</c>) 시스템 권한을 모은다.
    /// <paramref name="readPrivilege"/> 면 조회만 주는 READ 권한도 SELECT 로 본다(<see cref="ICopyPrecheckDialect.SupportsReadPrivilege"/>).
    /// </summary>
    public static async Task<HashSet<string>> ReadObjectPrivilegesAsync(IBulkExecutionService bulk, ISqlCatalog catalog, IBulkValueCodec codec,
                                                                        ISession s, SessionAccess access, string owner, string name, string anyKind,
                                                                        bool readPrivilege, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(access);
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (access.IsOwner(owner))
        {
            held.UnionWith(["SELECT", "INSERT", "UPDATE", "DELETE"]);
            return held;
        }

        foreach (var row in await QueryAsync(bulk, s, Sql(catalog, s.Server, "copy.table_privs"), ct, ("OWN", owner), ("TBL", name)).ConfigureAwait(false))
        {
            var grantee = codec.ToText(row[0]);
            if (grantee is not null && access.IsGrantee(grantee) && codec.ToText(row[1]) is { } privilege)
            {
                held.Add(privilege);
            }
        }

        foreach (var verb in new[] { "SELECT", "INSERT", "UPDATE", "DELETE" })
        {
            if (access.Has($"{verb} ANY {anyKind}"))
            {
                held.Add(verb);
            }
        }

        // 12c 부터 조회만 주는 READ 객체 권한·READ ANY TABLE 이 있다. 복사가 쓰는 조회(SELECT·COUNT)에는 SELECT 와 같다.
        if (readPrivilege && (held.Contains("READ") || access.Has($"READ ANY {anyKind}")))
        {
            held.Add("SELECT");
        }

        return held;
    }
}

/// <summary>한 세션의 접속 계정·켜진 롤·시스템 권한. 이름은 대소문자를 가리지 않고 비교한다.</summary>
public sealed record SessionAccess(string User, IReadOnlySet<string> Roles, IReadOnlySet<string> SystemPrivileges)
{
    public bool IsOwner(string owner) => CopyOwners.SameOwner(User, owner);

    public bool Has(string systemPrivilege) => SystemPrivileges.Contains(systemPrivilege);

    /// <summary>객체 권한을 받은 쪽이 이 세션인가(본인·PUBLIC·켜진 롤).</summary>
    public bool IsGrantee(string grantee)
        => string.Equals(grantee, User, StringComparison.OrdinalIgnoreCase)
           || string.Equals(grantee, "PUBLIC", StringComparison.OrdinalIgnoreCase)
           || Roles.Contains(grantee);
}
