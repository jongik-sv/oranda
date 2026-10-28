using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.LockMonitor;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Monitoring;

namespace Oranda.App.Tests.Features.LockMonitor;

/// <summary>
/// TSK-05-04 Lock 모니터 P2 화면(Headless, 시안 d): 방치 트랜잭션 탭·이력 띠·창 알림, 순환 띠·데드락 기록, 11g 안내, 빈 화면, 알림 설정 플라이아웃.
/// <c>ORANDA_RENDER_DIR</c> 가 있으면 상태별 라이트·다크 PNG(<c>lock-p2-*.png</c>)를 그 폴더에 쓴다.
/// </summary>
public class LockP2ViewTests
{
    private const int W = 1360;
    private const int H = 820;

    private static void Pump()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static LockHarness New(ServerCapabilities? caps = null) =>
        new(caps, new ConnectionProfile("MESPRD", "mesadm", "h:1521/s", Environment: ConnectionEnvironment.Production) { Id = Guid.NewGuid() });

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

    private static void Render(Control target, string name, int width, int height)
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
                (target as TopLevel ?? TopLevel.GetTopLevel(target))?.UpdateLayout();
                using var bmp = new RenderTargetBitmap(new PixelSize(width, height));
                bmp.Render(target);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"lock-p2-{name}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Pump();
        }
    }

    private static IEnumerable<TextBlock> Texts(Visual root) => root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible);

    /// <summary>최근 30 분 동안 5 초마다 한 점(대기자·최대 대기가 오르내리는 모양).</summary>
    private static void FillHistory(LockMonitorViewModel vm)
    {
        var start = FakeLockMonitor.Now.AddMinutes(-30);
        for (var i = 0; i < 355; i++)
        {
            var phase = i / 355d;
            var waiters = phase < 0.3 ? (i % 40 < 6 ? 1 : 0) : phase < 0.6 ? 2 + (int)(5 * Math.Sin((phase - 0.3) * Math.PI / 0.3)) : 3 + i % 3;
            var wait = phase < 0.3 ? (i % 40 < 6 ? 15 : 0) : (long)((phase - 0.3) * 400);
            vm.History!.Add(new LockHistoryPoint(start.AddSeconds(i * 5), Math.Max(0, waiters), wait));
        }
    }

    [Fact]
    public Task Idle_tab_history_strip_and_alert_toast_follow_mockup_d1() => HeadlessHost.Run(async () =>
    {
        var h = New();
        h.Vm.AlertDuration = Timeout.InfiniteTimeSpan;
        await h.Vm.StartAsync();
        FillHistory(h.Vm);
        await h.Vm.RefreshKeepingDetailAsync();
        h.Vm.TopTabIndex = LockMonitorViewModel.IdleTab;
        await h.Vm.LoadIdleTransactionsAsync();
        h.Vm.SelectedKey = FakeLockMonitor.H2;
        await h.Vm.LoadDetailAsync();
        Assert.NotNull(h.Vm.ActiveAlert);

        var (w, view) = Show(h.Vm);
        try
        {
            var idle = view.FindControl<MonitorGrid>("IdleTable")!;
            await HeadlessHost.WaitUntil(() => idle.Model is { RowCount: 4, HasSelection: true });
            Assert.True(idle.IsEffectivelyVisible);
            Assert.Equal(LockMonitorView.IdleColumns.Select(c => c.Name), idle.Model!.Columns.Select(c => c.Name));
            Assert.Equal(0, idle.Model.Active.Row); // 2·0644 가 ▶
            Assert.True(idle.Model.IsEmphasized(0, 9)); // 차단 칸은 위험색
            Assert.True(view.FindControl<Border>("HistoryStrip")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<LockHistoryChart>("HistoryChart")!.Bounds.Width > 200);
            Assert.True(view.FindControl<LockAlertToast>("InlineToast")!.IsEffectivelyVisible);
            Assert.Contains(Texts(view), t => t.Text == "잠금 대기가 기준을 넘었습니다");
            Assert.Contains(Texts(view), t => t.Text == "사람이 쓰는 도구(SQL Developer)에서 고친 뒤 커밋하지 않은 것으로 보입니다.");
            Assert.Contains(Texts(view), t => t.Text == "방치 트랜잭션 4 · 그중 차단 중 2");
            Assert.False(view.FindControl<Border>("CycleBand")!.IsEffectivelyVisible);
            Render(w, "idle-history-toast", W, H);

            // 알림 닫기 → 카드가 사라지고 이력 띠는 남는다.
            h.Vm.DismissAlert();
            Pump();
            Assert.False(view.FindControl<LockAlertToast>("InlineToast")!.IsEffectivelyVisible);

            // 이력 띠 감추기(명령 oranda.monitor.locks.showHistory 와 같은 동작).
            h.Vm.ShowHistoryStrip = false;
            Pump();
            Assert.False(view.FindControl<Border>("HistoryStrip")!.IsEffectivelyVisible);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Cycle_band_and_deadlock_tab_follow_mockup_d2() => HeadlessHost.Run(async () =>
    {
        var h = New();
        var a = new SessionKey(1, 388, 11);
        var b = new SessionKey(2, 1903, 22);
        h.Monitor.Custom = new LockSnapshot(
        [
            new LockRow(a, "TX") { RequestedMode = 6, CtimeSeconds = 2, BlockingSession = b, Username = "MES_RPT", Event = "enq: TX - row lock contention", Status = "ACTIVE" },
            new LockRow(a, "TX") { HeldMode = 6, CtimeSeconds = 4, IsBlocking = true, Username = "MES_RPT", Status = "ACTIVE", Id1 = 9, Id2 = 1 },
            new LockRow(b, "TX") { RequestedMode = 6, CtimeSeconds = 1, BlockingSession = a, Username = "MES_BATCH", Event = "enq: TX - row lock contention", Status = "ACTIVE", Id1 = 9, Id2 = 1 },
        ],
        new BlockingTree([new BlockingNode(a, [new BlockingNode(b, []) { Username = "MES_BATCH", SecondsInWait = 1, Event = "enq: TX - row lock contention", Status = "ACTIVE" }]) { Username = "MES_RPT", Status = "ACTIVE" }]),
        FakeLockMonitor.Now);
        h.Vm.AlertEnabled = false;
        await h.Vm.StartAsync();
        h.Vm.TopTabIndex = LockMonitorViewModel.DeadlockTab;
        await h.Vm.LoadDeadlocksAsync();

        var (w, view) = Show(h.Vm);
        try
        {
            Assert.True(view.FindControl<Border>("CycleBand")!.IsEffectivelyVisible);
            Assert.StartsWith("1·0388 → 2·1903 → 1·0388", view.FindControl<TextBlock>("CycleTextLine")!.Text, StringComparison.Ordinal);
            var table = view.FindControl<MonitorGrid>("DeadlockTable")!;
            await HeadlessHost.WaitUntil(() => table.Model is { RowCount: 2 });
            Assert.True(table.IsEffectivelyVisible);
            Assert.Equal(LockMonitorView.DeadlockColumns.Select(c => c.Name), table.Model!.Columns.Select(c => c.Name));
            Assert.Contains(Texts(view), t => t.Text == "Enter: 편집기로 열기");
            Render(w, "cycle-deadlocks", W, H);

            // 「트리에서 보기」.
            view.FindControl<Button>("CycleTreeButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Pump();
            Assert.Equal(LockMonitorViewModel.TreeTab, h.Vm.TopTabIndex);
            Assert.Equal(a, h.Vm.SelectedKey);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Eleven_g_deadlock_notice_and_empty_idle_tab() => HeadlessHost.Run(async () =>
    {
        var caps = LockHarness.Dba;
        var monitor = new FakeLockMonitor { Idle = [] };
        var session = new FakeLockSession(new ServerInfo("11.2.0.4.0", "XE", "h", "XE", "XE", null, false, 1, caps),
            new ConnectionProfile("XE", "system", "127.0.0.1:1522/XE") { Id = Guid.NewGuid() });
        var vm = new LockMonitorViewModel(monitor, _ => Task.FromResult<Oranda.Core.Sessions.ISession?>(session))
        {
            PostToUi = a => a(),
            StartAutoRefresh = false,
            AlertEnabled = false,
        };
        await vm.StartAsync();
        vm.TopTabIndex = LockMonitorViewModel.DeadlockTab;
        await vm.LoadDeadlocksAsync();

        var (w, view) = Show(vm, 1100, 640);
        try
        {
            Assert.Contains(Texts(view), t => t.Text == "이 버전은 데드락 기록을 읽지 않습니다");
            Assert.Contains(Texts(view), t => t.Text?.StartsWith("11g 에서는 alert 로그를", StringComparison.Ordinal) == true);
            Assert.False(view.FindControl<MonitorGrid>("DeadlockTable")!.IsEffectivelyVisible);
            Render(w, "deadlock-11g", 1100, 640);

            vm.TopTabIndex = LockMonitorViewModel.IdleTab;
            await vm.LoadIdleTransactionsAsync();
            Pump();
            Assert.Contains(Texts(view), t => t.Text == "방치 트랜잭션이 없습니다");
            Assert.False(view.FindControl<MonitorGrid>("IdleTable")!.IsEffectivelyVisible);
            Render(w, "idle-empty", 1100, 640);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task No_deadlocks_in_24_hours_shows_the_empty_screen() => HeadlessHost.Run(async () =>
    {
        var h = New();
        h.Monitor.DeadlockEvents = [];
        h.Vm.AlertEnabled = false;
        await h.Vm.StartAsync();
        h.Vm.TopTabIndex = LockMonitorViewModel.DeadlockTab;
        await h.Vm.LoadDeadlocksAsync();
        var (w, view) = Show(h.Vm, 1100, 640);
        try
        {
            Assert.Contains(Texts(view), t => t.Text == "최근 24 시간 데드락이 없습니다");
            Render(w, "deadlock-empty", 1100, 640);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Alert_settings_flyout_has_threshold_snapshot_and_history_options() => HeadlessHost.Run(async () =>
    {
        var h = New();
        h.Vm.AlertEnabled = false;
        await h.Vm.StartAsync();
        var (w, view) = Show(h.Vm);
        try
        {
            var button = view.FindControl<Button>("AlertSettingsButton")!;
            var flyout = Assert.IsType<Flyout>(button.Flyout);
            var content = Assert.IsAssignableFrom<Control>(flyout.Content);

            // 플라이아웃 본문을 따로 창에 올려 그린다(헤드리스는 팝업을 창 그림에 넣지 않는다).
            flyout.Content = null;
            var host = new Window
            {
                Width = 330,
                Height = 400,
                DataContext = h.Vm,
                Content = new Border { Padding = new Thickness(14), Child = content },
            };
            host.Show();
            Pump();
            var texts = host.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("임계치 알림", texts);
            Assert.Contains("이력", texts);
            Assert.Contains("저장 위치: 앱 데이터 폴더\\lock-history. DB 에 쓰지 않습니다.", texts);
            var boxes = host.GetVisualDescendants().OfType<CheckBox>().ToList();
            Assert.Equal(4, boxes.Count);
            Assert.False(boxes[0].IsChecked); // 알림 꺼 둠
            var ups = host.GetVisualDescendants().OfType<NumericUpDown>().ToList();
            Assert.Equal([120m, 5m, 30m], ups.Select(u => u.Value ?? 0));
            Assert.All(ups.Take(2), u => Assert.False(u.IsEffectivelyEnabled)); // 꺼 두면 기준 칸도 꺼진다

            h.Vm.AlertEnabled = true;
            Pump();
            Assert.All(ups, u => Assert.True(u.IsEffectivelyEnabled));
            Render(host, "alert-settings", 330, 400);
            host.Close();
        }
        finally
        {
            w.Close();
        }
    });
}
