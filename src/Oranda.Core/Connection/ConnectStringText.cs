using System.Text;
using System.Text.RegularExpressions;

namespace Oranda.Core.Connection;

/// <summary>
/// 사용자가 적은 접속 문자열(여러 줄 tnsnames.ora 형식 포함)을 드라이버에 넘길 한 줄로 정리한 결과.
/// <see cref="Alias"/> 는 <c>ALIAS = (DESCRIPTION=…)</c> 처럼 tnsnames 항목을 통째로 붙여 넣었을 때 뗀 별칭(없으면 null).
/// <see cref="ParenthesesBalanced"/> 가 false 면 괄호 짝이 맞지 않는다(따옴표 안 괄호는 세지 않는다).
/// </summary>
public sealed record ConnectStringParse(string Normalized, string? Alias, bool ParenthesesBalanced);

/// <summary>
/// 접속 문자열 정규화(CONN-01 사용자 지정). 저장은 사람이 적은 모양 그대로 하고, 접속·시험·방식 판별 때만 이 한 줄을 쓴다:
/// <c>#</c> 로 시작하는 주석 줄을 지우고, 따옴표 밖 줄 바꿈·연속 공백을 정리하며(괄호·<c>=</c>·쉼표 둘레 공백은 없앤다),
/// 맨 앞 <c>별칭 =</c> 을 뗀다.
/// </summary>
public static partial class ConnectStringText
{
    // 정리한 뒤 맨 앞의 「별칭=(」 (쉼표로 이은 여러 별칭 허용). Easy Connect(host:port/svc?k=v)는 ':' '/' '?' 때문에 걸리지 않는다.
    [GeneratedRegex(@"^(?<alias>[A-Za-z0-9_.\-]+)(?:,[A-Za-z0-9_.\-]+)*=(?=\()", RegexOptions.CultureInvariant)]
    private static partial Regex AliasPrefix();

    /// <summary>드라이버에 넘길 한 줄(별칭·주석을 뗀 값). null·빈 글자는 빈 글자.</summary>
    public static string Normalize(string? text) => Parse(text).Normalized;

    /// <summary>정리한 한 줄과 뗀 별칭, 괄호 짝 여부.</summary>
    public static ConnectStringParse Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ConnectStringParse(string.Empty, null, true);
        }

        var collapsed = Collapse(StripCommentLines(text));
        string? alias = null;
        var m = AliasPrefix().Match(collapsed);
        if (m.Success)
        {
            alias = m.Groups["alias"].Value;
            collapsed = collapsed[m.Length..];
        }

        return new ConnectStringParse(collapsed, alias, Balanced(collapsed));
    }

    private static string StripCommentLines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return string.Join('\n', lines.Where(l => !l.TrimStart().StartsWith('#')));
    }

    /// <summary>따옴표 밖 공백 묶음을 공백 하나로, 괄호·<c>=</c>·쉼표 옆 공백은 없앤다. 따옴표 안은 그대로 둔다.</summary>
    private static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        var quoted = false;
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (!quoted && char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace && !IsTight(sb[^1]) && !IsTight(ch))
            {
                sb.Append(' ');
            }

            pendingSpace = false;
            if (ch == '"')
            {
                quoted = !quoted;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static bool IsTight(char ch) => ch is '(' or ')' or '=' or ',';

    private static bool Balanced(string text)
    {
        var depth = 0;
        var quoted = false;
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '"':
                    quoted = !quoted;
                    break;
                case '(' when !quoted:
                    depth++;
                    break;
                case ')' when !quoted:
                    if (--depth < 0)
                    {
                        return false;
                    }

                    break;
            }
        }

        return depth == 0;
    }
}
