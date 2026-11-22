using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;

namespace Oranda.Core.Tests.Ai;

// 탭이 접속을 정하는 앱 도구(run_tab·get_result·get_tab): 처리자가 알린 접속(AgentToolOutput.ProfileId)이 허가 안이면 감사 줄의 접속과 data=local_ai 에 쓰인다.
public partial class AgentGateTests
{
    [Fact]
    public async Task Run_tab_audit_row_gets_the_tab_connection_and_local_ai_from_the_handler()
    {
        var fx = new ExternalFixture(true, ExtDev());
        fx.App(AppToolNames.RunTab).Respond = _ => AppToolOutputs.OkWithRows(new JsonObject { ["status"] = "succeeded", ["fetched"] = 3L }, 3) with { ProfileId = ExtDevId };

        var response = await fx.Call(AppToolNames.RunTab, """{"tab":"t1"}""");

        Assert.False(response.Result.IsError, response.Result.Text);
        Assert.Null(Assert.Single(fx.App(AppToolNames.RunTab).Calls).Connection); // 호출에는 접속이 없다(탭이 정한다)
        var audit = fx.Inner.Audit.Entries[^1];
        Assert.Equal(ExtDevId, audit.ProfileId);
        Assert.Equal("dev-erp", audit.ProfileName);
        Assert.Equal(fx.Issue.Grant.GrantId + ":dev-erp", audit.RunId);
        Assert.Equal(fx.Issue.Grant.FindConnection(ExtDevId)!.Level, audit.AccessLevel);
        Assert.StartsWith("data=local_ai", audit.Arguments, StringComparison.Ordinal);
        Assert.Equal("rows=3", audit.Reason);
    }

    [Fact]
    public async Task Get_result_with_a_cloud_model_records_the_connection_without_local_ai()
    {
        var fx = new ExternalFixture(ExtDev(), ExtTest());
        fx.App(AppToolNames.GetResult).Respond = _ => AgentToolOutput.Ok(new JsonObject { ["fetched"] = 0 }) with { ProfileId = ExtTestId };
        var seen = new List<AgentExternalToolActivity>();
        fx.Gate.ExternalToolCompleted += (_, e) => seen.Add(e);

        await fx.Call(AppToolNames.GetResult, """{"tab":"t1"}""");

        var audit = fx.Inner.Audit.Entries[^1];
        Assert.Equal(ExtTestId, audit.ProfileId);
        Assert.Equal("tst-erp", audit.ProfileName);
        Assert.DoesNotContain("data=local_ai", audit.Arguments ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("tst-erp", Assert.Single(seen).Connection);
    }

    [Fact]
    public async Task A_reported_connection_outside_the_grant_is_not_recorded_as_that_connection()
    {
        var fx = new ExternalFixture(true, ExtDev());
        var outside = Guid.NewGuid();
        fx.App(AppToolNames.GetTab).Respond = _ => AgentToolOutput.Ok(new JsonObject { ["text"] = "SELECT 1 FROM dual" }) with { ProfileId = outside };

        await fx.Call(AppToolNames.GetTab, """{"tab":"t1"}""");

        var audit = fx.Inner.Audit.Entries[^1];
        Assert.Null(audit.ProfileId);
        Assert.Null(audit.ProfileName);
        Assert.Equal(fx.Issue.Grant.GrantId, audit.RunId);
        Assert.Equal(AiAccessLevel.Off, audit.AccessLevel);
        Assert.Contains("connection=outside_grant", audit.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("data=local_ai", audit.Arguments ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_after_the_tab_was_found_is_audited_on_its_connection()
    {
        var fx = new ExternalFixture(true, ExtDev());
        fx.App(AppToolNames.RunTab).Respond = _ => AppToolOutputs.Deny("tab_busy", "The tab is busy.") with { ProfileId = ExtDevId };

        var response = await fx.Call(AppToolNames.RunTab, """{"tab":"t1"}""");

        Assert.True(response.Result.IsError);
        var audit = fx.Inner.Audit.Entries[^1];
        Assert.Equal(ExtDevId, audit.ProfileId);
        Assert.StartsWith("data=local_ai", audit.Arguments, StringComparison.Ordinal);
        Assert.Equal("tab_busy", audit.Reason);
    }

    [Fact]
    public async Task A_reported_connection_does_not_make_the_gate_mask_or_withhold_the_handler_result()
    {
        // Query 수준 접속에서 게이트가 가리면 get_tab 의 글 모양은 가릴 수 없어 unsafe_result 로 막힌다. 알린 접속은 감사에만 쓴다.
        var fx = new ExternalFixture(ExtDev(AiAccessLevel.Query));
        fx.App(AppToolNames.GetTab).Respond = _ => AgentToolOutput.Ok(new JsonObject { ["text"] = "SELECT 1 FROM dual", ["version"] = 4 }) with { ProfileId = ExtDevId };

        var response = await fx.Call(AppToolNames.GetTab, """{"tab":"t1"}""");

        Assert.False(response.Result.IsError, response.Result.Text);
        Assert.Equal("SELECT 1 FROM dual", JsonNode.Parse(response.Result.Text)!["text"]!.GetValue<string>());
        Assert.Equal(AiToolJudgement.Allowed, response.Judgement);
        Assert.Equal(ExtDevId, fx.Inner.Audit.Entries[^1].ProfileId);
    }

    [Fact]
    public async Task The_call_connection_wins_over_a_reported_one()
    {
        var fx = new ExternalFixture(ExtDev(), ExtTest());
        fx.App(AppToolNames.NewTab).Respond = _ => AgentToolOutput.Ok(new JsonObject { ["tab_id"] = "t1", ["version"] = 1 }) with { ProfileId = ExtTestId };

        await fx.Call(AppToolNames.NewTab, """{"text":"a","connection":"dev-erp"}""");

        Assert.Equal(ExtDevId, fx.Inner.Audit.Entries[^1].ProfileId);
    }
}
