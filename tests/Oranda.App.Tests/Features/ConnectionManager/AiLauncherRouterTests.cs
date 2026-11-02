using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.DedicatedSession;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Composition;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;
using Oranda.Core.Extensibility;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.ConnectionManager;

/// <summary>
/// AI 런처가 실제 <see cref="SessionRouter"/>(컨테이너에서 찾는 경로 그대로)를 지나 활성 문서 종류를 읽는지: 일반 대화의 접속 정보(<c>GeneralContextAsync</c>)와
/// 튜닝 시작 경로. 라우터를 건너뛰는 시험용 주입(<c>SqlDocumentActiveProvider</c>)은 쓰지 않는다.
/// </summary>
[Collection("GridEditStatics")]
public sealed class AiLauncherRouterTests
{
    private sealed class Rig : IDisposable
    {
        private readonly SqlEditorHost _previousHost = SqlEditorHost.Current;

        public Rig(bool withRouter = true)
        {
            SqlEditorHost.Current = Host;
            Fx.Sessions.PublishOpened = true;
            Fx.Container.AddInstance<IConnectionService>(Fx.Service);
            Fx.Container.AddInstance<IEditorContext>(new EditorContext { Host = Host });
            Fx.Container.AddInstance<IAiDriver>(new IdleDriver());
            if (withRouter)
            {
                Router = SessionRouter.Resolve(Fx.Container);
                Registry = DedicatedSessionCommands.Resolve(Fx.Container);
            }

            Launcher = new AiTuningLauncher(Fx.Container) { ChatOverride = () => Task.FromResult<AiChatViewModel?>(Chat) };
        }

        public ServiceFixture Fx { get; } = new();

        public SqlEditorHost Host { get; } = new();

        public SessionRouter? Router { get; }

        public DedicatedSessionRegistry? Registry { get; }

        public AiTuningLauncher Launcher { get; }

        public AiChatViewModel Chat { get; } = new();

        /// <summary>접속 a 로 열린 SQL 탭 하나(문장에 리터럴)를 활성으로 두고 보이는 접속을 a 로 맞춘다.</summary>
        public async Task<ISession> SqlTabOnAsync(string name)
        {
            var profile = await Fx.Service.SaveProfileAsync(new ConnectionProfile(name, "scott", "localhost:1521/FREEPDB1"), null, false, default);
            var outcome = await Fx.Service.ConnectAsync(profile, "pw", default);
            Assert.True(outcome.IsConnected, outcome.Message);
            Pump();
            Host.EnsureWorkspace();
            var tab = Host.NewDocument();
            tab.Document.Text = "select 'TAB-LITERAL' from t";
            Host.Active = tab;
            Assert.Equal(SessionSelect.Selected, await Router!.SelectAsync(outcome.Session!));
            Pump();
            return outcome.Session!;
        }

        /// <summary>PL/SQL 문서가 활성인 것처럼 둔다(null 이면 SQL 탭이 활성).</summary>
        public void ShowDocument(object? panel)
        {
            Registry!.SqlDocumentActive = () => panel is null;
            Router!.ActiveDocument = () => panel;
            Registry.Refocus();
            Pump();
        }

        public void Dispose()
        {
            SqlEditorHost.Current = _previousHost;
            Fx.Dispose();
        }
    }

    private sealed class IdleDriver : IAiDriver
    {
        public AiDriverCapabilities Capabilities { get; } = new(false, true, true, false, false, true);

        public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new AiFinal("답", null, "s");
            await Task.Yield();
        }
    }

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

    private static void Pump()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    [Fact]
    public Task The_general_scope_reads_the_real_router_for_an_active_sql_tab_and_for_a_plsql_document() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.SqlTabOnAsync("a");

        var onSqlTab = await rig.Launcher.GeneralContextAsync(CancellationToken.None);
        Assert.True(rig.Router!.SqlDocumentActive);
        Assert.True(onSqlTab.SqlDocumentActive); // 라우터가 참이면 참
        Assert.Equal(a.Profile.Id, onSqlTab.ConnectionId);

        rig.ShowDocument(new FakeDocument(a));
        var onPlsql = await rig.Launcher.GeneralContextAsync(CancellationToken.None);
        Assert.False(rig.Router.SqlDocumentActive);
        Assert.False(onPlsql.SqlDocumentActive); // 라우터가 거짓이면 거짓(항상 참·참 고정이 아니다)
    });

    [Fact]
    public Task Without_a_router_in_the_container_the_scope_cannot_tell_and_stays_unknown() => Ui.Run(async () =>
    {
        using var rig = new Rig(withRouter: false);
        var profile = await rig.Fx.Service.SaveProfileAsync(new ConnectionProfile("a", "scott", "localhost:1521/FREEPDB1"), null, false, default);
        var outcome = await rig.Fx.Service.ConnectAsync(profile, "pw", default);
        Assert.True(outcome.IsConnected, outcome.Message);
        Pump();
        ExecutionState.For(rig.Fx.Events).FocusPending(outcome.Session!.Profile); // 보이는 접속은 있으나 라우터가 없다

        var scope = await rig.Launcher.GeneralContextAsync(CancellationToken.None);

        Assert.NotNull(scope.Context);
        Assert.Null(scope.SqlDocumentActive); // 판단 불가: ?? true 로 열리지 않는다
    });

    [Fact]
    public Task A_tuning_start_with_a_plsql_document_active_is_refused_with_a_notice_before_any_start_window() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.SqlTabOnAsync("a");
        rig.ShowDocument(new FakeDocument(a)); // 활성 문서는 PL/SQL, 마지막 SQL 탭에는 리터럴 문장

        await rig.Launcher.RunAsync(CancellationToken.None);

        var notice = Assert.Single(rig.Chat.Entries.OfType<ChatAssistantEntry>());
        Assert.Equal(AiTuningLauncher.OtherDocumentText, notice.Text);
        Assert.False(rig.Chat.IsTuning); // 튜닝이 시작되지 않았다
    });

    [Fact]
    public Task A_tuning_start_on_an_sql_tab_of_the_shown_connection_passes_the_check() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        await rig.SqlTabOnAsync("a");

        await rig.Launcher.RunAsync(CancellationToken.None); // 주 창이 없어 시작 창 없이 끝난다

        Assert.Empty(rig.Chat.Entries.OfType<ChatAssistantEntry>()); // 판단에서 막히지 않았다
    });

    [Fact]
    public Task A_tuning_start_is_refused_when_the_tab_belongs_to_another_connection() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.SqlTabOnAsync("a");
        var other = await rig.Fx.Service.SaveProfileAsync(new ConnectionProfile("b", "scott", "localhost:1521/FREEPDB1"), null, false, default);
        var b = await rig.Fx.Service.ConnectAsync(other, "pw", default);
        Assert.True(b.IsConnected, b.Message);
        Pump();
        Assert.Equal(SessionSelect.Selected, await rig.Router!.SelectAsync(b.Session!)); // 탭은 접속 b 로 옮겨진다
        Assert.Equal(b.Session!.Profile.Id, rig.Host.Active!.ConnectionProfileId);
        ExecutionState.For(rig.Fx.Events).FocusPending(a.Profile); // 보이는 접속은 a: 탭(b)과 어긋난다
        Assert.Equal(a.Profile.Id, ExecutionState.For(rig.Fx.Events).ShownProfile!.Id);

        await rig.Launcher.RunAsync(CancellationToken.None);

        Assert.Equal(AiTuningLauncher.OtherDocumentText, Assert.Single(rig.Chat.Entries.OfType<ChatAssistantEntry>()).Text);
    });
}
