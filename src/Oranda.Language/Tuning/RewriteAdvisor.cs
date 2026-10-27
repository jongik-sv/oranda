using System.Globalization;
using System.Text.RegularExpressions;
using Oranda.Core.Plans;
using Oranda.Core.Tuning;
using Oranda.Language.Lexing;

namespace Oranda.Language.Tuning;

/// <summary>재작성 규칙(TN-10·11).</summary>
public enum RewriteRule
{
    NotInToNotExists,
    OrToUnionAll,
    FunctionOnColumn,
    ImplicitConversion,
    ScalarSubqueryToJoin,
    SelectStar,
}

/// <summary>
/// 재작성 후보 하나. <see cref="Candidate"/> 는 Core 계약 형(<see cref="RewriteCandidate.Sql"/> 이 빈 글이면 경고만).
/// </summary>
public sealed record RewriteAdvice(RewriteCandidate Candidate, RewriteRule Rule)
{
    /// <summary>목록에 보일 제목(예: <c>NOT IN → NOT EXISTS</c>).</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>벤치마크 표의 짧은 이름(예: <c>NOT EXISTS</c>).</summary>
    public string ShortLabel { get; init; } = string.Empty;

    /// <summary>문장 안 줄(1 부터). 계획에서 찾은 것이면 null.</summary>
    public int? Line { get; init; }

    /// <summary>계획 단계 Id(암시적 형 변환). 문장에서 찾은 것이면 null.</summary>
    public int? StepId { get; init; }

    /// <summary>결과가 달라질 수 있는 재작성(NULL 의미·행 중복). 벤치마크 체크를 꺼 둔다.</summary>
    public bool SemanticsMayChange { get; init; }

    /// <summary>경고 상자 글. 없으면 null.</summary>
    public string? Caution { get; init; }

    /// <summary>바꾼 SQL 이 없는 까닭 한 마디(목록 줄에 보인다). 없으면 화면이 일반 문구를 쓴다.</summary>
    public string? NoRewriteReason { get; init; }

    /// <summary>고친 SQL 이 있다.</summary>
    public bool HasRewrite => Candidate.Sql.Length > 0;

    /// <summary>위치 칸 글(줄 7 / Id 4).</summary>
    public string Location(bool english = false) =>
        Line is { } l ? (english ? $"line {l}" : $"줄 {l}") : StepId is { } id ? $"Id {id}" : string.Empty;
}

/// <summary>
/// 규칙 기반 재작성 후보(TSK-06-05, TN-10·11). 순수 함수: SQL 글(토큰)과, 있으면 계획의 조건만 본다.
/// 바꿀 수 있는 것은 고친 SQL 전체를 주고, 바꾸기 위험하거나 모양을 모르면 경고만 준다. TSK-06-07 보고서도 이것을 쓴다.
/// </summary>
public static partial class RewriteAdvisor
{
    private static readonly HashSet<string> WrapFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "TRUNC", "UPPER", "LOWER", "INITCAP", "TO_CHAR", "TO_DATE", "TO_NUMBER", "SUBSTR", "NVL", "NVL2", "TRIM", "LTRIM", "RTRIM", "ROUND",
        "DECODE", "COALESCE", "ABS", "REPLACE", "INSTR", "LENGTH", "LPAD", "RPAD",
    };

    private static readonly HashSet<string> Aggregates = new(StringComparer.OrdinalIgnoreCase)
    {
        "COUNT", "SUM", "AVG", "MIN", "MAX", "LISTAGG", "STDDEV", "VARIANCE", "MEDIAN", "XMLAGG", "JSON_ARRAYAGG", "COLLECT",
    };

    /// <summary>후보를 모두 만든다. 규칙 순서·문장 순서. 빈 글이면 빈 목록.</summary>
    public static IReadOnlyList<RewriteAdvice> Analyze(string sql, ExecutionPlan? plan = null, bool english = false)
    {
        var list = new List<RewriteAdvice>();
        if (string.IsNullOrWhiteSpace(sql))
        {
            return list;
        }

        sql = sql.TrimEnd();
        if (sql.EndsWith(';'))
        {
            sql = sql[..^1];
        }

        var t = new TuningSql(sql);
        if (t.Count == 0)
        {
            return list;
        }

        NotIn(t, list, english);
        OrExpansion(t, list, english);
        FunctionOnColumn(t, list, english);
        if (plan is not null)
        {
            ImplicitConversion(t, plan, list, english);
        }

        ScalarSubquery(t, list, english);
        SelectStar(t, list, english);
        return list;
    }

    // ---- NOT IN (하위 질의) → NOT EXISTS ---------------------------------------------------------

    private static void NotIn(TuningSql t, List<RewriteAdvice> list, bool en)
    {
        for (var i = 0; i + 3 < t.Count; i++)
        {
            if (!t.Is(i, "NOT") || !t.Is(i + 1, "IN") || !t.IsPunct(i + 2, '(') || !t.Is(i + 3, "SELECT") || t.Partner[i + 2] < 0)
            {
                continue;
            }

            var close = t.Partner[i + 2];
            string? why = null, rewritten = null, item = null;
            var leftStart = ColumnRefStart(t, i - 1);
            var inner = t.Block(i + 3);
            if (!t.Balanced)
            {
                why = en ? "the parentheses do not balance" : "괄호 짝이 맞지 않습니다";
            }
            else if (leftStart < 0 || !IsConditionStart(t, leftStart - 1))
            {
                why = en ? "the left side is not a single column" : "왼쪽이 열 하나가 아닙니다";
            }
            else if (inner is null || inner.Limit != close)
            {
                why = en ? "the subquery shape is not recognised" : "하위 질의 모양을 알 수 없습니다";
            }
            else if (inner.HasSetOperator || inner.HasGroupBy || inner.HasConnectBy || inner.HasOrderBy || inner.HasFetch)
            {
                why = en ? "the subquery has UNION, GROUP BY, CONNECT BY, ORDER BY or FETCH" : "하위 질의에 UNION·GROUP BY·CONNECT BY·ORDER BY·FETCH 가 있습니다";
            }
            else
            {
                var (itemFrom, itemTo) = SelectItems(t, inner);
                var innerTables = t.TablesOf(inner.From, inner.FromEnd);
                var left = t.Slice(leftStart, i - 1);
                if (itemFrom > itemTo || HasCommaAt(t, itemFrom, itemTo, inner.Depth))
                {
                    why = en ? "the subquery returns more than one column" : "하위 질의가 열을 여러 개 고릅니다";
                }
                else if (innerTables.Count == 0)
                {
                    why = en ? "the subquery has no table" : "하위 질의에 표가 없습니다";
                }
                else
                {
                    item = t.Slice(itemFrom, itemTo);
                    if (itemFrom == itemTo && t.IsName(itemFrom))
                    {
                        var q = innerTables.Count == 1 ? innerTables[0].Qualifier : null;
                        item = q is null ? null : q + "." + item;
                        why = q is null ? (en ? "the subquery column cannot be qualified" : "하위 질의 열을 한정할 수 없습니다") : null;
                    }

                    if (why is null && leftStart == i - 1)
                    {
                        // 한정 안 된 바깥 열: 바깥 블록의 표가 하나면 그 이름으로 한정한다.
                        var outer = t.Enclosing(i);
                        var outerTables = outer is null ? [] : t.TablesOf(outer.From, outer.FromEnd);
                        var oq = outerTables.Count == 1 ? outerTables[0].Qualifier : null;
                        if (oq is null || innerTables.Any(r => string.Equals(r.Qualifier, oq, StringComparison.OrdinalIgnoreCase)))
                        {
                            why = en ? "the outer column cannot be qualified (add a table alias)" : "바깥 열을 한정할 수 없습니다(표 별칭을 붙이세요)";
                        }
                        else
                        {
                            left = oq + "." + left;
                        }
                    }

                    if (why is null)
                    {
                        var cond = item + " = " + left;
                        string body;
                        if (inner.Where >= 0)
                        {
                            var w = t.Slice(inner.Where + 1, close - 1);
                            var hasOr = t.SplitAt(inner.Where + 1, close - 1, "OR").Count > 1;
                            body = t.Slice(inner.From, inner.Where) + " " + (hasOr ? "(" + w + ")" : w) + "\n   AND " + cond;
                        }
                        else
                        {
                            body = t.Slice(inner.From, close - 1) + "\n WHERE " + cond;
                        }

                        rewritten = Replace(t.Text, t.Tokens[leftStart].Start, t.Tokens[close].End, "NOT EXISTS (SELECT 1 " + body + ")");
                    }
                }
            }

            var detail = en
                ? "Rewrites NOT IN (subquery) as NOT EXISTS so the optimizer can use an anti-join instead of re-reading the subquery."
                : "NOT IN (하위 질의) 를 NOT EXISTS 로 바꿉니다. 안티 조인이 되어 하위 질의를 행마다 다시 읽지 않습니다.";
            list.Add(Advice(RewriteRule.NotInToNotExists, "not-in-to-not-exists", "NOT IN → NOT EXISTS", "NOT EXISTS",
                Detail(detail, why, en), rewritten, t.LineOf(i)) with
            {
                SemanticsMayChange = true,
                Caution = en
                    ? $"If the subquery column{(item is null ? string.Empty : " (" + item + ")")} can be NULL the results differ (NOT IN returns no rows). Check that the column is NOT NULL."
                    : $"하위 질의 열{(item is null ? string.Empty : "(" + item + ")")}에 NULL 이 있으면 결과가 달라집니다(NOT IN 은 0 행). 열이 NOT NULL 인지 확인하세요.",
            });
        }
    }

    // ---- OR → UNION ALL ---------------------------------------------------------------------------

    private static void OrExpansion(TuningSql t, List<RewriteAdvice> list, bool en)
    {
        var main = t.MainSelect();
        if (main < 0 || t.Block(main) is not { Where: >= 0 } b)
        {
            return;
        }

        var w0 = b.Where + 1;
        var w1 = b.WhereEnd - 1;
        if (w0 > w1)
        {
            return;
        }

        List<(int From, int To)>? disjuncts = null;
        var rest = new List<(int From, int To)>();
        var ors = t.SplitAt(w0, w1, "OR");
        if (ors.Count > 1)
        {
            disjuncts = ors;
        }
        else
        {
            var ands = t.SplitAt(w0, w1, "AND");
            for (var k = 0; k < ands.Count; k++)
            {
                var (s, e) = t.Strip(ands[k].From, ands[k].To);
                var inner = s != ands[k].From ? t.SplitAt(s, e, "OR") : [];
                if (inner.Count > 1)
                {
                    disjuncts = inner;
                    rest.AddRange(ands.Where((_, x) => x != k));
                    break;
                }
            }
        }

        if (disjuncts is null || SameColumnEquality(t, disjuncts))
        {
            return; // OR 가 없거나, 같은 열 등호뿐(IN 목록과 같다)
        }

        string? why = null;
        if (!t.Balanced)
        {
            why = en ? "the parentheses do not balance" : "괄호 짝이 맞지 않습니다";
        }
        else if (t.Depth[main] != 0)
        {
            why = en ? "the query is wrapped in parentheses" : "질의가 괄호로 감싸여 있습니다";
        }
        else if (b.HasSetOperator || b.HasGroupBy || b.HasDistinct || b.HasOrderBy || b.HasConnectBy || b.HasForUpdate || b.HasFetch || b.WhereEnd != t.Count)
        {
            why = en ? "the query has UNION, GROUP BY, DISTINCT, ORDER BY, CONNECT BY or FETCH" : "질의에 UNION·GROUP BY·DISTINCT·ORDER BY·CONNECT BY·FETCH 가 있습니다";
        }
        else if (Enumerable.Range(w0, w1 - w0 + 1).Any(k => t.Is(k, "ROWNUM")))
        {
            why = en ? "the condition uses ROWNUM" : "조건에 ROWNUM 이 있습니다";
        }
        else if (HasAggregate(t, b.Select + 1, b.From - 1))
        {
            why = en ? "the select list has an aggregate" : "SELECT 목록에 집계 함수가 있습니다";
        }
        else if (disjuncts.Count > 4)
        {
            why = en ? "more than four OR branches" : "OR 가지가 넷보다 많습니다";
        }
        else if (disjuncts.Any(d => Complex(t, d.From, d.To)))
        {
            why = en ? "a branch has AND, OR or a subquery" : "가지 안에 AND·OR·하위 질의가 있습니다";
        }

        string? rewritten = null;
        if (why is null)
        {
            var prefix = t.Slice(b.Select, b.Where);
            var with = main > 0 ? t.Slice(0, main - 1) + "\n" : string.Empty;
            var restText = rest.Select(r => t.Slice(r.From, r.To).Trim()).ToList();
            var branches = new List<string>();
            for (var k = 0; k < disjuncts.Count; k++)
            {
                var conds = new List<string>(restText) { "(" + Text(t, disjuncts[k]) + ")" };
                conds.AddRange(disjuncts.Take(k).Select(d => "LNNVL(" + Text(t, d) + ")"));
                branches.Add(prefix + " " + string.Join("\n   AND ", conds));
            }

            rewritten = with + string.Join("\nUNION ALL\n", branches);
        }

        var orToken = Enumerable.Range(w0, w1 - w0 + 1).FirstOrDefault(k => t.Is(k, "OR"), w0);
        var detail = en
            ? "Reads each OR branch separately and joins them with UNION ALL, so each branch can use its own index. LNNVL drops rows already returned by an earlier branch."
            : "OR 로 묶인 조건을 가지마다 따로 읽어 UNION ALL 로 합칩니다. 가지마다 자기 조건의 인덱스를 쓸 수 있고, 앞 가지에서 나온 행은 LNNVL 로 뺍니다.";
        list.Add(Advice(RewriteRule.OrToUnionAll, "or-to-union-all", "OR → UNION ALL", "UNION ALL", Detail(detail, why, en), rewritten, t.LineOf(orToken)));
    }

    private static bool SameColumnEquality(TuningSql t, List<(int From, int To)> parts)
    {
        string? column = null;
        foreach (var p in parts)
        {
            var (s, e) = t.Strip(p.From, p.To);
            var end = t.ColumnRefEnd(s);
            if (end < 0 || !t.Is(end + 1, "=") || end + 1 >= e)
            {
                return false;
            }

            var name = string.Join(".", Enumerable.Range(s, end - s + 1).Where(k => t.IsName(k)).Select(k => TuningSql.Normalize(t.Word(k))));
            if (column is not null && column != name)
            {
                return false;
            }

            column = name;
        }

        return column is not null;
    }

    private static bool Complex(TuningSql t, int from, int to)
    {
        var (s, e) = t.Strip(from, to);
        return t.SplitAt(s, e, "AND").Count > 1 || t.SplitAt(s, e, "OR").Count > 1 || Enumerable.Range(s, e - s + 1).Any(k => t.Is(k, "SELECT"));
    }

    private static string Text(TuningSql t, (int From, int To) part)
    {
        var (s, e) = t.Strip(part.From, part.To);
        return t.Slice(s, e).Trim();
    }

    // ---- 열에 씌운 함수 ---------------------------------------------------------------------------

    private static void FunctionOnColumn(TuningSql t, List<RewriteAdvice> list, bool en)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 3 < t.Count; i++)
        {
            if (t.Kind(i) != TokenKind.Identifier || !WrapFunctions.Contains(t.Word(i)) || !t.IsPunct(i + 1, '(') || t.Partner[i + 1] < 0 || t.IsPunct(i - 1, '.'))
            {
                continue;
            }

            var arg = i + 2;
            var end = t.ColumnRefEnd(arg);
            if (end < 0 || !(t.IsPunct(end + 1, ',') || t.IsPunct(end + 1, ')')) || (end == arg && PlanPredicates.PseudoColumns.Contains(t.Word(arg))) || !InPredicate(t, i))
            {
                continue;
            }

            var close = t.Partner[i + 1];
            var call = t.Slice(i, close);
            if (!seen.Add(call))
            {
                continue;
            }

            var column = t.Slice(arg, end);
            var fn = t.Word(i).ToUpperInvariant();
            string? rewritten = null;
            var label = string.Empty;
            string? caution = null;
            string? done = null;
            var rhsEnd = t.Is(close + 1, "=") && t.Balanced ? ConditionEnd(t, close + 2, t.Depth[i]) : -1;
            if (fn == "TRUNC" && t.IsPunct(end + 1, ')') && rhsEnd >= close + 2)
            {
                var rhs = t.Slice(close + 2, rhsEnd).Trim();
                rewritten = Replace(t.Text, t.Tokens[i].Start, t.Tokens[rhsEnd].End, $"({column} >= TRUNC({rhs}) AND {column} < TRUNC({rhs}) + 1)");
                label = en ? "TRUNC removed" : "TRUNC 제거";
                caution = en
                    ? "Same result when the column is DATE or TIMESTAMP (for a NUMBER column negative values can differ)."
                    : "열이 DATE·TIMESTAMP 이면 같은 결과입니다(숫자 열이면 음수에서 달라질 수 있습니다).";
                done = en ? " TRUNC(column) = value was turned into a one-day range." : " TRUNC(열) = 값 을 하루 범위 조건으로 바꿨습니다.";
            }
            else if (fn == "TO_CHAR" && rhsEnd == close + 2 && t.IsPunct(end + 1, ',') && end + 3 == close && t.Kind(end + 2) == TokenKind.StringLiteral
                     && DateFormatStep(t.Word(end + 2)) is { } step
                     && (t.Kind(close + 2) == TokenKind.Bind || (t.Kind(close + 2) == TokenKind.StringLiteral && LiteralFits(t.Word(close + 2), t.Word(end + 2)))))
            {
                // TO_CHAR(날짜 열, 'YYYYMMDD') = '20261001' → 그날(그달·그해) 범위.
                var fmt = t.Word(end + 2);
                var value = t.Word(close + 2);
                var low = step.TruncUnit is null ? $"TO_DATE({value}, {fmt})" : $"TRUNC(TO_DATE({value}, {fmt}), {step.TruncUnit})";
                var high = step.High.Replace("{0}", low, StringComparison.Ordinal);
                rewritten = Replace(t.Text, t.Tokens[i].Start, t.Tokens[rhsEnd].End, $"({column} >= {low} AND {column} < {high})");
                label = en ? "TO_CHAR removed" : "TO_CHAR 제거";
                caution = en
                    ? "Same result when the column is DATE or TIMESTAMP and the value is a valid date in that format (a bad value now raises an error instead of matching nothing)."
                    : "열이 DATE·TIMESTAMP 이고 값이 그 형식의 올바른 날짜면 같은 결과입니다(틀린 값은 0 행 대신 오류가 납니다).";
                done = en ? " TO_CHAR(column, format) = value was turned into a date range on the bare column." : " TO_CHAR(열, 형식) = 값 을 맨 열의 날짜 범위로 바꿨습니다.";
            }
            else if (fn == "SUBSTR" && rhsEnd == close + 2 && t.IsPunct(end + 1, ',') && t.Word(end + 2) == "1" && t.IsPunct(end + 3, ',')
                     && t.Kind(end + 4) == TokenKind.Number && end + 5 == close && t.Kind(close + 2) == TokenKind.StringLiteral
                     && PlainLiteral(t.Word(close + 2)) is { Length: > 0 } prefix && prefix.IndexOfAny(['%', '_']) < 0
                     && int.TryParse(t.Word(end + 4), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n == prefix.Length)
            {
                // SUBSTR(열, 1, n) = 'n 글자' → 열 LIKE '글자%'(앞부분이라 인덱스 범위 읽기가 된다).
                rewritten = Replace(t.Text, t.Tokens[i].Start, t.Tokens[rhsEnd].End, $"{column} LIKE '{prefix}%'");
                label = "LIKE";
                done = en ? " SUBSTR(column, 1, n) = 'n chars' was turned into a LIKE prefix match." : " SUBSTR(열, 1, n) = 'n 글자' 를 LIKE 앞부분 비교로 바꿨습니다.";
            }

            var shortCall = call.Length > 40 ? call[..37] + "…" : call;
            var detail = en
                ? $"Wrapping a column in a function ({shortCall}) keeps the optimizer from using a normal index on {column}. Leave the column bare and transform the value side, or consider a function-based index."
                : $"{shortCall} 처럼 열에 함수를 씌우면 {column} 의 일반 인덱스를 쓰지 못합니다. 열은 그대로 두고 비교 값 쪽을 바꾸거나 함수 기반 인덱스를 검토하세요.";
            string? reason = null;
            if (done is not null)
            {
                detail += done;
            }
            else
            {
                reason = en ? "Can't rewrite safely — consider a function-based index" : "같은 결과로 못 바꿈 — 함수 기반 인덱스 검토";
                detail += en
                    ? $" This shape cannot be turned into a condition on the bare column with a surely equal result. If the condition is frequent, a function-based index on ({call}) lets the optimizer use an index."
                    : $" 이 모양은 결과가 확실히 같은 맨 열 조건으로 바꿀 수 없습니다. 자주 쓰는 조건이면 ({call}) 로 만든 함수 기반 인덱스로 인덱스를 쓸 수 있습니다.";
            }

            list.Add(Advice(RewriteRule.FunctionOnColumn, "function-on-column", (en ? "Function on a column · " : "열에 씌운 함수 · ") + shortCall, label, detail, rewritten, t.LineOf(i)) with
            {
                Caution = caution,
                NoRewriteReason = reason,
            });
        }
    }

    // ---- 암시적 형 변환 의심(계획의 조건) ---------------------------------------------------------

    private static void ImplicitConversion(TuningSql t, ExecutionPlan plan, List<RewriteAdvice> list, bool en)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in plan.Steps)
        {
            foreach (var term in PlanPredicates.Parse(step.AccessPredicates, step.FilterPredicates))
            {
                if (term.Kind != PredicateKind.Function || term.Function is not { } fn || !PlanPredicates.ConversionFunctions.Contains(fn))
                {
                    continue;
                }

                // 사용자가 직접 쓴 TO_NUMBER(열) 등은 「열에 씌운 함수」 가 이미 말한다.
                if (!fn.Equals("INTERNAL_FUNCTION", StringComparison.OrdinalIgnoreCase)
                    && Regex.IsMatch(t.Text, $@"\b{Regex.Escape(fn)}\s*\(\s*(""?[\w$#]+""?\s*\.\s*)?""?{Regex.Escape(term.Column)}""?\s*[,)]", RegexOptions.IgnoreCase))
                {
                    continue;
                }

                var target = (term.Alias is null ? string.Empty : term.Alias + ".") + term.Column;
                if (!seen.Add(target))
                {
                    continue;
                }

                var detail = en
                    ? $"Plan Id {step.Id} shows {fn}({target}) in its condition ({term.Text}). When the compared value has a different type Oracle converts the column, and the index on {term.Column} cannot be used. Match the bind or literal type to the column type."
                    : $"계획 Id {step.Id} 의 조건({term.Text})에 {fn}({target}) 이 보입니다. 비교 값의 형이 열과 달라 Oracle 이 열 쪽을 바꾸면 {term.Column} 인덱스를 쓰지 못합니다. 바인드·리터럴의 형을 열 형에 맞추세요.";
                if (fn.Equals("INTERNAL_FUNCTION", StringComparison.OrdinalIgnoreCase))
                {
                    detail += en ? " INTERNAL_FUNCTION also appears for IN lists, so read it with the condition." : " INTERNAL_FUNCTION 은 IN 목록 등에서도 보이니 조건 원문과 함께 보세요.";
                }

                // TO_NUMBER(열) 이 보이면 열은 문자형이고 비교 값이 숫자다 → 문장의 정수 리터럴은 따옴표로, 바인드는 TO_CHAR 로 맞춘다.
                // 다른 변환(INTERNAL_FUNCTION·TO_CHAR·TO_DATE…)은 계획만으로 열 형을 알 수 없어 경고만 둔다.
                string? rewritten = null;
                string? reason;
                if (fn.Equals("TO_NUMBER", StringComparison.OrdinalIgnoreCase))
                {
                    var edits = CharColumnEdits(t, term.Alias, term.Column);
                    rewritten = edits.Count > 0 ? Apply(t.Text, edits) : null;
                    reason = rewritten is null ? en ? "No comparison that can be changed safely" : "같은 결과로 바꿀 비교가 아님" : null;
                }
                else
                {
                    reason = en ? "Column type unknown — can't rewrite" : "열 형을 몰라 못 바꿈";
                }

                if (rewritten is not null)
                {
                    detail += en
                        ? $" The plan converts the column with TO_NUMBER, so it holds text: number literals compared with {target} were quoted and binds wrapped in TO_CHAR."
                        : $" 계획이 열을 TO_NUMBER 로 바꾸므로 열은 문자형입니다. {target} 와 비교하는 숫자 리터럴은 따옴표로, 바인드는 TO_CHAR 로 감쌌습니다.";
                }

                list.Add(Advice(RewriteRule.ImplicitConversion, "implicit-conversion", (en ? "Suspected implicit conversion · " : "암시적 형 변환 의심 · ") + term.Column,
                    rewritten is null ? string.Empty : en ? "types matched" : "형 맞춤", detail, rewritten, null) with
                {
                    StepId = step.Id,
                    NoRewriteReason = reason,
                    SemanticsMayChange = rewritten is not null,
                    Caution = rewritten is null ? null : en
                        ? $"A number comparison also matches '0123' or ' 123'; a text comparison matches only '123'. Check that {term.Column} holds plain digits."
                        : $"숫자 비교는 '0123'·' 123' 도 맞지만 문자 비교는 '123' 만 맞습니다. {term.Column} 값이 앞 0·공백 없는 숫자 글인지 확인하세요.",
                });
            }
        }
    }

    // ---- 스칼라 하위 질의 → 바깥 조인 -------------------------------------------------------------

    private static void ScalarSubquery(TuningSql t, List<RewriteAdvice> list, bool en)
    {
        var main = t.MainSelect();
        if (main < 0 || t.Block(main) is not { } b)
        {
            return;
        }

        for (var k = b.Select + 1; k < b.From; k++)
        {
            if (t.Depth[k] != b.Depth || !t.IsPunct(k, '(') || !t.Is(k + 1, "SELECT") || t.Partner[k] < 0)
            {
                continue;
            }

            var close = t.Partner[k];
            var inner = t.Block(k + 1);
            string? why = null, rewritten = null, table = null;
            if (!t.Balanced)
            {
                why = en ? "the parentheses do not balance" : "괄호 짝이 맞지 않습니다";
            }
            else if (inner is null || inner.Limit != close)
            {
                why = en ? "the subquery shape is not recognised" : "하위 질의 모양을 알 수 없습니다";
            }
            else if (inner.HasSetOperator || inner.HasGroupBy || inner.HasConnectBy || inner.HasOrderBy || inner.HasFetch || inner.HasDistinct)
            {
                why = en ? "the subquery has UNION, GROUP BY, DISTINCT, ORDER BY or FETCH" : "하위 질의에 UNION·GROUP BY·DISTINCT·ORDER BY·FETCH 가 있습니다";
            }
            else
            {
                var (itemFrom, itemTo) = SelectItems(t, inner);
                var innerTables = t.TablesOf(inner.From, inner.FromEnd);
                var outerTables = t.TablesOf(b.From, b.FromEnd);
                var itemEnd = ItemExpressionEnd(t, itemFrom, itemTo, inner.Depth);
                table = innerTables.Count > 0 ? innerTables[0].Name : null;
                if (itemFrom > itemTo || HasCommaAt(t, itemFrom, itemTo, inner.Depth))
                {
                    why = en ? "the subquery returns more than one column" : "하위 질의가 열을 여러 개 고릅니다";
                }
                else if (HasAggregate(t, itemFrom, itemTo))
                {
                    why = en ? "the subquery aggregates (a join would need GROUP BY)" : "하위 질의가 집계합니다(조인으로 바꾸면 GROUP BY 가 필요합니다)";
                }
                else if (innerTables.Count != 1 || innerTables[0].Name is null || HasCommaAt(t, inner.From + 1, inner.FromEnd - 1, inner.Depth)
                    || Enumerable.Range(inner.From + 1, Math.Max(0, inner.FromEnd - inner.From - 1)).Any(x => t.Depth[x] == inner.Depth && t.Is(x, "JOIN")))
                {
                    why = en ? "the subquery reads more than one table" : "하위 질의가 표를 여럿 읽습니다";
                }
                else if (innerTables[0].Alias is null)
                {
                    why = en ? "the subquery table has no alias" : "하위 질의 표에 별칭이 없습니다";
                }
                else if (inner.Where < 0)
                {
                    why = en ? "there is no correlation condition" : "상관 조건이 없습니다";
                }
                else if (outerTables.Any(o => string.Equals(o.Qualifier, innerTables[0].Alias, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(o.Name, innerTables[0].Alias, StringComparison.OrdinalIgnoreCase)))
                {
                    why = en ? "the subquery alias is also used outside" : "하위 질의 별칭을 바깥에서도 씁니다";
                }
                else if ((BareName(t, itemFrom, itemEnd) ?? BareName(t, inner.Where + 1, close - 1)) is { } bare)
                {
                    why = en ? $"the column {bare} is not qualified, so its table is unknown" : $"한정하지 않은 열({bare})이 있어 어느 표 열인지 모릅니다";
                }
                else
                {
                    var alias = innerTables[0].Alias!;
                    var tableText = t.Slice(innerTables[0].First, innerTables[0].Last);
                    var whereText = t.Slice(inner.Where + 1, close - 1).Trim();
                    var comma = HasCommaAt(t, b.From + 1, b.FromEnd - 1, b.Depth);
                    var innerOr = t.SplitAt(inner.Where + 1, close - 1, "OR").Count > 1;
                    if (comma && innerOr)
                    {
                        why = en ? "comma joins with an OR condition cannot take (+)" : "쉼표 조인에 OR 조건이라 (+) 를 붙일 수 없습니다";
                    }
                    else
                    {
                        var edits = new List<(int Start, int End, string With)>
                        {
                            (t.Tokens[k].Start, t.Tokens[close].End, t.Slice(itemFrom, itemEnd)),
                        };
                        var fromTail = t.Tokens[b.FromEnd - 1].End;
                        if (!comma)
                        {
                            edits.Add((fromTail, fromTail, "\n  LEFT JOIN " + tableText + " ON " + whereText));
                        }
                        else
                        {
                            var plus = OuterJoinMarks(t, inner.Where + 1, close - 1, alias);
                            edits.Add((fromTail, fromTail, ", " + tableText));
                            if (b.Where >= 0)
                            {
                                var ws = t.Tokens[b.Where + 1].Start;
                                var we = t.Tokens[b.WhereEnd - 1].End;
                                var outerOr = t.SplitAt(b.Where + 1, b.WhereEnd - 1, "OR").Count > 1;
                                var outerWhere = t.Text[ws..we];
                                edits.Add((ws, we, (outerOr ? "(" + outerWhere + ")" : outerWhere) + "\n   AND " + plus));
                            }
                            else
                            {
                                edits.Add((fromTail, fromTail, "\n WHERE " + plus));
                            }
                        }

                        rewritten = Apply(t.Text, edits);
                    }
                }
            }

            var detail = en
                ? "A scalar subquery in the select list runs once per outer row (slow when its cache misses). An outer join reads it in one pass."
                : "SELECT 목록의 스칼라 하위 질의는 바깥 행마다 한 번씩 돕니다(캐시가 안 맞으면 느립니다). 바깥 조인으로 바꾸면 한 번에 읽습니다.";
            list.Add(Advice(RewriteRule.ScalarSubqueryToJoin, "scalar-subquery-to-join",
                (en ? "Scalar subquery → join" : "스칼라 하위 질의 → 조인") + (table is null ? string.Empty : " · " + table),
                en ? "outer join" : "바깥 조인", Detail(detail, why, en), rewritten, t.LineOf(k)) with
            {
                SemanticsMayChange = true,
                Caution = en
                    ? "If the join column is not unique the join returns more rows (the scalar subquery would raise ORA-01427)."
                    : "조인 열이 유일하지 않으면 행이 늘어납니다(스칼라 하위 질의였다면 ORA-01427 오류).",
            });
        }
    }

    // ---- SELECT * --------------------------------------------------------------------------------

    private static void SelectStar(TuningSql t, List<RewriteAdvice> list, bool en)
    {
        for (var s = 0; s < t.Count; s++)
        {
            if (!t.Is(s, "SELECT") || (t.IsPunct(s - 1, '(') && t.Is(s - 2, "EXISTS")) || t.Block(s) is not { } b)
            {
                continue;
            }

            for (var k = s + 1; k < b.From; k++)
            {
                if (t.Depth[k] != b.Depth || !t.Is(k, "*"))
                {
                    continue;
                }

                var prev = k - 1;
                if (t.Is(prev, "SELECT") || t.Is(prev, "DISTINCT") || t.Is(prev, "UNIQUE") || t.Is(prev, "ALL") || t.IsPunct(prev, ',') || t.IsPunct(prev, '.')
                    || t.Kind(prev) == TokenKind.Hint)
                {
                    var detail = en
                        ? "Selecting only the needed columns reads and sends less, and may let an index answer alone. The result shape also changes when the table gains columns."
                        : "필요한 열만 고르면 읽는 양·전송량이 줄고 인덱스만으로 답할 수도 있습니다. 표에 열이 늘면 결과 모양도 바뀝니다.";
                    detail += en
                        ? " Only listing the columns runs the same plan, so there is nothing to benchmark — pick just the columns you need."
                        : " 열 목록을 펼치기만 해서는 같은 계획이라 벤치마크할 것이 없습니다 — 필요한 열만 고르세요.";
                    list.Add(Advice(RewriteRule.SelectStar, "select-star", en ? "SELECT * warning" : "SELECT * 경고", string.Empty, detail, null, t.LineOf(k)) with
                    {
                        NoRewriteReason = en ? "Same speed if expanded — pick needed columns" : "바꿔도 성능 같음 — 필요한 열만 고르세요",
                    });
                    break;
                }
            }
        }
    }

    // ---- 도우미 -----------------------------------------------------------------------------------

    private static RewriteAdvice Advice(RewriteRule rule, string id, string title, string label, string detail, string? sql, int? line) =>
        new(new RewriteCandidate(id, detail, sql ?? string.Empty), rule) { Title = title, ShortLabel = label, Line = line };

    private static string Detail(string detail, string? why, bool en) =>
        why is null ? detail : detail + (en ? " Not rewritten automatically: " : " 자동으로 바꾸지 못했습니다: ") + why + ".";

    /// <summary>SELECT 목록(힌트·DISTINCT 뺀) 토큰 범위.</summary>
    private static (int From, int To) SelectItems(TuningSql t, SelectBlock b)
    {
        var from = b.Select + 1;
        if (from < t.Count && t.Kind(from) == TokenKind.Hint)
        {
            from++;
        }

        if (t.Is(from, "DISTINCT") || t.Is(from, "UNIQUE") || t.Is(from, "ALL"))
        {
            from++;
        }

        return (from, b.From - 1);
    }

    private static bool HasCommaAt(TuningSql t, int from, int to, int depth) =>
        Enumerable.Range(from, Math.Max(0, to - from + 1)).Any(k => t.Depth[k] == depth && t.IsPunct(k, ','));

    private static bool HasAggregate(TuningSql t, int from, int to) =>
        Enumerable.Range(from, Math.Max(0, to - from + 1)).Any(k => t.Kind(k) == TokenKind.Identifier && Aggregates.Contains(t.Word(k)) && t.IsPunct(k + 1, '('));

    /// <summary>항목의 별칭(<c>AS x</c>·끝 이름)을 뺀 식의 끝.</summary>
    private static int ItemExpressionEnd(TuningSql t, int from, int to, int depth)
    {
        for (var k = from; k <= to; k++)
        {
            if (t.Depth[k] == depth && t.Is(k, "AS"))
            {
                return k - 1;
            }
        }

        return to > from && t.IsName(to) && !t.IsPunct(to - 1, '.') && (t.IsName(to - 1) || t.IsPunct(to - 1, ')')) ? to - 1 : to;
    }

    /// <summary>한정하지 않은 열 이름(함수·의사 열·별칭 앞뒤 점 제외). 없으면 null.</summary>
    private static string? BareName(TuningSql t, int from, int to)
    {
        for (var k = from; k <= to; k++)
        {
            if (t.Kind(k) != TokenKind.Identifier && t.Kind(k) != TokenKind.QuotedIdent)
            {
                continue;
            }

            if (t.IsPunct(k - 1, '.') || t.IsPunct(k + 1, '.') || t.IsPunct(k + 1, '(') || PlanPredicates.PseudoColumns.Contains(t.Word(k)))
            {
                continue;
            }

            return t.Word(k);
        }

        return null;
    }

    /// <summary>조건 글에서 <paramref name="alias"/>.열 뒤마다 (+) 를 붙인다.</summary>
    private static string OuterJoinMarks(TuningSql t, int from, int to, string alias)
    {
        var edits = new List<(int Start, int End, string With)>();
        for (var k = from; k + 2 <= to; k++)
        {
            if (t.IsName(k) && string.Equals(t.Word(k), alias, StringComparison.OrdinalIgnoreCase) && t.IsPunct(k + 1, '.') && t.IsName(k + 2) && !t.IsPunct(k + 3, '('))
            {
                edits.Add((t.Tokens[k + 2].End, t.Tokens[k + 2].End, "(+)"));
            }
        }

        var start = t.Tokens[from].Start;
        var text = t.Text[start..t.Tokens[to].End];
        return Apply(text, edits.Select(e => (e.Start - start, e.End - start, e.With)).ToList()).Trim();
    }

    /// <summary>열 참조가 <paramref name="end"/> 에서 끝나면 그 첫 토큰. 아니면 -1.</summary>
    private static int ColumnRefStart(TuningSql t, int end)
    {
        if (!t.IsName(end))
        {
            return -1;
        }

        var start = end;
        while (t.IsPunct(start - 1, '.') && t.IsName(start - 2))
        {
            start -= 2;
        }

        return t.IsPunct(start - 1, '.') ? -1 : start;
    }

    /// <summary><paramref name="k"/> 가 조건 하나의 앞 경계(WHERE·AND·OR·ON·( 등)인가.</summary>
    private static bool IsConditionStart(TuningSql t, int k) =>
        k < 0 || t.IsPunct(k, '(') || t.Is(k, "WHERE") || t.Is(k, "AND") || t.Is(k, "OR") || t.Is(k, "ON") || t.Is(k, "HAVING") || t.Is(k, "NOT") || t.Is(k, "WHEN");

    /// <summary>
    /// 토큰 <paramref name="i"/> 가 조건(WHERE·ON·HAVING·AND·OR 뒤) 안에 있는가. SELECT 목록·ORDER BY·GROUP BY·SET 은 아니다.
    /// </summary>
    private static bool InPredicate(TuningSql t, int i)
    {
        var d = t.Depth[i];
        for (var k = i - 1; k >= 0; k--)
        {
            if (t.Depth[k] > d)
            {
                continue;
            }

            if (t.Depth[k] < d)
            {
                d = t.Depth[k];
                continue;
            }

            if (t.Is(k, "WHERE") || t.Is(k, "ON") || t.Is(k, "HAVING") || t.Is(k, "AND") || t.Is(k, "OR"))
            {
                return true;
            }

            if (t.Is(k, "SELECT") || t.Is(k, "BY") || t.Is(k, "SET") || t.Is(k, "THEN") || t.Is(k, "ELSE") || t.Is(k, "WHEN") || t.Is(k, "INTO")
                || t.Is(k, "VALUES") || t.Is(k, "FROM") || t.Is(k, "RETURNING"))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>비교 오른쪽 식의 끝(같은 깊이의 AND·OR·절 낱말·쉼표·닫는 괄호 앞).</summary>
    private static int ConditionEnd(TuningSql t, int from, int depth)
    {
        var k = from;
        for (; k < t.Count; k++)
        {
            if (t.Depth[k] < depth)
            {
                break;
            }

            if (t.Depth[k] == depth && (t.Is(k, "AND") || t.Is(k, "OR") || t.IsPunct(k, ',') || t.IsPunct(k, ')') || t.IsPunct(k, ';')
                || t.Is(k, "GROUP") || t.Is(k, "ORDER") || t.Is(k, "HAVING") || t.Is(k, "UNION") || t.Is(k, "MINUS") || t.Is(k, "INTERSECT")
                || t.Is(k, "CONNECT") || t.Is(k, "START") || t.Is(k, "FETCH") || t.Is(k, "FOR") || t.Is(k, "THEN") || t.Is(k, "JOIN")
                || t.Is(k, "LEFT") || t.Is(k, "RIGHT") || t.Is(k, "INNER") || t.Is(k, "FULL") || t.Is(k, "CROSS") || t.Is(k, "WHERE")))
            {
                break;
            }
        }

        return k - 1;
    }

    /// <summary>TO_CHAR 날짜 형식이 하루·한 달·한 해를 뜻하면 다음 경계 식(<c>{0}</c> = 아래 경계)과 필요한 TRUNC 단위. 시각이 든 형식·모르는 형식이면 null.</summary>
    private static (string High, string? TruncUnit)? DateFormatStep(string formatLiteral) => PlainLiteral(formatLiteral)?.ToUpperInvariant() switch
    {
        "YYYYMMDD" or "YYYY-MM-DD" or "YYYY/MM/DD" or "YYYY.MM.DD" => ("{0} + 1", null),
        "YYYYMM" or "YYYY-MM" or "YYYY/MM" or "YYYY.MM" => ("ADD_MONTHS({0}, 1)", null),
        // TO_DATE('2026', 'YYYY') 는 그해 「이번 달」 1 일이라 해 첫날로 자른다.
        "YYYY" => ("ADD_MONTHS({0}, 12)", "'YYYY'"),
        _ => null,
    };

    /// <summary>리터럴 값이 형식과 같은 길이의 숫자·구분자 글인가(그래야 TO_DATE 가 같은 날을 만든다).</summary>
    private static bool LiteralFits(string literal, string formatLiteral) =>
        PlainLiteral(literal) is { } v && PlainLiteral(formatLiteral) is { } f && v.Length == f.Length
        && v.Zip(f).All(p => char.IsAsciiLetter(p.Second) ? char.IsAsciiDigit(p.First) : p.First == p.Second);

    /// <summary><c>'abc'</c> 의 안쪽. 따옴표가 겹친 글·q''·N'' 이면 null.</summary>
    private static string? PlainLiteral(string literal) =>
        literal.Length >= 2 && literal[0] == '\'' && literal[^1] == '\'' && literal.IndexOf('\'', 1, literal.Length - 2) < 0 ? literal[1..^1] : null;

    /// <summary>
    /// 문자형 열 <paramref name="column"/>(별칭 <paramref name="alias"/>)을 숫자와 비교하는 조건의 값 쪽 고침:
    /// <c>열 = 123</c>·<c>123 = 열</c>·<c>열 IN (1, 2)</c> 의 정수 리터럴은 <c>'123'</c>, 바인드는 <c>TO_CHAR(:b)</c>.
    /// 크기 비교(&lt; &gt;)·식 안의 열처럼 확실하지 않은 쓰임이 하나라도 있으면 빈 목록(반쯤 바꾸지 않는다).
    /// </summary>
    private static List<(int Start, int End, string With)> CharColumnEdits(TuningSql t, string? alias, string column)
    {
        var edits = new List<(int Start, int End, string With)>();
        if (!t.Balanced)
        {
            return edits;
        }

        string? Fix(int k) => k < 0 || k >= t.Count ? null : t.Kind(k) switch
        {
            TokenKind.Number when t.Word(k).All(char.IsAsciiDigit) => "'" + t.Word(k) + "'",
            TokenKind.Bind => "TO_CHAR(" + t.Word(k) + ")",
            _ => null,
        };

        for (var k = 0; k < t.Count; k++)
        {
            var end = t.ColumnRefEnd(k);
            if (end < 0 || t.IsPunct(k - 1, '.') || TuningSql.Normalize(t.Word(end)) != column || !InPredicate(t, k))
            {
                continue;
            }

            if (end > k && alias is not null && TuningSql.Normalize(t.Word(end - 2)) != alias)
            {
                continue;
            }

            if (t.Is(end + 1, "=") && ConditionEnd(t, end + 2, t.Depth[k]) == end + 2 && Fix(end + 2) is { } right)
            {
                edits.Add((t.Tokens[end + 2].Start, t.Tokens[end + 2].End, right));
            }
            else if (t.Is(k - 1, "=") && IsConditionStart(t, k - 3) && ConditionEnd(t, k, t.Depth[k]) == end && Fix(k - 2) is { } left)
            {
                edits.Add((t.Tokens[k - 2].Start, t.Tokens[k - 2].End, left));
            }
            else if (t.Is(end + 1, "IN") && t.IsPunct(end + 2, '(') && t.Partner[end + 2] > end + 3)
            {
                var close = t.Partner[end + 2];
                for (var v = end + 3; v < close; v += 2)
                {
                    if (Fix(v) is not { } item || !(t.IsPunct(v + 1, ',') || v + 1 == close))
                    {
                        return [];
                    }

                    edits.Add((t.Tokens[v].Start, t.Tokens[v].End, item));
                }

                k = close;
            }
            else
            {
                return []; // 크기 비교·식 안의 열·하위 질의: 문자 비교로 바꾸면 뜻이 달라질 수 있다
            }
        }

        return edits;
    }

    private static string Replace(string text, int start, int end, string with) => text[..start] + with + text[end..];

    /// <summary>겹치지 않는 고침 여럿을 뒤에서부터 적용한다.</summary>
    private static string Apply(string text, List<(int Start, int End, string With)> edits)
    {
        foreach (var e in edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.End))
        {
            text = Replace(text, e.Start, e.End, e.With);
        }

        return text;
    }
}
