using System.Text;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Core.Text;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Oracle.Transfer.Copy;

/// <summary>
/// 「테이블 다시 만들기」(설계 6.2.3)가 지우기 전에 받아 두는 대상 쪽 DDL. 적재가 끝난 뒤 되살린다.
/// </summary>
/// <param name="Triggers">트리거 생성 문장(<c>CREATE OR REPLACE TRIGGER …</c>). 꺼져 있던 트리거는 뒤에 <c>ALTER TRIGGER … DISABLE</c> 이 따른다</param>
/// <param name="Grants">다른 계정에 준 객체 권한(<c>GRANT … ON … TO …</c>)</param>
/// <param name="ReferencingForeignKeys">
/// 다른 표가 이 표를 참조하던 FK(<c>ALTER TABLE … ADD CONSTRAINT … FOREIGN KEY … REFERENCES …</c>, DEFERRABLE·꺼짐은 원래대로).
/// <c>DROP TABLE … CASCADE CONSTRAINTS</c> 가 함께 지우므로 되살린다. 켜져 있던 FK 는 기본(ENABLE VALIDATE)으로 보내고,
/// 부모 없는 행 때문에 실패하면(ORA-02298) 실행기가 끝에 <c>ENABLE NOVALIDATE</c> 를 붙여 다시 보낸다
/// </param>
internal sealed record PreservedDdl(IReadOnlyList<string> Triggers, IReadOnlyList<string> Grants, IReadOnlyList<string> ReferencingForeignKeys)
{
    public static PreservedDdl Empty { get; } = new([], [], []);

    /// <summary>
    /// <see cref="Triggers"/> 와 같은 자리의 트리거(생성 문장과 그 뒤 DISABLE 문장 모두 같은 트리거).
    /// 되살린 트리거가 컴파일 오류(ORA-24344)로 INVALID 이면 실행기가 이것으로 끈다.
    /// </summary>
    public IReadOnlyList<DisabledObject> TriggerObjects { get; init; } = [];

    /// <summary>
    /// <see cref="ReferencingForeignKeys"/> 와 같은 자리의 FK(자식 소유자·표·이름). 실행기가 작업 안에서 함께 다시 만드는 자식 표의 FK 를 거르고
    /// (그 FK 는 <see cref="CreateTableDdl"/> 이 만든다), 부모 없는 행 수를 셀 때 쓴다. 두 목록은 같은 위치로 짝짓는다.
    /// </summary>
    public IReadOnlyList<DisabledObject> ReferencingConstraints { get; init; } = [];
}

/// <summary>
/// 대상 표의 트리거·권한·참조 FK 를 받아 둔다. 대상 계정은 자기 스키마 객체이므로 트리거 본문은 <c>DBMS_METADATA.GET_DDL</c> 로 받는다.
/// 권한과 참조 FK 는 카탈로그(<c>ALL_TAB_PRIVS</c>, <c>ALL_CONSTRAINTS</c>·<c>ALL_CONS_COLUMNS</c>)로 만든다:
/// 자식 표가 다른 스키마에 있으면 <c>GET_DDL('REF_CONSTRAINT')</c> 는 SELECT_CATALOG_ROLE 이 있어야 해서다.
/// 조회는 모두 대량 읽기 경로(정책 1회, 이력 없음)로 하고, 하나씩 끝까지 읽은 뒤 다음을 보낸다.
/// </summary>
internal sealed class RecreateStep
{
    private const string TriggersSql =
        "SELECT owner, trigger_name, status FROM all_triggers"
        + " WHERE table_owner = :OWN AND table_name = :TBL AND base_object_type = 'TABLE' ORDER BY trigger_name";

    private const string TriggerDdlSql = "SELECT DBMS_METADATA.GET_DDL('TRIGGER', :NAME, :OWN) FROM DUAL";

    // ALL_TAB_PRIVS 의 TYPE 열은 12c 부터 있다(11.2 는 ORA-00904). 한 계정 안에서 표 이름은 다른 객체와 겹치지 않으므로 이름으로만 거른다.
    private const string GrantsSql =
        "SELECT grantee, privilege, grantable FROM all_tab_privs"
        + " WHERE table_schema = :OWN AND table_name = :TBL AND grantor = :OWN"
        + " ORDER BY grantee, privilege";

    /// <summary>이 표의 PK·UK 를 참조하는 다른 표의 FK(자기 참조 제외). 열은 위치 순서.</summary>
    private const string ReferencingSql =
        "SELECT c.owner, c.table_name, c.constraint_name, c.delete_rule, cc.column_name, pc.column_name, c.status, c.deferrable, c.deferred"
        + " FROM all_constraints c"
        + " JOIN all_constraints p ON p.owner = c.r_owner AND p.constraint_name = c.r_constraint_name"
        + " JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name"
        + " JOIN all_cons_columns pc ON pc.owner = p.owner AND pc.constraint_name = p.constraint_name AND pc.position = cc.position"
        + " WHERE c.constraint_type = 'R' AND p.owner = :OWN AND p.table_name = :TBL"
        + " AND NOT (c.owner = p.owner AND c.table_name = p.table_name)"
        + " ORDER BY c.owner, c.table_name, c.constraint_name, cc.position";

    /// <summary>원본 표의 FK(부모가 같은 소유자). 열은 위치 순서. 줄 모양은 <see cref="ReferencingSql"/> 와 같고 [9] = 부모 표.</summary>
    private const string SourceForeignKeysSql =
        "SELECT c.owner, c.table_name, c.constraint_name, c.delete_rule, cc.column_name, pc.column_name, c.status, c.deferrable, c.deferred, p.table_name"
        + " FROM all_constraints c"
        + " JOIN all_constraints p ON p.owner = c.r_owner AND p.constraint_name = c.r_constraint_name"
        + " JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name"
        + " JOIN all_cons_columns pc ON pc.owner = p.owner AND pc.constraint_name = p.constraint_name AND pc.position = cc.position"
        + " WHERE c.constraint_type = 'R' AND c.owner = :OWN AND c.table_name = :TBL AND p.owner = c.owner"
        + " ORDER BY c.constraint_name, cc.position";

    private const string RecycleBinSql =
        "SELECT object_name FROM user_recyclebin WHERE original_name = :TBL AND type = 'TABLE' AND operation = 'DROP'";

    private readonly IBulkExecutionService _bulk;

    public RecreateStep(IBulkExecutionService bulk) => _bulk = bulk;

    public async Task<PreservedDdl> CaptureAsync(ISession target, string owner, string table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var triggers = new List<string>();
        var triggerObjects = new List<DisabledObject>();
        foreach (var row in await CopyCatalogQueries.QueryAsync(_bulk, target, TriggersSql, ct, ("OWN", owner), ("TBL", table)))
        {
            var trgOwner = CopyCatalogQueries.Text(row[0])!;
            var name = CopyCatalogQueries.Text(row[1])!;
            var ddl = await CopyCatalogQueries.QueryAsync(_bulk, target, TriggerDdlSql, ct, ("NAME", name), ("OWN", trgOwner));
            if (ddl.Count == 0 || CopyCatalogQueries.Text(ddl[0][0]) is not { } text)
            {
                throw new InvalidOperationException($"트리거 {trgOwner}.{name} 의 DDL 을 받지 못했습니다.");
            }

            var obj = new DisabledObject(DisabledKind.Trigger, trgOwner, table, name);
            triggers.Add(TriggerCreate(text));
            triggerObjects.Add(obj);
            if (!string.Equals(CopyCatalogQueries.Text(row[2]), "ENABLED", StringComparison.Ordinal))
            {
                triggers.Add($"ALTER TRIGGER {CopyStatements.Qualified(trgOwner, name)} DISABLE");
                triggerObjects.Add(obj);
            }
        }

        var grants = new List<string>();
        foreach (var row in await CopyCatalogQueries.QueryAsync(_bulk, target, GrantsSql, ct, ("OWN", owner), ("TBL", table)))
        {
            var grantee = CopyCatalogQueries.Text(row[0])!;
            var privilege = CopyCatalogQueries.Text(row[1])!;
            var withGrant = string.Equals(CopyCatalogQueries.Text(row[2]), "YES", StringComparison.Ordinal) ? " WITH GRANT OPTION" : string.Empty;
            var to = string.Equals(grantee, "PUBLIC", StringComparison.Ordinal) ? "PUBLIC" : SqlIdentifier.QuoteAlways(grantee);
            grants.Add($"GRANT {privilege} ON {CopyStatements.Qualified(owner, table)} TO {to}{withGrant}");
        }

        var fks = new List<string>();
        var referencing = new List<DisabledObject>();
        var rows = await CopyCatalogQueries.QueryAsync(_bulk, target, ReferencingSql, ct, ("OWN", owner), ("TBL", table));
        foreach (var fk in rows.GroupBy(r => (Owner: CopyCatalogQueries.Text(r[0])!, Table: CopyCatalogQueries.Text(r[1])!, Name: CopyCatalogQueries.Text(r[2])!)))
        {
            fks.Add(ForeignKeyDdl(fk.Key.Owner, fk.Key.Table, fk.Key.Name, owner, table, [.. fk]));
            referencing.Add(new DisabledObject(DisabledKind.ForeignKey, fk.Key.Owner, fk.Key.Table, fk.Key.Name));
        }

        return new PreservedDdl(triggers, grants, fks) { TriggerObjects = triggerObjects, ReferencingConstraints = referencing };
    }

    /// <summary>
    /// 원본 표의 FK 가운데 부모가 <paramref name="parents"/>(함께 다시 만드는 표)에 있는 것을, 대상 소유자 이름으로 만든 <c>ADD CONSTRAINT</c> 문장.
    /// 원본 카탈로그(참조 열·ON DELETE·DEFERRABLE·꺼짐)를 그대로 따른다: UK 를 참조하는 FK 도 원본 열 그대로다.
    /// 서버가 지은 이름(SYS_C…)은 쓰지 않는다(대상 서버가 새로 짓는다). 원본은 읽기 전용 세션이고 조회만 한다.
    /// </summary>
    public async Task<IReadOnlyList<(string Parent, string Ddl, string? Name)>> SourceForeignKeysAsync(ISession source, string sourceOwner, string table,
                                                                                      IReadOnlySet<string> parents, string targetOwner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(parents);
        var rows = await CopyCatalogQueries.QueryAsync(_bulk, source, SourceForeignKeysSql, ct, ("OWN", sourceOwner), ("TBL", table));
        var result = new List<(string, string, string?)>();
        foreach (var fk in rows.GroupBy(r => (Name: CopyCatalogQueries.Text(r[2])!, Parent: CopyCatalogQueries.Text(r[9])!)))
        {
            if (!parents.Contains(fk.Key.Parent))
            {
                continue;
            }

            var name = CreateTableDdl.IsServerName(fk.Key.Name) ? null : fk.Key.Name;
            result.Add((fk.Key.Parent, ForeignKeyDdl(targetOwner, table, name, targetOwner, fk.Key.Parent, [.. fk]), name));
        }

        return result;
    }

    /// <summary>
    /// 휴지통(USER_RECYCLEBIN)에 이 원래 이름으로 있는 표의 휴지통 이름(BIN$…). 대상 세션은 소유자다(사전 점검 결정 10).
    /// DROP 전후를 비교하면 이번 DROP 이 만든 이름을 안다. 비면 휴지통에 남지 않았다(영구 삭제).
    /// </summary>
    public async Task<HashSet<string>> RecycleBinAsync(ISession target, string table, CancellationToken ct)
    {
        var rows = await CopyCatalogQueries.QueryAsync(_bulk, target, RecycleBinSql, ct, ("TBL", table));
        return rows.Select(r => CopyCatalogQueries.Text(r[0])).OfType<string>().ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// FK 문장 하나. <paramref name="rows"/> 는 <see cref="ReferencingSql"/>·<see cref="SourceForeignKeysSql"/> 의 같은 FK 줄(위치 순서):
    /// [3] delete_rule, [4] 자식 열, [5] 부모 열, [6] status, [7] deferrable, [8] deferred.
    /// 꺼져 있던 FK 는 꺼진 채로, 켜져 있던 FK 는 기본(ENABLE VALIDATE)으로 만든다.
    /// </summary>
    internal static string ForeignKeyDdl(string childOwner, string childTable, string? name, string parentOwner, string parentTable,
                                         IReadOnlyList<object?[]> rows)
    {
        var first = rows[0];
        var childColumns = string.Join(", ", rows.Select(r => SqlIdentifier.QuoteAlways(CopyCatalogQueries.Text(r[4])!)));
        var parentColumns = string.Join(", ", rows.Select(r => SqlIdentifier.QuoteAlways(CopyCatalogQueries.Text(r[5])!)));
        var sb = new StringBuilder()
            .Append("ALTER TABLE ").Append(CopyStatements.Qualified(childOwner, childTable)).Append(" ADD ");
        if (name is not null)
        {
            sb.Append("CONSTRAINT ").Append(SqlIdentifier.QuoteAlways(name)).Append(' ');
        }

        sb.Append("FOREIGN KEY (").Append(childColumns).Append(") REFERENCES ")
            .Append(CopyStatements.Qualified(parentOwner, parentTable)).Append(" (").Append(parentColumns).Append(')');
        switch (CopyCatalogQueries.Text(first[3]))
        {
            case "CASCADE":
                sb.Append(" ON DELETE CASCADE");
                break;
            case "SET NULL":
                sb.Append(" ON DELETE SET NULL");
                break;
        }

        if (string.Equals(CopyCatalogQueries.Text(first[7]), "DEFERRABLE", StringComparison.Ordinal))
        {
            sb.Append(string.Equals(CopyCatalogQueries.Text(first[8]), "DEFERRED", StringComparison.Ordinal)
                ? " DEFERRABLE INITIALLY DEFERRED" : " DEFERRABLE INITIALLY IMMEDIATE");
        }

        if (string.Equals(CopyCatalogQueries.Text(first[6]), "DISABLED", StringComparison.Ordinal))
        {
            sb.Append(" DISABLE");
        }

        return sb.ToString();
    }

    /// <summary>
    /// <c>GET_DDL('TRIGGER')</c> 글은 생성 문장 뒤에 상태 문장(<c>ALTER TRIGGER … ENABLE</c>)이 붙어 온다. 생성 문장만 남긴다
    /// (상태는 카탈로그의 STATUS 로 따로 정한다). 끝의 공백·<c>/</c> 는 지운다(드라이버로 보내는 본문에는 붙이지 않는다).
    /// </summary>
    internal static string TriggerCreate(string ddl)
    {
        var text = ddl.Trim();
        var alter = text.LastIndexOf("ALTER TRIGGER ", StringComparison.Ordinal);
        if (alter > 0)
        {
            text = text[..alter].TrimEnd();
        }

        text = text.TrimEnd('/').TrimEnd();
        return text;
    }
}
