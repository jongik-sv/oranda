using System.Text;

namespace Oranda.Oracle.Execution;

/// <summary>
/// 치환 변수(<c>&amp;name</c>, <c>&amp;&amp;name</c>) 텍스트 치환(EXEC-03, SQL*Plus 방식).
/// 이름 바로 뒤의 <c>.</c> 는 이름 끝 표시로 보고 지운다. 값이 없는 이름은 그대로 둔다. 문자열 안도 치환한다(SQL*Plus 와 같음).
/// </summary>
internal static class SubstitutionText
{
    /// <param name="text">원문</param>
    /// <param name="lookup">이름 → 값(없으면 null)</param>
    /// <param name="onDoubleAmpersand"><c>&amp;&amp;</c> 로 쓴 이름과 값(세션 기억용)</param>
    public static string Apply(string text, Func<string, string?> lookup, Action<string, string>? onDoubleAmpersand = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(lookup);
        if (!text.Contains('&', StringComparison.Ordinal))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c != '&')
            {
                sb.Append(c);
                i++;
                continue;
            }

            var start = i;
            var amps = 1;
            if (i + 1 < text.Length && text[i + 1] == '&')
            {
                amps = 2;
            }

            var nameStart = i + amps;
            var j = nameStart;
            while (j < text.Length && IsNameChar(text[j]))
            {
                j++;
            }

            if (j == nameStart)
            {
                sb.Append(text, start, amps);
                i = nameStart;
                continue;
            }

            var name = text[nameStart..j];
            var value = lookup(name);
            if (value is null)
            {
                sb.Append(text, start, j - start);
                i = j;
                continue;
            }

            if (amps == 2)
            {
                onDoubleAmpersand?.Invoke(name, value);
            }

            sb.Append(value);
            i = j < text.Length && text[j] == '.' ? j + 1 : j;
        }

        return sb.ToString();
    }

    // 숫자로 시작하는 이름(&1 같은 위치 인자)도 받는다.
    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';
}
