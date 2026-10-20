using Oranda.Language.Lexing;

namespace Oranda.Language.Lint;

/// <summary>점검용 토큰. 주석·힌트는 빼고, 하위 질의 <c>( SELECT … )</c> 는 토큰 하나(<see cref="Sub"/>)로 접는다.</summary>
internal sealed class Tok
{
    public int Start { get; init; }

    public int End { get; init; }

    /// <summary>원문.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>비교용: 따옴표 없는 이름·키워드는 대문자, 따옴표 이름은 안쪽 글자 그대로.</summary>
    public string Up { get; init; } = string.Empty;

    public TokenKind Kind { get; init; }

    /// <summary>블록 안 괄호 깊이(하위 질의 괄호는 세지 않는다).</summary>
    public int Depth { get; set; }

    public Block? Sub { get; init; }

    public bool IsWord => Kind is TokenKind.Identifier or TokenKind.Keyword or TokenKind.QuotedIdent;

    public bool IsPunct(char c) => Kind == TokenKind.Punct && Text.Length == 1 && Text[0] == c;

    public bool IsWordUp(string up) => Kind is TokenKind.Identifier or TokenKind.Keyword && Up == up;
}

/// <summary>질의 블록 하나(SELECT 한 덩어리, UNION 의 한쪽, UPDATE/DELETE 문 자체).</summary>
internal sealed class Block
{
    public List<Tok> Toks { get; } = [];

    public Block? Parent { get; init; }

    public Dictionary<string, (int Start, int End)>? ClauseCache { get; set; }

    /// <summary>첫 깊이 0 동사(SELECT·UPDATE·DELETE·INSERT·MERGE).</summary>
    public string Verb { get; set; } = string.Empty;
}

/// <summary>FROM 의 표 하나. <see cref="Unit"/> 은 쉼표로 나뉜 묶음 번호(ANSI JOIN 으로 이어진 표는 같은 묶음).</summary>
internal sealed class FromRef
{
    public string? Owner { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Alias { get; init; } = string.Empty;

    public bool IsSub { get; init; }

    public int Unit { get; init; }

    public Tok First { get; init; } = null!;
}

/// <summary>열 참조. <see cref="Last"/> 는 마지막 토큰 인덱스(바깥 조인 <c>(+)</c> 포함).</summary>
internal sealed record ColRef(string? Qualifier, string Name, int First, int Last, bool OuterPlus);

/// <summary>한 문장을 점검용으로 나눈 결과.</summary>
internal sealed class LintModel
{
    public List<Block> Blocks { get; } = [];

    public required string Text { get; init; }

    public static readonly HashSet<string> SetOps = new(StringComparer.Ordinal) { "UNION", "INTERSECT", "MINUS" };

    private static readonly HashSet<string> ClauseWords = new(StringComparer.Ordinal)
    {
        "SELECT", "FROM", "WHERE", "GROUP", "HAVING", "ORDER", "CONNECT", "START", "SET", "MODEL", "RETURNING", "UPDATE", "DELETE", "INSERT", "MERGE",
    };

    private static readonly HashSet<string> Verbs = new(StringComparer.Ordinal) { "SELECT", "UPDATE", "DELETE", "INSERT", "MERGE" };

    public static LintModel Parse(string text)
    {
        var toks = new List<Tok>();
        foreach (var t in SqlLexer.Tokenize(text))
        {
            if (t.Kind is TokenKind.Comment or TokenKind.Hint)
            {
                continue;
            }

            var raw = text.Substring(t.Start, t.Length);
            string up = t.Kind switch
            {
                TokenKind.QuotedIdent => raw.Length >= 2 ? raw[1..^1] : raw,
                TokenKind.Identifier or TokenKind.Keyword => raw.ToUpperInvariant(),
                _ => raw,
            };
            toks.Add(new Tok { Start = t.Start, End = t.End, Text = raw, Up = up, Kind = t.Kind });
        }

        var model = new LintModel { Text = text };
        int i = 0;
        model.ParseSequence(toks, ref i, null, nested: false);
        foreach (var b in model.Blocks)
        {
            b.Verb = b.Toks.FirstOrDefault(t => t.Depth == 0 && t.Kind is TokenKind.Identifier or TokenKind.Keyword && Verbs.Contains(t.Up))?.Up ?? string.Empty;
            if (b.Verb == "INSERT" && b.Toks.Any(t => t.Depth == 0 && t.IsWordUp("SELECT")))
            {
                b.Verb = "SELECT"; // INSERT … SELECT: 조회 부분을 점검한다
            }
        }

        return model;
    }

    /// <summary>집합 연산으로 이어진 블록들을 읽는다. 첫 블록을 돌려주고, 중첩이면 닫는 괄호 앞에서 멈춘다.</summary>
    private Block ParseSequence(List<Tok> toks, ref int i, Block? parent, bool nested)
    {
        var first = NewBlock(parent);
        var cur = first;
        int depth = 0;
        while (i < toks.Count)
        {
            var t = toks[i];
            if (t.IsPunct('(') && i + 1 < toks.Count && toks[i + 1].IsWordUp2("SELECT", "WITH"))
            {
                int open = t.Start;
                i++;
                var child = ParseSequence(toks, ref i, cur, nested: true);
                int close = i < toks.Count ? toks[i].End : open + 1;
                if (i < toks.Count)
                {
                    i++;
                }

                cur.Toks.Add(new Tok { Start = open, End = close, Text = "(…)", Up = "(…)", Kind = TokenKind.Error, Depth = depth, Sub = child });
                continue;
            }

            if (t.IsPunct(')'))
            {
                if (nested && depth == 0)
                {
                    return first;
                }

                depth = Math.Max(0, depth - 1);
                t.Depth = depth;
                cur.Toks.Add(t);
                i++;
                continue;
            }

            if (depth == 0 && t.Kind is TokenKind.Identifier or TokenKind.Keyword && SetOps.Contains(t.Up))
            {
                i++;
                if (i < toks.Count && toks[i].IsWordUp("ALL") || i < toks.Count && toks[i].IsWordUp("DISTINCT"))
                {
                    i++;
                }

                cur = NewBlock(parent);
                continue;
            }

            if (t.IsPunct(';'))
            {
                i++;
                continue;
            }

            t.Depth = depth;
            if (t.IsPunct('('))
            {
                depth++;
            }

            cur.Toks.Add(t);
            i++;
        }

        return first;
    }

    private Block NewBlock(Block? parent)
    {
        var b = new Block { Parent = parent };
        Blocks.Add(b);
        return b;
    }

    // ---- 절·조건 ----------------------------------------------------------------------------

    /// <summary>깊이 0 절 키워드(SELECT·FROM·WHERE·GROUP·HAVING·ORDER·CONNECT·START·SET)별 토큰 구간 [Start, End). 키워드 자리 포함, 처음 나온 것만.</summary>
    public static (int Start, int End)? Clause(Block b, string word)
    {
        b.ClauseCache ??= BuildClauses(b);
        return b.ClauseCache.TryGetValue(word, out var r) ? r : null;
    }

    private static Dictionary<string, (int, int)> BuildClauses(Block b)
    {
        var result = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        string? open = null;
        int start = 0;
        for (int i = 0; i < b.Toks.Count; i++)
        {
            var t = b.Toks[i];
            if (t.Depth != 0 || t.Kind is not (TokenKind.Identifier or TokenKind.Keyword) || !ClauseWords.Contains(t.Up))
            {
                continue;
            }

            // 열 이름·별칭 자리(a.model, model.x)는 절 키워드가 아니다. MODEL 은 뒤에 MODEL 절 단어가 올 때만, RETURNING 은 DML 일 때만 절이다.
            if (i > 0 && b.Toks[i - 1].IsPunct('.') || i + 1 < b.Toks.Count && b.Toks[i + 1].IsPunct('.'))
            {
                continue;
            }

            if (t.Up == "MODEL" && !(i + 1 < b.Toks.Count && b.Toks[i + 1].Up is "DIMENSION" or "PARTITION" or "MAIN" or "REFERENCE" or "RETURN" or "IGNORE" or "KEEP" or "UNIQUE"))
            {
                continue;
            }

            if (t.Up == "RETURNING" && b.Verb is not ("UPDATE" or "DELETE" or "INSERT"))
            {
                continue;
            }

            // FOR UPDATE, SELECT 뒤 DISTINCT 같은 꼬리는 절이 아니다. UPDATE/DELETE/INSERT/MERGE 는 문장 첫 동사일 때만 절로 본다.
            if (t.Up is "UPDATE" or "DELETE" or "INSERT" or "MERGE" && !(i == 0 || b.Toks[i - 1].IsWordUp("WITH")))
            {
                continue;
            }

            if (open is not null && !result.ContainsKey(open))
            {
                result[open] = (start, i);
            }

            open = t.Up;
            start = i;
        }

        if (open is not null && !result.ContainsKey(open))
        {
            result[open] = (start, b.Toks.Count);
        }

        return result;
    }

    /// <summary>[start, end) 구간을 깊이 0 AND 로 나눈다(BETWEEN … AND … 의 AND 는 나누지 않는다). 구간은 [Start, End).</summary>
    public static List<(int Start, int End)> Conjuncts(Block b, int start, int end)
    {
        var list = new List<(int, int)>();
        int baseDepth = start < end ? b.Toks[start].Depth : 0;
        int from = start;
        bool between = false;
        for (int i = start; i < end; i++)
        {
            var t = b.Toks[i];
            if (t.Depth != baseDepth || t.Kind is not (TokenKind.Identifier or TokenKind.Keyword))
            {
                continue;
            }

            if (t.Up == "BETWEEN")
            {
                between = true;
            }
            else if (t.Up == "AND")
            {
                if (between)
                {
                    between = false;
                    continue;
                }

                if (i > from)
                {
                    list.Add((from, i));
                }

                from = i + 1;
            }
        }

        if (end > from)
        {
            list.Add((from, end));
        }

        return list;
    }

    // ---- FROM -------------------------------------------------------------------------------

    private static readonly HashSet<string> JoinWords = new(StringComparer.Ordinal) { "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "NATURAL", "OUTER" };

    private static readonly HashSet<string> NotAlias = new(StringComparer.Ordinal)
    {
        "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "NATURAL", "OUTER", "ON", "USING", "WHERE", "GROUP", "HAVING", "ORDER", "CONNECT", "START", "SET",
        "PARTITION", "SAMPLE", "FOR", "RETURNING", "MODEL", "PIVOT", "UNPIVOT", "UNION", "INTERSECT", "MINUS", "AS", "WHEN", "VALUES",
    };

    /// <summary>블록의 FROM(UPDATE 는 표 자리, DELETE 는 FROM 뒤)에 나온 표들.</summary>
    public static List<FromRef> FromRefs(Block b)
    {
        int s, e;
        if (b.Verb == "UPDATE")
        {
            var upd = Clause(b, "UPDATE");
            var set = Clause(b, "SET");
            if (upd is null)
            {
                return [];
            }

            s = upd.Value.Start;
            e = set?.Start ?? upd.Value.End;
        }
        else
        {
            var from = Clause(b, "FROM");
            if (from is null)
            {
                return [];
            }

            (s, e) = from.Value;
        }

        var refs = new List<FromRef>();
        int unit = -1;
        int i = s + 1;
        bool newUnit = true;
        while (i < e)
        {
            var t = b.Toks[i];
            if (t.Depth != b.Toks[s].Depth)
            {
                i++;
                continue;
            }

            if (t.IsPunct(','))
            {
                newUnit = true;
                i++;
                continue;
            }

            if (t.Kind is TokenKind.Identifier or TokenKind.Keyword && (JoinWords.Contains(t.Up) || t.Up is "ON" or "USING"))
            {
                i = t.Up is "ON" or "USING" ? SkipJoinCondition(b, i, e) : i + 1;
                continue;
            }

            if (t.Sub is not null || t.IsWord)
            {
                if (newUnit)
                {
                    unit++;
                    newUnit = false;
                }

                i = ReadTableRef(b, i, e, unit, refs);
                continue;
            }

            i++;
        }

        return refs;
    }

    private static int SkipJoinCondition(Block b, int i, int e)
    {
        int d = b.Toks[i].Depth;
        i++;
        while (i < e)
        {
            var t = b.Toks[i];
            if (t.Depth == d && (t.IsPunct(',') || t.Kind is TokenKind.Identifier or TokenKind.Keyword && JoinWords.Contains(t.Up)))
            {
                break;
            }

            i++;
        }

        return i;
    }

    private static int ReadTableRef(Block b, int i, int e, int unit, List<FromRef> refs)
    {
        var first = b.Toks[i];
        string? owner = null;
        string name;
        bool isSub = false;
        if (first.Sub is not null)
        {
            name = string.Empty;
            isSub = true;
            i++;
        }
        else
        {
            name = first.Up;
            i++;
            while (i + 1 < e && b.Toks[i].IsPunct('.') && b.Toks[i + 1].IsWord)
            {
                owner = owner is null ? name : owner + "." + name;
                name = b.Toks[i + 1].Up;
                i += 2;
            }

            if (i < e && b.Toks[i].IsPunct('(') || i < e && b.Toks[i].Kind == TokenKind.Operator && b.Toks[i].Text == "@")
            {
                // TABLE(...) 같은 표 함수·DB 링크는 표 정의를 모른다.
                isSub = true;
                if (b.Toks[i].IsPunct('('))
                {
                    i = SkipParens(b, i, e);
                }
                else
                {
                    i += 2;
                }
            }
        }

        string alias = name;
        if (i < e && b.Toks[i].IsWordUp("AS"))
        {
            i++;
        }

        if (i < e && b.Toks[i].IsWord && !NotAlias.Contains(b.Toks[i].Up) && b.Toks[i].Depth == first.Depth)
        {
            alias = b.Toks[i].Up;
            i++;
        }

        refs.Add(new FromRef { Owner = owner, Name = name, Alias = alias, IsSub = isSub, Unit = unit, First = first });
        return i;
    }

    private static int SkipParens(Block b, int i, int e)
    {
        int d = b.Toks[i].Depth;
        i++;
        while (i < e && !(b.Toks[i].IsPunct(')') && b.Toks[i].Depth == d))
        {
            i++;
        }

        return Math.Min(e, i + 1);
    }

    // ---- 열 참조 ----------------------------------------------------------------------------

    private static readonly HashSet<string> NotColumns = new(StringComparer.Ordinal)
    {
        "NULL", "TRUE", "FALSE", "ROWNUM", "ROWID", "SYSDATE", "SYSTIMESTAMP", "CURRENT_DATE", "CURRENT_TIMESTAMP", "LEVEL", "USER", "DUAL", "DEFAULT",
        "AND", "OR", "NOT", "IN", "IS", "LIKE", "BETWEEN", "EXISTS", "ANY", "ALL", "SOME", "CASE", "WHEN", "THEN", "ELSE", "END", "DISTINCT", "AS",
        "ASC", "DESC", "NULLS", "FIRST", "LAST", "ESCAPE", "INTERVAL", "DATE", "TIMESTAMP", "PRIOR", "CONNECT_BY_ROOT", "SELECT", "FROM", "WHERE", "BY",
        "OVER", "PARTITION", "ROWS", "RANGE", "CURRENT", "ROW", "UNBOUNDED", "PRECEDING", "FOLLOWING", "SYS_CONTEXT",
    };

    /// <summary>[start, end) 구간의 열 참조들(함수 이름·키워드·리터럴 제외). <c>a.b</c> 는 한 건, 뒤에 <c>(+)</c> 가 붙으면 OuterPlus.</summary>
    public static List<ColRef> ColumnRefs(Block b, int start, int end)
    {
        var list = new List<ColRef>();
        for (int i = start; i < end; i++)
        {
            var t = b.Toks[i];
            if (!t.IsWord)
            {
                continue;
            }

            int last = i;
            string? qual = null;
            string name = t.Up;
            if (i + 2 < end + 0 && b.Toks[i + 1].IsPunct('.') && b.Toks[i + 2].IsWord)
            {
                qual = name;
                name = b.Toks[i + 2].Up;
                last = i + 2;
                if (last + 1 < end && b.Toks[last + 1].IsPunct('.') && last + 2 < end && b.Toks[last + 2].IsWord)
                {
                    // 스키마.표.열
                    qual = name;
                    name = b.Toks[last + 2].Up;
                    last += 2;
                }
            }
            else if (t.Kind == TokenKind.Keyword && NotColumns.Contains(t.Up) || NotColumns.Contains(t.Up) && t.Kind != TokenKind.QuotedIdent)
            {
                continue;
            }
            else if (i > start && b.Toks[i - 1].IsPunct('.'))
            {
                continue;
            }

            if (last + 1 < end && b.Toks[last + 1].IsPunct('('))
            {
                bool outer = last + 3 < end + 0 && b.Toks[last + 2].Kind == TokenKind.Operator && b.Toks[last + 2].Text == "+" && b.Toks[last + 3].IsPunct(')');
                if (!outer)
                {
                    i = last;
                    continue; // 함수 호출
                }

                list.Add(new ColRef(qual, name, i, last + 3, true));
                i = last + 3;
                continue;
            }

            list.Add(new ColRef(qual, name, i, last, false));
            i = last;
        }

        return list;
    }
}

internal static class TokExtensions
{
    public static bool IsWordUp2(this Tok t, string a, string b) => t.IsWordUp(a) || t.IsWordUp(b);
}
