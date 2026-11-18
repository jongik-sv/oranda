using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
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

/// <summary>AWR 계획 이력 도구 두 개(AI 확대 §3.1): 계획별 요약·스냅숏 줄·계획 줄, 쓸 수 없는 서버의 「지원 안 함」 결과, 오류 글.</summary>
public class AwrToolHandlersTests
{
    private const string SqlId = "7b2kq9x1m3f0d";

    private sealed class Tuning : ITuningService
    {
        public IReadOnlyList<AwrPlanRow> History { get; set; } = [];

        public Exception? Failure { get; set; }

        public List<(string SqlId, long Hash)> PlanCalls { get; } = [];

        public Task<IReadOnlyList<AwrPlanRow>> GetAwrPlanHistoryAsync(ISession s, string sqlId, CancellationToken ct) =>
            Failure is { } f ? Task.FromException<IReadOnlyList<AwrPlanRow>>(f) : Task.FromResult(History);

        public Task<ExecutionPlan> GetAwrPlanAsync(ISession s, string sqlId, long planHashValue, CancellationToken ct)
        {
            PlanCalls.Add((sqlId, planHashValue));
            if (Failure is { } f)
            {
                return Task.FromException<ExecutionPlan>(f);
            }

            IReadOnlyList<PlanStep> steps = planHashValue == 1 ? [] :
            [
                new PlanStep(0, null, 0, "SELECT STATEMENT") { Cost = 9 },
                new PlanStep(1, 0, 1, "TABLE ACCESS") { Options = "FULL", ObjectOwner = "SAMPLE", ObjectName = "ORDERS", Cost = 8, EstimatedRows = 40, FilterPredicates = "\"STATUS\"='N'" },
            ];
            return Task.FromResult(new ExecutionPlan(steps, "text") { PlanHashValue = planHashValue });
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

    private static AgentToolboxProvider Boxes(Tuning tuning) => new(tuning, new FakeMetadataProvider(), new NoKeys(), new NoPlans(), new LanguageService());

    private static AgentToolInvocation Invoke(JsonObject args, Dbms dbms = Dbms.Oracle)
    {
        var profile = TestData.Profile(licensePack: LicensePack.Diagnostics) with { Dbms = dbms };
        var run = new AgentRunScope("run-awr", profile, new AiContext("test", profile.Environment, AiAccessLevel.Plan, SqlId: SqlId));
        return new AgentToolInvocation(new AgentSession(run), new FakeSession(profile), AiAccessLevel.Plan, args, new Dictionary<string, AgentObjectRef>());
    }

    private static AwrPlanRow Row(long snap, int hour, long hash, long execs, long elapsedMicros, long buffers) =>
        new(snap, new DateTime(2026, 11, 12, hour, 0, 0, DateTimeKind.Unspecified), hash, execs, elapsedMicros, buffers);

    [Fact]
    public async Task History_sums_each_plan_and_lists_snapshots_in_order()
    {
        var tuning = new Tuning
        {
            History =
            [
                Row(12, 3, 3940012871, 405, 405 * 14_200_000L, 405 * 918_442L),
                Row(10, 1, 1288391044, 400, 400 * 800_000L, 400 * 3_200L),
                Row(11, 2, 1288391044, 0, 0, 0),
            ],
        };

        var output = await new GetAwrPlanHistoryHandler(Boxes(tuning)).ExecuteAsync(Invoke(new JsonObject { ["sql_id"] = SqlId }), default);

        Assert.False(output.IsError, output.ErrorMessage);
        var r = output.Result!;
        Assert.True(r["supported"]!.GetValue<bool>());
        var plans = r["plans"]!.AsArray();
        Assert.Equal([1288391044L, 3940012871L], plans.Select(p => p!["plan_hash"]!.GetValue<long>()));
        Assert.Equal(400, plans[0]!["executions"]!.GetValue<long>());
        Assert.Equal(800d, plans[0]!["elapsed_ms_per_exec"]!.GetValue<double>(), 3);
        Assert.Equal("2026-11-12 01:00", plans[0]!["first_snap_time"]!.GetValue<string>());
        Assert.Equal("2026-11-12 02:00", plans[0]!["last_snap_time"]!.GetValue<string>());
        Assert.Equal(918_442d, plans[1]!["buffer_gets_per_exec"]!.GetValue<double>(), 3);
        var snaps = r["snapshots"]!.AsArray();
        Assert.Equal([10L, 11L, 12L], snaps.Select(x => x!["snap_id"]!.GetValue<long>()));
        Assert.Null(snaps[1]!["elapsed_ms_per_exec"]); // 실행 0 인 구간은 실행당 값이 없다
    }

    [Fact]
    public async Task History_keeps_the_latest_snapshots_and_counts_the_rest()
    {
        var tuning = new Tuning { History = [.. Enumerable.Range(1, AwrToolLimits.MaxSnapshots + 5).Select(i => Row(i, 1, 42, 1, 1000, 1))] };

        var r = (await new GetAwrPlanHistoryHandler(Boxes(tuning)).ExecuteAsync(Invoke(new JsonObject { ["sql_id"] = SqlId }), default)).Result!;

        Assert.Equal(AwrToolLimits.MaxSnapshots, r["snapshots"]!.AsArray().Count);
        Assert.Equal(6, r["snapshots"]![0]!["snap_id"]!.GetValue<long>());
        Assert.Equal(5, r["omitted_snapshots"]!.GetValue<int>());
        Assert.Equal(AwrToolLimits.MaxSnapshots + 5, r["plans"]![0]!["snapshots"]!.GetValue<int>()); // 요약은 전부로
    }

    [Fact]
    public async Task Plan_lists_the_lines_with_predicates_in_the_masked_field_names()
    {
        var tuning = new Tuning();

        var output = await new GetAwrPlanHandler(Boxes(tuning)).ExecuteAsync(Invoke(new JsonObject { ["sql_id"] = SqlId, ["plan_hash_value"] = 3940012871L }), default);

        Assert.False(output.IsError, output.ErrorMessage);
        var r = output.Result!;
        Assert.True(r["found"]!.GetValue<bool>());
        Assert.Equal(3940012871L, r["plan_hash"]!.GetValue<long>());
        var full = r["steps"]![1]!;
        Assert.Equal("TABLE ACCESS FULL", full["operation"]!.GetValue<string>());
        Assert.Equal("SAMPLE.ORDERS", full["object_name"]!.GetValue<string>());
        Assert.Equal("\"STATUS\"='N'", full["filter"]!.GetValue<string>()); // AgentFields.SqlTextFields 의 이름이라 게이트가 가린다
        Assert.Equal((SqlId, 3940012871L), Assert.Single(tuning.PlanCalls));
    }

    [Fact]
    public async Task Plan_without_lines_says_not_found_and_a_missing_hash_is_an_error()
    {
        var tuning = new Tuning();
        var handler = new GetAwrPlanHandler(Boxes(tuning));

        var empty = (await handler.ExecuteAsync(Invoke(new JsonObject { ["sql_id"] = SqlId, ["plan_hash_value"] = 1 }), default)).Result!;
        var missing = await handler.ExecuteAsync(Invoke(new JsonObject { ["sql_id"] = SqlId }), default);

        Assert.False(empty["found"]!.GetValue<bool>());
        Assert.Empty(empty["steps"]!.AsArray());
        Assert.True(missing.IsError);
        Assert.Contains("plan_hash_value", missing.ErrorMessage, StringComparison.Ordinal);
        Assert.Single(tuning.PlanCalls);
    }

    [Fact]
    public async Task Without_the_pack_both_tools_answer_not_supported_instead_of_failing()
    {
        // 카탈로그 관문(CatalogLicenseGate)이 보내기 전에 막은 경우: 오류가 아니라 「지원 안 함」 + 까닭(접속 이름 등 원문은 싣지 않는다).
        var tuning = new Tuning { Failure = new UnauthorizedAccessException("이 조회는 Diagnostics 팩이 필요합니다. 접속 'secret-alias' 의 보유 선언(또는 서버 설정)이 없어 보내지 않았습니다.") };

        var history = await new GetAwrPlanHistoryHandler(Boxes(tuning)).ExecuteAsync(Invoke(new JsonObject { ["sql_id"] = SqlId }), default);
        var plan = await new GetAwrPlanHandler(Boxes(tuning)).ExecuteAsync(Invoke(new JsonObject { ["sql_id"] = SqlId, ["plan_hash_value"] = 5 }), default);

        foreach (var output in new[] { history, plan })
        {
            Assert.False(output.IsError, output.ErrorMessage);
            Assert.False(output.Result!["supported"]!.GetValue<bool>());
            Assert.Contains("Diagnostics Pack", output.Result["reason"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.DoesNotContain("secret-alias", output.Result.ToJsonString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_dbms_without_awr_history_is_not_supported_and_db_errors_keep_only_the_code()
    {
        var unsupported = new Tuning { Failure = new NotSupportedException() };
        var broken = new Tuning { Failure = new InvalidOperationException("ORA-00942: table or view \"SYS\".\"DBA_HIST_SQLSTAT\" does not exist") };

        var none = (await new GetAwrPlanHistoryHandler(Boxes(unsupported)).ExecuteAsync(Invoke(new JsonObject { ["sql_id"] = SqlId }, Dbms.Tibero), default)).Result!;
        var error = await new GetAwrPlanHistoryHandler(Boxes(broken)).ExecuteAsync(Invoke(new JsonObject { ["sql_id"] = SqlId }), default);

        Assert.False(none["supported"]!.GetValue<bool>());
        Assert.True(error.IsError);
        Assert.Equal("조회에 실패했습니다 (ORA-00942).", error.ErrorMessage);
    }
}
