using System.Globalization;
using System.Text;
using System.Text.Json;
using Oranda.Core.Execution;

namespace Oranda.App.Features.MyBatisWorkspace.Parameters;

/// <summary>
/// 「바인드 변수」 창 값 칸의 글을 MyBatis 계산기 값으로 읽는다(MB-04, 2026-10-23 사용자 결정).
/// <list type="bullet">
/// <item>빈 칸 = null, 따옴표 두 개 <c>''</c> = 빈 글자(<c>x != null and x != ''</c> 를 가를 수 있게).</item>
/// <item>목록(foreach 에 쓰인 행, 또는 값이 <c>[</c> 로 시작): <c>['A', 'B', 'C']</c> 또는 대괄호 없이 <c>'A', 'B'</c>.
/// 따옴표 안 쉼표는 나누지 않는다. 따옴표 = 글자, 따옴표 없는 수 = 숫자, <c>null</c> = null 항목, <c>true</c>·<c>false</c> = 참거짓,
/// <c>{"name":"a"}</c> = 객체(<c>#{item.name}</c> 용), <c>[]</c> = 빈 목록. 빈 칸 = null(값을 모름 → 계산기가 foreach 를 바인드 자리 하나로 펼친다, 2026-10-24).</item>
/// <item>그 밖은 행 형식대로: 숫자 형식이면 수(못 읽으면 글자 그대로), 참거짓 형식이면 참거짓, 나머지는 칸 글 그대로.</item>
/// </list>
/// 실행할 바인드 값은 이것이 아니라 행 형식 규칙(<c>BindInput</c>)으로 만든다(<c>Run/MyBatisRunBinds</c>).
/// </summary>
public static class MyBatisBindText
{
    /// <summary>빈 글자를 뜻하는 칸 글.</summary>
    public const string EmptyString = "''";

    /// <summary>칸 글 하나를 계산기 값으로 읽는다. <paramref name="asList"/> = foreach collection 으로 쓰인 행.</summary>
    public static object? Parse(string? text, BindDataType type, bool asList)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (asList && trimmed.Length == 0)
        {
            return null;
        }

        if (asList || trimmed.StartsWith('['))
        {
            return ParseList(trimmed);
        }

        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed == EmptyString)
        {
            return string.Empty;
        }

        return type switch
        {
            BindDataType.Number when decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            BindDataType.BinaryDouble when double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) => f,
            BindDataType.Boolean when bool.TryParse(trimmed, out var b) => b,
            _ => text,
        };
    }

    /// <summary>목록 글을 항목 목록으로 읽는다. 빈 글·<c>[]</c> 는 빈 목록.</summary>
    public static List<object?> ParseList(string text)
    {
        var body = text.Trim();
        if (body.StartsWith('[') && body.EndsWith(']'))
        {
            body = body[1..^1];
        }

        var items = new List<object?>();
        foreach (var part in SplitItems(body))
        {
            var item = part.Trim();
            if (item.Length > 0)
            {
                items.Add(ParseItem(item));
            }
        }

        return items;
    }

    // 최상위 쉼표로 나눈다(따옴표·중괄호·대괄호 안은 나누지 않음). 짝이 안 맞으면 끝까지 한 항목.
    private static List<string> SplitItems(string body)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        int depth = 0;
        for (int i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (quote != '\0')
            {
                current.Append(c);
                if (c == quote)
                {
                    if (quote == '\'' && i + 1 < body.Length && body[i + 1] == '\'')
                    {
                        current.Append(body[++i]); // '' = 따옴표 글자
                    }
                    else
                    {
                        quote = '\0';
                    }
                }
                else if (c == '\\' && quote == '"' && i + 1 < body.Length)
                {
                    current.Append(body[++i]);
                }

                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    break;
                case '{' or '[':
                    depth++;
                    break;
                case '}' or ']':
                    depth = Math.Max(0, depth - 1);
                    break;
                case ',' when depth == 0:
                    parts.Add(current.ToString());
                    current.Clear();
                    continue;
            }

            current.Append(c);
        }

        parts.Add(current.ToString());
        return parts;
    }

    private static object? ParseItem(string item)
    {
        if (item.Length >= 2 && item[0] == '\'' && item[^1] == '\'')
        {
            return item[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        if (item[0] is '"' or '{' or '[')
        {
            try
            {
                using var json = JsonDocument.Parse(item);
                return FromJson(json.RootElement);
            }
            catch (JsonException)
            {
                return item; // 읽지 못한 글은 그대로 글자
            }
        }

        if (string.Equals(item, "null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (bool.TryParse(item, out var b))
        {
            return b;
        }

        return decimal.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : item;
    }

    private static object? FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => FromJson(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => e.EnumerateArray().Select(FromJson).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetDecimal(out var d) ? d : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
