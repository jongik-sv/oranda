using static Oranda.Core.Catalog.Providers.CatalogValues;

namespace Oranda.Core.Plans.Providers;

/// <summary>
/// 실행 계획 질의 행 → <see cref="PlanStep"/> 조립·위험 판정(DBMS 무관). 공급자는 카탈로그 <c>plan.*</c> 행을 읽어 이 함수에 넘긴다.
/// </summary>
public static class PlanRows
{
    /// <summary>예상·실제 행 수가 이 배수 이상 다르면 <see cref="PlanRisk.CardinalityMisestimate"/>.</summary>
    public const double MisestimateFactor = 10;

    /// <summary>두 값 중 큰 쪽이 이 값 미만이면 오차를 강조하지 않는다(작은 수 잡음).</summary>
    public const long MisestimateMinRows = 10;

    /// <summary>원자료 → 계획 단계(Id 순). 깊이는 부모 사슬로 구하고 위험 표시를 붙인다.</summary>
    public static IReadOnlyList<PlanStep> BuildSteps(IEnumerable<RawPlanStep> raw)
    {
        var list = raw.OrderBy(r => r.Id).ToList();
        var depth = new Dictionary<int, int>();
        var result = new List<PlanStep>(list.Count);
        foreach (var r in list)
        {
            var d = r.ParentId is { } p && depth.TryGetValue(p, out var pd) ? pd + 1 : 0;
            depth[r.Id] = d;
            result.Add(new PlanStep(r.Id, r.ParentId, d, r.Operation)
            {
                Options = r.Options,
                ObjectOwner = r.Owner,
                ObjectName = r.Name,
                Cost = r.Cost,
                EstimatedRows = r.EstimatedRows,
                ActualRows = r.ActualRows,
                Starts = r.Starts,
                ActualTime = r.ElapsedMicros is { } us ? TimeSpan.FromTicks(us * 10) : null,
                AccessPredicates = r.Access,
                FilterPredicates = r.Filter,
                Risk = ClassifyRisk(r),
            });
        }

        return result;
    }

    /// <summary>위험 단계 판정(PLAN-03): 테이블 전체 스캔, 카테시안 조인, 예상·실제 행 수 차이(시작 횟수당 실제 행 기준).</summary>
    public static PlanRisk ClassifyRisk(RawPlanStep r)
    {
        ArgumentNullException.ThrowIfNull(r);

        var risk = PlanRisk.None;
        var options = r.Options ?? string.Empty;
        if (r.Operation.Equals("TABLE ACCESS", StringComparison.OrdinalIgnoreCase) && options.Contains("FULL", StringComparison.OrdinalIgnoreCase))
        {
            risk |= PlanRisk.FullScan;
        }

        if (options.Contains("CARTESIAN", StringComparison.OrdinalIgnoreCase))
        {
            risk |= PlanRisk.Cartesian;
        }

        if (r.ActualRows is { } actual && r.EstimatedRows is { } est)
        {
            var a = (double)actual / Math.Max(r.Starts ?? 1, 1);
            var e = Math.Max(est, 1);
            var big = Math.Max(a, e);
            var ratio = big / Math.Max(Math.Min(a, e), 1);
            if (big >= MisestimateMinRows && ratio >= MisestimateFactor)
            {
                risk |= PlanRisk.CardinalityMisestimate;
            }
        }

        return risk;
    }

    /// <summary>
    /// PLAN_TABLE 행(<c>SELECT p.*</c>)을 열 이름으로 읽는다. 사용자 스키마에 옛 PLAN_TABLE(plan_id·access_predicates 등이 없는 것)이
    /// 있어도 실패하지 않게, 없는 열은 null 로 본다(서버 버전 무관, TSK-03-07).
    /// </summary>
    public static IEnumerable<RawPlanStep> ReadPlanTableRows(IReadOnlyList<string> names, IEnumerable<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(rows);
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Count; i++)
        {
            index.TryAdd(names[i], i);
        }

        object? At(object?[] r, string name) => index.TryGetValue(name, out var i) && i < r.Length ? r[i] : null;

        return rows.Select(r => new RawPlanStep(
            Int(At(r, "ID")) ?? 0, Int(At(r, "PARENT_ID")), Str(At(r, "OPERATION")) ?? string.Empty, Str(At(r, "OPTIONS")),
            Str(At(r, "OBJECT_OWNER")), Str(At(r, "OBJECT_NAME")), Long(At(r, "COST")), Long(At(r, "CARDINALITY")),
            null, null, null, Str(At(r, "ACCESS_PREDICATES")), Str(At(r, "FILTER_PREDICATES"))));
    }

    /// <summary>첫 열 글을 줄바꿈으로 이은 계획 글.</summary>
    public static string JoinLines(IEnumerable<object?[]> rows) =>
        string.Join(Environment.NewLine, rows.Select(r => Str(r[0]) ?? string.Empty));
}

/// <summary>계획 한 줄의 원자료. 행 수·시간은 <c>last_*</c>(마지막 실행) 값.</summary>
public sealed record RawPlanStep(int Id, int? ParentId, string Operation, string? Options, string? Owner, string? Name,
                                 long? Cost, long? EstimatedRows, long? ActualRows, long? Starts, long? ElapsedMicros,
                                 string? Access, string? Filter);
