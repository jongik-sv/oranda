using Oranda.Language.Lexing;

namespace Oranda.Language.Tuning;

/// <summary>
/// 튜닝 규칙이 쓰는 토큰 보기(TSK-06-05). 주석을 뺀 토큰, 괄호 짝, 괄호 깊이를 한 번에 만든다.
/// 깊이는 괄호 밖 기준이다: <c>(</c>·<c>)</c> 자체는 바깥 깊이, 안쪽 토큰은 하나 더 깊다.
/// </summary>
internal sealed class TuningSql
{
    /// <summary>절 끝(같은 깊이의 이 낱말이 나오면 앞 절이 끝난다).</summary>
    private static readonly HashSet<string> ClauseEnds = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "GROUP", "ORDER", "HAVING", "CONNECT", "START", "UNION", "MINUS", "INTERSECT", "FETCH", "OFFSET", "FOR", "MODEL", "WINDOW",
    };

    /// <summary>FROM 절 안에서 표 이름 뒤에 와도 별칭이 아닌 낱말.</summary>
    private static readonly HashSet<string> NotAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "NATURAL", "PARTITION", "SUBPARTITION", "SAMPLE", "PIVOT", "UNPIVOT", "VERSIONS", "AS", "ON", "USING", "JOIN", "LEFT", "RIGHT", "FULL",
        "INNER", "OUTER", "CROSS", "APPLY", "LATERAL", "WHERE", "GROUP", "ORDER", "HAVING", "CONNECT", "START", "UNION", "MINUS", "INTERSECT",
        "FETCH", "OFFSET", "FOR", "MODEL", "WINDOW",
    };

    public TuningSql(string text)
    {
        Text = text ?? string.Empty;
        Tokens = SqlLexer.Tokenize(Text).Where(t => t.Kind != TokenKind.Comment).ToList();
        Partner = new int[Tokens.Count];
        Depth = new int[Tokens.Count];
        var stack = new Stack<int>();
        var balanced = true;
        for (var i = 0; i < Tokens.Count; i++)
        {
            Partner[i] = -1;
            if (IsPunct(i, '('))
            {
                Depth[i] = stack.Count;
                stack.Push(i);
            }
            else if (IsPunct(i, ')'))
            {
                if (stack.Count == 0)
                {
                    balanced = false;
                    Depth[i] = 0;
                    continue;
                }

                var open = stack.Pop();
                Partner[open] = i;
                Partner[i] = open;
                Depth[i] = stack.Count;
            }
            else
            {
                Depth[i] = stack.Count;
            }
        }

        Balanced = balanced && stack.Count == 0;
    }

    public string Text { get; }

    public List<Token> Tokens { get; }

    /// <summary>괄호 짝의 토큰 번호(괄호가 아니면 -1).</summary>
    public int[] Partner { get; }

    public int[] Depth { get; }

    /// <summary>괄호가 모두 짝이 맞는가. 아니면 고쳐 쓰기 규칙은 손대지 않는다.</summary>
    public bool Balanced { get; }

    public int Count => Tokens.Count;

    public string Word(int i) => i >= 0 && i < Count ? Text.Substring(Tokens[i].Start, Tokens[i].Length) : string.Empty;

    public TokenKind Kind(int i) => Tokens[i].Kind;

    /// <summary>낱말·기호가 <paramref name="word"/> 와 같다(대소문자 무시). 문자열·바인드·따옴표 이름은 아니다.</summary>
    public bool Is(int i, string word) =>
        i >= 0 && i < Count && Tokens[i].Kind is TokenKind.Keyword or TokenKind.Identifier or TokenKind.Operator or TokenKind.Punct
        && string.Equals(Word(i), word, StringComparison.OrdinalIgnoreCase);

    public bool IsPunct(int i, char c) => i >= 0 && i < Count && Tokens[i].Kind == TokenKind.Punct && Tokens[i].Length == 1 && Text[Tokens[i].Start] == c;

    public bool IsName(int i) => i >= 0 && i < Count && Tokens[i].Kind is TokenKind.Identifier or TokenKind.QuotedIdent;

    /// <summary>토큰 <paramref name="from"/>~<paramref name="to"/>(포함)의 원문.</summary>
    public string Slice(int from, int to) =>
        from > to || from < 0 || to >= Count ? string.Empty : Text[Tokens[from].Start..Tokens[to].End];

    /// <summary>토큰 <paramref name="i"/> 가 있는 줄(1 부터).</summary>
    public int LineOf(int i)
    {
        var line = 1;
        var end = i < Count ? Tokens[i].Start : Text.Length;
        for (var k = 0; k < end; k++)
        {
            if (Text[k] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    /// <summary><paramref name="from"/> 부터 <paramref name="limit"/>(제외) 앞까지 깊이 <paramref name="depth"/> 인 첫 <paramref name="word"/>. 없으면 -1.</summary>
    public int Find(int from, int limit, int depth, string word)
    {
        for (var i = Math.Max(0, from); i < Math.Min(limit, Count); i++)
        {
            if (Depth[i] == depth && Is(i, word))
            {
                return i;
            }

            if (Depth[i] < depth)
            {
                return -1;
            }
        }

        return -1;
    }

    /// <summary>이름 하나를 비교용으로: 따옴표 이름은 안쪽 그대로, 아니면 대문자.</summary>
    public static string Normalize(string name) =>
        name.Length >= 2 && name[0] == '"' && name[^1] == '"' ? name[1..^1] : name.ToUpperInvariant();

    /// <summary>
    /// 주 질의(맨 바깥) SELECT 의 토큰 번호. <c>WITH a AS (…), b AS (…) SELECT</c> 면 CTE 뒤의 SELECT. 질의가 아니거나 WITH FUNCTION 이면 -1.
    /// </summary>
    public int MainSelect()
    {
        var i = 0;
        while (i < Count && IsPunct(i, '('))
        {
            i++; // ( SELECT … ) 처럼 괄호로 감싼 질의
        }

        if (Is(i, "SELECT"))
        {
            return i;
        }

        if (!Is(i, "WITH"))
        {
            return -1;
        }

        if (Is(i + 1, "FUNCTION") || Is(i + 1, "PROCEDURE"))
        {
            return -1;
        }

        var k = i + 1;
        while (k < Count)
        {
            // 이름 [ (열, …) ] AS [ [NOT] MATERIALIZED ] ( 질의 ) [SEARCH …|CYCLE …]
            if (!IsName(k))
            {
                return -1;
            }

            k++;
            if (IsPunct(k, '('))
            {
                k = Partner[k] + 1;
                if (k <= 0)
                {
                    return -1;
                }
            }

            if (!Is(k, "AS"))
            {
                return -1;
            }

            k++;
            if (!IsPunct(k, '(') || Partner[k] < 0)
            {
                return -1;
            }

            k = Partner[k] + 1;
            while (k < Count && !IsPunct(k, ',') && !Is(k, "SELECT") && Depth[k] == Depth[i])
            {
                if (IsPunct(k, '('))
                {
                    k = Partner[k];
                }

                k++;
            }

            if (IsPunct(k, ','))
            {
                k++;
                continue;
            }

            return Is(k, "SELECT") ? k : -1;
        }

        return -1;
    }

    /// <summary>SELECT 블록 하나의 절 위치.</summary>
    public SelectBlock? Block(int select)
    {
        if (!Is(select, "SELECT"))
        {
            return null;
        }

        var d = Depth[select];
        var limit = Count;
        for (var k = select + 1; k < Count; k++)
        {
            if (Depth[k] < d)
            {
                limit = k;
                break;
            }
        }

        var from = Find(select + 1, limit, d, "FROM");
        if (from < 0)
        {
            return null;
        }

        var fromEnd = limit;
        for (var k = from + 1; k < limit; k++)
        {
            if (Depth[k] == d && (ClauseEnds.Contains(Word(k)) && Kind(k) is TokenKind.Keyword or TokenKind.Identifier))
            {
                fromEnd = k;
                break;
            }
        }

        var where = Is(fromEnd, "WHERE") && Depth[fromEnd] == d ? fromEnd : -1;
        var whereEnd = limit;
        if (where >= 0)
        {
            for (var k = where + 1; k < limit; k++)
            {
                if (Depth[k] == d && !Is(k, "WHERE") && ClauseEnds.Contains(Word(k)) && Kind(k) is TokenKind.Keyword or TokenKind.Identifier)
                {
                    whereEnd = k;
                    break;
                }
            }
        }

        bool Has(string w) => Enumerable.Range(select + 1, limit - select - 1).Any(k => Depth[k] == d && Is(k, w));
        return new SelectBlock(select, from, fromEnd, where, where >= 0 ? whereEnd : -1, limit, d)
        {
            HasSetOperator = Has("UNION") || Has("MINUS") || Has("INTERSECT"),
            HasGroupBy = Has("GROUP") || Has("HAVING"),
            HasOrderBy = Has("ORDER"),
            HasConnectBy = Has("CONNECT"),
            HasForUpdate = Enumerable.Range(select + 1, limit - select - 1).Any(k => Depth[k] == d && Is(k, "FOR") && Is(k + 1, "UPDATE")),
            HasDistinct = Is(select + 1, "DISTINCT") || Is(select + 1, "UNIQUE") || (Kind(select + 1) == TokenKind.Hint && (Is(select + 2, "DISTINCT") || Is(select + 2, "UNIQUE"))),
            HasFetch = Has("FETCH") || Has("OFFSET"),
        };
    }

    /// <summary>FROM 절(<paramref name="from"/> 다음 ~ <paramref name="end"/> 앞)의 표 참조. 인라인 뷰는 이름 없이 별칭만.</summary>
    public List<TableRefToken> TablesOf(int from, int end)
    {
        var list = new List<TableRefToken>();
        if (from < 0)
        {
            return list;
        }

        var d = Depth[from];
        var k = from + 1;
        var expectTable = true;
        while (k < end)
        {
            if (Depth[k] != d)
            {
                k++;
                continue;
            }

            if (IsPunct(k, ','))
            {
                expectTable = true;
                k++;
                continue;
            }

            if (Is(k, "JOIN") || Is(k, "APPLY"))
            {
                expectTable = true;
                k++;
                continue;
            }

            if (Is(k, "ON"))
            {
                // ON 조건은 다음 JOIN 계열 낱말·쉼표까지 건너뛴다.
                k++;
                while (k < end && !(Depth[k] == d && (IsPunct(k, ',') || Is(k, "JOIN") || Is(k, "LEFT") || Is(k, "RIGHT") || Is(k, "FULL")
                    || Is(k, "INNER") || Is(k, "CROSS") || Is(k, "NATURAL") || Is(k, "OUTER"))))
                {
                    k++;
                }

                continue;
            }

            if (Is(k, "USING") && IsPunct(k + 1, '('))
            {
                k = Partner[k + 1] + 1;
                continue;
            }

            if (!expectTable)
            {
                k++;
                continue;
            }

            if (Is(k, "LATERAL") || Is(k, "ONLY"))
            {
                k++;
                continue;
            }

            int first = k, last;
            string? owner = null, name = null;
            if (IsPunct(k, '('))
            {
                last = Partner[k] < 0 ? end - 1 : Partner[k];
                k = last + 1;
            }
            else if (IsName(k))
            {
                name = Word(k);
                last = k;
                k++;
                if (IsPunct(k, '.') && IsName(k + 1))
                {
                    owner = name;
                    name = Word(k + 1);
                    last = k + 1;
                    k += 2;
                }

                if (Is(k, "@") && IsName(k + 1))
                {
                    last = k + 1;
                    k += 2; // db 링크
                }
            }
            else
            {
                k++;
                continue;
            }

            // PARTITION (p) · SAMPLE (n) 같은 꼬리
            while (k < end && (Is(k, "PARTITION") || Is(k, "SUBPARTITION") || Is(k, "SAMPLE")) && IsPunct(k + 1, '('))
            {
                k = Partner[k + 1] + 1;
            }

            if (Is(k, "AS"))
            {
                k++;
            }

            string? alias = null;
            var aliasIndex = -1;
            if (k < end && IsName(k) && !NotAliases.Contains(Word(k)))
            {
                alias = Word(k);
                aliasIndex = k;
                last = k;
                k++;
            }

            list.Add(new TableRefToken(owner, name, alias, first, last, aliasIndex));
            expectTable = false;
        }

        return list;
    }

    /// <summary>문장 전체의 모든 FROM 절 표 참조(하위 질의 포함). EXTRACT(… FROM …)·TRIM(… FROM …) 은 SELECT 가 앞에 없어 뺀다.</summary>
    public List<TableRefToken> AllTables()
    {
        var all = new List<TableRefToken>();
        for (var i = 0; i < Count; i++)
        {
            if (!Is(i, "SELECT"))
            {
                continue;
            }

            if (Block(i) is { } b)
            {
                all.AddRange(TablesOf(b.From, b.FromEnd));
            }
        }

        return all;
    }

    /// <summary><paramref name="i"/> 를 품은 가장 가까운 SELECT 블록. 없으면 null.</summary>
    public SelectBlock? Enclosing(int i)
    {
        var d = Depth[i];
        for (var k = i - 1; k >= 0; k--)
        {
            if (Depth[k] < d)
            {
                d = Depth[k];
                continue;
            }

            if (Depth[k] == d && Is(k, "SELECT") && Block(k) is { } b && b.Limit > i)
            {
                return b;
            }
        }

        return null;
    }

    /// <summary>
    /// 조건을 같은 깊이의 <paramref name="separator"/>(AND·OR)로 나눈다. BETWEEN … AND … 의 AND 는 나누지 않는다.
    /// 결과는 (첫 토큰, 끝 토큰) 쌍(포함).
    /// </summary>
    public List<(int From, int To)> SplitAt(int from, int to, string separator)
    {
        var parts = new List<(int, int)>();
        if (from > to)
        {
            return parts;
        }

        var d = Depth[from];
        var start = from;
        var pendingBetween = 0;
        for (var k = from; k <= to; k++)
        {
            if (Depth[k] != d)
            {
                continue;
            }

            if (Is(k, "BETWEEN"))
            {
                pendingBetween++;
            }
            else if (Is(k, separator))
            {
                if (separator.Equals("AND", StringComparison.OrdinalIgnoreCase) && pendingBetween > 0)
                {
                    pendingBetween--;
                    continue;
                }

                parts.Add((start, k - 1));
                start = k + 1;
            }
        }

        parts.Add((start, to));
        return parts;
    }

    /// <summary>바깥을 감싼 괄호를 벗긴다(<c>((a = 1))</c> → <c>a = 1</c>).</summary>
    public (int From, int To) Strip(int from, int to)
    {
        while (from < to && IsPunct(from, '(') && Partner[from] == to)
        {
            from++;
            to--;
        }

        return (from, to);
    }

    /// <summary>열 참조 <c>a.b</c>·<c>b</c>·<c>"A"."B"</c> 의 끝 토큰 번호(<paramref name="from"/> 이 이름이 아니면 -1).</summary>
    public int ColumnRefEnd(int from)
    {
        if (!IsName(from))
        {
            return -1;
        }

        if (IsPunct(from + 1, '.') && IsName(from + 2))
        {
            return IsPunct(from + 3, '.') && IsName(from + 4) ? from + 4 : from + 2;
        }

        return from;
    }
}

/// <summary>SELECT 블록의 절 위치(토큰 번호). 없는 절은 -1. <see cref="Limit"/> = 블록이 끝난 다음 토큰.</summary>
internal sealed record SelectBlock(int Select, int From, int FromEnd, int Where, int WhereEnd, int Limit, int Depth)
{
    public bool HasSetOperator { get; init; }

    public bool HasGroupBy { get; init; }

    public bool HasOrderBy { get; init; }

    public bool HasConnectBy { get; init; }

    public bool HasForUpdate { get; init; }

    public bool HasDistinct { get; init; }

    public bool HasFetch { get; init; }
}

/// <summary>FROM 절의 표 참조 하나. 인라인 뷰면 <see cref="Name"/> 이 null.</summary>
internal sealed record TableRefToken(string? Owner, string? Name, string? Alias, int First, int Last, int AliasIndex)
{
    /// <summary>열을 한정할 이름(별칭, 없으면 표 이름).</summary>
    public string? Qualifier => Alias ?? Name;
}
