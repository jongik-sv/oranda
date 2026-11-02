using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Core.Settings;
using Oranda.Platform.Ai;
using Oranda.Platform.Ai.Api;
using Oranda.Testing;

namespace Oranda.Platform.Tests.Ai.Api;

/// <summary>
/// 가짜 게이트가 아니라 실제 <see cref="AgentGate"/> 를 거치는 시험: 거부와 가림이 모델에게 돌아가는 글과 화면 사건에 그대로 실리는지,
/// 감사 기록의 client 칸을 드라이버가 채우는지 확인한다. DB 쪽(세션·문장 분석·처리자)만 가짜다.
/// </summary>
public sealed class OpenAiApiDriverGateTests : IDisposable
{
    private const string HistogramResult =
        """{"table":"APP.TB_ORDER","column":"STAT_CD","kind":"FREQUENCY","buckets":[{"endpoint":"A","count":10},{"endpoint":"B","count":5}]}""";

    private static readonly AiLlmProfile Profile = new()
    {
        Id = Guid.NewGuid(),
        Name = "사내 서버",
        Preset = AiProfilePreset.Custom,
        Format = AiApiFormat.OpenAi,
        BaseUrl = "http://llm.example:8080",
        Model = "model-test",
    };

    private readonly FakeOpenAiServer _server = new();
    private readonly RecordingAudit _audit = new();
    private readonly CountingHandler _histogram = new(AgentToolNames.GetHistogram);
    private readonly FixedSource _source = new();
    private readonly OpenAiApiDriver _driver;

    public OpenAiApiDriverGateTests()
    {
        var gate = new AgentGate(_source, new FixedExtractor(), [_histogram, new CountingHandler(AgentToolNames.DescribeObject)], _audit);
        _driver = new OpenAiApiDriver(gate, _server, new OpenAiApiOptions(TimeSpan.FromSeconds(30)));
    }

    public void Dispose()
    {
        _driver.Dispose();
        _source.Dispose();
    }

    private static AgentSession NewSession()
    {
        var profile = new ConnectionProfile("dev", "scott", "h/s", AiAccess: AiAccessLevel.Plan);
        return new AgentSession(new AgentRunScope("run-1", profile, new AiContext("dev", null, AiAccessLevel.Plan, StatementText: "SELECT * FROM TB_ORDER")));
    }

    private async Task<List<AiEvent>> RunAsync()
    {
        var events = new List<AiEvent>();
        var request = new AiRequest("히스토그램을 확인해 줘", new AiContext("dev", null, AiAccessLevel.Plan), Session: NewSession());
        await foreach (var e in _driver.RunAsync(request, new AiRoute(Profile, null), CancellationToken.None))
        {
            events.Add(e);
        }

        return events;
    }

    [Fact]
    public async Task A_tool_the_gate_does_not_know_is_denied_and_the_denial_reaches_the_model_and_the_card()
    {
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("call_1", "DROP_EVERYTHING", "{\"sql\":\"drop table t\"}"));
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("거부되었습니다."));

        var events = await RunAsync();

        var result = events.OfType<AiToolResult>().Single();
        Assert.Equal(AiToolJudgement.Denied, result.Judgement);
        var toolMessage = (string?)_server.Requests[1].Messages[^1]!["content"];
        Assert.Contains("\"error\"", toolMessage, StringComparison.Ordinal);
        Assert.Empty(_histogram.Calls);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal((AiToolJudgement.Denied, OpenAiApiDriver.ClientName), (entry.Verdict, entry.Client));
    }

    [Fact]
    public async Task Masked_results_reach_the_model_masked_and_the_card_says_masked_with_the_driver_client_in_the_audit()
    {
        _histogram.Respond = JsonNode.Parse(HistogramResult)!;
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("call_1", AgentToolNames.GetHistogram, "{\"table\":\"TB_ORDER\",\"column\":\"STAT_CD\"}"));
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("두 개 구간입니다."));

        var events = await RunAsync();

        var result = events.OfType<AiToolResult>().Single();
        Assert.Equal((AiToolJudgement.Masked, "masked"), (result.Judgement, result.Note));
        var toolMessage = (string?)_server.Requests[1].Messages[^1]!["content"];
        Assert.DoesNotContain("endpoint", toolMessage, StringComparison.Ordinal);
        Assert.Contains("\"count\":10", toolMessage, StringComparison.Ordinal);
        Assert.Equal(toolMessage!.Length, result.Size);
        Assert.Single(_histogram.Calls);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal((AiToolJudgement.Masked, OpenAiApiDriver.ClientName), (entry.Verdict, entry.Client));
    }

    private sealed class CountingHandler(string toolName) : IAgentToolHandler
    {
        public string ToolName { get; } = toolName;

        public List<AgentToolInvocation> Calls { get; } = [];

        public JsonNode Respond { get; set; } = new JsonObject { ["ok"] = true };

        public ValueTask<AgentToolOutput> ExecuteAsync(AgentToolInvocation invocation, CancellationToken ct)
        {
            Calls.Add(invocation);
            return ValueTask.FromResult(AgentToolOutput.Ok(Respond.DeepClone()));
        }
    }

    private sealed class RecordingAudit : IAgentAuditStore
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

    private sealed class FixedSource : IAgentSessionSource, IDisposable
    {
        private readonly FakeSession _db = new();

        public void Dispose() => _ = _db.DisposeAsync().AsTask();

        public AgentSession? Find(string runId) => null;

        public ValueTask<ISession> AcquireDbSessionAsync(AgentSession session, CancellationToken ct) => ValueTask.FromResult<ISession>(_db);
    }

    /// <summary>모든 문장이 APP.TB_ORDER 하나를 읽는 조회라고 답하는 추출기.</summary>
    private sealed class FixedExtractor : IStatementReferenceExtractor
    {
        private static readonly AgentObjectRef Order = new("APP", "TB_ORDER", "TABLE");

        public ValueTask<AgentStatementInfo> AnalyzeAsync(ISession db, string sql, string? defaultSchema, CancellationToken ct) =>
            ValueTask.FromResult(new AgentStatementInfo(StatementKind.Query, false, [Order], 1));

        public ValueTask<AgentObjectRef?> ResolveAsync(ISession db, string name, string? defaultSchema, CancellationToken ct) =>
            ValueTask.FromResult<AgentObjectRef?>(name.Equals("TB_ORDER", StringComparison.OrdinalIgnoreCase) ? Order : null);

        public ValueTask<IReadOnlyList<AgentObjectRef>> GetRelatedAsync(ISession db, IReadOnlyList<AgentObjectRef> objects, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<AgentObjectRef>>([]);
    }
}
