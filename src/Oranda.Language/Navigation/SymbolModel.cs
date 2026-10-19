using Oranda.Language.Lexing;

namespace Oranda.Language.Navigation;

/// <summary>문서 안에서 정의되는 이름의 종류.</summary>
public enum LocalSymbolKind
{
    /// <summary><c>FROM emp e</c> 의 <c>e</c>(테이블 별칭).</summary>
    TableAlias,

    /// <summary><c>FROM (select …) x</c> 의 <c>x</c>(파생 테이블 별칭).</summary>
    DerivedAlias,

    /// <summary><c>WITH x AS (…)</c> 의 <c>x</c>.</summary>
    Cte,

    Variable,
    Cursor,

    /// <summary>문서 안 로컬 프로시저·함수(또는 이 문서가 만드는 프로시저·함수).</summary>
    Subprogram,

    Parameter,
    Type,
    LoopVariable,
}

/// <summary>문서 안 정의 하나. <see cref="Start"/>·<see cref="Length"/> 는 정의 이름 토큰의 범위.</summary>
public sealed record LocalDefinition(int Start, int Length, string Name, LocalSymbolKind Kind)
{
    /// <summary>테이블 별칭이 가리키는 DB 객체 소유자(없으면 null).</summary>
    public string? TargetOwner { get; init; }

    /// <summary>테이블 별칭이 가리키는 DB 객체 이름(별칭이 DB 객체를 가리킬 때만).</summary>
    public string? TargetName { get; init; }

    public int End => Start + Length;
}

/// <summary>
/// L1 토큰만으로 만든 문서 한 장의 기호 모델: 식별자 사슬(<c>a.b.c</c>), 정의(별칭·CTE·변수·커서·로컬 서브프로그램·매개변수),
/// 테이블 자리 판정. L2(ANTLR) 분석이 생기면 이 모델을 만드는 자리(<see cref="Build"/>)만 바꾸면 된다.
/// </summary>
internal sealed class SymbolModel
{
    private static readonly HashSet<string> AliasStop = new(StringComparer.OrdinalIgnoreCase)
    {
        "NATURAL", "CONNECT", "START", "OFFSET", "PARTITION", "SAMPLE", "PIVOT", "UNPIVOT", "MODEL", "WINDOW", "LOG", "ERRORS",
        "VERSIONS", "USING", "ON", "SET", "WHERE", "GROUP", "ORDER", "HAVING", "UNION", "MINUS", "INTERSECT", "FETCH", "FOR",
        "LEFT", "RIGHT", "FULL", "INNER", "CROSS", "OUTER", "JOIN", "LATERAL", "RETURNING", "VALUES", "SELECT", "WHEN", "THEN",
    };

    public required string Text { get; init; }

    public required List<Tok> Toks { get; init; }

    public required List<Chain> Chains { get; init; }

    public required List<Def> Defs { get; init; }

    /// <summary>토큰 번호 → 문장(<c>;</c> 로 나눈) 번호.</summary>
    public required int[] Segment { get; init; }

    public readonly record struct Tok(int Start, int Length, TokenKind Kind)
    {
        public int End => Start + Length;
    }

    /// <summary><c>a.b.c</c> 한 덩어리. <see cref="Parts"/> 는 토큰 번호.</summary>
    public sealed class Chain
    {
        public required List<int> Parts { get; init; }

        public bool IsTableRef { get; set; }

        public int Start { get; init; }

        public int End { get; init; }
    }

    public sealed class Def
    {
        public required int Token { get; init; }

        public required string Name { get; init; }

        public required LocalSymbolKind Kind { get; init; }

        public Chain? Target { get; init; }

        public int Segment { get; init; }

        public bool IsSqlScoped => Kind is LocalSymbolKind.TableAlias or LocalSymbolKind.DerivedAlias or LocalSymbolKind.Cte;
    }

    public string Word(int token) => Text.Substring(Toks[token].Start, Toks[token].Length);

    public string Norm(int token) => NormalizeIdent(Word(token));

    public static string NormalizeIdent(string word)
        => word.Length >= 2 && word[0] == '"' && word[^1] == '"' ? word[1..^1] : word.ToUpperInvariant();

    public bool IsWord(int token, string upper)
        => Toks[token].Kind is TokenKind.Keyword or TokenKind.Identifier
           && Toks[token].Length == upper.Length
           && string.Compare(Text, Toks[token].Start, upper, 0, upper.Length, StringComparison.OrdinalIgnoreCase) == 0;

    public bool IsPunct(int token, char c)
        => token >= 0 && token < Toks.Count && Toks[token].Kind is TokenKind.Punct or TokenKind.Operator && Toks[token].Length == 1 && Text[Toks[token].Start] == c;

    public bool IsIdentLike(int token) => token >= 0 && token < Toks.Count && Toks[token].Kind is TokenKind.Identifier or TokenKind.QuotedIdent;

    public static SymbolModel Build(string text)
    {
        var toks = new List<Tok>();
        foreach (var t in SqlLexer.Tokenize(text))
        {
            if (t.Kind is TokenKind.Comment or TokenKind.Hint)
            {
                continue;
            }

            // 줄에 걸친 따옴표 식별자 조각은 합치지 않는다(드문 경우).
            toks.Add(new Tok(t.Start, t.Length, t.Kind));
        }

        var seg = new int[toks.Count];
        int s = 0;
        for (int i = 0; i < toks.Count; i++)
        {
            seg[i] = s;
            if (toks[i].Kind == TokenKind.Punct && text[toks[i].Start] == ';')
            {
                s++;
            }
        }

        var m = new SymbolModel { Text = text, Toks = toks, Chains = [], Defs = [], Segment = seg };
        m.FindChains();
        m.FindTableRefsAndAliases();
        m.FindCtes();
        m.FindProceduralDefs();
        return m;
    }

    // ---- 사슬 -------------------------------------------------------------------------

    private bool IsPartCandidate(int i, bool allowKeyword)
        => i < Toks.Count && (Toks[i].Kind is TokenKind.Identifier or TokenKind.QuotedIdent || (allowKeyword && Toks[i].Kind == TokenKind.Keyword));

    private bool IsAdjacentDot(int i) => i < Toks.Count && Toks[i].Kind == TokenKind.Punct && Text[Toks[i].Start] == '.';

    private void FindChains()
    {
        int i = 0;
        while (i < Toks.Count)
        {
            bool dotNext = i + 1 < Toks.Count && IsAdjacentDot(i + 1) && Toks[i + 1].Start == Toks[i].End;
            bool dotPrev = i > 0 && IsAdjacentDot(i - 1) && Toks[i - 1].End == Toks[i].Start;
            if (!IsPartCandidate(i, dotNext || dotPrev))
            {
                i++;
                continue;
            }

            var parts = new List<int> { i };
            int j = i;
            while (j + 2 < Toks.Count && IsAdjacentDot(j + 1) && Toks[j + 1].Start == Toks[j].End
                   && Toks[j + 2].Start == Toks[j + 1].End && IsPartCandidate(j + 2, true))
            {
                parts.Add(j + 2);
                j += 2;
            }

            Chains.Add(new Chain { Parts = parts, Start = Toks[i].Start, End = Toks[j].End });
            i = j + 1;
        }
    }

    public Chain? ChainStartingAt(int token) => Chains.Find(c => c.Parts[0] == token);

    private Chain? ChainOfToken(int token) => Chains.Find(c => c.Parts.Contains(token));

    // ---- 테이블 자리와 별칭 ------------------------------------------------------------------

    private int MatchClose(int open)
    {
        int depth = 0;
        for (int k = open; k < Toks.Count; k++)
        {
            if (IsPunct(k, '('))
            {
                depth++;
            }
            else if (IsPunct(k, ')') && --depth == 0)
            {
                return k;
            }
        }

        return Toks.Count - 1;
    }

    private void FindTableRefsAndAliases()
    {
        // 괄호가 EXTRACT(… FROM x)·TRIM(… FROM x) 안에 있는지 보려고 여는 괄호 앞 낱말을 쌓는다.
        var parenOwner = new Stack<string?>();
        string? firstWord = null;
        int curSeg = -1;
        for (int i = 0; i < Toks.Count; i++)
        {
            if (Segment[i] != curSeg)
            {
                curSeg = Segment[i];
                firstWord = null;
            }

            if (firstWord is null && Toks[i].Kind == TokenKind.Keyword)
            {
                firstWord = Word(i).ToUpperInvariant();
            }

            if (IsPunct(i, '('))
            {
                parenOwner.Push(i > 0 && IsIdentLike(i - 1) ? Word(i - 1).ToUpperInvariant() : null);
                continue;
            }

            if (IsPunct(i, ')'))
            {
                if (parenOwner.Count > 0)
                {
                    parenOwner.Pop();
                }

                continue;
            }

            if (Toks[i].Kind != TokenKind.Keyword)
            {
                continue;
            }

            bool from = IsWord(i, "FROM");
            bool tableKw = from || IsWord(i, "JOIN") || IsWord(i, "UPDATE") || IsWord(i, "USING")
                || (IsWord(i, "INTO") && firstWord is "INSERT" or "MERGE");
            if (!tableKw)
            {
                continue;
            }

            if (from && parenOwner.Count > 0 && parenOwner.Peek() is "EXTRACT" or "TRIM")
            {
                continue;
            }

            // FOR UPDATE 는 테이블 자리가 아니다.
            if (IsWord(i, "UPDATE") && i > 0 && IsWord(i - 1, "FOR"))
            {
                continue;
            }

            ParseTableList(i + 1, from, curSeg);
        }
    }

    private void ParseTableList(int j, bool commaList, int seg)
    {
        while (j < Toks.Count)
        {
            Chain? target = null;
            bool derived = false;
            if (IsPunct(j, '('))
            {
                j = MatchClose(j) + 1;
                derived = true;
            }
            else if (j < Toks.Count && IsIdentLike(j))
            {
                target = ChainStartingAt(j);
                if (target is null)
                {
                    return;
                }

                target.IsTableRef = true;
                j = target.Parts[^1] + 1;
                // 데이터베이스 링크 @name 건너뛰기
                if (IsPunct(j, '@') && IsIdentLike(j + 1))
                {
                    j += 2;
                }
            }
            else
            {
                return;
            }

            if (j < Toks.Count && IsWord(j, "AS"))
            {
                j++;
            }

            if (j < Toks.Count && IsIdentLike(j) && !(Toks[j].Kind == TokenKind.Identifier && AliasStop.Contains(Word(j))))
            {
                Defs.Add(new Def
                {
                    Token = j,
                    Name = Norm(j),
                    Kind = derived ? LocalSymbolKind.DerivedAlias : LocalSymbolKind.TableAlias,
                    Target = derived ? null : target,
                    Segment = seg,
                });
                j++;
            }

            if (commaList && IsPunct(j, ','))
            {
                j++;
                continue;
            }

            return;
        }
    }

    // ---- WITH (CTE) --------------------------------------------------------------------

    private void FindCtes()
    {
        for (int i = 0; i + 2 < Toks.Count; i++)
        {
            if (!IsWord(i, "WITH"))
            {
                continue;
            }

            int j = i + 1;
            while (j < Toks.Count && IsIdentLike(j))
            {
                int name = j;
                int k = j + 1;
                if (IsPunct(k, '('))
                {
                    k = MatchClose(k) + 1;
                }

                if (!(k < Toks.Count && IsWord(k, "AS") && IsPunct(k + 1, '(')))
                {
                    break;
                }

                Defs.Add(new Def { Token = name, Name = Norm(name), Kind = LocalSymbolKind.Cte, Segment = Segment[name] });
                j = MatchClose(k + 1) + 1;
                if (IsPunct(j, ','))
                {
                    j++;
                    continue;
                }

                break;
            }
        }
    }

    // ---- 변수·커서·서브프로그램·매개변수 ---------------------------------------------------------

    private sealed class Frame
    {
        public bool Decl;

        public int CaseDepth;
    }

    private void AddDef(int token, LocalSymbolKind kind)
        => Defs.Add(new Def { Token = token, Name = Norm(token), Kind = kind, Segment = Segment[token] });

    private void FindProceduralDefs()
    {
        var frames = new Stack<Frame>();
        bool pendingSub = false;
        bool stmtStart = false;
        int paren = 0;
        for (int i = 0; i < Toks.Count; i++)
        {
            var k = Toks[i].Kind;
            if (k == TokenKind.Punct)
            {
                char c = Text[Toks[i].Start];
                if (c == '(')
                {
                    paren++;
                }
                else if (c == ')')
                {
                    paren = Math.Max(0, paren - 1);
                }
                else if (c == ';')
                {
                    stmtStart = true;
                    if (paren == 0)
                    {
                        pendingSub = false;
                    }

                    continue;
                }

                stmtStart = false;

                continue;
            }

            if (k is TokenKind.Identifier or TokenKind.QuotedIdent)
            {
                if (stmtStart && paren == 0 && frames.Count > 0 && frames.Peek().Decl && i + 1 < Toks.Count
                    && Toks[i + 1].Kind is TokenKind.Identifier or TokenKind.Keyword or TokenKind.QuotedIdent
                    && !(IsAdjacentDot(i + 1)))
                {
                    AddDef(i, LocalSymbolKind.Variable);
                }

                stmtStart = false;
                continue;
            }

            if (k != TokenKind.Keyword)
            {
                stmtStart = false;
                continue;
            }

            stmtStart = false;
            bool prevPercent = i > 0 && IsPunct(i - 1, '%');
            if (IsWord(i, "DECLARE"))
            {
                frames.Push(new Frame { Decl = true });
                stmtStart = true;
            }
            else if (IsWord(i, "CURSOR") && IsIdentLike(i + 1))
            {
                AddDef(i + 1, LocalSymbolKind.Cursor);
                if (IsPunct(i + 2, '('))
                {
                    AddParameters(i + 2);
                }
            }
            else if ((IsWord(i, "PROCEDURE") || IsWord(i, "FUNCTION")) && IsIdentLike(i + 1))
            {
                AddDef(i + 1, LocalSymbolKind.Subprogram);
                pendingSub = true;
                if (IsPunct(i + 2, '('))
                {
                    AddParameters(i + 2);
                }
            }
            else if (IsWord(i, "PACKAGE"))
            {
                pendingSub = true;
            }
            else if ((IsWord(i, "TYPE") && !prevPercent || IsWord(i, "SUBTYPE")) && IsIdentLike(i + 1) && i + 2 < Toks.Count && IsWord(i + 2, "IS"))
            {
                AddDef(i + 1, LocalSymbolKind.Type);
            }
            else if ((IsWord(i, "FOR") || IsWord(i, "FORALL")) && IsIdentLike(i + 1) && i + 2 < Toks.Count && IsWord(i + 2, "IN"))
            {
                AddDef(i + 1, LocalSymbolKind.LoopVariable);
            }
            else if ((IsWord(i, "IS") || IsWord(i, "AS")) && pendingSub && paren == 0)
            {
                pendingSub = false;
                frames.Push(new Frame { Decl = true });
                stmtStart = true;
            }
            else if (IsWord(i, "BEGIN"))
            {
                if (frames.Count > 0 && frames.Peek().Decl)
                {
                    frames.Peek().Decl = false;
                }
                else
                {
                    frames.Push(new Frame());
                }
            }
            else if (IsWord(i, "CASE") && !(i > 0 && IsWord(i - 1, "END")))
            {
                if (frames.Count > 0)
                {
                    frames.Peek().CaseDepth++;
                }
            }
            else if (IsWord(i, "END"))
            {
                int nx = i + 1;
                if (nx < Toks.Count && (IsWord(nx, "IF") || IsWord(nx, "LOOP")))
                {
                    continue;
                }

                if (nx < Toks.Count && IsWord(nx, "CASE"))
                {
                    if (frames.Count > 0 && frames.Peek().CaseDepth > 0)
                    {
                        frames.Peek().CaseDepth--;
                    }

                    continue;
                }

                if (frames.Count > 0 && frames.Peek().CaseDepth > 0)
                {
                    frames.Peek().CaseDepth--;
                }
                else if (frames.Count > 0)
                {
                    frames.Pop();
                }
            }
        }
    }

    private void AddParameters(int open)
    {
        int close = MatchClose(open);
        int depth = 0;
        bool first = true;
        for (int i = open + 1; i < close; i++)
        {
            if (IsPunct(i, '('))
            {
                depth++;
            }
            else if (IsPunct(i, ')'))
            {
                depth--;
            }
            else if (depth == 0 && IsPunct(i, ','))
            {
                first = true;
                continue;
            }

            if (first && depth == 0 && IsIdentLike(i))
            {
                AddDef(i, LocalSymbolKind.Parameter);
            }

            first = false;
        }
    }

    // ---- 해석 ---------------------------------------------------------------------------

    /// <summary>토큰이 정의 이름 토큰이면 그 정의.</summary>
    public Def? DefAtToken(int token) => Defs.Find(d => d.Token == token);

    /// <summary>사슬 첫 부분이 가리키는 문서 안 정의(없으면 null). 정의 토큰 자신은 자기 자신을 돌려준다.</summary>
    public Def? ResolveLocal(Chain chain)
    {
        int tok = chain.Parts[0];
        var self = DefAtToken(tok);
        if (self is not null)
        {
            return self;
        }

        if (tok > 0 && IsAdjacentDot(tok - 1))
        {
            return null;
        }

        var name = Norm(tok);
        int seg = Segment[tok];
        Def? best = null;
        foreach (var d in Defs)
        {
            if (d.Name != name || !d.IsSqlScoped || d.Segment != seg)
            {
                continue;
            }

            if (chain.IsTableRef && d.Kind != LocalSymbolKind.Cte)
            {
                continue;
            }

            if (best is null || (d.Token <= tok && (best.Token > tok || d.Token > best.Token)))
            {
                best = d;
            }
        }

        if (best is not null || chain.IsTableRef)
        {
            return best;
        }

        foreach (var d in Defs)
        {
            if (d.Name == name && !d.IsSqlScoped && d.Token < tok && (best is null || d.Token > best.Token))
            {
                best = d;
            }
        }

        return best;
    }

    /// <summary>offset 이 속한 사슬과 그 안 부분 번호.</summary>
    public (Chain Chain, int Part)? Locate(int offset)
    {
        foreach (var c in Chains)
        {
            if (offset < c.Start || offset > c.End)
            {
                continue;
            }

            // 점 위·바로 뒤에서는 앞 부분을 고른다.
            for (int p = 0; p < c.Parts.Count; p++)
            {
                var t = Toks[c.Parts[p]];
                if (offset >= t.Start && offset <= t.End)
                {
                    return (c, p);
                }
            }
        }

        return null;
    }
}
