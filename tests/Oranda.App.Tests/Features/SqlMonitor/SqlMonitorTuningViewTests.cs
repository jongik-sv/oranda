using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.SqlMonitor;
using Oranda.App.Shell.Controls;
using Oranda.App.Tests.Features.MyBatisWorkspace.Flow;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Tuning;

namespace Oranda.App.Tests.Features.SqlMonitor;

/// <summary>
/// TSK-06-06 화면 시험(Headless): 튜닝 탭·도구 모음 단추가 상태마다 맞게 보이고, 대기 분류 막대가 채워진다.
/// <c>ORANDA_RENDER_DIR</c> 가 있으면 상태마다 라이트·다크 PNG 를 뽑는다(시안 비교용, 저장소 밖).
/// </summary>
public class SqlMonitorTuningViewTests
{
    private const int W = 1180;
    private const int H = 760;

    private static T Named<T>(Window w, string name) where T : Control => ((Control)w.Content!).FindControl<T>(name) ?? throw new InvalidOperationException(name);

    private static Window Show(SqlMonitorViewModel vm)
    {
        var window = new Window { Content = new SqlMonitorView { DataContext = vm }, Width = W, Height = H };
        window.Show();
        MyBatisFlowRig.Pump();
        return window;
    }

    private static void Shot(Window window, string name)
    {
        MyBatisFlowRig.Pump();
        MyBatisFlowRig.Render(window, "sqlmon-" + name, W, H);
    }

    [Fact]
    public Task Toolbar_buttons_follow_selection_and_registered_commands() => HeadlessHost.Run(async () =>
    {
        var h = await TuningHarness.StartAsync(select: false);
        var window = Show(h.Vm);
        var tabs = Named<TabControl>(window, "DetailTabs");
        Assert.Equal(5, tabs.ItemCount);
        Assert.False(Named<Button>(window, "ChildCursorsButton").IsVisible);
        Assert.False(Named<Button>(window, "AdvisorScriptButton").IsEnabled);

        h.Available.Add(SqlMonitorViewModel.ChildCursorsCommand);
        h.Vm.SelectedSql = h.Vm.Results[0];
        MyBatisFlowRig.Pump();
        Assert.True(Named<Button>(window, "ChildCursorsButton").IsVisible);
        Assert.True(Named<Button>(window, "AdvisorScriptButton").IsEnabled);
        Assert.True(Named<Button>(window, "BaselineScriptButton").IsEnabled);
        Assert.Contains("실행하지 않습니다", ToolTip.GetTip(Named<Button>(window, "AdvisorScriptButton")) as string, StringComparison.Ordinal);

        h.Vm.DetailTabIndex = SqlMonitorViewModel.WaitsTab;
        MyBatisFlowRig.Pump();
        Assert.Equal(SqlMonitorViewModel.WaitsTab, tabs.SelectedIndex);
        Assert.True(Named<Button>(window, "WaitStartBigButton").IsEffectivelyVisible);
        window.Close();
    });

    [Fact]
    public Task Wait_tab_states_render_and_the_class_bar_fills() => HeadlessHost.Run(async () =>
    {
        var h = await TuningHarness.StartAsync();
        h.Available.Add(SqlMonitorViewModel.LockMonitorCommand);
        h.Available.Add(SqlMonitorViewModel.ChildCursorsCommand);
        h.Vm.DetailTabIndex = SqlMonitorViewModel.WaitsTab;
        var window = Show(h.Vm);
        Shot(window, "waits-ready");

        // 모으는 중(18 / 30): 17 번째 기다림까지는 바로, 그 뒤는 멈춘다.
        h.Tuning.Samples = i => Mix(i);
        var delays = 0;
        var gate = new TaskCompletionSource();
        h.Vm.Waits.Delay = async (_, ct) =>
        {
            if (++delays >= 18)
            {
                gate.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        var run = h.Vm.Waits.StartAsync();
        await gate.Task;
        Assert.Equal("18 / 30", h.Vm.Waits.ProgressText);
        Assert.True(Named<Button>(window, "WaitCancelButton").IsEffectivelyVisible);
        Shot(window, "waits-sampling");
        var bar = Named<Grid>(window, "ClassBar");
        Assert.Equal(h.Vm.Waits.Classes.Count, bar.Children.Count);
        Assert.True(h.Vm.Waits.Events.Single(e => e.IsLockWait).ShowLockLink);
        h.Vm.Waits.Cancel();
        await run;

        h.Vm.Waits.Delay = (_, _) => Task.CompletedTask;
        await h.Vm.Waits.StartAsync();
        Assert.Equal(WaitPaneState.Done, h.Vm.Waits.State);
        Shot(window, "waits-done");

        h.Tuning.Samples = _ => [];
        await h.Vm.Waits.StartAsync();
        Assert.Equal(WaitPaneState.Empty, h.Vm.Waits.State);
        Shot(window, "waits-empty");

        h.Vm.Waits.UseAsh = true;
        Assert.True(h.Vm.Waits.IsLicenseOff);
        MyBatisFlowRig.Pump();
        Assert.True(Named<Button>(window, "WaitLicenseManagerButton").IsEffectivelyVisible);
        Shot(window, "waits-ash-license-off");
        window.Close();
    });

    [Fact]
    public Task Awr_tab_license_off_loaded_and_compare_render() => HeadlessHost.Run(async () =>
    {
        var off = await TuningHarness.StartAsync();
        off.Available.Add(SqlMonitorViewModel.ChildCursorsCommand);
        off.Vm.DetailTabIndex = SqlMonitorViewModel.AwrTab;
        var window = Show(off.Vm);
        Assert.True(Named<Button>(window, "AwrLicenseManagerButton").IsEffectivelyVisible);
        Assert.True(Named<Button>(window, "AwrChildCursorsButton").IsEffectivelyVisible);
        Shot(window, "awr-license-off");
        window.Close();

        var h = await TuningHarness.StartAsync(pack: LicensePack.Diagnostics);
        h.Tuning.Awr = SqlMonitorTuningTests.AwrRows();
        h.Vm.DetailTabIndex = SqlMonitorViewModel.AwrTab;
        window = Show(h.Vm);
        Assert.Equal(AwrPaneState.Loaded, h.Vm.Awr.State);
        Assert.Equal(4, Named<DetailTable>(window, "AwrList").Rows!.Count);
        Assert.True(Named<Button>(window, "AwrCompareButton").IsEnabled);
        h.Vm.Awr.SelectedRow = h.Vm.Awr.Rows[3];
        Shot(window, "awr-loaded");

        await h.Vm.Awr.CompareAsync();
        MyBatisFlowRig.Pump();
        Assert.True(Named<DetailTable>(window, "AwrCompareList").IsEffectivelyVisible);
        Shot(window, "awr-compare");

        h.Tuning.Awr = [];
        await h.Vm.Awr.LoadAsync();
        Shot(window, "awr-empty");
        window.Close();
    });

    [Fact]
    public Task Plan_controls_tab_states_render() => HeadlessHost.Run(async () =>
    {
        var h = await TuningHarness.StartAsync();
        h.Tuning.Controls =
        [
            new SqlPlanControlRow(SqlPlanControlKind.Baseline, "SQL_PLAN_8k2xa1c4", "ENABLED", new DateTime(2026, 8, 11), "SELECT lh.lot_id, lh.evt_time FROM mes.lot_hist lh") { Accepted = true },
            new SqlPlanControlRow(SqlPlanControlKind.Baseline, "SQL_PLAN_8k2x77f0", "ENABLED", new DateTime(2026, 10, 24), "SELECT lh.lot_id, lh.evt_time FROM mes.lot_hist lh") { Accepted = false },
            new SqlPlanControlRow(SqlPlanControlKind.Profile, "SYS_SQLPROF_01920a", "DISABLED", new DateTime(2026, 3, 2), "SELECT lh.lot_id, lh.evt_time FROM mes.lot_hist lh"),
        ];
        h.Vm.DetailTabIndex = SqlMonitorViewModel.ControlsTab;
        var window = Show(h.Vm);
        Assert.Equal(PlanControlsState.Loaded, h.Vm.Controls.State);
        Assert.Equal(3, Named<DetailTable>(window, "ControlsList").Rows!.Count);
        Shot(window, "controls-loaded");

        h.Tuning.Controls = [];
        await h.Vm.Controls.LoadAsync();
        Shot(window, "controls-empty");

        h.Tuning.ControlsFail = new InvalidOperationException("ORA-01031: insufficient privileges");
        await h.Vm.Controls.LoadAsync();
        MyBatisFlowRig.Pump();
        Assert.True(Named<Button>(window, "ControlsCopyGrantButton").IsEffectivelyVisible);
        Shot(window, "controls-denied");
        window.Close();
    });

    [Fact]
    public Task Before_the_monitor_opens_every_tab_says_what_to_do() => HeadlessHost.Run(() =>
    {
        var vm = new SqlMonitorViewModel { PostToUi = a => a() };
        vm.DetailTabIndex = SqlMonitorViewModel.WaitsTab;
        var window = Show(vm);
        Assert.Equal(WaitPaneState.NoSession, vm.Waits.State);
        Shot(window, "no-session");
        Dispatcher.UIThread.RunJobs();
        window.Close();
    });

    // 시안과 비슷한 섞임: CPU 41 % · User I/O 33 % · Application 18 % · Cluster 5 % · Commit 3 %.
    private static List<WaitSample> Mix(int i)
    {
        var list = new List<WaitSample> { SqlMonitorTuningTests.Sample(11 + (i % 5), null), SqlMonitorTuningTests.Sample(16, null) };
        if (i % 3 != 2)
        {
            list.Add(SqlMonitorTuningTests.Sample(21 + (i % 4), "db file scattered read", "User I/O"));
            list.Add(SqlMonitorTuningTests.Sample(25, "db file scattered read", "User I/O"));
        }

        if (i % 2 == 0)
        {
            list.Add(SqlMonitorTuningTests.Sample(31 + (i % 2), "enq: TX - row lock contention", "Application"));
        }

        if (i % 6 == 0)
        {
            list.Add(SqlMonitorTuningTests.Sample(41, "gc cr multi block request", "Cluster"));
        }

        if (i % 9 == 0)
        {
            list.Add(SqlMonitorTuningTests.Sample(51, "log file sync", "Commit"));
        }

        return list;
    }
}
