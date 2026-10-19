using System;
using System.IO;
using System.Text;

namespace Oranda.App.Features.ConnectionManager;

/// <summary>
/// tnsnames.ora 별칭을 접속 기술자 문자열로 풀어 준다.
/// ODP.NET 의 <c>TNS_ADMIN</c>·지갑 위치는 프로세스 전체 설정이라 연결을 한 번 연 뒤에는 바꿀 수 없다(ORA-50099).
/// 접속마다 TNS 디렉터리·지갑이 다른 프로필(CONN-01·03)을 한 프로세스에서 쓰려면 기술자를 직접 만들어 넘기는 쪽이 안전하다.
/// </summary>
public static class TnsResolver
{
    /// <summary><paramref name="tnsAdmin"/>/tnsnames.ora 에서 별칭의 기술자(<c>(DESCRIPTION=…)</c>)를 찾는다.</summary>
    public static bool TryResolve(string tnsAdmin, string alias, out string descriptor)
    {
        descriptor = string.Empty;
        var file = Path.Combine(tnsAdmin, "tnsnames.ora");
        if (string.IsNullOrWhiteSpace(alias) || !File.Exists(file))
        {
            return false;
        }

        try
        {
            return TryFind(File.ReadAllText(file), alias.Trim(), out descriptor);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>tnsnames.ora 본문에서 별칭의 기술자를 찾는다(대소문자 무시, <c>#</c> 주석 제거).</summary>
    public static bool TryFind(string tnsnames, string alias, out string descriptor)
    {
        ArgumentNullException.ThrowIfNull(tnsnames);
        descriptor = string.Empty;
        var text = StripComments(tnsnames);
        var index = 0;
        while (index < text.Length)
        {
            // 줄 맨 앞(공백 허용)에서 시작하는 「이름 =」 만 항목으로 본다.
            var lineStart = index == 0 || text[index - 1] == '\n';
            if (!lineStart)
            {
                index++;
                continue;
            }

            var cursor = index;
            while (cursor < text.Length && (text[cursor] == ' ' || text[cursor] == '\t'))
            {
                cursor++;
            }

            var nameStart = cursor;
            while (cursor < text.Length && !char.IsWhiteSpace(text[cursor]) && text[cursor] is not ('=' or '(' or ')'))
            {
                cursor++;
            }

            var name = text[nameStart..cursor];
            while (cursor < text.Length && (text[cursor] == ' ' || text[cursor] == '\t'))
            {
                cursor++;
            }

            if (name.Length > 0 && cursor < text.Length && text[cursor] == '=')
            {
                cursor++;
                var end = EndOfEntry(text, cursor);
                if (name.Equals(alias, StringComparison.OrdinalIgnoreCase))
                {
                    descriptor = Collapse(text[cursor..end]);
                    return descriptor.StartsWith('(');
                }

                index = end;
                continue;
            }

            var next = text.IndexOf('\n', index);
            index = next < 0 ? text.Length : next + 1;
        }

        return false;
    }

    /// <summary>
    /// 접속 기술자의 <c>SECURITY</c> 절에 <c>MY_WALLET_DIRECTORY</c> 를 넣는다(접속별 지갑 위치).
    /// 이미 있으면 그대로 둔다.
    /// </summary>
    public static string WithWalletDirectory(string descriptor, string walletDirectory)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(walletDirectory);
        if (descriptor.Contains("MY_WALLET_DIRECTORY", StringComparison.OrdinalIgnoreCase))
        {
            return descriptor;
        }

        var entry = $"(MY_WALLET_DIRECTORY={walletDirectory})";
        var security = descriptor.IndexOf("(security=", StringComparison.OrdinalIgnoreCase);
        if (security >= 0)
        {
            return descriptor.Insert(security + "(security=".Length, entry);
        }

        var last = descriptor.LastIndexOf(')');
        return last < 0 ? descriptor : descriptor.Insert(last, $"(SECURITY={entry})");
    }

    private static int EndOfEntry(string text, int start)
    {
        var depth = 0;
        var seen = false;
        for (var i = start; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(':
                    depth++;
                    seen = true;
                    break;
                case ')':
                    depth--;
                    break;
            }

            if (seen && depth <= 0)
            {
                return i + 1;
            }
        }

        return text.Length;
    }

    private static string StripComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var hash = line.IndexOf('#', StringComparison.Ordinal);
            sb.Append(hash >= 0 ? line[..hash] : line).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>따옴표 밖의 공백·줄 바꿈을 지운다(ADB 의 인증서 DN 처럼 따옴표 안 공백은 유지).</summary>
    private static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        var quoted = false;
        foreach (var ch in text)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }

            if (quoted || !char.IsWhiteSpace(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }
}
