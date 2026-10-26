using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.Sessions;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Features.ConnectionManager;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Sessions;
using Oranda.Core.Storage;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.Sessions;

/// <summary>
/// 결함(TSK-14-06): 같은 프로필 세션 넷(1 = 접속·미사용, 2·3 = 미사용, 4 = 활성 작업 공간 전용)에서 툴바 콤보·「세션」 창으로 세션을 바꾸면
/// 라우터는 고른 세션에 묶였는데 콤보 상자는 빈 「접속 안 됨」 을 보였다(목록을 다시 만들면 ComboBox 의 SelectionBoxItem 이 비었다).
/// 「세션」 창 줄 두 번 누르기·Enter 도 활성 작업 공간(SQL·MyBatis)에 그 세션을 묶는다.
/// </summary>
public sealed class SessionSwitchTests
{
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            Pump();
            await Task.Delay(10);
        }
    }

    /// <summary>콤보 상자가 <paramref name="pick"/> 줄을 고른 채로 보이는지(빈 「접속 안 됨」 이 아니다). 실패하면 VM·상자 값을 함께 적는다.</summary>
    private static void AssertComboShows(ConnectionComboView view, ConnectionComboViewModel combo, ISession pick, string step)
    {
        var c = view.Combo;
        static string L(object? o) => (o as ConnectionComboItem)?.Label ?? "null";
        var text = $"{step}: vm={L(combo.Selected)} selected={L(c.SelectedItem)} index={c.SelectedIndex} box={L(c.SelectionBoxItem)}";
        Assert.True(ReferenceEquals(pick, combo.Selected?.Session), text);
        Assert.True(ReferenceEquals(pick, (c.SelectedItem as ConnectionComboItem)?.Session), text);
        Assert.True(ReferenceEquals(pick, (c.SelectionBoxItem as ConnectionComboItem)?.Session), text); // 상자에 보이는 줄(없으면 「접속 안 됨」 빈 글)
    }

    /// <summary>작업 공간 둘(기본 = 탭 하나, B = 활성) 중 B 에서 「새 세션」 을 세 번 눌러 같은 DB 세션 넷을 만든다(1·2·3 미사용, 4 = B 전용).</summary>
    private static async Task<(ISession A, ISession S2, ISession S3, ISession S4, SqlEditorViewModel TabB)> FourSessionsAsync(SessionsViewModelTests.Rig rig)
    {
        var a = await rig.ConnectAsync();
        rig.Host.EnsureWorkspace();
        rig.Host.NewDocument();
        var wsB = rig.Host.NewWorkspace();
        var tabB = wsB.Tabs[0];
        rig.Host.Active = tabB;
        Pump();
        var s2 = (await rig.Router.NewSessionAsync())!;
        var s3 = (await rig.Router.NewSessionAsync())!;
        var s4 = (await rig.Router.NewSessionAsync())!;
        Pump();
        Assert.Equal([a, s2, s3, s4], rig.Service.SessionsOf(a.Profile.Id));
        Assert.Same(s4, rig.Router.SessionFor(tabB));
        Assert.Empty(rig.Router.WorkspacesOn(a));
        return (a, s2, s3, s4, tabB);
    }

    [Fact]
    public Task The_combo_box_keeps_showing_the_session_picked_in_the_combo_or_the_sessions_window() => Ui.Run(async () =>
    {
        using var rig = new SessionsViewModelTests.Rig();
        var (a, s2, s3, s4, tabB) = await FourSessionsAsync(rig);
        var combo = new ConnectionComboViewModel(rig.Fx.Events, router: () => rig.Router);
        var view = new ConnectionComboView { DataContext = combo };
        var window = new Window { Width = 400, Height = 100, Content = view };
        window.Show();
        try
        {
            Pump();
            AssertComboShows(view, combo, s4, "start");

            // 콤보에서 고르기(상자의 SelectedItem 을 바꾸면 사용자가 고른 것과 같은 길로 간다).
            foreach (var pick in new[] { s2, a, s3, s4, a })
            {
                view.Combo.SelectedItem = view.Combo.Items.OfType<ConnectionComboItem>().Single(i => !i.IsNewSession && ReferenceEquals(i.Session, pick));
                await SettleAsync();
                Assert.Same(pick, rig.Router.SessionFor(tabB));
                AssertComboShows(view, combo, pick, "combo " + rig.Router.LabelOf(pick));
            }

            // 「세션」 창의 「이 세션 쓰기」.
            foreach (var pick in new[] { s3, a, s2 })
            {
                await rig.Vm.UseAsync(rig.Vm.Rows.Single(r => ReferenceEquals(r.Session, pick)));
                await SettleAsync();
                Assert.Same(pick, rig.Router.SessionFor(tabB));
                AssertComboShows(view, combo, pick, "use " + rig.Router.LabelOf(pick));
            }

            Assert.Equal(4, rig.Service.SessionsOf(a.Profile.Id).Count); // 옮겨도 쓰지 않게 된 세션은 열려 있다
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Double_clicking_or_pressing_enter_on_a_row_uses_that_session_for_the_active_workspace() => Ui.Run(async () =>
    {
        using var rig = new SessionsViewModelTests.Rig();
        var (a, s2, s3, _, tabB) = await FourSessionsAsync(rig);
        rig.Vm.Refresh();
        var combo = new ConnectionComboViewModel(rig.Fx.Events, router: () => rig.Router);
        var comboView = new ConnectionComboView { DataContext = combo };
        var view = new SessionsView { DataContext = rig.Vm };
        DockPanel.SetDock(comboView, Avalonia.Controls.Dock.Top);
        var window = new Window { Width = 320, Height = 420, Content = new DockPanel { Children = { comboView, view } } };
        window.Show();
        try
        {
            await SettleAsync();
            var list = view.FindControl<ListBox>("SessionList")!;
            Assert.Equal(4, list.ItemCount);

            // 두 번 누르기: 줄 2 → 활성 작업 공간이 그 세션을 쓰고 콤보도 그 줄을 보인다.
            await DoubleClickAsync(window, list, 1);
            Assert.Same(s2, rig.Router.SessionFor(tabB));
            AssertComboShows(comboView, combo, s2, "double-click (2)");
            Assert.Same(rig.Vm.Rows[1], rig.Vm.Selected);

            // 두 번 누르기: 줄 1(접속) → 함께 쓰기로 묶인다.
            await DoubleClickAsync(window, list, 0);
            Assert.Same(a, rig.Router.SessionFor(tabB));
            AssertComboShows(comboView, combo, a, "double-click (1)");

            // Enter: 고른 줄(3).
            rig.Vm.Selected = rig.Vm.Rows[2];
            list.ContainerFromIndex(2)!.Focus();
            Pump();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await SettleAsync();
            Assert.Same(s3, rig.Router.SessionFor(tabB));
            AssertComboShows(comboView, combo, s3, "enter (3)");
            Assert.Equal(4, rig.Service.SessionsOf(a.Profile.Id).Count);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Activating_a_row_does_nothing_for_the_session_in_use_or_while_a_non_sql_document_is_active() => Ui.Run(async () =>
    {
        using var rig = new SessionsViewModelTests.Rig();
        var (_, s2, _, s4, tabB) = await FourSessionsAsync(rig);
        rig.Vm.Refresh();
        var changed = 0;
        rig.Router.Changed += (_, _) => changed++;

        await rig.Vm.ActivateAsync(rig.Vm.Rows.Single(r => ReferenceEquals(r.Session, s4))); // 이미 쓰는 세션
        Assert.Equal(0, changed);

        rig.Registry.SqlDocumentActive = () => false; // PL/SQL 편집기 같은 다른 문서가 활성
        await rig.Vm.ActivateAsync(rig.Vm.Rows.Single(r => ReferenceEquals(r.Session, s2)));
        Assert.Same(s4, rig.Router.SessionFor(tabB));
        Assert.Equal(0, changed);

        await rig.Vm.ActivateAsync(null);
        Assert.Equal(0, changed);
    });

    [Fact]
    public Task In_the_app_shell_a_mybatis_workspace_switches_sessions_from_the_toolbar_combo_and_the_sessions_window() => HeadlessHost.Run(async () =>
    {
        using var harness = new ShellHarness();
        await harness.Services.GetService<IEnumerable<IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single().RunAsync(default); // SQL 작업 공간 ↔ 문서
        await SettleAsync();

        // 접속 서비스는 가짜 세션 관리자로 미리 넣는다(라우터·콤보·도킹 연결은 앱과 같다).
        var events = harness.Get<IEventBus>();
        var stub = new StubSessionManager(events) { PublishOpened = true };
        var service = new ConnectionService(harness.Get<IConnectionStore>(), new MemoryCredentials(), stub, harness.Get<IAppDataPaths>(), events);
        harness.Services.AddInstance<IConnectionService>(service);
        var router = SessionRouter.Resolve(harness.Services);
        var profile = await service.SaveProfileAsync(new ConnectionProfile("prod", "scott", "localhost:1521/FREEPDB1"), null, false, default);
        var outcome = await service.ConnectAsync(profile, "pw", default);
        Assert.True(outcome.IsConnected, outcome.Message);
        var a = outcome.Session!;
        await SettleAsync();

        // 「* 기본」 SQL 작업 공간 + MyBatis 매퍼 작업 공간(활성, 문장 탭 하나).
        var host = SqlEditorHost.Current;
        if (host.EnsureWorkspace().Tabs.Count == 0)
        {
            host.NewDocument();
        }

        var root = Path.Combine(Path.GetTempPath(), "oranda-msswitch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "EmpMapper.xml");
            await File.WriteAllTextAsync(path, MapperSamples.EmpMapper);
            host.MyBatisHooks = new MyBatisHooks(harness.Services);
            await host.OpenWorkspaceFileAsync(path, default);
            await SettleAsync();
            var mapper = host.ActiveWorkspace!;
            var tab = Assert.IsType<MyBatisWorkspaceSession>(mapper.MyBatis).OpenStatement("selectEmpList")!;
            host.Active = tab;
            await SettleAsync();
            Assert.Same(mapper, harness.Get<DockWorkspace>().ActiveDocument?.Panel);
            Assert.True(router.SqlDocumentActive);

            var s2 = (await router.NewSessionAsync())!;
            var s3 = (await router.NewSessionAsync())!;
            var s4 = (await router.NewSessionAsync())!;
            await SettleAsync();
            Assert.Equal([a, s2, s3, s4], service.SessionsOf(a.Profile.Id));
            Assert.Same(s4, router.SessionFor(tab));

            var comboView = (ConnectionComboView)harness.Window.FindControl<ContentControl>("ConnectionComboHost")!.Content!;
            var combo = (ConnectionComboViewModel)comboView.DataContext!;
            AssertComboShows(comboView, combo, s4, "start");

            // 툴바 콤보: 펼쳐서 고르고 닫는다.
            foreach (var pick in new[] { s2, a, s3, s4, a })
            {
                comboView.Combo.IsDropDownOpen = true;
                await SettleAsync();
                comboView.Combo.SelectedItem = comboView.Combo.Items.OfType<ConnectionComboItem>().Single(i => !i.IsNewSession && ReferenceEquals(i.Session, pick));
                comboView.Combo.IsDropDownOpen = false;
                await SettleAsync();
                Assert.Same(pick, router.SessionFor(tab));
                Assert.Same(mapper, host.WorkspaceOf(tab));
                AssertComboShows(comboView, combo, pick, "combo " + router.LabelOf(pick));
            }

            // 「세션」 창: 두 번 누르기·Enter 와 같은 동작(이 세션 쓰기).
            using var sessions = new SessionsViewModel(harness.Services);
            foreach (var pick in new[] { s3, a, s2 })
            {
                await sessions.ActivateAsync(sessions.Rows.Single(r => ReferenceEquals(r.Session, pick)));
                await SettleAsync();
                Assert.Same(pick, router.SessionFor(tab));
                AssertComboShows(comboView, combo, pick, "sessions " + router.LabelOf(pick));
            }

            Assert.Equal(4, service.SessionsOf(a.Profile.Id).Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    });

    private static async Task DoubleClickAsync(Window window, ListBox list, int index)
    {
        var item = list.ContainerFromIndex(index)!;
        var p = item.TranslatePoint(new Point(20, item.Bounds.Height / 2), window)!.Value;
        for (var i = 0; i < 2; i++)
        {
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
        }

        await SettleAsync();
    }
}
