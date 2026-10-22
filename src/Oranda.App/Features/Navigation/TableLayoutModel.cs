using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Metadata;
using Oranda.Core.Settings;

namespace Oranda.App.Features.Navigation;

/// <summary>테이블 레이아웃 팝업의 열 한 행(Orange 의 Name·Type·Nullable·Default·Comment).</summary>
public sealed record TableLayoutRow(int Index, string Name, string TypeText, bool NotNull, string? DefaultValue, string? Comment, bool IsPk);

/// <summary>팝업이 보여 줄 표 하나. 제목은 「소유자.표」.</summary>
public sealed record TableLayoutModel(string Title, IReadOnlyList<TableLayoutRow> Rows)
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
        return new TableLayoutModel($"{obj.Owner}.{obj.Name}", rows);
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

/// <summary>팝업의 입력 4개 + 별칭. 확인란 4개는 <see cref="TableLayoutSettings"/> 로 저장된다.</summary>
public sealed record TableLayoutOptions(string Alias, bool InsertComma, bool LowerCase, bool ShowPk, bool ShowComment)
{
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
    /// 이름만 쓰는 모드: 열을 「, 」로 잇고 쉼표 넣기가 켜져 있으면 끝에도 「, 」를 붙인다.
    /// 주석 넣기가 켜져 있고 주석이 하나라도 있으면 줄마다 한 열이고 「 -- 주석」이 열 뒤에 붙는다(줄 주석이 뒤 글을 먹지 않게 열 사이 쉼표는 늘 넣는다).
    /// </summary>
    public static string Build(IReadOnlyList<TableLayoutRow> rows, TableLayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(options);
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var alias = options.Alias.Trim();
        if (alias.Length > 0 && options.LowerCase)
        {
            alias = alias.ToLowerInvariant();
        }

        var names = rows.Select(r => Qualify(alias, ColumnName(r.Name, options.LowerCase))).ToList();
        var withComments = options.ShowComment &&rows.Any(r => r.Comment is not null);
        if (!withComments)
        {
            var joined = string.Join(", ", names);
            return options.InsertComma ? joined + ", " : joined;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < rows.Count; i++)
        {
            var last = i == rows.Count - 1;
            sb.Append(names[i]);
            if (!last || options.InsertComma)
            {
                sb.Append(',');
            }

            if (rows[i].Comment is { } c)
            {
                sb.Append(" -- ").Append(c.ReplaceLineEndings(" "));
            }

            if (!last)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
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
