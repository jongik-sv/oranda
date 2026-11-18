using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Composition;
using Oranda.Language.Ai.Tools;

namespace Oranda.Language.Ai.Handlers;

/// <summary>
/// <c>get_awr_plan_history</c>(AI 확대 §3.1): AWR 스냅숏별 계획 이력. sql_id 범위는 게이트가 본다(요청의 sql_id 와 도구가 알려 준 것만).
/// 팩 선언이 없거나 AWR 이 없는 DBMS 는 오류가 아니라 <c>supported=false</c> 결과다.
/// </summary>
[OrandaService(typeof(IAgentToolHandler))]
internal sealed class GetAwrPlanHistoryHandler(AgentToolboxProvider boxes) : ToolHandlerBase(boxes)
{
    public override string ToolName => AgentToolNames.GetAwrPlanHistory;

    protected override async Task<JsonNode> RunAsync(TuningToolbox box, AgentToolInvocation inv, CancellationToken ct) =>
        Node(await box.GetAwrPlanHistoryAsync(inv.Db, RequiredText(inv, AgentFields.SqlId), ct).ConfigureAwait(false));
}

/// <summary><c>get_awr_plan</c>(AI 확대 §3.1): AWR 에 남은 한 계획의 줄. 술어는 <c>access</c>·<c>filter</c> 이름이라 게이트가 가린다.</summary>
[OrandaService(typeof(IAgentToolHandler))]
internal sealed class GetAwrPlanHandler(AgentToolboxProvider boxes) : ToolHandlerBase(boxes)
{
    public override string ToolName => AgentToolNames.GetAwrPlan;

    protected override async Task<JsonNode> RunAsync(TuningToolbox box, AgentToolInvocation inv, CancellationToken ct) =>
        Node(await box.GetAwrPlanAsync(inv.Db, RequiredText(inv, AgentFields.SqlId), PlanHash(inv), ct).ConfigureAwait(false));

    /// <summary>plan_hash_value 인자(0~4294967295 정수). 없거나 맞지 않으면 앱이 쓴 오류.</summary>
    private static long PlanHash(AgentToolInvocation inv)
    {
        if (inv.Arguments.TryGetPropertyValue(AgentFields.PlanHashValue, out var n) && n is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            && long.TryParse(v.ToJsonString(), NumberStyles.None, CultureInfo.InvariantCulture, out var hash) && hash <= uint.MaxValue)
        {
            return hash;
        }

        throw new AiToolException($"'{AgentFields.PlanHashValue}' 인자가 필요합니다(get_awr_plan_history 가 알려 준 0~4294967295 정수).");
    }
}
