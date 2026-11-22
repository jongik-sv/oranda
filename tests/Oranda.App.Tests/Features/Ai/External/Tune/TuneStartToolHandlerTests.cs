using System.Text.Json.Nodes;
using Oranda.App.Features.Ai.External.Editor;
using Oranda.App.Features.Ai.External.Tune;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Core.Security;

namespace Oranda.App.Tests.Features.Ai.External.Tune;

/// <summary>S4 <c>tune_start</c>: 탭 접속 수준 Plan 이상만, 탭을 앞으로 낸 뒤 앱 안 튜닝을 올리고 기다리지 않는다(§6 #5).</summary>
public sealed class TuneStartToolHandlerTests
{
    private static readonly Guid Dev = Guid.NewGuid();
    private static readonly Guid Meta = Guid.NewGuid();

    private static readonly AgentGrantView Grant = new(
        "g1",
        [
            new AgentGrantConnection(Dev, "dev-erp", ConnectionEnvironment.Development, Dbms.Oracle, AiAccessLevel.Plan),
            new AgentGrantConnection(Meta, "test-erp", ConnectionEnvironment.Test, Dbms.Oracle, AiAccessLevel.Metadata),
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
            DevTab = Host.NewDocument("SELECT * FROM sample_orders WHERE id = 1");
            DevTab.ConnectionProfileId = Dev;
            MetaTab = Host.NewDocument("SELECT 1 FROM dual");
            MetaTab.ConnectionProfileId = Meta;
            Unbound = Host.NewDocument("SELECT 2 FROM dual");
            Outside = Host.NewDocument("SELECT 3 FROM dual");
            Outside.ConnectionProfileId = Guid.NewGuid();
            Host.Active = Unbound;
            Handler = new TuneStartToolHandler(Service)
            {
                OnUiThread = body => body(),
                Services = () => new EmptyServices(),
                Launch = _ => Launches++,
            };
        }

        public SqlEditorHost Host { get; }

        public ExternalEditorService Service { get; }

        public SqlEditorViewModel DevTab { get; }

        public SqlEditorViewModel MetaTab { get; }

        public SqlEditorViewModel Unbound { get; }

        public SqlEditorViewModel Outside { get; }

        public TuneStartToolHandler Handler { get; }

        public int Launches { get; private set; }

        public async Task<AgentToolOutput> Call(SqlEditorViewModel tab, string? range = null)
        {
            var args = new JsonObject { [AppFields.Tab] = tab.TabId.ToString() };
            if (range is not null)
            {
                args[AppFields.Range] = range;
            }

            return await Handler.ExecuteAsync(new AppToolInvocation(Grant, "Claude Code", args, null), CancellationToken.None);
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    [Fact]
    public async Task Plan_level_tab_is_brought_forward_and_tuning_is_launched_without_waiting()
    {
        var rig = new Rig();

        var output = await rig.Call(rig.DevTab, range: "statement");

        Assert.Null(output.ErrorCode);
        Assert.True((bool)output.Result!["started"]!);
        Assert.Equal(TuneStartToolHandler.ChatWindow, (string?)output.Result["chat"]);
        Assert.Same(rig.DevTab, rig.Host.Active);
        Assert.Equal(1, rig.Launches);
    }

    [Fact]
    public async Task Refuses_low_level_unbound_outside_and_unknown_tabs_without_launching()
    {
        var rig = new Rig();

        Assert.Equal(AgentErrorCodes.Level, (await rig.Call(rig.MetaTab)).ErrorCode);
        Assert.Equal(AgentErrorCodes.ConnectionRequired, (await rig.Call(rig.Unbound)).ErrorCode);
        Assert.Equal(AgentErrorCodes.Locked, (await rig.Call(rig.Outside)).ErrorCode);
        var unknown = await rig.Handler.ExecuteAsync(
            new AppToolInvocation(Grant, "Claude Code", new JsonObject { [AppFields.Tab] = Guid.NewGuid().ToString() }, null), CancellationToken.None);
        Assert.Equal(AgentErrorCodes.TabNotFound, unknown.ErrorCode);

        Assert.Equal(0, rig.Launches);
        Assert.Same(rig.Unbound, rig.Host.Active); // 거부하면 화면도 바꾸지 않는다
    }

    [Fact]
    public async Task Only_the_statement_range_is_accepted()
    {
        var rig = new Rig();

        Assert.Equal(AgentErrorCodes.InvalidArguments, (await rig.Call(rig.DevTab, range: "document")).ErrorCode);
        Assert.Equal(0, rig.Launches);
    }

    [Fact]
    public async Task Without_app_services_it_refuses()
    {
        var rig = new Rig();
        var handler = new TuneStartToolHandler(rig.Service) { OnUiThread = body => body(), Services = () => null, Launch = _ => throw new InvalidOperationException() };

        var output = await handler.ExecuteAsync(
            new AppToolInvocation(Grant, "Claude Code", new JsonObject { [AppFields.Tab] = rig.DevTab.TabId.ToString() }, null), CancellationToken.None);

        Assert.Equal(AgentErrorCodes.NotImplemented, output.ErrorCode);
    }
}
