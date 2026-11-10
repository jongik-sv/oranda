using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.DedicatedSession;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.ConnectionManager;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;
using Oranda.Core.Extensibility;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// 활성 SQL 탭이 접속이 닫힌 접속에 묶여 있을 때(보이는 접속 없음)의 기타 CLI 일반 대화. 「연결 없음」 과 달리 그 접속이 운영이거나 알 수 없으면 가림 대상으로 보고 보내지 않는다.
/// 실제 <see cref="SessionRouter"/>·접속 저장소·런처의 <c>GeneralContextAsync</c> 를 지난다(시험 틀이 접속 정보를 고정값으로 주지 않는다).
/// </summary>
[Collection("GridEditStatics")]
public sealed class AiCustomCliClosedBindingTests
{
    private sealed class Rig : IDisposable
    {
        private readonly SqlEditorHost _previousHost = SqlEditorHost.Current;

        public Rig()
        {
            SqlEditorHost.Current = Host;
            Fx.Sessions.PublishOpened = true;
            Fx.Container.AddInstance<IConnectionService>(Fx.Service);
            Fx.Container.AddInstance<IEditorContext>(new EditorContext { Host = Host });
            Fx.Container.AddInstance<IAiDriver>(Driver);
            Fx.Container.AddInstance<Oranda.Core.Ai.Bridge.IAgentBridgeHost>(new NoPipeHost()); // 시험마다 실제 파이프를 열지 않는다
            Router = SessionRouter.Resolve(Fx.Container);
            Registry = DedicatedSessionCommands.Resolve(Fx.Container);
            Settings = new SettingsService(new MemoryStore());
            Fx.Container.AddInstance(Settings);
            Launcher = new AiTuningLauncher(Fx.Container) { ChatOverride = () => Task.FromResult<AiChatViewModel?>(Chat) };
        }

        public ServiceFixture Fx { get; } = new();

        public RecordingDriver Driver { get; } = new();

        public SqlEditorHost Host { get; } = new();

        public SessionRouter Router { get; }

        public DedicatedSessionRegistry Registry { get; }

        public SettingsService Settings { get; }

        public AiTuningLauncher Launcher { get; }

        public AiChatViewModel Chat { get; } = new();

        /// <summary>접속 이름·환경으로 저장하고 접속해 활성 SQL 탭에 묶는다.</summary>
        public async Task<ISession> TabOnAsync(string name, ConnectionEnvironment environment)
        {
            var profile = await Fx.Service.SaveProfileAsync(new ConnectionProfile(name, "scott", "localhost:1521/FREEPDB1", Environment: environment), null, false, default);
            var outcome = await Fx.Service.ConnectAsync(profile, "pw", default);
            Assert.True(outcome.IsConnected, outcome.Message);
            Pump();
            Host.EnsureWorkspace();
            var tab = Host.NewDocument();
            tab.Document.Text = "select 1 from dual";
            Host.Active = tab;
            Assert.Equal(SessionSelect.Selected, await Router.SelectAsync(outcome.Session!));
            Pump();
            return outcome.Session!;
        }

        public async Task UseCustomCliAsync() =>
            await Settings.UpdateAsync(s => s with { Ai = AiCustomCliRig.CustomSettings() });

        /// <summary>PL/SQL 문서가 활성인 것처럼 두고 그 문서가 쓰는 세션을 정한다.</summary>
        public void ShowPlsqlDocumentOn(ISession? session)
        {
            Registry.SqlDocumentActive = () => false;
            Router.ActiveDocument = () => new FakeDocument(session);
            Registry.Refocus();
            Pump();
        }

        /// <summary>대화 창에서 글을 보내고 대화 줄에 결과(안내나 답)가 생길 때까지 기다린다.</summary>
        public async Task SendAsync(string text)
        {
            Launcher.EnsureChat(Chat);
            Chat.Draft = text;
            Chat.SendCommand.Execute(null);
            await HeadlessHost.WaitUntil(() => Driver.Last is not null || Chat.Entries.OfType<ChatAssistantEntry>().Any(e => e.Text.Length > 0));
            await Task.Delay(30);
        }

        public void Dispose()
        {
            SqlEditorHost.Current = _previousHost;
            Fx.Dispose();
        }
    }

    internal sealed class RecordingDriver : IAiDriver
    {
        public AiDriverCapabilities Capabilities { get; } = new(false, true, true, false, false, true);

        public AiRequest? Last { get; private set; }

        public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Last = request;
            yield return new AiFinal("답", null, "s");
            await Task.Yield();
        }
    }

    private sealed class NoPipeHost : Oranda.Core.Ai.Bridge.IAgentBridgeHost
    {
        public string Start() => "pipe-example";
    }

    private sealed class MemoryStore : Oranda.Core.Storage.ISettingsStore
    {
        private Oranda.Core.Settings.AppSettings _current = new();

        public Task<Oranda.Core.Storage.StoredDocument<Oranda.Core.Settings.AppSettings>> LoadAsync(CancellationToken ct) => Task.FromResult(new Oranda.Core.Storage.StoredDocument<Oranda.Core.Settings.AppSettings>(_current));

        public Task SaveAsync(Oranda.Core.Settings.AppSettings settings, CancellationToken ct)
        {
            _current = settings;
            return Task.CompletedTask;
        }
    }

    private static void Pump()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    [Fact]
    public Task A_closed_production_tab_blocks_a_custom_cli_general_chat() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        await rig.UseCustomCliAsync();
        var session = await rig.TabOnAsync("prod", ConnectionEnvironment.Production);
        await rig.Fx.Service.DisconnectAsync(session, CancellationToken.None); // 탭은 운영 접속에 묶인 채 접속만 닫혔다
        Pump();
        Assert.Equal(session.Profile.Id, rig.Router.ShownMissing);

        await rig.SendAsync("이 테이블에서 뭘 지워도 돼?");

        Assert.Null(rig.Driver.Last);
        Assert.Contains(AiTuningCustomCli.MaskingBlockedText, rig.Chat.Entries.OfType<ChatAssistantEntry>().Select(e => e.Text));
    });

    [Fact]
    public Task A_closed_tab_whose_connection_is_no_longer_stored_is_closed_too() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        await rig.UseCustomCliAsync();
        var session = await rig.TabOnAsync("gone", ConnectionEnvironment.Development);
        await rig.Fx.Service.DisconnectAsync(session, CancellationToken.None);
        await rig.Fx.Service.DeleteProfileAsync(session.Profile.Id, CancellationToken.None); // 환경을 알 수 없다
        Pump();

        await rig.SendAsync("질문");

        Assert.Null(rig.Driver.Last);
        Assert.Contains(AiTuningCustomCli.MaskingBlockedText, rig.Chat.Entries.OfType<ChatAssistantEntry>().Select(e => e.Text));
    });

    [Fact]
    public Task A_closed_development_tab_does_not_block_a_custom_cli_general_chat() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        await rig.UseCustomCliAsync();
        var session = await rig.TabOnAsync("dev", ConnectionEnvironment.Development);
        await rig.Fx.Service.DisconnectAsync(session, CancellationToken.None);
        Pump();

        await rig.SendAsync("질문");

        Assert.NotNull(rig.Driver.Last);
        Assert.False(rig.Driver.Last!.MaskingRequired);
    });

    [Fact]
    public Task A_closed_production_tab_does_not_change_what_claude_code_receives() => Ui.Run(async () =>
    {
        using var rig = new Rig(); // 기본 설정 = Claude Code
        var session = await rig.TabOnAsync("prod", ConnectionEnvironment.Production);
        await rig.Fx.Service.DisconnectAsync(session, CancellationToken.None);
        Pump();

        await rig.SendAsync("질문");

        Assert.NotNull(rig.Driver.Last); // 연결 없음 대화로 그대로 나간다(Claude 경로의 가림 동작은 바꾸지 않았다)
        Assert.Null(rig.Driver.Last!.Expected!.CustomCliCommandHash);
    });

    private sealed class FakeDocument(ISession? session) : IConnectionDocument
    {
        public ISession? Session { get; private set; } = session;

        public bool IsSessionBusy { get; set; }

        public event EventHandler? SessionChanged;

        public bool UseSession(ISession session)
        {
            Session = session;
            SessionChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public void Attach(ISession session) => UseSession(session);
    }

    [Fact]
    public Task A_plsql_document_on_a_production_session_blocks_a_custom_cli_because_the_shown_connection_follows_the_document() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        await rig.UseCustomCliAsync();
        var prod = await rig.TabOnAsync("prod", ConnectionEnvironment.Production);
        await rig.TabOnAsync("dev", ConnectionEnvironment.Development); // 지금 접속은 개발
        rig.ShowPlsqlDocumentOn(prod);

        var scope = await rig.Launcher.GeneralContextAsync(CancellationToken.None);
        Assert.Equal(ConnectionEnvironment.Production, scope.Context!.Environment); // 보이는 접속이 문서의 세션을 따라간다: 지적한 틈은 재현되지 않는다
        Assert.True(scope.Masking);
        Assert.False(scope.UnsafeBinding);

        await rig.SendAsync("이 패키지 설명해 줘");

        Assert.Null(rig.Driver.Last);
        Assert.Contains(AiTuningCustomCli.MaskingBlockedText, rig.Chat.Entries.OfType<ChatAssistantEntry>().Select(e => e.Text));
    });

    [Fact]
    public Task A_plsql_document_on_the_shown_development_session_does_not_block() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        await rig.UseCustomCliAsync();
        var dev = await rig.TabOnAsync("dev", ConnectionEnvironment.Development);
        rig.ShowPlsqlDocumentOn(dev);

        var scope = await rig.Launcher.GeneralContextAsync(CancellationToken.None);

        Assert.False(scope.UnsafeBinding);
    });

    [Fact]
    public Task The_tuning_command_on_a_custom_cli_explains_before_the_start_dialog_and_the_chat_input_shows_an_unusable_row() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        await rig.UseCustomCliAsync();
        await rig.TabOnAsync("dev", ConnectionEnvironment.Development);

        await rig.Launcher.RunAsync(CancellationToken.None);

        Assert.Equal(AiTuningCustomCli.TuningText, Assert.Single(rig.Chat.Entries.OfType<ChatAssistantEntry>()).Text);
        Assert.False(rig.Chat.IsTuning);

        rig.Launcher.EnsureChat(rig.Chat);
        Assert.Null(rig.Chat.UnavailableReason); // 실행할 수 있는 줄이면 입력은 그대로 열려 있다
        await rig.Settings.UpdateAsync(s => s with { Ai = new Oranda.Core.Settings.AiSettings { SystemCliId = "custom:gone" } });
        Assert.Equal(AiTuningCustomCli.UnavailableText, rig.Chat.UnavailableReason);
    });

    [Fact]
    public Task A_plsql_document_still_waiting_for_its_session_blocks_a_custom_cli() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        await rig.UseCustomCliAsync();
        await rig.TabOnAsync("dev", ConnectionEnvironment.Development);
        rig.ShowPlsqlDocumentOn(null); // 복원된 문서: 접속을 기다리는 중

        var scope = await rig.Launcher.GeneralContextAsync(CancellationToken.None);

        Assert.True(scope.UnsafeBinding);
    });
}
