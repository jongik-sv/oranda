using System.Text.RegularExpressions;
using Oranda.Language.Lexing;

namespace Oranda.Language.Tuning;

/// <summary>계획 조건 한 항의 종류(인덱스 후보가 열 순서를 정할 때 쓴다).</summary>
public enum PredicateKind
{
    /// <summary><c>열 = 값</c>(바인드·리터럴·식).</summary>
    Equality,

    /// <summary><c>열 IN (…)</c>, 같은 열의 등호를 OR 로 묶은 것.</summary>
    InList,

    /// <summary><c>&lt; &gt; &lt;= &gt;= BETWEEN</c>.</summary>
    Range,

    /// <summary><c>LIKE 'abc%'</c>(앞이 고정된 것).</summary>
    Like,

    /// <summary>두 별칭의 열끼리 비교(조인 조건).</summary>
    Join,

    /// <summary>열이 함수 안에 있다(<see cref="PredicateTerm.Function"/>). 일반 인덱스를 못 쓴다.</summary>
    Function,

    /// <summary>그 밖(&lt;&gt;·IS NULL·앞이 % 인 LIKE·OR 묶음 등).</summary>
    Other,
}

/// <summary>
/// 계획 조건(access/filter predicate) 한 항. 열 이름은 따옴표를 벗긴 그대로(보통 대문자).
/// </summary>
/// <param name="Alias">열을 한정한 별칭(<c>"LH"."EQP_ID"</c> 의 LH). 없으면 null.</param>
/// <param name="Column">열</param>
/// <param name="Kind">종류</param>
/// <param name="Text">항의 원문</param>
public sealed record PredicateTerm(string? Alias, string Column, PredicateKind Kind, string Text)
{
    /// <summary>열을 감싼 가장 안쪽 함수(대문자). <see cref="PredicateKind.Function"/> 일 때.</summary>
    public string? Function { get; init; }

    /// <summary>조인 상대 별칭(<see cref="PredicateKind.Join"/> 일 때).</summary>
    public string? OtherAlias { get; init; }

    /// <summary>조인 상대 열.</summary>
    public string? OtherColumn { get; init; }

    /// <summary>비교 연산자(대문자, 예: <c>=</c>·<c>&gt;=</c>·<c>LIKE</c>).</summary>
    public string? Operator { get; init; }
}

/// <summary>
/// DBMS_XPLAN·v$sql_plan 의 조건 글(<c>("LH"."EQP_ID"=:B1 AND "LH"."EVT_TIME"&gt;=:B2)</c>)을 항으로 나눈다(TSK-06-05, 순수 함수).
/// 최상위 AND 로 나누고, 같은 열 등호만 든 OR 묶음은 IN 목록으로 본다. 읽을 수 없는 조각은 건너뛴다(예외 없음).
/// </summary>
public static partial class PlanPredicates
{
    /// <summary>열을 형 변환하는 함수(계획에 이것이 열을 감싸면 암시적 형 변환 의심).</summary>
    public static readonly IReadOnlySet<string> ConversionFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "INTERNAL_FUNCTION", "TO_NUMBER", "TO_CHAR", "TO_DATE", "TO_TIMESTAMP", "TO_TIMESTAMP_TZ", "TO_BINARY_DOUBLE", "TO_BINARY_FLOAT",
        "TO_NCHAR", "SYS_OP_C2C",
    };

    private static readonly HashSet<string> RangeOps = new(StringComparer.OrdinalIgnoreCase) { "<", ">", "<=", ">=" };

    private static readonly HashSet<string> CompareOps = new(StringComparer.OrdinalIgnoreCase) { "=", "<>", "!=", "^=", "<", ">", "<=", ">=" };

    /// <summary>열처럼 보이지만 열이 아닌 이름.</summary>
    internal static readonly HashSet<string> PseudoColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "SYSDATE", "SYSTIMESTAMP", "CURRENT_DATE", "CURRENT_TIMESTAMP", "LOCALTIMESTAMP", "ROWNUM", "LEVEL", "USER", "UID", "NULL", "ROWID",
    };

    /// <summary>조건 글을 항으로 나눈다. null·빈 글은 빈 목록.</summary>
    public static IReadOnlyList<PredicateTerm> Parse(string? predicate)
    {
        var terms = new List<PredicateTerm>();
        if (string.IsNullOrWhiteSpace(predicate))
        {
            return terms;
        }

        var sql = new TuningSql(predicate);
        if (sql.Count == 0 || !sql.Balanced)
        {
            return terms;
        }

        Conjuncts(sql, 0, sql.Count - 1, terms);
        return terms;
    }

    /// <summary>여러 조건 글(access·filter)을 한꺼번에.</summary>
    public static IReadOnlyList<PredicateTerm> Parse(params string?[] predicates) =>
        predicates.SelectMany(p => Parse(p)).ToList();

    private static void Conjuncts(TuningSql sql, int from, int to, List<PredicateTerm> terms)
    {
        (from, to) = sql.Strip(from, to);
        if (from > to)
        {
            return;
        }

        var ands = sql.SplitAt(from, to, "AND");
        if (ands.Count > 1)
        {
            foreach (var (a, b) in ands)
            {
                Conjuncts(sql, a, b, terms);
            }

            return;
        }

        var ors = sql.SplitAt(from, to, "OR");
        if (ors.Count > 1)
        {
            var parts = ors.Select(p => Term(sql, p.From, p.To)).ToList();
            if (parts.All(p => p is { Kind: PredicateKind.Equality })
                && parts.Select(p => (p!.Alias, p.Column)).Distinct().Count() == 1)
            {
                var first = parts[0]!;
                terms.Add(first with { Kind = PredicateKind.InList, Text = sql.Slice(from, to), Operator = "IN" });
            }
            else
            {
                // OR 묶음 안의 열은 인덱스 앞 열로 쓰지 않는다. 함수·형 변환 표시는 남긴다.
                foreach (var p in parts.Where(p => p is not null))
                {
                    terms.Add(p!.Kind == PredicateKind.Function ? p : p with { Kind = PredicateKind.Other });
                }
            }

            return;
        }

        if (Term(sql, from, to) is { } term)
        {
            terms.Add(term);
        }
    }

    private static PredicateTerm? Term(TuningSql sql, int from, int to)
    {
        (from, to) = sql.Strip(from, to);
        if (from > to)
        {
            return null;
        }

        var text = sql.Slice(from, to);
        var d = sql.Depth[from];
        var op = -1;
        for (var k = from; k <= to; k++)
        {
            if (sql.Depth[k] != d)
            {
                continue;
            }

            var w = sql.Word(k);
            if ((sql.Kind(k) == TokenKind.Operator && CompareOps.Contains(w)) || sql.Is(k, "LIKE") || sql.Is(k, "IN") || sql.Is(k, "BETWEEN") || sql.Is(k, "IS")
                || sql.Is(k, "LIKEC") || sql.Is(k, "LIKE2") || sql.Is(k, "LIKE4"))
            {
                op = k;
                break;
            }
        }

        if (op < 0)
        {
            // INTERNAL_FUNCTION("LH"."LOT_ID") 처럼 비교 없이 함수만 있는 항
            return FunctionColumn(sql, from, to) is { } f ? new PredicateTerm(f.Alias, f.Column, PredicateKind.Function, text) { Function = f.Function } : null;
        }

        var opStart = op;
        if (sql.Is(op - 1, "NOT") && op - 1 >= from)
        {
            opStart = op - 1; // NOT LIKE · NOT IN · NOT BETWEEN
        }

        var negated = opStart != op || (sql.Is(op, "IS") && sql.Is(op + 1, "NOT"));
        var opWord = sql.Word(op).ToUpperInvariant();
        var left = (From: from, To: opStart - 1);
        var right = (From: op + 1, To: to);
        var leftCol = Column(sql, left.From, left.To);
        var rightCol = Column(sql, right.From, right.To);
        if (leftCol is null && rightCol is not null && sql.Kind(op) == TokenKind.Operator)
        {
            // :B1 = "A"."X" → "A"."X" = :B1
            (leftCol, rightCol) = (rightCol, null);
            (left, right) = (right, left);
            opWord = opWord switch { "<" => ">", ">" => "<", "<=" => ">=", ">=" => "<=", _ => opWord };
        }

        if (leftCol is null)
        {
            var leftFn = FunctionColumn(sql, left.From, left.To);
            var rightFn = FunctionColumn(sql, right.From, right.To);
            var fn = leftFn ?? rightFn;
            return fn is null ? null : new PredicateTerm(fn.Value.Alias, fn.Value.Column, PredicateKind.Function, text) { Function = fn.Value.Function, Operator = opWord };
        }

        var (alias, column) = leftCol.Value;
        if (rightCol is { } other && sql.Kind(op) == TokenKind.Operator)
        {
            if (opWord == "=" && !string.Equals(other.Alias, alias, StringComparison.Ordinal) && other.Alias is not null && alias is not null)
            {
                return new PredicateTerm(alias, column, PredicateKind.Join, text) { OtherAlias = other.Alias, OtherColumn = other.Column, Operator = opWord };
            }

            return new PredicateTerm(alias, column, PredicateKind.Other, text) { Operator = opWord };
        }

        if (FunctionColumn(sql, right.From, right.To) is { } rf && sql.Kind(op) == TokenKind.Operator && opWord == "=" && rf.Alias != alias && rf.Alias is not null && alias is not null)
        {
            // "A"."X" = INTERNAL_FUNCTION("B"."Y"): 조인이지만 상대 열이 변환된다 → 상대 열 쪽 형 변환 표시
            return new PredicateTerm(rf.Alias, rf.Column, PredicateKind.Function, text) { Function = rf.Function, Operator = opWord };
        }

        var kind = negated ? PredicateKind.Other : opWord switch
        {
            "=" => PredicateKind.Equality,
            "IN" => PredicateKind.InList,
            "BETWEEN" => PredicateKind.Range,
            "LIKE" or "LIKEC" or "LIKE2" or "LIKE4" => LeadingWildcard(sql, right.From, right.To) ? PredicateKind.Other : PredicateKind.Like,
            _ when RangeOps.Contains(opWord) => PredicateKind.Range,
            _ => PredicateKind.Other,
        };
        return new PredicateTerm(alias, column, kind, text) { Operator = negated && opWord != "IS" ? "NOT " + opWord : opWord };
    }

    private static bool LeadingWildcard(TuningSql sql, int from, int to)
    {
        if (from > to)
        {
            return true;
        }

        if (sql.Kind(from) == TokenKind.StringLiteral)
        {
            var lit = sql.Word(from);
            return lit.Length < 3 || lit[1] is '%' or '_';
        }

        return false; // 바인드: 값을 모른다 — 앞이 고정됐다고 본다
    }

    /// <summary><paramref name="from"/>~<paramref name="to"/> 가 열 참조 하나뿐이면 (별칭, 열).</summary>
    private static (string? Alias, string Column)? Column(TuningSql sql, int from, int to)
    {
        (from, to) = sql.Strip(from, to);
        var end = sql.ColumnRefEnd(from);
        if (end < 0 || end != to || (end == from && PseudoColumns.Contains(sql.Word(from))))
        {
            return null;
        }

        return end == from
            ? (null, TuningSql.Normalize(sql.Word(from)))
            : (TuningSql.Normalize(sql.Word(end - 2)), TuningSql.Normalize(sql.Word(end)));
    }

    /// <summary>
    /// 함수 호출 안에 든 열(<c>TRUNC(INTERNAL_FUNCTION("LH"."EVT_TIME"))</c> → LH.EVT_TIME, INTERNAL_FUNCTION).
    /// 함수 이름은 열을 바로 감싼 것. 열이 없으면 null.
    /// </summary>
    private static (string? Alias, string Column, string Function)? FunctionColumn(TuningSql sql, int from, int to)
    {
        (from, to) = sql.Strip(from, to);
        for (var k = from; k < to; k++)
        {
            if (sql.Kind(k) != TokenKind.Identifier || !sql.IsPunct(k + 1, '(') || sql.Partner[k + 1] < 0)
            {
                continue;
            }

            // 열이 바로 들어 있지 않으면 안쪽 함수로 내려간다(다음 반복이 k+2 부터 본다).
            var arg = k + 2;
            var end = sql.ColumnRefEnd(arg);
            if (end >= 0 && (sql.IsPunct(end + 1, ')') || sql.IsPunct(end + 1, ',')) && !(end == arg && PseudoColumns.Contains(sql.Word(arg))))
            {
                var alias = end == arg ? null : TuningSql.Normalize(sql.Word(end - 2));
                return (alias, TuningSql.Normalize(sql.Word(end)), sql.Word(k).ToUpperInvariant());
            }
        }

        return null;
    }

    /// <summary>계획 글의 「Query Block Name / Object Alias」 절에서 단계 Id → 별칭(@ 앞, 대문자). 없으면 빈 사전.</summary>
    public static IReadOnlyDictionary<int, string> AliasesFromPlanText(string? planText)
    {
        var map = new Dictionary<int, string>();
        if (string.IsNullOrEmpty(planText))
        {
            return map;
        }

        foreach (Match m in AliasLine().Matches(planText))
        {
            if (int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id))
            {
                map[id] = m.Groups[2].Value.Trim('"').ToUpperInvariant();
            }
        }

        return map;
    }

    [GeneratedRegex("^\\s*(\\d+)\\s+-\\s+\\S+\\s*/\\s*(\"[^\"@]+\"|[^\\s@\"]+)@", RegexOptions.Multiline)]
    private static partial Regex AliasLine();
}
