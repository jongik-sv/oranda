using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using Oranda.Language.Ai.Handlers;
using Oranda.Language.Ai.Tools;
using Oranda.Testing;

namespace Oranda.Language.Tests.Ai.Handlers;

// 운영 벤치마크 제한 시간: 값은 실행 범위에서만 오고(도구 인자로 바꿀 수 없다), 0 은 취소 타이머를 걸지 않으며, 개발·테스트 접속은 제한이 없다.
public class BenchmarkTimeoutTests
{
    /// <summary>실행을 시작하면 취소될 때까지(또는 <c>Hold</c> 가 지날 때까지) 기다린다. 받은 취소 토큰이 취소됐는지를 남긴다.</summary>
    private sealed class WaitingTuning(TimeSpan? hold = null) : ITuningService
    {
        public bool Started { get; private set; }

        public bool CancelledByLimit { get; private set; }

        public Task<TuningCandidateSet> GetTuningCandidatesAsync(ISession s, TuningCandidateQuery query, CancellationToken ct) =>
            Task.FromResult(new TuningCandidateSet([], 0));

        public async Task<BenchmarkResult> RunBenchmarkAsync(ISession s, BenchmarkCase benchmark, int iterations, CancellationToken ct)
        {
            Started = true;
            try
            {
                await Task.Delay(hold ?? Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                CancelledByLimit = true;
                throw;
            }

            return new BenchmarkResult(benchmark, iterations, TimeSpan.FromMilliseconds(5), 10, 0, 1, 99);
        }
    }

    private sealed class NoKeys : ITableKeyDetailProvider
    {
        public Task<SchemaFolderResult<IndexColumnDetail>> GetIndexColumnsAsync(ISession s, DbObjectRef table, string indexName, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SchemaFolderResult<ConstraintColumnDetail>> GetConstraintColumnsAsync(ISession s, DbObjectRef table, string constraintName, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class NoPlans : IExecutionPlanService
    {
        public Task<ExecutionPlan> ExplainAsync(ISession s, SqlStatement stmt, CancellationToken ct) => throw new NotSupportedException();

        public Task<ExecutionPlan> GetCursorPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct) => throw new NotSupportedException();
    }

    private static BenchmarkQueriesHandler Handler(ITuningService tuning)
    {
        var meta = new FakeMetadataProvider();
        var boxes = new AgentToolboxProvider(tuning, meta, new NoKeys(), new NoPlans(), new LanguageService());
        return new BenchmarkQueriesHandler(boxes, new AgentStatementReferenceExtractor(new LanguageService(), meta));
    }

    /// <param name="seconds">실행 범위에 넣을 제한 시간. null 이면 칸을 채우지 않는다(기본값).</param>
    private static AgentToolInvocation Invoke(ConnectionEnvironment environment, int? seconds, JsonObject? extraArguments = null)
    {
        var profile = TestData.Profile(environment);
        var run = new AgentRunScope("run-t", profile, new AiContext("test", environment, AiAccessLevel.Query));
        if (seconds is { } value)
        {
            run = run with { BenchmarkTimeoutSeconds = value };
        }

        var arguments = new JsonObject
        {
            ["variants"] = new JsonArray(new JsonObject { ["label"] = "v0", ["sql"] = "SELECT 1 FROM dual" }),
        };
        foreach (var (key, node) in extraArguments ?? [])
        {
            arguments[key] = node?.DeepClone();
        }

        return new AgentToolInvocation(new AgentSession(run), new FakeSession(profile), AiAccessLevel.Query, arguments,
            new Dictionary<string, AgentObjectRef>(), MayExecuteSql: true);
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(5, 5)]
    [InlineData(600, 600)]
    [InlineData(3600, 3600)]
    [InlineData(4, 30)] // 범위 밖은 제한 없음이 아니라 30초로 닫는다
    [InlineData(-1, 30)]
    [InlineData(3601, 30)]
    public void The_limit_is_built_from_the_scope_seconds(int seconds, int expected) =>
        Assert.Equal(TimeSpan.FromSeconds(expected), TuningToolbox.ProductionStatementTimeout(seconds));

    [Fact]
    public void Zero_means_no_limit_so_no_cancel_timer_is_built() => Assert.Null(TuningToolbox.ProductionStatementTimeout(0));

    [Fact]
    public async Task A_production_run_is_stopped_at_the_scope_value_and_the_message_names_the_real_seconds()
    {
        var tuning = new WaitingTuning();

        var r = await Handler(tuning).ExecuteAsync(Invoke(ConnectionEnvironment.Production, 5), CancellationToken.None);

        Assert.True(r.IsError);
        Assert.Contains("제한 시간(5초)을 넘겨 중단됐습니다", r.ErrorMessage, StringComparison.Ordinal);
        Assert.True(tuning.CancelledByLimit);
    }

    [Fact]
    public async Task The_model_cannot_change_the_limit_with_tool_arguments()
    {
        // 스키마에 없는 인자를 보내도 실행 범위의 값만 쓴다(0 을 보내 제한을 풀 수 없다).
        var tuning = new WaitingTuning();
        var extra = new JsonObject { ["timeout_seconds"] = 0, ["timeoutSeconds"] = 0, ["statement_timeout"] = 0, ["benchmarkTimeoutSeconds"] = 0 };

        var r = await Handler(tuning).ExecuteAsync(Invoke(ConnectionEnvironment.Production, 5, extra), CancellationToken.None);

        Assert.True(r.IsError);
        Assert.Contains("제한 시간(5초)", r.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tool_definition_has_no_time_argument_and_no_fixed_seconds_in_its_text()
    {
        var tool = AgentTools.Find(AgentToolNames.BenchmarkQueries)!;
        var schema = JsonNode.Parse(tool.InputSchemaJson)!.AsObject();

        Assert.False(schema["additionalProperties"]!.GetValue<bool>()); // 모르는 인자는 스키마가 거부한다
        Assert.Equal(["connection", "variants", "repeat"], schema["properties"]!.AsObject().Select(p => p.Key));
        Assert.DoesNotContain("timeout", tool.InputSchemaJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("second", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("30", tool.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_zero_the_run_is_not_cancelled_by_a_timer_but_the_user_stop_still_works()
    {
        var tuning = new WaitingTuning();
        using var stop = new CancellationTokenSource();

        var run = Handler(tuning).ExecuteAsync(Invoke(ConnectionEnvironment.Production, 0), stop.Token).AsTask();
        while (!tuning.Started)
        {
            await Task.Delay(5);
        }

        await Task.Delay(300);
        Assert.False(run.IsCompleted); // 제한이 없으니 타이머로는 끝나지 않는다
        Assert.False(tuning.CancelledByLimit);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); // 사용자의 중지는 제한 시간 메시지가 아니라 취소로 끝난다
        Assert.True(tuning.CancelledByLimit);
    }

    [Fact]
    public async Task A_scope_with_the_field_unfilled_is_limited_to_30_seconds_in_production_not_unlimited()
    {
        var tuning = new WaitingTuning();
        using var stop = new CancellationTokenSource();

        var run = Handler(tuning).ExecuteAsync(Invoke(ConnectionEnvironment.Production, null), stop.Token).AsTask();
        while (!tuning.Started)
        {
            await Task.Delay(5);
        }

        // 30초 타이머가 걸렸는지는 값 변환으로 고정한다(30초를 기다리지 않는다). 칸을 채우지 않은 범위는 30이다.
        Assert.Equal(30, Invoke(ConnectionEnvironment.Production, null).Session.Run.BenchmarkTimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(30), TuningToolbox.ProductionStatementTimeout(Invoke(ConnectionEnvironment.Production, null).Session.Run.BenchmarkTimeoutSeconds));

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Theory]
    [InlineData(ConnectionEnvironment.Development)]
    [InlineData(ConnectionEnvironment.Test)]
    public async Task Development_and_test_connections_have_no_limit_whatever_the_scope_value(ConnectionEnvironment environment)
    {
        // 범위 값이 5초여도 개발·테스트 접속은 제한이 없다: 5초보다 오래 걸리는 실행이 그대로 끝난다.
        var tuning = new WaitingTuning(TimeSpan.FromSeconds(5.5));

        var r = await Handler(tuning).ExecuteAsync(Invoke(environment, 5), CancellationToken.None);

        Assert.False(r.IsError, r.ErrorMessage);
        Assert.False(tuning.CancelledByLimit);
    }
}
