using System.Text;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Core.Text;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Catalog;

namespace Oranda.Oracle.Transfer.Copy;

/// <summary>
/// 대상 표의 트리거·권한·참조 FK 를 받아 둔다. 대상 계정은 자기 스키마 객체이므로 트리거 본문은 <c>DBMS_METADATA.GET_DDL</c> 로 받는다.
/// 권한과 참조 FK 는 카탈로그(<c>ALL_TAB_PRIVS</c>, <c>ALL_CONSTRAINTS</c>·<c>ALL_CONS_COLUMNS</c>)로 만든다:
/// 자식 표가 다른 스키마에 있으면 <c>GET_DDL('REF_CONSTRAINT')</c> 는 SELECT_CATALOG_ROLE 이 있어야 해서다.
/// 조회는 모두 대량 읽기 경로(정책 1회, 이력 없음)로 하고, 하나씩 끝까지 읽은 뒤 다음을 보낸다.
/// Oracle 의 <see cref="ICopyRecreateStep"/>(받아 둔 것은 Core 의 <see cref="PreservedDdl"/>). <c>OracleCopyRunner</c> 가 실행기에 넘긴다.
/// </summary>
internal sealed class RecreateStep : ICopyRecreateStep
{
    private readonly IBulkExecutionService _bulk;

    public RecreateStep(IBulkExecutionService bulk) => _bulk = bulk;

    public async Task<PreservedDdl> CaptureAsync(ISession target, string owner, string table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var triggers = new List<string>();
        var triggerObjects = new List<DisabledObject>();
        foreach (var row in await CopyCatalogQueries.QueryAsync(_bulk, target, EmbeddedCatalog.Text("copy.recreate_triggers"), ct, ("OWN", owner), ("TBL", table)))
        {
            var trgOwner = OracleBulkValueCodec.Text(row[0])!;
            var name = OracleBulkValueCodec.Text(row[1])!;
            var ddl = await CopyCatalogQueries.QueryAsync(_bulk, target, EmbeddedCatalog.Text("copy.recreate_trigger_ddl"), ct, ("NAME", name), ("OWN", trgOwner));
            if (ddl.Count == 0 || OracleBulkValueCodec.Text(ddl[0][0]) is not { } text)
            {
                throw new InvalidOperationException($"트리거 {trgOwner}.{name} 의 DDL 을 받지 못했습니다.");
            }

            var obj = new DisabledObject(DisabledKind.Trigger, trgOwner, table, name);
            triggers.Add(TriggerCreate(text));
            triggerObjects.Add(obj);
            if (!string.Equals(OracleBulkValueCodec.Text(row[2]), "ENABLED", StringComparison.Ordinal))
            {
                triggers.Add($"ALTER TRIGGER {CopyStatements.Qualified(trgOwner, name)} DISABLE");
                triggerObjects.Add(obj);
            }
        }

        var grants = new List<string>();
        foreach (var row in await CopyCatalogQueries.QueryAsync(_bulk, target, EmbeddedCatalog.Text("copy.recreate_grants"), ct, ("OWN", owner), ("TBL", table)))
        {
            var grantee = OracleBulkValueCodec.Text(row[0])!;
            var privilege = OracleBulkValueCodec.Text(row[1])!;
            var withGrant = string.Equals(OracleBulkValueCodec.Text(row[2]), "YES", StringComparison.Ordinal) ? " WITH GRANT OPTION" : string.Empty;
            var to = string.Equals(grantee, "PUBLIC", StringComparison.Ordinal) ? "PUBLIC" : SqlIdentifier.QuoteAlways(grantee);
            grants.Add($"GRANT {privilege} ON {CopyStatements.Qualified(owner, table)} TO {to}{withGrant}");
        }

        var fks = new List<string>();
        var referencing = new List<DisabledObject>();
        var rows = await CopyCatalogQueries.QueryAsync(_bulk, target, EmbeddedCatalog.Text("copy.recreate_referencing_fks"), ct, ("OWN", owner), ("TBL", table));
        foreach (var fk in rows.GroupBy(r => (Owner: OracleBulkValueCodec.Text(r[0])!, Table: OracleBulkValueCodec.Text(r[1])!, Name: OracleBulkValueCodec.Text(r[2])!)))
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
        var rows = await CopyCatalogQueries.QueryAsync(_bulk, source, EmbeddedCatalog.Text("copy.recreate_source_fks"), ct, ("OWN", sourceOwner), ("TBL", table));
        var result = new List<(string, string, string?)>();
        foreach (var fk in rows.GroupBy(r => (Name: OracleBulkValueCodec.Text(r[2])!, Parent: OracleBulkValueCodec.Text(r[9])!)))
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
        var rows = await CopyCatalogQueries.QueryAsync(_bulk, target, EmbeddedCatalog.Text("copy.recreate_recycle_bin"), ct, ("TBL", table));
        return rows.Select(r => OracleBulkValueCodec.Text(r[0])).OfType<string>().ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// FK 문장 하나. <paramref name="rows"/> 는 <c>copy.recreate_referencing_fks</c>·<c>copy.recreate_source_fks</c> 의 같은 FK 줄(위치 순서):
    /// [3] delete_rule, [4] 자식 열, [5] 부모 열, [6] status, [7] deferrable, [8] deferred.
    /// 꺼져 있던 FK 는 꺼진 채로, 켜져 있던 FK 는 기본(ENABLE VALIDATE)으로 만든다.
    /// </summary>
    internal static string ForeignKeyDdl(string childOwner, string childTable, string? name, string parentOwner, string parentTable,
                                         IReadOnlyList<object?[]> rows)
    {
        var first = rows[0];
        var childColumns = string.Join(", ", rows.Select(r => SqlIdentifier.QuoteAlways(OracleBulkValueCodec.Text(r[4])!)));
        var parentColumns = string.Join(", ", rows.Select(r => SqlIdentifier.QuoteAlways(OracleBulkValueCodec.Text(r[5])!)));
        var sb = new StringBuilder()
            .Append("ALTER TABLE ").Append(CopyStatements.Qualified(childOwner, childTable)).Append(" ADD ");
        if (name is not null)
        {
            sb.Append("CONSTRAINT ").Append(SqlIdentifier.QuoteAlways(name)).Append(' ');
        }

        sb.Append("FOREIGN KEY (").Append(childColumns).Append(") REFERENCES ")
            .Append(CopyStatements.Qualified(parentOwner, parentTable)).Append(" (").Append(parentColumns).Append(')');
        switch (OracleBulkValueCodec.Text(first[3]))
        {
            case "CASCADE":
                sb.Append(" ON DELETE CASCADE");
                break;
            case "SET NULL":
                sb.Append(" ON DELETE SET NULL");
                break;
        }

        if (string.Equals(OracleBulkValueCodec.Text(first[7]), "DEFERRABLE", StringComparison.Ordinal))
        {
            sb.Append(string.Equals(OracleBulkValueCodec.Text(first[8]), "DEFERRED", StringComparison.Ordinal)
                ? " DEFERRABLE INITIALLY DEFERRED" : " DEFERRABLE INITIALLY IMMEDIATE");
        }

        if (string.Equals(OracleBulkValueCodec.Text(first[6]), "DISABLED", StringComparison.Ordinal))
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
