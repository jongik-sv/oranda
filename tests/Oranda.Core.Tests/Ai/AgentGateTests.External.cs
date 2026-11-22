using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Testing;

namespace Oranda.Core.Tests.Ai;

// 외부 허가의 관문(A3): Host 갈래·connection 필수·외부 상한·앱 도구 글 인자 한도·감사.
public partial class AgentGateTests
{
    private static readonly Guid ExtDevId = Guid.NewGuid();
    private static readonly Guid ExtTestId = Guid.NewGuid();
    private static readonly Guid ExtPrdId = Guid.NewGuid();

    private sealed class ExternalFixture
    {
        public ExternalFixture(params ConnectionProfile[] profiles)
        {
            Store.AttachWriter(new NullWriter());
            Issue = Store.Create(new AgentGrantRequest(
                [.. profiles.Select(p => new AgentGrantTarget(p, AgentGrantPolicy.DefaultCap(p.Environment)))], null));
            foreach (var name in new[] { AppToolNames.Query, AppToolNames.FindObjects, AppToolNames.GetDdl })
            {
                Handlers[name] = new FakeHandler(name);
            }

            Gate = new AgentGate(new ProfileSource(), Inner.Extractor, Handlers.Values, Inner.Audit, masker: new FakeMasker(), grants: Store)
            {
                AppHandlers = AppHandlerList,
            };
            Gate.ToolCompleted += (_, e) => Inner.Events.Add(e);
        }

        public GateFixture Inner { get; } = new();

        public AgentGrantStore Store { get; } = new(new ManualTimeProvider());

        public AgentGrantIssue Issue { get; }

        public Dictionary<string, FakeHandler> Handlers => Inner.Handlers;

        public List<FakeAppHandler> AppHandlerList { get; } =
            [new(AppToolNames.ListTabs), new(AppToolNames.NewTab), new(AppToolNames.RevealObject), new(AppToolNames.GetHistory), new(AppToolNames.CheckSql)];

        public FakeAppHandler App(string name) => AppHandlerList.Single(h => h.ToolName == name);

        public AgentGate Gate { get; }

        public Task<AgentGateResponse> Call(string tool, string? json = null, string client = "claude-code") =>
            Gate.CallToolAsync(Issue.Grant, client, tool, json is null ? null : JsonNode.Parse(json), CancellationToken.None);
    }

    private static ConnectionProfile ExtDev(AiAccessLevel level = AiAccessLevel.QueryRows) => new("dev-erp", "scott", "h/s", Id: ExtDevId, AiAccess: level);

    private static ConnectionProfile ExtTest() => new("tst-erp", "scott", "h/s", Id: ExtTestId, Environment: ConnectionEnvironment.Test);

    private static ConnectionProfile ExtPrd() => new("prd-erp", "scott", "h/s", Id: ExtPrdId, Environment: ConnectionEnvironment.Production);

    [Fact]
    public async Task External_db_tool_needs_a_connection_when_several_are_allowed()
    {
        var fx = new ExternalFixture(ExtDev(), ExtTest());

        AssertDenied(await fx.Call(AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}"""), AgentErrorCodes.ConnectionRequired);
        AssertDenied(await fx.Call(AgentToolNames.DescribeObject, """{"object":"TB_ORDER","connection":"nope"}"""), AgentErrorCodes.Connection);
        Assert.Empty(fx.Handlers[AgentToolNames.DescribeObject].Calls);

        var ok = await fx.Call(AgentToolNames.DescribeObject, """{"object":"TB_ORDER","connection":"DEV-ERP"}""");
        Assert.False(ok.Result.IsError, ok.Result.Text);
        var call = Assert.Single(fx.Handlers[AgentToolNames.DescribeObject].Calls);
        Assert.Equal("APP.TB_ORDER", (string?)call.Arguments["object"]); // 시작 문장이 없어도 접속 전체 범위(AllObjects)
        Assert.True(call.Session.Run.External);

        var audit = fx.Inner.Audit.Entries[^1];
        Assert.Equal("claude-code", audit.Client);
        Assert.Equal(fx.Issue.Grant.GrantId + ":dev-erp", audit.RunId);
        Assert.Equal(3, fx.Inner.Audit.Entries.Count); // 거부 둘도 감사에 남는다
    }

    [Fact]
    public async Task External_db_tool_uses_the_only_connection_when_omitted()
    {
        var fx = new ExternalFixture(ExtDev());
        var ok = await fx.Call(AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        Assert.False(ok.Result.IsError, ok.Result.Text);
        Assert.Equal("dev-erp", (string?)Assert.Single(fx.Handlers[AgentToolNames.DescribeObject].Calls).Arguments["connection"]);
    }

    [Fact]
    public async Task Production_is_capped_at_metadata_for_external_grants()
    {
        var fx = new ExternalFixture(ExtPrd());
        AssertDenied(await fx.Call(AgentToolNames.GetTableStats, """{"table":"TB_ORDER"}"""), AgentErrorCodes.Level);
        AssertDenied(await fx.Call(AppToolNames.Query, """{"sql":"SELECT 1 FROM TB_ORDER"}"""), AgentErrorCodes.Level);
        var ddl = await fx.Call(AppToolNames.GetDdl, """{"object":"TB_ORDER"}""");
        Assert.False(ddl.Result.IsError, ddl.Result.Text); // 메타데이터는 된다(결과의 SQL 글은 가림)

        var names = fx.Gate.ListTools(fx.Store.Find(fx.Issue.Grant.GrantId)!).Select(t => t.Name).ToList();
        Assert.Contains(AgentToolNames.DescribeObject, names);
        Assert.Contains(AppToolNames.GetDdl, names);
        Assert.Contains(AppToolNames.ListTabs, names);
        Assert.DoesNotContain(AppToolNames.Query, names);
        Assert.DoesNotContain(AgentToolNames.GetCursorPlan, names);
    }

    [Fact]
    public async Task Query_runs_one_checked_select_and_refuses_unverifiable_functions()
    {
        var fx = new ExternalFixture(ExtDev());
        var ok = await fx.Call(AppToolNames.Query, """{"sql":"SELECT * FROM TB_ORDER","max_rows":10}""");
        Assert.False(ok.Result.IsError, ok.Result.Text);
        var call = Assert.Single(fx.Handlers[AppToolNames.Query].Calls);
        Assert.True(call.MayExecuteSql);

        AssertDenied(await fx.Call(AppToolNames.Query, """{"sql":"UPDATE TB_ORDER SET A = 1"}"""), "sql_not_single_query");
        AssertDenied(await fx.Call(AppToolNames.Query, """{"sql":"SELECT * FROM TB_ORDER@remote"}"""), "sql_unsafe");
        AssertDenied(await fx.Call(AppToolNames.Query, """{"sql":"SELECT APP.F_X(1) FROM TB_ORDER"}"""), "sql_unsafe"); // 함수 확인기가 없으면 실행하지 않는다
        Assert.Single(fx.Handlers[AppToolNames.Query].Calls);
    }

    [Fact]
    public async Task Row_values_from_query_are_removed_below_query_rows()
    {
        var fx = new ExternalFixture(ExtDev(AiAccessLevel.Query));
        fx.Handlers[AppToolNames.Query].Respond = _ => AgentToolOutput.Ok(new JsonObject
        {
            ["columns"] = new JsonArray(new JsonObject { ["name"] = "ID", ["type"] = "NUMBER" }),
            ["row_values"] = new JsonArray(new JsonArray(1)),
            ["row_count"] = 1,
        });

        var response = await fx.Call(AppToolNames.Query, """{"sql":"SELECT ID FROM TB_ORDER"}""");

        var result = JsonNode.Parse(response.Result.Text)!;
        Assert.Null(result["row_values"]);
        Assert.Equal(1, (int)result["row_count"]!);
        Assert.Equal(AiToolJudgement.Masked, response.Judgement);
    }

    [Fact]
    public async Task App_tools_skip_the_database_and_get_the_grant()
    {
        var fx = new ExternalFixture(ExtDev(), ExtTest());
        var response = await fx.Call(AppToolNames.ListTabs, null, client: "codex");

        Assert.False(response.Result.IsError, response.Result.Text);
        var invocation = Assert.Single(fx.App(AppToolNames.ListTabs).Calls);
        Assert.Equal(fx.Issue.Grant.GrantId, invocation.Grant.GrantId);
        Assert.Equal("codex", invocation.Client);
        Assert.Null(invocation.Connection);
        Assert.True(fx.Gate.RunsWithoutDatabase(AppToolNames.ListTabs));
        Assert.True(fx.Gate.RunsWithoutDatabase(AppToolNames.CheckSql));
        Assert.False(fx.Gate.RunsWithoutDatabase(AppToolNames.Query));
        Assert.False(fx.Gate.RunsWithoutDatabase(AgentToolNames.DescribeObject));

        var audit = fx.Inner.Audit.Entries[^1];
        Assert.Equal(AppToolNames.ListTabs, audit.Tool);
        Assert.Equal("codex", audit.Client);
        Assert.Equal(fx.Issue.Grant.GrantId, audit.RunId);
        Assert.Equal(AiToolJudgement.Allowed, audit.Verdict);
    }

    [Fact]
    public async Task App_tool_text_follows_the_schema_max_length_and_db_tools_keep_the_default()
    {
        var fx = new ExternalFixture(ExtDev());
        var big = new string('x', 150_000);
        var ok = await fx.Call(AppToolNames.NewTab, new JsonObject { ["text"] = big }.ToJsonString());
        Assert.False(ok.Result.IsError, ok.Result.Text);
        Assert.Equal(big, (string?)Assert.Single(fx.App(AppToolNames.NewTab).Calls).Arguments["text"]);

        AssertDenied(await fx.Call(AppToolNames.NewTab, new JsonObject { ["text"] = new string('x', 200_001) }.ToJsonString()), AgentErrorCodes.InvalidArguments);
        AssertDenied(await fx.Call(AppToolNames.Query, new JsonObject { ["sql"] = "SELECT 1 FROM TB_ORDER " + new string(' ', 20_000) }.ToJsonString()), AgentErrorCodes.InvalidArguments);
        AssertDenied(await fx.Call(AppToolNames.NewTab, """{"text":"a","kind":"plsql"}"""), AgentErrorCodes.InvalidArguments); // enum
        AssertDenied(await fx.Call(AppToolNames.NewTab, """{"text":"a","bogus":1}"""), AgentErrorCodes.InvalidArguments);
        AssertDenied(await fx.Call(AppToolNames.RevealObject, """{"object":"T","open":"yes"}"""), AgentErrorCodes.InvalidArguments); // boolean
    }

    [Fact]
    public async Task App_tools_with_a_connection_check_its_level_and_require_it_when_needed()
    {
        var fx = new ExternalFixture(ExtDev(AiAccessLevel.Off), ExtTest());

        AssertDenied(await fx.Call(AppToolNames.RevealObject, """{"object":"T"}"""), AgentErrorCodes.ConnectionRequired);
        AssertDenied(await fx.Call(AppToolNames.RevealObject, """{"object":"T","connection":"dev-erp"}"""), AgentErrorCodes.Level);
        AssertDenied(await fx.Call(AppToolNames.NewTab, """{"text":"a","connection":"zzz"}"""), AgentErrorCodes.Connection);

        var ok = await fx.Call(AppToolNames.RevealObject, """{"object":"T","connection":"tst-erp"}""");
        Assert.False(ok.Result.IsError, ok.Result.Text);
        Assert.Equal(ExtTestId, Assert.Single(fx.App(AppToolNames.RevealObject).Calls).Connection!.ProfileId);

        // 고르기용 connection 은 빠져도 된다(처리자가 허가 안 접속만 다룬다).
        var history = await fx.Call(AppToolNames.GetHistory);
        Assert.False(history.Result.IsError, history.Result.Text);
        Assert.Null(Assert.Single(fx.App(AppToolNames.GetHistory).Calls).Connection);
    }

    [Fact]
    public async Task App_handler_errors_keep_their_code_and_exceptions_hide_the_message()
    {
        var fx = new ExternalFixture(ExtDev());
        fx.App(AppToolNames.ListTabs).Respond = _ => AppToolOutputs.Locked();
        AssertDenied(await fx.Call(AppToolNames.ListTabs), AgentErrorCodes.Locked);

        fx.App(AppToolNames.ListTabs).Respond = _ => AppToolOutputs.TabNotFound("x");
        var missing = await fx.Call(AppToolNames.ListTabs);
        Assert.Equal(AiToolJudgement.Failed, missing.Judgement);
        Assert.Equal(AgentErrorCodes.TabNotFound, Code(missing));

        fx.App(AppToolNames.ListTabs).Respond = _ => throw new InvalidOperationException(@"C:\Users\me\secret.sql not found");
        var thrown = await fx.Call(AppToolNames.ListTabs);
        Assert.Equal(AgentErrorCodes.ToolException, Code(thrown));
        Assert.DoesNotContain("secret", thrown.Result.Text, StringComparison.Ordinal);

        fx.App(AppToolNames.ListTabs).Respond = _ => AgentToolOutput.Ok(new JsonObject { ["text"] = new string('x', AgentGate.MaxResultChars) });
        Assert.Equal(AgentErrorCodes.ResultTooLarge, Code(await fx.Call(AppToolNames.ListTabs)));

        Assert.Equal(AgentErrorCodes.NotImplemented, Code(await fx.Call(AppToolNames.GetStatus)));
        AssertDenied(await fx.Call("drop_everything"), AgentErrorCodes.UnknownTool);
    }

    [Fact]
    public async Task Ended_grants_refuse_every_tool()
    {
        var fx = new ExternalFixture(ExtDev());
        var session = fx.Store.SessionFor(fx.Issue.Grant.GrantId, ExtDevId, "claude-code")!;
        fx.Store.Revoke(fx.Issue.Grant.GrantId);

        AssertDenied(await fx.Call(AppToolNames.ListTabs), AgentErrorCodes.RunRevoked);
        AssertDenied(await fx.Call(AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}"""), AgentErrorCodes.RunRevoked);
        AssertDenied(await Call(fx.Inner, session, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}"""), "run_revoked");
        Assert.Empty(fx.App(AppToolNames.ListTabs).Calls);
    }

    [Fact]
    public async Task Every_external_call_raises_one_activity_with_grant_and_client()
    {
        var fx = new ExternalFixture(ExtDev(), ExtTest());
        var seen = new List<AgentExternalToolActivity>();
        fx.Gate.ExternalToolCompleted += (_, e) => seen.Add(e);

        await fx.Call(AppToolNames.ListTabs, null, client: "codex");
        await fx.Call(AgentToolNames.DescribeObject, """{"object":"TB_ORDER","connection":"dev-erp"}""", client: "claude-code");
        await fx.Call(AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""", client: "agy");

        Assert.Equal(3, seen.Count);
        Assert.All(seen, e => Assert.Equal(fx.Issue.Grant.GrantId, e.GrantId));
        Assert.Equal(["codex", "claude-code", "agy"], seen.Select(e => e.Client));
        Assert.Equal([null, "dev-erp", null], seen.Select(e => e.Connection));
        Assert.Equal(AiToolJudgement.Allowed, seen[1].Judgement);
        Assert.Equal(AiToolJudgement.Denied, seen[2].Judgement);
        Assert.Equal(AgentErrorCodes.ConnectionRequired, seen[2].Note);
        Assert.True(seen[0].Sequence < seen[1].Sequence && seen[1].Sequence < seen[2].Sequence);
    }

    [Fact]
    public async Task Ddl_tools_pass_packages_without_the_table_only_name_resolution()
    {
        var fx = new ExternalFixture(ExtDev());
        var ok = await fx.Call(AppToolNames.GetDdl, """{"object":"APP.PKG_ORDER"}""");
        Assert.False(ok.Result.IsError, ok.Result.Text);
        var call = Assert.Single(fx.Handlers[AppToolNames.GetDdl].Calls);
        Assert.Equal("APP.PKG_ORDER", (string?)call.Arguments["object"]); // 처리자가 스스로 푼다
        Assert.Empty(call.Resolved);

        // 표 도구는 그대로 이름을 풀고, 모르는 객체는 scope 로 막는다.
        AssertDenied(await fx.Call(AgentToolNames.DescribeObject, """{"object":"APP.PKG_ORDER"}"""), "scope");
    }

    [Fact]
    public void Internal_runs_still_see_only_the_twelve_tools()
    {
        var fx = new GateFixture();
        Assert.DoesNotContain(fx.Gate.ListTools(Session()), t => t.Name == AppToolNames.Query);
    }

    [Fact]
    public async Task Internal_runs_cannot_call_external_tools()
    {
        var fx = new ExternalFixture(ExtDev());
        AssertDenied(await fx.Gate.CallToolAsync(Session(), AppToolNames.Query, JsonNode.Parse("""{"sql":"SELECT 1 FROM TB_ORDER"}"""), CancellationToken.None), "unknown_tool");
    }

    private sealed class FakeAppHandler(string toolName) : IAppToolHandler
    {
        public string ToolName { get; } = toolName;

        public List<AppToolInvocation> Calls { get; } = [];

        public Func<AppToolInvocation, AgentToolOutput> Respond { get; set; } = _ => AgentToolOutput.Ok(new JsonObject { ["ok"] = true });

        public ValueTask<AgentToolOutput> ExecuteAsync(AppToolInvocation invocation, CancellationToken ct)
        {
            Calls.Add(invocation);
            return ValueTask.FromResult(Respond(invocation));
        }
    }

    /// <summary>하위 세션의 접속으로 DB 세션을 준다(접속이 다른 세션이 돌아오면 게이트가 session_mismatch 로 막는다).</summary>
    private sealed class ProfileSource : IAgentSessionSource
    {
        public AgentSession? Find(string runId) => null;

        public ValueTask<Oranda.Core.Sessions.ISession> AcquireDbSessionAsync(AgentSession session, CancellationToken ct) =>
            ValueTask.FromResult<Oranda.Core.Sessions.ISession>(new FakeSession(session.Profile));
    }

    private sealed class NullWriter : IAgentEndpointWriter
    {
        public void Publish(AgentGrantIssue issue)
        {
        }

        public void Update(AgentGrantView grant)
        {
        }

        public void Remove(AgentGrantView grant)
        {
        }
    }
}
