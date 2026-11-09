using System.Globalization;
using System.Text;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Schema;

/// <summary>
/// 메타데이터 제공자 본체(스키마·객체·테이블·프로시저 인자) — Oracle <c>OracleMetadataProvider</c> 대응(T3a). 상세·DDL 은 .Details.cs, 소스·오류는 .Source.cs.
/// <para>
/// 쿼리는 Tibero 카탈로그 <c>meta.*</c> 에서 꺼내고 <see cref="CatalogRunner"/> 로 보낸다(<c>@PFX@</c> = dba/all, 명령 잠금은 <see cref="TiberoSessionQuery"/>).
/// </para>
/// <para>
/// 결정 44: 객체 목록 캐시(<c>IMetadataCache</c>)·백그라운드 갱신·<c>MetadataInvalidated</c> 를 두지 않는다. 부를 때마다 서버에 직접 묻는다
/// (캐시를 Core 로 옮기는 일은 T3e). Java 저장 객체는 Tibero 에 없어 인터페이스 기본 구현(빈 목록·false)을 쓴다.
/// </para>
/// <para>
/// 오류 규칙: Oracle 이 실패를 삼키는 덧붙임 구역(트리거·참조됨·크기·이름 풀이)은 Oracle 처럼 드라이버 서버 오류를 삼키되 끊김(Disconnected)은 던진다.
/// 그 밖의 질의 오류와 카탈로그 변형 없음(<c>CatalogQueryNotFoundException</c>)은 Oracle 처럼 그대로 던진다.
/// </para>
/// </summary>
[OrandaService(typeof(IMetadataProvider), Dbms = Dbms.Tibero)]
internal sealed partial class TiberoMetadataProvider : IMetadataProvider
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

    public TiberoMetadataProvider()
        : this(TiberoCatalogQuery.RunAsync)
    {
    }

    /// <summary>시험용: 카탈로그 실행기를 바꿔 끼운다.</summary>
    internal TiberoMetadataProvider(CatalogRunner run)
    {
        _run = run;
    }

    public async Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var rows = await RunAsync(s, "meta.schemas", null, ct).ConfigureAwait(false);
        return [.. rows.Select(r => Str(r[0])!)];
    }

    public async Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(q);
        return ToRefs(await RunAsync(s, "meta.objects", ObjectBinds(q), ct).ConfigureAwait(false));
    }

    /// <summary>
    /// 이름 접두어로 서버에 바로 묻는다(<c>meta.objects</c> 의 <c>name_pattern</c> = 접두어 + <c>%</c>, LIKE 특수 글자는 <c>\</c> 로 막는다).
    /// 인터페이스 기본 구현은 서버 전체 목록을 받아 거르므로 키 입력마다 부르기에 무겁다.
    /// <paramref name="limit"/> 은 받은 뒤에 적용한다: 카탈로그 정렬이 소유자·유형 순이라 서버에서 자르면 이름순 앞쪽을 놓친다(짧은 접두어면 많이 받는다).
    /// 이름순 정렬 + ROWNUM 으로 자르는 카탈로그 질의는 뒤 작업이다.
    /// </summary>
    public async Task<IReadOnlyList<DbObjectRef>> FindObjectsByPrefixAsync(ISession s, string namePrefix, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(namePrefix);
        if (limit <= 0)
        {
            return [];
        }

        var found = await ListObjectsAsync(s, new ObjectQuery { NamePattern = EscapeLike(namePrefix) + "%" }, ct).ConfigureAwait(false);
        return [.. found.Where(o => o.Name.StartsWith(namePrefix, StringComparison.Ordinal)).OrderBy(o => o.Name, StringComparer.Ordinal).Take(limit)];
    }

    public async Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(table);
        var binds = TiberoCatalogQuery.TableBinds(table);

        var colRows = await RunAsync(s, "meta.columns", binds, ct).ConfigureAwait(false);
        if (colRows.Count == 0)
        {
            throw new InvalidOperationException($"테이블 또는 뷰를 찾을 수 없습니다: {table.Owner}.{table.Name}");
        }

        var consRows = await RunAsync(s, "meta.constraints", binds, ct).ConfigureAwait(false);
        var idxRows = await RunAsync(s, "meta.indexes", binds, ct).ConfigureAwait(false);
        var commentRows = await RunAsync(s, "meta.table_comment", binds, ct).ConfigureAwait(false);

        return new TableInfo(table, [.. colRows.Select(ToColumn)])
        {
            Constraints = BuildConstraints(consRows),
            Indexes = BuildIndexes(idxRows),
            Comment = commentRows.Count > 0 ? Str(commentRows[0][0]) : null,
        };
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
        var binds = TiberoCatalogQuery.TableBinds(normalized);

        var members = await RunAsync(s, "meta.procedures", binds, ct).ConfigureAwait(false);
        var args = await RunAsync(s, "meta.arguments", binds, ct).ConfigureAwait(false);
        return BuildProcedures(normalized, members, args);
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

    /// <summary>LIKE 특수 글자(<c>\</c>, <c>%</c>, <c>_</c>)를 <c>\</c> 로 막는다(<c>meta.objects</c> 의 <c>ESCAPE '\'</c>).</summary>
    internal static string EscapeLike(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length + 4);
        foreach (var ch in text)
        {
            if (ch is '\\' or '%' or '_')
            {
                sb.Append('\\');
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static List<DbObjectRef> ToRefs(IReadOnlyList<object?[]> rows)
        => [.. rows.Select(r => new DbObjectRef(Str(r[0])!, Str(r[1])!, Str(r[2])!))];

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
    /// argument_name, in_out, data_type, defaulted, type_owner, type_name, type_subname) → 프로시저 목록.
    /// <para>
    /// Oracle 과 달라진 점: Tibero 의 멤버 행에는 오버로드 번호가 없다(늘 NULL). 그래서 (이름, 오버로드) 대신 이름으로 인자 묶음을 찾는다.
    /// <list type="bullet">
    /// <item>인자 쪽에 오버로드 번호가 있으면 묶음마다 하나씩 낸다.</item>
    /// <item>멤버 행이 여럿인데 그 이름의 인자 행이 모두 오버로드 NULL 이면 위치로 나눈다(<see cref="SplitByPosition"/>).</item>
    /// <item>인자 행이 없는 오버로드도 호출할 수 있는 대상이라, 멤버 행 수보다 적으면 인자 없는 항목으로 채운다.</item>
    /// <item>한 이름에 항목이 여럿인데 오버로드 번호가 모두 없으면 1부터 차례로 붙인다(화면이 「오버로드 n」 으로 가른다).</item>
    /// </list>
    /// <c>defaulted</c> 가 없어 <see cref="ArgumentInfo.HasDefault"/> 는 늘 false 다.
    /// </para>
    /// </summary>
    internal static List<ProcedureInfo> BuildProcedures(DbObjectRef obj, IReadOnlyList<object?[]> members, IReadOnlyList<object?[]> args)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(args);

        // 인자를 (프로시저 이름, 오버로드) 로 묶는다. 카탈로그가 이름·오버로드·위치 순으로 주므로 묶음 순서는 오버로드 순이다.
        var argGroups = args
            .GroupBy(r => (Name: Str(r[0])!, Overload: Str(r[1])))
            .ToList();

        var result = new List<ProcedureInfo>();
        var isPackage = string.Equals(obj.ObjectType, "PACKAGE", StringComparison.OrdinalIgnoreCase);
        foreach (var byName in members.GroupBy(m => Str(m[0]) ?? obj.Name, StringComparer.Ordinal))
        {
            var memberCount = byName.Count();
            var groups = argGroups
                .Where(g => string.Equals(g.Key.Name, byName.Key, StringComparison.Ordinal))
                .Select(g => (g.Key.Overload, Rows: g.ToList()))
                .ToList();
            if (memberCount > 1 && groups is [{ Overload: null } only])
            {
                groups = [.. SplitByPosition(only.Rows).Select(rows => ((string?)null, rows))];
            }

            var built = groups.Select(g => BuildProcedure(byName.Key, g.Overload, g.Rows)).ToList();
            while (built.Count < memberCount)
            {
                built.Add(new ProcedureInfo(byName.Key, false, []));
            }

            if (built.Count > 1 && built.TrueForAll(p => p.Overload is null))
            {
                built = [.. built.Select((p, i) => p with { Overload = i + 1 })];
            }

            result.AddRange(built);
        }

        if (members.Count == 0 && !isPackage)
        {
            // 단독 프로시저·함수인데 *_procedures 에 행이 없는 경우(권한)에도 인자 행이 있으면 답한다.
            result.AddRange(argGroups.OrderBy(g => g.Key.Name, StringComparer.Ordinal).Select(g => BuildProcedure(g.Key.Name, g.Key.Overload, [.. g])));
        }

        return result;
    }

    /// <summary>
    /// 오버로드 번호 없이 섞여 온 한 이름의 인자 행을 오버로드별로 나눈다(position = r[2]).
    /// <list type="bullet">
    /// <item>위치가 한 번이라도 줄면 선언 순서로 온 행이다: 위치가 줄거나 되풀이되는 곳에서 새 묶음을 연다.</item>
    /// <item>위치가 줄지 않으면 위치순으로 정렬된 행이다(지금 <c>meta.arguments</c> 의 <c>ORDER BY … position</c>): 같은 위치의 k 번째 행을 k 번째 묶음에 넣는다.
    /// 인자 수가 다른 오버로드는 어느 행이 어느 오버로드 것인지 알 수 없어 앞 묶음부터 채운다(최선의 추정).</item>
    /// </list>
    /// </summary>
    internal static List<List<object?[]>> SplitByPosition(IReadOnlyList<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var positions = rows.Select(r => Int(r[2]) ?? 0).ToList();
        var groups = new List<List<object?[]>>();
        var declarationOrder = positions.Zip(positions.Skip(1)).Any(p => p.Second < p.First);
        if (declarationOrder)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                if (i == 0 || positions[i] <= positions[i - 1])
                {
                    groups.Add([]);
                }

                groups[^1].Add(rows[i]);
            }

            return groups;
        }

        var seen = new Dictionary<int, int>();
        for (var i = 0; i < rows.Count; i++)
        {
            var k = seen.GetValueOrDefault(positions[i]);
            seen[positions[i]] = k + 1;
            if (k == groups.Count)
            {
                groups.Add([]);
            }

            groups[k].Add(rows[i]);
        }

        return groups;
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
