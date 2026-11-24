using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;

namespace Oranda.Core.Tests.Ai;

// D-069: 접속마다 「민감 데이터 가리기」(ConnectionProfile.AiMaskSensitive, null = 켬). 끄면 가림이 「항상 가리는 열」로 좁혀진다(로컬 AI 와 같은 좁힘).
public partial class AgentGateTests
{
    private static AgentSession CloudSession(ConnectionProfile profile)
    {
        var ctx = new AiContext(profile.Name, profile.Environment, AiAccessLevel.QueryRows, StatementText: Statement);
        return new AgentSession(new AgentRunScope("run-m", profile, ctx)) { Client = "test-client" };
    }

    [Fact]
    public void Sensitive_masking_is_on_unless_the_connection_turns_it_off()
    {
        Assert.True(AiAccessPolicy.MasksSensitiveColumns(Dev(AiAccessLevel.QueryRows)));
        Assert.False(AiAccessPolicy.MasksSensitiveColumns(Dev(AiAccessLevel.QueryRows) with { AiMaskSensitive = false }));
        Assert.True(SensitiveColumns.Hides("PHONE_NO", narrow: false));
        Assert.False(SensitiveColumns.Hides("PHONE_NO", narrow: true));
        Assert.True(SensitiveColumns.Hides("USER_PWD", narrow: true));
    }

    [Fact]
    public async Task Default_masking_drops_every_sensitive_statistics_value()
    {
        var fx = new GateFixture();
        fx.Handlers[AgentToolNames.GetTableStats].Respond = _ => AgentToolOutput.Ok(JsonNode.Parse(StatsResult)!);

        var response = await Call(fx, CloudSession(Dev(AiAccessLevel.QueryRows)), AgentToolNames.GetTableStats, """{"table":"TB_ORDER"}""");

        Assert.False(response.Result.IsError, response.Result.Text);
        Assert.DoesNotContain("010-LOW", response.Result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("PWD-LOW", response.Result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turning_masking_off_sends_phone_values_but_keeps_always_hidden_columns_out()
    {
        var fx = new GateFixture();
        fx.Handlers[AgentToolNames.GetTableStats].Respond = _ => AgentToolOutput.Ok(JsonNode.Parse(StatsResult)!);
        var profile = Dev(AiAccessLevel.QueryRows) with { AiMaskSensitive = false };

        var response = await Call(fx, CloudSession(profile), AgentToolNames.GetTableStats, """{"table":"TB_ORDER"}""");

        Assert.False(response.Result.IsError, response.Result.Text);
        Assert.Contains("010-LOW", response.Result.Text, StringComparison.Ordinal);
        foreach (var hidden in new[] { "PWD-LOW", "RRN-LOW", "CARD-LOW", "ACCT-LOW", "ADDR-LOW" })
        {
            Assert.DoesNotContain(hidden, response.Result.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_grant_connection_carries_the_connection_masking_switch()
    {
        var on = AgentGrantPolicy.ToConnection(new AgentGrantTarget(Dev(AiAccessLevel.QueryRows), AiAccessLevel.QueryRows));
        var off = AgentGrantPolicy.ToConnection(new AgentGrantTarget(Dev(AiAccessLevel.QueryRows) with { AiMaskSensitive = false }, AiAccessLevel.QueryRows));
        Assert.True(on.MaskSensitiveColumns);
        Assert.False(off.MaskSensitiveColumns);
    }
}
