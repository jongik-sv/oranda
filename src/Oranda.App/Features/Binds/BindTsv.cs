using System.Text;
using Oranda.Core.Execution;

namespace Oranda.App.Features.Binds;

/// <summary>바인드 변수 블록 복사·붙여 넣기(TSV 4열: 변수·값·자료형·방향, TSK-03-09). 오렌지 Bind Variable List 의 블록 복사와 같은 모양.</summary>
public static class BindTsv
{
    public static readonly string[] Header = ["변수", "값", "자료형", "방향"];

    /// <summary>붙여 넣은 글 한 줄: 변수·값·자료형(없으면 null)·방향(없으면 null).</summary>
    public sealed record Entry(string Name, string Value, BindDataType? Type, BindDirection? Direction);

    /// <summary>고른 줄을 TSV 로. 머리 줄은 <paramref name="withHeader"/> 일 때만. 값 안의 탭·줄바꿈·따옴표는 따옴표로 감싼다.</summary>
    public static string Format(IEnumerable<BindVariableRow> rows, bool withHeader = false)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder();
        if (withHeader)
        {
            sb.Append(string.Join('\t', Header)).Append('\n');
        }

        foreach (var r in rows)
        {
            sb.Append(r.Name.Trim()).Append('\t').Append(Quote(r.Text)).Append('\t')
                .Append(r.TypeChoice.Label).Append('\t').Append(r.DirectionChoice.Label).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// TSV 를 읽는다. 열은 2개(변수·값)~4개. 머리 줄(첫 줄의 첫 칸이 「변수」·NAME)은 건너뛴다.
    /// 자료형·방향 칸은 알아듣지 못하거나 없으면 null(= 그 속성은 건드리지 않음).
    /// </summary>
    public static IReadOnlyList<Entry> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var entries = new List<Entry>();
        var first = true;
        foreach (var cells in Records(text))
        {
            if (cells.Count == 1 && cells[0].Trim().Length == 0)
            {
                continue;
            }

            var head = cells[0].Trim().TrimStart(':').Trim();
            if (first)
            {
                first = false;
                if (head.Equals("변수", StringComparison.Ordinal) || head.Equals("NAME", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (head.Length == 0)
            {
                continue;
            }

            entries.Add(new Entry(
                head,
                cells.Count > 1 ? cells[1] : string.Empty,
                cells.Count > 2 ? ParseType(cells[2]) : null,
                cells.Count > 3 ? ParseDirection(cells[3]) : null));
        }

        return entries;
    }

    /// <summary>
    /// 붙여 넣기를 목록에 적용한다. 이름이 같은 줄(대소문자·앞 콜론 무시)에 채운다. 목록에 없어도 <paramref name="addable"/>
    /// (탭의 SQL 이 쓰는 이름 — 아직 실행하지 않아 줄이 없을 때)에 있으면 줄을 만들어 채우고, 그 밖의 이름은 세어서 돌려준다.
    /// </summary>
    public static (int Applied, int Skipped) Apply(BindVariableStore store, string text, string? dateFormat = null, IEnumerable<string>? addable = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var canAdd = new HashSet<string>(addable ?? [], StringComparer.OrdinalIgnoreCase);
        var applied = 0;
        var skipped = 0;
        foreach (var e in Parse(text))
        {
            var row = store.Find(e.Name) ?? (canAdd.Contains(e.Name) ? store.Add(e.Name) : null);
            if (row is null)
            {
                skipped++;
                continue;
            }

            if (e.Type is { } t)
            {
                row.Type = t;
            }

            if (e.Direction is { } d)
            {
                row.Direction = d;
            }

            row.Text = e.Value;
            row.Commit(dateFormat);
            applied++;
        }

        return (applied, skipped);
    }

    private static BindDataType? ParseType(string cell)
    {
        var t = cell.Trim();
        if (t.Length == 0)
        {
            return null;
        }

        var hit = BindTypeChoice.All.FirstOrDefault(c => string.Equals(c.Label, t, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.Type.ToString(), t, StringComparison.OrdinalIgnoreCase));
        if (hit is not null)
        {
            return hit.Type;
        }

        return t.ToUpperInvariant().Replace('_', ' ') switch
        {
            "VARCHAR2" or "VARCHAR" or "STRING" or "문자열" => BindDataType.Varchar2,
            "NUMBER" or "NUMERIC" or "INTEGER" or "INT" or "숫자" => BindDataType.Number,
            "DATE" or "날짜" => BindDataType.Date,
            "CHAR" or "고정 길이 문자" => BindDataType.FixedChar,
            "TIMESTAMP" => BindDataType.Timestamp,
            "BINARY DOUBLE" => BindDataType.BinaryDouble,
            "TIMESTAMPTZ" => BindDataType.TimestampTz,
            _ => null,
        };
    }

    private static BindDirection? ParseDirection(string cell) => cell.Trim().ToUpperInvariant().Replace('_', ' ') switch
    {
        "IN" => BindDirection.In,
        "OUT" => BindDirection.Out,
        "IN OUT" or "INOUT" => BindDirection.InOut,
        _ => null,
    };

    private static string Quote(string text) =>
        text.Contains('\t') || text.Contains('\n') || text.Contains('\r') || text.Contains('"')
            ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : text;

    // 탭으로 칸, 줄바꿈으로 레코드를 나눈다. 칸이 따옴표로 시작하면 닫는 따옴표까지가 한 칸(안의 "" = ").
    private static List<List<string>> Records(string text)
    {
        var result = new List<List<string>>();
        var cells = new List<string>();
        var sb = new StringBuilder();
        var i = 0;
        var atStart = true;
        while (i < text.Length)
        {
            var c = text[i];
            if (atStart && c == '"')
            {
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            sb.Append('"');
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    sb.Append(text[i++]);
                }

                atStart = false;
                continue;
            }

            if (c == '\t')
            {
                cells.Add(sb.ToString());
                sb.Clear();
                atStart = true;
                i++;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                i++;
                cells.Add(sb.ToString());
                sb.Clear();
                result.Add(cells);
                cells = [];
                atStart = true;
            }
            else
            {
                sb.Append(c);
                atStart = false;
                i++;
            }
        }

        if (sb.Length > 0 || cells.Count > 0)
        {
            cells.Add(sb.ToString());
            result.Add(cells);
        }

        return result;
    }
}
