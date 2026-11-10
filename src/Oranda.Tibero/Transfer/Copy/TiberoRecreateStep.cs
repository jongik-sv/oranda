using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Catalog;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Text;
using Oranda.Core.Transfer.Copy;
using Oranda.Tibero.Catalog;
using Oranda.Tibero.Schema;

namespace Oranda.Tibero.Transfer.Copy;

/// <summary>
/// Tibero 「테이블 다시 만들기」가 지우기 전에 받아 두는 것(<see cref="ICopyRecreateStep"/>, T5 TB4). 모양은 Oracle <c>RecreateStep</c> 과 같고 달라진 것은 다음뿐이다
/// (실측 = docs/design/tibero-driver-transfer.md 3장, 서버로 끝까지 도는 확인은 <c>TiberoRecreateStepIt</c>).
/// <list type="bullet">
/// <item>트리거 DDL: <c>DBMS_METADATA.GET_DDL('TRIGGER')</c> 는 -14001(3.4)이라 <c>copy.recreate_trigger_ddl</c> 카탈로그가 없다. T3 의 트리거 DDL 조립
/// (<see cref="TiberoMetadataProvider"/> 의 <c>GetDdlAsync(TRIGGER)</c> = <c>*_SOURCE</c> 글 + <c>ALTER TRIGGER … ENABLE|DISABLE</c>)을 받아 생성 문장만 남긴다
/// (<see cref="TriggerCreate"/>, 상태는 카탈로그 STATUS 로 따로 정한다). 이 조회는 대량 읽기 경로가 아니라 세션 질의 도우미로 간다(사전 뷰 SELECT 만).</item>
/// <item>FK: <c>ALL_CONSTRAINTS</c> 에 DEFERRABLE·DEFERRED 가 없어(-8026, 3.5) 카탈로그가 그 자리에 NULL 을 준다 → <see cref="ForeignKeyDdl"/> 는 DEFERRABLE 절을 쓰지 않는다
/// (지연 FK 는 기본 NOT DEFERRABLE 로 돌아온다). ON DELETE CASCADE·SET NULL 과 꺼짐(DISABLE)은 Oracle 과 같다.</item>
/// <item>서버가 지은 이름: 6.2.15 는 <c>&lt;소유자&gt;_CON&lt;숫자&gt;</c> 로 짓는다(#76 실측, <c>_SYS_</c> 아님). Core 의 <see cref="CreateTableDdl.IsServerName"/> 은
/// <c>SYS_</c>·<c>BIN$</c> 만 보므로 <see cref="IsServerName"/> 이 소유자 이름으로 더 본다. 실행기·사전 점검이 원본 방언의 이 판정을 <see cref="CreateTableDdl"/> 에 넘겨
/// 서버 이름 PK·UK·CHECK·작업 안 FK·인덱스는 이름 없이 만든다(대상 서버가 새로 짓는다, TR1 위험 5). 이 표를 참조하던 다른 표의 FK 는 같은 DB 에 있던 그 FK 를 되붙이는 것이라 이름 그대로 쓴다.</item>
/// <item>휴지통: <c>USER_RECYCLEBIN</c> 에 OPERATION 이 없다(3.7). 휴지통 이름은 <c>BIN$…</c> 가 아니라 <c>&lt;계정&gt;_TBL…</c> 모양.</item>
/// <item>컴파일 오류 트리거(TR1 위험 1): 지우기 전부터 INVALID 인 트리거를 <c>copy.invalid_triggers</c>(Tibero 에만 있는 id)로 읽어 <see cref="PreservedDdl.InvalidTriggers"/> 에 둔다.
/// Tibero 는 컴파일 오류 트리거 CREATE 를 오류 없이 받으므로(TB4 #79) 그대로 다시 만들 수 있고, 실행기가 되살린 뒤 상태를 보고 꺼 둔다.</item>
/// </list>
/// 카탈로그 SELECT 는 <c>Catalog/copy/recreate_*.sql</c>(<c>dbms: tibero</c>, Oracle 과 같은 id·바인드·열 위치). 값은 <see cref="Text"/> 로 글자로 바꾼다(Tibero 대량 코덱에 기대지 않는다).
/// 키 서비스가 아니다: Tibero 복사 실행기 하위 형식 <c>TiberoCopyRunner</c>(TI1b)가 생성자에서 넘긴다.
/// </summary>
internal sealed partial class TiberoRecreateStep : ICopyRecreateStep
{
    private static readonly Lazy<SqlCatalog> Catalog = new(TiberoEmbeddedCatalog.Load);

    private static readonly Lazy<TiberoMetadataProvider> Metadata = new(() => new TiberoMetadataProvider(TiberoCatalogQuery.RunAsync));

    private readonly Func<ISession, string, (string Name, string Value)[], CancellationToken, Task<List<object?[]>>> _query;

    private readonly Func<ISession, string, string, CancellationToken, Task<string>> _triggerDdl;

    /// <summary>카탈로그 조회는 대량 읽기 경로(<c>CopyCatalogQueries.QueryAsync</c>, 정책 1회·이력 없음), 트리거 DDL 은 메타데이터 제공자(<see cref="MetadataTriggerDdlAsync"/>).</summary>
    public TiberoRecreateStep(IBulkExecutionService bulk)
        : this(BulkQuery(bulk), MetadataTriggerDdlAsync)
    {
    }

    /// <summary>
    /// 시험·IT 용: <paramref name="query"/> = (세션, SELECT 글, 바인드, 토큰) → 행, <paramref name="triggerDdl"/> = (세션, 트리거 소유자, 이름, 토큰) → 트리거 DDL 글
    /// (생성 문장 뒤에 상태 문장·끝 <c>/</c> 가 붙어 와도 된다).
    /// </summary>
    internal TiberoRecreateStep(Func<ISession, string, (string Name, string Value)[], CancellationToken, Task<List<object?[]>>> query,
                                Func<ISession, string, string, CancellationToken, Task<string>> triggerDdl)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _triggerDdl = triggerDdl ?? throw new ArgumentNullException(nameof(triggerDdl));
    }

    public async Task<PreservedDdl> CaptureAsync(ISession target, string owner, string table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var triggers = new List<string>();
        var triggerObjects = new List<DisabledObject>();
        // 지우기 전부터 컴파일 오류(INVALID)인 트리거(TR1 위험 1): 그대로 다시 만들고, 되살린 뒤 INVALID 면 실행기가 꺼 두고 알린다(결과는 실패로 두지 않는다).
        var invalid = (await QueryAsync(target, "copy.invalid_triggers", ct, ("OWN", owner), ("TBL", table)).ConfigureAwait(false))
            .Select(r => (Owner: Text(r[0]), Name: Text(r[1]))).ToHashSet();
        var invalidObjects = new List<DisabledObject>();
        foreach (var row in await QueryAsync(target, "copy.recreate_triggers", ct, ("OWN", owner), ("TBL", table)).ConfigureAwait(false))
        {
            var trgOwner = Text(row[0])!;
            var name = Text(row[1])!;
            var ddl = await _triggerDdl(target, trgOwner, name, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(ddl))
            {
                throw new InvalidOperationException($"트리거 {trgOwner}.{name} 의 DDL 을 받지 못했습니다.");
            }

            var obj = new DisabledObject(DisabledKind.Trigger, trgOwner, table, name);
            triggers.Add(TriggerCreate(ddl));
            triggerObjects.Add(obj);
            if (invalid.Contains((trgOwner, name)))
            {
                invalidObjects.Add(obj);
            }

            if (!string.Equals(Text(row[2]), "ENABLED", StringComparison.Ordinal))
            {
                triggers.Add($"ALTER TRIGGER {CopyCommonStatements.Qualified(trgOwner, name)} DISABLE");
                triggerObjects.Add(obj);
            }
        }

        var grants = new List<string>();
        foreach (var row in await QueryAsync(target, "copy.recreate_grants", ct, ("OWN", owner), ("TBL", table)).ConfigureAwait(false))
        {
            var grantee = Text(row[0])!;
            var privilege = Text(row[1])!;
            var withGrant = string.Equals(Text(row[2]), "YES", StringComparison.Ordinal) ? " WITH GRANT OPTION" : string.Empty;
            var to = string.Equals(grantee, "PUBLIC", StringComparison.Ordinal) ? "PUBLIC" : SqlIdentifier.QuoteAlways(grantee);
            grants.Add($"GRANT {privilege} ON {CopyCommonStatements.Qualified(owner, table)} TO {to}{withGrant}");
        }

        var fks = new List<string>();
        var referencing = new List<DisabledObject>();
        var rows = await QueryAsync(target, "copy.recreate_referencing_fks", ct, ("OWN", owner), ("TBL", table)).ConfigureAwait(false);
        foreach (var fk in rows.GroupBy(r => (Owner: Text(r[0])!, Table: Text(r[1])!, Name: Text(r[2])!)))
        {
            fks.Add(ForeignKeyDdl(fk.Key.Owner, fk.Key.Table, fk.Key.Name, owner, table, [.. fk]));
            referencing.Add(new DisabledObject(DisabledKind.ForeignKey, fk.Key.Owner, fk.Key.Table, fk.Key.Name));
        }

        return new PreservedDdl(triggers, grants, fks) { TriggerObjects = triggerObjects, ReferencingConstraints = referencing, InvalidTriggers = invalidObjects };
    }

    /// <summary>
    /// 원본 표의 FK 가운데 부모가 <paramref name="parents"/>(함께 다시 만드는 표)에 있는 것을, 대상 소유자 이름으로 만든 <c>ADD CONSTRAINT</c> 문장.
    /// 서버가 지은 이름(<see cref="IsServerName"/>)은 쓰지 않는다(대상 서버가 새로 짓는다). 원본은 읽기 전용 세션이고 조회만 한다.
    /// </summary>
    public async Task<IReadOnlyList<(string Parent, string Ddl, string? Name)>> SourceForeignKeysAsync(ISession source, string sourceOwner, string table,
                                                                                      IReadOnlySet<string> parents, string targetOwner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(parents);
        var rows = await QueryAsync(source, "copy.recreate_source_fks", ct, ("OWN", sourceOwner), ("TBL", table)).ConfigureAwait(false);
        var result = new List<(string, string, string?)>();
        foreach (var fk in rows.GroupBy(r => (Name: Text(r[2])!, Parent: Text(r[9])!)))
        {
            if (!parents.Contains(fk.Key.Parent))
            {
                continue;
            }

            var name = IsServerName(fk.Key.Name, sourceOwner) ? null : fk.Key.Name;
            result.Add((fk.Key.Parent, ForeignKeyDdl(targetOwner, table, name, targetOwner, fk.Key.Parent, [.. fk]), name));
        }

        return result;
    }

    /// <summary>
    /// 휴지통(USER_RECYCLEBIN)에 이 원래 이름으로 있는 표의 휴지통 이름(<c>&lt;계정&gt;_TBL…</c>). 대상 세션은 소유자다.
    /// DROP 전후를 비교하면 이번 DROP 이 만든 이름을 안다. 비면 휴지통에 남지 않았다(영구 삭제).
    /// </summary>
    public async Task<HashSet<string>> RecycleBinAsync(ISession target, string table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var rows = await QueryAsync(target, "copy.recreate_recycle_bin", ct, ("TBL", table)).ConfigureAwait(false);
        return rows.Select(r => Text(r[0])).OfType<string>().ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>원본 인덱스 열(함수 기반 식 포함, SQ2). 객체 상세와 같은 조회(<see cref="TiberoTableKeyDetailProvider"/>). 볼 수 없으면 null.</summary>
    public async Task<IReadOnlyList<IndexColumnDetail>?> IndexColumnsAsync(ISession source, DbObjectRef table, string indexName, CancellationToken ct)
    {
        var result = await new TiberoTableKeyDetailProvider().GetIndexColumnsAsync(source, table, indexName, ct).ConfigureAwait(false);
        return result.Denied ? null : result.Items;
    }

    /// <summary>
    /// 서버가 지은 이름인가. 6.2.15 실측(#76): 이름 없는 제약은 <c>&lt;소유자&gt;_CON&lt;숫자&gt;</c>. 문서에 나오는 <c>_SYS_…</c> 와
    /// Core 가 보는 Oracle 모양(<c>SYS_…</c>, <c>BIN$…</c>)도 서버 이름으로 본다.
    /// </summary>
    internal static bool IsServerName(string name, string owner)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(owner);
        return name.StartsWith("_SYS_", StringComparison.Ordinal) || CreateTableDdl.IsServerName(name) || IsOwnerConstraintName(name, owner);
    }

    private static bool IsOwnerConstraintName(string name, string owner)
    {
        var prefix = owner + "_CON";
        return name.Length > prefix.Length
               && name.StartsWith(prefix, StringComparison.Ordinal)
               && name.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    /// <summary>
    /// FK 문장 하나. <paramref name="rows"/> 는 <c>copy.recreate_referencing_fks</c>·<c>copy.recreate_source_fks</c> 의 같은 FK 줄(위치 순서):
    /// [3] delete_rule, [4] 자식 열, [5] 부모 열, [6] status. [7]·[8](DEFERRABLE·DEFERRED)은 Tibero 에서 늘 NULL 이라 보지 않는다.
    /// 꺼져 있던 FK 는 꺼진 채로, 켜져 있던 FK 는 기본(ENABLE VALIDATE)으로 만든다.
    /// </summary>
    internal static string ForeignKeyDdl(string childOwner, string childTable, string? name, string parentOwner, string parentTable,
                                         IReadOnlyList<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var first = rows[0];
        var childColumns = string.Join(", ", rows.Select(r => SqlIdentifier.QuoteAlways(Text(r[4])!)));
        var parentColumns = string.Join(", ", rows.Select(r => SqlIdentifier.QuoteAlways(Text(r[5])!)));
        var sb = new StringBuilder()
            .Append("ALTER TABLE ").Append(CopyCommonStatements.Qualified(childOwner, childTable)).Append(" ADD ");
        if (name is not null)
        {
            sb.Append("CONSTRAINT ").Append(SqlIdentifier.QuoteAlways(name)).Append(' ');
        }

        sb.Append("FOREIGN KEY (").Append(childColumns).Append(") REFERENCES ")
            .Append(CopyCommonStatements.Qualified(parentOwner, parentTable)).Append(" (").Append(parentColumns).Append(')');
        switch (Text(first[3]))
        {
            case "CASCADE":
                sb.Append(" ON DELETE CASCADE");
                break;
            case "SET NULL":
                sb.Append(" ON DELETE SET NULL");
                break;
        }

        if (string.Equals(Text(first[6]), "DISABLED", StringComparison.Ordinal))
        {
            sb.Append(" DISABLE");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 메타데이터 제공자의 트리거 DDL(생성 문장 [+ 새 줄 <c>/</c>] + 새 줄 <c>ALTER TRIGGER "O"."N" ENABLE|DISABLE[;]</c>)에서 생성 문장만 남긴다.
    /// 끝의 상태 문장은 그 모양일 때만 떼고(본문 안의 같은 글자는 건드리지 않는다), 끝 공백·<c>/</c> 를 지운다. 본문 끝의 <c>END;</c> 는 남는다.
    /// 원문이 <c>CREATE TRIGGER</c> 로 시작하면 <c>CREATE OR REPLACE TRIGGER</c> 로 바꾼다(T3 조립기는 이미 OR REPLACE 를 붙여 준다).
    /// </summary>
    internal static string TriggerCreate(string ddl)
    {
        ArgumentNullException.ThrowIfNull(ddl);
        var text = ddl.Trim();
        var alter = text.LastIndexOf("ALTER TRIGGER ", StringComparison.Ordinal);
        if (alter > 0 && StateStatement().IsMatch(text[alter..]))
        {
            text = text[..alter].TrimEnd();
        }

        text = text.TrimEnd('/').TrimEnd();
        return CreateWithoutReplace().Replace(text, "CREATE OR REPLACE TRIGGER", 1);
    }

    /// <summary>값 → 글자(문자열은 그대로, 그 밖은 고정 문화권 글자, NULL·DBNull 은 null).</summary>
    private static string? Text(object? value) => value switch
    {
        null or DBNull => null,
        string s => s,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <summary>Tibero 카탈로그의 <paramref name="id"/> 글(세션 서버 버전, DBMS 는 늘 Tibero)을 보내 모든 행을 읽는다.</summary>
    private Task<List<object?[]>> QueryAsync(ISession s, string id, CancellationToken ct, params (string Name, string Value)[] binds)
        => _query(s, Catalog.Value.Get(id, s.Server.MajorVersion, Dbms.Tibero).Sql, binds, ct);

    private static Func<ISession, string, (string Name, string Value)[], CancellationToken, Task<List<object?[]>>> BulkQuery(IBulkExecutionService bulk)
    {
        ArgumentNullException.ThrowIfNull(bulk);
        return (s, sql, binds, ct) => CopyCatalogQueries.QueryAsync(bulk, s, sql, ct, binds);
    }

    /// <summary>T3 트리거 DDL 조립(<c>*_SOURCE</c> + 상태 문장, 끝 표시 없음). 소스가 없으면 <see cref="InvalidOperationException"/>(실행기가 받아 「받아 두지 못함」 으로 멈춘다).</summary>
    internal static Task<string> MetadataTriggerDdlAsync(ISession s, string owner, string name, CancellationToken ct)
        => Metadata.Value.GetDdlAsync(s, new DbObjectRef(owner, name, "TRIGGER"), new DdlOptions { SqlTerminator = false }, ct);

    /// <summary>끝의 상태 문장: <c>ALTER TRIGGER 이름[.이름] ENABLE|DISABLE[;]</c>(이름은 따옴표 있을 수 있음).</summary>
    [GeneratedRegex("""^ALTER TRIGGER\s+(?:"(?:[^"]|"")*"|[^\s".]+)(?:\.(?:"(?:[^"]|"")*"|[^\s".]+))?\s+(?:ENABLE|DISABLE)\s*;?$""", RegexOptions.CultureInvariant)]
    private static partial Regex StateStatement();

    [GeneratedRegex(@"^CREATE\s+TRIGGER\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateWithoutReplace();
}
