using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Text;
using Oranda.Language.Lexing;

namespace Oranda.Language.Lint;

/// <summary>메모리 캐시에 이미 있는 표 정의만 돌려준다(DB 를 부르지 않는다). 없으면 null.</summary>
internal interface ILintCatalog
{
    TableInfo? Table(string? owner, string name);
}

/// <summary>점검 규칙 8개(TSK-13-04). 모두 토큰 구조만으로 판정하고, ③⑥⑦ 만 <see cref="ILintCatalog"/> 가 있어야 한다.</summary>
internal static class LintRules
{
    public const string UpdateSubqueryNoWhere = "UPDATE_SUBQUERY_NO_WHERE";
    public const string RownumOrderBy = "ROWNUM_ORDER_BY";
    public const string NotInNullable = "NOT_IN_NULLABLE";
    public const string CartesianJoin = "CARTESIAN_JOIN";
    public const string EqualsNull = "EQUALS_NULL";
    public const string ImplicitConversion = "IMPLICIT_CONVERSION";
    public const string FunctionOnIndexedColumn = "FUNCTION_ON_INDEXED_COLUMN";
    public const string PartialOuterJoin = "PARTIAL_OUTER_JOIN";

    private static readonly HashSet<string> CompareOps = new(StringComparer.Ordinal) { "=", "<>", "!=", "^=", "<", ">", "<=", ">=" };

    /// <summary>뒤에 괄호가 오지만 함수가 아닌 단어.</summary>
    private static readonly HashSet<string> NotFunctions = new(StringComparer.Ordinal)
    {
        "AND", "OR", "NOT", "IN", "ANY", "ALL", "SOME", "EXISTS", "PRIOR", "WHEN", "THEN", "ELSE", "ON", "USING", "WHERE", "SELECT", "CASE",
    };

    private static readonly HashSet<string> CharTypes = new(StringComparer.OrdinalIgnoreCase) { "VARCHAR2", "VARCHAR", "CHAR", "NVARCHAR2", "NCHAR" };

    /// <summary>문장 하나를 점검한다. 범위는 문장 본문 안 오프셋.</summary>
    public static List<Diagnostic> Run(LintModel model, ILintCatalog? catalog)
    {
        var ctx = new Ctx(model, catalog);
        foreach (var b in model.Blocks)
        {
            if (b.Verb is not ("SELECT" or "UPDATE" or "DELETE"))
            {
                continue;
            }

            ctx.Refs[b] = LintModel.FromRefs(b);
        }

        foreach (var b in model.Blocks)
        {
            if (b.Verb is not ("SELECT" or "UPDATE" or "DELETE"))
            {
                continue;
            }

            UpdateWithoutWhere(ctx, b);
            RownumWithOrderBy(ctx, b);
            NotInNullableColumn(ctx, b);
            Cartesian(ctx, b);
            EqualsNullCompare(ctx, b);
            CharCompareNumber(ctx, b);
            FunctionOnIndex(ctx, b);
            PartialOuter(ctx, b);
        }

        return ctx.Out;
    }

    private sealed class Ctx(LintModel model, ILintCatalog? catalog)
    {
        public LintModel Model { get; } = model;

        public ILintCatalog? Catalog { get; } = catalog;

        public Dictionary<Block, List<FromRef>> Refs { get; } = [];

        public List<Diagnostic> Out { get; } = [];

        public void Report(string rule, int start, int end, string message)
            => Out.Add(new Diagnostic(new TextRange(start, Math.Max(1, end - start)), DiagnosticSeverity.Warning, message) { Code = "LINT." + rule });

        public void Report(string rule, Tok first, Tok last, string message) => Report(rule, first.Start, last.End, message);

        public List<FromRef> RefsOf(Block b)
        {
            if (!Refs.TryGetValue(b, out var r))
            {
                r = b.Verb is "SELECT" or "UPDATE" or "DELETE" ? LintModel.FromRefs(b) : [];
                Refs[b] = r;
            }

            return r;
        }

        /// <summary>열 참조가 가리키는 표(자기 블록, 없으면 바깥 블록들). 표 정의가 캐시에 없으면 null.</summary>
        public (TableInfo Table, ColumnInfo? Column)? Resolve(Block b, ColRef c)
        {
            if (Catalog is null)
            {
                return null;
            }

            for (var cur = b; cur is not null; cur = cur.Parent)
            {
                var refs = RefsOf(cur);
                FromRef? hit = null;
                if (c.Qualifier is { } q)
                {
                    hit = refs.FirstOrDefault(r => r.Alias == q) ?? refs.FirstOrDefault(r => r.Name == q && !r.IsSub);
                }
                else if (cur == b && refs.Count == 1)
                {
                    hit = refs[0];
                }

                if (hit is null)
                {
                    if (c.Qualifier is null)
                    {
                        return null;
                    }

                    continue;
                }

                if (hit.IsSub || Catalog.Table(hit.Owner, hit.Name) is not { } table)
                {
                    return null;
                }

                return (table, table.Columns.FirstOrDefault(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase)));
            }

            return null;
        }
    }

    // ① 상관 UPDATE SET c = (SELECT …) 에 WHERE 없음 -------------------------------------------

    private static void UpdateWithoutWhere(Ctx ctx, Block b)
    {
        if (b.Verb != "UPDATE" || LintModel.Clause(b, "SET") is not { } set || LintModel.Clause(b, "WHERE") is not null)
        {
            return;
        }

        var own = ctx.RefsOf(b).Select(r => r.Alias).Concat(ctx.RefsOf(b).Select(r => r.Name)).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
        for (int i = set.Start; i < set.End; i++)
        {
            var t = b.Toks[i];
            if (t.Sub is not { } child || i == 0 || b.Toks[i - 1].Text != "=")
            {
                continue;
            }

            if (AlwaysOneRow(child))
            {
                continue; // COUNT(*) 나 NVL(집계, 기본값) 은 짝이 없어도 NULL 이 아니다
            }

            if (UsesOuterAlias(ctx, child, own))
            {
                ctx.Report(UpdateSubqueryNoWhere, t.Start, t.End,
                    "WHERE 가 없어 하위 질의와 짝이 없는 행의 열이 NULL 이 됩니다. 바깥 UPDATE 에도 EXISTS 나 같은 조건의 WHERE 를 붙이세요.");
            }
        }
    }

    private static bool AlwaysOneRow(Block sub)
    {
        if (LintModel.Clause(sub, "GROUP") is not null || LintModel.Clause(sub, "SELECT") is not { } sel || sel.Start + 1 >= sel.End)
        {
            return false;
        }

        var first = sub.Toks[sel.Start + 1].Up;
        return first is "COUNT" or "NVL" or "COALESCE";
    }

    private static bool UsesOuterAlias(Ctx ctx, Block sub, HashSet<string> outerNames)
    {
        var inner = ctx.RefsOf(sub).Select(r => r.Alias).ToHashSet(StringComparer.Ordinal);
        foreach (var b in ctx.Model.Blocks)
        {
            if (!IsWithin(b, sub))
            {
                continue;
            }

            foreach (var c in LintModel.ColumnRefs(b, 0, b.Toks.Count))
            {
                if (c.Qualifier is { } q && outerNames.Contains(q) && !inner.Contains(q))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsWithin(Block b, Block ancestor)
    {
        for (var c = b; c is not null; c = c.Parent)
        {
            if (c == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    // ② ROWNUM <= n 과 ORDER BY 가 같은 단계 ----------------------------------------------------

    private static void RownumWithOrderBy(Ctx ctx, Block b)
    {
        if (b.Verb != "SELECT" || LintModel.Clause(b, "WHERE") is not { } where || LintModel.Clause(b, "ORDER") is null)
        {
            return;
        }

        var refs = ctx.RefsOf(b);
        if (refs.Count == 1 && refs[0].IsSub && refs[0].First.Sub is { } view && LintModel.Clause(view, "ORDER") is not null)
        {
            return; // 안쪽에서 이미 정렬한 뒤 ROWNUM 으로 자르는 정상 Top-N
        }

        for (int i = where.Start; i + 1 < where.End; i++)
        {
            if (b.Toks[i].IsWordUp("ROWNUM") && b.Toks[i + 1].Kind == TokenKind.Operator && b.Toks[i + 1].Text is "<=" or "<" or "=")
            {
                ctx.Report(RownumOrderBy, b.Toks[i], b.Toks[i + 1],
                    "ROWNUM 은 ORDER BY 보다 먼저 적용돼 정렬 전의 행이 잘립니다. 정렬을 안쪽 질의로 내리고 바깥에서 ROWNUM 을 거세요(또는 FETCH FIRST).");
            }
        }
    }

    // ③ NOT IN (하위 질의) 열이 NULL 허용 ------------------------------------------------------

    private static void NotInNullableColumn(Ctx ctx, Block b)
    {
        if (ctx.Catalog is null)
        {
            return;
        }

        for (int i = 2; i < b.Toks.Count; i++)
        {
            var t = b.Toks[i];
            if (t.Sub is not { } child || !b.Toks[i - 1].IsWordUp("IN") || !b.Toks[i - 2].IsWordUp("NOT"))
            {
                continue;
            }

            if (LintModel.Clause(child, "SELECT") is not { } sel)
            {
                continue;
            }

            int s = sel.Start + 1;
            if (s < sel.End && b.Toks.Count > 0 && child.Toks[s].IsWordUp2("DISTINCT", "ALL"))
            {
                s++;
            }

            var cols = LintModel.ColumnRefs(child, s, sel.End);
            if (cols.Count != 1 || cols[0].First != s || cols[0].Last != sel.End - 1)
            {
                continue;
            }

            if (ctx.Resolve(child, cols[0]) is not { Column: { IsNullable: true } } || HasNotNullFilter(child, cols[0].Name))
            {
                continue;
            }

            ctx.Report(NotInNullable, b.Toks[i - 2].Start, t.End,
                $"하위 질의의 {cols[0].Name} 에 NULL 이 하나라도 있으면 결과가 항상 비어 버립니다. NOT EXISTS 로 바꾸거나 하위 질의에 {cols[0].Name} IS NOT NULL 을 더하세요.");
        }
    }

    private static bool HasNotNullFilter(Block b, string column)
    {
        for (int i = 0; i + 2 < b.Toks.Count; i++)
        {
            if (!b.Toks[i].IsWord || b.Toks[i].Up != column)
            {
                continue;
            }

            // 열 IS NOT NULL, 또는 비교·LIKE·IN·BETWEEN 이 이미 NULL 행을 뺀다.
            var n = b.Toks[i + 1];
            if (n.IsWordUp("IS") && b.Toks[i + 2].IsWordUp("NOT") || IsComparison(n))
            {
                return true;
            }
        }

        return false;
    }

    // ④ 조인 조건이 빠진 표(카테시안 곱) --------------------------------------------------------

    private static void Cartesian(Ctx ctx, Block b)
    {
        if (b.Verb != "SELECT")
        {
            return;
        }

        var refs = ctx.RefsOf(b);
        int units = refs.Count == 0 ? 0 : refs.Max(r => r.Unit) + 1;
        if (units < 2)
        {
            return;
        }

        var parent = Enumerable.Range(0, units).ToArray();
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        var unitOf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in refs)
        {
            unitOf.TryAdd(r.Alias, r.Unit);
        }

        if (LintModel.Clause(b, "WHERE") is { } where)
        {
            foreach (var (cs, ce) in LintModel.Conjuncts(b, where.Start + 1, where.End))
            {
                var cols = LintModel.ColumnRefs(b, cs, ce);
                var touched = cols.Where(c => c.Qualifier is not null && unitOf.ContainsKey(c.Qualifier)).Select(c => unitOf[c.Qualifier!]).Distinct().ToList();
                if (cols.Count >= 2 && cols.Any(c => c.Qualifier is null) && touched.Count < 2)
                {
                    return; // 한정 없는 열끼리 비교: 어느 표를 잇는지 몰라 판정하지 않는다
                }

                for (int k = 1; k < touched.Count; k++)
                {
                    parent[Find(touched[k])] = Find(touched[0]);
                }
            }
        }

        if (LintModel.Clause(b, "CONNECT") is not null)
        {
            return;
        }

        int root0 = Find(0);
        for (int u = 1; u < units; u++)
        {
            if (Find(u) != root0)
            {
                var r = refs.First(x => x.Unit == u);
                ctx.Report(CartesianJoin, r.First.Start, r.First.End,
                    $"{(r.IsSub ? "이 인라인 뷰" : r.Alias)} 와 다른 표를 잇는 조인 조건이 없어 행이 곱으로 늘어납니다. WHERE 에 조인 조건을 더하세요(의도한 곱이면 CROSS JOIN 으로 적으세요).");
                return;
            }
        }
    }

    // ⑤ = NULL, != NULL, <> NULL -----------------------------------------------------------------

    private static void EqualsNullCompare(Ctx ctx, Block b)
    {
        var set = b.Verb == "UPDATE" ? LintModel.Clause(b, "SET") : null;
        for (int i = 0; i < b.Toks.Count; i++)
        {
            var t = b.Toks[i];
            if (t.Kind != TokenKind.Operator || t.Text is not ("=" or "<>" or "!=" or "^="))
            {
                continue;
            }

            if (set is { } sr && i >= sr.Start && i < sr.End && t.Depth == 0)
            {
                continue; // UPDATE … SET c = NULL 은 대입
            }

            bool after = i + 1 < b.Toks.Count && b.Toks[i + 1].IsWordUp("NULL");
            bool before = i > 0 && b.Toks[i - 1].IsWordUp("NULL");
            if (!after && !before)
            {
                continue;
            }

            var first = before ? b.Toks[i - 1] : i > 0 && b.Toks[i - 1].IsWord && !b.Toks[i - 1].IsPunct('.') ? b.Toks[i - 1] : t;
            var last = after ? b.Toks[i + 1] : t;
            ctx.Report(EqualsNull, first, last,
                $"NULL 과 {t.Text} 로 비교하면 결과가 항상 알 수 없음(UNKNOWN)이라 행이 걸리지 않습니다. {(t.Text == "=" ? "IS NULL" : "IS NOT NULL")} 을 쓰세요.");
        }
    }

    // ⑥ 문자 열을 숫자와 비교 --------------------------------------------------------------------

    private static void CharCompareNumber(Ctx ctx, Block b)
    {
        if (ctx.Catalog is null || LintModel.Clause(b, "WHERE") is not { } where)
        {
            return;
        }

        foreach (var c in LintModel.ColumnRefs(b, where.Start + 1, where.End))
        {
            int rightOp = c.Last + 1;
            bool right = rightOp + 1 < where.End && b.Toks[rightOp].Kind == TokenKind.Operator && CompareOps.Contains(b.Toks[rightOp].Text)
                && b.Toks[rightOp + 1].Kind == TokenKind.Number && !IsArithmetic(b, rightOp + 2, where.End);
            bool left = c.First - 2 > where.Start && b.Toks[c.First - 1].Kind == TokenKind.Operator && CompareOps.Contains(b.Toks[c.First - 1].Text)
                && b.Toks[c.First - 2].Kind == TokenKind.Number && !IsArithmetic(b, c.First - 3, c.First - 2, backward: true);
            if (!right && !left)
            {
                continue;
            }

            if (ctx.Resolve(b, c) is not { Column: { } col } || !CharTypes.Contains(col.DataType))
            {
                continue;
            }

            int start = left ? b.Toks[c.First - 2].Start : b.Toks[c.First].Start;
            int end = right ? b.Toks[rightOp + 1].End : b.Toks[c.Last].End;
            ctx.Report(ImplicitConversion, start, end,
                $"문자 열 {c.Name}({col.DataType})을 숫자와 비교하면 열 값이 숫자로 바뀌어 인덱스를 못 쓰고 숫자가 아닌 값에서 ORA-01722 가 납니다. 숫자를 따옴표로 감싸 문자로 비교하세요.");
        }
    }

    private static bool IsArithmetic(Block b, int index, int end, bool backward = false)
    {
        if (index < 0 || index >= b.Toks.Count || (!backward && index >= end))
        {
            return false;
        }

        var t = b.Toks[index];
        return t.Kind == TokenKind.Operator && t.Text is "+" or "-" or "*" or "/" or "||" or "%";
    }

    // ⑦ 인덱스 열에 함수 -------------------------------------------------------------------------

    private static void FunctionOnIndex(Ctx ctx, Block b)
    {
        if (ctx.Catalog is null || LintModel.Clause(b, "WHERE") is not { } where)
        {
            return;
        }

        for (int i = where.Start + 1; i + 1 < where.End; i++)
        {
            var f = b.Toks[i];
            if (!f.IsWord || !b.Toks[i + 1].IsPunct('(') || f.Kind == TokenKind.QuotedIdent || b.Toks[i - 1].IsPunct('.') || NotFunctions.Contains(f.Up)
                || b.Toks[i - 1].Kind == TokenKind.Operator && b.Toks[i - 1].Text is "+" or "-" or "*" or "/" or "||")
            {
                continue;
            }

            int close = i + 2;
            while (close < where.End && !(b.Toks[close].IsPunct(')') && b.Toks[close].Depth == b.Toks[i + 1].Depth))
            {
                close++;
            }

            if (close + 1 >= where.End || !IsComparison(b.Toks[close + 1]))
            {
                continue;
            }

            foreach (var c in LintModel.ColumnRefs(b, i + 2, close))
            {
                if (ctx.Resolve(b, c) is { } r && LeadsAnIndex(r.Table, c.Name))
                {
                    ctx.Report(FunctionOnIndexedColumn, f.Start, b.Toks[close].End,
                        $"인덱스가 있는 열 {c.Name} 에 {f.Text} 함수를 씌우면 그 인덱스를 못 씁니다. 함수를 상수 쪽으로 옮기세요(예: TO_CHAR(dt,…) = '…' → dt >= TO_DATE(…) AND dt < …).");
                    break;
                }
            }

            i = close;
        }
    }

    private static bool IsComparison(Tok t)
        => t.Kind == TokenKind.Operator && CompareOps.Contains(t.Text) || t.Kind is TokenKind.Identifier or TokenKind.Keyword && t.Up is "LIKE" or "BETWEEN" or "IN";

    private static bool LeadsAnIndex(TableInfo table, string column)
    {
        // 함수 기반 인덱스(열 목록에 식이 든 것)가 있으면 함수를 쓰는 쪽이 맞을 수 있어 판정하지 않는다.
        if (table.Indexes.Any(ix => ix.Columns.Any(c => c.Contains('(', StringComparison.Ordinal))))
        {
            return false;
        }

        return table.Indexes.Any(ix => ix.Columns.Count > 0 && string.Equals(ix.Columns[0], column, StringComparison.OrdinalIgnoreCase));
    }

    // ⑧ (+) 를 일부 조건에만 -------------------------------------------------------------------

    private static void PartialOuter(Ctx ctx, Block b)
    {
        if (LintModel.Clause(b, "WHERE") is not { } where)
        {
            return;
        }

        var byPair = new Dictionary<(string, string), List<(int S, int E, bool Plus)>>();
        foreach (var (cs, ce) in LintModel.Conjuncts(b, where.Start + 1, where.End))
        {
            var cols = LintModel.ColumnRefs(b, cs, ce);
            var quals = cols.Where(c => c.Qualifier is not null).Select(c => c.Qualifier!).Distinct().Order(StringComparer.Ordinal).ToList();
            if (quals.Count != 2)
            {
                continue;
            }

            var key = (quals[0], quals[1]);
            if (!byPair.TryGetValue(key, out var list))
            {
                byPair[key] = list = [];
            }

            list.Add((cs, ce, cols.Any(c => c.OuterPlus)));
        }

        foreach (var list in byPair.Values)
        {
            if (!list.Any(x => x.Plus))
            {
                continue;
            }

            foreach (var (s, e, plus) in list)
            {
                if (!plus)
                {
                    ctx.Report(PartialOuterJoin, b.Toks[s].Start, b.Toks[e - 1].End,
                        "같은 두 표의 다른 조인 조건에는 (+) 가 있는데 이 조건에는 없어 바깥 조인이 안쪽 조인으로 바뀝니다. 이 조건의 바깥 쪽 열에도 (+) 를 붙이세요.");
                }
            }
        }
    }
}
