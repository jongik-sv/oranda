using Oranda.Core.Plans;
using Oranda.Core.Tuning;

namespace Oranda.Language.Tests.Tuning;

/// <summary>튜닝 도우미 규칙 시험의 공용 계획(시안 i 의 LOT_HIST·LOT_MST 조인).</summary>
internal static class TuningFixtures
{
    public const string Sql = """
        SELECT lh.lot_id, lh.evt_time, lm.prod_id
          FROM mes.lot_hist lh JOIN mes.lot_mst lm ON lm.lot_id = lh.lot_id
         WHERE lh.eqp_id = :B1 AND lh.evt_time >= :B2
        """;

    public const string AliasSection = """
        Plan hash value: 1838229974

        Query Block Name / Object Alias (identified by operation id):
        -------------------------------------------------------------

           1 - SEL$1
           2 - SEL$1 / LM@SEL$1
           3 - SEL$1 / "LH"@"SEL$1"
        """;

    /// <summary>HASH JOIN(LOT_MST FULL, LOT_HIST FULL). 시간 대부분이 LOT_HIST.</summary>
    public static ExecutionPlan JoinPlan(string? text = AliasSection) => new(
        [
            new PlanStep(0, null, 0, "SELECT STATEMENT") { Cost = 4200, ActualTime = TimeSpan.FromMilliseconds(1842) },
            new PlanStep(1, 0, 1, "HASH JOIN") { Cost = 4200, AccessPredicates = "\"LM\".\"LOT_ID\"=\"LH\".\"LOT_ID\"", ActualTime = TimeSpan.FromMilliseconds(1840) },
            new PlanStep(2, 1, 2, "TABLE ACCESS") { Options = "FULL", ObjectOwner = "MES", ObjectName = "LOT_MST", Cost = 300, ActualTime = TimeSpan.FromMilliseconds(100) },
            new PlanStep(3, 1, 2, "TABLE ACCESS")
            {
                Options = "FULL", ObjectOwner = "MES", ObjectName = "LOT_HIST", Cost = 3800, ActualTime = TimeSpan.FromMilliseconds(1600),
                FilterPredicates = "(\"LH\".\"EQP_ID\"=:B1 AND \"LH\".\"EVT_TIME\">=:B2 AND TO_NUMBER(\"LH\".\"LOT_NO\")=123)",
            },
        ],
        text ?? string.Empty);

    public static IReadOnlyList<IndexColumnsRow>? Existing(string owner, string table) => table switch
    {
        "LOT_HIST" => [new IndexColumnsRow("LOT_HIST_IX1", ["LOT_ID", "EVT_TIME"]), new IndexColumnsRow("LOT_HIST_IX2", ["EQP_ID"])],
        "LOT_MST" => [new IndexColumnsRow("LOT_MST_PK", ["LOT_ID"])],
        _ => [],
    };
}
