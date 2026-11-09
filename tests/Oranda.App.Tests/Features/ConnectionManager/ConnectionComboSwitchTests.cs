using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.DedicatedSession;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Features;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.ConnectionManager;

// 소유: TSK-12-04. 툴바 접속 콤보로 고른 접속이 활성 문서(SQL 탭·PL/SQL 문서)의 다음 실행 대상이 되는지,
// 이전 접속의 미커밋은 묻고(커밋/롤백/그대로 두고 바꾸기/취소) 실행 중이면 바꾸지 않는지.
[Collection("DbmsFeatureKeysSerial")] // DBMS 키 시험이 쓰는 정적 상태와 겹치지 않게
public sealed class ConnectionComboSwitchTests
{
    private static ConnectionProfile Profile(string name) => new(name, "scott", "localhost:1521/FREEPDB1");

    private static async Task<ISession> ConnectAsync(ServiceFixture fx, string name)
    {
        var profile = await fx.Service.SaveProfileAsync(Profile(name), "pw", true, CancellationToken.None);
        var outcome = await fx.Service.ConnectAsync(profile, null, CancellationToken.None);
        Assert.True(outcome.IsConnected, outcome.Message);
        fx.Events.Publish(new SessionOpened(outcome.Session!.Id, outcome.Session.Profile, outcome.Session.Server, SessionPurpose.Work));
        Pump();
        return outcome.Session;
    }

    private static void Pump()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed class RouterRig : IDisposable
    {
        public RouterRig()
        {
            State = new ExecutionState(Fx.Events);
            Registry = new DedicatedSessionRegistry(Host, State, Fx.Events);
            Router = new SessionRouter(Fx.Service, Host, State, Registry);
        }

        public ServiceFixture Fx { get; } = new();

        public SqlEditorHost Host { get; } = new();

        public ExecutionState State { get; }

        public DedicatedSessionRegistry Registry { get; }

        public SessionRouter Router { get; }

        public List<ISession> Asked { get; } = [];

        /// <summary>미커밋 확인의 답(true = 바꿔도 된다)을 정하고 물은 이전 접속을 모은다.</summary>
        public void Answer(bool proceed) => Router.ConfirmSwitch = (session, _) =>
        {
            Asked.Add(session);
            return Task.FromResult(proceed);
        };

        /// <summary>PL/SQL 문서가 활성인 것처럼 둔다.</summary>
        public void ShowDocument(object? panel)
        {
            Registry.SqlDocumentActive = () => panel is null;
            Router.ActiveDocument = () => panel;
            Registry.Refocus();
        }

        public void Dispose() => Fx.Dispose();
    }

    /// <summary>PL/SQL 편집기 대역.</summary>
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

    /// <summary>요청을 모으기만 하는 AI 드라이버 대역.</summary>
    private sealed class RecordingAiDriver : Oranda.Core.Ai.Contracts.IAiDriver
    {
        public List<Oranda.Core.Ai.Contracts.AiRequest> Requests { get; } = [];

        public Oranda.Core.Ai.Contracts.AiDriverCapabilities Capabilities { get; } = new(false, true, true, false, false, true);

        public async IAsyncEnumerable<Oranda.Core.Ai.Contracts.AiEvent> RunAsync(Oranda.Core.Ai.Contracts.AiRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            yield return new Oranda.Core.Ai.Contracts.AiFinal("답", null, "sess-1");
            await Task.Yield();
        }
    }

    [Fact]
    public Task General_chat_does_not_send_the_last_sql_tab_statement_while_a_plsql_document_of_another_connection_is_active() => Ui.Run(async () =>
    {
        // 실제 SessionRouter·EditorContext: 활성 문서가 접속 b 의 PL/SQL 편집기인데 EditorContext 는 마지막 SQL 탭(접속 a)의 문장을 돌려준다.
        using var rig = new RouterRig();
        var a = await ConnectAsync(rig.Fx, "a");
        var tab = rig.Host.NewDocument();
        var b = await ConnectAsync(rig.Fx, "b");
        rig.Answer(true);
        Assert.Equal(SessionSelect.Selected, await rig.Router.SelectAsync(a));
        tab.Document.Text = "select 'TAB-LITERAL' from t";
        var editor = new EditorContext { Host = rig.Host };
        var driver = new RecordingAiDriver();
        var chat = new Oranda.App.Features.Ai.Chat.AiChatViewModel();
        var flow = new Oranda.App.Features.Ai.Tuning.AiTuningFlow(
            editor, driver, chat, (_, _, _) => { },
            generalContext: _ =>
            {
                var shown = rig.State.ShownProfile!;
                return Task.FromResult(new Oranda.App.Features.Ai.Tuning.AiGeneralScope(
                    new Oranda.Core.Ai.Contracts.AiContext(shown.Name, shown.Environment, Oranda.Core.Ai.Access.AiAccessLevel.Query), false, shown.Id, rig.Router.SqlDocumentActive));
            });

        // 대조: SQL 탭이 활성이고 탭의 접속이 지금 접속이면 문장이 범위가 된다.
        rig.ShowDocument(null);
        await flow.FollowUpAsync(new Oranda.App.Features.Ai.Chat.ChatFollowUp("질문", []));
        Assert.Contains("TAB-LITERAL", driver.Requests[^1].Prompt, System.StringComparison.Ordinal);
        chat.StartNewConversation();

        // PL/SQL 문서(접속 b)가 활성: 마지막 SQL 탭의 문장(접속 a 의 리터럴)을 싣지 않고 도구도 닫는다.
        rig.ShowDocument(new FakeDocument(b));
        Assert.False(rig.Router.SqlDocumentActive);
        Assert.Equal(b.Profile.Id, rig.State.ShownProfile!.Id);
        await flow.FollowUpAsync(new Oranda.App.Features.Ai.Chat.ChatFollowUp("질문", []));

        var request = driver.Requests[^1];
        Assert.DoesNotContain("TAB-LITERAL", request.Prompt, System.StringComparison.Ordinal);
        Assert.Equal(Oranda.Core.Ai.Access.AiAccessLevel.Off, request.Session!.Level);
        Assert.Contains(chat.Entries.OfType<Oranda.App.Features.Ai.Chat.ChatAssistantEntry>(), e => e.Text == Oranda.App.Features.Ai.Tuning.AiTuningFlow.GeneralOtherDocumentText);
    });

    // ---------- SQL 탭 ----------

    [Fact]
    public Task Choosing_another_connection_moves_the_tab_and_the_current_connection() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var a = await ConnectAsync(rig.Fx, "a");
        var tab = rig.Host.NewDocument();
        var b = await ConnectAsync(rig.Fx, "b");
        rig.Answer(true);

        Assert.Equal(SessionSelect.Selected, await rig.Router.SelectAsync(a));
        Assert.Same(a, rig.Router.SessionFor(tab));
        Assert.Same(a, rig.Fx.Service.ActiveSession);
        Assert.Same(a, rig.Router.ShownSession);
        Assert.Empty(rig.Asked); // 미커밋이 없으면 묻지 않는다

        Assert.Equal(SessionSelect.Selected, await rig.Router.SelectAsync(b));
        Assert.Same(b, rig.Router.SessionFor(tab));
        var fresh = rig.Host.NewDocument(); // 새 탭의 기본 접속 = 고른 것
        Assert.Same(b, rig.Router.SessionFor(fresh));
    });

    [Fact]
    public Task An_uncommitted_previous_connection_is_asked_and_cancel_keeps_the_tab() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var a = await ConnectAsync(rig.Fx, "a");
        var tab = rig.Host.NewDocument();
        rig.State.ApplyTransaction(a.Id, true);
        var b = await ConnectAsync(rig.Fx, "b"); // 미커밋 탭은 새 접속을 따르지 않는다
        Assert.Same(a, rig.Router.SessionFor(tab));

        rig.Answer(false);
        Assert.Equal(SessionSelect.Cancelled, await rig.Router.SelectAsync(b));
        Assert.Equal([a], rig.Asked);
        Assert.Same(a, rig.Router.SessionFor(tab));
        Assert.Same(a, rig.Fx.Service.ActiveSession);
        Assert.Null(SessionRouter.RefusalText(SessionSelect.Cancelled)); // 사용자가 취소했으니 메시지 없음

        rig.Answer(true); // 커밋·롤백했거나 그대로 두고 바꾸기
        Assert.Equal(SessionSelect.Selected, await rig.Router.SelectAsync(b));
        Assert.Same(b, rig.Router.SessionFor(tab));
        Assert.Same(b, rig.Fx.Service.ActiveSession);
        Assert.True(rig.State.IsTxnPending(a.Id)); // 그대로 둔 트랜잭션은 a 에 남는다(종료 때 다시 묻는다)
    });

    [Fact]
    public Task Without_a_confirm_hook_an_uncommitted_tab_is_refused_as_before() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var a = await ConnectAsync(rig.Fx, "a");
        var tab = rig.Host.NewDocument();
        rig.State.ApplyTransaction(a.Id, true);
        var b = await ConnectAsync(rig.Fx, "b");

        Assert.Equal(SessionSelect.Held, await rig.Router.SelectAsync(b));
        Assert.Same(a, rig.Router.SessionFor(tab));
    });

    [Fact]
    public Task A_running_tab_is_not_switched_and_nothing_is_asked() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var a = await ConnectAsync(rig.Fx, "a");
        var tab = rig.Host.NewDocument();
        var b = await ConnectAsync(rig.Fx, "b");
        tab.ConnectionProfileId = a.Profile.Id;
        rig.State.BeginRun(a.Id);
        rig.Answer(true);

        Assert.Equal(SessionSelect.Busy, await rig.Router.SelectAsync(b));
        Assert.Empty(rig.Asked);
        Assert.Same(a, rig.Router.SessionFor(tab));
        Assert.Contains("실행", SessionRouter.RefusalText(SessionSelect.Busy), StringComparison.Ordinal);

        rig.State.EndRun(a.Id); // 끝나면 바꿀 수 있다
        Assert.Equal(SessionSelect.Selected, await rig.Router.SelectAsync(b));
        Assert.Same(b, rig.Router.SessionFor(tab));
    });

    [Fact]
    public Task A_tab_sharing_its_uncommitted_connection_with_another_tab_switches_without_asking() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var a = await ConnectAsync(rig.Fx, "a");
        var first = rig.Host.NewDocument();
        var second = rig.Host.NewDocument();
        var b = await ConnectAsync(rig.Fx, "b");
        first.ConnectionProfileId = a.Profile.Id;
        second.ConnectionProfileId = a.Profile.Id;
        rig.State.ApplyTransaction(a.Id, true);
        rig.Answer(false);

        Assert.Equal(SessionSelect.Selected, await rig.Router.SelectAsync(b)); // 미커밋은 first 가 계속 본다
        Assert.Empty(rig.Asked);
        Assert.Same(b, rig.Router.SessionFor(second));
        Assert.Same(a, rig.Router.SessionFor(first));
    });

    [Fact]
    public Task Choosing_a_closed_connection_or_on_a_dedicated_tab_is_refused() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var a = await ConnectAsync(rig.Fx, "a");
        var tab = rig.Host.NewDocument();

        Assert.Equal(SessionSelect.Closed, await rig.Router.SelectAsync(new FakeSession(Profile("gone"))));
        Assert.Equal(a.Profile.Id, tab.ConnectionProfileId);

        var b = await ConnectAsync(rig.Fx, "b");
        rig.Registry.Attach(tab, new FakeSession(b.Profile));
        Assert.Equal(SessionSelect.Dedicated, await rig.Router.SelectAsync(a));
    });

    private sealed class SelectorProvider(IDbmsServiceSelector selector) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IDbmsServiceSelector) ? selector : null;
    }

    [Fact]
    public Task Dbms_keys_follow_the_shown_tab_session_and_not_the_current_connection() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var keys = new ContextKeyService();
        ConnectionManagerCommands.WatchDbmsKeys(rig.Router, keys, new SelectorProvider(new DbmsServiceTable())); // Tibero 는 어느 영역도 구현이 없다
        var current = await ConnectAsync(rig.Fx, "a"); // 지금 접속 = Oracle
        var oracleTab = rig.Host.NewDocument();
        var tiberoTab = rig.Host.NewDocument();
        Assert.Same(tiberoTab, rig.Host.Active);
        var monitorOn = DbmsServiceKeys.Enabled(DbmsServiceArea.Monitor);
        var planOn = DbmsServiceKeys.Enabled(DbmsServiceArea.ExecutionPlan);
        var noDisconnectOff = "!" + Oranda.App.Features.SessionMonitor.SessionMonitorCommands.NoDisconnectKey;
        try
        {
            Assert.True(keys.Evaluate(monitorOn)); // Oracle 탭

            rig.Registry.Attach(tiberoTab, new FakeSession(new ConnectionProfile("tb", "SYS", "h:8629/s", Dbms: Dbms.Tibero)));
            Pump();
            Assert.Same(current, rig.Fx.Service.ActiveSession); // 전용 세션 탭은 지금 접속을 바꾸지 않는다
            Assert.False(keys.Evaluate(monitorOn)); // 그래도 보이는 탭이 Tibero 라서 영역 키가 Tibero 기준
            Assert.False(keys.Evaluate(planOn));
            Assert.False(keys.Evaluate(noDisconnectOff)); // DISCONNECT 문장이 카탈로그에 없는 DBMS 는 연결 끊기 메뉴가 꺼진다(P6)

            rig.Host.Active = oracleTab; // 활성 탭이 바뀌면 키도 따라간다(지금 접속은 처음부터 Oracle)
            Pump();
            Assert.True(keys.Evaluate(monitorOn));
            Assert.True(keys.Evaluate(planOn));
            Assert.True(keys.Evaluate(noDisconnectOff)); // Oracle 은 그대로 켜짐

            rig.Host.Active = tiberoTab;
            Pump();
            Assert.False(keys.Evaluate(monitorOn));
        }
        finally
        {
            DbmsServiceKeys.Apply(keys, null, null);
            DbmsFeatureKeys.Apply(keys, null);
        }
    });

    // ---------- PL/SQL 문서 ----------

    [Fact]
    public Task A_plsql_document_shows_its_own_connection_and_the_combo_changes_it() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var a = await ConnectAsync(rig.Fx, "a");
        var tab = rig.Host.NewDocument();
        var b = await ConnectAsync(rig.Fx, "b");
        var doc = new FakeDocument(a);
        var changed = 0;
        rig.Router.Changed += (_, _) => changed++;

        rig.ShowDocument(doc);
        Assert.Same(doc, rig.Router.ConnectionDocument);
        Assert.Same(a, rig.Router.ShownSession); // 지금 접속(b)이 아니라 문서의 접속
        Assert.Same(a, rig.Fx.Service.ActiveSession); // 스키마 브라우저도 문서의 접속을 본다

        Assert.Equal(SessionSelect.Selected, await rig.Router.SelectAsync(b));
        Assert.Same(b, doc.Session); // 다음 컴파일·실행은 b
        Assert.Same(b, rig.Router.ShownSession);
        Assert.Same(b, rig.Fx.Service.ActiveSession);
        Assert.Same(b, rig.Router.SessionFor(tab)); // SQL 탭은 그대로(b 로 묶여 있었다)

        // 문서 쪽에서 접속이 바뀌어도(복원 탭 붙이기) 표시가 따른다.
        changed = 0;
        doc.Attach(a);
        Assert.True(changed > 0);
        Assert.Same(a, rig.Router.ShownSession);

        // SQL 작업 공간으로 돌아가면 탭의 접속을 보인다.
        rig.ShowDocument(null);
        Assert.Null(rig.Router.ConnectionDocument);
        Assert.Same(b, rig.Router.ShownSession);
        Assert.Same(b, rig.Fx.Service.ActiveSession);
    });

    [Fact]
    public Task A_compiling_plsql_document_is_not_switched_and_one_without_a_session_shows_the_current_connection() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var a = await ConnectAsync(rig.Fx, "a");
        var b = await ConnectAsync(rig.Fx, "b");
        var doc = new FakeDocument(a) { IsSessionBusy = true };
        rig.ShowDocument(doc);

        Assert.Equal(SessionSelect.Busy, await rig.Router.SelectAsync(b));
        Assert.Same(a, doc.Session);

        var waiting = new FakeDocument(null); // 복원한 탭이 제 접속을 기다린다
        rig.ShowDocument(waiting);
        Assert.Same(rig.Fx.Service.ActiveSession, rig.Router.ShownSession);
        Assert.Equal(SessionSelect.Selected, await rig.Router.SelectAsync(b));
        Assert.Same(b, waiting.Session);
    });

    // ---------- 콤보 ----------

    [Fact]
    public Task Combo_choice_follows_the_router_and_reverts_when_cancelled() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var a = await ConnectAsync(rig.Fx, "a");
        var tab = rig.Host.NewDocument();
        rig.State.ApplyTransaction(a.Id, true);
        var b = await ConnectAsync(rig.Fx, "b");
        var combo = new ConnectionComboViewModel(rig.Fx.Events, router: () => rig.Router);
        rig.Answer(false);

        Assert.Equal(SessionSelect.Cancelled, await combo.ChooseAsync(combo.Items.Single(i => ReferenceEquals(i.Session, b))));
        Assert.Same(a, combo.Selected?.Session); // 원래 접속으로 되돌아간다

        rig.Answer(true);
        Assert.Equal(SessionSelect.Selected, await combo.ChooseAsync(combo.Items.Single(i => ReferenceEquals(i.Session, b))));
        Assert.Same(b, combo.Selected?.Session);
        Assert.Same(b, rig.Router.SessionFor(tab));
    });

    // ---------- 미커밋 묻기(실행 컨트롤러) ----------

    [Theory]
    [InlineData(UncommittedChoice.Keep, true, true)]
    [InlineData(UncommittedChoice.Rollback, true, false)]
    [InlineData(UncommittedChoice.Commit, true, false)]
    [InlineData(UncommittedChoice.Cancel, false, true)]
    public async Task Confirm_switch_asks_with_the_previous_connection_name(UncommittedChoice answer, bool proceeds, bool stillPending)
    {
        var a = new FakeSession(Profile("a")) { HasPendingTransaction = true };
        var dialogs = new FakeDialogs { UncommittedAnswer = answer };
        var state = new ExecutionState();
        var controller = new ExecutionController(new ExecutionDeps
        {
            Host = () => new SqlEditorHost(),
            State = state,
            Log = new ExecutionLog(null, state),
            Dialogs = dialogs,
            Session = () => a,
            Execution = () => throw new NotSupportedException(),
            Language = () => throw new NotSupportedException(),
        });

        Assert.Equal(proceeds, await controller.ConfirmSwitchAsync(a));
        Assert.Equal(["scott@a"], dialogs.SwitchAsked);
        Assert.Equal(stillPending, a.HasPendingTransaction);

        var clean = new FakeSession(Profile("clean"));
        Assert.True(await controller.ConfirmSwitchAsync(clean)); // 미커밋이 없으면 묻지 않는다
        Assert.Single(dialogs.SwitchAsked);
    }

    [Fact]
    public Task Switch_dialog_has_keep_and_switch_only_when_asked_for_a_switch() => Ui.Run(() =>
    {
        UncommittedChoice? chosen = null;
        var plain = AvaloniaExecutionDialogs.CreateUncommittedDialog(null, c => chosen = c, "1 - scott@a");
        var swap = AvaloniaExecutionDialogs.CreateUncommittedDialog(null, c => chosen = c, "1 - scott@a", keep: true);

        static List<Avalonia.Controls.Button> Buttons(Avalonia.Controls.Window w) =>
            [.. Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(w).OfType<Avalonia.Controls.Button>()];
        static string Text(Avalonia.Controls.Window w) =>
            string.Join(" ", Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(w).OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text));

        Assert.Equal(["커밋", "롤백", "취소"], Buttons(plain).Select(b => b.Content as string));
        Assert.Equal(["커밋", "롤백", "그대로 두고 바꾸기", "취소"], Buttons(swap).Select(b => b.Content as string));
        Assert.Contains("이전 접속 「1 - scott@a」", Text(swap), StringComparison.Ordinal);

        swap.Show();
        Shots(swap, "switch-uncommitted");
        Buttons(swap)[2].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal(UncommittedChoice.Keep, chosen);
    });

    /// <summary><c>ORANDA_RENDER_DIR</c> 이 있으면 라이트·다크 PNG(<c>combo-*.png</c>)를 남긴다(화면 확인용).</summary>
    private static void Shots(Avalonia.Controls.Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR") is not { Length: > 0 } dir)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (variant, tone) in new[] { (Avalonia.Styling.ThemeVariant.Light, "light"), (Avalonia.Styling.ThemeVariant.Dark, "dark") })
            {
                Avalonia.Application.Current!.RequestedThemeVariant = variant;
                Pump();
                window.UpdateLayout();
                var size = new Avalonia.PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height));
                using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"combo-{name}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
            Pump();
        }
    }
}
