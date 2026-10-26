using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.DedicatedSession;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.ConnectionManager;

/// <summary>
/// 소유: TSK-14-03. 같은 DB 여러 세션의 화면: 툴바 콤보(더 연 세션·「새 세션」), 상태 표시줄 세션 범위 글, 함께 쓰는 세션이 다른 워크스페이스에서
/// 실행 중일 때의 안내 막대(D-036)와 실행 가로채기. <c>ORANDA_RENDER_DIR</c> 가 있으면 라이트·다크 PNG(<c>multisession-*.png</c>)를 그 폴더에 쓴다.
/// </summary>
public sealed class MultiSessionUiTests
{
    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Fx.Sessions.PublishOpened = true;
            State = ExecutionState.For(Fx.Events); // 상태 표시줄 구간 ViewModel 이 같은 버스로 같은 상태를 읽는다
            Registry = new DedicatedSessionRegistry(Host, State, Fx.Events);
            Router = new SessionRouter(Fx.Service, Host, State, Registry);
        }

        public ServiceFixture Fx { get; } = new();

        public SqlEditorHost Host { get; } = new();

        public ExecutionState State { get; }

        public DedicatedSessionRegistry Registry { get; }

        public SessionRouter Router { get; }

        public IConnectionService Service => Fx.Service;

        public async Task<ISession> ConnectAsync(string name = "a")
        {
            var profile = await Fx.Service.SaveProfileAsync(new ConnectionProfile(name, "scott", "localhost:1521/FREEPDB1"), null, false, default);
            var outcome = await Fx.Service.ConnectAsync(profile, "pw-" + name, default);
            Assert.True(outcome.IsConnected, outcome.Message);
            Pump();
            return outcome.Session!;
        }

        /// <summary>작업 공간 둘(A = 기본 작업 공간의 탭, B = 새 작업 공간의 첫 탭). 이름을 붙여 둔다(안내 막대 글).</summary>
        public (SqlWorkspaceViewModel A, SqlEditorViewModel TabA, SqlWorkspaceViewModel B, SqlEditorViewModel TabB) TwoWorkspaces()
        {
            var a = Host.EnsureWorkspace();
            var tabA = Host.NewDocument();
            var b = Host.NewWorkspace();
            a.Name = "월말 정산";
            b.Name = "재고 점검";
            return (a, tabA, b, b.Tabs[0]);
        }

        public ExecutionController Controller() => new(new ExecutionDeps
        {
            Host = () => Host,
            State = State,
            Log = new ExecutionLog(null, State),
            Dialogs = new FakeDialogs(),
            Session = () => Router.Current,
            TabSession = Router.SessionFor,
            MissingConnection = Router.MissingProfileOf,
            PrepareSession = Router.PrepareAsync,
            SharedBusy = Router.OfferNewSession,
            Execution = () => throw new NotSupportedException(),
            Language = () => throw new NotSupportedException(),
        });

        public void Dispose() => Fx.Dispose();
    }

    private static void Pump()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    // ---------- 툴바 콤보 ----------

    [Fact]
    public Task Combo_lists_extra_sessions_of_a_connection_and_a_new_session_row() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, _, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        var extra = rig.Router.SessionFor(tabB)!;
        var combo = new ConnectionComboViewModel(rig.Fx.Events, router: () => rig.Router);

        rig.Host.Active = tabB;
        Pump();
        combo.Refresh();

        Assert.Equal(["1 - scott@a", "1 - scott@a (2)"], combo.Items.Select(i => i.Label));
        Assert.Same(extra, combo.Selected?.Session); // 「전용 세션 · …」 빈 글이 아니라 그 세션 줄을 고른다
        Assert.Equal("접속 안 됨", combo.PlaceholderText);
        var last = combo.Entries[^1];
        Assert.True(last.IsNewSession);
        Assert.Equal("새 세션 — scott@a (3)", last.Label);
        Assert.True(last.Enabled);
        Assert.Equal(3, combo.Entries.Count);
        Assert.StartsWith("세션 SID ", combo.Items[1].Tip, StringComparison.Ordinal);
        Assert.Contains("재고 점검", combo.Items[1].Tip, StringComparison.Ordinal);
        Assert.Same(a, combo.Items[0].Session);
    });

    [Fact]
    public Task Choosing_the_new_session_row_opens_a_session_for_the_workspace_and_selects_it() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (wsA, tabA, _, _) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        rig.Host.Active = tabA;
        Pump();
        var combo = new ConnectionComboViewModel(rig.Fx.Events, router: () => rig.Router);

        Assert.Equal(SessionSelect.Selected, await combo.ChooseAsync(combo.Entries[^1]));
        Pump();

        var opened = rig.Router.SessionFor(tabA)!;
        Assert.NotSame(a, opened);
        Assert.Equal(WorkspaceSessionMode.Own, wsA.SessionMode);
        Assert.Same(opened, combo.Selected?.Session); // 「새 세션」 줄이 고른 채로 남지 않는다
        Assert.False(combo.Selected!.IsNewSession);
        Assert.Equal(["1 - scott@a", "1 - scott@a (2)", "새 세션 — scott@a (3)"], combo.Entries.Select(i => i.Label));
    });

    [Fact]
    public Task A_plsql_document_sees_only_connections_and_no_new_session_row() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, _, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        var combo = new ConnectionComboViewModel(rig.Fx.Events, router: () => rig.Router);

        rig.Registry.SqlDocumentActive = () => false; // PL/SQL 편집기 같은 다른 문서가 활성
        combo.Refresh();

        Assert.Equal(["1 - scott@a"], combo.Entries.Select(i => i.Label));
        Assert.Same(a, combo.Items.Single().Session);
    });

    [Fact]
    public Task The_new_session_row_is_dimmed_at_the_limit_and_opens_nothing() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, _, _) = rig.TwoWorkspaces();
        for (var i = 1; i < ConnectionService.MaxSessionsPerProfile; i++)
        {
            Assert.True((await rig.Service.OpenSessionAsync(a.Profile.Id, default)).IsConnected);
        }

        rig.Host.Active = tabA;
        Pump();
        var combo = new ConnectionComboViewModel(rig.Fx.Events, router: () => rig.Router);
        var row = combo.Entries[^1];

        Assert.True(row.IsNewSession);
        Assert.False(row.Enabled);
        Assert.Contains("8", row.Tip, StringComparison.Ordinal);
        Assert.Equal(SessionSelect.Cancelled, await combo.ChooseAsync(row));
        Assert.Equal(ConnectionService.MaxSessionsPerProfile, rig.Service.SessionsOf(a.Profile.Id).Count);
    });

    // ---------- 상태 표시줄 ----------

    [Fact]
    public Task Status_text_names_the_workspace_session_shared_session_and_pending_new_session() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        await rig.ConnectAsync();
        var (_, tabA, _, tabB) = rig.TwoWorkspaces();
        rig.Host.Active = tabA;
        Pump();
        Assert.Equal("연결됨", rig.State.ShownSessionText); // 작업 공간 하나·세션 하나면 WP-14 전과 같다

        await rig.Router.PrepareAsync(tabA);
        Assert.Equal("연결됨", rig.State.ShownSessionText);

        rig.Host.Active = tabB;
        Pump();
        Assert.Equal("새 세션 · 첫 실행 때 연결", rig.State.ShownSessionText); // 아직 세션이 없다(「연결됨」 이 아니다)

        await rig.Router.PrepareAsync(tabB);
        Pump();
        Assert.Equal("워크스페이스 세션 · 연결됨", rig.State.ShownSessionText);

        var wsC = rig.Host.NewWorkspace();
        rig.Host.Active = wsC.Tabs[0];
        Pump();
        await rig.Router.SelectAsync(rig.Router.SessionFor(tabB)!); // C 가 B 의 세션을 함께 쓴다
        Pump();
        Assert.Equal("공유 세션 · 다른 워크스페이스 1개와 함께 · 연결됨", rig.State.ShownSessionText);

        rig.Host.Active = tabB;
        Pump();
        Assert.Equal("공유 세션 · 다른 워크스페이스 1개와 함께 · 연결됨", rig.State.ShownSessionText);
        Assert.Equal(2, rig.Router.WorkspacesOn(rig.Router.SessionFor(tabB)!).Count);
    });

    // ---------- 함께 쓰는 세션이 바쁠 때: 실행 가로채기·안내 막대 ----------

    [Fact]
    public Task Running_on_a_session_another_workspace_is_running_shows_the_info_bar_instead_of_running() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (wsA, tabA, wsB, tabB) = rig.TwoWorkspaces();
        wsB.SessionMode = WorkspaceSessionMode.Shared; // B 는 A 가 차지한 접속의 세션을 함께 쓴다
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        rig.Host.Active = tabA;
        Pump();
        rig.State.BeginRun(a.Id); // A 가 실행을 시작했다
        Assert.False(rig.State.ShownBusyElsewhere); // 자기 실행이면 지금처럼 실행이 꺼진다

        rig.Host.Active = tabB;
        Pump();
        Assert.True(rig.State.ShownBusy);
        Assert.True(rig.State.ShownBusyElsewhere); // 실행 명령을 켜 두는 문맥 키(sessionBusyElsewhere)

        var controller = rig.Controller();
        Assert.False(await controller.RunAsync(RunMode.Statement, tab: tabB)); // 실행하지 않고(엔진을 부르면 NotSupportedException)

        var notice = wsB.BusyNotice;
        Assert.NotNull(notice);
        Assert.Contains("「월말 정산」", notice.Text, StringComparison.Ordinal);
        Assert.Contains("트랜잭션은 옮겨지지 않습니다", notice.Text, StringComparison.Ordinal);
        Assert.Null(wsA.BusyNotice);

        rig.State.EndRun(a.Id); // 앞 실행이 끝나면 막대는 저절로 닫힌다
        Pump();
        Assert.Null(wsB.BusyNotice);
        Assert.False(rig.State.ShownBusyElsewhere);
    });

    [Fact]
    public Task Run_in_a_new_session_moves_the_workspace_and_sends_the_run_again_while_wait_only_closes_the_bar() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, wsB, tabB) = rig.TwoWorkspaces();
        wsB.SessionMode = WorkspaceSessionMode.Shared;
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        rig.Host.Active = tabA;
        Pump();
        rig.State.BeginRun(a.Id);
        rig.Host.Active = tabB;
        Pump();
        var reruns = 0;

        Assert.True(rig.Router.OfferNewSession(tabB, () =>
        {
            reruns++;
            return Task.CompletedTask;
        }));
        wsB.BusyNotice!.Wait();
        Assert.Null(wsB.BusyNotice);
        Assert.Equal(0, reruns);

        Assert.True(rig.Router.OfferNewSession(tabB, () =>
        {
            reruns++;
            return Task.CompletedTask;
        }));
        await wsB.BusyNotice!.RunInNewSession();
        Pump();

        Assert.Equal(1, reruns);
        Assert.Null(wsB.BusyNotice);
        var moved = rig.Router.SessionFor(tabB)!;
        Assert.NotSame(a, moved);
        Assert.Equal(WorkspaceSessionMode.Own, wsB.SessionMode);
        Assert.False(rig.State.ShownBusy); // 새 세션은 바쁘지 않다
        Assert.Same(a, rig.Router.SessionFor(tabA)); // A 의 실행·트랜잭션은 그대로
    });

    [Fact]
    public Task Cancel_and_the_info_bar_do_not_reach_a_run_of_the_same_workspace_or_a_dedicated_tab() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, wsB, tabB) = rig.TwoWorkspaces();
        wsB.SessionMode = WorkspaceSessionMode.Shared;
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        rig.Host.Active = tabB;
        Pump();
        rig.State.BeginRun(a.Id); // B 자신의 실행

        Assert.False(rig.Router.OfferNewSession(tabB, () => Task.CompletedTask));
        Assert.Null(wsB.BusyNotice);
        Assert.False(rig.State.ShownBusyElsewhere);

        Assert.True(ContextKeyReasons.Evaluate("sessionBusy && !sessionBusyElsewhere", k => k == "sessionBusy"));
        Assert.False(ContextKeyReasons.Evaluate("sessionBusy && !sessionBusyElsewhere", k => k is "sessionBusy" or "sessionBusyElsewhere")); // 남의 실행은 취소하지 않는다
        Assert.True(ContextKeyReasons.Evaluate("(hasSession || dedicatedSession || tabConnectionMissing) && (!sessionBusy || sessionBusyElsewhere) && !mybatisDocActive",
            k => k is "hasSession" or "sessionBusy" or "sessionBusyElsewhere"));
    });

    [Fact]
    public Task The_run_owner_is_the_workspace_that_started_it_not_the_one_active_when_it_began() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (wsA, tabA, wsB, tabB) = rig.TwoWorkspaces();
        wsB.SessionMode = WorkspaceSessionMode.Shared;
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        rig.Host.Active = tabB; // 세션을 여는 사이 B 로 옮겨 갔다
        Pump();
        rig.State.BeginRun(a.Id, wsA); // 실행 컨트롤러는 실행한 탭의 작업 공간을 넘긴다

        Assert.True(rig.State.ShownBusyElsewhere); // B 에서는 남의 실행
        rig.Host.Active = tabA;
        Pump();
        Assert.False(rig.State.ShownBusyElsewhere); // A 에서는 자기 실행: 실행 꺼짐·취소 켜짐
        Assert.False(rig.Router.OfferNewSession(tabA, () => Task.CompletedTask));
        rig.State.EndRun(a.Id);

        rig.Host.Active = tabB;
        Pump();
        rig.State.BeginRun(); // PL/SQL 실행 창처럼 작업 공간 밖의 실행: 시작한 쪽을 모른다
        Assert.False(rig.State.ShownBusyElsewhere);
        rig.State.EndRun();
    });

    [Fact]
    public Task With_the_setting_off_a_shared_busy_session_behaves_as_before() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Router.SessionPerWorkspace = () => false; // 모두 접속의 세션을 함께 쓴다(WP-14 전)
        var a = await rig.ConnectAsync();
        var (wsA, tabA, wsB, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        rig.Host.Active = tabB;
        Pump();
        Assert.Equal("연결됨", rig.State.ShownSessionText); // 세션이 하나면 범위 글을 붙이지 않는다

        rig.State.BeginRun(a.Id, wsA);
        Assert.True(rig.State.ShownBusy);
        Assert.False(rig.State.ShownBusyElsewhere); // 실행 꺼짐·취소 켜짐(B 에서도 A 의 실행을 취소할 수 있다)
        Assert.False(rig.Router.OfferNewSession(tabB, () => Task.CompletedTask));
        Assert.Null(wsB.BusyNotice);
        rig.State.EndRun(a.Id);
    });

    [Fact]
    public Task The_info_bar_closes_when_its_tab_was_closed_or_the_workspace_moved_to_another_session() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (wsA, tabA, wsB, tabB) = rig.TwoWorkspaces();
        wsB.SessionMode = WorkspaceSessionMode.Shared;
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        rig.Host.Active = tabB;
        var tabB2 = rig.Host.NewDocument();
        Pump();
        rig.State.BeginRun(a.Id, wsA);
        var reruns = 0;
        Assert.True(rig.Router.OfferNewSession(tabB, () =>
        {
            reruns++;
            return Task.CompletedTask;
        }));
        var notice = wsB.BusyNotice!;

        Assert.True(await rig.Host.CloseAsync(tabB, default)); // 막대가 떠 있는 사이 그 탭을 닫았다
        Pump();
        await notice.RunInNewSession();

        Assert.Equal(0, reruns); // 닫은 탭의 글을 실행하지 않는다
        Assert.Null(wsB.BusyNotice);
        Assert.Single(rig.Service.SessionsOf(a.Profile.Id)); // 다른 탭의 작업 공간을 옮기지도 않았다

        rig.Host.Active = tabB2;
        Pump();
        Assert.True(rig.Router.OfferNewSession(tabB2, () => Task.CompletedTask));
        Assert.NotNull(await rig.Router.NewSessionAsync()); // 콤보 「새 세션」 으로 옮겼다
        Pump();
        Assert.Null(wsB.BusyNotice); // 더는 쓰지 않는 세션의 안내는 닫는다
        rig.State.EndRun(a.Id);
    });

    [Fact]
    public Task Stepping_onto_the_new_session_row_of_a_closed_combo_opens_nothing() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, _, _) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        rig.Host.Active = tabA;
        Pump();
        var combo = new ConnectionComboViewModel(rig.Fx.Events, router: () => rig.Router);
        Assert.False(combo.IsDropDownOpen);

        combo.Selected = combo.Entries[^1]; // 닫힌 콤보에서 화살표·휠로 넘어갔다
        Pump();

        Assert.Single(rig.Service.SessionsOf(a.Profile.Id));
        Assert.Same(a, combo.Selected?.Session);
        Assert.False(combo.Selected!.IsNewSession);
    });

    // ---------- 안내 막대 View·렌더 ----------

    [Fact]
    public Task The_info_bar_shows_the_notice_of_its_workspace_and_hides_without_one() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, wsB, tabB) = rig.TwoWorkspaces();
        wsB.SessionMode = WorkspaceSessionMode.Shared;
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        rig.Host.Active = tabA;
        Pump();
        rig.State.BeginRun(a.Id);
        rig.Host.Active = tabB;
        Pump();
        var bar = new SessionBusyBar { DataContext = wsB };
        var window = new Window { Width = 900, Height = 120, Content = new StackPanel { Children = { bar } } };
        window.Show();
        try
        {
            Pump();
            Assert.False(bar.IsVisible);

            Assert.True(rig.Router.OfferNewSession(tabB, () => Task.CompletedTask));
            Assert.True(bar.IsVisible);
            Assert.Equal(wsB.BusyNotice!.Text, bar.Text);
            Pump();
            Render(window, "busybar");
            bar.WaitButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Pump();
            Assert.False(bar.IsVisible);
            Assert.Null(wsB.BusyNotice);

            window.Content = null; // 떼어 낸 막대는 작업 공간을 보지 않는다
            Pump();
            Assert.True(rig.Router.OfferNewSession(tabB, () => Task.CompletedTask));
            Assert.False(bar.IsVisible);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Combo_list_and_the_three_status_texts_render_in_light_and_dark() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        await rig.ConnectAsync();
        var (_, tabA, wsB, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        rig.Host.Active = tabB;
        Pump();
        var status = new SessionStatusViewModel(rig.Fx.Events);
        var statusView = new SessionStatusView { DataContext = status };
        var combo = new ConnectionComboViewModel(rig.Fx.Events, router: () => rig.Router);
        var comboView = new ConnectionComboView { DataContext = combo };
        var window = new Window { Width = 520, Height = 260, Content = new StackPanel { Spacing = 8, Margin = new Thickness(8), Children = { comboView, statusView } } };
        window.Show();
        try
        {
            Pump();
            Assert.Equal("새 세션 · 첫 실행 때 연결", status.Text);
            Render(window, "status-pending");

            await rig.Router.PrepareAsync(tabB);
            Pump();
            Assert.Equal("워크스페이스 세션 · 연결됨", status.Text);
            Render(window, "status-own");

            var wsC = rig.Host.NewWorkspace();
            rig.Host.Active = wsC.Tabs[0];
            Pump();
            await rig.Router.SelectAsync(rig.Router.SessionFor(tabB)!);
            Pump();
            Assert.Equal("공유 세션 · 다른 워크스페이스 1개와 함께 · 연결됨", status.Text);
            Render(window, "status-shared");

            rig.Host.Active = tabB;
            Pump();
            combo.IsDropDownOpen = true; // 헤드리스는 펼친 목록을 창의 덧그림 층에 그린다
            Pump();
            Assert.Equal(["1 - scott@a", "1 - scott@a (2)", "새 세션 — scott@a (3)"], combo.Entries.Select(i => i.Label));
            Assert.Same(combo.Entries, comboView.Combo.ItemsSource);
            Render(window, "combo");
            combo.IsDropDownOpen = false;
            Assert.Equal("재고 점검", wsB.Name);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Settings_has_a_new_session_per_workspace_checkbox_on_by_default_and_saves_it() => Ui.Run(async () =>
    {
        var service = new Oranda.App.Shell.Settings.SettingsService(new Oranda.App.Tests.Features.Update.MemorySettingsStore());
        var vm = new Oranda.App.Shell.Settings.SettingsViewModel(service, new ShellText(language: "ko"));
        var window = new Oranda.App.Shell.Settings.SettingsWindow { DataContext = vm };
        window.Show();
        try
        {
            Pump();
            var box = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<CheckBox>().Single(c => c.Name == "SessionPerWorkspaceBox");
            Assert.Equal("워크스페이스마다 새 세션", box.Content);
            Assert.True(box.IsChecked);
            Assert.True(box.IsEffectivelyVisible);
            Render(window, "settings");

            box.IsChecked = false;
            Assert.False(vm.SessionPerWorkspace);
            await vm.SaveAsync();
            Assert.False(service.Current.Workspace.SessionPerWorkspace);
        }
        finally
        {
            window.Close();
        }
    });

    internal static void Render(Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (variant, tone) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var size = new PixelSize(Math.Max(1, (int)window.Bounds.Width), Math.Max(1, (int)window.Bounds.Height));
                using var bmp = new RenderTargetBitmap(size);
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"multisession-{name}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Dispatcher.UIThread.RunJobs();
        }
    }
}
