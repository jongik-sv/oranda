using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Compare;
using Oranda.Language.Editing;

namespace Oranda.App.Features.PointCompare;

/// <summary>범위 좁히기 2번에 쓰는 문장 하나(설계 8.2): WHERE 글과 별칭, 바인드. <see cref="WholeTable"/> 이면 WHERE 가 없어 표 전체다.</summary>
public sealed record CompareStatementScope(string? WhereText, string? Alias, IReadOnlyDictionary<string, BindValue> Binds)
{
    /// <summary>한 행 INSERT 의 열 목록과 값 글(불러올 때 PK 조건으로 바꾼다).</summary>
    public IReadOnlyList<string> InsertColumns { get; init; } = [];

    public IReadOnlyList<string>? InsertValues { get; init; }

    public bool WholeTable => WhereText is null && InsertValues is null;
}

/// <summary>비교 요청: 표 하나와 기준 시점(필수), 커밋 시점(없으면 두 시점 비교), 범위 근거.</summary>
internal sealed record CompareRequest(DbObjectRef Table, long BasePoint)
{
    public DateTimeOffset? BaseAt { get; init; }
    public long? CommitPoint { get; init; }
    public DateTimeOffset? CommitAt { get; init; }
    public string? TransactionId { get; init; }
    public IReadOnlyList<CompareStatementScope> Statements { get; init; } = [];

    /// <summary>그리드가 고른 행의 키(<see cref="RollbackTable.KeyColumns"/> 순서, PK 가 없으면 ROWID 글 하나). 있으면 범위 좁히기를 건너뛴다.</summary>
    public IReadOnlyList<IReadOnlyList<object?>>? Keys { get; init; }

    /// <summary><see cref="Keys"/> 의 열 이름(그리드 순서). <c>ROWID</c> 하나면 ROWID 로 짝짓는다. null 이면 표 PK 순서로 본다.</summary>
    public IReadOnlyList<string>? KeyColumns { get; init; }
}

/// <summary>불러온 결과. <see cref="Error"/> 가 있으면 <see cref="Plan"/> 은 null.</summary>
internal sealed record CompareLoad(ComparePlan? Plan, string? Error)
{
    public IReadOnlyList<string> Notes { get; init; } = [];
    public string? Range { get; init; }
    public bool SnapshotTooOld { get; init; }
}

/// <summary>
/// 화면 3 의 불러오기(설계 8.2·8.4·14장): undo 안내·DDL 확인 → 범위 좁히기(VERSIONS_XID → 문장 WHERE → 표 전체) → 세 시점 읽기 → 분류.
/// DB 일은 <see cref="IPointCompareDb"/> 로만 한다.
/// </summary>
internal static class PointCompareLoader
{
    public const string RangeVersions = "내 트랜잭션이 바꾼 행(VERSIONS_XID)";
    public const string RangeWhere = "실행한 문장의 조건";
    public const string RangeTable = "테이블 전체";
    public const string RangeKeys = "고른 행";

    public static async Task<CompareLoad> LoadAsync(IPointCompareDb db, CompareRequest request, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            if (await db.TableAsync(request.Table, ct).ConfigureAwait(true) is not { } table)
            {
                return new CompareLoad(null, "뷰는 시점 비교로 되돌릴 수 없습니다.");
            }

            if (request.Keys is not null && request.KeyColumns is { } kc)
            {
                if (kc is [var only] && string.Equals(only, RowDmlBuilder.RowIdKey, StringComparison.OrdinalIgnoreCase))
                {
                    table = table with { KeyColumns = [] }; // 편집 모드(ROWID 키) 결과
                }
                else if (!kc.SequenceEqual(table.KeyColumns, StringComparer.Ordinal))
                {
                    if (kc.Count != table.KeyColumns.Count || table.KeyColumns.Any(k => !kc.Contains(k, StringComparer.Ordinal)))
                    {
                        return new CompareLoad(null, "고른 행의 키가 테이블 기본 키와 다릅니다.");
                    }

                    var order = table.KeyColumns.Select(k => kc.ToList().IndexOf(k)).ToArray();
                    request = request with { Keys = [.. request.Keys.Select(key => (IReadOnlyList<object?>)[.. order.Select(i => key[i])])] };
                }
            }

            if (request.Keys is { } gridKeys)
            {
                request = request with { Keys = [.. gridKeys.Select(k => KeyTexts(table, k))] };
            }

            var notes = new List<string>();
            if (await UndoNoteAsync(db, request, now, ct).ConfigureAwait(true) is { } undo)
            {
                notes.Add(undo);
            }

            if (request.BaseAt is { } baseAt && await db.LastDdlAsync(table.Table, ct).ConfigureAwait(true) is { } ddl && ddl > baseAt)
            {
                return new CompareLoad(null, "기준 시점 뒤 테이블 정의가 바뀌어(DDL) 그 시점으로 읽을 수 없습니다(ORA-01466). 테이블 정의가 바뀐 시각: "
                                             + ddl.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)) { Notes = notes };
            }

            // 미래 시점은 Tibero 에서 오류 없이 지금 데이터가 나온다(RB-13 실측) — 읽기 전에 막는다.
            var current = await db.CurrentPointAsync(ct).ConfigureAwait(true);
            if (current is null && db.Dbms == Dbms.Tibero)
            {
                return new CompareLoad(null, "지금 TSN 을 읽지 못해 시점을 확인할 수 없습니다(Tibero 는 지금보다 뒤의 시점도 오류 없이 지금 값을 돌려줍니다).") { Notes = notes };
            }

            if (current is { } cur && (request.BasePoint > cur || request.CommitPoint > cur))
            {
                return new CompareLoad(null, $"지금 {db.Dialect.PointName}({cur.ToString(CultureInfo.InvariantCulture)})보다 큰 시점입니다. 시점 번호를 확인하세요.") { Notes = notes };
            }

            var (keys, range, error) = await RangeAsync(db, table, request, ct).ConfigureAwait(true);
            if (error is not null)
            {
                return new CompareLoad(null, error) { Notes = notes };
            }

            var (baseRows, skippedKeys) = await ReadCountingAsync(db, table, request.BasePoint, keys, ct).ConfigureAwait(true);
            if (skippedKeys > 0)
            {
                notes.Add($"키 값을 리터럴로 쓸 수 없어 {skippedKeys.ToString(CultureInfo.InvariantCulture)} 행은 비교하지 못했습니다.");
            }

            var commitRows = request.CommitPoint is { } cp ? await ReadAsync(db, table, cp, keys, ct).ConfigureAwait(true) : null;
            var nowRows = await ReadAsync(db, table, null, keys, ct).ConfigureAwait(true);
            var plan = PointComparePlanner.Classify(table, baseRows, commitRows, nowRows);
            return new CompareLoad(plan, null) { Notes = notes, Range = range };
        }
        catch (PointCompareDbException e)
        {
            return Explain(db, e);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 메타데이터(표가 없음·이름 바뀜)·가져오는 중 드라이버 오류: 글로 분류한다(ORA-01555 등)
            return Explain(db, new PointCompareDbException(e.Message.ReplaceLineEndings(" "), Classify(e.Message)));
        }
    }

    private static DbError? Classify(string message)
        => message.Contains("ORA-01555", StringComparison.Ordinal) || message.Contains("ORA-08180", StringComparison.Ordinal) || message.Contains("TBR-21003", StringComparison.Ordinal)
           || message.Contains("TBR-21034", StringComparison.Ordinal)
            ? new DbError(string.Empty, message) { Kind = DbErrorKind.SnapshotTooOld }
            : message.Contains("ORA-01031", StringComparison.Ordinal) || message.Contains("ORA-01039", StringComparison.Ordinal)
                ? new DbError(string.Empty, message) { Kind = DbErrorKind.InsufficientPrivileges }
                : null;

    /// <summary>
    /// 원장 문서의 문장 하나 → 범위 근거(분석기로 WHERE·별칭을 읽고 바인드를 되살린다). 한 행 INSERT 는 넣은 값(열·값 글)을 담아
    /// 불러올 때 키 조건으로 바꾼다. 모양을 모르면 표 전체 근거(<see cref="CompareStatementScope.WholeTable"/>).
    /// 어느 표의 문장인지는 부르는 쪽이 원장의 문장 정보(시노님을 푼 표)로 정한다.
    /// </summary>
    public static CompareStatementScope ScopeOf(RollbackScriptStatement statement, IDmlAnalyzer analyzer)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(analyzer);
        var empty = new Dictionary<string, BindValue>();
        if (analyzer.Analyze(statement.Text) is not { } shape)
        {
            return new CompareStatementScope(null, null, empty);
        }

        var values = shape.Verb == DmlVerb.InsertValues && shape.InsertColumns.Count > 0 ? DmlShapeAnalyzer.InsertValues(statement.Text) : null;
        if (values is not null && values.Count != shape.InsertColumns.Count)
        {
            values = null;
        }

        // 쓴 바인드만 넘긴다(쓰지 않은 이름을 바인드하면 ORA-01036).
        var text = shape.WhereText ?? (values is null ? string.Empty : string.Join(" ", values));
        var used = System.Text.RegularExpressions.Regex.Matches(text, @":([A-Za-z_][A-Za-z0-9_$#]*|\d+)").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var binds = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in statement.Binds.Where(b => used.Contains(b.Name.TrimStart(':'))))
        {
            binds[b.Name.TrimStart(':')] = new BindValue(b.Type, b.Type == BindDataType.Number && decimal.TryParse(b.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : b.Value);
        }

        return new CompareStatementScope(shape.WhereText, shape.Alias, binds) { InsertColumns = shape.InsertColumns, InsertValues = values };
    }

    /// <summary>한 행 INSERT 근거 → 키 조건(PK 열이 열 목록에 모두 있을 때). 못 바꾸면 표 전체 근거.</summary>
    internal static CompareStatementScope Resolve(CompareStatementScope scope, RollbackTable table)
    {
        if (scope.WhereText is not null || scope.InsertValues is not { } values)
        {
            return scope;
        }

        var parts = new List<string>();
        foreach (var key in table.KeyColumns)
        {
            var i = scope.InsertColumns.ToList().IndexOf(key);
            if (i < 0)
            {
                return new CompareStatementScope(null, null, scope.Binds); // PK 없음·PK 열을 안 넣음(시퀀스·트리거): 표 전체
            }

            parts.Add(PointCompareSql.Quote(key) + " = " + values[i]);
        }

        return parts.Count == 0 ? new CompareStatementScope(null, null, scope.Binds) : new CompareStatementScope(string.Join(" AND ", parts), null, scope.Binds);
    }

    private static async Task<(IReadOnlyList<IReadOnlyList<object?>>? Keys, string Range, string? Error)> RangeAsync(IPointCompareDb db, RollbackTable table,
                                                                                                              CompareRequest request, CancellationToken ct)
    {
        if (request.Keys is { } given)
        {
            return (given, RangeKeys, null);
        }

        // 1. VERSIONS_XID(Oracle 만 — Tibero 카탈로그에는 VERSIONS 문장이 없다)
        if (db.Dbms == Dbms.Oracle && request.CommitPoint is { } commit && TransactionXid.ToRawHex(request.TransactionId) is { } xid)
        {
            try
            {
                var rows = await db.QueryAsync(PointCompareQueries.VersionKeys(table, db.Dialect, request.BasePoint, commit, xid, PointCompareSql.Quote), ct).ConfigureAwait(true);
                if (rows.Count > 0)
                {
                    return (Distinct(rows), RangeVersions, null);
                }
            }
            catch (PointCompareDbException)
            {
                // ORA-30052 등: 다음 방법
            }
        }

        // 2. 문장 WHERE 를 기준·커밋 두 시점에서(한 행 INSERT 는 넣은 값의 키 조건)
        var scopes = request.Statements.Select(s => Resolve(s, table)).ToList();
        if (scopes.Count > 0 && scopes.All(s => !s.WholeTable))
        {
            try
            {
                var keys = new List<object?[]>();
                foreach (var s in scopes)
                {
                    foreach (var point in new[] { request.BasePoint, request.CommitPoint }.OfType<long>())
                    {
                        keys.AddRange(await db.QueryAsync(PointCompareQueries.WhereKeys(table, db.Dialect, point, s.WhereText, s.Alias, s.Binds, PointCompareSql.Quote), ct)
                            .ConfigureAwait(true));
                    }
                }

                return (Distinct(keys), RangeWhere, null);
            }
            catch (PointCompareDbException e) when (e.Error?.Kind != DbErrorKind.SnapshotTooOld)
            {
                // 다음 방법
            }
        }

        // 3. 표 전체(10만 행 이하)
        var count = SessionPointCompareDb.ToLong((await db.QueryAsync(PointCompareQueries.Count(table, db.Dialect, null, PointCompareSql.Quote), ct).ConfigureAwait(true))
            .First0()) ?? long.MaxValue;
        return count > PointCompareQueries.FullTableLimit ? (null, RangeTable, PointCompareQueries.TooManyRows + ".") : (null, RangeTable, null);
    }

    private static async Task<IReadOnlyList<CompareSnapshotRow>> ReadAsync(IPointCompareDb db, RollbackTable table, long? point,
                                                                            IReadOnlyList<IReadOnlyList<object?>>? keys, CancellationToken ct)
        => (await ReadCountingAsync(db, table, point, keys, ct).ConfigureAwait(true)).Rows;

    private static async Task<(IReadOnlyList<CompareSnapshotRow> Rows, int Skipped)> ReadCountingAsync(IPointCompareDb db, RollbackTable table, long? point,
                                                                                                     IReadOnlyList<IReadOnlyList<object?>>? keys, CancellationToken ct)
    {
        if (keys is { Count: 0 })
        {
            return ([], 0);
        }

        var rows = new List<CompareSnapshotRow>();
        var queries = PointCompareQueries.ReadRows(table, db.Dialect, point, keys, out var skipped, PointCompareSql.Quote);
        foreach (var q in queries)
        {
            foreach (var r in await db.QueryAsync(q, ct).ConfigureAwait(true))
            {
                var n = table.Columns.Count;
                rows.Add(new CompareSnapshotRow(r.Take(n).Select(v => v is DBNull ? null : v).ToArray()) { RowId = r.Length > n ? r[n] as string : null });
            }
        }

        return (rows, skipped);
    }

    /// <summary>
    /// 그리드가 준 키 값(.NET 값) → 방언 리터럴이 받는 고정 형식 글(설계 6.6 의 읽기 식과 같은 모양). 숫자는 decimal, 날짜·시각은 마스크 모양 글.
    /// 바꿀 수 없는 값은 그대로 둔다(리터럴에서 걸러져 「비교하지 못함」으로 센다).
    /// </summary>
    internal static IReadOnlyList<object?> KeyTexts(RollbackTable table, IReadOnlyList<object?> key)
    {
        if (table.KeyColumns.Count == 0)
        {
            return [.. key.Select(v => v is null or string ? v : Convert.ToString(v, CultureInfo.InvariantCulture))]; // ROWID 글
        }

        var result = new object?[key.Count];
        for (var i = 0; i < key.Count; i++)
        {
            var kind = i < table.KeyColumns.Count ? table.Columns.FirstOrDefault(c => c.Name == table.KeyColumns[i])?.Kind : null;
            result[i] = (kind, key[i]) switch
            {
                (_, null or DBNull) => null,
                (RollbackValueKind.Number, double d) when !double.IsNaN(d) && !double.IsInfinity(d) => (decimal)d,
                (RollbackValueKind.Number, float f) when !float.IsNaN(f) && !float.IsInfinity(f) => (decimal)f,
                (RollbackValueKind.Number, int or long or short or byte) => Convert.ToDecimal(key[i], CultureInfo.InvariantCulture),
                (RollbackValueKind.Date, DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                (RollbackValueKind.Timestamp, DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "00",
                (RollbackValueKind.TimestampTz, DateTimeOffset o) => o.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "00 " + o.ToString("zzz", CultureInfo.InvariantCulture),
                (RollbackValueKind.TimestampLtz, DateTimeOffset o) => o.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "00",
                (RollbackValueKind.Text or RollbackValueKind.NationalText, var v and not string) => Convert.ToString(v, CultureInfo.InvariantCulture),
                (_, var v) => v,
            };
        }

        return result;
    }

    private static List<IReadOnlyList<object?>> Distinct(IEnumerable<object?[]> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. rows.Select(r => (IReadOnlyList<object?>)r.Select(v => v is DBNull ? null : v).ToArray()).Where(k => seen.Add(PointComparePlanner.KeyText(k)))];
    }

    private static async Task<string?> UndoNoteAsync(IPointCompareDb db, CompareRequest request, DateTimeOffset now, CancellationToken ct)
    {
        if (await db.UndoRetentionAsync(ct).ConfigureAwait(true) is not { } seconds)
        {
            return null;
        }

        var retention = $"undo_retention {seconds.ToString(CultureInfo.InvariantCulture)}초({(seconds / 60m).ToString("0.#", CultureInfo.InvariantCulture)}분)";
        if ((request.CommitAt ?? request.BaseAt) is not { } since)
        {
            return retention + "입니다. 이보다 오래된 시점은 읽지 못할 수 있습니다.";
        }

        var minutes = Math.Max(0, (int)(now - since).TotalMinutes);
        var what = request.CommitAt is null ? "기준 시점" : "커밋";
        // undo_retention 은 보장이 아니라 목표값이라 「읽을 수 있다」고 단정하지 않는다(설계 8.4).
        return $"{retention}이고 {what} 뒤 {minutes.ToString(CultureInfo.InvariantCulture)}분이 지났습니다. 시간이 더 지나면 "
               + (db.Dbms == Dbms.Tibero ? "TBR-21003" : "ORA-01555") + " 로 기준 시점을 읽지 못할 수 있습니다.";
    }

    private static CompareLoad Explain(IPointCompareDb db, PointCompareDbException e)
    {
        if (e.Error?.Kind == DbErrorKind.SnapshotTooOld)
        {
            var why = db.Dbms == Dbms.Tibero
                ? "기준 시점의 값을 읽지 못했습니다: undo 를 잃었거나 그 시점 뒤 테이블 정의가 바뀌었습니다(DDL)."
                : "기준 시점의 값을 undo 에서 잃었습니다.";
            return new CompareLoad(null, why + " 원복 스크립트가 있으면 그것을 쓰세요. (" + e.Message + ")") { SnapshotTooOld = true };
        }

        if (FlashbackErrorAdvisor.IsDefinitionChanged(e.Message))
        {
            return new CompareLoad(null, "기준 시점 뒤 테이블 정의가 바뀌어(DDL) 그 시점으로 읽을 수 없습니다. (" + e.Message + ")");
        }

        if (e.Error?.Kind == DbErrorKind.InsufficientPrivileges)
        {
            return new CompareLoad(null, "다른 스키마 테이블은 FLASHBACK 권한이 있어야 합니다. (" + e.Message + ")");
        }

        return new CompareLoad(null, "비교하지 못했습니다: " + e.Message);
    }
}
