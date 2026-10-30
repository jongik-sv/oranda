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
using Oranda.Language.Ai.Literals;
using Oranda.Testing;

namespace Oranda.Language.Tests.Ai.Handlers;

// benchmark_queries 의 바인드(사용자 결정 2026-10-30): 시작 탭의 바인드 값은 AI 로 보내지 않고 변형을 로컬에서 실행할 때만 바인드한다.
public class BenchmarkBindsTests
{
    private sealed class RecordingTuning : ITuningService
    {
        public List<BenchmarkCase> Ran { get; } = [];

        public Task<TuningCandidateSet> GetTuningCandidatesAsync(ISession s, TuningCandidateQuery query, CancellationToken ct) =>
            Task.FromResult(new TuningCandidateSet([], 0));

        public Task<BenchmarkResult> RunBenchmarkAsync(ISession s, BenchmarkCase benchmark, int iterations, CancellationToken ct)
        {
            Ran.Add(benchmark);
            return Task.FromResult(new BenchmarkResult(benchmark, iterations, TimeSpan.FromMilliseconds(5), 10, 0, 1, 99));
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

    private sealed class FixedSource(FakeSession db) : IAgentSessionSource
    {
        public AgentSession? Find(string runId) => null;

        public ValueTask<ISession> AcquireDbSessionAsync(AgentSession session, CancellationToken ct) => ValueTask.FromResult<ISession>(db);
    }

    private sealed class NoAudit : IAgentAuditStore
    {
        public ValueTask AppendAsync(AgentAuditEntry entry, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<AgentAuditEntry>> ListRecentAsync(int limit, Guid? profileId, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<AgentAuditEntry>>([]);
    }

    private static readonly Dictionary<string, BindValue> WordDh = new() { ["WORD_DH"] = new BindValue(BindDataType.Varchar2, "20261030") };

    private static (BenchmarkQueriesHandler Handler, RecordingTuning Tuning) Handler()
    {
        var tuning = new RecordingTuning();
        var meta = new FakeMetadataProvider();
        var boxes = new AgentToolboxProvider(tuning, meta, new NoKeys(), new NoPlans(), new LanguageService());
        return (new BenchmarkQueriesHandler(boxes, new AgentStatementReferenceExtractor(new LanguageService(), meta)), tuning);
    }

    private static AgentToolInvocation Invoke(IReadOnlyDictionary<string, BindValue> binds, params string[] sqls)
    {
        var profile = TestData.Profile();
        var run = new AgentRunScope("run-b", profile, new AiContext("test", profile.Environment, AiAccessLevel.Query)) { Binds = binds };
        var variants = new JsonArray([.. sqls.Select((sql, i) => (JsonNode)new JsonObject { ["label"] = "v" + i, ["sql"] = sql })]);
        return new AgentToolInvocation(new AgentSession(run), new FakeSession(profile), AiAccessLevel.Query, new JsonObject { ["variants"] = variants },
            new Dictionary<string, AgentObjectRef>(), MayExecuteSql: true);
    }

    [Fact]
    public async Task Variants_run_with_the_start_tab_binds_they_use_and_case_does_not_matter()
    {
        var (handler, tuning) = Handler();

        var r = await handler.ExecuteAsync(Invoke(WordDh, "SELECT 1 FROM dual WHERE :word_dh IS NOT NULL", "SELECT 2 FROM dual"), CancellationToken.None);

        Assert.False(r.IsError, r.ErrorMessage);
        Assert.Equal(2, tuning.Ran.Count);
        Assert.Equal("20261030", tuning.Ran[0].Binds["word_dh"].Value);
        Assert.Empty(tuning.Ran[1].Binds); // 문장이 쓰지 않는 이름은 넘기지 않는다
    }

    [Fact]
    public async Task A_variant_with_a_bind_that_has_no_value_fails_naming_it_before_anything_runs()
    {
        var (handler, tuning) = Handler();

        var r = await handler.ExecuteAsync(Invoke(WordDh, "SELECT 2 FROM dual", "SELECT 1 FROM dual WHERE :word_dh = :plant_cd"), CancellationToken.None);

        Assert.True(r.IsError);
        Assert.True(r.AppWritten); // 앱이 쓴 글이라 게이트가 Query 수준에서도 모델에 보인다
        Assert.Contains(":plant_cd", r.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("바인드 변수 창", r.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("20261030", r.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(tuning.Ran);
    }

    [Fact]
    public async Task A_secret_looking_bind_name_is_never_bound_even_when_a_value_is_given()
    {
        var (handler, tuning) = Handler();
        var binds = new Dictionary<string, BindValue> { ["USER_PWD"] = new BindValue(BindDataType.Varchar2, "hunter2") };

        var r = await handler.ExecuteAsync(Invoke(binds, "SELECT 1 FROM dual WHERE :user_pwd IS NOT NULL"), CancellationToken.None);

        Assert.True(r.IsError);
        Assert.Contains(":user_pwd", r.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", r.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(tuning.Ran);
    }

    [Fact]
    public async Task Failed_execution_keeps_only_the_error_number_for_the_model_and_the_raw_text_for_the_log()
    {
        var meta = new FakeMetadataProvider();
        var boxes = new AgentToolboxProvider(new FailingTuning(), meta, new NoKeys(), new NoPlans(), new LanguageService());
        var handler = new BenchmarkQueriesHandler(boxes, new AgentStatementReferenceExtractor(new LanguageService(), meta));

        var r = await handler.ExecuteAsync(Invoke(WordDh, "SELECT 1 FROM dual WHERE :word_dh IS NOT NULL"), CancellationToken.None);

        Assert.True(r.IsError);
        Assert.True(r.AppWritten);
        Assert.Contains("ORA-01722", r.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", r.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("SECRET", r.LogDetail, StringComparison.Ordinal); // 원문은 게이트가 가려 로그에만 쓴다
    }

    private sealed class FailingTuning : ITuningService
    {
        public Task<TuningCandidateSet> GetTuningCandidatesAsync(ISession s, TuningCandidateQuery query, CancellationToken ct) =>
            Task.FromResult(new TuningCandidateSet([], 0));

        public Task<BenchmarkResult> RunBenchmarkAsync(ISession s, BenchmarkCase benchmark, int iterations, CancellationToken ct) =>
            throw new InvalidOperationException("ORA-01722: invalid number 'SECRET'");
    }

    [Fact]
    public async Task Through_the_gate_literal_placeholders_are_restored_before_binds_are_judged_so_only_real_binds_are_bound()
    {
        // 리터럴을 가린 대화: AI 는 :v1(리터럴 자리표시)과 :WORD_DH(진짜 바인드)를 함께 보낸다. :v1 을 바인드로 오인하면 모든 변형이 실패한다.
        const string statement = "SELECT 1 FROM dual WHERE dummy = 'X' AND :WORD_DH IS NOT NULL";
        var masker = new SqlLiteralMasker();
        var vault = masker.CreateVault(statement);
        var masked = masker.Mask(statement, vault);
        Assert.True(masked.Succeeded);
        Assert.Contains(":v1", masked.Text, StringComparison.Ordinal);

        var profile = new ConnectionProfile("dev", "scott", "h/s", AiAccess: AiAccessLevel.Query);
        var context = new AiContext(profile.Name, profile.Environment, AiAccessLevel.Query, StatementText: statement);
        var session = new AgentSession(new AgentRunScope("run-g", profile, context) { Literals = vault, Binds = WordDh });
        var (handler, tuning) = Handler();
        var meta = new FakeMetadataProvider();
        var gate = new AgentGate(
            new FixedSource(new FakeSession(profile)), new AgentStatementReferenceExtractor(new LanguageService(), meta), [handler], new NoAudit(), masker: masker);
        var args = new JsonObject
        {
            ["variants"] = new JsonArray(new JsonObject { ["label"] = "masked", ["sql"] = masked.Text }, new JsonObject { ["label"] = "original" }),
        };

        var response = await gate.CallToolAsync(session, AgentToolNames.BenchmarkQueries, args, CancellationToken.None);

        Assert.False(response.Result.IsError, response.Result.Text);
        Assert.Equal(2, tuning.Ran.Count);
        Assert.All(tuning.Ran, c => Assert.Contains("'X'", c.Sql, StringComparison.Ordinal));
        Assert.All(tuning.Ran, c => Assert.Equal(["WORD_DH"], c.Binds.Keys));
        Assert.DoesNotContain("20261030", response.Result.Text, StringComparison.Ordinal);
    }
}
