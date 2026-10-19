using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;

namespace Oranda.Language.Editing;

/// <summary>
/// 편집 계획: 편집 가능 여부(<see cref="Capability"/>)와 결과 열 → 대상 테이블 열 이름.
/// <see cref="ColumnNames"/>[열 번호] 는 대상 테이블 열이면 그 이름(ROWID 열은 <c>ROWID</c>), 아니면 null.
/// </summary>
public sealed record RowEditPlan(EditCapability Capability, IReadOnlyList<string?> ColumnNames)
{
    /// <summary>키가 ROWID 면 true(DML 조건 <c>WHERE ROWID = :rid</c>).</summary>
    public bool UsesRowId { get; init; }

    /// <summary>조인 결과(키 보존을 결과 행으로 한 번 더 확인해야 함, <see cref="KeyPreservation"/>).</summary>
    public bool IsJoin { get; init; }

    /// <summary>FROM 절 테이블(인라인 뷰 제외). 조인일 때 편집 대상 후보.</summary>
    public IReadOnlyList<SelectTableRef> Candidates { get; init; } = [];
}

/// <summary>
/// PRD 6.5.1 행 식별 규칙. PK(없으면 NOT NULL 유니크 키) 전체 열 또는 ROWID 가 결과에 있어야 편집할 수 있다.
/// 둘 다 있으면 PK 를 쓴다. 집계·DISTINCT·GROUP BY·집합 연산은 편집 불가. 조인은 대상 테이블 열만 편집한다.
/// </summary>
public static class RowEditPlanner
{
    /// <summary>편집할 수 있는 값 종류(LOB 은 TSK-06-05 LOB 편집기, 나머지는 바인드 형식이 없다).</summary>
    public static bool IsEditableKind(ColumnDataKind kind) => kind is ColumnDataKind.Text or ColumnDataKind.Number
        or ColumnDataKind.BinaryFloat or ColumnDataKind.Date or ColumnDataKind.Timestamp or ColumnDataKind.TimestampTz
        or ColumnDataKind.Raw or ColumnDataKind.Boolean;

    public const string NoKeyMessage = "행을 하나로 특정할 키가 결과에 없습니다. ROWID 를 SELECT 에 추가하거나 PK 열을 모두 포함하세요.";

    public static RowEditPlan Plan(SqlStatement query, IReadOnlyList<ResultColumn> cols, TableKeyInfo keys)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(cols);
        ArgumentNullException.ThrowIfNull(keys);

        var none = new string?[cols.Count];
        if (query.Kind != StatementKind.Query)
        {
            return Blocked(EditBlockReason.NotAnalyzable, "질의 결과가 아니어서 편집할 수 없습니다.", none);
        }

        var shape = SelectAnalyzer.Analyze(query.Text);
        if (shape.Block != EditBlockReason.None)
        {
            return Blocked(shape.Block, shape.BlockMessage, none);
        }

        var candidates = shape.Tables.Where(t => !t.IsInlineView).ToList();
        var target = FindTarget(shape.Tables, keys.Table);
        if (target is null)
        {
            return Blocked(EditBlockReason.NotAnalyzable, $"편집 대상 테이블 {Display(keys.Table)} 이(가) FROM 절에 없습니다.", none) with
            {
                Candidates = candidates,
                IsJoin = shape.IsJoin,
            };
        }

        if (target.IsRemote)
        {
            return Blocked(EditBlockReason.NotAnalyzable, "DB 링크 테이블은 편집할 수 없습니다.", none) with { Candidates = candidates };
        }

        // 결과 열 → (출처 테이블, 열 이름)
        var sources = MapSources(shape, cols);
        var names = new string?[cols.Count];
        for (var i = 0; i < cols.Count; i++)
        {
            if (sources[i] is { } src && ReferenceEquals(src.Table, target))
            {
                names[i] = src.Column;
            }
        }

        var keyOrdinals = new List<int>();
        var usesRowId = false;
        if (keys.Kind != TableKeyKind.None && keys.KeyColumns.Count > 0)
        {
            foreach (var k in keys.KeyColumns)
            {
                var idx = Array.FindIndex(names, n => n is not null && n != "ROWID" && string.Equals(n, k, StringComparison.OrdinalIgnoreCase));
                if (idx < 0)
                {
                    keyOrdinals.Clear();
                    break;
                }

                keyOrdinals.Add(idx);
            }
        }

        if (keyOrdinals.Count == 0)
        {
            var rid = Array.IndexOf(names, "ROWID");
            if (rid >= 0)
            {
                keyOrdinals.Add(rid);
                usesRowId = true;
            }
        }

        if (keyOrdinals.Count == 0)
        {
            var single = shape.Tables.Count == 1;
            var withRowId = single ? SelectAnalyzer.AppendRowId(query.Text, shape) : null;
            var cap = new EditCapability(false, EditBlockReason.NoKeyColumns)
            {
                Message = NoKeyMessage,
                Target = keys.Table,
                CanAddRowId = withRowId is not null,
                QueryWithRowId = withRowId is null ? null : query with { Text = withRowId, Range = default },
            };
            return new RowEditPlan(cap, none) { Candidates = candidates, IsJoin = shape.IsJoin };
        }

        var editable = new List<int>();
        for (var i = 0; i < cols.Count; i++)
        {
            if (names[i] is { } n && n != "ROWID" && IsEditableKind(cols[i].Kind))
            {
                editable.Add(i);
            }
        }

        var capability = new EditCapability(true)
        {
            Target = keys.Table,
            KeyColumnOrdinals = keyOrdinals,
            EditableColumnOrdinals = editable,
            Message = shape.IsJoin ? $"조인 결과: {Display(keys.Table)} 열만 편집합니다." : null,
        };
        return new RowEditPlan(capability, names) { UsesRowId = usesRowId, IsJoin = shape.IsJoin, Candidates = candidates };
    }

    /// <summary>표시용 이름 <c>OWNER.NAME</c>.</summary>
    public static string Display(DbObjectRef table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return string.IsNullOrEmpty(table.Owner) ? table.Name : table.Owner + "." + table.Name;
    }

    private static RowEditPlan Blocked(EditBlockReason reason, string? message, string?[] names) =>
        new(new EditCapability(false, reason) { Message = message }, names);

    private static SelectTableRef? FindTarget(IReadOnlyList<SelectTableRef> tables, DbObjectRef table)
    {
        SelectTableRef? match = null;
        foreach (var t in tables)
        {
            if (t.IsInlineView || !string.Equals(t.Name, table.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (t.Owner is not null && !string.Equals(t.Owner, table.Owner, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (match is not null)
            {
                return null; // 같은 테이블이 두 번(자기 조인): 어느 쪽인지 모른다.
            }

            match = t;
        }

        return match;
    }

    private sealed record Source(SelectTableRef Table, string Column);

    private static Source?[] MapSources(SelectShape shape, IReadOnlyList<ResultColumn> cols)
    {
        var result = new Source?[cols.Count];
        var items = shape.Items;
        var stars = items.Count(i => i.IsStar);
        var single = shape.Tables.Count == 1 ? shape.Tables[0] : null;

        // 위치로 맞출 수 있으면(별 0개이고 수가 같음, 또는 별 1개) 항목 → 결과 열.
        var positional = (stars == 0 && items.Count == cols.Count) || (stars == 1 && cols.Count >= items.Count - 1);
        if (positional)
        {
            var starWidth = cols.Count - (items.Count - 1);
            var ord = 0;
            foreach (var item in items)
            {
                var width = item.IsStar ? starWidth : 1;
                for (var k = 0; k < width && ord < cols.Count; k++, ord++)
                {
                    var col = cols[ord];
                    if (item.IsStar)
                    {
                        var table = item.Qualifier is null ? single ?? FromBase(shape, col) : Resolve(shape, item.Qualifier);
                        if (table is not null && !table.IsInlineView)
                        {
                            result[ord] = new Source(table, col.Kind == ColumnDataKind.RowId ? "ROWID" : SelectAnalyzer.NormalizeIdentifier(col.BaseColumn ?? col.Name));
                        }
                    }
                    else if (item.Column is { } c)
                    {
                        var table = item.Qualifier is null ? single ?? FromBase(shape, col) : Resolve(shape, item.Qualifier);
                        if (table is not null && !table.IsInlineView)
                        {
                            result[ord] = new Source(table, c);
                        }
                    }
                }
            }

            return result;
        }

        // 위치를 모르면 드라이버가 알려 준 출처(BaseTable·BaseColumn)만 믿는다.
        for (var i = 0; i < cols.Count; i++)
        {
            var col = cols[i];
            if (col.BaseColumn is not null && FromBase(shape, col) is { IsInlineView: false } t)
            {
                result[i] = new Source(t, SelectAnalyzer.NormalizeIdentifier(col.BaseColumn));
            }
        }

        return result;
    }

    private static SelectTableRef? Resolve(SelectShape shape, string qualifier)
    {
        foreach (var t in shape.Tables)
        {
            if (t.Alias is not null ? t.Alias == qualifier : t.Name == qualifier)
            {
                return t;
            }
        }

        return null;
    }

    private static SelectTableRef? FromBase(SelectShape shape, ResultColumn col)
    {
        if (col.BaseTable is null)
        {
            return null;
        }

        var found = shape.Tables.Where(t => !t.IsInlineView
                                            && string.Equals(t.Name, col.BaseTable, StringComparison.OrdinalIgnoreCase)
                                            && (col.BaseOwner is null || t.Owner is null
                                                || string.Equals(t.Owner, col.BaseOwner, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return found.Count == 1 ? found[0] : null;
    }
}

/// <summary>조인 결과의 키 보존 확인: 같은 키가 두 행 이상에 나오면 대상 테이블은 키 보존이 아니다.</summary>
public static class KeyPreservation
{
    /// <param name="keyOrdinals">키 열 번호</param>
    /// <param name="rowCount">확인할 행 수(불러온 행)</param>
    /// <param name="read">(행, 열) → 값</param>
    /// <returns>처음 겹친 행 번호, 없으면 null. 키가 NULL 인 행(바깥 조인의 빈 쪽)은 건너뛴다.</returns>
    public static long? FindDuplicate(IReadOnlyList<int> keyOrdinals, long rowCount, Func<long, int, object?> read)
    {
        ArgumentNullException.ThrowIfNull(keyOrdinals);
        ArgumentNullException.ThrowIfNull(read);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (long r = 0; r < rowCount; r++)
        {
            var parts = new string[keyOrdinals.Count];
            var hasNull = false;
            for (var k = 0; k < keyOrdinals.Count; k++)
            {
                var v = read(r, keyOrdinals[k]);
                if (v is null or DBNull)
                {
                    hasNull = true;
                    break;
                }

                parts[k] = v is byte[] b ? Convert.ToHexString(b)
                    : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            }

            if (!hasNull && !seen.Add(string.Join('\u001f', parts)))
            {
                return r;
            }
        }

        return null;
    }
}
