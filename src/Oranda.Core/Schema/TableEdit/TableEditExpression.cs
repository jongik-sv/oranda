using System.Text;
using Oranda.Core.Text;

namespace Oranda.Core.Schema.TableEdit;

/// <summary>
/// 식(가상 식·기본값·CHECK 조건·인덱스 식) 한 줄 토큰화. Core 는 렉서(<c>Oranda.Language</c>)를 쓰지 못해(아키텍처 규칙) 비교·이름 찾기에 필요한 만큼만 나눈다:
/// 문자열 리터럴(<c>'…'</c>, <c>''</c> 포함), 따옴표 이름(<c>"…"</c>), 단어(글자·숫자·<c>_$#</c>), 그 밖의 한 글자. 주석은 다루지 않는다.
/// </summary>
internal static class TableEditExpression
{
    internal enum TokenKind
    {
        Word,
        Quoted,
        Literal,
        Symbol,
    }

    internal readonly record struct Token(TokenKind Kind, string Text, int Start = 0, int Length = 0);

    public static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '\'')
            {
                var start = i++;
                while (i < text.Length)
                {
                    if (text[i] == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
                    {
                        i += 2;
                    }
                    else if (text[i++] == '\'')
                    {
                        break;
                    }
                }

                tokens.Add(new Token(TokenKind.Literal, text[start..i], start, i - start));
            }
            else if (c == '"')
            {
                var sb = new StringBuilder();
                var quoteStart = i++;
                while (i < text.Length)
                {
                    if (text[i] == '"' && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        sb.Append('"');
                        i += 2;
                    }
                    else if (text[i] == '"')
                    {
                        i++;
                        break;
                    }
                    else
                    {
                        sb.Append(text[i++]);
                    }
                }

                tokens.Add(new Token(TokenKind.Quoted, sb.ToString(), quoteStart, i - quoteStart));
            }
            else if (IsWordChar(c))
            {
                var start = i;
                while (i < text.Length && IsWordChar(text[i]))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Word, text[start..i], start, i - start));
            }
            else
            {
                tokens.Add(new Token(TokenKind.Symbol, c.ToString(), i, 1));
                i++;
            }
        }

        return tokens;
    }

    /// <summary>
    /// 비교용 표기: 따옴표 안 대문자 일반 이름은 따옴표를 벗기고, 단어는 대문자로, 리터럴은 그대로, 공백은 단어·리터럴 사이에만 하나.
    /// 바깥을 다 감싼 괄호는 벗긴다(서버가 식을 괄호로 감싸 돌려주기도 한다). 빈 식은 null.
    /// </summary>
    public static string? Canonical(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var tokens = Tokenize(text);
        while (tokens.Count >= 2 && tokens[0] is { Kind: TokenKind.Symbol, Text: "(" } && ClosingOf(tokens, 0) == tokens.Count - 1)
        {
            tokens = tokens[1..^1];
        }

        var sb = new StringBuilder();
        var previousWord = false;
        foreach (var t in tokens)
        {
            var (part, word) = t.Kind switch
            {
                TokenKind.Word => (t.Text.ToUpperInvariant(), true),
                TokenKind.Quoted => (SqlIdentifier.IsPlain(t.Text) ? t.Text : SqlIdentifier.QuoteAlways(t.Text), true),
                TokenKind.Literal => (t.Text, true),
                _ => (t.Text, false),
            };
            if (word && previousWord)
            {
                sb.Append(' ');
            }

            sb.Append(part);
            previousWord = word;
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>식에 나온 이름(단어는 대문자, 따옴표 이름은 그대로, 나온 순서, 중복 없음). 함수 이름·키워드도 섞여 나오므로 부르는 쪽이 열 이름과 맞춘다.</summary>
    public static IReadOnlyList<string> Names(string? text)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return names;
        }

        foreach (var t in Tokenize(text))
        {
            var name = t.Kind switch
            {
                TokenKind.Word when !char.IsDigit(t.Text[0]) => t.Text.ToUpperInvariant(),
                TokenKind.Quoted => t.Text,
                _ => null,
            };
            if (name is not null && !names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// 식 안의 열 이름을 바꿔 쓴다(<paramref name="renames"/>: 옛 이름 → 새 이름, 사전 표기). 점 뒤 이름(<c>o.f</c> 의 <c>f</c>)·리터럴은 건드리지 않는다.
    /// 따옴표로 쓴 이름은 늘 따옴표로, 아닌 이름은 필요할 때만 따옴표로. 나머지 글자는 그대로.
    /// </summary>
    public static string RenameNames(string text, IReadOnlyDictionary<string, string> renames, Func<string, bool>? isReserved = null)
    {
        if (renames.Count == 0 || string.IsNullOrEmpty(text))
        {
            return text;
        }

        var tokens = Tokenize(text);
        var sb = new StringBuilder();
        var at = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var name = t.Kind switch
            {
                TokenKind.Word when !char.IsDigit(t.Text[0]) => t.Text.ToUpperInvariant(),
                TokenKind.Quoted => t.Text,
                _ => null,
            };
            var qualified = i > 0 && tokens[i - 1] is { Kind: TokenKind.Symbol, Text: "." };
            if (name is null || qualified || !renames.TryGetValue(name, out var renamed))
            {
                continue;
            }

            sb.Append(text, at, t.Start - at)
              .Append(t.Kind == TokenKind.Quoted ? SqlIdentifier.QuoteAlways(renamed) : SqlIdentifier.Quote(renamed, isReserved));
            at = t.Start + t.Length;
        }

        return sb.Append(text, at, text.Length - at).ToString();
    }

    private static int ClosingOf(List<Token> tokens, int open)
    {
        var depth = 0;
        for (var i = open; i < tokens.Count; i++)
        {
            if (tokens[i] is { Kind: TokenKind.Symbol, Text: "(" })
            {
                depth++;
            }
            else if (tokens[i] is { Kind: TokenKind.Symbol, Text: ")" } && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';
}
