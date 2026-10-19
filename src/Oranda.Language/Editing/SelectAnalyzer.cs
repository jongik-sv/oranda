using System.Collections.Frozen;
using Oranda.Core.Grid;
using Oranda.Language.Lexing;

namespace Oranda.Language.Editing;

/// <summary>FROM 절 테이블 1개(또는 인라인 뷰). <see cref="Text"/> 는 원문 그대로의 이름(ROWID 한정자로 쓴다).</summary>
/// <param name="Owner">스키마(적지 않았으면 null). 따옴표 없는 이름은 대문자</param>
/// <param name="Name">테이블 이름. 인라인 뷰면 null</param>
/// <param name="Alias">별칭(없으면 null)</param>
/// <param name="Text">원문 이름(<c>scott.emp</c>). 인라인 뷰면 null</param>
public sealed record SelectTableRef(string? Owner, string? Name, string? Alias, string? Text)
{
    public bool IsInlineView => Name is null;

    /// <summary>DB 링크(<c>@link</c>)가 붙은 원격 테이블.</summary>
    public bool IsRemote { get; init; }

    /// <summary>별칭 원문(대소문자·따옴표 그대로).</summary>
    public string? AliasText { get; init; }

    /// <summary>열 한정자로 쓸 원문: 별칭, 없으면 원문 이름.</summary>
    public string? Qualifier => AliasText ?? Text;
}

/// <summary>SELECT 목록 항목 1개.</summary>
/// <param name="Qualifier">열 한정자(<c>e.ename</c> 의 <c>E</c>). 없으면 null</param>
/// <param name="Column">단순 열 참조면 열 이름, 식이면 null</param>
/// <param name="IsStar"><c>*</c> 또는 <c>q.*</c></param>
public sealed record SelectItem(string? Qualifier, string? Column, bool IsStar)
{
    public bool IsRowId => string.Equals(Column, "ROWID", StringComparison.Ordinal);
}

/// <summary>그리드 편집용 SELECT 분석 결과(TRD 4.4 「편집」 1단계).</summary>
public sealed record SelectShape
{
    /// <summary>편집을 막는 구조(집계·DISTINCT·GROUP BY·집합 연산·분석 불가). 없으면 <see cref="EditBlockReason.None"/>.</summary>
    public EditBlockReason Block { get; init; }

    public string? BlockMessage { get; init; }

    public IReadOnlyList<SelectTableRef> Tables { get; init; } = [];

    public IReadOnlyList<SelectItem> Items { get; init; } = [];

    /// <summary>SELECT 목록 마지막 토큰 끝 위치(본문 안). ROWID 를 덧붙일 자리. 모르면 -1.</summary>
    public int SelectListEnd { get; init; } = -1;

    /// <summary>한정자 없는 <c>*</c> 항목의 위치(다른 항목과 섞으려면 <c>q.*</c> 로 바꿔야 함). 없으면 -1.</summary>
    public int BareStarOffset { get; init; } = -1;

    public bool IsJoin => Tables.Count > 1;
}

/// <summary>
/// 렉서 토큰으로 SELECT 한 개의 편집 관련 구조를 읽는다(L1 수준, 문법 오류에 관대함).
/// 바깥 깊이(괄호 0단)만 본다. 하위 질의 안의 집계·DISTINCT 는 바깥 결과에 영향이 없으므로 무시한다.
/// </summary>
public static class SelectAnalyzer
{
    private static readonly FrozenSet<string> Aggregates = new[]
    {
        "COUNT", "SUM", "AVG", "MIN", "MAX", "LISTAGG", "STDDEV", "STDDEV_POP", "STDDEV_SAMP", "VARIANCE", "VAR_POP",
        "VAR_SAMP", "MEDIAN", "COLLECT", "XMLAGG", "JSON_ARRAYAGG", "JSON_OBJECTAGG", "APPROX_COUNT_DISTINCT",
        "APPROX_COUNT", "APPROX_SUM", "APPROX_MEDIAN", "APPROX_PERCENTILE", "CORR", "COVAR_POP", "COVAR_SAMP",
        "PERCENTILE_CONT", "PERCENTILE_DISC", "STATS_MODE", "GROUPING", "GROUPING_ID", "BIT_AND_AGG", "BIT_OR_AGG",
        "BIT_XOR_AGG", "ANY_VALUE", "CHECKSUM", "KURTOSIS_POP", "KURTOSIS_SAMP", "SKEWNESS_POP", "SKEWNESS_SAMP",
        "REGR_SLOPE", "REGR_INTERCEPT", "REGR_COUNT", "REGR_R2", "REGR_AVGX", "REGR_AVGY", "REGR_SXX", "REGR_SYY", "REGR_SXY",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>FROM 절을 끝내는 바깥 깊이 낱말.</summary>
    private static readonly FrozenSet<string> FromEnd = new[]
    {
        "WHERE", "GROUP", "HAVING", "ORDER", "CONNECT", "START", "UNION", "INTERSECT", "MINUS", "EXCEPT", "FETCH",
        "OFFSET", "FOR", "MODEL", "WINDOW", "QUALIFY",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> JoinWords = new[]
    {
        "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "NATURAL", "APPLY",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>테이블 이름 뒤에 와도 별칭이 아닌 낱말.</summary>
    private static readonly FrozenSet<string> NotAlias = new[]
    {
        "ON", "USING", "PARTITION", "SUBPARTITION", "SAMPLE", "AS", "PIVOT", "UNPIVOT", "MATCH_RECOGNIZE", "VERSIONS",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>열처럼 보이지만 테이블 열이 아닌 의사 열·함수.</summary>
    private static readonly FrozenSet<string> PseudoColumns = new[]
    {
        "ROWNUM", "LEVEL", "SYSDATE", "SYSTIMESTAMP", "USER", "UID", "CURRENT_DATE", "CURRENT_TIMESTAMP", "LOCALTIMESTAMP",
        "DBTIMEZONE", "SESSIONTIMEZONE", "CONNECT_BY_ISLEAF", "CONNECT_BY_ISCYCLE", "ORA_ROWSCN", "NULL", "ROWID",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>SELECT 본문을 분석한다.</summary>
    public static SelectShape Analyze(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var toks = new List<Token>();
        foreach (var t in SqlLexer.Tokenize(text))
        {
            if (t.Kind is not (TokenKind.Comment or TokenKind.Hint))
            {
                toks.Add(t);
            }
        }

        if (toks.Count == 0)
        {
            return Blocked(EditBlockReason.NotAnalyzable, "빈 문장입니다.");
        }

        var first = Word(text, toks[0]);
        if (first != "SELECT")
        {
            return Blocked(EditBlockReason.NotAnalyzable,
                first == "WITH" ? "WITH 절 질의는 편집할 수 없습니다." : "SELECT 문이 아니어서 편집할 수 없습니다.");
        }

        // 1. 바깥 깊이 구조 훑기
        int depth = 0, fromIndex = -1;
        for (var i = 1; i < toks.Count; i++)
        {
            var t = toks[i];
            if (IsPunct(text, t, '('))
            {
                depth++;
                continue;
            }

            if (IsPunct(text, t, ')'))
            {
                depth--;
                continue;
            }

            if (depth != 0)
            {
                continue;
            }

            var w = Word(text, t);
            switch (w)
            {
                case "UNION" or "INTERSECT" or "MINUS" or "EXCEPT":
                    return Blocked(EditBlockReason.SetOperation, "집합 연산(UNION·INTERSECT·MINUS) 결과는 편집할 수 없습니다.");
                case "GROUP" when i + 1 < toks.Count && Word(text, toks[i + 1]) == "BY":
                case "HAVING":
                    return Blocked(EditBlockReason.GroupBy, "GROUP BY 결과는 편집할 수 없습니다.");
                case "MODEL" or "PIVOT" or "UNPIVOT" or "MATCH_RECOGNIZE":
                    return Blocked(EditBlockReason.NotAnalyzable, $"{w} 절이 있는 결과는 편집할 수 없습니다.");
                case "FROM" when fromIndex < 0:
                    fromIndex = i;
                    break;
            }
        }

        var listStart = 1;
        if (listStart < toks.Count && Word(text, toks[listStart]) is "DISTINCT" or "UNIQUE")
        {
            return Blocked(EditBlockReason.Distinct, "DISTINCT 결과는 편집할 수 없습니다.");
        }

        if (listStart < toks.Count && Word(text, toks[listStart]) == "ALL")
        {
            listStart++;
        }

        if (fromIndex < 0 || fromIndex <= listStart)
        {
            return Blocked(EditBlockReason.NotAnalyzable, "FROM 절을 찾지 못했습니다.");
        }

        // 2. SELECT 목록
        var items = new List<SelectItem>();
        var bareStar = -1;
        foreach (var (a, b) in SplitTopLevel(text, toks, listStart, fromIndex))
        {
            if (b - a == 1 && IsPunct(text, toks[a], '*'))
            {
                bareStar = toks[a].Start;
            }

            if (HasAggregate(text, toks, a, b))
            {
                return Blocked(EditBlockReason.Aggregate, "집계 함수 결과는 편집할 수 없습니다.");
            }

            items.Add(ReadItem(text, toks, a, b));
        }

        // 3. FROM 절
        var fromEnd = toks.Count;
        depth = 0;
        for (var i = fromIndex + 1; i < toks.Count; i++)
        {
            if (IsPunct(text, toks[i], '('))
            {
                depth++;
            }
            else if (IsPunct(text, toks[i], ')'))
            {
                depth--;
            }
            else if (depth == 0 && FromEnd.Contains(Word(text, toks[i]) ?? string.Empty))
            {
                fromEnd = i;
                break;
            }
        }

        var tables = ReadTables(text, toks, fromIndex + 1, fromEnd);
        if (tables.Count == 0)
        {
            return Blocked(EditBlockReason.NotAnalyzable, "FROM 절의 테이블을 읽지 못했습니다.");
        }

        return new SelectShape
        {
            Tables = tables,
            Items = items,
            SelectListEnd = toks[fromIndex - 1].End,
            BareStarOffset = bareStar,
        };
    }

    /// <summary>결과 끝에 <c>q.ROWID</c> 를 덧붙인 본문. 단일 테이블(원격·인라인 뷰 아님)일 때만, 아니면 null.</summary>
    public static string? AppendRowId(string text, SelectShape shape)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(shape);
        if (shape.Block != EditBlockReason.None || shape.Tables.Count != 1 || shape.SelectListEnd < 0)
        {
            return null;
        }

        var t = shape.Tables[0];
        if (t.IsInlineView || t.IsRemote)
        {
            return null;
        }

        // 오라클은 한정자 없는 * 를 다른 항목과 섞지 못한다: "SELECT *" → "SELECT q.*, q.ROWID".
        var head = text[..shape.SelectListEnd];
        if (shape.BareStarOffset >= 0)
        {
            head = head[..shape.BareStarOffset] + t.Qualifier + "." + head[shape.BareStarOffset..];
        }

        return head + ", " + t.Qualifier + ".ROWID" + text[shape.SelectListEnd..];
    }

    /// <summary>식별자 정규화: 따옴표 이름은 그대로(따옴표 제거), 아니면 대문자.</summary>
    public static string NormalizeIdentifier(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"'
            ? raw[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
            : raw.ToUpperInvariant();
    }

    private static SelectShape Blocked(EditBlockReason reason, string message) => new() { Block = reason, BlockMessage = message };

    private static string? Word(string text, Token t) => t.Kind switch
    {
        TokenKind.Identifier or TokenKind.Keyword => text.Substring(t.Start, t.Length).ToUpperInvariant(),
        _ => null,
    };

    /// <summary>이름 토큰(식별자·키워드·따옴표 식별자)의 정규화 이름.</summary>
    private static string? Name(string text, Token t) => t.Kind switch
    {
        TokenKind.Identifier or TokenKind.Keyword or TokenKind.QuotedIdent => NormalizeIdentifier(text.Substring(t.Start, t.Length)),
        _ => null,
    };

    private static bool IsPunct(string text, Token t, char c) =>
        (t.Kind is TokenKind.Punct or TokenKind.Operator) && t.Length == 1 && text[t.Start] == c;

    private static IEnumerable<(int Start, int End)> SplitTopLevel(string text, List<Token> toks, int start, int end)
    {
        int depth = 0, s = start;
        for (var i = start; i < end; i++)
        {
            if (IsPunct(text, toks[i], '('))
            {
                depth++;
            }
            else if (IsPunct(text, toks[i], ')'))
            {
                depth--;
            }
            else if (depth == 0 && IsPunct(text, toks[i], ','))
            {
                if (i > s)
                {
                    yield return (s, i);
                }

                s = i + 1;
            }
        }

        if (end > s)
        {
            yield return (s, end);
        }
    }

    /// <summary>항목 안에 바깥 질의의 집계 함수가 있나(분석 함수 OVER·하위 질의 안은 제외).</summary>
    private static bool HasAggregate(string text, List<Token> toks, int a, int b)
    {
        // 괄호마다 하위 질의 여부를 쌓는다.
        var stack = new Stack<bool>();
        var inSubquery = 0;
        for (var i = a; i < b; i++)
        {
            var t = toks[i];
            if (IsPunct(text, t, '('))
            {
                var sub = i + 1 < b && Word(text, toks[i + 1]) is "SELECT" or "WITH";
                stack.Push(sub);
                if (sub)
                {
                    inSubquery++;
                }

                continue;
            }

            if (IsPunct(text, t, ')'))
            {
                if (stack.Count > 0 && stack.Pop())
                {
                    inSubquery--;
                }

                continue;
            }

            if (inSubquery > 0 || Word(text, t) is not { } w || !Aggregates.Contains(w)
                || i + 1 >= b || !IsPunct(text, toks[i + 1], '('))
            {
                continue;
            }

            // 짝 괄호 뒤에 OVER 가 있으면(KEEP 절을 건너뛰고) 분석 함수.
            var close = MatchParen(text, toks, i + 1, b);
            var next = close + 1;
            if (next < b && Word(text, toks[next]) == "KEEP" && next + 1 < b && IsPunct(text, toks[next + 1], '('))
            {
                next = MatchParen(text, toks, next + 1, b) + 1;
            }

            if (next < b && Word(text, toks[next]) == "OVER")
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static int MatchParen(string text, List<Token> toks, int open, int end)
    {
        var depth = 0;
        for (var i = open; i < end; i++)
        {
            if (IsPunct(text, toks[i], '('))
            {
                depth++;
            }
            else if (IsPunct(text, toks[i], ')') && --depth == 0)
            {
                return i;
            }
        }

        return end - 1;
    }

    private static SelectItem ReadItem(string text, List<Token> toks, int a, int b)
    {
        // 별칭 떼기: "... AS x" 또는 "expr x"(x 앞이 '.'·연산자가 아님)
        var end = b;
        if (end - a >= 2 && toks[end - 1].Kind is TokenKind.Identifier or TokenKind.QuotedIdent)
        {
            var prev = toks[end - 2];
            if (Word(text, prev) == "AS")
            {
                end -= 2;
            }
            else if (!IsPunct(text, prev, '.') && prev.Kind != TokenKind.Operator)
            {
                end -= 1;
            }
        }

        var n = end - a;

        // *, q.*, o.q.*
        if (n >= 1 && IsPunct(text, toks[end - 1], '*') && (n == 1 || IsPunct(text, toks[end - 2], '.')))
        {
            return new SelectItem(n >= 3 ? Name(text, toks[end - 3]) : null, null, true);
        }

        // col, q.col, o.q.col
        if (n is 1 or 3 or 5)
        {
            var ok = true;
            for (var i = a; i < end; i++)
            {
                var isName = (i - a) % 2 == 0;
                if (isName ? Name(text, toks[i]) is null : !IsPunct(text, toks[i], '.'))
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                var column = Name(text, toks[end - 1])!;
                var qualifier = n >= 3 ? Name(text, toks[end - 3]) : null;
                if (column == "ROWID")
                {
                    return new SelectItem(qualifier, column, false);
                }

                if (!(n == 1 && toks[a].Kind != TokenKind.QuotedIdent && PseudoColumns.Contains(column)))
                {
                    return new SelectItem(qualifier, column, false);
                }
            }
        }

        return new SelectItem(null, null, false);
    }

    private static List<SelectTableRef> ReadTables(string text, List<Token> toks, int start, int end)
    {
        var tables = new List<SelectTableRef>();
        var i = start;
        var expectTable = true;
        while (i < end)
        {
            var t = toks[i];
            var w = Word(text, t);

            if (!expectTable)
            {
                // 다음 테이블 시작까지 건너뛴다(ON 조건·USING 목록 포함).
                if (IsPunct(text, t, ','))
                {
                    expectTable = true;
                    i++;
                    continue;
                }

                if (w is not null && JoinWords.Contains(w))
                {
                    while (i < end && Word(text, toks[i]) is { } jw && JoinWords.Contains(jw) && jw != "JOIN" && jw != "APPLY")
                    {
                        i++;
                    }

                    i++; // JOIN 또는 APPLY
                    expectTable = true;
                    continue;
                }

                if (IsPunct(text, t, '('))
                {
                    i = MatchParen(text, toks, i, end) + 1;
                    continue;
                }

                i++;
                continue;
            }

            expectTable = false;

            // 인라인 뷰·TABLE()·LATERAL()
            if (IsPunct(text, t, '(') || (w is "TABLE" or "LATERAL" or "JSON_TABLE" or "XMLTABLE"
                                          && i + 1 < end && IsPunct(text, toks[i + 1], '(')))
            {
                var open = IsPunct(text, t, '(') ? i : i + 1;
                i = MatchParen(text, toks, open, end) + 1;
                string? viewAlias = null;
                if (i < end && Name(text, toks[i]) is { } va && !IsClauseWord(text, toks[i]))
                {
                    viewAlias = va;
                    i++;
                }

                tables.Add(new SelectTableRef(null, null, viewAlias, null));
                continue;
            }

            // [owner.]name[@link]
            if (Name(text, t) is not { } part1)
            {
                i++;
                continue;
            }

            var nameStart = t.Start;
            var nameEnd = t.End;
            string? owner = null;
            var name = part1;
            i++;
            if (i + 1 < end && IsPunct(text, toks[i], '.') && Name(text, toks[i + 1]) is { } part2)
            {
                owner = part1;
                name = part2;
                nameEnd = toks[i + 1].End;
                i += 2;
            }

            var remote = false;
            if (i < end && IsPunct(text, toks[i], '@'))
            {
                remote = true;
                i++;
                while (i < end && (Name(text, toks[i]) is not null || IsPunct(text, toks[i], '.')) && !IsClauseWord(text, toks[i]))
                {
                    i++;
                }
            }

            string? alias = null, aliasText = null;
            if (i < end && Name(text, toks[i]) is { } maybeAlias && !IsClauseWord(text, toks[i]))
            {
                alias = maybeAlias;
                aliasText = text.Substring(toks[i].Start, toks[i].Length);
                i++;
            }
            else if (i + 1 < end && Word(text, toks[i]) == "AS" && Word(text, toks[i + 1]) != "OF"
                     && Name(text, toks[i + 1]) is { } asAlias)
            {
                alias = asAlias;
                aliasText = text.Substring(toks[i + 1].Start, toks[i + 1].Length);
                i += 2;
            }

            tables.Add(new SelectTableRef(owner, name, alias, text[nameStart..nameEnd]) { IsRemote = remote, AliasText = aliasText });
        }

        return tables;
    }

    private static bool IsClauseWord(string text, Token t) =>
        Word(text, t) is { } w && (JoinWords.Contains(w) || NotAlias.Contains(w) || FromEnd.Contains(w) || w == "FROM");
}
