using System.Collections.Frozen;
using Oranda.Core.ChangeBundle;
using Oranda.Core.Composition;

namespace Oranda.Language.Lexing;

/// <summary>
/// <see cref="IOwnerRewriter"/> 구현(테이블 편집기 설계 9.3·9.4). 글 고치기는 <see cref="OwnerRewriter"/> 에 맡기고, 묶음이 더 알아야 하는 두 가지만 여기서 읽는다:
/// 문장이 바꾸는 객체(소유자·이름, TE-24 의 대상 소유자 판정)와 <c>ALTER SESSION SET CURRENT_SCHEMA</c>(소유자를 안 쓴 문장의 소유자).
/// 읽기는 렉서(<see cref="SqlLexer"/>) 토큰 기준이라 문자열 리터럴·주석 안의 글자에 속지 않는다.
/// </summary>
[OrandaService(typeof(IOwnerRewriter))]
internal sealed class ChangeBundleOwnerRewriter : IOwnerRewriter
{
    /// <summary>대상으로 읽는 객체 종류의 첫 단어(PACKAGE·TYPE 은 BODY, MATERIALIZED 는 VIEW 가 뒤따를 수 있다).</summary>
    private static readonly FrozenSet<string> TypeWords = new[]
    {
        "TABLE", "INDEX", "VIEW", "SEQUENCE", "SYNONYM", "PROCEDURE", "FUNCTION", "PACKAGE", "TYPE", "TRIGGER", "MATERIALIZED",
        "CLUSTER", "LIBRARY", "OPERATOR", "INDEXTYPE", "DOMAIN",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>CREATE 와 종류 사이에 올 수 있는 수식어.</summary>
    private static readonly FrozenSet<string> CreateModifiers = new[]
    {
        "FORCE", "NOFORCE", "EDITIONABLE", "NONEDITIONABLE", "EDITIONING", "GLOBAL", "PRIVATE", "TEMPORARY", "SHARED", "DUPLICATED",
        "UNIQUE", "BITMAP", "MULTIVALUE", "IMMUTABLE", "BLOCKCHAIN", "SHARDED",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>대상 앞 종류·IF EXISTS 를 지나 이름을 읽는 동사.</summary>
    private static readonly FrozenSet<string> ObjectVerbs = new[] { "ALTER", "DROP", "TRUNCATE", "FLASHBACK", "PURGE", "ANALYZE" }
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>GRANT·REVOKE … ON 뒤에서 소유자 객체가 아닌 것.</summary>
    private static readonly FrozenSet<string> GrantNonObjects = new[] { "DIRECTORY", "EDITION", "MINING", "JAVA", "USER", "SQL" }
        .ToFrozenSet(StringComparer.Ordinal);

    public OwnerQualifyResult Qualify(string text, string currentSchema)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentSchema);
        var r = OwnerRewriter.Qualify(text, currentSchema);
        if (r.Uncertain)
        {
            return new OwnerQualifyResult(text, false, false, null, null);
        }

        var target = ReadTarget(r.Sql);
        return new OwnerQualifyResult(r.Sql, true, r.Changed, target?.Owner, target?.Name);
    }

    public OwnerRenameResult Rewrite(string text, IReadOnlyDictionary<string, string> map)
    {
        var r = OwnerRewriter.Rewrite(text, map);
        return new OwnerRenameResult(r.Sql, r.Changed, r.Uncertain);
    }

    public string? CurrentSchemaSwitch(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lx = Lex(text);
        if (!lx.Clean || !lx.Is(0, "ALTER") || !lx.Is(1, "SESSION") || !lx.Is(2, "SET") || !lx.Is(3, "CURRENT_SCHEMA")
            || lx.Tokens.Count < 6 || lx.Text.Substring(lx.Tokens[4].Start, lx.Tokens[4].Length) != "=")
        {
            return null;
        }

        return ReadName(lx, 5, out var next) is [var one] && next == lx.Tokens.Count ? one : null;
    }

    /// <summary>
    /// 소유자를 붙인 뒤의 문장에서 바꾸는 객체(소유자, 이름)를 읽는다. 소유자가 없는 이름이면 PUBLIC 동의어만 소유자 PUBLIC, 나머지는 읽지 못함(null).
    /// </summary>
    internal static (string Owner, string Name)? ReadTarget(string text)
    {
        var lx = Lex(text);
        if (!lx.Clean || lx.Word(0) is not { } w0)
        {
            return null;
        }

        if (w0 == "COMMENT")
        {
            return lx.Is(1, "ON") ? CommentTarget(lx) : null;
        }

        if (w0 is "GRANT" or "REVOKE")
        {
            var on = FindTopWord(lx, 1, "ON");
            return on < 0 || (lx.Word(on + 1) is { } w && GrantNonObjects.Contains(w)) ? null : Owned(ReadName(lx, on + 1, out _), isPublic: false);
        }

        var i = 1;
        if (w0 == "CREATE")
        {
            if (lx.Is(i, "OR") && lx.Is(i + 1, "REPLACE"))
            {
                i += 2;
            }

            while (lx.Word(i) is { } m && (CreateModifiers.Contains(m) || (m == "NO" && lx.Is(i + 1, "FORCE"))))
            {
                i += m == "NO" ? 2 : 1;
            }
        }
        else if (!ObjectVerbs.Contains(w0) || (w0 == "ALTER" && lx.Word(1) is "SESSION" or "SYSTEM"))
        {
            return null;
        }

        var isPublic = lx.Is(i, "PUBLIC");
        if (isPublic)
        {
            i++;
        }

        if (ReadType(lx, ref i) is not { } type || (isPublic && type != "SYNONYM"))
        {
            return null;
        }

        if (lx.Is(i, "IF") && lx.Is(i + 1, "NOT") && lx.Is(i + 2, "EXISTS"))
        {
            i += 3;
        }
        else if (lx.Is(i, "IF") && lx.Is(i + 1, "EXISTS"))
        {
            i += 2;
        }

        return Owned(ReadName(lx, i, out _), isPublic);
    }

    private static (string Owner, string Name)? Owned(List<string>? parts, bool isPublic) => parts switch
    {
        [var one] when isPublic => ("PUBLIC", one),
        [var o, var n] when !isPublic => (o, n),
        _ => null,
    };

    /// <summary><c>COMMENT ON TABLE o.t</c>·<c>COMMENT ON MATERIALIZED VIEW o.m</c>·<c>COMMENT ON COLUMN o.t.c</c>.</summary>
    private static (string Owner, string Name)? CommentTarget(Lexed lx)
    {
        if (lx.Is(2, "COLUMN"))
        {
            return ReadName(lx, 3, out _) is [var o, var t, _] ? (o, t) : null;
        }

        var i = lx.Is(2, "TABLE") ? 3 : lx.Is(2, "MATERIALIZED") && lx.Is(3, "VIEW") ? 4 : -1;
        return i < 0 ? null : Owned(ReadName(lx, i, out _), isPublic: false);
    }

    private static string? ReadType(Lexed lx, ref int i)
    {
        switch (lx.Word(i))
        {
            case "PACKAGE" or "TYPE" when lx.Is(i + 1, "BODY"):
                i += 2;
                return lx.Word(i - 2) + " BODY";
            case "MATERIALIZED" when lx.Is(i + 1, "VIEW") && !lx.Is(i + 2, "LOG"):
                i += 2;
                return "MATERIALIZED VIEW";
            case "MATERIALIZED":
                return null;
            case { } w when TypeWords.Contains(w):
                i++;
                return w;
            default:
                return null;
        }
    }

    /// <summary><paramref name="from"/> 부터 괄호 밖(깊이 0)의 첫 <paramref name="word"/> 위치. 없으면 -1.</summary>
    private static int FindTopWord(Lexed lx, int from, string word)
    {
        var depth = 0;
        for (var k = from; k < lx.Tokens.Count; k++)
        {
            if (lx.IsPunct(k, '('))
            {
                depth++;
            }
            else if (lx.IsPunct(k, ')'))
            {
                depth--;
            }
            else if (depth == 0 && lx.Is(k, word))
            {
                return k;
            }
        }

        return -1;
    }

    /// <summary><paramref name="i"/> 부터 <c>a.b.c</c> 모양 이름(이름 규칙대로). 이름 토큰이 없거나 <c>@링크</c> 가 붙으면 null.</summary>
    private static List<string>? ReadName(Lexed lx, int i, out int next)
    {
        var parts = new List<string>();
        next = i;
        while (next < lx.Tokens.Count && lx.Tokens[next].Kind is TokenKind.Identifier or TokenKind.Keyword or TokenKind.QuotedIdent)
        {
            var t = lx.Tokens[next];
            var raw = lx.Text.Substring(t.Start, t.Length);
            parts.Add(t.Kind == TokenKind.QuotedIdent && raw.Length >= 2 && raw[0] == '"'
                ? raw[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
                : raw.ToUpperInvariant());
            next++;
            if (lx.IsPunct(next, '.'))
            {
                next++;
                continue;
            }

            break;
        }

        if (next < lx.Tokens.Count && lx.Text.AsSpan(lx.Tokens[next].Start, lx.Tokens[next].Length).SequenceEqual("@"))
        {
            return null; // 원격 객체(링크)는 이 DB 의 소유자가 아니다
        }

        return parts.Count == 0 ? null : parts;
    }

    private static Lexed Lex(string text)
    {
        var all = new List<Token>();
        var line = new List<Token>();
        int state = SqlLexer.Normal, pos = 0;
        while (pos <= text.Length)
        {
            var nl = text.IndexOf('\n', pos);
            var end = nl < 0 ? text.Length : nl;
            line.Clear();
            state = SqlLexer.LexLine(text.AsSpan(pos, end - pos), state, line);
            foreach (var t in line)
            {
                all.Add(t with { Start = t.Start + pos });
            }

            if (nl < 0)
            {
                break;
            }

            pos = nl + 1;
        }

        var clean = (state & 0xFF) == SqlLexer.Normal && all.TrueForAll(t => t.Kind != TokenKind.Error);
        return new Lexed(text, all.FindAll(t => t.Kind is not (TokenKind.Comment or TokenKind.Hint)), clean);
    }

    /// <summary>주석·힌트를 뺀 토큰. <see cref="Clean"/> = 렉서가 글 끝까지 보통 상태로 읽었다.</summary>
    private sealed record Lexed(string Text, List<Token> Tokens, bool Clean)
    {
        public string? Word(int i)
        {
            if (i < 0 || i >= Tokens.Count)
            {
                return null;
            }

            var t = Tokens[i];
            return t.Kind is TokenKind.Keyword or TokenKind.Identifier ? Text.Substring(t.Start, t.Length).ToUpperInvariant() : null;
        }

        public bool Is(int i, string word) => Word(i) == word;

        public bool IsPunct(int i, char c) => i >= 0 && i < Tokens.Count && Tokens[i].Kind == TokenKind.Punct && Text[Tokens[i].Start] == c;
    }
}
