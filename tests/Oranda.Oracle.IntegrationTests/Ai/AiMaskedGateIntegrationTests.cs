using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;
using Oranda.Language.Ai.Handlers;
using Oranda.Language.Ai.Literals;

namespace Oranda.Oracle.IntegrationTests.Ai;

/// <summary>
/// 리터럴 가림(ai-tuning-2 c5): 실제 <see cref="AgentGate"/> + 실제 <see cref="SqlLiteralMasker"/> + 실제 26ai.
/// 가린 인자(<c>:vN</c>)로 explain_plan·get_cursor_plan·benchmark_queries 를 부르고, 운영 라벨 접속에서 benchmark 가 되는지,
/// 결과의 술어에 원래 리터럴이 남지 않는지 확인한다. DB 를 바꾸지 않는 조회만 한다.
/// </summary>
[Trait("Category", "Integration")]
public sealed partial class AiMaskedGateIntegrationTests(AiToolsDbFixture db) : IClassFixture<AiToolsDbFixture>
{
    private static readonly SqlLiteralMasker Masker = new();

    [GeneratedRegex("'[^']*'")]
    private static partial Regex QuotedString();

    private (AgentSession Session, LiteralVault Vault) NewSession(
        AiAccessLevel level, ConnectionEnvironment env = ConnectionEnvironment.Development, bool withVault = true)
    {
        var profile = db.Profile with { Environment = env, AiAccess = level, AllowExplainPlan = null };
        var vault = Masker.CreateVault(db.StatementText);
        var context = new AiContext("it-admin", env, level, StatementText: db.StatementText, SqlId: db.SqlId, CurrentSchema: db.Owner);
        var session = new AgentSession(new AgentRunScope("it-mask", profile, context) { Literals = withVault ? vault : null });
        return (session, vault);
    }

    private AgentGate NewGate(AgentSession session) =>
        new(
            new FixedSource(session, db.Db),
            db.Extractor,
            [
                new GetCursorPlanHandler(db.Boxes),
                new ExplainPlanHandler(db.Boxes, db.Extractor),
                new GetTuningContextHandler(db.Boxes, db.Extractor),
                new BenchmarkQueriesHandler(db.Boxes, db.Extractor),
            ],
            new MemoryAudit(),
            masker: Masker);

    private async Task<AgentGateResponse> CallAsync(AgentSession session, string tool, JsonObject args) =>
        await NewGate(session).CallToolAsync(session, tool, args, default);

    private static JsonNode Ok(AgentGateResponse r)
    {
        Assert.False(r.Result.IsError, r.Result.Text);
        return JsonNode.Parse(r.Result.Text)!;
    }

    private static string Code(AgentGateResponse r)
    {
        Assert.True(r.Result.IsError, r.Result.Text);
        return JsonNode.Parse(r.Result.Text)!["code"]!.GetValue<string>();
    }

    private static IEnumerable<string> Predicates(JsonNode plan) =>
        plan["steps"]!.AsArray().SelectMany(step => new[] { (string?)step!["access"], (string?)step["filter"] }).OfType<string>();

    [Fact]
    public async Task Explain_plan_runs_with_masked_arguments_and_the_result_predicates_carry_no_literal()
    {
        if (!db.Configured)
        {
            return;
        }

        var (session, vault) = NewSession(AiAccessLevel.Plan);
        using var _ = session;
        var original = $"SELECT * FROM {db.Order} WHERE stat_cd = 'ZQ' AND order_id > 4242";
        var masked = Masker.Mask(original, vault).Text;
        Assert.DoesNotContain("ZQ", masked, StringComparison.Ordinal);

        var plan = Ok(await CallAsync(session, AgentToolNames.ExplainPlan, new JsonObject { ["sql"] = masked }));

        var predicates = Predicates(plan).ToList();
        Assert.NotEmpty(predicates);
        Assert.DoesNotContain(predicates, p => p.Contains("ZQ", StringComparison.Ordinal) || QuotedString().IsMatch(p));
        Assert.DoesNotContain("4242", string.Concat(predicates), StringComparison.Ordinal);
        Assert.Contains(predicates, p => p.Contains(":v", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cursor_plan_of_the_request_statement_has_masked_predicates()
    {
        if (!db.Configured)
        {
            return;
        }

        var (session, _) = NewSession(AiAccessLevel.Plan);
        using var _s = session;

        var plan = Ok(await CallAsync(session, AgentToolNames.GetCursorPlan, new JsonObject { ["sql_id"] = db.SqlId }));

        // 대상 문장은 stat_cd = 'B' 로 실행됐다. 공유 풀의 계획 술어에 원래 값이 그대로 나오면 안 된다.
        Assert.DoesNotContain(Predicates(plan), p => QuotedString().IsMatch(p));
    }

    [Fact]
    public async Task Benchmark_runs_on_a_production_label_with_masked_arguments_and_audit_marks_masking()
    {
        if (!db.Configured)
        {
            return;
        }

        var (session, vault) = NewSession(AiAccessLevel.Query, ConnectionEnvironment.Production);
        using var _ = session;
        var original = $"SELECT COUNT(*) FROM {db.Order} WHERE stat_cd = 'ZQ' OR order_id = 4242";
        var masked = Masker.Mask(original, vault).Text;
        var variants = new JsonArray(new JsonObject { ["label"] = "prod", ["sql"] = masked });
        var audit = new MemoryAudit();
        var gate = new AgentGate(new FixedSource(session, db.Db), db.Extractor, [new BenchmarkQueriesHandler(db.Boxes, db.Extractor)], audit, masker: Masker);

        var response = await gate.CallToolAsync(session, AgentToolNames.BenchmarkQueries, new JsonObject { ["variants"] = variants, ["repeat"] = 2 }, default);

        var rows = Ok(response).AsArray();
        Assert.Equal(2, rows[0]!["repeat"]!.GetValue<int>());
        Assert.DoesNotContain("ZQ", response.Result.Text, StringComparison.Ordinal);
        var entry = Assert.Single(audit.Entries);
        Assert.StartsWith("literals=masked", entry.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("ZQ", entry.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_made_up_placeholder_is_refused_and_production_without_a_vault_closes_the_sql_tools()
    {
        if (!db.Configured)
        {
            return;
        }

        var (session, vault) = NewSession(AiAccessLevel.Plan);
        using var _ = session;
        vault.Add("'ZQ'");
        Assert.Equal(
            "literal_unknown",
            Code(await CallAsync(session, AgentToolNames.ExplainPlan, new JsonObject { ["sql"] = $"SELECT * FROM {db.Order} WHERE stat_cd = :v9" })));

        var (noVault, _) = NewSession(AiAccessLevel.Query, ConnectionEnvironment.Production, withVault: false);
        using var _n = noVault;
        Assert.Equal(
            "literal_mask_unavailable",
            Code(await CallAsync(noVault, AgentToolNames.ExplainPlan, new JsonObject { ["sql"] = $"SELECT * FROM {db.Order}" })));
        Assert.Equal(
            "literal_mask_unavailable",
            Code(await CallAsync(noVault, AgentToolNames.BenchmarkQueries, new JsonObject
            {
                ["variants"] = new JsonArray(new JsonObject { ["label"] = "a", ["sql"] = $"SELECT COUNT(*) FROM {db.Order}" }),
            })));
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
