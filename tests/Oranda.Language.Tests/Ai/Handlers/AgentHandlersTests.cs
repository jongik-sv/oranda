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

public class AgentHandlersTests
{
    private static readonly DbObjectRef Order = new("APP", "TB_ORDER", "TABLE");

    private sealed class Tuning : ITuningService
    {
        public Task<TuningCandidateSet> GetTuningCandidatesAsync(ISession s, TuningCandidateQuery query, CancellationToken ct) =>
            Task.FromResult(new TuningCandidateSet([.. (query.SqlIds ?? []).Select(id => new SqlStatsSample(id) { ParsingSchema = "SCOTT" })], 0));

        public Task<IReadOnlyList<HistogramBucket>> GetHistogramAsync(ISession s, string owner, string table, string column, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<HistogramBucket>>([new HistogramBucket(10, "SECRET", 10)]);

        public Task<IReadOnlyList<ColumnStatsRow>> GetColumnStatsAsync(ISession s, string owner, string table, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ColumnStatsRow>>(
                [new ColumnStatsRow("STAT_CD", 6, 0, 0.1, "FREQUENCY", 6, null), new ColumnStatsRow("ORDER_DT", 9, 0, 0.1, "NONE", 1, null)]);

        public Task<TableStatsRow?> GetTableStatsAsync(ISession s, string owner, string table, CancellationToken ct) =>
            Task.FromResult<TableStatsRow?>(new TableStatsRow(owner, table, 100, 10, 50, null, false));

        public Task<BenchmarkResult> RunBenchmarkAsync(ISession s, BenchmarkCase benchmark, int iterations, CancellationToken ct) =>
            throw new InvalidOperationException("ORA-01722: 'SECRET-VALUE'");
    }

    private sealed class Metadata : IMetadataProvider
    {
        private readonly FakeMetadataProvider _inner = new();

        public Dictionary<DbObjectRef, TableInfo> Tables => _inner.Tables;

        public Dictionary<(string, string), NameStep> Steps { get; } = [];

        public Dictionary<DbObjectRef, string> Ddl { get; } = [];

        public Task<NameStep?> GetNameStepAsync(ISession s, string owner, string name, CancellationToken ct) =>
            Task.FromResult(Steps.TryGetValue((owner, name), out var st) ? st : null);

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) =>
            Task.FromResult(Ddl.TryGetValue(obj, out var d) ? d : string.Empty);

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => _inner.ListSchemasAsync(s, ct);

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => _inner.ListObjectsAsync(s, q, ct);

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) => _inner.GetTableAsync(s, table, ct);

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => _inner.GetProceduresAsync(s, obj, ct);

        public Task<ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => _inner.GetObjectDetailAsync(s, obj, ct);

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => _inner.GetSourceAsync(s, obj, ct);

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => _inner.GetCompileErrorsAsync(s, obj, ct);
    }

    private sealed class NoKeys : ITableKeyDetailProvider
    {
        public Task<SchemaFolderResult<IndexColumnDetail>> GetIndexColumnsAsync(ISession s, DbObjectRef table, string indexName, CancellationToken ct) =>
            Task.FromResult(new SchemaFolderResult<IndexColumnDetail>([]));

        public Task<SchemaFolderResult<ConstraintColumnDetail>> GetConstraintColumnsAsync(ISession s, DbObjectRef table, string constraintName, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class NoPlans : IExecutionPlanService
    {
        public Task<ExecutionPlan> ExplainAsync(ISession s, SqlStatement stmt, CancellationToken ct) => throw new NotSupportedException();

        public Task<ExecutionPlan> GetCursorPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct) => throw new NotSupportedException();
    }

    private static AgentToolboxProvider Boxes(Metadata meta) =>
        new(new Tuning(), meta, new NoKeys(), new NoPlans(), new LanguageService());

    private static AgentToolInvocation Invoke(
        JsonObject args, AiAccessLevel level, ConnectionEnvironment env = ConnectionEnvironment.Development, string? statement = null,
        params (string Arg, AgentObjectRef Obj)[] resolved)
    {
        var profile = TestData.Profile(env);
        var run = new AgentRunScope("run1", profile, new AiContext("test", env, level, StatementText: statement));
        // 게이트가 채우는 허용 값을 같은 규칙으로 채운다(문장은 검사를 마친 것으로 본다).
        return new AgentToolInvocation(
            new AgentSession(run), new FakeSession(profile), level, args, resolved.ToDictionary(r => r.Arg, r => r.Obj),
            ExplainPlanAllowed: level >= AiAccessLevel.Plan && Oranda.Core.Ai.Access.AiAccessPolicy.ExplainPlanEnabled(profile),
            MayExecuteSql: level >= AiAccessLevel.Query);
    }

    private static readonly (string, AgentObjectRef) TableArg = ("table", new AgentObjectRef("APP", "TB_ORDER", "TABLE"));

    [Fact]
    public async Task Histogram_handler_carries_endpoints_only_at_row_values_level()
    {
        var h = new GetHistogramHandler(Boxes(new Metadata()));
        var args = new JsonObject { ["table"] = "TB_ORDER", ["column"] = "STAT_CD" };

        var plan = await h.ExecuteAsync(Invoke(args, AiAccessLevel.Plan, resolved: TableArg), CancellationToken.None);
        var rows = await h.ExecuteAsync(Invoke(args, AiAccessLevel.QueryRows, resolved: TableArg), CancellationToken.None);

        Assert.DoesNotContain("SECRET", plan.Result!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("\"endpoint\":\"SECRET\"", rows.Result!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Table_stats_handler_limits_columns_to_the_ones_the_statement_uses_when_omitted()
    {
        var h = new GetTableStatsHandler(Boxes(new Metadata()));
        var args = new JsonObject { ["table"] = "TB_ORDER" };

        var r = await h.ExecuteAsync(
            Invoke(args, AiAccessLevel.Plan, statement: "SELECT stat_cd FROM app.tb_order", resolved: TableArg), CancellationToken.None);

        var cols = r.Result!["columns"]!.AsArray();
        Assert.Equal("STAT_CD", Assert.Single(cols)!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Handler_errors_become_error_outputs_without_server_text_or_missing_argument_details()
    {
        var bench = new BenchmarkQueriesHandler(Boxes(new Metadata()), new AgentStatementReferenceExtractor(new LanguageService(), new Metadata()));
        var variants = new JsonObject { ["variants"] = new JsonArray(new JsonObject { ["label"] = "a", ["sql"] = "SELECT 1 FROM dual" }) };

        var failed = await bench.ExecuteAsync(Invoke(variants, AiAccessLevel.Query), CancellationToken.None);
        var prod = await bench.ExecuteAsync(Invoke(variants, AiAccessLevel.Query, ConnectionEnvironment.Production), CancellationToken.None);
        var missing = await new GetCursorPlanHandler(Boxes(new Metadata())).ExecuteAsync(Invoke([], AiAccessLevel.Plan), CancellationToken.None);

        Assert.Contains("ORA-01722", failed.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", failed.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("ORA-01722", prod.ErrorMessage, StringComparison.Ordinal); // 운영도 환경 때문에 거부되지 않고 실제로 실행된다
        Assert.Contains("sql_id", missing.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Toolbox_is_shared_within_one_agent_session_and_separate_between_sessions()
    {
        var boxes = Boxes(new Metadata());
        var a = Invoke([], AiAccessLevel.Plan).Session;
        var b = Invoke([], AiAccessLevel.Plan).Session;

        Assert.Same(boxes.For(a), boxes.For(a));
        Assert.NotSame(boxes.For(a), boxes.For(b));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Extractor_analyzes_statement_with_synonym_remote_locking_and_user_function_flags()
    {
        var meta = new Metadata();
        meta.Steps[("SCOTT", "SYN_ORDER")] = NameStep.Synonym("APP", "TB_ORDER");
        meta.Steps[("APP", "TB_ORDER")] = NameStep.TableOrView;
        var x = new AgentStatementReferenceExtractor(new LanguageService(), meta);

        var plain = await x.AnalyzeAsync(new FakeSession(), "SELECT NVL(a, 0) FROM syn_order", null, CancellationToken.None);
        var risky = await x.AnalyzeAsync(new FakeSession(), "SELECT pkg.f(a) FROM tb_r@lnk FOR UPDATE", null, CancellationToken.None);
        var multi = await x.AnalyzeAsync(new FakeSession(), "SELECT 1 FROM dual; DELETE FROM t", null, CancellationToken.None);

        Assert.Equal("APP.TB_ORDER", Assert.Single(plain.Objects).Key);
        Assert.Equal(StatementKind.Query, plain.Kind);
        Assert.False(plain.CallsUserFunction);
        Assert.True(risky.HasRemoteReference);
        Assert.True(risky.HasLockingClause);
        Assert.True(risky.CallsUserFunction);
        Assert.Equal(2, multi.StatementCount);
    }

    [Theory]
    [InlineData("SELECT * FROM (tb_secret a JOIN tb_other b ON a.id = b.id)")]
    [InlineData("SELECT * FROM TABLE(CAST(MULTISET(SELECT id FROM tb_secret) AS t_ids))")]
    [InlineData("SELECT * FROM record")]
    [InlineData("SELECT * FROM tb_a AS OF TIMESTAMP (SELECT MAX(ts) FROM tb_secret)")]
    [InlineData("SELECT * FROM \"tb_order\"")]
    [InlineData("SELECT * FROM syn_loop")]
    public async Task Extractor_fails_closed_when_it_cannot_be_sure_it_found_every_object(string sql)
    {
        var meta = new Metadata();
        meta.Steps[("SCOTT", "SYN_LOOP")] = NameStep.Synonym("SCOTT", "SYN_LOOP");
        meta.Steps[("SCOTT", "TB_A")] = NameStep.TableOrView;
        meta.Steps[("SCOTT", "tb_order")] = NameStep.TableOrView;
        var x = new AgentStatementReferenceExtractor(new LanguageService(), meta);

        var info = await x.AnalyzeAsync(new FakeSession(), sql, null, CancellationToken.None);

        Assert.True(info.IsUncertain);
    }

    [Theory]
    [InlineData("SELECT * FROM tb_a FOR /* x */ UPDATE", true, false, false)]
    [InlineData("SELECT * FROM tb_a FOR\nUPDATE", true, false, false)]
    [InlineData("SELECT fn@lnk(1) FROM dual", false, true, false)]
    [InlineData("SELECT seq_x.NEXTVAL FROM dual", false, false, true)]
    [InlineData("SELECT * FROM TABLE(pkg.pipe_fn(1))", false, false, true)]
    public async Task Extractor_flags_locking_remote_and_function_calls_through_comments_and_remote_links(string sql, bool locking, bool remote, bool calls)
    {
        var x = new AgentStatementReferenceExtractor(new LanguageService(), new Metadata());

        var info = await x.AnalyzeAsync(new FakeSession(), sql, null, CancellationToken.None);

        Assert.Equal((locking, remote, calls), (info.HasLockingClause, info.HasRemoteReference, info.CallsUserFunction));
    }

    [Fact]
    public async Task Handlers_reject_ill_typed_or_out_of_range_arguments_and_unify_cancellation()
    {
        var boxes = Boxes(new Metadata());
        var x = new AgentStatementReferenceExtractor(new LanguageService(), new Metadata());

        var badVariant = await new BenchmarkQueriesHandler(boxes, x).ExecuteAsync(
            Invoke(new JsonObject { ["variants"] = new JsonArray(new JsonObject { ["label"] = 1, ["sql"] = "SELECT 1 FROM dual" }) }, AiAccessLevel.Query), CancellationToken.None);
        var badChild = await new GetCursorPlanHandler(boxes).ExecuteAsync(
            Invoke(new JsonObject { ["sql_id"] = "abc", ["child"] = -1 }, AiAccessLevel.Plan), CancellationToken.None);
        var badBudget = await new GetTuningContextHandler(boxes, x).ExecuteAsync(
            Invoke(new JsonObject { ["budget_tokens"] = 1, ["sql"] = "SELECT 1 FROM dual" }, AiAccessLevel.Plan), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.Contains("variants", badVariant.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("child", badChild.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("budget_tokens", badBudget.ErrorMessage, StringComparison.Ordinal);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new GetSqlStatsHandler(boxes).ExecuteAsync(Invoke(new JsonObject { ["sql_id"] = "abc" }, AiAccessLevel.Plan), cts.Token));
    }

    [Fact]
    public async Task Explain_is_refused_when_the_current_schema_and_the_login_resolve_the_name_to_different_objects()
    {
        var meta = new Metadata();
        meta.Steps[("APP", "TB_ORDER")] = NameStep.TableOrView;
        meta.Steps[("SCOTT", "TB_ORDER")] = NameStep.TableOrView;
        var x = new AgentStatementReferenceExtractor(new LanguageService(), meta);
        var profile = TestData.Profile();
        var run = new AgentRunScope("r", profile, new AiContext("test", profile.Environment, AiAccessLevel.Plan, CurrentSchema: "APP"));
        var inv = new AgentToolInvocation(new AgentSession(run), new FakeSession(profile), AiAccessLevel.Plan, new JsonObject { ["sql"] = "SELECT * FROM tb_order" }, new Dictionary<string, AgentObjectRef>());

        var r = await new ExplainPlanHandler(Boxes(meta), x).ExecuteAsync(inv, CancellationToken.None);

        Assert.True(r.IsError);
        Assert.Contains("OWNER.NAME", r.ErrorMessage, StringComparison.Ordinal);
    }

    private sealed class PoolTuning(string? poolSchema) : ITuningService
    {
        public List<string> Ran { get; } = [];

        public Task<TuningCandidateSet> GetTuningCandidatesAsync(ISession s, TuningCandidateQuery query, CancellationToken ct) =>
            Task.FromResult(new TuningCandidateSet(
                poolSchema is null ? [] : [.. (query.SqlIds ?? []).Select(id => new SqlStatsSample(id) { ParsingSchema = poolSchema })], 0));

        public Task<SqlStatsRow?> GetSqlStatsAsync(ISession s, string sqlId, CancellationToken ct) =>
            Task.FromResult<SqlStatsRow?>(new SqlStatsRow(1, 1, 2, 4_000_000, 0, 100, 0, 10));

        public Task<BenchmarkResult> RunBenchmarkAsync(ISession s, BenchmarkCase benchmark, int iterations, CancellationToken ct)
        {
            Ran.Add(benchmark.Sql);
            return Task.FromResult(new BenchmarkResult(benchmark, iterations, TimeSpan.FromMilliseconds(5), 10, 0, 1, 99));
        }

        public Task<IReadOnlyList<ColumnStatsRow>> GetColumnStatsAsync(ISession s, string owner, string table, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ColumnStatsRow>>([]);

        public Task<TableStatsRow?> GetTableStatsAsync(ISession s, string owner, string table, CancellationToken ct) =>
            Task.FromResult<TableStatsRow?>(null);

        public Task<IReadOnlyList<IndexStatsRow>> GetIndexStatsAsync(ISession s, string owner, string table, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IndexStatsRow>>([]);
    }

    private sealed class OnePlan : IExecutionPlanService
    {
        public Task<ExecutionPlan> ExplainAsync(ISession s, SqlStatement stmt, CancellationToken ct) => throw new NotSupportedException();

        public Task<ExecutionPlan> GetCursorPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct) =>
            Task.FromResult(new ExecutionPlan([new PlanStep(0, null, 0, "SELECT STATEMENT")], string.Empty) { Cursor = cursor });
    }

    private static AgentToolboxProvider PoolBoxes(string? poolSchema, Metadata meta) =>
        new(new PoolTuning(poolSchema), meta, new NoKeys(), new OnePlan(), new LanguageService());

    [Fact]
    public async Task Tuning_context_returns_the_found_sql_id_so_a_following_cursor_plan_call_is_allowed()
    {
        var meta = new Metadata();
        meta.Steps[("SCOTT", "TB_ORDER")] = NameStep.TableOrView;
        meta.Tables[new DbObjectRef("SCOTT", "TB_ORDER", "TABLE")] = new TableInfo(new DbObjectRef("SCOTT", "TB_ORDER", "TABLE"), []);
        var boxes = PoolBoxes("SCOTT", meta);
        const string sql = "SELECT * FROM tb_order";
        var x = new AgentStatementReferenceExtractor(new LanguageService(), meta);

        var ctx = await new GetTuningContextHandler(boxes, x).ExecuteAsync(
            Invoke(new JsonObject { ["sql"] = sql }, AiAccessLevel.Plan, statement: sql), CancellationToken.None);
        var id = Assert.Single(ctx.SqlIds);
        var plan = await new GetCursorPlanHandler(boxes).ExecuteAsync(Invoke(new JsonObject { ["sql_id"] = id }, AiAccessLevel.Plan), CancellationToken.None);

        Assert.Equal(SqlIdCalculator.Compute(sql), id);
        Assert.False(plan.IsError, plan.ErrorMessage);
        Assert.Equal(id, plan.Result!["sql_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Benchmark_returns_sql_ids_of_executed_statements_that_are_in_the_pool_for_this_schema()
    {
        var meta = new Metadata();
        var x = new AgentStatementReferenceExtractor(new LanguageService(), meta);
        var args = new JsonObject { ["variants"] = new JsonArray(new JsonObject { ["label"] = "a", ["sql"] = "SELECT 1 FROM dual" }) };

        var inPool = await new BenchmarkQueriesHandler(PoolBoxes("SCOTT", meta), x).ExecuteAsync(Invoke(args, AiAccessLevel.Query), CancellationToken.None);
        var otherSchema = await new BenchmarkQueriesHandler(PoolBoxes("OTHER", meta), x).ExecuteAsync(Invoke(args, AiAccessLevel.Query), CancellationToken.None);
        var notInPool = await new BenchmarkQueriesHandler(PoolBoxes(null, meta), x).ExecuteAsync(Invoke(args, AiAccessLevel.Query), CancellationToken.None);

        Assert.Equal([SqlIdCalculator.Compute("SELECT 1 FROM dual")], inPool.SqlIds);
        Assert.Empty(otherSchema.SqlIds);
        Assert.Empty(notInPool.SqlIds);
        Assert.False(otherSchema.IsError);
    }

    [Fact]
    public async Task Benchmark_substitutes_pool_cursor_stats_for_flagged_variants_without_running_them_and_keeps_the_order()
    {
        var meta = new Metadata();
        var x = new AgentStatementReferenceExtractor(new LanguageService(), meta);
        var args = new JsonObject
        {
            ["variants"] = new JsonArray(
                new JsonObject { ["label"] = "risky", ["sql"] = "SELECT pkg_log.fn(1) FROM dual WHERE 1 = :b1" },
                new JsonObject { ["label"] = "plain", ["sql"] = "SELECT 1 FROM dual" }),
        };
        var tuning = new PoolTuning("SCOTT");
        var boxes = new AgentToolboxProvider(tuning, meta, new NoKeys(), new OnePlan(), new LanguageService());
        var inv = Invoke(args, AiAccessLevel.Query) with { Substitutions = ["user_function_autonomous", null] };

        var r = await new BenchmarkQueriesHandler(boxes, x).ExecuteAsync(inv, CancellationToken.None);

        Assert.False(r.IsError, r.ErrorMessage);
        Assert.Equal(["SELECT 1 FROM dual"], tuning.Ran); // 대체한 변형은 실행하지 않았다(바인드가 있어도 받는다)
        var items = r.Result!.AsArray();
        Assert.Equal("risky", items[0]!["label"]!.GetValue<string>());
        Assert.Equal("cursor_stats", items[0]!["substituted"]!.GetValue<string>());
        Assert.Equal("user_function_autonomous", items[0]!["reason"]!.GetValue<string>());
        Assert.Equal(2, items[0]!["executions"]!.GetValue<long>());
        Assert.Contains("운영 커서 실측", items[0]!["note"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("plain", items[1]!["label"]!.GetValue<string>());
        Assert.Null(items[1]!["substituted"]);
        Assert.Contains(SqlIdCalculator.Compute("SELECT pkg_log.fn(1) FROM dual WHERE 1 = :b1"), r.SqlIds); // 이어서 get_cursor_plan 을 부를 수 있다
    }

    [Fact]
    public async Task A_substituted_variant_that_cannot_be_read_is_refused_before_any_variant_runs()
    {
        var meta = new Metadata();
        var x = new AgentStatementReferenceExtractor(new LanguageService(), meta);
        var args = new JsonObject
        {
            ["variants"] = new JsonArray(
                new JsonObject { ["label"] = "plain", ["sql"] = "SELECT 1 FROM dual" },
                new JsonObject { ["label"] = "risky", ["sql"] = "SELECT 1 FROM dual; SELECT 2 FROM dual" }),
        };
        var tuning = new PoolTuning("SCOTT");
        var boxes = new AgentToolboxProvider(tuning, meta, new NoKeys(), new OnePlan(), new LanguageService());

        var r = await new BenchmarkQueriesHandler(boxes, x).ExecuteAsync(Invoke(args, AiAccessLevel.Query) with { Substitutions = [null, "db_link"] }, CancellationToken.None);

        Assert.True(r.IsError);
        Assert.Contains("'risky'", r.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(tuning.Ran); // 앞 변형도 실행하지 않았다(실행한 결과가 버려지지 않게)
    }

    [Fact]
    public async Task A_substituted_variant_without_a_pool_cursor_says_to_judge_from_the_plan()
    {
        var meta = new Metadata();
        var x = new AgentStatementReferenceExtractor(new LanguageService(), meta);
        var args = new JsonObject { ["variants"] = new JsonArray(new JsonObject { ["label"] = "a", ["sql"] = "SELECT * FROM t@remote_db" }) };
        var tuning = new PoolTuning(null);
        var boxes = new AgentToolboxProvider(tuning, meta, new NoKeys(), new OnePlan(), new LanguageService());

        var r = await new BenchmarkQueriesHandler(boxes, x).ExecuteAsync(Invoke(args, AiAccessLevel.Query) with { Substitutions = ["db_link"] }, CancellationToken.None);

        Assert.False(r.IsError, r.ErrorMessage);
        Assert.Empty(tuning.Ran);
        var item = Assert.Single(r.Result!.AsArray())!;
        Assert.Equal(0, item["executions"]!.GetValue<long>());
        Assert.Contains("실측 없음, 계획 기준으로 판단", item["note"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(r.SqlIds);
    }

    [Fact]
    public async Task Cursor_tools_refuse_or_hide_cursors_parsed_by_another_schema()
    {
        var boxes = PoolBoxes("OTHER", new Metadata());

        var plan = await new GetCursorPlanHandler(boxes).ExecuteAsync(Invoke(new JsonObject { ["sql_id"] = "abc" }, AiAccessLevel.Plan), CancellationToken.None);
        var stats = await new GetSqlStatsHandler(boxes).ExecuteAsync(Invoke(new JsonObject { ["sql_id"] = "abc" }, AiAccessLevel.Plan), CancellationToken.None);

        Assert.True(plan.IsError);
        Assert.False(stats.Result!["found"]!.GetValue<bool>());
    }

    [Fact]
    public void Sql_id_calculator_returns_thirteen_base32_characters_and_depends_on_every_character()
    {
        var a = SqlIdCalculator.Compute("select 1 from dual");

        Assert.Matches("^[0-9a-z]{13}$", a);
        Assert.DoesNotMatch("[eilo]", a);
        Assert.NotEqual(a, SqlIdCalculator.Compute("select 1 from dual "));
        Assert.Equal(a, SqlIdCalculator.Compute("select 1 from dual"));
    }

    [Fact]
    public async Task Extractor_resolve_returns_null_for_unknown_names_and_follows_synonyms()
    {
        var meta = new Metadata();
        meta.Steps[("PUBLIC", "SYN_ORDER")] = NameStep.Synonym("APP", "TB_ORDER");
        meta.Steps[("APP", "TB_ORDER")] = NameStep.TableOrView;
        var x = new AgentStatementReferenceExtractor(new LanguageService(), meta);

        var found = await x.ResolveAsync(new FakeSession(), "syn_order", null, CancellationToken.None);
        var missing = await x.ResolveAsync(new FakeSession(), "app.nothing", null, CancellationToken.None);

        Assert.Equal("APP.TB_ORDER", found?.Key);
        Assert.Null(missing);
    }

    [Fact]
    public async Task Extractor_related_includes_fk_parents_and_view_base_tables_but_not_the_input()
    {
        var meta = new Metadata();
        var dept = new DbObjectRef("APP", "TB_DEPT", "TABLE");
        meta.Tables[Order] = new TableInfo(Order, [])
        {
            Constraints = [new ConstraintInfo("FK_DEPT", ConstraintKind.ForeignKey, ["DEPT_ID"]) { ReferencedTable = dept }],
        };
        var view = new DbObjectRef("APP", "V_ORDER", "VIEW");
        meta.Tables[view] = new TableInfo(view, []);
        meta.Ddl[view] = "CREATE OR REPLACE VIEW APP.V_ORDER AS SELECT o.id FROM app.tb_base o JOIN app.tb_order x ON x.id = o.id";
        meta.Steps[("APP", "TB_BASE")] = NameStep.TableOrView;
        meta.Steps[("APP", "TB_ORDER")] = NameStep.TableOrView;
        var x = new AgentStatementReferenceExtractor(new LanguageService(), meta);

        var related = await x.GetRelatedAsync(
            new FakeSession(),
            [new AgentObjectRef("APP", "TB_ORDER"), new AgentObjectRef("APP", "V_ORDER", "VIEW")],
            CancellationToken.None);

        Assert.Equal(["APP.TB_BASE", "APP.TB_DEPT"], related.Select(r => r.Key).Order());
    }
}
