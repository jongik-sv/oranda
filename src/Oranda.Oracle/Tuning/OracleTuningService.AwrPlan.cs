using System.Globalization;
using System.Text;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Tuning;

/// <summary>AWR 에 남은 계획 단계(TSK-06-06 두 plan_hash 비교). 카탈로그 <c>tuning.awr_plan_steps</c>(license: diagnostics) — 선언이 모자라면 보내지 않는다.</summary>
internal sealed partial class OracleTuningService
{
    public async Task<ExecutionPlan> GetAwrPlanAsync(ISession s, string sqlId, long planHashValue, CancellationToken ct)
    {
        var id = CheckSqlId(sqlId);
        ArgumentOutOfRangeException.ThrowIfNegative(planHashValue);
        var rows = (await QueryAsync(s, "tuning.awr_plan_steps", c =>
        {
            AddText(c, "sql_id", id);
            AddLong(c, "plan_hash_value", planHashValue);
        }, ct)).Rows;
        return ToAwrPlan(rows, planHashValue);
    }

    /// <summary>
    /// dba_hist_sql_plan 줄(id, parent_id, depth, operation, options, object_owner, object_name, cost, cardinality, access, filter) → 계획.
    /// 글은 DBMS_XPLAN 처럼 들여쓴 표 한 장(비교 화면은 단계만 쓴다).
    /// </summary>
    internal static ExecutionPlan ToAwrPlan(IEnumerable<object?[]> rows, long planHashValue)
    {
        var steps = rows.Select(r => new PlanStep(Int(r[0]) ?? 0, Int(r[1]), Int(r[2]) ?? 0, Str(r[3]) ?? string.Empty)
        {
            Options = Str(r[4]),
            ObjectOwner = Str(r[5]),
            ObjectName = Str(r[6]),
            Cost = Long(r[7]),
            EstimatedRows = Long(r[8]),
            AccessPredicates = Str(r[9]),
            FilterPredicates = Str(r[10]),
        }).OrderBy(p => p.Id).ToList();

        var text = new StringBuilder();
        text.Append("Plan hash value: ").AppendLine(planHashValue.ToString(CultureInfo.InvariantCulture));
        text.AppendLine("(AWR dba_hist_sql_plan)");
        foreach (var p in steps)
        {
            text.Append(p.Id.ToString(CultureInfo.InvariantCulture).PadLeft(4)).Append(" | ")
                .Append(new string(' ', p.Depth)).Append(p.Operation);
            if (p.Options is { Length: > 0 } o)
            {
                text.Append(' ').Append(o);
            }

            if (p.ObjectName is { Length: > 0 } n)
            {
                text.Append(" | ").Append(p.ObjectOwner is { Length: > 0 } owner ? owner + "." + n : n);
            }

            if (p.Cost is { } cost)
            {
                text.Append(" | cost ").Append(cost.ToString(CultureInfo.InvariantCulture));
            }

            text.AppendLine();
        }

        return new ExecutionPlan(steps, text.ToString()) { PlanHashValue = planHashValue };
    }
}
