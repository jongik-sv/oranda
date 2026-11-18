using System.Text.Json.Nodes;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Composition;
using Oranda.Language.Ai.Tools;

namespace Oranda.Language.Ai.Handlers;

/// <summary><c>get_dependents</c>(TSK-15-09): 실행 전 검토가 무효화·FK·트리거·동의어를 본다. 읽기 전용, 메타데이터 수준.</summary>
[OrandaService(typeof(IAgentToolHandler))]
internal sealed class GetDependentsHandler(AgentToolboxProvider boxes) : ToolHandlerBase(boxes)
{
    public override string ToolName => AgentToolNames.GetDependents;

    protected override async Task<JsonNode> RunAsync(TuningToolbox box, AgentToolInvocation inv, CancellationToken ct) =>
        Node(await box.GetDependentsAsync(inv.Db, Table(inv, AgentFields.ObjectName), ct).ConfigureAwait(false));
}
