using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Plans;
using Oranda.Core.Tuning;

namespace Oranda.Language.Tuning;

/// <summary>인덱스 후보의 근거.</summary>
public enum IndexBasis
{
    /// <summary>전체 읽기 단계의 access/filter 조건 열.</summary>
    Filter,

    /// <summary>조인 조건의 이 표 쪽 열.</summary>
    Join,
}

/// <summary>후보 열과 기존 인덱스의 겹침.</summary>
public enum IndexOverlapKind
{
    /// <summary>겹치는 인덱스 없음.</summary>
    None,

    /// <summary>기존 인덱스의 앞 열이 후보 열을 모두 덮는다(새로 만들 필요 없음).</summary>
    Covered,

    /// <summary>기존 인덱스가 후보의 앞 열 일부만 가진다(넓히는 안도 같다).</summary>
    Extends,

    /// <summary>첫 열만 같다.</summary>
    SharesLeading,

    /// <summary>기존 인덱스를 조회하지 못했다.</summary>
    Unknown,
}

/// <summary>인덱스를 확인할 표(<see cref="IndexAdvisor.TablesToCheck"/>).</summary>
public sealed record TableTarget(string Owner, string Table);

/// <summary>
/// 인덱스 후보 하나(TN-09). <see cref="Candidate"/> 는 Core 계약 형, 나머지는 화면·보고서가 쓰는 근거.
/// </summary>
public sealed record IndexAdvice(IndexCandidate Candidate, IndexBasis Basis, IndexOverlapKind Overlap)
{
    /// <summary>계획에서 이 표를 가리킨 별칭(대문자). 모르면 null.</summary>
    public string? Alias { get; init; }

    /// <summary>근거 단계의 동작(예: <c>TABLE ACCESS FULL</c>).</summary>
    public string Operation { get; init; } = string.Empty;

    /// <summary>등호 열(앞에 둔다).</summary>
    public IReadOnlyList<string> EqualityColumns { get; init; } = [];

    /// <summary>범위 열(뒤에 둔다, 하나까지).</summary>
    public IReadOnlyList<string> RangeColumns { get; init; } = [];

    /// <summary>겹치는 기존 인덱스의 열(없으면 빈 목록).</summary>
    public IReadOnlyList<string> OverlappingColumns { get; init; } = [];

    /// <summary>근거 단계가 전체에서 차지한 시간 비율(0~1, 실제 시간이 없으면 비용 비율). 모르면 null.</summary>
    public double? Share { get; init; }

    /// <summary>비율이 실제 시간에서 왔는가(거짓이면 비용).</summary>
    public bool ShareIsTime { get; init; }

    /// <summary>근거 문장(사람이 읽는 글).</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>근거 열 칸 글(예: <c>Id 4 FULL · filter</c>).</summary>
    public string BasisText { get; init; } = string.Empty;

    /// <summary>CREATE INDEX 문안을 새 탭에 넣을 만한가(이미 있는 인덱스가 덮으면 거짓).</summary>
    public bool CanCreate => Overlap != IndexOverlapKind.Covered;
}

/// <summary>
/// 규칙 기반 인덱스 후보(TSK-06-05, TN-09). 순수 함수: 계획과 기존 인덱스 열 구성만 보고 DB 를 부르지 않는다.
/// TABLE ACCESS FULL·INDEX FULL SCAN·INDEX FAST FULL SCAN 단계의 조건 열(등호 앞·범위 하나 뒤)과 그 표의 조인 열에서 후보를 만들고,
/// 기존 인덱스와의 겹침을 보고 근거와 CREATE INDEX 문안(실행하지 않는다)을 붙인다. TSK-06-07 보고서도 이것을 쓴다.
/// </summary>
public static partial class IndexAdvisor
{
    /// <summary>후보 한 개의 최대 열 수.</summary>
    public const int MaxColumns = 4;

    /// <summary>11.2 의 이름 길이 한도(12.2+ 는 128 이지만 낮은 쪽에 맞춘다).</summary>
    public const int MaxNameLength = 30;

    /// <summary>후보를 만들 단계의 표(소유자 모르면 <paramref name="defaultOwner"/>). 기존 인덱스를 미리 읽을 때 쓴다.</summary>
    public static IReadOnlyList<TableTarget> TablesToCheck(ExecutionPlan plan, string? defaultOwner = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var byId = plan.Steps.ToDictionary(s => s.Id);
        return plan.Steps
            .Select(s => TargetTable(s, byId))
            .Where(t => t is not null)
            .Select(t => new TableTarget(t!.ObjectOwner ?? defaultOwner ?? string.Empty, t.ObjectName!))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// 인덱스 후보. <paramref name="existing"/> 은 (소유자, 표) → 기존 인덱스 열 구성(조회 못 했으면 null → 겹침 「확인 못함」).
    /// </summary>
    public static IReadOnlyList<IndexAdvice> Suggest(
        ExecutionPlan plan,
        Func<string, string, IReadOnlyList<IndexColumnsRow>?>? existing,
        string? defaultOwner = null,
        bool english = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var byId = plan.Steps.ToDictionary(s => s.Id);
        var planAliases = PlanPredicates.AliasesFromPlanText(plan.Text);
        var root = plan.Steps.FirstOrDefault(s => s.ParentId is null) ?? (plan.Steps.Count > 0 ? plan.Steps[0] : null);
        var all = new List<IndexAdvice>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var step in plan.Steps)
        {
            if (TargetTable(step, byId) is not { } tableStep)
            {
                continue;
            }

            var owner = tableStep.ObjectOwner ?? defaultOwner ?? string.Empty;
            var table = tableStep.ObjectName!;
            var terms = PlanPredicates.Parse(step.AccessPredicates, step.FilterPredicates,
                tableStep.Id != step.Id ? tableStep.FilterPredicates : null);
            var alias = AliasFor(step, tableStep, planAliases, terms);
            var mine = terms.Where(t => t.Alias is null || alias is null || t.Alias == alias).ToList();
            var eq = mine.Where(t => t.Kind is PredicateKind.Equality or PredicateKind.InList).Select(t => t.Column).Distinct(StringComparer.Ordinal).ToList();
            var range = mine.Where(t => t.Kind is PredicateKind.Range or PredicateKind.Like).Select(t => t.Column)
                .Where(c => !eq.Contains(c, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).Take(1).ToList();
            var share = Share(step, root, out var isTime);
            var indexes = existing?.Invoke(owner, table);

            if (eq.Count + range.Count > 0)
            {
                var cols = eq.Concat(range).Take(MaxColumns).ToList();
                Add(new Draft(owner, table, alias, step, cols, eq.Take(MaxColumns).ToList(), range, IndexBasis.Filter, share, isTime, Filters(mine, cols)));
            }

            // 조인 열: 이 표의 별칭이 든 조인 조건(어느 단계에 있든)
            if (alias is not null)
            {
                foreach (var joinStep in plan.Steps)
                {
                    foreach (var j in PlanPredicates.Parse(joinStep.AccessPredicates, joinStep.FilterPredicates).Where(t => t.Kind == PredicateKind.Join))
                    {
                        var col = j.Alias == alias ? j.Column : j.OtherAlias == alias ? j.OtherColumn : null;
                        if (col is null)
                        {
                            continue;
                        }

                        Add(new Draft(owner, table, alias, joinStep, [col], [col], [], IndexBasis.Join, Share(step, root, out var jt), jt, [j.Text]) { ScanStep = step });
                    }
                }
            }

            void Add(Draft draft)
            {
                var key = draft.Owner + "." + draft.Table + "(" + string.Join(",", draft.Columns) + ")";
                if (!seen.Add(key))
                {
                    return;
                }

                all.Add(Build(draft, indexes, existing is null, 0, english));
            }
        }

        // 만들 만한 것 먼저(시간 비중 큰 순), 이미 있는 것은 뒤. 문안의 후보 번호는 정렬한 순서로 매긴다.
        var sorted = all
            .OrderBy(a => a.CanCreate ? 0 : 1)
            .ThenByDescending(a => a.Share ?? 0)
            .ThenBy(a => a.Candidate.BasisStepId)
            .ToList();
        var n = 0;
        return sorted
            .Select(a => a.CanCreate
                ? a with { Candidate = a.Candidate with { Ddl = Ddl(a.Candidate.Owner, a.Candidate.TableName, a.Candidate.Columns, ++n, english) } }
                : a)
            .ToList();
    }

    private sealed record Draft(
        string Owner, string Table, string? Alias, PlanStep Step, IReadOnlyList<string> Columns, IReadOnlyList<string> Equality,
        IReadOnlyList<string> Range, IndexBasis Basis, double? Share, bool ShareIsTime, IReadOnlyList<string> Conditions)
    {
        /// <summary>조인 후보의 전체 읽기 단계(근거 단계는 조인 단계).</summary>
        public PlanStep? ScanStep { get; init; }
    }

    private static IndexAdvice Build(Draft d, IReadOnlyList<IndexColumnsRow>? indexes, bool notAsked, int ordinal, bool english)
    {
        var (overlap, index) = indexes is null ? (IndexOverlapKind.Unknown, null) : Overlap(d.Columns, d.Equality.Count == d.Columns.Count, indexes);
        var op = Operation(d.ScanStep ?? d.Step);
        var ddl = overlap == IndexOverlapKind.Covered && index is not null
            ? CoveredComment(index, english)
            : Ddl(d.Owner, d.Table, d.Columns, ordinal, english);
        var candidate = new IndexCandidate(d.Owner, d.Table, d.Columns, d.Step.Id, index?.IndexName, ddl);
        var advice = new IndexAdvice(candidate, d.Basis, notAsked ? IndexOverlapKind.Unknown : overlap)
        {
            Alias = d.Alias,
            Operation = op,
            EqualityColumns = d.Equality.Where(d.Columns.Contains).ToList(),
            RangeColumns = d.Range.Where(d.Columns.Contains).ToList(),
            OverlappingColumns = index?.Columns ?? [],
            Share = d.Share,
            ShareIsTime = d.ShareIsTime,
        };
        return advice with
        {
            Reason = Reason(advice, d, english),
            BasisText = d.Basis == IndexBasis.Filter
                ? $"Id {d.Step.Id} {ShortOp(d.Step)} · {(d.Step.AccessPredicates is not null && d.Step.FilterPredicates is null ? "access" : "filter")}"
                : english ? $"Id {d.Step.Id} join column" : $"Id {d.Step.Id} 조인 열",
        };
    }

    /// <summary>기존 인덱스와 후보 열의 겹침. 등호 열만이면 앞 열 순서는 따지지 않는다.</summary>
    public static (IndexOverlapKind Kind, IndexColumnsRow? Index) Overlap(IReadOnlyList<string> columns, bool allEquality, IReadOnlyList<IndexColumnsRow> indexes)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(indexes);
        if (columns.Count == 0)
        {
            return (IndexOverlapKind.None, null);
        }

        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        foreach (var ix in indexes)
        {
            if (ix.Columns.Count < columns.Count)
            {
                continue;
            }

            var lead = ix.Columns.Take(columns.Count).ToList();
            var covered = allEquality
                ? lead.All(c => columns.Any(x => Same(x, c)))
                : lead.Zip(columns).All(p => Same(p.First, p.Second));
            if (covered)
            {
                return (IndexOverlapKind.Covered, ix);
            }
        }

        // 기존 인덱스 열이 후보의 앞부분이면 넓히는 안(가장 긴 것).
        var extends = indexes
            .Where(ix => ix.Columns.Count > 0 && ix.Columns.Count < columns.Count && ix.Columns.Zip(columns).All(p => Same(p.First, p.Second)))
            .OrderByDescending(ix => ix.Columns.Count)
            .FirstOrDefault();
        if (extends is not null)
        {
            return (IndexOverlapKind.Extends, extends);
        }

        var leading = indexes.FirstOrDefault(ix => ix.Columns.Count > 0 && Same(ix.Columns[0], columns[0]));
        return leading is null ? (IndexOverlapKind.None, null) : (IndexOverlapKind.SharesLeading, leading);
    }

    /// <summary>CREATE INDEX 문안(머리 주석 「실행하면 DB 에 씁니다」). 이름은 <c>표_IXC번호</c>, 30 자 이내.</summary>
    public static string Ddl(string owner, string table, IReadOnlyList<string> columns, int ordinal, bool english = false)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var suffix = "_IXC" + ordinal.ToString(CultureInfo.InvariantCulture);
        var baseName = TuningSql.Normalize(table);
        if (baseName.Length + suffix.Length > MaxNameLength)
        {
            baseName = baseName[..(MaxNameLength - suffix.Length)];
        }

        var qualified = string.IsNullOrEmpty(owner) ? Ident(table) : Ident(owner) + "." + Ident(table);
        var name = string.IsNullOrEmpty(owner) ? Ident(baseName + suffix) : Ident(owner) + "." + Ident(baseName + suffix);
        var sb = new StringBuilder();
        sb.Append(english
            ? $"-- Candidate {ordinal} · running this writes to the database. Review space and DML impact before running it\n"
            : $"-- 후보 {ordinal} · 실행하면 DB 에 씁니다. 공간·DML 영향을 검토한 뒤 실행하세요\n");
        sb.Append("CREATE INDEX ").Append(name).Append('\n');
        sb.Append("    ON ").Append(qualified).Append(" (").Append(string.Join(", ", columns.Select(Ident))).Append(") ONLINE;");
        return sb.ToString();
    }

    private static string CoveredComment(IndexColumnsRow index, bool english) => english
        ? $"-- Existing index {index.IndexName} ({string.Join(", ", index.Columns)}) already covers these columns. No new index is needed"
        : $"-- 기존 인덱스 {index.IndexName}({string.Join(", ", index.Columns)}) 가 이미 이 열을 덮습니다. 새로 만들 필요가 없습니다";

    /// <summary>이름을 SQL 에 쓸 모양으로: 대문자 일반 이름은 그대로, 아니면 큰따옴표.</summary>
    public static string Ident(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var bare = name.Length >= 2 && name[0] == '"' && name[^1] == '"' ? name[1..^1] : name;
        return PlainName().IsMatch(bare) ? bare : "\"" + bare.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    [GeneratedRegex("^[A-Z][A-Z0-9_$#]*$")]
    private static partial Regex PlainName();

    // 이 단계가 후보 대상이면 표를 가진 단계(INDEX FULL SCAN 이면 부모 TABLE ACCESS BY … ROWID).
    private static PlanStep? TargetTable(PlanStep step, Dictionary<int, PlanStep> byId)
    {
        var op = step.Operation.Trim().ToUpperInvariant();
        var options = (step.Options ?? string.Empty).Trim().ToUpperInvariant();
        if (op == "TABLE ACCESS" && options.Contains("FULL", StringComparison.Ordinal) && !string.IsNullOrEmpty(step.ObjectName))
        {
            return step;
        }

        if (op == "INDEX" && options is "FULL SCAN" or "FAST FULL SCAN" or "FULL SCAN (MIN/MAX)" && step.ParentId is { } p
            && byId.TryGetValue(p, out var parent) && parent.Operation.Trim().Equals("TABLE ACCESS", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(parent.ObjectName))
        {
            return parent;
        }

        return null;
    }

    private static string? AliasFor(PlanStep step, PlanStep tableStep, IReadOnlyDictionary<int, string> planAliases, IReadOnlyList<PredicateTerm> terms)
    {
        if (planAliases.TryGetValue(tableStep.Id, out var a) || planAliases.TryGetValue(step.Id, out a))
        {
            return a;
        }

        // 계획 글에 별칭 절이 없으면 이 단계 조건에 가장 많이 나온 별칭.
        return terms.Where(t => t.Alias is not null && t.Kind != PredicateKind.Join)
            .GroupBy(t => t.Alias!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
    }

    private static double? Share(PlanStep step, PlanStep? root, out bool isTime)
    {
        isTime = false;
        if (root is null)
        {
            return null;
        }

        if (step.ActualTime is { } t && root.ActualTime is { } total && total > TimeSpan.Zero)
        {
            isTime = true;
            return Math.Clamp(t.TotalMilliseconds / total.TotalMilliseconds, 0, 1);
        }

        return step.Cost is { } c && root.Cost is > 0 ? Math.Clamp((double)c / root.Cost.Value, 0, 1) : null;
    }

    private static List<string> Filters(IReadOnlyList<PredicateTerm> terms, IReadOnlyList<string> columns) =>
        terms.Where(t => columns.Contains(t.Column, StringComparer.Ordinal) && t.Kind is not PredicateKind.Join).Select(t => t.Text).Distinct(StringComparer.Ordinal).ToList();

    private static string Operation(PlanStep step) =>
        (step.Operation + (string.IsNullOrWhiteSpace(step.Options) ? string.Empty : " " + step.Options)).Trim();

    private static string ShortOp(PlanStep step) =>
        step.Operation.Trim().Equals("INDEX", StringComparison.OrdinalIgnoreCase) ? "INDEX " + (step.Options ?? string.Empty).Trim() : (step.Options ?? step.Operation).Trim();

    private static string Reason(IndexAdvice a, Draft d, bool english)
    {
        var c = a.Candidate;
        var target = string.IsNullOrEmpty(c.Owner) ? c.TableName : c.Owner + "." + c.TableName;
        var scan = d.ScanStep ?? d.Step;
        var sb = new StringBuilder();
        var pct = a.Share is { } s ? Math.Round(s * 100).ToString("0", CultureInfo.InvariantCulture) : null;
        if (english)
        {
            sb.Append(CultureInfo.InvariantCulture, $"Id {scan.Id} {Operation(scan)} {target}");
            if (pct is not null)
            {
                sb.Append(CultureInfo.InvariantCulture, $" takes {pct} % of the {(a.ShareIsTime ? "time" : "cost")}");
            }

            sb.Append(". ");
            if (d.Basis == IndexBasis.Join)
            {
                sb.Append(CultureInfo.InvariantCulture, $"Id {d.Step.Id} joins on {string.Join(", ", d.Conditions)}. An index on the join column lets a nested loop reach the rows directly.");
            }
            else
            {
                if (a.EqualityColumns.Count > 0)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"Equality on {string.Join(", ", a.EqualityColumns)}");
                }

                if (a.RangeColumns.Count > 0)
                {
                    sb.Append(a.EqualityColumns.Count > 0 ? " and range on " : "Range on ").Append(string.Join(", ", a.RangeColumns));
                }

                sb.Append(". Equality columns go first, the range column last.");
            }
        }
        else
        {
            sb.Append(CultureInfo.InvariantCulture, $"Id {scan.Id} {Operation(scan)} {target}");
            sb.Append(pct is not null ? $" 가 {(a.ShareIsTime ? "시간" : "비용")} {pct} %. " : ". ");
            if (d.Basis == IndexBasis.Join)
            {
                sb.Append(CultureInfo.InvariantCulture, $"Id {d.Step.Id} 의 조인 조건 {string.Join(", ", d.Conditions)}. 조인 열에 인덱스가 있으면 중첩 루프가 행을 바로 찾습니다.");
            }
            else
            {
                var parts = new List<string>();
                if (a.EqualityColumns.Count > 0)
                {
                    parts.Add(string.Join(", ", a.EqualityColumns) + "(등호)");
                }

                if (a.RangeColumns.Count > 0)
                {
                    parts.Add(string.Join(", ", a.RangeColumns) + "(범위)");
                }

                sb.Append("조건 열 ").Append(string.Join(" 와 ", parts)).Append(". 등호 열을 앞에, 범위 열을 뒤에 둡니다.");
            }
        }

        var ix = c.OverlappingIndex;
        var ixCols = string.Join(", ", a.OverlappingColumns);
        sb.Append(' ').Append(a.Overlap switch
        {
            IndexOverlapKind.Covered => english ? $"Existing {ix}({ixCols}) already covers it." : $"기존 {ix}({ixCols}) 가 이미 덮습니다.",
            IndexOverlapKind.Extends => english ? $"Widening the existing {ix}({ixCols}) works the same." : $"기존 {ix}({ixCols}) 를 넓히는 안도 같은 효과입니다.",
            IndexOverlapKind.SharesLeading => english ? $"The existing {ix}({ixCols}) shares only the first column." : $"기존 {ix}({ixCols}) 는 첫 열만 같습니다.",
            IndexOverlapKind.Unknown => english ? "Existing indexes could not be checked." : "기존 인덱스는 확인하지 못했습니다.",
            _ => english ? "No existing index overlaps." : "겹치는 기존 인덱스가 없습니다.",
        });
        return sb.ToString().Trim();
    }
}
