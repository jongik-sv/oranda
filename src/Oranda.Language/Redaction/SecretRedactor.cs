using System.Text;
using Oranda.Core.Composition;
using Oranda.Core.Security;
using Oranda.Language.Lexing;

namespace Oranda.Language.Redaction;

/// <summary>
/// 이력·로그 비밀 가림(data-model 5.3). L1 렉서 토큰 기준이라 문자열·주석 안은 건드리지 않는다.
/// 가리는 곳: <c>IDENTIFIED BY [VALUES] x</c>, <c>… REPLACE x</c>(IDENTIFIED BY 뒤), 줄 처음 <c>CONNECT u/p[@db]</c>, 문장 처음 <c>PASSWORD x</c>.
/// 소유: TSK-06-06.
/// </summary>
[OrandaService(typeof(ISecretRedactor))]
internal sealed class SecretRedactor : ISecretRedactor
{
    private const string Mask = "***";

    private static readonly string[] SensitiveNameParts = ["pass", "pwd", "secret", "token"];

    public string Redact(string sqlText)
    {
        if (string.IsNullOrEmpty(sqlText))
        {
            return sqlText ?? string.Empty;
        }

        var all = SqlLexer.Tokenize(sqlText);
        var tokens = all.Where(t => t.Kind is not (TokenKind.Comment or TokenKind.Hint)).ToList();
        var edits = new List<(int Start, int End, string Text)>();
        var statementStart = true;
        var seenIdentifiedBy = false;

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (IsPunct(sqlText, t, ';'))
            {
                statementStart = true;
                seenIdentifiedBy = false;
                continue;
            }

            if (!IsWord(t))
            {
                statementStart = false;
                continue;
            }

            if (IsWord(sqlText, t, "IDENTIFIED") && i + 1 < tokens.Count && IsWord(sqlText, tokens[i + 1], "BY"))
            {
                seenIdentifiedBy = true;
                var next = i + 2;
                if (next < tokens.Count && IsWord(sqlText, tokens[next], "VALUES"))
                {
                    next++;
                }

                if (next < tokens.Count)
                {
                    var span = SecretSpan(sqlText, tokens, next);
                    edits.Add((span.Start, span.End, Mask));
                    i = span.LastIndex;
                }

                statementStart = false;
                continue;
            }

            if (seenIdentifiedBy && IsWord(sqlText, t, "REPLACE") && i + 1 < tokens.Count)
            {
                var span = SecretSpan(sqlText, tokens, i + 1);
                edits.Add((span.Start, span.End, Mask));
                i = span.LastIndex;
                continue;
            }

            if (IsLineStart(sqlText, t.Start) && (IsWord(sqlText, t, "CONNECT") || IsWord(sqlText, t, "CONN"))
                && !(i + 1 < tokens.Count && (IsWord(sqlText, tokens[i + 1], "BY") || IsWord(sqlText, tokens[i + 1], "TO"))))
            {
                if (TryRedactConnect(sqlText, t.End, out var edit))
                {
                    edits.Add(edit);
                }

                statementStart = false;
                continue;
            }

            if (statementStart && IsWord(sqlText, t, "PASSWORD") && i + 1 < tokens.Count
                && LineOf(sqlText, tokens[i + 1].Start) == LineOf(sqlText, t.Start) && IsWord(tokens[i + 1]))
            {
                var span = SecretSpan(sqlText, tokens, i + 1);
                edits.Add((span.Start, span.End, Mask));
                i = span.LastIndex;
            }

            statementStart = false;
        }

        return Apply(sqlText, edits);
    }

    public bool IsSensitiveBindName(string bindName)
    {
        if (string.IsNullOrEmpty(bindName))
        {
            return false;
        }

        foreach (var part in SensitiveNameParts)
        {
            if (bindName.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWord(Token t) => t.Kind is TokenKind.Identifier or TokenKind.Keyword;

    private static bool IsWord(string text, Token t, string word)
        => IsWord(t) && t.Length == word.Length && string.Compare(text, t.Start, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0;

    private static bool IsPunct(string text, Token t, char c) => t.Kind == TokenKind.Punct && t.Length == 1 && text[t.Start] == c;

    private static bool IsLineStart(string text, int pos)
    {
        for (var i = pos - 1; i >= 0 && text[i] != '\n'; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static int LineOf(string text, int pos)
    {
        var line = 0;
        for (var i = 0; i < pos && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    /// <summary>비밀 토큰 하나(따옴표 없는 값은 붙어 있는 토큰까지)의 범위.</summary>
    private static (int Start, int End, int LastIndex) SecretSpan(string text, List<Token> tokens, int index)
    {
        var first = tokens[index];
        var end = first.End;
        var last = index;
        if (first.Kind is not (TokenKind.StringLiteral or TokenKind.QuotedIdent))
        {
            while (last + 1 < tokens.Count && tokens[last + 1].Start == end
                   && !(tokens[last + 1].Kind == TokenKind.Punct && ";,)".Contains(text[tokens[last + 1].Start], StringComparison.Ordinal)))
            {
                last++;
                end = tokens[last].End;
            }
        }

        return (first.Start, end, last);
    }

    /// <summary>줄 끝까지의 <c>user/pass[@db]</c> 인자에서 비밀번호를 가린다. 비밀번호가 없으면 false.</summary>
    private static bool TryRedactConnect(string text, int afterVerb, out (int Start, int End, string Text) edit)
    {
        edit = default;
        var i = afterVerb;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
        {
            i++;
        }

        var argStart = i;
        var inQuote = false;
        var slash = -1;
        for (; i < text.Length && text[i] != '\n' && text[i] != '\r'; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if (!inQuote && (char.IsWhiteSpace(c) || c == ';'))
            {
                break;
            }
            else if (!inQuote && c == '@')
            {
                break;
            }
            else if (!inQuote && c == '/')
            {
                slash = i;
                break;
            }
        }

        if (slash < 0 || slash == argStart)
        {
            return false;
        }

        var p = slash + 1;
        var passStart = p;
        if (p < text.Length && text[p] == '"')
        {
            p++;
            while (p < text.Length && text[p] != '"' && text[p] != '\n')
            {
                p++;
            }

            if (p < text.Length && text[p] == '"')
            {
                p++;
            }
        }
        else
        {
            while (p < text.Length && text[p] != '@' && !char.IsWhiteSpace(text[p]) && text[p] != ';')
            {
                p++;
            }
        }

        if (p == passStart)
        {
            return false;
        }

        edit = (passStart, p, Mask);
        return true;
    }

    private static string Apply(string text, List<(int Start, int End, string Text)> edits)
    {
        if (edits.Count == 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var pos = 0;
        foreach (var e in edits.OrderBy(x => x.Start))
        {
            if (e.Start < pos)
            {
                continue;
            }

            sb.Append(text, pos, e.Start - pos).Append(e.Text);
            pos = e.End;
        }

        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }
}
