using System.Text.RegularExpressions;

namespace Oranda.Core.Catalog;

/// <summary>카탈로그 SQL 의 <c>@KEY@</c> 자리(바인드로 보낼 수 없는 식별자·리터럴)를 값으로 채운다.</summary>
public static partial class CatalogText
{
    [GeneratedRegex("@([A-Z_]+)@", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    /// <summary>
    /// <paramref name="sql"/> 의 <c>@KEY@</c> 를 <paramref name="args"/> 의 값으로 바꾼다. 키는 소문자로 넘겨도 되고(파일 쪽은 대문자),
    /// 모르는 자리는 그대로 둔다. 한 번에 바꾸므로 값 안의 <c>@KEY@</c> 모양 글은 다시 바뀌지 않는다. 값은 호출자가 이스케이프해서 넘긴다.
    /// </summary>
    public static string Fill(string sql, params (string Key, string Value)[] args)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (args is null || args.Length == 0)
        {
            return sql;
        }

        var map = args.ToDictionary(a => a.Key.ToUpperInvariant(), a => a.Value, StringComparer.Ordinal);
        return Placeholder().Replace(sql, m => map.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }
}
