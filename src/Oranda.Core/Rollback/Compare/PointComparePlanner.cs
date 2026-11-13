using System.Globalization;
using System.Text;
using Oranda.Core.Text;

namespace Oranda.Core.Rollback.Compare;

/// <summary>
/// 시점 비교 계획기(설계 8.2·8.3, RB-31). 기준(B)·커밋 직후(C, 없을 수 있음)·지금(N) 세 시점의 행을 키(PK, 없으면 ROWID)로 짝지어 나누고,
/// 되돌리는 DML 을 리터럴로 만든다(값·리터럴 규칙은 방언, 6.6). 순수 함수라 DB 없이 시험한다.
/// </summary>
public static class PointComparePlanner
{
    public const string NoKeyWarning = "기본 키가 없는 테이블이라 ROWID 로 견줍니다. 지웠다가 다시 넣은 행은 다른 행으로 보입니다.";
    public const string NoCommitPointWarning = "커밋 시점이 없어 기준 시점과 지금을 견줍니다. 그 사이 다른 사람의 변경까지 차이로 잡힙니다.";

    /// <summary>
    /// 행을 나눈다. <paramref name="commit"/> 이 null 이면 B 와 N 두 시점만 견준다(충돌 없음).
    /// <paramref name="revertColumns"/> 는 되돌릴 수 있는 열 자리(기본: 키·가상·큰 열을 뺀 모든 열). 바뀐 열은 이 안에서만 센다.
    /// </summary>
    public static ComparePlan Classify(RollbackTable table, IReadOnlyList<CompareSnapshotRow> baseRows, IReadOnlyList<CompareSnapshotRow>? commit,
                                       IReadOnlyList<CompareSnapshotRow> now, IReadOnlyList<int>? revertColumns = null, IEqualityComparer<object?>? comparer = null,
                                       Func<IReadOnlyList<object?>, string>? keyText = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(baseRows);
        ArgumentNullException.ThrowIfNull(now);
        var same = comparer ?? CompareValues.Default;
        var keyPos = KeyPositions(table);
        var settable = revertColumns ?? DefaultRevertColumns(table, keyPos);
        var comparable = Enumerable.Range(0, table.Columns.Count).Where(i => table.Columns[i].Kind != RollbackValueKind.Large).ToArray();

        var text = keyText ?? KeyText;
        var b = Index(table, keyPos, baseRows, text);
        var c = commit is null ? null : Index(table, keyPos, commit, text);
        var n = Index(table, keyPos, now, text);
        var keys = new List<string>(b.Keys);
        var seen = new HashSet<string>(b.Keys, StringComparer.Ordinal);
        keys.AddRange((c?.Keys ?? Enumerable.Empty<string>()).Concat(n.Keys).Where(seen.Add));

        var rows = new List<CompareRow>(keys.Count);
        foreach (var k in keys)
        {
            var rb = b.GetValueOrDefault(k);
            var rn = n.GetValueOrDefault(k);
            var rc = c?.GetValueOrDefault(k);
            var any = rb ?? rc ?? rn!;
            var key = keyPos.Count == 0 ? (IReadOnlyList<object?>)[any.RowId] : [.. keyPos.Select(p => any.Values[p])];
            var (kind, action) = c is null ? TwoPoint(rb, rn, settable, same) : ThreePoint(rb, rc, rn, settable, comparable, same);
            var changed = action == CompareAction.Update
                ? Changed(rb!.Values, (kind == CompareKind.Conflict || c is null ? rn : rc)!.Values, settable, same)
                : [];
            if (action == CompareAction.Update && changed.Count == 0)
            {
                (kind, action) = (CompareKind.Same, CompareAction.None);
            }

            rows.Add(new CompareRow(kind, action, key)
            {
                RowId = any.RowId,
                Base = rb?.Values,
                Commit = rc?.Values,
                Now = rn?.Values,
                ChangedColumns = changed,
            });
        }

        var warnings = new List<string>();
        if (keyPos.Count == 0)
        {
            warnings.Add(NoKeyWarning);
        }

        if (commit is null)
        {
            warnings.Add(NoCommitPointWarning);
        }

        return new ComparePlan(table, rows, commit is not null) { Warnings = warnings };
    }

    /// <summary>
    /// 되돌리는 DML(설계 8.3). 순서는 되살림 → 되돌림 → 지움(부모·자식 순서를 모르므로 원복 스크립트와 같게).
    /// 충돌 행은 <paramref name="includeConflicts"/> 일 때만 N 값 조건을 넣어 만든다. UPDATE·DELETE 조건에는 지금 값을 넣어,
    /// 미리 본 뒤 다른 세션이 바꾼 행은 0행으로 끝난다.
    /// </summary>
    public static CompareScript BuildDml(ComparePlan plan, IRollbackDialect dialect, bool includeConflicts = false, Func<string, string>? quote = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dialect);
        var q = quote ?? (n => SqlIdentifier.Quote(n));
        var table = plan.Table;
        var name = string.IsNullOrEmpty(table.Table.Owner) ? q(table.Table.Name) : q(table.Table.Owner) + "." + q(table.Table.Name);
        var keyPos = KeyPositions(table);
        var statements = new List<CompareStatement>();
        var skipped = new List<(CompareRow, string)>();
        foreach (var action in new[] { CompareAction.Insert, CompareAction.Update, CompareAction.Delete })
        {
            foreach (var row in plan.Rows.Where(r => r.Action == action && (r.Kind != CompareKind.Conflict || includeConflicts)))
            {
                var sql = action switch
                {
                    CompareAction.Insert => Insert(table, name, row, dialect, q, out var why) ?? Skip(why),
                    CompareAction.Update => Update(table, name, keyPos, row, dialect, q, out var why) ?? Skip(why),
                    _ => Delete(table, name, keyPos, row, dialect, q, out var why) ?? Skip(why),
                };
                if (sql is not null)
                {
                    statements.Add(new CompareStatement(row, sql) { Note = Note(row, action, table) });
                }

                string? Skip(string? reason)
                {
                    skipped.Add((row, reason ?? "리터럴로 쓸 수 없는 값"));
                    return null;
                }
            }
        }

        return new CompareScript(statements, skipped);
    }

    /// <summary>미리보기·「새 편집기에 열기」 글: 머리 주석 한 줄과 문장마다 <c>;</c>(충돌은 뒤에 주석).</summary>
    public static string Format(CompareScript script, string? header = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        var sb = new StringBuilder();
        if (header is { Length: > 0 })
        {
            sb.Append("-- ").AppendLine(header);
        }

        foreach (var s in script.Statements)
        {
            sb.Append(s.Sql).Append(';');
            if (s.Note is { } note)
            {
                sb.Append(" -- ").Append(note);
            }

            sb.AppendLine();
        }

        foreach (var (row, reason) in script.Skipped)
        {
            sb.Append("-- 만들지 못함(").Append(KeyText(row.Key)).Append("): ").AppendLine(reason);
        }

        return sb.ToString();
    }

    /// <summary>키 열 자리(<see cref="RollbackTable.KeyColumns"/> 순서). 비면 ROWID 로 짝짓는다.</summary>
    public static IReadOnlyList<int> KeyPositions(RollbackTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var list = new List<int>(table.KeyColumns.Count);
        foreach (var k in table.KeyColumns)
        {
            var i = IndexOf(table, k);
            if (i < 0)
            {
                throw new ArgumentException($"키 열 {k} 이(가) 열 목록에 없습니다.", nameof(table));
            }

            list.Add(i);
        }

        return list;
    }

    /// <summary>짝짓기 글(정수 계열은 decimal 로 맞추고, 형식을 함께 적어 1 과 "1" 을 가른다).</summary>
    public static string KeyText(IReadOnlyList<object?> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var sb = new StringBuilder();
        foreach (var v in key)
        {
            sb.Append(v switch
            {
                null or DBNull => "~",
                byte[] bytes => "x" + Convert.ToHexString(bytes),
                decimal or int or long or short or byte or sbyte or ushort or uint or ulong =>
                    "n:" + Convert.ToDecimal(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                string str => "s:" + str,
                DateTime dt => "d:" + dt.Ticks.ToString(CultureInfo.InvariantCulture),
                DateTimeOffset dto => "o:" + dto.UtcTicks.ToString(CultureInfo.InvariantCulture),
                double dbl => "f:" + dbl.ToString("R", CultureInfo.InvariantCulture),
                float flt => "f:" + flt.ToString("R", CultureInfo.InvariantCulture),
                IFormattable f => v.GetType().Name + ":" + f.ToString(null, CultureInfo.InvariantCulture),
                _ => v.GetType().Name + ":" + v,
            }).Append('\u001f');
        }

        return sb.ToString();
    }

    private static (CompareKind, CompareAction) TwoPoint(CompareSnapshotRow? b, CompareSnapshotRow? n, IReadOnlyList<int> settable, IEqualityComparer<object?> same)
        => (b, n) switch
        {
            (not null, not null) => Equal(b.Values, n.Values, settable, same) ? (CompareKind.Same, CompareAction.None) : (CompareKind.Revert, CompareAction.Update),
            (not null, null) => (CompareKind.Restore, CompareAction.Insert),
            (null, not null) => (CompareKind.Remove, CompareAction.Delete),
            _ => (CompareKind.Same, CompareAction.None),
        };

    private static (CompareKind, CompareAction) ThreePoint(CompareSnapshotRow? b, CompareSnapshotRow? c, CompareSnapshotRow? n, IReadOnlyList<int> settable,
                                                           IReadOnlyList<int> comparable, IEqualityComparer<object?> same)
    {
        switch (b, c)
        {
            case (not null, not null):
                if (Equal(b.Values, c.Values, settable, same))
                {
                    return (CompareKind.Same, CompareAction.None);
                }

                if (n is null)
                {
                    return (CompareKind.Conflict, CompareAction.Insert); // 커밋 뒤 다른 세션이 지움
                }

                return Equal(c.Values, n.Values, comparable, same) ? (CompareKind.Revert, CompareAction.Update) : (CompareKind.Conflict, CompareAction.Update);
            case (not null, null):
                return n is null ? (CompareKind.Restore, CompareAction.Insert) : (CompareKind.Conflict, CompareAction.Update);
            case (null, not null):
                if (n is null)
                {
                    return (CompareKind.Same, CompareAction.None); // 넣은 행을 그 뒤 누군가 이미 지움
                }

                return Equal(c.Values, n.Values, comparable, same) ? (CompareKind.Remove, CompareAction.Delete) : (CompareKind.Conflict, CompareAction.Delete);
            default:
                return (CompareKind.Same, CompareAction.None); // 기준·커밋 뒤에 생긴 행: 이 실행과 무관
        }
    }

    private static string? Update(RollbackTable table, string name, IReadOnlyList<int> keyPos, CompareRow row, IRollbackDialect dialect, Func<string, string> q, out string? why)
    {
        why = null;
        if (row.Base is not { } b || row.Now is not { } n)
        {
            return null;
        }

        var sb = new StringBuilder("UPDATE ").Append(name).Append(" SET ");
        for (var i = 0; i < row.ChangedColumns.Count; i++)
        {
            var p = row.ChangedColumns[i];
            if (Lit(table.Columns[p], b[p], dialect, out why) is not { } lit)
            {
                return null;
            }

            sb.Append(i == 0 ? string.Empty : ", ").Append(q(table.Columns[p].Name)).Append(" = ").Append(lit);
        }

        sb.Append(" WHERE ");
        if (!Where(sb, table, keyPos, row, row.ChangedColumns, n, dialect, q, out why))
        {
            return null;
        }

        return sb.ToString();
    }

    private static string? Insert(RollbackTable table, string name, CompareRow row, IRollbackDialect dialect, Func<string, string> q, out string? why)
    {
        why = null;
        if (row.Base is not { } b)
        {
            return null;
        }

        var cols = new List<string>();
        var vals = new List<string>();
        for (var i = 0; i < table.Columns.Count; i++)
        {
            var col = table.Columns[i];
            if (col.IsVirtual)
            {
                continue;
            }

            if (col.Kind == RollbackValueKind.Large)
            {
                // 큰 열은 값 대신 NULL 여부 표지로 읽는다(방언 읽기 식, 1 = NULL). NULL 이면 빼고 넣는다.
                if (b[i] is null or DBNull || CompareValues.Default.Equals(b[i], 1m) || b[i] is "1")
                {
                    continue;
                }

                why = $"큰 열 {col.Name} 의 값은 되살릴 수 없습니다";
                return null;
            }

            if (Lit(col, b[i], dialect, out why) is not { } lit)
            {
                return null;
            }

            cols.Add(q(col.Name));
            vals.Add(lit);
        }

        return "INSERT INTO " + name + " (" + string.Join(", ", cols) + ") VALUES (" + string.Join(", ", vals) + ")";
    }

    /// <summary>문장 뒤 주석: 충돌, 그리고 ID 열이 있는 표의 되살림(GENERATED ALWAYS 면 ORA-32795 로 거부된다, RD-12).</summary>
    private static string? Note(CompareRow row, CompareAction action, RollbackTable table)
    {
        var id = action == CompareAction.Insert && table.Columns.FirstOrDefault(c => c.IsIdentity) is { } col
            ? $"ID 열 {col.Name}: GENERATED ALWAYS 면 넣을 수 없습니다(ORA-32795)"
            : null;
        var conflict = row.Kind == CompareKind.Conflict ? "충돌" : null;
        return conflict is null ? id : id is null ? conflict : conflict + " · " + id;
    }

    private static string? Delete(RollbackTable table, string name, IReadOnlyList<int> keyPos, CompareRow row, IRollbackDialect dialect, Func<string, string> q, out string? why)
    {
        why = null;
        if (row.Now is not { } n)
        {
            return null;
        }

        var all = Enumerable.Range(0, table.Columns.Count)
            .Where(i => table.Columns[i].Kind != RollbackValueKind.Large && !table.Columns[i].IsVirtual && !keyPos.Contains(i))
            .ToArray();
        var sb = new StringBuilder("DELETE FROM ").Append(name).Append(" WHERE ");
        return Where(sb, table, keyPos, row, all, n, dialect, q, out why) ? sb.ToString() : null;
    }

    /// <summary>키 조건(PK = 값, 또는 ROWID) 그리고 열 = 지금 값(NULL 은 IS NULL).</summary>
    private static bool Where(StringBuilder sb, RollbackTable table, IReadOnlyList<int> keyPos, CompareRow row, IReadOnlyList<int> columns,
                              IReadOnlyList<object?> now, IRollbackDialect dialect, Func<string, string> q, out string? why)
    {
        why = null;
        var first = true;
        if (keyPos.Count == 0)
        {
            if (row.RowId is not { Length: > 0 } rid)
            {
                why = "ROWID 를 읽지 못했습니다";
                return false;
            }

            sb.Append("ROWID = ").Append(dialect.RowIdLiteral(rid));
            first = false;
        }

        foreach (var p in keyPos.Concat(columns))
        {
            sb.Append(first ? string.Empty : " AND ");
            first = false;
            var col = table.Columns[p];
            if (now[p] is null or DBNull)
            {
                sb.Append(q(col.Name)).Append(" IS NULL");
                continue;
            }

            if (Lit(col, now[p], dialect, out why) is not { } lit)
            {
                return false;
            }

            sb.Append(q(col.Name)).Append(" = ").Append(lit);
        }

        return true;
    }

    private static string? Lit(RollbackColumn col, object? value, IRollbackDialect dialect, out string? why)
    {
        var lit = dialect.Literal(col, value is DBNull ? null : value, out why);
        why ??= lit is null ? $"{col.Name} 값을 리터럴로 쓸 수 없습니다" : null;
        return lit;
    }

    private static Dictionary<string, CompareSnapshotRow> Index(RollbackTable table, IReadOnlyList<int> keyPos, IReadOnlyList<CompareSnapshotRow> rows,
                                                                Func<IReadOnlyList<object?>, string> keyText)
    {
        var map = new Dictionary<string, CompareSnapshotRow>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (r.Values.Count != table.Columns.Count)
            {
                throw new ArgumentException("행 값 수가 열 수와 다릅니다.", nameof(rows));
            }

            var key = keyPos.Count == 0 ? r.RowId is { } rid ? "r:" + rid : null : keyText([.. keyPos.Select(p => r.Values[p])]);
            if (key is not null)
            {
                map[key] = r;
            }
        }

        return map;
    }

    private static IReadOnlyList<int> DefaultRevertColumns(RollbackTable table, IReadOnlyList<int> keyPos)
        => [.. Enumerable.Range(0, table.Columns.Count).Where(i => !keyPos.Contains(i) && !table.Columns[i].IsVirtual && table.Columns[i].Kind != RollbackValueKind.Large)];

    private static bool Equal(IReadOnlyList<object?> x, IReadOnlyList<object?> y, IReadOnlyList<int> columns, IEqualityComparer<object?> same)
        => columns.All(p => same.Equals(x[p], y[p]));

    private static List<int> Changed(IReadOnlyList<object?> from, IReadOnlyList<object?> to, IReadOnlyList<int> columns, IEqualityComparer<object?> same)
        => [.. columns.Where(p => !same.Equals(from[p], to[p]))];

    private static int IndexOf(RollbackTable table, string column)
    {
        for (var i = 0; i < table.Columns.Count; i++)
        {
            if (string.Equals(table.Columns[i].Name, column, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>값 같음: NULL 과 DBNull 은 같고, 바이트 배열은 내용으로, 숫자는 decimal 로 견준다.</summary>
public sealed class CompareValues : IEqualityComparer<object?>
{
    public static CompareValues Default { get; } = new();

    public new bool Equals(object? x, object? y)
    {
        x = x is DBNull ? null : x;
        y = y is DBNull ? null : y;
        if (x is null || y is null)
        {
            return x is null && y is null;
        }

        if (x is byte[] a && y is byte[] b)
        {
            return a.AsSpan().SequenceEqual(b);
        }

        if (IsNumber(x) && IsNumber(y) && x is not (double or float) && y is not (double or float))
        {
            return Convert.ToDecimal(x, CultureInfo.InvariantCulture) == Convert.ToDecimal(y, CultureInfo.InvariantCulture);
        }

        return x.Equals(y);
    }

    public int GetHashCode(object? obj) => obj switch
    {
        null or DBNull => 0,
        byte[] b => b.Length,
        _ when IsNumber(obj) && obj is not (double or float) => Convert.ToDecimal(obj, CultureInfo.InvariantCulture).GetHashCode(),
        _ => obj.GetHashCode(),
    };

    private static bool IsNumber(object o) => o is decimal or int or long or short or byte or sbyte or ushort or uint or ulong or double or float;
}
