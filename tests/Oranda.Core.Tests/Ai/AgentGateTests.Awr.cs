using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;

namespace Oranda.Core.Tests.Ai;

// AWR 계획 이력 도구 두 개(AI 확대 §3.1): 허용 수준·sql_id 범위·인자 검사·SQL 글 가림 대상 여부.
public partial class AgentGateTests
{
    private const string AwrSqlId = "7b2kq9x1m3f0d";

    [Fact]
    public async Task Awr_tools_need_the_plan_level_and_a_sql_id_known_to_the_request()
    {
        var fx = new GateFixture();
        var known = Session(sqlId: AwrSqlId);

        Assert.False((await Call(fx, known, AgentToolNames.GetAwrPlanHistory, $$"""{"sql_id":"{{AwrSqlId}}"}""")).Result.IsError);
        Assert.False((await Call(fx, known, AgentToolNames.GetAwrPlan, $$"""{"sql_id":"{{AwrSqlId}}","plan_hash_value":3940012871}""")).Result.IsError);
        AssertDenied(await Call(fx, known, AgentToolNames.GetAwrPlanHistory, """{"sql_id":"0000000000000"}"""), "sql_id_scope");
        AssertDenied(await Call(fx, Session(Dev(AiAccessLevel.Metadata), AiAccessLevel.Metadata, sqlId: AwrSqlId), AgentToolNames.GetAwrPlanHistory, $$"""{"sql_id":"{{AwrSqlId}}"}"""), "level");
        Assert.Equal(2, fx.Handlers[AgentToolNames.GetAwrPlanHistory].Calls.Count + fx.Handlers[AgentToolNames.GetAwrPlan].Calls.Count);
    }

    [Theory]
    [InlineData("""{"sql_id":"7b2kq9x1m3f0d"}""")]
    [InlineData("""{"sql_id":"7b2kq9x1m3f0d","plan_hash_value":-1}""")]
    [InlineData("""{"sql_id":"7b2kq9x1m3f0d","plan_hash_value":4294967296}""")]
    [InlineData("""{"sql_id":"7b2kq9x1m3f0d","plan_hash_value":"3940012871"}""")]
    public async Task Awr_plan_refuses_a_missing_or_out_of_range_plan_hash(string json)
    {
        var fx = new GateFixture();

        AssertDenied(await Call(fx, Session(sqlId: AwrSqlId), AgentToolNames.GetAwrPlan, json), "invalid_arguments");
        Assert.Empty(fx.Handlers[AgentToolNames.GetAwrPlan].Calls);
    }

    [Fact]
    public async Task Awr_plan_carries_predicates_so_it_closes_without_a_vault_but_the_history_does_not()
    {
        // 계획 줄의 access·filter 는 SQL 글이라 가림이 필요한 실행에서 사전이 없으면 닫는다. 이력은 숫자뿐이라 막지 않는다.
        var fx = new GateFixture();
        var dev = Dev(AiAccessLevel.Plan);
        var ctx = new AiContext(dev.Name, dev.Environment, AiAccessLevel.Plan, StatementText: Statement, SqlId: AwrSqlId);
        var session = new AgentSession(new AgentRunScope("run-awr", dev, ctx) { MaskLiteralsRequired = true });

        AssertDenied(await Call(fx, session, AgentToolNames.GetAwrPlan, $$"""{"sql_id":"{{AwrSqlId}}","plan_hash_value":1}"""), "literal_mask_unavailable");
        Assert.False((await Call(fx, session, AgentToolNames.GetAwrPlanHistory, $$"""{"sql_id":"{{AwrSqlId}}"}""")).Result.IsError);
    }
}
