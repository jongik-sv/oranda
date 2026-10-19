using System.Text;

namespace Oranda.Core.Grid;

/// <summary>복사 형식(PRD GRID-03).</summary>
public enum CopyFormat
{
    Tsv,
    Csv,
    Insert,
    InList,
}

/// <summary>복사 옵션.</summary>
public sealed record CopyOptions
{
    /// <summary>TSV·CSV 첫 줄에 열 이름을 넣는다.</summary>
    public bool IncludeHeaders { get; init; }

    /// <summary>INSERT 대상 테이블 이름(그대로 쓴다. 스키마 접두어 포함 가능).</summary>
    public string TableName { get; init; } = "TABLE_NAME";

    /// <summary>TSV·CSV 값 표시 옵션. 기본은 복사용(<see cref="GridDisplayOptions.Plain"/>).</summary>
    public GridDisplayOptions Display { get; init; } = GridDisplayOptions.Plain;

    /// <summary>줄 구분. null 이면 CSV 는 CRLF, 나머지는 LF.</summary>
    public string? NewLine { get; init; }

    /// <summary>IN 목록에서 같은 값을 한 번만 넣는다.</summary>
    public bool DistinctInList { get; init; } = true;
}

/// <summary>선택 범위 복사 형식(TSV/CSV/INSERT/IN 목록, TRD 4.4). 소유: TSK-06-03.</summary>
public static class SelectionCopyFormatter
{
    /// <summary>
    /// 선택 범위를 글자로 만든다.
    /// </summary>
    /// <param name="format">복사 형식</param>
    /// <param name="columns">선택된 열(표시 순서)</param>
    /// <param name="rows">행 지향 값 <c>rows[행][열 위치]</c>. 열 위치는 <paramref name="columns"/> 와 같은 순서</param>
    /// <param name="options">옵션</param>
    public static string Format(CopyFormat format, IReadOnlyList<ResultColumn> columns, IReadOnlyList<IReadOnlyList<object?>> rows, CopyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        var o = options ?? new CopyOptions();
        return format switch
        {
            CopyFormat.Tsv => Delimited(columns, rows, o, '\t', o.NewLine ?? "\n"),
            CopyFormat.Csv => Delimited(columns, rows, o, ',', o.NewLine ?? "\r\n"),
            CopyFormat.Insert => Insert(columns, rows, o),
            CopyFormat.InList => InList(columns, rows, o),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    private static string Delimited(IReadOnlyList<ResultColumn> columns, IReadOnlyList<IReadOnlyList<object?>> rows, CopyOptions o, char sep, string nl)
    {
        var lines = new List<string>(rows.Count + 1);
        if (o.IncludeHeaders)
        {
            lines.Add(string.Join(sep, columns.Select(c => Escape(c.Name, sep))));
        }

        foreach (var row in rows)
        {
            lines.Add(string.Join(sep, columns.Select((c, i) => Escape(CellValueFormatter.Format(c, i < row.Count ? row[i] : null, o.Display), sep))));
        }

        return string.Join(nl, lines);
    }

    private static string Escape(string s, char sep)
    {
        var needs = s.Contains(sep, StringComparison.Ordinal) || s.AsSpan().IndexOfAny('"', '\r', '\n') >= 0;
        return needs ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
    }

    private static string Insert(IReadOnlyList<ResultColumn> columns, IReadOnlyList<IReadOnlyList<object?>> rows, CopyOptions o)
    {
        var names = string.Join(", ", columns.Select(c => CellValueFormatter.QuoteIdentifier(c.Name)));
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            if (sb.Length > 0)
            {
                sb.Append(o.NewLine ?? "\n");
            }

            var values = string.Join(", ", columns.Select((c, i) => CellValueFormatter.ToSqlLiteral(c, i < row.Count ? row[i] : null)));
            sb.Append("INSERT INTO ").Append(o.TableName).Append(" (").Append(names).Append(") VALUES (").Append(values).Append(");");
        }

        return sb.ToString();
    }

    private static string InList(IReadOnlyList<ResultColumn> columns, IReadOnlyList<IReadOnlyList<object?>> rows, CopyOptions o)
    {
        var items = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                var v = i < row.Count ? row[i] : null;
                if (v is null or DBNull)
                {
                    continue; // NULL 은 IN 으로 맞지 않으므로 뺀다
                }

                var lit = CellValueFormatter.ToSqlLiteral(columns[i], v);
                if (!o.DistinctInList || seen.Add(lit))
                {
                    items.Add(lit);
                }
            }
        }

        return "(" + string.Join(", ", items) + ")";
    }
}
