namespace Oranda.App.Features.MyBatisWorkspace.Conversion;

/// <summary>WHERE 감싸기 판정 결과: 대상 아님(없음·바인드 조건 없음) / 감쌀 수 없음(알림) / 감쌈.</summary>
internal enum ConvertWhereOutcome
{
    None,
    NotWrapped,
    Wrapped,
}

/// <summary>WHERE 의 AND 조건 하나: 글 범위(앞뒤 공백 제외)와 그 안의 바인드 토큰 번호.</summary>
internal sealed record ConvertWhereCondition(int Start, int End, IReadOnlyList<int> BindTokens);

/// <summary>감쌀 범위: <c>WHERE</c> 키워드 시작부터 절 끝(ORDER BY 등 앞)까지와 그 안의 조건 목록.</summary>
internal sealed record ConvertWherePlan(int WhereStart, int ClauseEnd, IReadOnlyList<ConvertWhereCondition> Conditions);

internal sealed record ConvertWhereResult(ConvertWhereOutcome Outcome, ConvertWherePlan? Plan);

/// <summary>
/// 최상위(괄호 깊이 0) WHERE 를 AND 로 나눠 <c>&lt;where&gt;&lt;if test="x != null"&gt;</c> 로 감쌀 계획을 세운다.
/// 최상위 OR·UNION/INTERSECT/MINUS·CONNECT BY 가 있거나 WHERE 가 둘 이상이면 감싸지 않는다.
/// 최상위 <c>CASE … END</c> 안의 AND·OR 는 조건을 나누지 않는다.
/// </summary>
internal static class ConvertWhereWrap
{
    // WHERE 절을 끝내는 최상위 키워드. MODEL·WINDOW·OFFSET·FETCH 는 열 이름으로도 쓰이므로 IsClauseEnd 가 앞뒤 토큰을 더 본다.
    static readonly string[] ClauseEnders = ["GROUP", "ORDER", "HAVING", "FOR", "FETCH", "OFFSET", "MODEL", "WINDOW"];

    static readonly string[] ModelFollowers = ["MAIN", "PARTITION", "DIMENSION", "RETURN", "IGNORE", "KEEP", "UNIQUE", "REFERENCE"];

    public static ConvertWhereResult Analyze(string sql, IReadOnlyList<ConvertTok> toks)
    {
        int depth = 0, whereCount = 0, whereIdx = -1;
        bool unsupported = false;
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Kind == ConvertTokKind.Open) depth++;
            else if (t.Kind == ConvertTokKind.Close) { depth--; if (depth < 0) unsupported = true; }
            else if (depth == 0 && t.Kind == ConvertTokKind.Word)
            {
                if (ConvertTok.Is(sql, t, "WHERE")) { whereCount++; if (whereIdx < 0) whereIdx = i; }
                else if (ConvertTok.Is(sql, t, "UNION") || ConvertTok.Is(sql, t, "INTERSECT") || ConvertTok.Is(sql, t, "MINUS")
                         || ConvertTok.Is(sql, t, "CONNECT"))
                    unsupported = true;
            }
        }

        if (whereCount == 0) return new ConvertWhereResult(ConvertWhereOutcome.None, null);
        if (unsupported || whereCount > 1 || depth != 0) return NotWrapped;

        // 절 끝 찾기 + AND 로 나누기(BETWEEN a AND b 의 AND 는 조건 안).
        var conditions = new List<ConvertWhereCondition>();
        int condStart = whereIdx + 1, d = 0, endIdx = toks.Count, caseDepth = 0;
        bool between = false;
        for (int j = whereIdx + 1; j <= toks.Count; j++)
        {
            bool split = false;
            if (j == toks.Count) split = true;
            else
            {
                var t = toks[j];
                if (t.Kind == ConvertTokKind.Open) d++;
                else if (t.Kind == ConvertTokKind.Close) d--;
                else if (d == 0 && t.Kind == ConvertTokKind.Word)
                {
                    bool afterDot = toks[j - 1].Kind == ConvertTokKind.Dot;
                    if (ConvertTok.Is(sql, t, "CASE") && !afterDot) caseDepth++;
                    else if (caseDepth > 0)
                    {
                        // CASE … END 안의 AND·OR·BETWEEN·절 끝 키워드는 한 조건 안이다.
                        if (ConvertTok.Is(sql, t, "END") && !afterDot) caseDepth--;
                    }
                    else if (IsClauseEnd(sql, toks, j))
                    {
                        endIdx = j;
                        split = true;
                    }
                    else if (ConvertTok.Is(sql, t, "OR")) return NotWrapped;
                    else if (ConvertTok.Is(sql, t, "BETWEEN")) between = true;
                    else if (ConvertTok.Is(sql, t, "AND"))
                    {
                        if (between) between = false;
                        else split = true;
                    }
                }
            }

            if (!split) continue;
            if (condStart >= j) return NotWrapped; // 빈 조건(WHERE AND …)
            var binds = new List<int>();
            for (int k = condStart; k < j; k++)
            {
                if (toks[k].Kind == ConvertTokKind.Bind) binds.Add(k);
            }

            conditions.Add(new ConvertWhereCondition(toks[condStart].Start, toks[j - 1].End, binds));
            condStart = j + 1;
            if (j == endIdx || j == toks.Count) break;
        }

        if (!conditions.Exists(c => c.BindTokens.Count > 0)) return new ConvertWhereResult(ConvertWhereOutcome.None, null);
        int clauseEnd = endIdx < toks.Count ? toks[endIdx].Start : sql.Length;
        return new ConvertWhereResult(ConvertWhereOutcome.Wrapped, new ConvertWherePlan(toks[whereIdx].Start, clauseEnd, conditions));
    }

    /// <summary>
    /// 최상위 토큰 <paramref name="j"/> 가 WHERE 절을 끝내는 키워드인가. 앞 토큰이 마침표면(<c>p.MODEL</c>) 열 이름이다.
    /// OFFSET·FETCH·MODEL·WINDOW 는 열 이름과 구별하려고 뒤 토큰(OFFSET n ROWS · FETCH FIRST|NEXT · MODEL MAIN…)도 본다.
    /// </summary>
    static bool IsClauseEnd(string sql, IReadOnlyList<ConvertTok> toks, int j)
    {
        var t = toks[j];
        if (j > 0 && toks[j - 1].Kind == ConvertTokKind.Dot) return false;
        if (!Array.Exists(ClauseEnders, w => ConvertTok.Is(sql, t, w))) return false;

        int n1 = NextSignificant(toks, j), n2 = n1 < 0 ? -1 : NextSignificant(toks, n1);
        if (ConvertTok.Is(sql, t, "FETCH"))
            return n1 >= 0 && (ConvertTok.Is(sql, toks[n1], "FIRST") || ConvertTok.Is(sql, toks[n1], "NEXT"));
        if (ConvertTok.Is(sql, t, "OFFSET"))
        {
            if (n1 < 0) return false;
            if (toks[n1].Kind == ConvertTokKind.Open) return true;
            bool operand = toks[n1].Kind is ConvertTokKind.Bind or ConvertTokKind.Subst || IsNumber(sql, toks[n1]);
            return operand && n2 >= 0 && (ConvertTok.Is(sql, toks[n2], "ROW") || ConvertTok.Is(sql, toks[n2], "ROWS"));
        }

        if (ConvertTok.Is(sql, t, "MODEL")) return n1 >= 0 && Array.Exists(ModelFollowers, w => ConvertTok.Is(sql, toks[n1], w));
        if (ConvertTok.Is(sql, t, "WINDOW")) return n1 >= 0 && toks[n1].Kind == ConvertTokKind.Word;
        return true;
    }

    static int NextSignificant(IReadOnlyList<ConvertTok> toks, int index)
    {
        for (int k = index + 1; k < toks.Count; k++)
        {
            if (toks[k].Kind != ConvertTokKind.Comment) return k;
        }

        return -1;
    }

    static bool IsNumber(string sql, ConvertTok t) =>
        t.Kind == ConvertTokKind.Other && (char.IsAsciiDigit(sql[t.Start]) || sql[t.Start] == '.');

    static ConvertWhereResult NotWrapped { get; } = new(ConvertWhereOutcome.NotWrapped, null);
}
