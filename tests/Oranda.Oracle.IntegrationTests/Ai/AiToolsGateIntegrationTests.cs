using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;
using Oranda.Language.Ai.Handlers;

namespace Oranda.Oracle.IntegrationTests.Ai;

// 소유: ai-tools(t6, 수락 기준 2-a). 실제 AgentGate 에 도구 처리자 9개와 문장 분석기를 연결해 실제 26ai 로 부른다:
// 계획 수준에서 끝값이 없음 · 범위 밖 표 거부 · sql_id 제한 · 수준·환경·EXPLAIN 설정 거부.

/// <summary>실제 <see cref="AgentGate"/> 를 거친 도구 호출. DB 를 바꾸지 않는 조회(벤치마크의 SELECT 포함)만 한다.</summary>
[Trait("Category", "Integration")]
public sealed class AiToolsGateIntegrationTests(AiToolsDbFixture db) : IClassFixture<AiToolsDbFixture>
{
    private AgentSession NewSession(AiAccessLevel level, ConnectionEnvironment env = ConnectionEnvironment.Development, bool? allowExplain = null)
    {
        var profile = db.Profile with { Environment = env, AiAccess = level, AllowExplainPlan = allowExplain };
        var run = new AgentRunScope("it-gate", profile, new AiContext("it-admin", env, level, StatementText: db.StatementText, SqlId: db.SqlId, CurrentSchema: db.Owner));
        return new AgentSession(run);
    }

    private AgentGate NewGate(AgentSession session, MemoryAudit audit) =>
        new(
            new FixedSource(session, db.Db),
            db.Extractor,
            [
                new DescribeObjectHandler(db.Boxes),
                new GetIndexInfoHandler(db.Boxes),
                new GetTableStatsHandler(db.Boxes),
                new GetHistogramHandler(db.Boxes),
                new GetCursorPlanHandler(db.Boxes),
                new ExplainPlanHandler(db.Boxes, db.Extractor),
                new GetSqlStatsHandler(db.Boxes),
                new GetTuningContextHandler(db.Boxes, db.Extractor),
                new BenchmarkQueriesHandler(db.Boxes, db.Extractor),
            ],
            audit);

    private async Task<AgentGateResponse> CallAsync(AgentSession session, string tool, JsonObject args, MemoryAudit? audit = null) =>
        await NewGate(session, audit ?? new MemoryAudit()).CallToolAsync(session, tool, args, default);

    private static string Code(AgentGateResponse r)
    {
        Assert.True(r.Result.IsError, r.Result.Text);
        return JsonNode.Parse(r.Result.Text)!["code"]!.GetValue<string>();
    }

    private static JsonNode Ok(AgentGateResponse r)
    {
        Assert.False(r.Result.IsError, r.Result.Text);
        return JsonNode.Parse(r.Result.Text)!;
    }

    [Fact]
    public async Task Histogram_has_no_end_values_at_plan_level_and_gets_them_at_row_values_level()
    {
        if (!db.Configured)
        {
            return;
        }

        var args = () => new JsonObject { ["table"] = db.Order, ["column"] = "STAT_CD" };
        var audit = new MemoryAudit();

        using var plan = NewSession(AiAccessLevel.Plan);
        var planBody = Ok(await CallAsync(plan, AgentToolNames.GetHistogram, args(), audit));
        using var rows = NewSession(AiAccessLevel.QueryRows);
        var rowsBody = Ok(await CallAsync(rows, AgentToolNames.GetHistogram, args()));

        Assert.Equal(20000, planBody["total_count"]!.GetValue<long>());
        Assert.DoesNotContain("endpoint", planBody.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("\"endpoint\"", rowsBody.ToJsonString(), StringComparison.Ordinal);

        var line = Assert.Single(audit.Entries);
        Assert.Equal(AiToolJudgement.Allowed, line.Verdict);
        Assert.Equal(AgentToolNames.GetHistogram, line.Tool);
    }

    [Fact]
    public async Task Objects_outside_the_statement_scope_are_refused_and_look_the_same_as_missing_ones()
    {
        if (!db.Configured)
        {
            return;
        }

        using var session = NewSession(AiAccessLevel.Plan);
        var missing = "AI_T_NO_SUCH_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        (string Tool, Func<string, JsonObject> Args)[] tools =
        [
            (AgentToolNames.DescribeObject, name => new JsonObject { ["object"] = name }),
            (AgentToolNames.GetIndexInfo, name => new JsonObject { ["table"] = name }),
            (AgentToolNames.GetTableStats, name => new JsonObject { ["table"] = name }),
            (AgentToolNames.GetHistogram, name => new JsonObject { ["table"] = name, ["column"] = "K" }),
        ];

        foreach (var (tool, makeArgs) in tools)
        {
            var outside = await CallAsync(session, tool, makeArgs(db.Outside));
            var none = await CallAsync(session, tool, makeArgs(missing));

            Assert.Equal("scope", Code(outside));
            Assert.Equal("scope", Code(none));
            Assert.Equal(outside.Result.Text, none.Result.Text);
            Assert.DoesNotContain(db.Outside, outside.Result.Text, StringComparison.OrdinalIgnoreCase);
        }

        // 범위 안 표는 같은 도구로 읽힌다.
        Ok(await CallAsync(session, AgentToolNames.GetTableStats, new JsonObject { ["table"] = db.Order }));
        Ok(await CallAsync(session, AgentToolNames.DescribeObject, new JsonObject { ["object"] = db.Dept }));
    }

    [Fact]
    public async Task Sql_that_reaches_a_table_outside_the_scope_is_refused_by_explain_plan_and_benchmark()
    {
        if (!db.Configured)
        {
            return;
        }

        using var session = NewSession(AiAccessLevel.Query);
        var inside = $"SELECT * FROM {db.Order} WHERE stat_cd = 'A'";
        var outside = $"SELECT k FROM {db.Outside}";

        Ok(await CallAsync(session, AgentToolNames.ExplainPlan, new JsonObject { ["sql"] = inside }));
        Assert.Equal("scope", Code(await CallAsync(session, AgentToolNames.ExplainPlan, new JsonObject { ["sql"] = outside })));

        var variants = new JsonArray(
            new JsonObject { ["label"] = "ok", ["sql"] = $"SELECT COUNT(*) FROM {db.Order}" },
            new JsonObject { ["label"] = "bad", ["sql"] = outside });
        Assert.Equal("scope", Code(await CallAsync(session, AgentToolNames.BenchmarkQueries, new JsonObject { ["variants"] = variants, ["repeat"] = 1 })));
    }

    [Fact]
    public async Task Sql_ids_are_limited_to_the_request_statement_and_the_ids_that_tools_returned()
    {
        if (!db.Configured)
        {
            return;
        }

        using var session = NewSession(AiAccessLevel.Query);

        // 요청의 대상 문장 sql_id 는 처음부터 조회된다. 모르는 sql_id 는 거부된다.
        Ok(await CallAsync(session, AgentToolNames.GetCursorPlan, new JsonObject { ["sql_id"] = db.SqlId }));
        Assert.Equal("sql_id_scope", Code(await CallAsync(session, AgentToolNames.GetCursorPlan, new JsonObject { ["sql_id"] = "0000000000000" })));

        // 벤치마크가 돌려준 sql_id 는 이 요청 안에서만 조회된다.
        var variants = new JsonArray(new JsonObject { ["label"] = "a", ["sql"] = $"SELECT COUNT(*) FROM {db.Order}" });
        var bench = Ok(await CallAsync(session, AgentToolNames.BenchmarkQueries, new JsonObject { ["variants"] = variants, ["repeat"] = 1 }));
        var id = bench.AsArray()[0]!["sql_id"]!.GetValue<string>();

        Ok(await CallAsync(session, AgentToolNames.GetCursorPlan, new JsonObject { ["sql_id"] = id }));
        Ok(await CallAsync(session, AgentToolNames.GetSqlStats, new JsonObject { ["sql_id"] = id }));

        using var another = NewSession(AiAccessLevel.Query);
        Assert.Equal("sql_id_scope", Code(await CallAsync(another, AgentToolNames.GetCursorPlan, new JsonObject { ["sql_id"] = id })));
    }

    [Fact]
    public async Task Tuning_context_uses_the_request_statement_and_the_gate_remembers_its_sql_id()
    {
        if (!db.Configured)
        {
            return;
        }

        var audit = new MemoryAudit();
        using var session = NewSession(AiAccessLevel.Plan);

        var body = Ok(await CallAsync(session, AgentToolNames.GetTuningContext, new JsonObject(), audit));

        var text = body.ToJsonString();
        Assert.Contains(db.Order, text, StringComparison.Ordinal);
        Assert.Contains(db.Dept, text, StringComparison.Ordinal);
        Assert.DoesNotContain(db.Outside, text, StringComparison.Ordinal);
        Assert.True(session.IsKnownSqlId(db.SqlId));
        Assert.Equal(AiToolJudgement.Allowed, Assert.Single(audit.Entries).Verdict);
    }

    [Fact]
    public async Task Level_environment_and_explain_setting_gate_the_sql_tools()
    {
        if (!db.Configured)
        {
            return;
        }

        var sql = new JsonObject { ["sql"] = $"SELECT * FROM {db.Order}" };
        var variants = () => new JsonObject { ["variants"] = new JsonArray(new JsonObject { ["label"] = "a", ["sql"] = $"SELECT COUNT(*) FROM {db.Order}" }) };

        // Plan 수준에서는 실행하는 도구가 없다.
        using var plan = NewSession(AiAccessLevel.Plan);
        Assert.Equal("level", Code(await CallAsync(plan, AgentToolNames.BenchmarkQueries, variants())));

        // 운영 접속: 최대 Plan, EXPLAIN PLAN 은 접속이 켜지 않으면 닫혀 있다. 목록에도 없다.
        using var prod = NewSession(AiAccessLevel.Plan, ConnectionEnvironment.Production);
        Assert.Equal("explain_plan_disabled", Code(await CallAsync(prod, AgentToolNames.ExplainPlan, sql)));
        Assert.Equal("level", Code(await CallAsync(prod, AgentToolNames.BenchmarkQueries, variants())));
        var listed = NewGate(prod, new MemoryAudit()).ListTools(prod).Select(t => t.Name).ToList();
        Assert.DoesNotContain(AgentToolNames.ExplainPlan, listed);
        Assert.DoesNotContain(AgentToolNames.BenchmarkQueries, listed);

        // 개발 접속이라도 EXPLAIN PLAN 을 끄면 거부한다.
        using var off = NewSession(AiAccessLevel.Plan, allowExplain: false);
        Assert.Equal("explain_plan_disabled", Code(await CallAsync(off, AgentToolNames.ExplainPlan, sql)));
    }

    private sealed class FixedSource(AgentSession session, ISession dbSession) : IAgentSessionSource
    {
        public AgentSession? Find(string runId) => runId == session.RunId ? session : null;

        public ValueTask<ISession> AcquireDbSessionAsync(AgentSession s, CancellationToken ct) => ValueTask.FromResult(dbSession);
    }

    private sealed class MemoryAudit : IAgentAuditStore
    {
        public List<AgentAuditEntry> Entries { get; } = [];

        public ValueTask AppendAsync(AgentAuditEntry entry, CancellationToken ct)
        {
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<AgentAuditEntry>> ListRecentAsync(int limit, Guid? profileId, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<AgentAuditEntry>>(Entries);
    }
}
