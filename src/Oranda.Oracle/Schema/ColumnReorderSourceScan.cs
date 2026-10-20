using System.Text.RegularExpressions;
using Oranda.Core.Metadata;

namespace Oranda.Oracle.Schema;

/// <summary>열 순서를 바꾸면 결과가 달라질 수 있는 소스 줄의 종류.</summary>
public enum ColumnReorderRisk
{
    /// <summary><c>SELECT *</c> (또는 <c>t.*</c>): 열 순서가 곧 결과 열 순서다.</summary>
    SelectStar,

    /// <summary>열 목록 없는 <c>INSERT INTO 표 VALUES (…)</c> 또는 <c>INSERT INTO 표 SELECT …</c>: 값이 열 위치로 들어간다.</summary>
    InsertWithoutColumns,
}

/// <summary>위험 후보 한 줄.</summary>
public sealed record ColumnReorderRiskHit(DbObjectRef Ref, int Line, ColumnReorderRisk Risk, string Text);

/// <summary>
/// 표를 참조하는 소스 줄에서 열 순서에 민감한 문장 후보를 가려낸다(한 줄 단위 판정이라 줄을 넘는 문장은 못 찾는다). 소유: TSK-13-01.
/// </summary>
public static partial class ColumnReorderSourceScan
{
    [GeneratedRegex(@"\bSELECT\s+(?:(?:DISTINCT|UNIQUE|ALL)\s+)?(?:(?:""[^""]+""|[A-Za-z_][\w$#]*)\s*\.\s*)?\*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SelectStarRegex();

    [GeneratedRegex(@"\bFROM\s+((?:""[^""]+""|[A-Za-z_][\w$#]*)(?:\s*\.\s*(?:""[^""]+""|[A-Za-z_][\w$#]*))?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FromRegex();

    [GeneratedRegex(@"\bINSERT\s+(?:/\*.*?\*/\s*)?INTO\s+((?:""[^""]+""|[A-Za-z_][\w$#]*)(?:\s*\.\s*(?:""[^""]+""|[A-Za-z_][\w$#]*))?)\s+(?:VALUES|SELECT)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InsertRegex();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.CultureInvariant)]
    private static partial Regex BlockCommentRegex();

    /// <summary>한 줄을 판정한다. 위험 후보가 아니면 null.</summary>
    public static ColumnReorderRisk? Classify(string line, DbObjectRef table)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(table);

        var code = StripComments(line);
        if (code.Length == 0)
        {
            return null;
        }

        if (InsertRegex().Match(code) is { Success: true } insert)
        {
            return RefersTo(insert.Groups[1].Value, table) ? ColumnReorderRisk.InsertWithoutColumns : null;
        }

        if (SelectStarRegex().IsMatch(code))
        {
            // 같은 줄에 FROM 이 있으면 그 표가 이 표일 때만 후보로 본다(다른 표의 SELECT * 를 덜어 낸다). 없으면 알 수 없어 후보로 둔다.
            return FromRegex().Match(code) is { Success: true } from && !RefersTo(from.Groups[1].Value, table)
                ? null
                : ColumnReorderRisk.SelectStar;
        }

        return null;
    }

    /// <summary>소스 줄 가운데 위험 후보만 남긴다.</summary>
    public static IReadOnlyList<ColumnReorderRiskHit> Filter(IEnumerable<SourceSearchHit> lines, DbObjectRef table)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(table);
        var result = new List<ColumnReorderRiskHit>();
        foreach (var hit in lines)
        {
            if (Classify(hit.Text, table) is { } risk)
            {
                result.Add(new ColumnReorderRiskHit(hit.Ref, hit.Line, risk, hit.Text.Trim()));
            }
        }

        return result;
    }

    private static string StripComments(string line)
    {
        var text = BlockCommentRegex().Replace(line, " ");
        var dash = text.IndexOf("--", StringComparison.Ordinal);
        return (dash >= 0 ? text[..dash] : text).Trim();
    }

    /// <summary><c>[소유자.]이름</c> 이 대상 표인가. 소유자를 적었으면 소유자까지 맞아야 한다(소유자 없이 쓴 이름은 같은 스키마 소스로 본다).</summary>
    private static bool RefersTo(string reference, DbObjectRef table)
    {
        var parts = reference.Split('.', StringSplitOptions.TrimEntries);
        var name = Unquote(parts[^1]);
        if (!string.Equals(name, table.Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return parts.Length < 2 || string.Equals(Unquote(parts[^2]), table.Owner, StringComparison.OrdinalIgnoreCase);
    }

    private static string Unquote(string identifier) => identifier.Trim().Trim('"');
}
