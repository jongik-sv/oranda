using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.LockMonitor;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.LockMonitor;

/// <summary>
/// TSK-05-03 Lock 모니터 화면(Headless): 표·트리가 그려지고 고르기가 서로 따라가며, 상태마다 맞는 본문이 보인다.
/// <c>ORANDA_RENDER_DIR</c> 가 있으면 상태별 라이트·다크 PNG 를 그 폴더에 쓴다(시안과 견주기용).
/// </summary>
public class LockMonitorViewTests
{
    private const int W = 1360;
    private const int H = 780;

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static (Window Window, LockMonitorView View) Show(LockMonitorViewModel vm, int width = W, int height = H)
    {
        var view = new LockMonitorView { DataContext = vm };
        var window = new Window { Content = view, Width = width, Height = height };
        window.Show();
        Pump();
        view.Rebuild();
        Pump();
        window.UpdateLayout();
        return (window, view);
    }

    private static void Render(Window window, string name, int width = W, int height = H)
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
                Pump();
                window.UpdateLayout();
                using var bmp = new RenderTargetBitmap(new PixelSize(width, height));
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"lock-{name}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Pump();
        }
    }

    private static bool Visible(Control c) => c.IsEffectivelyVisible;

    private static IEnumerable<TextBlock> Texts(Visual root) => root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible);

    [Fact]
    public Task Data_screen_draws_tree_lists_and_detail_and_selection_follows() => HeadlessHost.Run(async () =>
    {
        var h = new LockHarness();
        await h.Vm.StartAsync();
        h.Vm.SelectedKey = FakeLockMonitor.H1;
        await h.Vm.LoadDetailAsync();
        var (w, view) = Show(h.Vm);
        try
        {
            var tree = view.FindControl<ListBox>("TreeList")!;
            var locks = view.FindControl<MonitorGrid>("LockTable")!;
            await HeadlessHost.WaitUntil(() => locks.Model is { RowCount: > 0, HasSelection: true });
            Assert.True(Visible(view.FindControl<Grid>("DataGrid")!));
            Assert.Equal(8, tree.GetVisualDescendants().OfType<ListBoxItem>().Count());
            Assert.Equal(LockMonitorView.LockColumns.Select(c => c.Name), locks.Model!.Columns.Select(c => c.Name));
            Assert.Same(h.Vm.TreeRows[0], tree.SelectedItem);
            Assert.Equal(2, locks.Model.SelectedRowCount); // 1·1287 의 TX·TM
            Assert.Equal(0, locks.Model.Active.Row);
            Assert.Contains(Texts(view), t => t.Text == "보유 · 대기자 3");
            Render(w, "data");

            // 트리에서 다른 세션을 고르면 잠금 목록 ▶ 가 따라간다.
            tree.SelectedItem = h.Vm.TreeRows[4]; // 2·0644
            Pump();
            Assert.Equal(FakeLockMonitor.H2, h.Vm.SelectedKey);
            Assert.Equal(6, locks.Model.Active.Row);

            // 자동 새로 고침(목록 다시 채움) 뒤에도 고름이 남는다.
            var before = locks.Model;
            await h.Vm.RefreshAsync();
            await HeadlessHost.WaitUntil(() => !ReferenceEquals(locks.Model, before));
            Pump();
            Assert.Equal(FakeLockMonitor.H2, h.Vm.SelectedKey);
            Assert.Equal(6, locks.Model!.Active.Row);

            // 영향 순위 탭 · 원인 힌트 탭 · DDL 탭 · 잠긴 행 탭.
            h.Vm.TopTabIndex = LockMonitorViewModel.RankTab;
            h.Vm.DetailTabIndex = LockMonitorViewModel.HintTab;
            h.Vm.SelectedKey = FakeLockMonitor.H1;
            Pump();
            view.Rebuild();
            Pump();
            await HeadlessHost.WaitUntil(() => view.FindControl<MonitorGrid>("ImpactTable")!.Model is { RowCount: 3 });
            Render(w, "rank-hints");

            h.Vm.TopTabIndex = LockMonitorViewModel.DdlTab;
            await h.Vm.LoadDdlLocksAsync();
            h.Vm.SelectedKey = FakeLockMonitor.W1;
            h.Vm.DetailTabIndex = LockMonitorViewModel.RowTab;
            await h.Vm.LoadDetailAsync();
            Pump();
            view.Rebuild();
            Pump();
            await HeadlessHost.WaitUntil(() => view.FindControl<MonitorGrid>("DdlTable")!.Model is { RowCount: 3 });
            await HeadlessHost.WaitUntil(() => view.FindControl<MonitorGrid>("LockedRowTable")!.Model is { RowCount: 4 });
            Render(w, "ddl-row");

            h.Vm.TopTabIndex = LockMonitorViewModel.TreeTab;
            h.Vm.DetailTabIndex = LockMonitorViewModel.BindTab;
            await h.Vm.LoadDetailAsync();
            Pump();
            await HeadlessHost.WaitUntil(() => view.FindControl<MonitorGrid>("BindTable")!.Model is { RowCount: 2 });
            Render(w, "binds");
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Collapse_button_in_the_tree_hides_waiters() => HeadlessHost.Run(async () =>
    {
        var h = new LockHarness();
        await h.Vm.StartAsync();
        var (w, view) = Show(h.Vm);
        try
        {
            var tree = view.FindControl<ListBox>("TreeList")!;
            var button = tree.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("expander"));
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Pump();
            Assert.Equal(5, h.Vm.TreeRows.Count);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Delete_asks_to_kill_the_selected_session_but_not_inside_a_filter_box() => HeadlessHost.Run(async () =>
    {
        var h = new LockHarness { Answer = false };
        await h.Vm.StartAsync();
        h.Vm.SelectedKey = FakeLockMonitor.H1;
        var (w, view) = Show(h.Vm);
        try
        {
            var box = view.FindControl<TextBox>("UserFilterBox")!;
            box.Focus();
            w.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            Pump();
            Assert.Null(h.Asked);

            view.FindControl<ListBox>("TreeList")!.GetVisualDescendants().OfType<ListBoxItem>().First().Focus();
            Pump();
            Assert.IsNotType<TextBox>(w.FocusManager!.GetFocusedElement());
            w.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            Pump();
            Assert.NotNull(h.Asked);
            Assert.Empty(h.Monitor.Killed);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task State_screens_show_their_own_body() => HeadlessHost.Run(async () =>
    {
        // 접속 없음
        var none = new LockMonitorViewModel(new FakeLockMonitor(), _ => Task.FromResult<Oranda.Core.Sessions.ISession?>(null)) { PostToUi = a => a() };
        await none.StartAsync();
        var (w1, v1) = Show(none, 900, 420);
        Assert.Contains(Texts(v1), t => t.Text == "접속이 없습니다");
        Assert.False(v1.FindControl<Button>("KillButton")!.IsEffectivelyEnabled);
        Render(w1, "state-noconnection", 900, 420);
        w1.Close();

        // 첫 읽기
        var loading = new LockHarness();
        loading.Monitor.Hold = new TaskCompletionSource();
        var start = loading.Vm.StartAsync();
        var (w2, v2) = Show(loading.Vm, 900, 420);
        Assert.Contains(Texts(v2), t => t.Text == "잠금 정보를 읽는 중…");
        Render(w2, "state-loading", 900, 420);
        loading.Monitor.Hold.SetResult();
        await start;
        w2.Close();

        // 빈 결과
        var empty = new LockHarness();
        empty.Monitor.Quiet = true;
        await empty.Vm.StartAsync();
        empty.Vm.MinWaitSeconds = 3;
        var (w3, v3) = Show(empty.Vm, 900, 420);
        Assert.Contains(Texts(v3), t => t.Text == "대기 중인 잠금이 없습니다");
        Assert.True(v3.FindControl<Button>("ClearFiltersButton")!.IsEffectivelyVisible);
        Render(w3, "state-empty", 900, 420);
        w3.Close();

        // 권한 없음
        var denied = new LockHarness(LockHarness.NoPrivilege, new ConnectionProfile("MESPRD", "app_ro", "h:1521/s", Environment: ConnectionEnvironment.Production));
        await denied.Vm.StartAsync();
        var (w4, v4) = Show(denied.Vm, 900, 420);
        Assert.True(v4.FindControl<Oranda.App.Features.SessionMonitor.MonitorAccessBanner>("AccessBanner")!.IsEffectivelyVisible);
        Assert.Contains(Texts(v4), t => t.Text == "권한이 생기면 F5 로 다시 읽습니다.");
        Render(w4, "state-denied", 900, 420);
        w4.Close();

        // 끊김
        var lost = new LockHarness();
        await lost.Vm.StartAsync();
        lost.Monitor.Fail = new InvalidOperationException("ORA-03113: end-of-file on communication channel");
        await lost.Vm.RefreshAsync();
        var (w5, v5) = Show(lost.Vm, 900, 420);
        Assert.True(v5.FindControl<Button>("ReconnectButton")!.IsEffectivelyVisible);
        Assert.False(v5.FindControl<Grid>("DataGrid")!.IsHitTestVisible);
        Render(w5, "state-lost", 900, 420);
        w5.Close();

        // DDL 탭 권한만 없음
        var ddl = new LockHarness();
        ddl.Monitor.DdlFail = new InvalidOperationException("ORA-00942: table or view does not exist");
        await ddl.Vm.StartAsync();
        ddl.Vm.TopTabIndex = LockMonitorViewModel.DdlTab;
        await ddl.Vm.LoadDdlLocksAsync();
        var (w6, v6) = Show(ddl.Vm, 1200, 520);
        Assert.Contains(Texts(v6), t => t.Text == "DDL 잠금은 dba_ddl_locks 권한이 필요합니다.");
        Render(w6, "state-ddl-denied", 1200, 520);
        w6.Close();
    });

    [Fact]
    public Task Kill_dialog_shows_waiters_and_defaults_to_cancel() => HeadlessHost.Run(async () =>
    {
        var h = new LockHarness();
        await h.Vm.StartAsync();
        h.Vm.SelectedKey = FakeLockMonitor.H1;
        var request = h.Vm.BuildKillRequest(disconnect: false)!;
        var content = LockKillDialog.Build(request, out var cancel, out var run);
        var window = new Window { Content = content, Width = 600, Height = 420 };
        window.Show();
        Pump();
        try
        {
            Assert.True(cancel.IsDefault);
            Assert.True(cancel.IsCancel);
            Assert.False(run.IsDefault);
            Assert.Equal("세션 종료", run.Content);
            Assert.Contains(Texts(content), t => t.Text == "이 세션 때문에 3개 세션이 기다립니다.");
            Assert.Contains(Texts(content), t => t.Text == "세션 1·1287 을 종료할까요?");
            Render(window, "kill-dialog", 600, 420);
        }
        finally
        {
            window.Close();
        }
    });
}
