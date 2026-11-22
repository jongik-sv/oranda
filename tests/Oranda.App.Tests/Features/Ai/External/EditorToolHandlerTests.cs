using System.Text.Json.Nodes;
using Oranda.App.Features.Ai.External.Editor;
using Oranda.App.Features.Navigation;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.ColumnReorder;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Security;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.Ai.External;

/// <summary>편집기 도구 처리자(계약 A1): 허가로 잠김·운영·바인드 값을 가르고, 결과 JSON 칸 이름과 오류 코드가 계약과 같다.</summary>
public class EditorToolHandlerTests
{
    private static readonly Guid Dev = Guid.NewGuid();
    private static readonly Guid Prod = Guid.NewGuid();

    private static readonly AgentGrantView Grant = new(
        "g1",
        [
            new AgentGrantConnection(Dev, "dev-erp", ConnectionEnvironment.Development, Dbms.Oracle, AiAccessLevel.QueryRows),
            new AgentGrantConnection(Prod, "prd-erp", ConnectionEnvironment.Production, Dbms.Oracle, AiAccessLevel.Metadata),
        ],
        DateTimeOffset.UnixEpoch,
        null,
        ["Claude Code"]);

    private sealed class Rig
    {
        public Rig()
        {
            Host = new SqlEditorHost(dialogs: new NoDialogs());
            Service = new ExternalEditorService { HostSource = () => Host };
            DevTab = Host.NewDocument("SELECT * FROM sample_orders WHERE id = :id");
            DevTab.ConnectionProfileId = Dev;
            DevTab.Binds.Add("id").Text = "7";
            ProdTab = Host.NewDocument("SELECT 1 FROM dual");
            ProdTab.ConnectionProfileId = Prod;
            OtherTab = Host.NewDocument("SELECT 2 FROM dual");
            OtherTab.ConnectionProfileId = Guid.NewGuid(); // 허가 밖 접속
            OtherTab.Title = "다른 접속";
        }

        public SqlEditorHost Host { get; }

        public ExternalEditorService Service { get; }

        public SqlEditorViewModel DevTab { get; }

        public SqlEditorViewModel ProdTab { get; }

        public SqlEditorViewModel OtherTab { get; }

        public static async Task<AgentToolOutput> Call(EditorToolHandler handler, JsonObject args, AgentGrantConnection? connection = null) =>
            await handler.ExecuteAsync(new AppToolInvocation(Grant, "Claude Code", args, connection), CancellationToken.None);
    }

    /// <summary>처리 본문을 시험 스레드에서 바로 부른다(같은 어셈블리의 Headless 앱이 떠 있으면 기본값은 그 UI 스레드로 넘겨 이 시험의 문서와 스레드가 갈린다).</summary>
    private static Task<AgentToolOutput> Inline(Func<Task<AgentToolOutput>> body) => body();

    private static JsonObject Args(params (string Key, JsonNode? Value)[] pairs)
    {
        var json = new JsonObject();
        foreach (var (key, value) in pairs)
        {
            json[key] = value;
        }

        return json;
    }

    [Fact]
    public async Task List_tabs_returns_an_object_with_locked_tabs_showing_only_the_title()
    {
        var rig = new Rig();

        var output = await Rig.Call(new ListTabsToolHandler(rig.Service) { OnUiThread = Inline }, new JsonObject());

        var tabs = output.Result!["tabs"]!.AsArray();
        Assert.Equal(3, tabs.Count);
        var dev = tabs.Single(t => (string)t!["tab_id"]! == rig.DevTab.TabId.ToString())!;
        Assert.Equal("dev-erp", (string)dev["connection"]!);
        Assert.Equal(rig.DevTab.Version, (long)dev["version"]!);
        Assert.False((bool)dev["locked"]!);
        var other = tabs.Single(t => (string)t!["tab_id"]! == rig.OtherTab.TabId.ToString())!;
        Assert.True((bool)other["locked"]!);
        Assert.Equal("다른 접속", (string)other["title"]!);
        Assert.Null(other["version"]);
        Assert.Null(other["connection"]);
        var prod = tabs.Single(t => (string)t!["tab_id"]! == rig.ProdTab.TabId.ToString())!;
        Assert.False((bool)prod["locked"]!); // 허가 안 운영 접속: 잠기지 않지만 글은 주지 않는다
    }

    [Fact]
    public async Task Get_tab_gives_text_for_development_and_refuses_production_and_outside_tabs()
    {
        var rig = new Rig();
        var handler = new GetTabToolHandler(rig.Service) { OnUiThread = Inline };

        var dev = await Rig.Call(handler, Args(("tab", rig.DevTab.TabId.ToString()), ("scope", "document")));
        Assert.Equal("SELECT * FROM sample_orders WHERE id = :id", (string)dev.Result!["text"]!);
        Assert.Equal("dev-erp", (string)dev.Result["connection"]!);
        Assert.Equal(1, (int)dev.Result["caret"]!["line"]!);
        Assert.Equal("query", (string)dev.Result["statement"]!["kind"]!);
        Assert.Equal("id", (string)dev.Result["binds"]![0]!);

        Assert.Equal(AgentErrorCodes.Environment, (await Rig.Call(handler, Args(("tab", rig.ProdTab.TabId.ToString())))).ErrorCode);
        Assert.Equal(AgentErrorCodes.Locked, (await Rig.Call(handler, Args(("tab", rig.OtherTab.TabId.ToString())))).ErrorCode);
        Assert.Equal(AgentErrorCodes.TabNotFound, (await Rig.Call(handler, Args(("tab", "not-a-tab")))).ErrorCode);
        Assert.Equal(AgentErrorCodes.InvalidArguments, (await Rig.Call(handler, Args(("scope", "caret")))).ErrorCode);
    }

    [Fact]
    public async Task Propose_edit_waits_then_get_proposal_follows_the_bar()
    {
        var rig = new Rig();
        var propose = new ProposeEditToolHandler(rig.Service) { OnUiThread = Inline };

        var pending = await Rig.Call(propose, Args(
            ("tab", rig.DevTab.TabId.ToString()), ("base_version", (int)rig.DevTab.Version), ("range", "0:8"), ("new_text", "SELECT a"), ("wait_s", 0)));
        Assert.Equal("pending", (string)pending.Result!["status"]!);
        var id = (string)pending.Result["proposal_id"]!;
        Assert.Equal("Claude Code 가 편집을 제안했습니다", rig.DevTab.AgentBar!.Headline);

        rig.DevTab.AgentBar.DiscardCommand.Execute(null);

        var after = await Rig.Call(new GetProposalToolHandler(rig.Service) { OnUiThread = Inline }, Args(("proposal_id", id)));
        Assert.Equal("rejected", (string)after.Result!["status"]!);

        var stale = await Rig.Call(propose, Args(("tab", rig.DevTab.TabId.ToString()), ("base_version", 999), ("new_text", "x")));
        Assert.Equal("stale", (string)stale.Result!["status"]!); // 거부가 아니라 성공 결과
        Assert.False(stale.IsError);

        Assert.Equal(AgentErrorCodes.InvalidArguments, (await Rig.Call(propose, Args(("tab", rig.DevTab.TabId.ToString()), ("base_version", (int)rig.DevTab.Version), ("new_text", "x"), ("range", "middle")))).ErrorCode);
        Assert.Equal(AgentErrorCodes.Environment, (await Rig.Call(propose, Args(("tab", rig.ProdTab.TabId.ToString()), ("base_version", (int)rig.ProdTab.Version), ("new_text", "x")))).ErrorCode);
        Assert.Equal(AgentErrorCodes.ProposalNotFound, (await Rig.Call(new GetProposalToolHandler(rig.Service) { OnUiThread = Inline }, Args(("proposal_id", "p-none")))).ErrorCode);

        rig.DevTab.IsReadOnly = true;
        Assert.Equal(AgentErrorCodes.ReadOnly, (await Rig.Call(propose, Args(("tab", rig.DevTab.TabId.ToString()), ("base_version", (int)rig.DevTab.Version), ("new_text", "x")))).ErrorCode);
    }

    [Fact]
    public async Task Get_binds_gives_values_at_row_level_and_new_tab_uses_the_resolved_connection()
    {
        var rig = new Rig();

        var binds = await Rig.Call(new GetBindsToolHandler(rig.Service) { OnUiThread = Inline }, Args(("tab", rig.DevTab.TabId.ToString())));
        var bind = binds.Result!["binds"]![0]!;
        Assert.Equal(("ID", "7", true), ((string)bind["name"]!, (string)bind["value"]!, (bool)bind["has_value"]!));

        var created = await Rig.Call(new NewTabToolHandler(rig.Service) { OnUiThread = Inline }, Args(("text", "SELECT 3 FROM dual"), ("title", "새 조회")), Grant.Connections[0]);
        var tab = rig.Host.Tabs.Single(t => t.TabId.ToString() == (string)created.Result!["tab_id"]!);
        Assert.Equal("AI·새 조회", tab.Title);
        Assert.Equal(Dev, tab.ConnectionProfileId);
        Assert.Equal(AgentErrorCodes.Environment, (await Rig.Call(new NewTabToolHandler(rig.Service) { OnUiThread = Inline }, Args(("text", "x")), Grant.Connections[1])).ErrorCode);
    }

    [Fact]
    public async Task Save_tab_times_out_and_focus_tab_checks_its_range()
    {
        var rig = new Rig();

        var focus = new FocusTabToolHandler(rig.Service) { OnUiThread = Inline };
        Assert.True((bool)(await Rig.Call(focus, Args(("tab", rig.DevTab.TabId.ToString()), ("range", "0:6")))).Result!["ok"]!);
        Assert.Same(rig.DevTab, rig.Host.Active);
        Assert.Equal(AgentErrorCodes.InvalidArguments, (await Rig.Call(focus, Args(("tab", rig.DevTab.TabId.ToString()), ("range", "document")))).ErrorCode);

        // 기다림 뒤에는 다른 스레드일 수 있어 문서를 읽지 않는다
        var save = await Rig.Call(new SaveTabToolHandler(rig.Service) { OnUiThread = Inline, Wait = TimeSpan.FromMilliseconds(20) }, Args(("tab", rig.DevTab.TabId.ToString())));
        Assert.Equal("timeout", (string)save.Result!["status"]!);
    }

    private sealed class FakeSessions(params ISession[] sessions) : ISessionManager
    {
        public IReadOnlyList<ISession> Sessions { get; } = sessions;

        public Task<ISession> OpenAsync(ConnectionProfile profile, SecureCredential credential, SessionPurpose purpose, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Names : IStatementReferenceExtractor
    {
        public ValueTask<AgentStatementInfo> AnalyzeAsync(ISession db, string sql, string? defaultSchema, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<AgentObjectRef?> ResolveAsync(ISession db, string name, string? defaultSchema, CancellationToken ct) =>
            ValueTask.FromResult(name == "ORDERS" ? new AgentObjectRef("SAMPLE", "ORDERS", "TABLE") : null);

        public ValueTask<IReadOnlyList<AgentObjectRef>> GetRelatedAsync(ISession db, IReadOnlyList<AgentObjectRef> objects, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Provider(ISessionManager sessions) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ISessionManager) ? sessions : serviceType == typeof(IStatementReferenceExtractor) ? new Names() : null;
    }

    [Fact]
    public async Task Reveal_object_resolves_the_name_on_the_open_session_and_refuses_an_unopened_connection()
    {
        var rig = new Rig();
        var session = new ReorderSession();
        var connection = new AgentGrantConnection(session.Profile.Id, "dev-erp", ConnectionEnvironment.Development, Dbms.Oracle, AiAccessLevel.Metadata);
        var opened = new List<DbObjectRef>();
        var handler = new RevealObjectToolHandler(rig.Service)
        {
            OnUiThread = Inline,
            Services = () => new Provider(new FakeSessions(session)),
            Revealer = _ => new ExternalObjectRevealer(new ObjectNavigationCommandHandler
            {
                BrowserProvider = () => null,
                OpenObject = (target, _, _) =>
                {
                    opened.Add(target);
                    return Task.CompletedTask;
                },
            }),
        };

        var output = await Rig.Call(handler, Args(("object", "ORDERS"), ("open", true)), connection);
        Assert.True((bool)output.Result!["opened"]!);
        Assert.Equal(new DbObjectRef("SAMPLE", "ORDERS", "TABLE"), Assert.Single(opened));

        var missing = await Rig.Call(handler, Args(("object", "NOPE")), connection);
        Assert.False((bool)missing.Result!["found"]!);

        var closed = await Rig.Call(handler, Args(("object", "ORDERS")), Grant.Connections[0]); // 열린 세션이 없는 접속
        Assert.Equal(AgentErrorCodes.NotAllowed, closed.ErrorCode);
    }
}
