using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;

namespace Oranda.Core.Tests.Ai;

// D-045: 「로컬 AI」 실행(AiContext.LocalAi)은 운영 접속에서도 사전 없이 원문으로 돌고 값을 그대로 낸다. 비밀번호·주민번호·카드·계좌·주소·인증값 열은 지운다.
public partial class AgentGateTests
{
    private const string StatsResult = """
        {"columns":[
          {"name":"PHONE_NO","low_value":"010-LOW","high_value":"010-HIGH"},
          {"name":"USER_PWD","low_value":"PWD-LOW","high_value":"PWD-HIGH"},
          {"name":"JUMIN_NO","low_value":"RRN-LOW","high_value":"RRN-HIGH"},
          {"name":"CARD_NO","low_value":"CARD-LOW","high_value":"CARD-HIGH"},
          {"name":"ACCOUNT_NO","low_value":"ACCT-LOW","high_value":"ACCT-HIGH"},
          {"name":"CUST_ADDR","low_value":"ADDR-LOW","high_value":"ADDR-HIGH"}
        ]}
        """;

    private static AgentSession LocalSession(ConnectionProfile? profile = null, AiAccessLevel context = AiAccessLevel.QueryRows, bool localAi = true)
    {
        profile ??= Prod(AiAccessLevel.Query);
        var ctx = new AiContext(profile.Name, profile.Environment, context, StatementText: Statement, LocalAi: localAi);
        return new AgentSession(new AgentRunScope("run-l", profile, ctx)) { Client = "test-client" };
    }

    [Fact]
    public async Task A_production_run_for_local_ai_uses_the_sql_tools_with_the_original_text_and_no_vault()
    {
        var fx = new GateFixture(masker: new FakeMasker());

        var response = await Call(fx, LocalSession(), AgentToolNames.ExplainPlan, $$"""{"sql":"{{Statement}}"}""");

        Assert.False(response.Result.IsError, response.Result.Text);
        Assert.Equal(Statement, (string?)Assert.Single(fx.Handlers[AgentToolNames.ExplainPlan].Calls).Arguments["sql"]);
    }

    [Fact]
    public async Task The_same_production_run_without_the_flag_is_still_closed()
    {
        var fx = new GateFixture(masker: new FakeMasker());

        var response = await Call(fx, LocalSession(localAi: false), AgentToolNames.ExplainPlan, $$"""{"sql":"{{Statement}}"}""");

        AssertDenied(response, "literal_mask_unavailable");
        Assert.Empty(fx.Handlers[AgentToolNames.ExplainPlan].Calls);
    }

    [Fact]
    public async Task A_vault_still_masks_when_a_run_has_one_even_with_the_flag()
    {
        var fx = new GateFixture(masker: new FakeMasker());
        var profile = Prod(AiAccessLevel.Query);
        var vault = new LiteralVault();
        vault.Add("'A'");
        var ctx = new AiContext(profile.Name, profile.Environment, AiAccessLevel.Query, StatementText: Statement, LocalAi: true);
        var session = new AgentSession(new AgentRunScope("run-v", profile, ctx) { Literals = vault }) { Client = "test-client" };

        var response = await Call(fx, session, AgentToolNames.ExplainPlan, """{"sql":"SELECT * FROM TB_ORDER o WHERE o.stat_cd = :v1"}""");

        Assert.False(response.Result.IsError, response.Result.Text);
        Assert.Equal(Statement, (string?)Assert.Single(fx.Handlers[AgentToolNames.ExplainPlan].Calls).Arguments["sql"]);
    }

    [Fact]
    public async Task Local_ai_keeps_statistics_values_and_drops_only_the_always_hidden_columns()
    {
        var fx = new GateFixture();
        fx.Handlers[AgentToolNames.GetTableStats].Respond = _ => AgentToolOutput.Ok(JsonNode.Parse(StatsResult)!);

        var response = await Call(fx, LocalSession(), AgentToolNames.GetTableStats, """{"table":"TB_ORDER"}""");

        Assert.False(response.Result.IsError, response.Result.Text);
        Assert.Contains("010-LOW", response.Result.Text, StringComparison.Ordinal); // 전화번호는 로컬 AI 에 보낸다
        Assert.Contains("010-HIGH", response.Result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("PWD-LOW", response.Result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("PWD-HIGH", response.Result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("RRN-LOW", response.Result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("RRN-HIGH", response.Result.Text, StringComparison.Ordinal);
        foreach (var hidden in new[] { "CARD-LOW", "CARD-HIGH", "ACCT-LOW", "ACCT-HIGH", "ADDR-LOW", "ADDR-HIGH" })
        {
            Assert.DoesNotContain(hidden, response.Result.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Without_the_flag_a_production_run_drops_every_statistics_value()
    {
        var fx = new GateFixture();
        fx.Handlers[AgentToolNames.GetTableStats].Respond = _ => AgentToolOutput.Ok(JsonNode.Parse(StatsResult)!);

        var response = await Call(fx, LocalSession(localAi: false), AgentToolNames.GetTableStats, """{"table":"TB_ORDER"}""");

        Assert.False(response.Result.IsError, response.Result.Text);
        Assert.DoesNotContain("010-LOW", response.Result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("PWD-LOW", response.Result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_flag_row_level_runs_still_use_the_wide_sensitive_list()
    {
        var fx = new GateFixture();
        fx.Handlers[AgentToolNames.GetTableStats].Respond = _ => AgentToolOutput.Ok(JsonNode.Parse(StatsResult)!);

        var response = await Call(fx, LocalSession(Dev(AiAccessLevel.QueryRows), localAi: false), AgentToolNames.GetTableStats, """{"table":"TB_ORDER"}""");

        Assert.False(response.Result.IsError, response.Result.Text);
        Assert.DoesNotContain("010-LOW", response.Result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PHONE_NO", true)]
    [InlineData("USER_PWD", false)]
    [InlineData("주민번호", false)]
    [InlineData("CARD_NO", false)]
    [InlineData("ACCOUNT_NO", false)]
    [InlineData("계좌번호", false)]
    [InlineData("CUST_ADDR", false)]
    [InlineData("주소", false)]
    [InlineData("ACCESS_TOKEN", false)]
    [InlineData("CLIENT_SECRET", false)]
    public async Task A_local_ai_histogram_keeps_endpoints_unless_the_column_is_always_hidden(string column, bool kept)
    {
        var fx = new GateFixture();
        fx.Handlers[AgentToolNames.GetHistogram].Respond = _ => AgentToolOutput.Ok(JsonNode.Parse("""{"buckets":[{"endpoint":"END-VALUE","count":3}]}""")!);

        var response = await Call(fx, LocalSession(), AgentToolNames.GetHistogram, $$"""{"table":"TB_ORDER","column":"{{column}}"}""");

        Assert.False(response.Result.IsError, response.Result.Text);
        Assert.Equal(kept, response.Result.Text.Contains("END-VALUE", StringComparison.Ordinal));
        Assert.Equal(AiAccessLevel.QueryRows, Assert.Single(fx.Handlers[AgentToolNames.GetHistogram].Calls).Level); // 처리자가 끝값을 읽는다
    }

    [Fact]
    public async Task Local_ai_does_not_add_tools_the_connection_level_does_not_allow()
    {
        var fx = new GateFixture();
        var session = LocalSession(Prod(AiAccessLevel.Plan), AiAccessLevel.Plan);

        AssertDenied(await Call(fx, session, AgentToolNames.BenchmarkQueries, """{"variants":[{"label":"a","sql":"SELECT 1 FROM TB_ORDER"}]}"""), "level");

        Assert.Empty(fx.Handlers[AgentToolNames.BenchmarkQueries].Calls);
        Assert.DoesNotContain(fx.Gate.ListTools(session), t => t.Name == AgentToolNames.BenchmarkQueries);
        Assert.Equal(fx.Gate.ListTools(LocalSession(Prod(AiAccessLevel.Plan), AiAccessLevel.Plan, localAi: false)).Select(t => t.Name), fx.Gate.ListTools(session).Select(t => t.Name));
    }

    [Fact]
    public async Task A_connection_turned_off_refuses_a_local_ai_run()
    {
        var fx = new GateFixture();

        AssertDenied(await Call(fx, LocalSession(Dev(AiAccessLevel.Off), AiAccessLevel.Off), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}"""), "level");

        Assert.Empty(fx.Handlers[AgentToolNames.DescribeObject].Calls);
    }

    [Fact]
    public async Task Local_ai_gets_the_server_error_text_and_a_production_run_without_the_flag_gets_only_the_number()
    {
        const string Message = "ORA-01722: invalid number 'SERVER-VALUE'";
        var fx = new GateFixture(masker: new FakeMasker());
        fx.Handlers[AgentToolNames.ExplainPlan].Respond = _ => AgentToolOutput.Error(Message);
        var masked = Session(Prod(AiAccessLevel.Query), AiAccessLevel.Query);

        var local = await Call(fx, LocalSession(), AgentToolNames.ExplainPlan, $$"""{"sql":"{{Statement}}"}""");
        var plain = await Call(fx, masked, AgentToolNames.ExplainPlan, $$"""{"sql":"{{Statement}}"}""");

        Assert.Contains("SERVER-VALUE", local.Result.Text, StringComparison.Ordinal);
        Assert.Contains("ORA-01722", plain.Result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("SERVER-VALUE", plain.Result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Local_ai_gets_the_original_database_error_the_handler_kept_for_the_log()
    {
        var fx = new GateFixture(masker: new FakeMasker());
        fx.Handlers[AgentToolNames.ExplainPlan].Respond = _ =>
            AgentToolOutput.Error("조회에 실패했습니다 (ORA-00904).") with { LogDetail = "OracleException: ORA-00904: \"X\": invalid identifier" };

        var local = await Call(fx, LocalSession(), AgentToolNames.ExplainPlan, $$"""{"sql":"{{Statement}}"}""");
        var plain = await Call(fx, Session(Prod(AiAccessLevel.Query), AiAccessLevel.Query), AgentToolNames.ExplainPlan, $$"""{"sql":"{{Statement}}"}""");

        Assert.Contains("invalid identifier", local.Result.Text, StringComparison.Ordinal);
        Assert.Contains("ORA-00904", plain.Result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid identifier", plain.Result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bind_value_in_the_original_error_is_not_sent_to_local_ai()
    {
        var fx = new GateFixture();
        fx.Handlers[AgentToolNames.ExplainPlan].Respond = _ =>
            AgentToolOutput.Error("조회에 실패했습니다 (ORA-01722).") with { LogDetail = "OracleException: ORA-01722: invalid number BIND-SECRET" };
        var profile = Prod(AiAccessLevel.Query);
        var ctx = new AiContext(profile.Name, profile.Environment, AiAccessLevel.Query, StatementText: Statement, LocalAi: true);
        var session = new AgentSession(new AgentRunScope("run-b", profile, ctx)
        {
            Binds = new Dictionary<string, Oranda.Core.Execution.BindValue> { ["name"] = new(Oranda.Core.Execution.BindDataType.Varchar2, "BIND-SECRET") },
        }) { Client = "test-client" };

        var response = await Call(fx, session, AgentToolNames.ExplainPlan, $$"""{"sql":"{{Statement}}"}""");

        Assert.Contains("invalid number", response.Result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("BIND-SECRET", response.Result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_audit_row_of_a_local_ai_run_says_so_even_when_it_was_refused()
    {
        var fx = new GateFixture();

        await Call(fx, LocalSession(), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        await Call(fx, LocalSession(), "no_such_tool");
        await Call(fx, LocalSession(localAi: false), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");

        Assert.StartsWith("data=local_ai", fx.Audit.Entries[0].Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("data=local_ai", fx.Audit.Entries[2].Arguments ?? string.Empty, StringComparison.Ordinal);
    }
}
