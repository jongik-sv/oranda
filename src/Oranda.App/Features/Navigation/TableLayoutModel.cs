using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Formatting;
using Oranda.Core.Metadata;
using Oranda.Core.Settings;

namespace Oranda.App.Features.Navigation;

/// <summary>테이블 레이아웃 팝업의 열 한 행(Orange 의 Name·Type·Nullable·Default·Comment).</summary>
public sealed record TableLayoutRow(int Index, string Name, string TypeText, bool NotNull, string? DefaultValue, string? Comment, bool IsPk);

/// <summary>팝업이 보여 줄 표 하나. 제목은 「소유자.표」, 종류는 객체 형식(<c>TABLE</c>·<c>VIEW</c>), 주석은 표 주석.</summary>
public sealed record TableLayoutModel(string Title, IReadOnlyList<TableLayoutRow> Rows, string Kind = "TABLE", string? Comment = null)
{
    /// <summary>메타데이터의 표·뷰 정보에서 만든다. 열은 위치 순서이고 행 번호는 1부터.</summary>
    public static TableLayoutModel From(DbObjectRef obj, TableInfo table)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(table);
        var pk = table.Constraints
            .Where(c => c.Kind == ConstraintKind.PrimaryKey)
            .SelectMany(c => c.Columns)
            .ToHashSet(StringComparer.Ordinal);
        var rows = table.Columns
            .OrderBy(c => c.Position)
            .Select((c, i) => new TableLayoutRow(i + 1, c.Name, TypeText(c), !c.IsNullable, Clean(c.DefaultValue), Clean(c.Comment), pk.Contains(c.Name)))
            .ToList();
        return new TableLayoutModel($"{obj.Owner}.{obj.Name}", rows, obj.ObjectType, Clean(table.Comment));
    }

    /// <summary>길이·정밀도를 포함한 형식 표기(<c>VARCHAR2(7)</c>, <c>NUMBER(5,3)</c>).</summary>
    public static string TypeText(ColumnInfo c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (c.Precision is { } p)
        {
            return c.Scale is { } s and > 0
                ? $"{c.DataType}({p.ToString(CultureInfo.InvariantCulture)},{s.ToString(CultureInfo.InvariantCulture)})"
                : $"{c.DataType}({p.ToString(CultureInfo.InvariantCulture)})";
        }

        return c.Length is { } l and > 0 && c.DataType.Contains("CHAR", StringComparison.OrdinalIgnoreCase)
            ? $"{c.DataType}({l.ToString(CultureInfo.InvariantCulture)})"
            : c.DataType;
    }

    // 기본값은 서버가 줄바꿈·뒤 공백을 붙여 돌려주므로 한 줄로 다듬는다.
    private static string? Clean(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.ReplaceLineEndings(" ").Trim();
}

/// <summary>
/// 팝업의 입력 4개 + 별칭. 확인란 4개는 <see cref="TableLayoutSettings"/> 로 저장된다.
/// <see cref="Comma"/> 는 창의 입력이 아니라 설정 창 「SQL 포맷」의 목록 쉼표(지금 포매터 프로필의 <c>commaPosition</c>)다(TSK-13-38).
/// </summary>
public sealed record TableLayoutOptions(string Alias, bool InsertComma, bool LowerCase, bool ShowPk, bool ShowComment)
{
    /// <summary>쉼표 위치. Leading 이면 열 사이가 「, COL」(주석 뒤 새 줄도 「, COL」로 시작), 쉼표 넣기면 맨 앞에도 「, 」.</summary>
    public CommaPosition Comma { get; init; } = CommaPosition.Trailing;

    public static TableLayoutOptions From(TableLayoutSettings s, string alias = "")
    {
        ArgumentNullException.ThrowIfNull(s);
        return new TableLayoutOptions(alias, s.InsertComma, s.LowerCase, s.ShowPk, s.ShowComment);
    }

    public TableLayoutSettings ToSettings() => new() { InsertComma = InsertComma, LowerCase = LowerCase, ShowPk = ShowPk, ShowComment = ShowComment };
}

/// <summary>선택한 열을 편집기에 넣을 글로 바꾼다(순수 함수).</summary>
public static partial class TableLayoutPaste
{
    /// <summary>
    /// 고른 행을 표 순서(열 순번)로 놓는다. 고른 순서와 상관없이 「COL1, COL2, …」가 된다.
    /// 주석 넣기가 꺼져 있으면 한 줄에 「, 」로 잇고 쉼표 넣기가 켜져 있으면 끝에도(앞 쉼표면 맨 앞에) 「, 」를 붙인다.
    /// 주석 넣기가 켜져 있으면 열마다 한 줄이고 주석이 있는 열은 「 -- 주석」이 뒤에 붙는다(줄 주석이 뒤 글을 먹지 않게 열 사이 쉼표는 늘 넣는다).
    /// 앞 쉼표(<see cref="TableLayoutOptions.Comma"/> = Leading)면 주석 뒤 새 줄이 「, COL」로 시작하고 쉼표 넣기면 첫 열도 「, COL」이다.
    /// 주석 넣기여도 줄은 주석이 붙은 열 뒤에서만 바꾸고(주석 없는 열은 「, 」로 같은 줄에 잇는다), 끝 열에 주석이 있으면 끝에서도 줄을 바꾼다.
    /// 둘째 줄부터는 <paramref name="indent"/>(편집기 커서 앞 글을 공백으로 바꾼 것)만큼 들여 첫 열과 세로로 맞춘다.
    /// </summary>
    public static string Build(IReadOnlyList<TableLayoutRow> rows, TableLayoutOptions options, string indent = "", string newLine = "\n")
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(indent);
        ArgumentException.ThrowIfNullOrEmpty(newLine);
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var alias = options.Alias.Trim();
        if (alias.Length > 0 && options.LowerCase)
        {
            alias = alias.ToLowerInvariant();
        }

        var ordered = rows.OrderBy(r => r.Index).ToList();
        var names = ordered.Select(r => Qualify(alias, ColumnName(r.Name, options.LowerCase))).ToList();
        var leading = options.Comma == CommaPosition.Leading;
        if (!options.ShowComment)
        {
            var joined = string.Join(", ", names);
            return !options.InsertComma ? joined : leading ? ", " + joined : joined + ", ";
        }

        // 줄은 주석(「 -- 주석」)을 붙인 열 뒤에서만 바꾼다. 주석이 없는 열은 같은 줄에 이어 붙인다.
        var sb = new StringBuilder();
        var afterComment = false;
        for (var i = 0; i < ordered.Count; i++)
        {
            var last = i == ordered.Count - 1;
            if (i > 0)
            {
                if (afterComment)
                {
                    sb.Append(newLine).Append(indent);
                }
                else if (!leading)
                {
                    sb.Append(' ');
                }
            }

            if (leading && (i > 0 || options.InsertComma))
            {
                sb.Append(", ");
            }

            sb.Append(names[i]);
            if (!leading && (!last || options.InsertComma))
            {
                sb.Append(',');
            }

            afterComment = ordered[i].Comment is not null;
            if (afterComment)
            {
                sb.Append(" -- ").Append(ordered[i].Comment!.ReplaceLineEndings(" "));
            }
        }

        // 끝 열에 줄 주석이 붙었으면 줄을 바꿔 뒤에 오는 글(다음에 넣는 열·FROM)이 주석에 묻히지 않게 한다.
        if (afterComment)
        {
            sb.Append(newLine).Append(indent);
        }
        else if (!leading && options.InsertComma)
        {
            sb.Append(' ');
        }

        return sb.ToString();
    }

    /// <summary>편집기 줄에서 커서 앞 글을 들여쓰기로 바꾼다(탭은 탭 그대로, 나머지는 공백).</summary>
    public static string IndentFor(string text, int caret)
    {
        ArgumentNullException.ThrowIfNull(text);
        caret = Math.Clamp(caret, 0, text.Length);
        var start = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        var sb = new StringBuilder(caret - start);
        for (var i = start; i < caret; i++)
        {
            sb.Append(text[i] == '\t' ? '\t' : ' ');
        }

        return sb.ToString();
    }

    /// <summary>
    /// 두 번 누른 행이 고른 행 안에 있으면 고른 행 전체를, 밖이면 누른 행 하나만 넣는다.
    /// </summary>
    public static IReadOnlyList<TableLayoutRow> DoubleClickRows(TableLayoutRow clicked, IReadOnlyCollection<TableLayoutRow> selected)
    {
        ArgumentNullException.ThrowIfNull(clicked);
        ArgumentNullException.ThrowIfNull(selected);
        return selected.Contains(clicked) ? selected.ToList() : [clicked];
    }

    /// <summary>거르기 칸: 이름·주석에 글이 들어 있는 행만(대소문자 무시). 빈 글이면 전체.</summary>
    public static IReadOnlyList<TableLayoutRow> Filter(IReadOnlyList<TableLayoutRow> rows, string? text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var q = text?.Trim();
        return string.IsNullOrEmpty(q)
            ? rows
            : rows.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (r.Comment?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
    }

    /// <summary>
    /// 이름이 따옴표 없이 쓸 수 있는 식별자(대문자·숫자·_$#)면 그대로(소문자 옵션이면 소문자로), 아니면 따옴표째 넣는다.
    /// 따옴표 이름의 대소문자는 뜻이 달라지므로 소문자 옵션을 적용하지 않는다.
    /// </summary>
    public static string ColumnName(string name, bool lower)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (Plain().IsMatch(name))
        {
            return lower ? name.ToLowerInvariant() : name;
        }

        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string Qualify(string alias, string name) => alias.Length == 0 ? name : alias + "." + name;

    [GeneratedRegex("^[A-Z][A-Z0-9_$#]*$")]
    private static partial Regex Plain();
}
