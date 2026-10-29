using System.Text.Json.Nodes;
using Oranda.App.Shell.Composition;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Shell.Composition;

/// <summary>
/// 조립 시험(mask-app u4): 실제 컨테이너가 만든 <see cref="IAgentGate"/> 에 리터럴 마스커가 주입되어 있다.
/// ServiceContainer 는 등록 안 된 선택 인자에 null 을 넣으므로, 마스커 등록이 빠지면 게이트가 가림 없이 조용히 열린다.
/// </summary>
public class AiMaskerCompositionTests
{
    [Fact]
    public async Task Real_container_registers_the_masker_and_the_gate_uses_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-mask-" + Guid.NewGuid().ToString("N"));
        using var container = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir });

        Assert.NotNull(container.GetService<ISqlLiteralMasker>());

        // 사전에 없는 자리표시(:v9)는 마스커의 되돌림이 알아보고 거부한다. 마스커가 게이트에 없으면 이 검사 없이 다음 단계로 넘어간다.
        var profile = new ConnectionProfile("dev", "scott", "h/s", AiAccess: AiAccessLevel.Plan);
        var context = new AiContext(profile.Name, profile.Environment, AiAccessLevel.Plan, StatementText: "SELECT * FROM tb_order WHERE stat_cd = 'A'");
        var session = new AgentSession(new AgentRunScope("run-mask", profile, context) { Literals = new LiteralVault() }) { Client = "test-client" };

        var gate = container.GetService<IAgentGate>()!;
        var response = await gate.CallToolAsync(session, AgentToolNames.ExplainPlan, JsonNode.Parse("""{"sql":"SELECT * FROM tb_order WHERE stat_cd = :v9"}"""), CancellationToken.None);

        Assert.True(response.Result.IsError);
        Assert.Equal("literal_unknown", (string?)JsonNode.Parse(response.Result.Text)!["code"]);
    }
}
