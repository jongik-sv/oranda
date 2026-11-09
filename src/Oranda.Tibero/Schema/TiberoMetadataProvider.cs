using System.Globalization;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Storage;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Schema;

/// <summary>
/// 메타데이터 제공자 본체(스키마·객체·테이블·프로시저 인자) — Oracle <c>OracleMetadataProvider</c> 대응(T3a). 상세·DDL 은 .Details.cs, 소스·오류는 .Source.cs.
/// <para>
/// 쿼리는 Tibero 카탈로그 <c>meta.*</c> 에서 꺼내고 <see cref="CatalogRunner"/> 로 보낸다(<c>@PFX@</c> = dba/all, 명령 잠금은 <see cref="TiberoSessionQuery"/>).
/// </para>
/// <para>
/// 객체 목록 캐시 흐름(<see cref="CachedMetadataProvider.ListObjectsAsync"/>·<see cref="CachedMetadataProvider.FindObjectsByPrefixAsync"/>·갱신·<c>MetadataInvalidated</c>·payload)은
/// Core <see cref="CachedMetadataProvider"/> 에 있다(결정 44 의 「캐시 없음」을 T3e 에서 바꿨다). 여기서는 서버 목록 읽기(<c>meta.objects</c>)와
/// 백그라운드 갱신이 삼킬 실패만 정하고, 테이블·프로시저 상세는 객체 행의 payload 에 담아 둔다.
/// Java 저장 객체는 Tibero 에 없어 인터페이스 기본 구현(빈 목록·false)을 쓴다.
/// </para>
/// <para>
/// 오류 규칙: Oracle 이 실패를 삼키는 덧붙임 구역(트리거·참조됨·크기·이름 풀이)은 Oracle 처럼 드라이버 서버 오류를 삼키되 끊김(Disconnected)은 던진다.
/// 그 밖의 질의 오류와 카탈로그 변형 없음(<c>CatalogQueryNotFoundException</c>)은 Oracle 처럼 그대로 던진다.
/// </para>
/// </summary>
[OrandaService(typeof(IMetadataProvider), Dbms = Dbms.Tibero)]
internal sealed partial class TiberoMetadataProvider : CachedMetadataProvider, IMetadataProvider
{
    /// <summary>이 제공자가 쓰는 카탈로그 id 모두(시험이 Tibero 카탈로그에서 모두 풀리는지 본다).</summary>
    internal static readonly IReadOnlyList<string> CatalogIds =
    [
        "meta.schemas", "meta.objects", "meta.columns", "meta.constraints", "meta.indexes", "meta.table_comment", "meta.table_comments",
        "meta.procedures", "meta.arguments", "meta.detail_general", "meta.detail_triggers", "meta.detail_referenced_by", "meta.detail_partitions",
        "meta.detail_stats", "meta.detail_size", "meta.detail_size_user", "meta.detail_privileges", "meta.detail_privileges_user",
        "meta.detail_dependencies", "meta.source", "meta.compile_errors", "meta.name_step",
    ];

    private readonly CatalogRunner _run;

    public TiberoMetadataProvider(IMetadataCache cache, IEventBus events)
        : this(cache, events, DefaultBackgroundRefreshInterval, TiberoCatalogQuery.RunAsync)
    {
    }

    /// <param name="backgroundRefreshInterval">캐시로 답한 뒤 백그라운드 갱신을 다시 시작하는 최소 간격. <see cref="Timeout.InfiniteTimeSpan"/> = 하지 않음(시험용).</param>
    /// <param name="run">시험용: 카탈로그 실행기를 바꿔 끼운다.</param>
    internal TiberoMetadataProvider(IMetadataCache cache, IEventBus events, TimeSpan backgroundRefreshInterval, CatalogRunner run)
        : base(cache, events, backgroundRefreshInterval)
    {
        _run = run;
    }

    /// <summary>시험용: 캐시 없이 카탈로그 실행기만 바꿔 끼운다(서버만 읽는 시험. 캐시 오류는 기능을 막지 않으므로 캐시 없이도 서버로 답한다).</summary>
    internal TiberoMetadataProvider(CatalogRunner run)
        : this(null!, null!, Timeout.InfiniteTimeSpan, run)
    {
    }

    public async Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var rows = await RunAsync(s, "meta.schemas", null, ct).ConfigureAwait(false);
        return [.. rows.Select(r => Str(r[0])!)];
    }

    public async Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(table);

        var cachedRow = await FindCachedAsync(s, table, ct).ConfigureAwait(false);
        if (cachedRow?.PayloadJson is { } json && TryReadPayload(json, "table", out TableInfo? hit))
        {
            return hit!;
        }

        var binds = TiberoCatalogQuery.TableBinds(table);

        var colRows = await RunAsync(s, "meta.columns", binds, ct).ConfigureAwait(false);
        if (colRows.Count == 0)
        {
            throw new InvalidOperationException($"테이블 또는 뷰를 찾을 수 없습니다: {table.Owner}.{table.Name}");
        }

        var consRows = await RunAsync(s, "meta.constraints", binds, ct).ConfigureAwait(false);
        var idxRows = await RunAsync(s, "meta.indexes", binds, ct).ConfigureAwait(false);
        var commentRows = await RunAsync(s, "meta.table_comment", binds, ct).ConfigureAwait(false);

        var info = new TableInfo(table, [.. colRows.Select(ToColumn)])
        {
            Constraints = BuildConstraints(consRows),
            Indexes = BuildIndexes(idxRows),
            Comment = commentRows.Count > 0 ? Str(commentRows[0][0]) : null,
        };

        await StorePayloadAsync(s, cachedRow, "table", info, ct).ConfigureAwait(false);
        return info;
    }

    public async Task<IReadOnlyDictionary<string, string>> ListTableCommentsAsync(ISession s, string owner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(owner);
        var rows = await RunAsync(s, "meta.table_comments", new Dictionary<string, object?>(StringComparer.Ordinal) { ["owner"] = owner }, ct).ConfigureAwait(false);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (Str(r[0]) is { } name && Str(r[1]) is { Length: > 0 } comment)
            {
                result[name] = comment;
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(obj);

        // 패키지는 명세(PACKAGE)로 본다. 본문 참조가 와도 같은 멤버를 돌려준다.
        var normalized = string.Equals(obj.ObjectType, "PACKAGE BODY", StringComparison.OrdinalIgnoreCase)
            ? obj with { ObjectType = "PACKAGE" }
            : obj;

        var cachedRow = await FindCachedAsync(s, normalized, ct).ConfigureAwait(false);
        // 빈 목록은 믿지 않고 다시 읽는다(Oracle 과 같다): 인자 없는 단독 프로시저를 빈 목록으로 저장한 캐시가 남아 있을 수 있다.
        if (cachedRow?.PayloadJson is { } json && TryReadPayload(json, "procs", out List<ProcedureInfo>? hit) && hit!.Count > 0)
        {
            return hit!;
        }

        var binds = TiberoCatalogQuery.TableBinds(normalized);

        var members = await RunAsync(s, "meta.procedures", binds, ct).ConfigureAwait(false);
        var args = await RunAsync(s, "meta.arguments", binds, ct).ConfigureAwait(false);
        var list = BuildProcedures(normalized, members, args);
        await StorePayloadAsync(s, cachedRow, "procs", list, ct).ConfigureAwait(false);
        return list;
    }

    // ---- 카탈로그 쿼리 실행 ----

    private async Task<IReadOnlyList<object?[]>> RunAsync(ISession s, string id, IReadOnlyDictionary<string, object?>? binds, CancellationToken ct) =>
        (await _run(s, id, binds, ct).ConfigureAwait(false)).Rows;

    /// <summary>
    /// 덧붙이는 구역 하나를 읽는다(Oracle <c>TryQueryAsync</c> 대응). 드라이버 서버 오류면 null(그 구역만 빼고 계속한다) — 단 끊김(Disconnected)은 던진다.
    /// 드라이버 오류가 아닌 것(카탈로그 변형 없음·취소 등)도 그대로 던진다.
    /// </summary>
    private async Task<IReadOnlyList<object?[]>?> TryRunAsync(ISession s, string id, IReadOnlyDictionary<string, object?>? binds, CancellationToken ct)
    {
        try
        {
            return await RunAsync(s, id, binds, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (TiberoErrorClassifier.ToDbError(ex, null) is { } e && e.Kind != DbErrorKind.Disconnected)
        {
            return null;
        }
    }

    /// <summary><c>meta.objects</c> 바인드(Oracle <c>FetchObjectsAsync</c> 와 같은 이름·값): 시각은 UTC, 유형은 <c>|A|B|</c>, 제한 없음 = int 최댓값.</summary>
    internal static Dictionary<string, object?> ObjectBinds(ObjectQuery q)
    {
        ArgumentNullException.ThrowIfNull(q);
        return new(StringComparer.Ordinal)
        {
            ["owner"] = q.Owner,
            ["types"] = q.ObjectTypes.Count == 0 ? null : "|" + string.Join('|', q.ObjectTypes) + "|",
            ["name_pattern"] = q.NamePattern,
            ["invalid_only"] = q.InvalidOnly ? 1 : 0,
            ["created_after"] = q.CreatedAfter is { } ca ? ca.UtcDateTime : null,
            ["modified_after"] = q.ModifiedAfter is { } ma ? ma.UtcDateTime : null,
            ["max_rows"] = q.Limit ?? int.MaxValue,
        };
    }

    // ---- 객체 목록 캐시(CachedMetadataProvider) ----

    /// <summary>
    /// 서버의 전체 객체 목록을 받아 캐시와 비교한다(<see cref="CachedMetadataProvider.RefreshObjectListAsync"/>). 시험이 직접 부른다.
    /// </summary>
    /// <returns>갱신 후 캐시에 있는 전체 목록(캐시를 못 쓰면 서버에서 받은 목록).</returns>
    internal Task<IReadOnlyList<MetaObject>> RefreshAsync(ISession s, CancellationToken ct) => RefreshObjectListAsync(s, ct);

    protected override async Task<IReadOnlyList<MetaObject>> FetchObjectsAsync(ISession s, ObjectQuery q, DateTimeOffset fetchedAt, CancellationToken ct)
    {
        var dbKey = s.DbKey;
        var rows = await RunAsync(s, "meta.objects", ObjectBinds(q), ct).ConfigureAwait(false);
        return [.. rows.Select(r => ToMeta(dbKey, r, fetchedAt))];
    }

    /// <summary>
    /// 세션이 닫혔거나 끊겼거나 바쁘다(<see cref="SessionBusyException"/> 는 <see cref="InvalidOperationException"/>).
    /// 드라이버 오류는 드라이버 형식을 참조하지 않고 <see cref="TiberoErrorClassifier"/> 가 알아보는 서버 오류로 가른다.
    /// 카탈로그 변형 없음(<c>CatalogQueryNotFoundException</c>)은 삼키지 않는다.
    /// </summary>
    protected override bool IsIgnorableBackgroundRefreshFailure(Exception ex)
        => TiberoErrorClassifier.IsDriverException(ex) || ex is InvalidOperationException or ObjectDisposedException;

    /// <summary>시험이 부른다(보호 멤버 <see cref="IsIgnorableBackgroundRefreshFailure"/> 를 연다).</summary>
    internal bool IsIgnorable(Exception ex) => IsIgnorableBackgroundRefreshFailure(ex);

    private static MetaObject ToMeta(string dbKey, object?[] r, DateTimeOffset fetchedAt) => new()
    {
        DbKey = dbKey,
        Owner = Str(r[0])!,
        Name = Str(r[1])!,
        Type = Str(r[2])!,
        Status = Str(r[3]),
        LastDdlTime = r[4] is DateTime d ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)) : null,
        FetchedAt = fetchedAt,
    };

    // ---- 행 → 모델 ----

    private static string? Str(object? v) => TiberoSessionQuery.Str(v);

    private static long? Long(object? v) => TiberoSessionQuery.Long(v);

    private static int? Int(object? v) => v is null ? null : Convert.ToInt32(v, CultureInfo.InvariantCulture);

    private static bool Yes(object? v) => string.Equals(Str(v), "YES", StringComparison.Ordinal);

    /// <summary>
    /// <c>meta.columns</c> 한 줄 → 열. 열 순서는 Oracle 판과 같다(column_name, position, data_type, data_length, data_precision, data_scale, nullable,
    /// data_default, virtual_column, identity_flag, hidden_column, domain_name, comments). Tibero 는 identity·domain 이 늘 NULL 이다.
    /// </summary>
    internal static ColumnInfo ToColumn(object?[] r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new ColumnInfo(Str(r[0])!, Int(r[1]) ?? 0, Str(r[2])!)
        {
            Length = Int(r[3]),
            Precision = Int(r[4]),
            Scale = Int(r[5]),
            IsNullable = !string.Equals(Str(r[6]), "N", StringComparison.Ordinal),
            DefaultValue = Str(r[7])?.Trim(),
            IsVirtual = Yes(r[8]),
            IsIdentity = Yes(r[9]),
            IsInvisible = Yes(r[10]),
            Domain = Str(r[11]),
            Comment = Str(r[12]),
        };
    }

    /// <summary>
    /// <c>meta.constraints</c> 행(constraint_name, constraint_type, status, search_condition, r_owner, r_table_name, column_name, position, r_constraint_name)
    /// → 제약 목록. NOT NULL 검사 제약은 SQL 이 거르지만 한 번 더 뺀다(Oracle 과 같은 규칙).
    /// </summary>
    internal static List<ConstraintInfo> BuildConstraints(IReadOnlyList<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var result = new List<ConstraintInfo>();
        foreach (var group in rows.GroupBy(r => Str(r[0])!, StringComparer.Ordinal))
        {
            var first = group.First();
            var kind = Str(first[1]) switch
            {
                "P" => ConstraintKind.PrimaryKey,
                "U" => ConstraintKind.Unique,
                "R" => ConstraintKind.ForeignKey,
                _ => ConstraintKind.Check,
            };
            if (kind == ConstraintKind.Check && IsNotNullCheck(Str(first[3])))
            {
                continue;
            }

            var columns = group.Where(r => r[6] is not null).Select(r => Str(r[6])!).ToList();
            result.Add(new ConstraintInfo(group.Key, kind, columns)
            {
                IsEnabled = string.Equals(Str(first[2]), "ENABLED", StringComparison.Ordinal),
                Condition = kind == ConstraintKind.Check ? Str(first[3]) : null,
                ReferencedTable = kind == ConstraintKind.ForeignKey && first[4] is not null && first[5] is not null
                    ? new DbObjectRef(Str(first[4])!, Str(first[5])!, "TABLE")
                    : null,
                ReferencedConstraint = kind == ConstraintKind.ForeignKey && first.Length > 8 ? Str(first[8]) : null,
            });
        }

        return result;
    }

    /// <summary>SQL 의 <c>search_condition LIKE '%IS NOT NULL'</c> 과 같은 판정(대소문자 구분, 끝 일치).</summary>
    internal static bool IsNotNullCheck(string? condition)
        => condition is not null && condition.EndsWith("IS NOT NULL", StringComparison.Ordinal);

    /// <summary><c>meta.indexes</c> 행(index_name, uniqueness, index_type, column_name, column_position) → 인덱스 목록.</summary>
    private static List<IndexInfo> BuildIndexes(IReadOnlyList<object?[]> rows)
        => [.. rows.GroupBy(r => Str(r[0])!, StringComparer.Ordinal).Select(g => new IndexInfo(
            g.Key,
            string.Equals(Str(g.First()[1]), "UNIQUE", StringComparison.Ordinal),
            [.. g.Select(r => Str(r[3])!)])
        {
            IndexType = Str(g.First()[2]),
        })];

    /// <summary>
    /// 멤버(<c>meta.procedures</c>: procedure_name, overload, subprogram_id, object_type)와 인자(<c>meta.arguments</c>: procedure_name, overload, position,
    /// argument_name, in_out, data_type, defaulted, type_owner, type_name, type_subname, subprogram_id) → 프로시저 목록. Oracle 과 같은 규칙이다:
    /// 멤버 행마다 (이름, 오버로드) 가 같은 인자 묶음을 붙인다(결정 64 — 두 카탈로그 질의가 오버로드 번호를 같은 규칙으로 낸다: 같은 이름이 둘 이상이면 1..n, 하나면 NULL).
    /// 인자 행이 없는 멤버도 호출할 수 있는 대상이라 인자 없는 항목으로 낸다. <c>defaulted</c> 가 없어 <see cref="ArgumentInfo.HasDefault"/> 는 늘 false 다.
    /// </summary>
    internal static List<ProcedureInfo> BuildProcedures(DbObjectRef obj, IReadOnlyList<object?[]> members, IReadOnlyList<object?[]> args)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(args);

        // 인자를 (프로시저 이름, 오버로드) 로 묶는다.
        var argGroups = args
            .GroupBy(r => (Name: Str(r[0])!, Overload: Str(r[1])))
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<ProcedureInfo>();
        foreach (var m in members)
        {
            var name = Str(m[0]) ?? obj.Name;
            var overload = Str(m[1]);
            result.Add(BuildProcedure(name, overload, argGroups.GetValueOrDefault((name, overload)) ?? []));
        }

        if (members.Count == 0 && !string.Equals(obj.ObjectType, "PACKAGE", StringComparison.OrdinalIgnoreCase))
        {
            // 단독 프로시저·함수인데 *_procedures 에 행이 없는 경우(권한)에도 인자 행이 있으면 답한다.
            result.AddRange(argGroups.OrderBy(g => g.Key.Name, StringComparer.Ordinal).Select(g => BuildProcedure(g.Key.Name, g.Key.Overload, g.Value)));
        }

        return result;
    }

    private static ProcedureInfo BuildProcedure(string name, string? overload, List<object?[]> rows)
    {
        var ret = rows.FirstOrDefault(r => Int(r[2]) == 0 && r[3] is null);
        var arguments = rows
            .Where(r => r[3] is not null && r[5] is not null)
            .Select(r => new ArgumentInfo(Str(r[3])!, Int(r[2]) ?? 0, Str(r[5])!, Str(r[4]) switch
            {
                "OUT" => ArgumentDirection.Out,
                "IN/OUT" or "IN OUT" or "INOUT" => ArgumentDirection.InOut,
                _ => ArgumentDirection.In,
            })
            {
                HasDefault = string.Equals(Str(r[6]), "Y", StringComparison.Ordinal),
                TypeName = r[8] is null ? null : string.Join('.', new[] { Str(r[7]), Str(r[8]), Str(r[9]) }.Where(x => x is not null)),
            })
            .ToList();
        return new ProcedureInfo(name, ret is not null, arguments)
        {
            Overload = ParseOverload(overload),
            ReturnType = ret is null ? null : Str(ret[5]),
        };
    }

    private static int? ParseOverload(string? overload) =>
        int.TryParse(overload, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
}
