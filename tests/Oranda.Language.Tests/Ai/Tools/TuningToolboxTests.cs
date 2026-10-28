using System.Text.Json;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using Oranda.Language.Ai.Tools;
using Oranda.Testing;

namespace Oranda.Language.Tests.Ai.Tools;

public class TuningToolboxTests
{
    private static readonly DbObjectRef Order = new("APP", "TB_ORDER", "TABLE");

    /// <summary>공유 풀 조회 가짜: 물은 sql_id 마다 <paramref name="schema"/> 가 파싱한 커서가 있다(schema 가 null 이면 풀에 없음).</summary>
    private static Task<TuningCandidateSet> Pool(TuningCandidateQuery q, string? schema) =>
        Task.FromResult(new TuningCandidateSet(
            schema is null ? [] : [.. (q.SqlIds ?? []).Select(id => new SqlStatsSample(id) { ParsingSchema = schema })], 0));

    private sealed class FakeTuning : ITuningService
    {
        public List<string> Calls { get; } = [];

        public string? PoolSchema { get; set; } = "APP";

        public Task<TuningCandidateSet> GetTuningCandidatesAsync(ISession s, TuningCandidateQuery query, CancellationToken ct) => Pool(query, PoolSchema);

        public Task<TableStatsRow?> GetTableStatsAsync(ISession s, string owner, string table, CancellationToken ct) =>
            Task.FromResult<TableStatsRow?>(new TableStatsRow(owner, table, 1_823_000, 40_000, 90, new DateTime(2026, 10, 27), false));

        public Task<IReadOnlyList<ColumnStatsRow>> GetColumnStatsAsync(ISession s, string owner, string table, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ColumnStatsRow>>(
            [
                new ColumnStatsRow("STAT_CD", 6, 0, 0.0001, "FREQUENCY", 6, null),
                new ColumnStatsRow("ORDER_DT", 4_000_000, 0, 0.0000002, "NONE", 1, null),
            ]);

        public Task<IReadOnlyList<HistogramBucket>> GetHistogramAsync(ISession s, string owner, string table, string column, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<HistogramBucket>>(
            [
                new HistogramBucket(100, "SECRET-A", 100),
                new HistogramBucket(130, "SECRET-B", 30),
                new HistogramBucket(135, "SECRET-C", 5),
            ]);

        public Task<IReadOnlyList<IndexStatsRow>> GetIndexStatsAsync(ISession s, string owner, string table, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IndexStatsRow>>(
            [
                new IndexStatsRow("IX_ORDER_01", "NONUNIQUE", 15_022_110, 61_234, 9_120_044, 3, new DateTime(2026, 10, 27), false),
                new IndexStatsRow("IX_STATS_ONLY", "UNIQUE", null, null, null, null, null, true),
            ]);

        public Task<IReadOnlyList<ChildCursorRow>> GetChildCursorsAsync(ISession s, string sqlId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChildCursorRow>>([new ChildCursorRow(0, 11, true, false, true, 10, 5000, 2_000_000), new ChildCursorRow(1, 12, true, true, true, 3, 900, 300_000)]);

        public Task<IReadOnlyList<SharedCursorReason>> GetSharedCursorReasonsAsync(ISession s, string sqlId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SharedCursorReason>>([new SharedCursorReason(1, "BIND_MISMATCH")]);

        public Task<SqlStatsRow?> GetSqlStatsAsync(ISession s, string sqlId, CancellationToken ct) =>
            Task.FromResult<SqlStatsRow?>(sqlId == "gone" ? null : new SqlStatsRow(2, 1, 4, 8_000_000, 4_000_000, 4000, 40, 400));

        public Task<BenchmarkResult> RunBenchmarkAsync(ISession s, BenchmarkCase benchmark, int iterations, CancellationToken ct)
        {
            Calls.Add($"bench:{benchmark.Label}:{iterations}");
            if (benchmark.Sql.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("SELECT 만 허용합니다.");
            }

            return Task.FromResult(new BenchmarkResult(benchmark, iterations, TimeSpan.FromMilliseconds(12.5), 300, 2, 10, 777));
        }
    }

    private sealed class FakeKeys(bool denied = false) : ITableKeyDetailProvider
    {
        public Task<SchemaFolderResult<IndexColumnDetail>> GetIndexColumnsAsync(ISession s, DbObjectRef table, string indexName, CancellationToken ct) =>
            Task.FromResult(denied || indexName != "IX_ORDER_01"
                ? new SchemaFolderResult<IndexColumnDetail>([], denied)
                : new SchemaFolderResult<IndexColumnDetail>([new(2, "ORDER_DT", null, true), new(1, "CUST_ID", null, false)]));

        public Task<SchemaFolderResult<ConstraintColumnDetail>> GetConstraintColumnsAsync(ISession s, DbObjectRef table, string constraintName, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakePlans : IExecutionPlanService
    {
        public int Explains { get; private set; }

        public Task<ExecutionPlan> ExplainAsync(ISession s, SqlStatement stmt, CancellationToken ct)
        {
            Explains++;
            return Task.FromResult(Plan());
        }

        public Task<ExecutionPlan> GetCursorPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct) =>
            Task.FromResult(Plan() with { Cursor = cursor });

        private static ExecutionPlan Plan() => new(
            [
                new PlanStep(0, null, 0, "SELECT STATEMENT") { Cost = 9000 },
                new PlanStep(1, 0, 1, "TABLE ACCESS") { Options = "FULL", ObjectOwner = "APP", ObjectName = "TB_ORDER", EstimatedRows = 12, ActualRows = 2_103_442, Starts = 1, FilterPredicates = "\"STAT_CD\"=:1", Risk = PlanRisk.FullScan },
            ],
            "plan text")
        {
            PlanHashValue = 2481234567,
        };
    }

    private static (TuningToolbox Box, FakePlans Plans, FakeTuning Tuning) Make(bool keysDenied = false)
    {
        var meta = new FakeMetadataProvider();
        meta.Tables[Order] = new TableInfo(Order, [new ColumnInfo("STAT_CD", 2, "VARCHAR2") { Length = 2, Comment = "상태코드" }, new ColumnInfo("ORDER_ID", 1, "NUMBER") { Precision = 10, IsNullable = false }])
        {
            Constraints = [new ConstraintInfo("PK_ORDER", ConstraintKind.PrimaryKey, ["ORDER_ID"])],
            Indexes = [new IndexInfo("IX_ORDER_01", false, ["CUST_ID", "ORDER_DT"]) { IndexType = "NORMAL" }],
        };
        var plans = new FakePlans();
        var tuning = new FakeTuning();
        return (new TuningToolbox(tuning, meta, new FakeKeys(keysDenied), plans, new LanguageService()), plans, tuning);
    }

    [Fact]
    public async Task Describe_orders_columns_and_renders_types()
    {
        var (box, _, _) = Make();

        var d = await box.DescribeObjectAsync(new FakeSession(), Order, CancellationToken.None);

        Assert.Equal(["ORDER_ID", "STAT_CD"], d.Columns.Select(c => c.Name));
        Assert.Equal("NUMBER(10)", d.Columns[0].Type);
        Assert.Equal("VARCHAR2(2)", d.Columns[1].Type);
        Assert.Equal("PRIMARY KEY", Assert.Single(d.Constraints).Kind);
    }

    [Fact]
    public async Task Index_info_merges_stats_and_detail_columns_with_order_and_direction()
    {
        var (box, _, _) = Make();

        var ix = await box.GetIndexInfoAsync(new FakeSession(), Order, CancellationToken.None);

        var main = ix.Single(i => i.Name == "IX_ORDER_01");
        Assert.Equal(["CUST_ID", "ORDER_DT"], main.Columns.Select(c => c.Column));
        Assert.True(main.Columns[1].Descending);
        Assert.Equal(15_022_110, main.ClusteringFactor);
        Assert.Equal("NORMAL", main.Kind);
        Assert.Equal("2026-10-27", main.LastAnalyzed);
        Assert.Null(main.Visibility);
        var statsOnly = ix.Single(i => i.Name == "IX_STATS_ONLY");
        Assert.True(statsOnly.Unique);
        Assert.True(statsOnly.Stale);
    }

    [Fact]
    public async Task Index_info_falls_back_to_basic_columns_when_detail_is_denied()
    {
        var (box, _, _) = Make(keysDenied: true);

        var ix = await box.GetIndexInfoAsync(new FakeSession(), Order, CancellationToken.None);

        Assert.Equal(["CUST_ID", "ORDER_DT"], ix.Single(i => i.Name == "IX_ORDER_01").Columns.Select(c => c.Column));
    }

    [Fact]
    public async Task Table_stats_filter_columns_case_insensitively()
    {
        var (box, _, _) = Make();

        var all = await box.GetTableStatsAsync(new FakeSession(), Order, null, CancellationToken.None);
        var one = await box.GetTableStatsAsync(new FakeSession(), Order, ["stat_cd"], CancellationToken.None);

        Assert.Equal(2, all.Columns.Count);
        Assert.Equal("STAT_CD", Assert.Single(one.Columns).Name);
        Assert.Equal(1_823_000, one.NumRows);
    }

    [Fact]
    public async Task Histogram_response_has_counts_only_and_never_the_endpoint_values()
    {
        var (box, _, _) = Make();

        var h = await box.GetHistogramAsync(new FakeSession(), Order, "stat_cd", 2, false, CancellationToken.None);

        Assert.Equal("FREQUENCY", h.Kind);
        Assert.Equal(135, h.TotalCount);
        Assert.Equal(3, h.BucketCount);
        Assert.Equal(1, h.OmittedBuckets);
        Assert.Equal([100L, 30L], h.Buckets.Select(b => b.Count));
        Assert.DoesNotContain("SECRET", ToolJson.Serialize(h), StringComparison.Ordinal);

        var withValues = await box.GetHistogramAsync(new FakeSession(), Order, "stat_cd", 0, true, CancellationToken.None);
        Assert.Equal(["SECRET-A", "SECRET-B", "SECRET-C"], withValues.Buckets.Select(b => b.Endpoint));
        Assert.Contains("\"endpoint\":\"SECRET-A\"", ToolJson.Serialize(withValues), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explain_is_refused_with_guidance_unless_connection_allows_it()
    {
        var (box, plans, _) = Make();
        const string sql = "SELECT 1 FROM dual WHERE x = :1";

        var ex = await Assert.ThrowsAsync<AiToolException>(() => box.ExplainPlanAsync(new FakeSession(), sql, false, CancellationToken.None));
        Assert.Contains("get_cursor_plan", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, plans.Explains);

        var plan = await box.ExplainPlanAsync(new FakeSession(), sql, true, CancellationToken.None);
        Assert.Equal(1, plans.Explains);
        Assert.Equal("TABLE ACCESS FULL", plan.Steps[1].Operation);
        Assert.Equal("APP.TB_ORDER", plan.Steps[1].ObjectName);
        Assert.Equal("FullScan", plan.Steps[1].Risk);
    }

    [Theory]
    [InlineData("DELETE FROM t")]
    [InlineData("SELECT 1 FROM dual; SELECT 2 FROM dual")]
    [InlineData("SELECT * FROM t FOR UPDATE")]
    [InlineData("SELECT * FROM t WHERE a = &v")]
    [InlineData("BEGIN NULL; END;")]
    [InlineData("  ")]
    public async Task Explain_accepts_only_one_plain_select_and_never_reaches_the_service_otherwise(string sql)
    {
        var (box, plans, _) = Make();

        await Assert.ThrowsAsync<AiToolException>(() => box.ExplainPlanAsync(new FakeSession(), sql, true, CancellationToken.None));

        Assert.Equal(0, plans.Explains);
    }

    [Fact]
    public async Task Explain_turns_read_only_server_refusal_into_cursor_plan_guidance()
    {
        var box = new TuningToolbox(new FakeTuning(), new FakeMetadataProvider(), new FakeKeys(), new RefusingPlans(), new LanguageService());

        var ex = await Assert.ThrowsAsync<AiToolException>(() =>
            box.ExplainPlanAsync(new FakeSession(), "SELECT 1 FROM dual", true, CancellationToken.None));

        Assert.Contains("ORA-01456", ex.Message, StringComparison.Ordinal);
        Assert.Contains("get_cursor_plan", ex.Message, StringComparison.Ordinal);
    }

    private sealed class RefusingPlans : IExecutionPlanService
    {
        public Task<ExecutionPlan> ExplainAsync(ISession s, SqlStatement stmt, CancellationToken ct) =>
            throw new InvalidOperationException("ORA-01456: 읽기 전용 접속은 서버가 EXPLAIN PLAN 의 PLAN_TABLE 기록을 막습니다.");

        public Task<ExecutionPlan> GetCursorPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Cursor_plan_carries_actual_rows_and_cursor()
    {
        var (box, _, _) = Make();

        var plan = await box.GetCursorPlanAsync(new FakeSession(), new CursorRef("7h35uxf5uhmm1", 0), null, CancellationToken.None);

        Assert.Equal("7h35uxf5uhmm1", plan.SqlId);
        Assert.Equal(2_103_442, plan.Steps[1].ARows);
        Assert.Equal(2481234567, plan.PlanHash);
    }

    [Fact]
    public async Task Sql_stats_are_per_execution_and_missing_cursor_is_reported()
    {
        var (box, _, _) = Make();

        var st = await box.GetSqlStatsAsync(new FakeSession(), "abc", null, CancellationToken.None);
        var gone = await box.GetSqlStatsAsync(new FakeSession(), "gone", null, CancellationToken.None);

        Assert.True(st.Found);
        Assert.Equal(2000, st.ElapsedMsPerExec);
        Assert.Equal(1000, st.BufferGetsPerExec);
        Assert.False(gone.Found);
    }

    [Fact]
    public async Task Child_cursors_include_mismatch_reasons()
    {
        var (box, _, _) = Make();

        var kids = await box.GetChildCursorsAsync(new FakeSession(), "abc", CancellationToken.None);

        Assert.Empty(kids[0].MismatchReasons);
        Assert.Equal(["BIND_MISMATCH"], kids[1].MismatchReasons);
    }

    [Fact]
    public async Task Benchmark_is_refused_on_production_and_clamps_repeat()
    {
        var (box, _, tuning) = Make();
        BenchmarkVariant[] one = [new("원문", "SELECT 1 FROM dual")];

        await Assert.ThrowsAsync<AiToolException>(() => box.BenchmarkAsync(new FakeSession(), one, 3, false, null, CancellationToken.None));
        Assert.Empty(tuning.Calls);

        var r = await box.BenchmarkAsync(new FakeSession(), one, 99, true, null, CancellationToken.None);
        Assert.Equal("bench:원문:5", Assert.Single(tuning.Calls));
        Assert.Equal(12.5, Assert.Single(r).ElapsedMs);
    }

    [Fact]
    public async Task Tuning_context_collects_used_columns_index_columns_and_plan_findings()
    {
        var (box, _, _) = Make();
        var req = new TuningContextRequest("SELECT stat_cd FROM app.tb_order WHERE stat_cd = :1", "7h35", 0, false, "APP");

        var ctx = await box.GetTuningContextAsync(new FakeSession(), req, CancellationToken.None);

        var obj = Assert.Single(ctx.Objects);
        Assert.Equal("APP.TB_ORDER", obj.Name);
        Assert.Equal(1_823_000, obj.Stats?.NumRows);
        Assert.Equal(["STAT_CD"], obj.UsedColumns.Select(c => c.Name)); // ORDER_ID 는 쓰지 않음
        Assert.Equal("FREQUENCY", obj.UsedColumns[0].Histogram);
        Assert.Equal(["CUST_ID", "ORDER_DT DESC"], obj.Indexes.Single(i => i.Name == "IX_ORDER_01").Columns);
        var finding = Assert.Single(ctx.PlanFindings);
        Assert.Equal("TABLE ACCESS FULL", finding.Operation);
        Assert.Equal("E-Rows 12 / A-Rows 2,103,442", finding.Note);
        Assert.Contains(ctx.Omitted, o => o.Contains("쓰지 않는 열 1개", StringComparison.Ordinal));
        Assert.Contains(ctx.Omitted, o => o.Contains("히스토그램 끝값", StringComparison.Ordinal));
        Assert.Equal(2481234567, ctx.Statement.PlanHash);
    }

    [Fact]
    public async Task Tuning_context_shrinks_in_order_until_it_fits_the_budget()
    {
        var (box, _, _) = Make();
        var sql = "SELECT stat_cd FROM app.tb_order WHERE stat_cd = :1";
        var full = await box.GetTuningContextAsync(new FakeSession(), new TuningContextRequest(sql, "7h35", 0, false, "APP"), CancellationToken.None);
        var fullTokens = ToolJson.EstimateTokens(full);

        var tight = await box.GetTuningContextAsync(new FakeSession(), new TuningContextRequest(sql, "7h35", fullTokens - 20, false, "APP"), CancellationToken.None);
        var tiny = await box.GetTuningContextAsync(new FakeSession(), new TuningContextRequest(sql, "7h35", 1, false, "APP"), CancellationToken.None);

        Assert.Empty(tight.PlanFindings);
        Assert.Contains(tight.Omitted, o => o.StartsWith("계획 지적 줄", StringComparison.Ordinal));
        Assert.True(ToolJson.EstimateTokens(tight) <= fullTokens - 20);
        Assert.Contains(tiny.Omitted, o => o.StartsWith("인덱스 통계", StringComparison.Ordinal));
        Assert.Contains(tiny.Omitted, o => o.StartsWith("열 통계", StringComparison.Ordinal));
        Assert.All(tiny.Objects[0].Indexes, i => Assert.Null(i.ClusteringFactor));
        Assert.All(tiny.Objects[0].UsedColumns, c => Assert.Null(c.NumDistinct));
        Assert.NotEmpty(tiny.Objects[0].UsedColumns); // 이름·형은 남는다
    }

    [Fact]
    public async Task Tuning_context_without_sql_or_sql_id_is_refused_and_unresolvable_object_keeps_a_reason()
    {
        var (box, _, _) = Make();

        await Assert.ThrowsAsync<AiToolException>(() =>
            box.GetTuningContextAsync(new FakeSession(), new TuningContextRequest(null, null, 100, false, "APP"), CancellationToken.None));

        var ctx = await box.GetTuningContextAsync(
            new FakeSession(), new TuningContextRequest("SELECT 1 FROM remote_t@lnk", null, 0, false, "APP"), CancellationToken.None);
        Assert.Contains("DB 링크", Assert.Single(ctx.Objects).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_table_referenced_twice_merges_used_columns_and_without_explain_the_context_says_why()
    {
        var (box, _, tuning) = Make();
        tuning.PoolSchema = null; // 공유 풀에 없는 문장: sql_id 가 없으면 EXPLAIN 도 못 쓰는 접속에서는 계획을 못 낸다
        var sql = "SELECT a.stat_cd FROM tb_order a, app.tb_order b WHERE b.order_id = 1";

        var ctx = await box.GetTuningContextAsync(new FakeSession(), new TuningContextRequest(sql, null, 0, false, "APP"), CancellationToken.None);

        var obj = Assert.Single(ctx.Objects);
        Assert.Equal(["ORDER_ID", "STAT_CD"], obj.UsedColumns.Select(c => c.Name).Order());
        Assert.Contains(ctx.Omitted, o => o.Contains("get_cursor_plan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sql_stats_of_a_cursor_that_has_not_finished_once_have_no_per_execution_values()
    {
        var (box, _, _) = Make();
        var tuning = new ZeroExecTuning();
        var zero = new TuningToolbox(tuning, new FakeMetadataProvider(), new FakeKeys(), new FakePlans(), new LanguageService());

        var st = await zero.GetSqlStatsAsync(new FakeSession(), "run", null, CancellationToken.None);

        Assert.True(st.Found);
        Assert.Null(st.ElapsedMsPerExec);
        _ = box;
    }

    private sealed class ZeroExecTuning : ITuningService
    {
        public Task<TuningCandidateSet> GetTuningCandidatesAsync(ISession s, TuningCandidateQuery query, CancellationToken ct) => Pool(query, "APP");

        public Task<SqlStatsRow?> GetSqlStatsAsync(ISession s, string sqlId, CancellationToken ct) =>
            Task.FromResult<SqlStatsRow?>(new SqlStatsRow(1, 1, 0, 600_000_000, 0, 5_000_000, 0, 0));
    }

    [Fact]
    public async Task Benchmark_checks_every_variant_before_running_any_and_hides_server_error_text()
    {
        var (box, _, tuning) = Make();

        var ex = await Assert.ThrowsAsync<AiToolException>(() => box.BenchmarkAsync(
            new FakeSession(), [new("a", "SELECT 1 FROM dual"), new("b", "SELECT * FROM t WHERE x = :1")], 1, true, null, CancellationToken.None));
        Assert.Contains("'b'", ex.Message, StringComparison.Ordinal);
        Assert.Empty(tuning.Calls);

        var failing = new TuningToolbox(new FailingBenchmarkTuning(), new FakeMetadataProvider(), new FakeKeys(), new FakePlans(), new LanguageService());
        var err = await Assert.ThrowsAsync<AiToolException>(() =>
            failing.BenchmarkAsync(new FakeSession(), [new("c", "SELECT 1 FROM dual")], 1, true, null, CancellationToken.None));
        Assert.Contains("ORA-01722", err.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", err.Message, StringComparison.Ordinal);
    }

    private sealed class FailingBenchmarkTuning : ITuningService
    {
        public Task<TuningCandidateSet> GetTuningCandidatesAsync(ISession s, TuningCandidateQuery query, CancellationToken ct) => Pool(query, "APP");

        public Task<BenchmarkResult> RunBenchmarkAsync(ISession s, BenchmarkCase benchmark, int iterations, CancellationToken ct) =>
            throw new InvalidOperationException("ORA-01722: 수치가 부적합합니다 'SECRET-VALUE'");
    }

    [Fact]
    public async Task Plan_findings_compare_actual_rows_with_estimate_times_starts_and_ignore_zero_rows()
    {
        var plans = new SteppedPlans(
            new PlanStep(1, 0, 1, "TABLE ACCESS") { Options = "BY INDEX ROWID", ObjectName = "TB_ORDER", EstimatedRows = 1, ActualRows = 1000, Starts = 1000 },
            new PlanStep(2, 1, 2, "INDEX") { Options = "RANGE SCAN", ObjectName = "IX_A", EstimatedRows = 1, ActualRows = 0, Starts = 1 },
            new PlanStep(3, 1, 2, "TABLE ACCESS") { Options = "BY INDEX ROWID", ObjectName = "TB_ORDER", EstimatedRows = 10, ActualRows = 5000, Starts = 1 });
        var meta = new FakeMetadataProvider { Tables = { [Order] = new TableInfo(Order, []) } };
        var box = new TuningToolbox(new FakeTuning(), meta, new FakeKeys(), plans, new LanguageService());

        var ctx = await box.GetTuningContextAsync(new FakeSession(), new TuningContextRequest(null, "abc", 0, false, "APP"), CancellationToken.None);

        Assert.Equal([3], ctx.PlanFindings.Select(f => f.Id));
    }

    private sealed class SteppedPlans(params PlanStep[] steps) : IExecutionPlanService
    {
        public Task<ExecutionPlan> ExplainAsync(ISession s, SqlStatement stmt, CancellationToken ct) => throw new NotSupportedException();

        public Task<ExecutionPlan> GetCursorPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct) =>
            Task.FromResult(new ExecutionPlan(steps, string.Empty));
    }

    [Fact]
    public async Task Cache_restarts_when_the_starter_was_cancelled_and_honors_each_callers_own_token()
    {
        var cache = new ToolRunCache();
        using var startedCts = new CancellationTokenSource();
        var gate = new TaskCompletionSource();
        var first = cache.GetOrAddAsync("k", async () =>
        {
            await gate.Task.WaitAsync(startedCts.Token);
            return 1;
        }, startedCts.Token);

        var second = cache.GetOrAddAsync("k", () => Task.FromResult(2));
        await startedCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(2, await second); // 시작한 호출자의 취소가 다른 호출자에게 번지지 않는다

        using var already = new CancellationTokenSource();
        await already.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrAddAsync("k2", () => Task.FromResult(3), already.Token));
    }

    [Fact]
    public async Task Repeated_lookups_in_one_run_hit_the_cache_not_the_database()
    {
        var cache = new ToolRunCache();
        var box = new TuningToolbox(new FakeTuning(), new FakeMetadataProvider { Tables = { [Order] = new TableInfo(Order, []) } }, new FakeKeys(), new FakePlans(), new LanguageService(), cache);

        await box.GetTableStatsAsync(new FakeSession(), Order, null, CancellationToken.None);
        await box.GetTableStatsAsync(new FakeSession(), Order, null, CancellationToken.None);
        await box.GetIndexInfoAsync(new FakeSession(), Order, CancellationToken.None);
        await box.GetIndexInfoAsync(new FakeSession(), Order, CancellationToken.None);

        Assert.Equal(2, cache.Hits);
    }

    [Fact]
    public async Task Benchmark_rejects_empty_too_many_and_non_select_variants()
    {
        var (box, _, _) = Make();

        await Assert.ThrowsAsync<AiToolException>(() => box.BenchmarkAsync(new FakeSession(), [], 1, true, null, CancellationToken.None));
        var six = Enumerable.Range(0, 6).Select(i => new BenchmarkVariant("v" + i, "SELECT 1 FROM dual")).ToList();
        await Assert.ThrowsAsync<AiToolException>(() => box.BenchmarkAsync(new FakeSession(), six, 1, true, null, CancellationToken.None));
        var ex = await Assert.ThrowsAsync<AiToolException>(() =>
            box.BenchmarkAsync(new FakeSession(), [new("삭제", "DELETE FROM t")], 1, true, null, CancellationToken.None));
        Assert.Contains("삭제", ex.Message, StringComparison.Ordinal);
    }
}
