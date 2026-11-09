using System.Globalization;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Schema;

/// <summary>
/// 객체 검색·소스 본문 검색·열 이름 검색·참조 관계·휴지통 목록(읽기 전용) — Oracle <c>OracleSchemaAdminProvider</c> 대응(T3d).
/// 쿼리는 Tibero 카탈로그 <c>dbinfo.*</c> 에서 꺼내 <see cref="CatalogRunner"/> 로 보낸다. 검색어·패턴은 모두 바인드로 보낸다.
/// Tibero 와 달라진 점: 휴지통의 되돌리기·비우기 가능 여부는 뷰에 열이 없어 늘 「아니오」, 휴지통 삭제 시각은 문자열이라 형식을 따져 읽는다,
/// 정규식 오류 번호표가 없어 정규식 검사에서 난 서버 오류(분류 불명)를 패턴 오류로 본다.
/// </summary>
[OrandaService(typeof(ISchemaAdminProvider), Dbms = Dbms.Tibero)]
internal sealed class TiberoSchemaAdminProvider : ISchemaAdminProvider
{
    internal const string ObjectSearchId = "dbinfo.object_search";
    internal const string SourceSearchId = "dbinfo.source_search";
    internal const string ColumnSearchId = "dbinfo.column_search";
    internal const string ReferencesId = "dbinfo.references";
    internal const string RecycleBinId = "dbinfo.recyclebin";
    internal const string RegexCheckId = "dbinfo.regex_check";

    /// <summary>객체 검색 쿼리의 세그먼트 자리표시자.</summary>
    internal const string SegmentsToken = "@SEGMENTS@";

    /// <summary>이 제공자가 쓰는 카탈로그 id 전부(시험이 오타를 잡는다).</summary>
    internal static readonly IReadOnlyList<string> CatalogIds =
    [
        ObjectSearchId, SourceSearchId, ColumnSearchId, ReferencesId, RecycleBinId, RegexCheckId,
    ];

    private static readonly string[] DropTimeFormats =
    [
        "yyyy-MM-dd:HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd:HH:mm:ss.fff",
    ];

    private readonly CatalogRunner _run;

    public TiberoSchemaAdminProvider()
        : this(RunWithReplacementsAsync)
    {
    }

    /// <summary>시험용: 카탈로그 실행기를 바꿔 끼운다.</summary>
    internal TiberoSchemaAdminProvider(CatalogRunner run)
    {
        _run = run;
    }

    public async Task<IReadOnlyList<ObjectSearchRow>> SearchObjectsAsync(ISession s, ObjectSearchQuery q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(q);

        var table = await _run(s, ObjectSearchId, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["owner"] = q.Owner,
            ["types"] = TypeList(q.ObjectTypes),
            ["name_pattern"] = q.NamePattern,
            ["invalid_only"] = q.InvalidOnly ? 1 : 0,
            ["created_after"] = q.CreatedAfter,
            ["modified_after"] = q.ModifiedAfter,
            ["min_size"] = q.MinSizeBytes,
            ["max_size"] = q.MaxSizeBytes,
            ["max_rows"] = q.Limit <= 0 ? int.MaxValue : q.Limit,
        }, ct).ConfigureAwait(false);

        // 열: owner, object_name, object_type, status, created, last_ddl_time, size_bytes
        return
        [
            .. table.Rows.Select(r => new ObjectSearchRow(
                new DbObjectRef(TiberoSessionQuery.Str(r[0])!, TiberoSessionQuery.Str(r[1])!, TiberoSessionQuery.Str(r[2])!),
                TiberoSessionQuery.Str(r[3]) ?? string.Empty, TiberoSessionQuery.Date(r[4]), TiberoSessionQuery.Date(r[5]), TiberoSessionQuery.Long(r[6]))),
        ];
    }

    public async Task<SearchHits<SourceSearchHit>> SearchSourceAsync(ISession s, SourceSearchQuery q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(q);

        var binds = SourceSearchPattern.Build(q);
        var limit = q.Limit <= 0 ? 1000 : q.Limit;

        if (q.UseRegex)
        {
            // 걸러진 줄이 없으면 서버가 패턴을 평가하지 않아 틀린 정규식을 놓친다. dual 한 줄로 먼저 평가한다.
            try
            {
                await _run(s, RegexCheckId, PatternBinds(binds), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (PatternError(ex) is { } error)
            {
                throw new SearchPatternException(DescribeError(error), ex);
            }
        }

        var table = await _run(s, SourceSearchId, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["owner"] = q.Owner,
            ["types"] = TypeList(q.ObjectTypes),
            ["needle"] = binds.Needle,
            ["pattern"] = binds.Pattern,
            ["flags"] = binds.Flags,
            ["max_rows"] = limit == int.MaxValue ? limit : limit + 1, // 하나 더 받아 잘림을 안다(최댓값이면 넘침 없이 그대로)
        }, ct).ConfigureAwait(false);

        // 열: owner, name, type, line, text
        var hits = table.Rows.Take(limit).Select(r => new SourceSearchHit(
            new DbObjectRef(TiberoSessionQuery.Str(r[0])!, TiberoSessionQuery.Str(r[1])!, TiberoSessionQuery.Str(r[2])!),
            (int)(TiberoSessionQuery.Long(r[3]) ?? 0),
            (TiberoSessionQuery.Str(r[4]) ?? string.Empty).TrimEnd('\r', '\n'))).ToList();
        return new SearchHits<SourceSearchHit>(hits, table.Rows.Count > limit);
    }

    public async Task<SearchHits<ColumnSearchHit>> SearchColumnsAsync(ISession s, ColumnSearchQuery q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(q);

        var limit = q.Limit <= 0 ? 1000 : q.Limit;
        var table = await _run(s, ColumnSearchId, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["owner"] = q.Owner,
            ["name_pattern"] = q.NamePattern ?? "%",
            ["max_rows"] = limit == int.MaxValue ? limit : limit + 1, // 하나 더 받아 잘림을 안다(최댓값이면 넘침 없이 그대로)
        }, ct).ConfigureAwait(false);

        // 열: owner, table_name, object_type, column_name, data_type, data_length, data_precision, data_scale, nullable
        var hits = table.Rows.Take(limit).Select(r => new ColumnSearchHit(
            new DbObjectRef(TiberoSessionQuery.Str(r[0])!, TiberoSessionQuery.Str(r[1])!, TiberoSessionQuery.Str(r[2]) ?? "TABLE"),
            TiberoSessionQuery.Str(r[3])!,
            TypeText(TiberoSessionQuery.Str(r[4]) ?? string.Empty, TiberoSessionQuery.Long(r[5]), TiberoSessionQuery.Long(r[6]), TiberoSessionQuery.Long(r[7])),
            !string.Equals(TiberoSessionQuery.Str(r[8]), "N", StringComparison.Ordinal))).ToList();
        return new SearchHits<ColumnSearchHit>(hits, table.Rows.Count > limit);
    }

    public async Task<IReadOnlyList<ObjectReference>> GetReferencesAsync(ISession s, DbObjectRef obj, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(obj);

        var table = await _run(s, ReferencesId, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["owner"] = obj.Owner,
            ["name"] = obj.Name,
            ["type"] = obj.ObjectType,
        }, ct).ConfigureAwait(false);

        // 열: direction(USES|USED_BY), owner_name, object_name, object_type
        return
        [
            .. table.Rows.Select(r => new ObjectReference(
                string.Equals(TiberoSessionQuery.Str(r[0]), "USED_BY", StringComparison.Ordinal),
                new DbObjectRef(TiberoSessionQuery.Str(r[1])!, TiberoSessionQuery.Str(r[2])!, TiberoSessionQuery.Str(r[3])!))),
        ];
    }

    public async Task<IReadOnlyList<RecycleBinItem>> ListRecycleBinAsync(ISession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);

        var table = await _run(s, RecycleBinId, null, ct).ConfigureAwait(false);

        // 열: object_name, original_name, type, ts_name, droptime(문자열), can_undrop(NULL), can_purge(NULL), space.
        // Tibero 뷰에 can_undrop·can_purge 열이 없어 카탈로그가 NULL 을 주고, NULL 은 「아니오」 로 읽는다(되돌리기·비우기를 막는다).
        return
        [
            .. table.Rows.Select(r => new RecycleBinItem(
                TiberoSessionQuery.Str(r[0])!, TiberoSessionQuery.Str(r[1]) ?? string.Empty, TiberoSessionQuery.Str(r[2]) ?? string.Empty, TiberoSessionQuery.Str(r[3]),
                DropTime(r[4]), string.Equals(TiberoSessionQuery.Str(r[5]), "YES", StringComparison.Ordinal),
                string.Equals(TiberoSessionQuery.Str(r[6]), "YES", StringComparison.Ordinal), TiberoSessionQuery.Long(r[7]))),
        ];
    }

    /// <summary>객체 검색의 세그먼트 원천: 사전 조회 권한이 있으면 <c>dba_segments</c>, 없으면 접속 사용자의 <c>user_segments</c> 에 owner 를 붙인 것.</summary>
    internal static string SegmentsSource(ISession s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Server.Capabilities.CanQueryDictionary
            ? "dba_segments"
            : "(SELECT USER AS owner, segment_name, segment_type, bytes FROM user_segments)";
    }

    /// <summary>카탈로그 id 별 자리표시자 바꿈(객체 검색만 세그먼트 원천을 바꾼다).</summary>
    internal static IReadOnlyDictionary<string, string>? Replacements(ISession s, string id) =>
        string.Equals(id, ObjectSearchId, StringComparison.Ordinal)
            ? new Dictionary<string, string>(StringComparer.Ordinal) { [SegmentsToken] = SegmentsSource(s) }
            : null;

    /// <summary>
    /// 열 형식 표시: <c>VARCHAR(30)</c>, <c>NUMBER(10,2)</c>, <c>FLOAT(126)</c>. 그 밖은 서버가 준 이름 그대로.
    /// Tibero 는 문자 형식을 <c>VARCHAR</c>·<c>NVARCHAR</c> 로도 적으므로 <c>VARCHAR2</c> 계열과 함께 길이를 붙인다.
    /// </summary>
    internal static string TypeText(string dataType, long? length, long? precision, long? scale) => dataType switch
    {
        "NUMBER" when precision is { } p => scale is { } sc and not 0 ? $"NUMBER({p},{sc})" : $"NUMBER({p})",
        "NUMBER" when scale is 0 => "NUMBER(*,0)",
        "FLOAT" when precision is { } p => $"FLOAT({p})",
        "VARCHAR" or "NVARCHAR" or "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" or "RAW" when length is { } l => $"{dataType}({l})",
        _ => dataType,
    };

    /// <summary>
    /// 휴지통 삭제 시각. Tibero 의 <c>droptime</c> 은 문자열이라 형식이 확인되지 않았다(서버에 행이 없음):
    /// 날짜 값이면 그대로, 문자열이면 알려진 형식으로, 그래도 안 되면 일반 날짜 읽기로 읽고, 못 읽으면 null.
    /// </summary>
    internal static DateTime? DropTime(object? value)
    {
        if (TiberoSessionQuery.Date(value) is { } date)
        {
            return date;
        }

        var text = TiberoSessionQuery.Str(value)?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (DateTime.TryParseExact(text, DropTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
        {
            return exact;
        }

        return null; // 모르는 모양은 읽지 않는다(느슨한 해석은 시각만·월일 순서가 모호한 글도 받아 버린다)
    }

    private static string? TypeList(IReadOnlyList<string> types) => types.Count == 0 ? null : "|" + string.Join('|', types) + "|";

    private static Dictionary<string, object?> PatternBinds(SourceSearchBinds binds) =>
        new(StringComparer.Ordinal) { ["pattern"] = binds.Pattern, ["flags"] = binds.Flags };

    /// <summary>
    /// 정규식 검사(dual 한 줄)에서 난 서버 오류가 패턴 오류이면 그 오류, 아니면 null. Tibero 는 정규식 오류 번호표가 없어, 이 문장이 패턴 말고는
    /// 탈 데가 없다는 점을 근거로 「분류 불명(<see cref="DbErrorKind.Other"/>)」 인 서버 오류를 패턴 오류로 본다.
    /// 끊김·취소·권한·객체 없음은 이미 분류된 오류라 그대로 던진다. 드라이버 쪽 번호(9xxxx: 통신·내부)와 번호 없는 오류도
    /// 패턴 탓이 아니므로 그대로 던진다(Other 는 분류기가 모르는 번호 모두라 범위를 좁힌다). 실서버의 정규식 오류 번호는 IT 가 확인한다.
    /// </summary>
    internal static DbError? PatternError(Exception ex) =>
        TiberoErrorClassifier.ToDbError(ex, null) is { Kind: DbErrorKind.Other } found && IsServerNumber(found.Number) ? found : null;

    /// <summary>서버 오류 번호 범위(부호 무시 1~89999). 90000 넘는 번호는 드라이버 쪽 오류다(tibero-driver-session.md 1장).</summary>
    private static bool IsServerNumber(int? number) => number is { } n && n != int.MinValue && Math.Abs(n) is > 0 and < 90000;

    private static string DescribeError(DbError error) =>
        "정규식이 올바르지 않다 — " + (string.IsNullOrWhiteSpace(error.Message) ? error.Code : error.Message.ReplaceLineEndings(" "));

    /// <summary>기본 실행기: 이름 바인드 + 카탈로그 id 별 자리표시자 바꿈(<see cref="Replacements"/>).</summary>
    private static Task<TiberoSessionQuery.Table> RunWithReplacementsAsync(ISession s, string id, IReadOnlyDictionary<string, object?>? binds, CancellationToken ct) =>
        TiberoCatalogQuery.RunAsync(s, id, binds is null ? null : c =>
        {
            foreach (var (name, value) in binds)
            {
                TiberoSessionQuery.Bind(c, name, value);
            }
        }, Replacements(s, id), ct);
}
