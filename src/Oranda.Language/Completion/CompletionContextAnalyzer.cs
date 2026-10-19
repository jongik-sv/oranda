using System.Collections.Frozen;
using Oranda.Core.Language;
using Oranda.Core.Text;
using Oranda.Language.Lexing;

namespace Oranda.Language.Completion;

/// <summary>
/// L1(렉서 토큰) 기반 자동완성 문맥 판정(TRD 4.5). 커서가 속한 문장(<c>;</c>·단독 <c>/</c> 줄 사이)만 렉싱하고,
/// 커서 앞 토큰으로 절(FROM 뒤 테이블, <c>별칭.</c> 뒤 열, <c>스키마.</c>·<c>패키지.</c> 뒤 객체·멤버, 힌트 주석 안)을 고른다.
/// 별칭 표는 문장 전체(커서 뒤 FROM 포함)에서 모은다. L2(ANTLR) 결과로 다듬는 자리는 <see cref="LanguageService"/> 의 partial 메서드다.
/// </summary>
internal static class CompletionContextAnalyzer
{
    /// <summary>FROM 목록에서 별칭으로 보지 않는 낱말(렉서가 식별자로 내는 것 포함).</summary>
    private static readonly FrozenSet<string> AliasStopWords = new[]
    {
        "WHERE", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "NATURAL", "ON", "USING", "GROUP", "ORDER",
        "HAVING", "CONNECT", "START", "UNION", "INTERSECT", "MINUS", "EXCEPT", "MODEL", "WINDOW", "FETCH", "OFFSET", "FOR",
        "PARTITION", "SUBPARTITION", "SAMPLE", "PIVOT", "UNPIVOT", "AS", "SET", "VALUES", "RETURNING", "RETURN", "LOG",
        "WHEN", "THEN", "SELECT", "FROM", "INTO", "WITH", "LATERAL", "APPLY", "VERSIONS", "QUALIFY", "LIMIT", "AND", "OR",
        "NOT", "IS", "IN", "DEFAULT", "BULK", "COLLECT", "SEED",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>FROM 목록을 끝내는 낱말.</summary>
    private static readonly FrozenSet<string> FromListEnd = new[]
    {
        "WHERE", "ON", "USING", "GROUP", "ORDER", "HAVING", "CONNECT", "START", "UNION", "INTERSECT", "MINUS", "EXCEPT",
        "MODEL", "WINDOW", "FETCH", "OFFSET", "SELECT", "RETURNING", "SET", "VALUES", "QUALIFY", "LIMIT", "FOR",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>뒤에 열·식이 오는 절 낱말.</summary>
    private static readonly FrozenSet<string> ExpressionClauses = new[]
    {
        "SELECT", "WHERE", "ON", "BY", "HAVING", "SET", "VALUES", "RETURNING", "WITH", "WHEN", "THEN", "ELSE", "AND", "OR",
        "NOT", "DISTINCT", "UNIQUE", "ALL", "CASE", "IN", "LIKE", "BETWEEN", "IS", "PRIOR", "EXISTS", "ANY", "SOME", "RETURN",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>뒤로 걸어가며 만나면 멈추는 절 시작 낱말.</summary>
    private static readonly FrozenSet<string> ClauseKeywords = new[]
    {
        "SELECT", "FROM", "JOIN", "WHERE", "ON", "BY", "HAVING", "SET", "INTO", "UPDATE", "VALUES", "USING", "RETURNING",
        "DELETE", "INSERT", "MERGE", "TABLE", "BEGIN", "DECLARE", "LOOP", "THEN", "ELSE", "IS", "AS", "WITH", "START",
        "DESC", "DESCRIBE", "TRUNCATE", "EXCEPTION",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly record struct Tok(int Start, int End, TokenKind Kind, string Upper);

    private readonly record struct TableRef(string Alias, string Name, int Score);

    public static CompletionContext Analyze(string text, int offset)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (offset < 0 || offset > text.Length)
        {
            return CompletionContext.None;
        }

        var all = CollectStatementTokens(text, offset);

        // 1) 주석·문자열·힌트 안인지
        foreach (var t in all)
        {
            if (t.Start >= offset)
            {
                break;
            }

            if (t.Kind is TokenKind.Comment or TokenKind.StringLiteral or TokenKind.QuotedIdent or TokenKind.Hint && IsInside(text, t, offset))
            {
                return t.Kind == TokenKind.Hint ? HintContext(text, t, offset) : CompletionContext.None;
            }
        }

        // 2) 커서 앞 낱말(접두어)과 점 앞 이름(한정자)
        int prefixStart = offset;
        while (prefixStart > 0 && IsIdentPart(text[prefixStart - 1]))
        {
            prefixStart--;
        }

        if (prefixStart < offset && !char.IsLetter(text[prefixStart]))
        {
            return CompletionContext.None; // 숫자
        }

        if (prefixStart > 0 && text[prefixStart - 1] is ':' or '&')
        {
            return CompletionContext.None; // 바인드·치환 변수
        }

        string prefix = text[prefixStart..offset];
        var range = new TextRange(prefixStart, offset - prefixStart);
        int nameStart = prefixStart;
        string? qualifier = null;
        if (prefixStart > 0 && text[prefixStart - 1] == '.')
        {
            if (prefixStart > 1 && text[prefixStart - 2] == '.')
            {
                return CompletionContext.None; // 범위 연산자 ..
            }

            qualifier = ReadQualifier(text, prefixStart - 1, out nameStart);
            if (qualifier is null)
            {
                return CompletionContext.None;
            }
        }

        var toks = new List<Tok>(all.Count);
        foreach (var t in all)
        {
            if (t.Kind is not (TokenKind.Comment or TokenKind.Hint))
            {
                toks.Add(t);
            }
        }

        var aliases = CollectAliases(toks, offset);
        int before = LastIndexBefore(toks, nameStart);
        var kind = qualifier is null
            ? Classify(toks, before)
            : ClassifyQualified(toks, before, qualifier, aliases);

        return new CompletionContext(kind, prefix, range) { Qualifier = qualifier, Aliases = aliases };
    }

    // ---- 문장 토큰 ---------------------------------------------------------------------------

    /// <summary>커서가 든 문장의 토큰. 커서 앞 <c>;</c>·<c>/</c> 줄에서 버리고, 커서 뒤 첫 끝 표지에서 멈춘다.</summary>
    private static List<Tok> CollectStatementTokens(string text, int offset)
    {
        // 문서 앞쪽 문장의 토큰은 곧 버려지므로 이름 문자열은 마지막에 남은 문장 토큰만 만든다.
        var raw = new List<Token>();
        var line = new List<Token>();
        int state = SqlLexer.Normal, pos = RestartPoint(text, offset);
        while (pos <= text.Length)
        {
            int nl = text.IndexOf('\n', pos);
            int end = nl < 0 ? text.Length : nl;
            if (state == SqlLexer.Normal)
            {
                var trimmed = text.AsSpan(pos, end - pos).Trim();
                if (trimmed.Length == 1 && trimmed[0] == '/')
                {
                    if (pos > offset)
                    {
                        break;
                    }

                    raw.Clear();
                    if (nl < 0)
                    {
                        break;
                    }

                    pos = nl + 1;
                    continue;
                }
            }

            line.Clear();
            state = SqlLexer.LexLine(text.AsSpan(pos, end - pos), state, line);
            foreach (var lt in line)
            {
                var t = lt with { Start = lt.Start + pos };
                if (t.Kind == TokenKind.Punct && text[t.Start] == ';')
                {
                    if (t.End <= offset)
                    {
                        raw.Clear();
                        continue;
                    }

                    return Named(text, raw);
                }

                raw.Add(t);
            }

            if (nl < 0)
            {
                break;
            }

            pos = nl + 1;
        }

        return Named(text, raw);
    }

    /// <summary>
    /// 렉싱을 시작할 줄. 커서 줄 위로 올라가며 <c>;</c> 로 끝나는 줄이나 단독 <c>/</c> 줄을 찾아 그 다음 줄에서 시작한다
    /// (1만 줄 문서에서 문서 전체를 렉싱하지 않기 위해). 여러 줄 문자열·블록 주석 안의 줄이 <c>;</c> 로 끝나면 잘못 짚을 수 있다 — L2 가 바로잡는 자리.
    /// </summary>
    private static int RestartPoint(string text, int offset)
    {
        int pos = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
        while (pos > 0)
        {
            int prevEnd = pos - 1; // 줄바꿈 위치
            int prevStart = prevEnd == 0 ? 0 : text.LastIndexOf('\n', prevEnd - 1) + 1;
            var prev = text.AsSpan(prevStart, prevEnd - prevStart).Trim();
            if (prev.Length > 0 && (prev[^1] == ';' || (prev.Length == 1 && prev[0] == '/')))
            {
                return pos;
            }

            pos = prevStart;
        }

        return 0;
    }

    private static List<Tok> Named(string text, List<Token> raw)
    {
        var toks = new List<Tok>(raw.Count);
        foreach (var t in raw)
        {
            string upper = t.Kind switch
            {
                TokenKind.Identifier or TokenKind.Keyword => text.Substring(t.Start, t.Length).ToUpperInvariant(),
                TokenKind.Punct or TokenKind.Operator => text.Substring(t.Start, t.Length),
                TokenKind.QuotedIdent => text.Substring(t.Start, t.Length).Trim('"'),
                _ => string.Empty,
            };
            toks.Add(new Tok(t.Start, t.End, t.Kind, upper));
        }

        return toks;
    }

    private static bool IsInside(string text, Tok t, int offset)
    {
        if (offset <= t.Start)
        {
            return false;
        }

        if (offset < t.End)
        {
            return true;
        }

        // 끝 위치: 닫히지 않은 주석·문자열이면 안쪽.
        var s = text.AsSpan(t.Start, t.End - t.Start);
        return t.Kind switch
        {
            TokenKind.Comment when s.StartsWith("--") => true,
            TokenKind.Comment or TokenKind.Hint => !(s.Length >= 4 && s.EndsWith("*/")),
            TokenKind.QuotedIdent => !(s.Length >= 2 && s[^1] == '"'),
            _ => !(s.Length >= 2 && s[^1] == '\'' || s.Length >= 3 && s[^1] == '\'' && s[0] is 'q' or 'Q' or 'n' or 'N'),
        };
    }

    private static bool IsClosedString(ReadOnlySpan<char> s)
    {
        if (s.Length > 0 && s[0] is 'n' or 'N')
        {
            s = s[1..];
        }

        if (s.Length > 0 && s[0] is 'q' or 'Q')
        {
            return s.Length >= 5 && s[^1] == '\'';
        }

        return s.Length >= 2 && s[^1] == '\'';
    }

    private static CompletionContext HintContext(string text, Tok hint, int offset)
    {
        int bodyStart = hint.Start + 3; // "/*+"
        if (offset < bodyStart)
        {
            return CompletionContext.None;
        }

        int start = offset;
        while (start > bodyStart && IsIdentPart(text[start - 1]))
        {
            start--;
        }

        // 괄호 안(테이블·인덱스 인자)은 힌트 이름이 아니다.
        int depth = 0;
        for (int i = bodyStart; i < start; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && depth > 0)
            {
                depth--;
            }
        }

        if (depth > 0)
        {
            return CompletionContext.None;
        }

        return new CompletionContext(CompletionContextKind.Hint, text[start..offset], new TextRange(start, offset - start));
    }

    // ---- 한정자 ------------------------------------------------------------------------------

    /// <summary><paramref name="dot"/> 위치의 점 앞 이름 사슬(<c>a.b</c>). 이름은 정규화(따옴표 없으면 대문자).</summary>
    private static string? ReadQualifier(string text, int dot, out int start)
    {
        var parts = new List<string>();
        int i = dot;
        start = dot;
        while (i >= 0 && text[i] == '.')
        {
            int end = i;
            int j = end;
            string part;
            if (j > 0 && text[j - 1] == '"')
            {
                int open = text.LastIndexOf('"', j - 2 < 0 ? 0 : j - 2);
                if (open < 0 || open == j - 1)
                {
                    break;
                }

                part = text[(open + 1)..(j - 1)];
                j = open;
            }
            else
            {
                while (j > 0 && IsIdentPart(text[j - 1]))
                {
                    j--;
                }

                if (j == end || !char.IsLetter(text[j]))
                {
                    break;
                }

                part = text[j..end].ToUpperInvariant();
            }

            parts.Insert(0, part);
            start = j;
            i = j - 1;
        }

        return parts.Count == 0 ? null : string.Join('.', parts);
    }

    // ---- 문맥 판정 ---------------------------------------------------------------------------

    private static int LastIndexBefore(List<Tok> toks, int pos)
    {
        for (int i = toks.Count - 1; i >= 0; i--)
        {
            if (toks[i].End <= pos)
            {
                return i;
            }
        }

        return -1;
    }

    private static string FirstWord(List<Tok> toks) => toks.Count > 0 ? toks[0].Upper : string.Empty;

    /// <summary>같은 괄호 깊이에서 뒤로 걸어 처음 만나는 절 낱말. 괄호를 빠져나가면 "(".</summary>
    private static (string Clause, int Index) FindClause(List<Tok> toks, int from)
    {
        int depth = 0;
        for (int i = from; i >= 0; i--)
        {
            var t = toks[i];
            if (t.Upper == ")")
            {
                depth++;
            }
            else if (t.Upper == "(")
            {
                if (depth == 0)
                {
                    return ("(", i);
                }

                depth--;
            }
            else if (depth == 0 && t.Kind == TokenKind.Keyword && ClauseKeywords.Contains(t.Upper))
            {
                return (t.Upper, i);
            }
            else if (depth == 0 && t.Kind == TokenKind.Identifier && t.Upper is "START" or "DESCRIBE")
            {
                return (t.Upper, i);
            }
        }

        return (string.Empty, -1);
    }

    private static bool IsTableKeyword(List<Tok> toks, int i)
    {
        var t = toks[i];
        if (t.Kind is not (TokenKind.Keyword or TokenKind.Identifier))
        {
            return false;
        }

        switch (t.Upper)
        {
            case "FROM":
                return !InsideFunctionCall(toks, i);
            case "JOIN":
            case "UPDATE":
                return true;
            case "INTO":
                {
                    // INSERT INTO, MERGE INTO, INSERT ALL INTO 만 테이블. SELECT … INTO 변수는 아니다.
                    var p = i > 0 ? toks[i - 1].Upper : string.Empty;
                    return p is "INSERT" or "MERGE" or "ALL" or "FIRST" or ")" || (p != "COLLECT" && FirstWord(toks) is "INSERT" or "MERGE");
                }

            case "USING":
                return FirstWord(toks) == "MERGE" && !(i + 1 < toks.Count && toks[i + 1].Upper == "(");
            case "TABLE":
                return i > 0 && toks[i - 1].Upper is "TRUNCATE" or "LOCK" or "ALTER" or "DROP" or "ANALYZE" or "COMMENT" or "ON";
            case "DESC":
            case "DESCRIBE":
                return i == 0;
            default:
                return false;
        }
    }

    /// <summary><c>EXTRACT(YEAR FROM x)</c>·<c>TRIM(… FROM x)</c> 안의 FROM 인지.</summary>
    private static bool InsideFunctionCall(List<Tok> toks, int i)
    {
        int depth = 0;
        for (int k = i - 1; k >= 0; k--)
        {
            if (toks[k].Upper == ")")
            {
                depth++;
            }
            else if (toks[k].Upper == "(")
            {
                if (depth == 0)
                {
                    return k > 0 && toks[k - 1].Upper is "EXTRACT" or "TRIM";
                }

                depth--;
            }
        }

        return false;
    }

    private static CompletionContextKind Classify(List<Tok> toks, int before)
    {
        if (before < 0)
        {
            return CompletionContextKind.Keyword;
        }

        var p = toks[before];
        if (IsTableKeyword(toks, before))
        {
            return CompletionContextKind.TableOrView;
        }

        // ALTER SESSION SET CURRENT_SCHEMA = |
        if (p.Upper == "=" && before > 0 && toks[before - 1].Upper == "CURRENT_SCHEMA")
        {
            return CompletionContextKind.Schema;
        }

        var (clause, ci) = FindClause(toks, before);
        if (p.Upper == "," && clause is "FROM")
        {
            return CompletionContextKind.TableOrView;
        }

        if (clause is "FROM" or "JOIN" or "UPDATE" or "TABLE" or "DESC" or "DESCRIBE" || (clause == "INTO" && ci >= 0 && IsTableKeyword(toks, ci)))
        {
            return CompletionContextKind.Keyword; // 테이블 이름·별칭 뒤
        }

        if (clause is "BEGIN" or "DECLARE" or "LOOP" or "EXCEPTION" || clause.Length == 0)
        {
            // PL/SQL 문 시작 또는 문장 시작. 식 안(연산자 뒤)이면 열.
            return IsExpressionStart(p) ? CompletionContextKind.Column : CompletionContextKind.Keyword;
        }

        // 식 자리: 연산자·쉼표·여는 괄호·식 낱말 뒤면 열, 완성된 식 뒤면 키워드(별칭·다음 절).
        return IsExpressionStart(p) ? CompletionContextKind.Column : CompletionContextKind.Keyword;
    }

    private static bool IsExpressionStart(Tok p) => p.Kind switch
    {
        TokenKind.Operator => true,
        TokenKind.Punct => p.Upper is "," or "(",
        TokenKind.Keyword => ExpressionClauses.Contains(p.Upper) || p.Upper is "THEN" or "ELSE" or "WHEN",
        TokenKind.Identifier => p.Upper is "PRIOR",
        _ => false,
    };

    private static CompletionContextKind ClassifyQualified(List<Tok> toks, int before, string qualifier, Dictionary<string, string> aliases)
    {
        // 점 앞 이름 사슬의 앞 토큰을 본다(FROM hr.| → 스키마 안 객체).
        if (before >= 0 && (IsTableKeyword(toks, before) || (toks[before].Upper == "," && FindClause(toks, before).Clause == "FROM")))
        {
            return CompletionContextKind.TableOrView;
        }

        if (aliases.ContainsKey(qualifier))
        {
            return CompletionContextKind.Column;
        }

        return CompletionContextKind.PackageMember;
    }

    // ---- 별칭 표 -----------------------------------------------------------------------------

    /// <summary>
    /// 문장 안 FROM·JOIN·UPDATE·INSERT INTO·MERGE INTO/USING 의 테이블 참조에서 별칭 → 객체 이름 표를 만든다.
    /// 테이블 이름 자신(<c>EMP</c>, <c>HR.EMP</c>)도 키로 넣는다. 같은 별칭이 여러 번 나오면 커서를 감싼 가장 안쪽 괄호의 것을 쓴다.
    /// </summary>
    private static Dictionary<string, string> CollectAliases(List<Tok> toks, int offset)
    {
        // 괄호 짝
        var close = new int[toks.Count];
        var stack = new Stack<int>();
        for (int i = 0; i < toks.Count; i++)
        {
            close[i] = -1;
            if (toks[i].Upper == "(")
            {
                stack.Push(i);
            }
            else if (toks[i].Upper == ")" && stack.Count > 0)
            {
                close[stack.Pop()] = i;
            }
        }

        var refs = new List<TableRef>();
        var groupStack = new Stack<(int Open, bool FromList)>();
        groupStack.Push((-1, false));
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Upper == "(")
            {
                groupStack.Push((i, false));
                continue;
            }

            if (t.Upper == ")")
            {
                if (groupStack.Count > 1)
                {
                    groupStack.Pop();
                }

                continue;
            }

            var (open, fromList) = groupStack.Peek();
            bool parse = false;
            if (t.Kind is TokenKind.Keyword or TokenKind.Identifier)
            {
                if (t.Upper is "FROM" or "JOIN")
                {
                    if (IsTableKeyword(toks, i))
                    {
                        fromList = true;
                        parse = true;
                    }
                }
                else if (IsTableKeyword(toks, i))
                {
                    parse = true;
                }
                else if (FromListEnd.Contains(t.Upper))
                {
                    fromList = false;
                }
            }
            else if (t.Upper == "," && fromList)
            {
                parse = true;
            }

            groupStack.Pop();
            groupStack.Push((open, fromList));
            if (parse && i + 1 < toks.Count)
            {
                int score = ScoreOf(open, close, toks, offset);
                ParseTableRef(toks, i + 1, score, offset, refs);
            }
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in refs)
        {
            if (!scores.TryGetValue(r.Alias, out var s) || r.Score > s)
            {
                map[r.Alias] = r.Name;
                scores[r.Alias] = r.Score;
            }
        }

        return map;
    }

    /// <summary>참조가 든 괄호 묶음이 커서를 감싸면 안쪽일수록 큰 점수, 아니면 가장 낮다.</summary>
    private static int ScoreOf(int open, int[] close, List<Tok> toks, int offset)
    {
        if (open < 0)
        {
            return 0;
        }

        int end = close[open] < 0 ? int.MaxValue : toks[close[open]].Start;
        return toks[open].End <= offset && offset <= end ? open + 1 : -1;
    }

    private static void ParseTableRef(List<Tok> toks, int j, int score, int offset, List<TableRef> refs)
    {
        if (toks[j].Upper == "(")
        {
            return; // 부분 질의·TABLE(…) — 열을 모른다
        }

        var parts = new List<string>();
        int k = j;
        while (k < toks.Count && toks[k].Kind is TokenKind.Identifier or TokenKind.QuotedIdent or TokenKind.Keyword)
        {
            var t = toks[k];
            if (t.Kind == TokenKind.Keyword && parts.Count == 0 && t.Upper is not ("USER" or "TABLE" or "INDEX"))
            {
                return;
            }

            // 커서가 지금 쓰고 있는 낱말은 아직 이름이 아니다.
            if (t.Start <= offset && offset <= t.End)
            {
                return;
            }

            parts.Add(NameOf(t));
            if (k + 1 < toks.Count && toks[k + 1].Upper == "." && k + 2 < toks.Count && toks[k + 1].End == toks[k + 2].Start)
            {
                k += 2;
                continue;
            }

            k++;
            break;
        }

        if (parts.Count == 0)
        {
            return;
        }

        // @dblink
        if (k < toks.Count && toks[k].Upper == "@")
        {
            k++;
            while (k < toks.Count && (toks[k].Kind is TokenKind.Identifier or TokenKind.QuotedIdent || toks[k].Upper == "."))
            {
                k++;
            }
        }

        string name = string.Join('.', parts);
        refs.Add(new TableRef(name, name, score));
        if (parts.Count > 1)
        {
            refs.Add(new TableRef(parts[^1], name, score - 1));
        }

        if (k < toks.Count && toks[k].Upper == "AS")
        {
            k++;
        }

        if (k < toks.Count && toks[k].Kind is TokenKind.Identifier or TokenKind.QuotedIdent && !AliasStopWords.Contains(toks[k].Upper))
        {
            var a = toks[k];
            if (!(a.Start <= offset && offset <= a.End))
            {
                refs.Add(new TableRef(NameOf(a), name, score));
            }
        }
    }

    /// <summary>토큰 이름(따옴표 없는 이름은 대문자, 따옴표 이름은 그대로).</summary>
    private static string NameOf(Tok t) => t.Upper;

    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';
}
