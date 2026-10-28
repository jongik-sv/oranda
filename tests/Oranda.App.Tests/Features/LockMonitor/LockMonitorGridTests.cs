using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Oranda.App.Features.LockMonitor;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Features.SqlMonitor;
using Oranda.App.Tests.Features.SqlMonitor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Grid;
using Oranda.Core.Tuning;

namespace Oranda.App.Tests.Features.LockMonitor;

/// <summary>
/// TSK-14-18: Lock 모니터 표 7개와 SQL 모니터 튜닝 탭 표 3개가 조회 결과와 같은 그리드(MonitorGrid)로 보이고,
/// 숫자 열은 수로 정렬되며, 새로 고침·다시 읽기 뒤에도 정렬·너비·필터·고른 행(키)이 남는다. 행 선택 연동·Delete(세션 종료)·Enter(데드락 열기)도 그대로.
/// </summary>
public class LockMonitorGridTests
{
    private static async Task<(Window Window, LockMonitorView View)> ShowAsync(LockMonitorViewModel vm, string grid)
    {
        var view = new LockMonitorView { DataContext = vm };
        var window = new Window { Content = view, Width = 1360, Height = 780 };
        window.Show();
        var g = view.FindControl<MonitorGrid>(grid)!;
        await HeadlessHost.WaitUntil(() => g.Model is { RowCount: > 0 } && g.GridControl.Bounds.Height > 0);
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    private static string[] Column(ResultGridModel m, int ordinal) => [.. Enumerable.Range(0, (int)m.RowCount).Select(r => m.GetCellText(r, ordinal))];

    private static Point Cell(Window w, MonitorGrid g, long row, int visibleCol)
    {
        var c = g.GridControl;
        var m = g.Model!;
        var x = c.RowHeaderWidth + m.VisibleColumns.Take(visibleCol).Sum(m.GetColumnWidth) + 12 - c.OffsetX;
        var y = c.HeaderHeight + (row * c.RowHeight) + (c.RowHeight / 2) - c.OffsetY;
        return c.TranslatePoint(new Point(x, y), w)!.Value;
    }

    [Fact]
    public Task Lock_list_sorts_ctime_as_numbers_and_keeps_sort_width_filter_and_session_after_a_refresh() => HeadlessHost.Run(async () =>
    {
        var h = new LockHarness();
        await h.Vm.StartAsync();
        var (w, view) = await ShowAsync(h.Vm, "LockTable");
        try
        {
            var g = view.FindControl<MonitorGrid>("LockTable")!;
            var m = g.Model!;
            Assert.True(m.IsRightAligned(6)); // ctime(초)는 숫자 열
            await m.SetLocalFilterAsync(m.LocalFilter.WithColumn(2, new ColumnFilter { Values = new HashSet<string> { "TX" } }));
            await m.ToggleSortAsync(6);
            m.SetColumnWidth(3, 240);
            Assert.Equal(["8", "22", "141", "203", "287", "287", "312"], Column(m, 6)); // 글자 순이면 141 이 22 앞에 온다
            Assert.True(m.IsEmphasized(0, 5)); // 요청 모드가 있는 칸은 위험색

            h.Vm.SelectedKey = FakeLockMonitor.W3;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, m.Active.Row);

            var before = g.Model;
            await h.Vm.RefreshAsync();
            await HeadlessHost.WaitUntil(() => !ReferenceEquals(g.Model, before));
            Dispatcher.UIThread.RunJobs();
            var n = g.Model!;
            Assert.Equal(6, n.SortColumn);
            Assert.Equal(240, n.GetColumnWidth(3));
            Assert.Equal(7, n.RowCount); // TX 만
            Assert.Equal(FakeLockMonitor.W3, h.Vm.SelectedKey);
            Assert.Equal(3, n.Active.Row);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Clicking_a_lock_row_picks_its_session_everywhere_and_Delete_asks_to_kill_it() => HeadlessHost.Run(async () =>
    {
        var h = new LockHarness { Answer = false };
        await h.Vm.StartAsync();
        var (w, view) = await ShowAsync(h.Vm, "LockTable");
        try
        {
            var g = view.FindControl<MonitorGrid>("LockTable")!;
            w.MouseDown(Cell(w, g, 6, 1), MouseButton.Left);
            w.MouseUp(Cell(w, g, 6, 1), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(FakeLockMonitor.H2, h.Vm.SelectedKey); // 7 번째 줄 = 2·0644 의 TM
            Assert.Same(h.Vm.TreeRows.First(t => t.Key == FakeLockMonitor.H2), view.FindControl<ListBox>("TreeList")!.SelectedItem);

            g.GridControl.Focus();
            w.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(FakeLockMonitor.H2, h.Asked?.Target);
            Assert.Empty(h.Monitor.Killed);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Deadlock_grid_sorts_by_time_and_Enter_opens_the_current_record_in_the_editor() => HeadlessHost.Run(async () =>
    {
        var h = new LockHarness();
        h.Vm.AlertEnabled = false;
        await h.Vm.StartAsync();
        h.Vm.TopTabIndex = LockMonitorViewModel.DeadlockTab;
        await h.Vm.LoadDeadlocksAsync();
        string? opened = null;
        h.Vm.OpenEditor = t => opened = t;
        var (w, view) = await ShowAsync(h.Vm, "DeadlockTable");
        try
        {
            var g = view.FindControl<MonitorGrid>("DeadlockTable")!;
            await g.Model!.ToggleSortAsync(0); // 시각 오름차순: 5 시간 전이 먼저
            Assert.Equal(["1", "2"], Column(g.Model, 1));
            g.Model.SelectCell(new GridCell(0, 0));
            g.GridControl.Focus();
            w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("MESPRD1_ora_40991", opened, StringComparison.Ordinal);

            var before = g.Model;
            await h.Vm.RefreshAsync(); // 데드락 탭이 앞이면 새로 고침이 기록도 다시 읽는다
            await HeadlessHost.WaitUntil(() => !ReferenceEquals(g.Model, before));
            Assert.Equal(0, g.Model!.SortColumn);
            Assert.Equal(["1", "2"], Column(g.Model, 1));
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Awr_history_grid_keeps_its_sort_and_picked_snapshot_after_a_reload_and_marks_slow_plans() => HeadlessHost.Run(async () =>
    {
        var h = await TuningHarness.StartAsync(pack: LicensePack.Diagnostics);
        h.Tuning.Awr = SqlMonitorTuningTests.AwrRows();
        h.Vm.DetailTabIndex = SqlMonitorViewModel.AwrTab;
        var view = new SqlMonitorView { DataContext = h.Vm };
        var w = new Window { Content = view, Width = 1180, Height = 760 };
        w.Show();
        try
        {
            var g = view.FindControl<MonitorGrid>("AwrList")!;
            await HeadlessHost.WaitUntil(() => g.Model is { RowCount: 4 });
            var m = g.Model!;
            Assert.Equal(["snap", "plan_hash", "실행", "경과/실행(ms)", "gets/실행"], m.Columns.Select(c => c.Name));
            await m.ToggleSortAsync(2); // 실행 오름차순
            Assert.Equal(["980", "1,120", "1,402", "1,611"], Column(m, 2));
            Assert.Equal("1838229974", m.GetCellText(2, 1)); // plan_hash 는 쉼표 없이
            Assert.True(m.IsWarned(2, 1)); // 느린 plan_hash
            Assert.False(m.IsWarned(0, 1));

            h.Vm.Awr.SelectedRow = h.Vm.Awr.Rows.First(r => r.Row.SnapId == 103);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, m.Active.Row);

            var before = g.Model;
            await h.Vm.Awr.LoadAsync();
            await HeadlessHost.WaitUntil(() => !ReferenceEquals(g.Model, before));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, g.Model!.SortColumn);
            Assert.Equal(2, g.Model.Active.Row);
            Assert.Equal(103, h.Vm.Awr.SelectedRow?.Row.SnapId); // 다시 읽어도 같은 snap·plan_hash 줄
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Plan_controls_grid_keeps_its_sort_after_a_reload() => HeadlessHost.Run(async () =>
    {
        var h = await TuningHarness.StartAsync();
        h.Tuning.Controls =
        [
            new SqlPlanControlRow(SqlPlanControlKind.Baseline, "SQL_PLAN_b", "ENABLED", new DateTime(2026, 8, 11), "SELECT 1 FROM dual") { Accepted = true },
            new SqlPlanControlRow(SqlPlanControlKind.Baseline, "SQL_PLAN_c", "ENABLED", new DateTime(2026, 10, 24), "SELECT 1 FROM dual") { Accepted = false },
            new SqlPlanControlRow(SqlPlanControlKind.Profile, "SYS_SQLPROF_a", "DISABLED", new DateTime(2026, 3, 2), "SELECT 1 FROM dual"),
        ];
        h.Vm.DetailTabIndex = SqlMonitorViewModel.ControlsTab;
        var view = new SqlMonitorView { DataContext = h.Vm };
        var w = new Window { Content = view, Width = 1180, Height = 760 };
        w.Show();
        try
        {
            var g = view.FindControl<MonitorGrid>("ControlsList")!;
            await HeadlessHost.WaitUntil(() => g.Model is { RowCount: 3 });
            await g.Model!.ToggleSortAsync(3); // 만든 때 오름차순
            Assert.Equal(["SYS_SQLPROF_a", "SQL_PLAN_b", "SQL_PLAN_c"], Column(g.Model, 1));
            Assert.True(g.Model.IsWarned(2, 2)); // 미승인

            var before = g.Model;
            await h.Vm.Controls.LoadAsync();
            await HeadlessHost.WaitUntil(() => !ReferenceEquals(g.Model, before));
            Assert.Equal(3, g.Model!.SortColumn);
            Assert.Equal(["SYS_SQLPROF_a", "SQL_PLAN_b", "SQL_PLAN_c"], Column(g.Model, 1));
        }
        finally
        {
            w.Close();
        }
    });
}
