using System.Text.Json.Nodes;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Sessions;

namespace Oranda.Core.Tests.Ai;

/// <summary>
/// 사용자 함수·패키지 함수가 든 SQL(c7): 해석·조회 도구는 확인 없이 허용한다(운영에서 튜닝하려면 계획을 봐야 한다).
/// 실행 도구(benchmark_queries)는 확인을 통과한 변형만 읽기 전용으로 실행하고, 위험 함수·DB 링크·시퀀스·분류 못 한 호출이 든 변형은
/// 거부하지 않고 실행 없이 커서 실측으로 대체한다(성공, 감사 substituted=cursor_stats). 「위험 함수도 실행」 을 켜면 위험 함수만 읽기 전용으로 실행한다.
/// </summary>
public partial class AgentGateTests
{
    private const string WithFunction = "SELECT PKG_UTIL.FN(o.id) FROM TB_ORDER o";

    private static string Variants(params string[] sql) =>
        new JsonObject { ["variants"] = new JsonArray([.. sql.Select((s, i) => (JsonNode)new JsonObject { ["label"] = "v" + i, ["sql"] = s })]) }.ToJsonString();

    [Fact]
    public async Task Non_executing_tools_allow_a_user_function_without_checking_it_and_audit_the_count()
    {
        var checker = new FakeFunctionChecker(UserFunctionVerdict.Autonomous);
        var fx = new GateFixture(userFunctions: checker);

        var explain = await Call(fx, Session(), AgentToolNames.ExplainPlan, new JsonObject { ["sql"] = WithFunction }.ToJsonString());
        var context = await Call(fx, Session(statement: WithFunction), AgentToolNames.GetTuningContext, "{}");

        Assert.False(explain.Result.IsError);
        Assert.False(context.Result.IsError);
        Assert.Empty(checker.Calls);
        Assert.False(fx.Handlers[AgentToolNames.ExplainPlan].Calls.Single().ReadOnlyExecution);
        Assert.Contains("user_functions=1", fx.Audit.Entries[0].Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Benchmark_with_a_checked_user_function_runs_read_only()
    {
        var checker = new FakeFunctionChecker(UserFunctionVerdict.Safe);
        var fx = new GateFixture(userFunctions: checker);

        Assert.False((await Call(fx, Session(), AgentToolNames.BenchmarkQueries, Variants(WithFunction))).Result.IsError);
        Assert.False((await Call(fx, Session(), AgentToolNames.BenchmarkQueries, Variants("SELECT * FROM TB_ORDER"))).Result.IsError);

        var calls = fx.Handlers[AgentToolNames.BenchmarkQueries].Calls;
        Assert.True(calls[0].ReadOnlyExecution);
        Assert.Null(calls[0].Substitutions);
        Assert.False(calls[1].ReadOnlyExecution); // 함수가 없는 문장은 평소대로
        Assert.Equal(["PKG_UTIL.FN"], checker.Calls.Single());
    }

    [Theory]
    [InlineData(UserFunctionVerdict.Autonomous, "user_function_autonomous")]
    [InlineData(UserFunctionVerdict.External, "user_function_external")]
    [InlineData(UserFunctionVerdict.Unverified, "user_function_unverified")]
    public async Task Benchmark_substitutes_cursor_stats_for_a_risky_function_instead_of_denying(UserFunctionVerdict verdict, string code)
    {
        var fx = new GateFixture(userFunctions: new FakeFunctionChecker(verdict));

        var response = await Call(fx, Session(), AgentToolNames.BenchmarkQueries, Variants("SELECT * FROM TB_ORDER", WithFunction));

        Assert.False(response.Result.IsError);
        Assert.Equal(AiToolJudgement.Allowed, response.Judgement);
        Assert.Equal("substituted: " + code, response.Note);
        var call = fx.Handlers[AgentToolNames.BenchmarkQueries].Calls.Single();
        Assert.Equal([null, code], call.Substitutions);
        Assert.False(call.ReadOnlyExecution);
        var audit = fx.Audit.Entries.Single().Arguments;
        Assert.Contains("user_functions=1", audit, StringComparison.Ordinal);
        Assert.Contains("substituted=cursor_stats", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Benchmark_without_a_checker_substitutes_as_unverified()
    {
        var fx = new GateFixture();

        var response = await Call(fx, Session(), AgentToolNames.BenchmarkQueries, Variants(WithFunction));

        Assert.False(response.Result.IsError);
        Assert.Equal(["user_function_unverified"], fx.Handlers[AgentToolNames.BenchmarkQueries].Calls.Single().Substitutions);
    }

    [Theory]
    [InlineData(UserFunctionVerdict.Autonomous)]
    [InlineData(UserFunctionVerdict.External)]
    [InlineData(UserFunctionVerdict.Unverified)]
    public async Task Run_risky_functions_executes_them_read_only(UserFunctionVerdict verdict)
    {
        var fx = new GateFixture(userFunctions: new FakeFunctionChecker(verdict));

        var response = await Call(fx, Session(allowRiskyFunctions: true), AgentToolNames.BenchmarkQueries, Variants(WithFunction));

        Assert.False(response.Result.IsError);
        Assert.Null(response.Note);
        var call = fx.Handlers[AgentToolNames.BenchmarkQueries].Calls.Single();
        Assert.True(call.ReadOnlyExecution);
        Assert.Null(call.Substitutions);
    }

    [Theory]
    [InlineData("SELECT SEQ_ORDER.NEXTVAL FROM TB_ORDER", "sql_unsafe")]
    [InlineData("SELECT * FROM TB_ORDER@REMOTE_DB", "db_link")]
    [InlineData("SELECT UTL_HTTP.REQUEST(o.url) FROM TB_ORDER o", "sql_unsafe")]
    [InlineData("SELECT UNCLASSIFIED_CALL FROM TB_ORDER", "sql_unsafe")]
    public async Task Sequences_links_and_unclassified_calls_are_denied_for_plans_and_substituted_for_benchmarks_even_with_risky_functions_on(string sql, string code)
    {
        var fx = new GateFixture(userFunctions: new FakeFunctionChecker(UserFunctionVerdict.Safe));

        AssertDenied(await Call(fx, Session(), AgentToolNames.ExplainPlan, new JsonObject { ["sql"] = sql }.ToJsonString()), "sql_unsafe");
        var bench = await Call(fx, Session(allowRiskyFunctions: true), AgentToolNames.BenchmarkQueries, Variants(sql));

        Assert.False(bench.Result.IsError);
        var call = fx.Handlers[AgentToolNames.BenchmarkQueries].Calls.Single();
        Assert.Equal([code], call.Substitutions);
        Assert.False(call.ReadOnlyExecution);
    }

    [Fact]
    public async Task Locking_and_out_of_scope_variants_are_still_denied()
    {
        var fx = new GateFixture(userFunctions: new FakeFunctionChecker(UserFunctionVerdict.Autonomous));

        AssertDenied(await Call(fx, Session(), AgentToolNames.BenchmarkQueries, Variants("SELECT * FROM TB_ORDER FOR UPDATE")), "sql_unsafe");
        AssertDenied(await Call(fx, Session(), AgentToolNames.BenchmarkQueries, Variants("SELECT PKG_UTIL.FN(x.id) FROM SECRET_TABLE x")), "scope");
        Assert.Empty(fx.Handlers[AgentToolNames.BenchmarkQueries].Calls);
    }

    private sealed class FakeFunctionChecker(UserFunctionVerdict verdict) : IUserFunctionSafetyChecker
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public ValueTask<UserFunctionVerdict> CheckAsync(ISession db, IReadOnlyList<string> functions, string? defaultSchema, CancellationToken ct)
        {
            Calls.Add(functions);
            return ValueTask.FromResult(verdict);
        }
    }
}
