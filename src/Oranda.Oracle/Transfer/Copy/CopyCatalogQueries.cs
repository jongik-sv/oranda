using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Execution;

namespace Oranda.Oracle.Transfer.Copy;

/// <summary>
/// 사전 점검이 쓰는 카탈로그 조회. 모두 <see cref="IBulkExecutionService.StreamRowsAsync"/> 로 읽고(<see cref="HistorySource.DataCopy"/>, 이력 없음),
/// 하나씩 끝까지 읽어 목록으로 만든 뒤 돌려준다: 열린 열거 안에서 같은 세션에 다른 조회를 보내면 세션 잠금 때문에 교착한다.
/// 값은 모두 바인드로 넘기고, 글은 항상 <c>SELECT</c> 로 시작한다(대량 읽기가 첫 낱말을 본다).
/// </summary>
internal static class CopyCatalogQueries
{
    /// <summary>점검 조회 공통 옵션.</summary>
    public static readonly ExecutionOptions Options = new() { Source = HistorySource.DataCopy, SkipHistory = true };

    /// <summary>접속 계정(프록시 접속이면 프로필의 사용자 이름과 다르다).</summary>
    public const string SessionUser = "SELECT USER FROM DUAL";

    public const string SessionRoles = "SELECT ROLE FROM SESSION_ROLES";

    /// <summary>롤을 거쳐 받은 시스템 권한까지 포함한다(<c>USER_SYS_PRIVS</c> 의 상위 집합).</summary>
    public const string SessionPrivs = "SELECT PRIVILEGE FROM SESSION_PRIVS";

    /// <summary>바인드: OWN, TBL. 표·시퀀스 모두 같은 뷰에 있다. 부여받은 대상(GRANTEE)은 호출한 쪽이 거른다.</summary>
    public const string TablePrivileges =
        "SELECT GRANTEE, PRIVILEGE FROM ALL_TAB_PRIVS WHERE TABLE_SCHEMA = :OWN AND TABLE_NAME = :TBL";

    /// <summary>바인드: OWN, TBL. 켜져 있는 트리거만.</summary>
    public const string EnabledTriggers =
        "SELECT OWNER, TRIGGER_NAME FROM ALL_TRIGGERS WHERE TABLE_OWNER = :OWN AND TABLE_NAME = :TBL AND STATUS = 'ENABLED' ORDER BY OWNER, TRIGGER_NAME";

    /// <summary>바인드: OWN, TBL. 이 표를 참조하는 켜진 외래키(자기 참조 제외).</summary>
    public const string ReferencingForeignKeys =
        "SELECT c.OWNER, c.TABLE_NAME, c.CONSTRAINT_NAME FROM ALL_CONSTRAINTS c "
        + "JOIN ALL_CONSTRAINTS p ON p.OWNER = c.R_OWNER AND p.CONSTRAINT_NAME = c.R_CONSTRAINT_NAME "
        + "WHERE c.CONSTRAINT_TYPE = 'R' AND c.STATUS = 'ENABLED' AND p.OWNER = :OWN AND p.TABLE_NAME = :TBL "
        + "AND NOT (c.OWNER = p.OWNER AND c.TABLE_NAME = p.TABLE_NAME) "
        + "ORDER BY c.OWNER, c.TABLE_NAME, c.CONSTRAINT_NAME";

    /// <summary>바인드: OWN, TBL. 12c 이상. 11g 에는 뷰가 없어 ORA-00942 가 나므로 호출한 쪽이 「IDENTITY 없음」으로 본다.</summary>
    public const string IdentityColumns =
        "SELECT COLUMN_NAME, GENERATION_TYPE FROM ALL_TAB_IDENTITY_COLS WHERE OWNER = :OWN AND TABLE_NAME = :TBL";

    /// <summary>바인드: OWN, SEQ. 다음 값(LAST_NUMBER)은 캐시가 있으면 실제보다 크거나 같다.</summary>
    public const string SequenceInfo =
        "SELECT LAST_NUMBER, INCREMENT_BY FROM ALL_SEQUENCES WHERE SEQUENCE_OWNER = :OWN AND SEQUENCE_NAME = :SEQ";

    /// <summary>바인드: OWN. 시퀀스 이름 후보(<c>SequenceGuess</c> 입력).</summary>
    public const string SequenceNames =
        "SELECT SEQUENCE_NAME FROM ALL_SEQUENCES WHERE SEQUENCE_OWNER = :OWN ORDER BY SEQUENCE_NAME";

    /// <summary>바인드: PNAME. 권한이 없으면 ORA-00942 로 실패한다(호출한 쪽이 「알 수 없음」으로 본다).</summary>
    public const string ParameterValue = "SELECT VALUE FROM V$PARAMETER WHERE NAME = :PNAME";

    /// <summary>문장을 보내 모든 행을 읽는다. <paramref name="binds"/> 는 글이 쓰는 이름만 담는다(남는 이름은 ORA-01036).</summary>
    public static async Task<List<object?[]>> QueryAsync(IBulkExecutionService bulk, ISession s, string sql,
                                                         IReadOnlyDictionary<string, BindValue> binds, CancellationToken ct)
    {
        var rows = new List<object?[]>();
        await foreach (var row in bulk.StreamRowsAsync(s, CopyStatements.Query(sql), binds, Options, ct).ConfigureAwait(false))
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
    public static async Task<SessionAccess> ReadAccessAsync(IBulkExecutionService bulk, ISession s, CancellationToken ct)
    {
        var user = Text((await QueryAsync(bulk, s, SessionUser, ct).ConfigureAwait(false)).FirstOrDefault()?[0]) ?? s.Profile.User;
        var roles = (await QueryAsync(bulk, s, SessionRoles, ct).ConfigureAwait(false)).Select(r => Text(r[0])).OfType<string>();
        var privs = (await QueryAsync(bulk, s, SessionPrivs, ct).ConfigureAwait(false)).Select(r => Text(r[0])).OfType<string>();
        return new SessionAccess(user, roles.ToHashSet(StringComparer.OrdinalIgnoreCase), privs.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 이 세션이 <paramref name="owner"/>.<paramref name="name"/> 에 가진 객체 권한(SELECT·INSERT·UPDATE·DELETE).
    /// 소유자면 모두, 아니면 <c>ALL_TAB_PRIVS</c> 에서 본인·PUBLIC·켜진 롤에 준 권한과 <c>… ANY TABLE</c>(시퀀스는 <c>… ANY SEQUENCE</c>) 시스템 권한을 모은다.
    /// </summary>
    public static async Task<HashSet<string>> ReadObjectPrivilegesAsync(IBulkExecutionService bulk, ISession s, SessionAccess access,
                                                                        string owner, string name, string anyKind, CancellationToken ct)
    {
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (access.IsOwner(owner))
        {
            held.UnionWith(["SELECT", "INSERT", "UPDATE", "DELETE"]);
            return held;
        }

        foreach (var row in await QueryAsync(bulk, s, TablePrivileges, ct, ("OWN", owner), ("TBL", name)).ConfigureAwait(false))
        {
            var grantee = Text(row[0]);
            if (grantee is not null && access.IsGrantee(grantee) && Text(row[1]) is { } privilege)
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
        if (held.Contains("READ") || access.Has($"READ ANY {anyKind}"))
        {
            held.Add("SELECT");
        }

        return held;
    }

    /// <summary>카탈로그 조회가 권한·객체 문제로 실패했나(<see cref="DbErrorKinds.IsNoAccess(DbErrorKind)"/>).</summary>
    public static bool IsNoAccess(OracleException ex) => OracleErrorClassifier.IsNoAccess(ex);

    public static string? Text(object? value) => value switch
    {
        null => null,
        string s => s,
        OracleDecimal d => d.IsNull ? null : d.ToString(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <summary>NUMBER 결과(<see cref="OracleDecimal"/>)를 long 으로. NULL 이면 null, 범위를 넘으면 끝값으로 자른다.</summary>
    public static long? ToLong(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case OracleDecimal d:
                if (d.IsNull)
                {
                    return null;
                }

                return d > long.MaxValue ? long.MaxValue : d < long.MinValue ? long.MinValue : (long)Math.Truncate(d.Value);
            case string s when decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                return (long)Math.Truncate(parsed);
            case IConvertible c:
                return Convert.ToInt64(c, CultureInfo.InvariantCulture);
            default:
                return null;
        }
    }
}

/// <summary>한 세션의 접속 계정·켜진 롤·시스템 권한. 이름은 대소문자를 가리지 않고 비교한다.</summary>
internal sealed record SessionAccess(string User, IReadOnlySet<string> Roles, IReadOnlySet<string> SystemPrivileges)
{
    public bool IsOwner(string owner) => CopyOwners.SameOwner(User, owner);

    public bool Has(string systemPrivilege) => SystemPrivileges.Contains(systemPrivilege);

    /// <summary>객체 권한을 받은 쪽이 이 세션인가(본인·PUBLIC·켜진 롤).</summary>
    public bool IsGrantee(string grantee)
        => string.Equals(grantee, User, StringComparison.OrdinalIgnoreCase)
           || string.Equals(grantee, "PUBLIC", StringComparison.OrdinalIgnoreCase)
           || Roles.Contains(grantee);
}
