using System.Security.Cryptography;
using System.Text;

namespace Oranda.Core.ChangeBundle;

/// <summary>
/// 묶음 문장 중복 판정 해시(설계 9.1 「공백 정규화」). 앞뒤 공백·끝의 <c>;</c> 를 떼고, 문장 글의 공백·줄바꿈 덩어리는 공백 하나로 본다.
/// 문자열 리터럴(<c>'…'</c>)·따옴표 이름(<c>"…"</c>)·주석 안은 그대로 둔다(<c>COMMENT … IS 'a  b'</c> 와 <c>'a b'</c> 는 다른 문장).
/// 대소문자는 그대로 본다. Core 는 렉서를 쓰지 않으므로 이 간단한 구분만 한다(<c>q'[…]'</c> 안은 보통 글로 본다).
/// </summary>
public static class StatementHash
{
    public static string Compute(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(text))));
    }

    /// <summary>해시 앞 정규화 글(시험·진단용).</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var s = text.Trim().TrimEnd(';').TrimEnd();
        var sb = new StringBuilder(s.Length);
        var space = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && sb.Length > 0)
            {
                sb.Append(' ');
            }

            space = false;
            var end = c switch
            {
                '\'' => Closing(s, i, "'"),
                '"' => Closing(s, i, "\""),
                '-' when i + 1 < s.Length && s[i + 1] == '-' => LineEnd(s, i),
                '/' when i + 1 < s.Length && s[i + 1] == '*' => Closing(s, i + 1, "*/"),
                _ => i,
            };
            sb.Append(s, i, end - i + 1);
            i = end;
        }

        return sb.ToString();
    }

    /// <summary><paramref name="open"/> 다음부터 <paramref name="close"/> 가 끝나는 위치(없으면 글 끝). 따옴표 두 개(<c>''</c>)는 닫힘이 아니다.</summary>
    private static int Closing(string s, int open, string close)
    {
        var i = open + 1;
        while (i < s.Length)
        {
            var at = s.IndexOf(close, i, StringComparison.Ordinal);
            if (at < 0)
            {
                return s.Length - 1;
            }

            var last = at + close.Length - 1;
            if (close.Length == 1 && last + 1 < s.Length && s[last + 1] == close[0])
            {
                i = last + 2; // '' / "" 는 글 안의 따옴표
                continue;
            }

            return last;
        }

        return s.Length - 1;
    }

    /// <summary>줄 주석의 끝(줄바꿈 앞 글자). 줄바꿈이 없으면 글 끝.</summary>
    private static int LineEnd(string s, int start)
    {
        var nl = s.IndexOf('\n', start);
        if (nl < 0)
        {
            return s.Length - 1;
        }

        var end = nl - 1;
        return end >= start && s[end] == '\r' ? end - 1 : end;
    }
}
