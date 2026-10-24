using System.Globalization;
using Oranda.Core.Plans;
using Oranda.Language.Lexing;

namespace Oranda.Language.Tuning;

/// <summary>힌트 대안 하나(TN-11). <see cref="Hint"/> 는 주석 표시 없는 본문(<c>LEADING(lm lh) USE_NL(lh)</c>).</summary>
public sealed record HintAdvice(string Hint, string Meaning)
{
    /// <summary>쓸 수 있는가(병렬은 설정에서 켜야 한다).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>못 쓰는 이유. 쓸 수 있으면 null.</summary>
    public string? DisabledReason { get; init; }

    /// <summary>문장에 넣을 모양(<c>/*+ … */</c>).</summary>
    public string Comment => "/*+ " + Hint + " */";
}

/// <summary>힌트 대안 설정.</summary>
public sealed record HintAdvisorOptions
{
    /// <summary>PARALLEL 힌트를 쓸 수 있게 한다(기본 꺼짐 — 운영 자원을 많이 쓴다).</summary>
    public bool AllowParallel { get; init; }

    /// <summary>PARALLEL 차수.</summary>
    public int ParallelDegree { get; init; } = 4;

    public bool English { get; init; }
}

/// <summary>
/// 힌트 대안(TSK-06-05, TN-11). 순수 함수: 계획의 표 접근 순서·조인 방법과 표 별칭으로 LEADING·USE_NL·USE_HASH·INDEX·FULL·PARALLEL 조합을 만든다.
/// 별칭은 계획 글의 Query Block·별칭 절(+ALIAS), 없으면 SQL 글의 FROM 절에서 찾고, SQL 에 쓴 철자를 따른다. TSK-06-07 보고서도 이것을 쓴다.
/// </summary>
public static class HintAdvisor
{
    private sealed record Access(int StepId, string Table, string Alias, bool Full, long? Cost);

    /// <summary>힌트 대안. <paramref name="indexAdvice"/> 가 있으면 겹친 기존 인덱스로 INDEX 힌트를 만든다.</summary>
    public static IReadOnlyList<HintAdvice> Suggest(ExecutionPlan plan, string? sql, IReadOnlyList<IndexAdvice>? indexAdvice = null, HintAdvisorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        options ??= new HintAdvisorOptions();
        var en = options.English;
        var accesses = Accesses(plan, sql);
        var hints = new List<HintAdvice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(HintAdvice h)
        {
            if (seen.Add(h.Hint))
            {
                hints.Add(h);
            }
        }

        // 1. 전체 읽기 표 + 조건 열이 겹치는 기존 인덱스 → INDEX
        foreach (var a in indexAdvice ?? [])
        {
            if (a.Basis != IndexBasis.Filter || a.Candidate.OverlappingIndex is not { } ix
                || a.Overlap is not (IndexOverlapKind.Covered or IndexOverlapKind.Extends or IndexOverlapKind.SharesLeading))
            {
                continue;
            }

            var access = accesses.FirstOrDefault(x => x.Full && string.Equals(x.Table, a.Candidate.TableName, StringComparison.OrdinalIgnoreCase));
            var alias = access?.Alias ?? SqlSpelling(sql, a.Alias) ?? a.Candidate.TableName;
            Add(new HintAdvice($"INDEX({alias} {ix})", en
                ? $"Read {a.Candidate.TableName} through {ix} ({string.Join(", ", a.OverlappingColumns)})"
                : $"{string.Join("·", a.OverlappingColumns)} 인덱스({ix})로 {a.Candidate.TableName} 읽기"));
        }

        // 2. 인덱스로 읽는 표 → FULL
        foreach (var a in accesses.Where(x => !x.Full).Take(2))
        {
            Add(new HintAdvice($"FULL({a.Alias})", en ? $"Read all of {a.Table} (better when many rows qualify)" : $"{a.Table} 전체 읽기(많은 행을 읽을 때 유리)"));
        }

        // 3. 조인 순서·방법
        var tables = accesses.GroupBy(x => x.Alias, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (tables.Count >= 2)
        {
            var a = tables[0];
            var b = tables[1];
            var others = string.Concat(tables.Skip(2).Select(x => " " + x.Alias));
            var current = JoinMethod(plan, b.StepId);
            var options4 = new (string Lead1, string Lead2, string Method, string Inner, string MeaningKo, string MeaningEn)[]
            {
                (b.Alias, a.Alias, "USE_NL", a.Alias, $"{b.Table} 먼저, {a.Table} 를 중첩 루프로", $"{b.Table} first, {a.Table} by nested loops"),
                (a.Alias, b.Alias, "USE_HASH", b.Alias, $"{a.Table} 먼저, 해시 조인", $"{a.Table} first, hash join"),
                (b.Alias, a.Alias, "USE_HASH", a.Alias, $"{b.Table} 먼저, 해시 조인", $"{b.Table} first, hash join"),
                (a.Alias, b.Alias, "USE_NL", b.Alias, $"{a.Table} 먼저, {b.Table} 를 중첩 루프로", $"{a.Table} first, {b.Table} by nested loops"),
            };
            foreach (var o in options4)
            {
                if (o.Lead1 == a.Alias && current is { } m && o.Method == m)
                {
                    continue; // 지금 계획과 같은 조합
                }

                Add(new HintAdvice($"LEADING({o.Lead1} {o.Lead2}{others}) {o.Method}({o.Inner})", en ? o.MeaningEn : o.MeaningKo));
            }
        }

        // 4. 가장 비싼 전체 읽기 표 → PARALLEL(설정)
        if (accesses.Where(x => x.Full).OrderByDescending(x => x.Cost ?? 0).FirstOrDefault() is { } big)
        {
            var degree = Math.Clamp(options.ParallelDegree, 2, 64).ToString(CultureInfo.InvariantCulture);
            Add(new HintAdvice($"FULL({big.Alias}) PARALLEL({big.Alias} {degree})", en ? $"Read {big.Table} in parallel ({degree})" : $"{big.Table} 를 병렬 {degree} 로 읽기")
            {
                Enabled = options.AllowParallel,
                DisabledReason = options.AllowParallel ? null : en ? "Parallel — turned off in settings" : "병렬 — 설정에서 꺼짐",
            });
        }

        return hints;
    }

    /// <summary>
    /// 주 질의 SELECT 바로 뒤에 힌트를 넣은 SQL. 이미 힌트가 있으면 그 안에 덧붙인다. 질의가 아니면 null.
    /// </summary>
    public static string? Apply(string sql, string hint)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(hint);
        var t = new TuningSql(sql);
        var main = t.MainSelect();
        if (main < 0)
        {
            return null;
        }

        var select = t.Tokens[main];
        if (main + 1 < t.Count && t.Kind(main + 1) == TokenKind.Hint)
        {
            var h = t.Tokens[main + 1];
            var existing = sql.Substring(h.Start, h.Length);
            if (existing.EndsWith("*/", StringComparison.Ordinal))
            {
                var merged = existing[..^2].TrimEnd() + " " + hint + " */";
                return sql[..h.Start] + merged + sql[h.End..];
            }
        }

        return sql[..select.End] + " /*+ " + hint + " */" + sql[select.End..];
    }

    // 표 접근 단계(실행 순서 = 루트에서 Id 순 깊이 우선). 별칭은 계획 별칭 절 → SQL FROM 절 → 표 이름.
    private static List<Access> Accesses(ExecutionPlan plan, string? sql)
    {
        var planAliases = PlanPredicates.AliasesFromPlanText(plan.Text);
        var sqlTables = sql is null ? [] : new TuningSql(sql).AllTables();
        var children = plan.Steps.Where(s => s.ParentId is not null).GroupBy(s => s.ParentId!.Value).ToDictionary(g => g.Key, g => g.OrderBy(s => s.Id).ToList());
        var list = new List<Access>();
        var roots = plan.Steps.Where(s => s.ParentId is null || plan.Steps.All(p => p.Id != s.ParentId)).OrderBy(s => s.Id);
        foreach (var root in roots)
        {
            Visit(root);
        }

        return list;

        void Visit(PlanStep step)
        {
            var op = step.Operation.Trim().ToUpperInvariant();
            if (op == "TABLE ACCESS" && !string.IsNullOrEmpty(step.ObjectName))
            {
                var full = (step.Options ?? string.Empty).Contains("FULL", StringComparison.OrdinalIgnoreCase);
                var kids = children.GetValueOrDefault(step.Id) ?? [];
                var alias = PlanAlias(step.Id) ?? kids.Select(k => PlanAlias(k.Id)).FirstOrDefault(a => a is not null);
                list.Add(new Access(step.Id, step.ObjectName!, Resolve(alias, step.ObjectName!), full, step.Cost));
                return;
            }

            if (op == "INDEX" && PlanAlias(step.Id) is { } indexAlias)
            {
                // 표를 읽지 않는 인덱스 접근: 별칭이 표를 가리킨다
                var table = sqlTables.FirstOrDefault(r => string.Equals(r.Qualifier, indexAlias, StringComparison.OrdinalIgnoreCase))?.Name ?? indexAlias;
                list.Add(new Access(step.Id, TuningSql.Normalize(table), Resolve(indexAlias, table), false, step.Cost));
                return;
            }

            foreach (var child in children.GetValueOrDefault(step.Id) ?? [])
            {
                Visit(child);
            }
        }

        string? PlanAlias(int id) => planAliases.TryGetValue(id, out var a) ? a : null;

        string Resolve(string? planAlias, string table)
        {
            if (planAlias is not null)
            {
                return SqlSpelling(sql, planAlias) ?? planAlias;
            }

            var refs = sqlTables.Where(r => r.Name is not null && string.Equals(TuningSql.Normalize(r.Name), TuningSql.Normalize(table), StringComparison.Ordinal)).ToList();
            return refs.Count == 1 && refs[0].Qualifier is { } q ? q : table;
        }
    }

    /// <summary>SQL 에 쓴 별칭 철자(대소문자 무시로 찾음). 없으면 null.</summary>
    private static string? SqlSpelling(string? sql, string? alias)
    {
        if (sql is null || alias is null)
        {
            return null;
        }

        return new TuningSql(sql).AllTables().Select(r => r.Alias).FirstOrDefault(a => a is not null && string.Equals(TuningSql.Normalize(a), alias, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>표 접근 단계 위로 처음 만나는 조인의 방법(USE_NL·USE_HASH·USE_MERGE). 모르면 null.</summary>
    private static string? JoinMethod(ExecutionPlan plan, int stepId)
    {
        var byId = plan.Steps.ToDictionary(s => s.Id);
        var cur = byId.GetValueOrDefault(stepId);
        while (cur?.ParentId is { } p && byId.TryGetValue(p, out var parent))
        {
            var op = parent.Operation.Trim().ToUpperInvariant();
            if (op.StartsWith("NESTED LOOPS", StringComparison.Ordinal))
            {
                return "USE_NL";
            }

            if (op.StartsWith("HASH JOIN", StringComparison.Ordinal))
            {
                return "USE_HASH";
            }

            if (op.StartsWith("MERGE JOIN", StringComparison.Ordinal))
            {
                return "USE_MERGE";
            }

            cur = parent;
        }

        return null;
    }
}
