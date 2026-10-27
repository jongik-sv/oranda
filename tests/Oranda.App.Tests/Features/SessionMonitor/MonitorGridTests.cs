using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Features.SqlMonitor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.SessionMonitor;

/// <summary>
/// TSK-14-15: 세션 모니터·SQL 모니터의 목록이 조회 결과와 같은 그리드(ResultGridControl)로 보이고, 머리글 정렬·열 끌기·너비·열 필터가 되며,
/// 자동 새로 고침(목록을 비우고 다시 채움) 뒤에도 정렬·열 순서·너비·필터·고른 세션(키)·스크롤 위치가 남는다. Ctrl+C·오른쪽 클릭·블로커 강조도 그대로.
/// </summary>
public class MonitorGridTests
{
    private static RawInputModifiers Primary
        => Application.Current!.PlatformSettings!.HotkeyConfiguration.CommandModifiers.HasFlag(KeyModifiers.Meta) ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static SessionItem Sess(int sid, string status, string user, long? elapsed = null) =>
        new(new SessionRow(new SessionKey(1, sid, sid * 10), status) { Username = user, Program = "SQLPLUS", LastCallElapsedSeconds = elapsed });

    private static SessionItem[] Three() => [Sess(10, "INACTIVE", "SCOTT", 300), Sess(20, "ACTIVE", "SCOTT", 5), Sess(30, "ACTIVE", "HR", 40)];

    private static async Task<(Window Window, SessionMonitorView View, SessionMonitorViewModel Vm, MonitorGrid Grid)> OpenSessionsAsync(params SessionItem[] items)
    {
        var vm = new SessionMonitorViewModel();
        foreach (var s in items.Length == 0 ? Three() : items)
        {
            vm.Sessions.Add(s);
        }

        var view = new SessionMonitorView { DataContext = vm };
        var window = new Window { Content = view, Width = 1100, Height = 500 };
        window.Show();
        var grid = view.FindControl<MonitorGrid>("SessionList")!;
        await HeadlessHost.WaitUntil(() => grid.Model is { } m && m.RowCount == vm.Sessions.Count && grid.GridControl.Bounds.Height > 0);
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm, grid);
    }

    /// <summary>자동 새로 고침이 하는 일: 목록을 비우고 새 항목으로 채운 뒤 같은 키의 항목을 다시 고른다. 새 결과로 바뀔 때까지 기다린다.</summary>
    private static async Task RefreshSessionsAsync(SessionMonitorViewModel vm, MonitorGrid grid, params SessionItem[] items)
    {
        var before = grid.Model;
        var key = vm.SelectedSession?.Row.Key;
        vm.Sessions.Clear();
        foreach (var s in items)
        {
            vm.Sessions.Add(s);
        }

        vm.SelectedSession = key is { } k ? vm.Sessions.FirstOrDefault(s => s.Row.Key == k) : null;
        await HeadlessHost.WaitUntil(() => !ReferenceEquals(grid.Model, before));
        Dispatcher.UIThread.RunJobs();
    }

    private static Point Cell(Window w, MonitorGrid g, long row, int visibleCol)
    {
        var c = g.GridControl;
        var m = g.Model!;
        var x = c.RowHeaderWidth + m.VisibleColumns.Take(visibleCol).Sum(m.GetColumnWidth) + 12 - c.OffsetX;
        var y = c.HeaderHeight + (row * c.RowHeight) + (c.RowHeight / 2) - c.OffsetY;
        return c.TranslatePoint(new Point(x, y), w)!.Value;
    }

    private static Point Header(Window w, MonitorGrid g, int visibleCol)
    {
        var c = g.GridControl;
        var m = g.Model!;
        var x = c.RowHeaderWidth + m.VisibleColumns.Take(visibleCol).Sum(m.GetColumnWidth) + 12;
        return c.TranslatePoint(new Point(x, c.HeaderHeight / 2), w)!.Value;
    }

    private static void Click(Window w, Point p, MouseButton button = MouseButton.Left, RawInputModifiers mods = RawInputModifiers.None)
    {
        w.MouseDown(p, button, mods);
        w.MouseUp(p, button, mods);
        Dispatcher.UIThread.RunJobs();
    }

    private static string[] Column(ResultGridModel m, int ordinal) => [.. Enumerable.Range(0, (int)m.RowCount).Select(r => m.GetCellText(r, ordinal))];

    [Fact]
    public Task Session_grid_is_the_result_grid_with_typed_columns() => HeadlessHost.Run(async () =>
    {
        var (w, _, _, g) = await OpenSessionsAsync();
        try
        {
            var m = g.Model!;
            Assert.Equal(["SID,SERIAL#", "사용자", "상태", "경과(초)", "블로커", "대기 이벤트", "SQL ID", "프로그램", "델타"], m.Columns.Select(c => c.Name));
            Assert.Equal(3, m.RowCount);
            Assert.Equal(["10,100", "20,200", "30,300"], Column(m, 0));
            Assert.True(m.IsRightAligned(3)); // 경과는 숫자 열
            Assert.Equal("", m.GetCellText(0, 4)); // NULL 은 빈 칸
            Assert.False(g.GridControl.IsCommandTarget); // 편집기 결과 명령의 대상이 되지 않는다
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Clicking_a_cell_selects_the_session_and_setting_it_from_code_moves_the_current_row() => HeadlessHost.Run(async () =>
    {
        var (w, _, vm, g) = await OpenSessionsAsync();
        try
        {
            Click(w, Cell(w, g, 1, 1));
            Assert.Same(vm.Sessions[1], vm.SelectedSession);
            Assert.Equal(1, g.Model!.Active.Row);

            vm.SelectedSession = vm.Sessions[2];
            Assert.Equal(2, g.Model.Active.Row);

            vm.SelectedSession = null;
            Assert.False(g.Model.HasSelection);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task A_selection_set_before_the_grid_appears_is_shown() => HeadlessHost.Run(async () =>
    {
        var vm = new SessionMonitorViewModel();
        foreach (var s in Three())
        {
            vm.Sessions.Add(s);
        }

        vm.SelectedSession = vm.Sessions[1];
        var view = new SessionMonitorView { DataContext = vm };
        var w = new Window { Content = view, Width = 1100, Height = 500 };
        w.Show();
        try
        {
            var g = view.FindControl<MonitorGrid>("SessionList")!;
            await HeadlessHost.WaitUntil(() => g.Model is { HasSelection: true });
            Assert.Equal(1, g.Model!.Active.Row);
            Assert.Same(vm.Sessions[1], vm.SelectedSession);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Header_click_sorts_numbers_as_numbers_and_the_sort_survives_a_refresh() => HeadlessHost.Run(async () =>
    {
        var (w, _, vm, g) = await OpenSessionsAsync();
        try
        {
            Click(w, Header(w, g, 3)); // 경과(초) 오름차순
            await HeadlessHost.WaitUntil(() => g.Model!.SortColumn == 3);
            Assert.Equal(["5", "40", "300"], Column(g.Model!, 3)); // 글자 순이면 300 이 40 앞에 온다

            Click(w, Cell(w, g, 2, 1)); // 300 초 세션(10,100)을 고른다
            Assert.Equal(10, vm.SelectedSession!.Row.Key.Sid);

            // 새 세션이 끼어들고 값이 바뀐 새로 고침: 정렬은 남고, 고른 세션은 키로 다시 찾아 새 자리로 따라간다.
            await RefreshSessionsAsync(vm, g, Sess(5, "ACTIVE", "SYS", 1000), Sess(10, "INACTIVE", "SCOTT", 20), Sess(20, "ACTIVE", "SCOTT", 6), Sess(30, "ACTIVE", "HR", 41));
            var m = g.Model!;
            Assert.Equal(3, m.SortColumn);
            Assert.False(m.SortDescending);
            Assert.Equal(["6", "20", "41", "1,000"], Column(m, 3));
            Assert.Equal(10, vm.SelectedSession!.Row.Key.Sid);
            Assert.Equal(1, m.Active.Row); // 20 초가 된 10,100 은 이제 둘째 줄
            Assert.Equal(1, m.Active.Col);

            Click(w, Header(w, g, 3)); // 내림차순
            await HeadlessHost.WaitUntil(() => g.Model!.SortDescending);
            await RefreshSessionsAsync(vm, g, Sess(10, "INACTIVE", "SCOTT", 20), Sess(20, "ACTIVE", "SCOTT", 6));
            Assert.True(g.Model!.SortDescending);
            Assert.Equal(["20", "6"], Column(g.Model, 3));
            Assert.Equal(0, g.Model.Active.Row);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Dragged_column_order_widths_and_filters_survive_a_refresh() => HeadlessHost.Run(async () =>
    {
        var (w, _, vm, g) = await OpenSessionsAsync();
        try
        {
            // 「상태」 머리글을 맨 앞(SID 앞)으로 끈다.
            var from = Header(w, g, 2);
            var to = Header(w, g, 0) - new Point(10, 0);
            w.MouseDown(from, MouseButton.Left);
            w.MouseMove(new Point(from.X - 30, from.Y));
            w.MouseMove(to);
            w.MouseUp(to, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([2, 0, 1, 3, 4, 5, 6, 7, 8], g.Model!.VisibleColumns);
            Assert.Equal(-1, g.Model.SortColumn); // 끌기는 정렬하지 않는다

            g.Model.SetColumnWidth(1, 222);
            g.Model.HideColumn(8);
            await g.Model.SetLocalFilterAsync(g.Model.LocalFilter.WithColumn(2, new ColumnFilter { Values = new HashSet<string> { "ACTIVE" } }));
            Assert.Equal(2, g.Model.RowCount);

            await RefreshSessionsAsync(vm, g, [.. Three(), Sess(40, "INACTIVE", "HR")]);
            var m = g.Model!;
            Assert.Equal([2, 0, 1, 3, 4, 5, 6, 7], m.VisibleColumns);
            Assert.Equal(222, m.GetColumnWidth(1));
            Assert.Equal(2, m.RowCount); // ACTIVE 만
            Assert.Equal(["20,200", "30,300"], Column(m, 0));
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Scroll_position_and_picked_rows_survive_a_refresh() => HeadlessHost.Run(async () =>
    {
        SessionItem[] Many() => [.. Enumerable.Range(1, 200).Select(i => Sess(i, "ACTIVE", "U" + i, i))];
        var (w, _, vm, g) = await OpenSessionsAsync(Many());
        try
        {
            g.GridControl.ScrollTo(0, g.GridControl.RowHeight * 100);
            var y = g.GridControl.OffsetY;
            Assert.True(y > 0);
            g.Model!.SetSelectedRows([(100, 101), (105, 105)], 105);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(106, vm.SelectedSession!.Row.Key.Sid);

            await RefreshSessionsAsync(vm, g, Many());
            Assert.Equal(y, g.GridControl.OffsetY);
            Assert.Equal([(100L, 101L), (105L, 105L)], g.Model!.SelectedRowRanges());
            Assert.Equal(105, g.Model.Active.Row);
            Assert.Equal(106, vm.SelectedSession!.Row.Key.Sid);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Ctrl_C_copies_the_picked_cells_and_the_menu_has_the_copy_and_monitor_items() => HeadlessHost.Run(async () =>
    {
        var (w, _, vm, g) = await OpenSessionsAsync();
        try
        {
            g.Model!.SetSelectedRows([(0, 0), (2, 2)], 2);
            g.GridControl.Focus();
            w.KeyPress(Key.C, Primary, PhysicalKey.C, "c");
            string? text = null;
            for (var i = 0; i < 100 && text is not { Length: > 0 }; i++)
            {
                await Task.Delay(20);
                text = await w.Clipboard!.TryGetTextAsync();
            }

            text = text!.ReplaceLineEndings("\n");
            Assert.Contains("10,100\tSCOTT\tINACTIVE\t300", text, StringComparison.Ordinal);
            Assert.Contains("30,300\tHR\tACTIVE\t40", text, StringComparison.Ordinal);

            Assert.NotNull(g.ExtraMenuItems);
            Assert.NotNull(g.GridControl.ContextMenu);
            var items = g.MenuItems().OfType<MenuItem>().ToList();
            Assert.All(items.Take(5), i => Assert.True(i.IsEnabled)); // 고른 칸이 있으면 복사 묶음이 켜진다
            var headers = items.Select(i => i.Header as string).ToList();
            Assert.Contains("복사", headers);
            Assert.Contains("머리글 포함 복사", headers);
            Assert.Contains("행 복사", headers);
            Assert.Contains("열 숨기기", headers);

            // 오른쪽 클릭: 고르지 않은 칸이면 그 세션이 선택된다.
            Click(w, Cell(w, g, 1, 2), MouseButton.Right);
            Assert.Same(vm.Sessions[1], vm.SelectedSession);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Blocker_cell_is_drawn_in_the_danger_colour_only_when_it_has_a_value() => HeadlessHost.Run(async () =>
    {
        var (w, _, _, g) = await OpenSessionsAsync(
            Sess(10, "INACTIVE", "SCOTT"),
            new SessionItem(new SessionRow(new SessionKey(1, 20, 200), "ACTIVE") { Username = "HR", BlockingSession = new SessionKey(1, 10, 100) }));
        try
        {
            var m = g.Model!;
            Assert.Equal("10", m.GetCellText(1, 4));
            Assert.True(m.IsEmphasized(1, 4));
            Assert.False(m.IsEmphasized(1, 1));
            Assert.True(m.IsNull(0, 4)); // 블로커가 없는 행은 값이 없어 칠하지 않는다
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Sql_grid_sorts_by_elapsed_and_keeps_the_sort_and_the_selected_cursor_after_a_search() => HeadlessHost.Run(async () =>
    {
        static TopSqlItem Top(string id, int child, double elapsed, long gets) =>
            new(new TopSqlRow(new CursorRef(id, child, 1), "select " + id) { Executions = 1, ElapsedSeconds = elapsed, BufferGets = gets });

        var vm = new SqlMonitorViewModel();
        vm.Results.Add(Top("sql1", 0, 2.5, 20_000));
        vm.Results.Add(Top("sql2", 1, 12.25, 900));
        vm.Results.Add(Top("sql3", 0, 3, 5));
        var view = new SqlMonitorView { DataContext = vm };
        var w = new Window { Content = view, Width = 1100, Height = 600 };
        w.Show();
        try
        {
            var g = view.FindControl<MonitorGrid>("ResultList")!;
            await HeadlessHost.WaitUntil(() => g.Model is { RowCount: 3 } && g.GridControl.Bounds.Height > 0);
            Dispatcher.UIThread.RunJobs();
            var m = g.Model!;
            Assert.Equal(["SQL ID", "자식", "소요(s)", "CPU(s)", "실행", "읽은 블록", "디스크", "스키마", "SQL"], m.Columns.Select(c => c.Name));
            Assert.Equal(["2.50", "12.25", "3.00"], Column(m, 2));
            Assert.Equal("20,000", m.GetCellText(0, 5));

            Click(w, Header(w, g, 2));
            await HeadlessHost.WaitUntil(() => g.Model!.SortColumn == 2);
            Assert.Equal(["sql1", "sql3", "sql2"], Column(g.Model!, 0));
            Click(w, Cell(w, g, 1, 0));
            Assert.Equal("sql3", vm.SelectedSql!.Row.Cursor.SqlId);

            // 다시 검색: 같은 커서(SQL_ID, CHILD#)를 키로 다시 고르고 정렬은 남는다.
            var before = g.Model;
            var cursor = vm.SelectedSql.Row.Cursor;
            vm.Results.Clear();
            vm.Results.Add(Top("sql3", 0, 30, 5));
            vm.Results.Add(Top("sql4", 0, 1, 5));
            vm.Results.Add(Top("sql1", 0, 2.5, 20_000));
            vm.SelectedSql = vm.Results.First(i => i.Row.Cursor == cursor);
            await HeadlessHost.WaitUntil(() => !ReferenceEquals(g.Model, before));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, g.Model!.SortColumn);
            Assert.Equal(["sql4", "sql1", "sql3"], Column(g.Model, 0));
            Assert.Equal(2, g.Model.Active.Row);
            Assert.Same(vm.Results[0], vm.SelectedSql);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public void ComposeStatus_puts_the_selection_before_the_last_part_or_at_the_end()
    {
        Assert.Equal("세션 6개 · 대기 중 1개 · 2개 선택 · 21:05:12 갱신", MonitorStatusLine.Compose("세션 6개 · 대기 중 1개 · 21:05:12 갱신", 2));
        Assert.Equal("세션 6개 · 대기 중 1개 · 21:05:12 갱신", MonitorStatusLine.Compose("세션 6개 · 대기 중 1개 · 21:05:12 갱신", 0));
        Assert.Equal("취소했습니다. · 1개 선택", MonitorStatusLine.Compose("취소했습니다.", 1));
        Assert.Equal(" · 3개 선택", MonitorStatusLine.Compose("", 3));
    }

    [Fact]
    public Task Session_monitor_shows_one_status_line_with_the_picked_row_count() => HeadlessHost.Run(async () =>
    {
        var (w, view, vm, g) = await OpenSessionsAsync();
        try
        {
            vm.StatusText = "세션 3개 · 대기 중 1개 · 21:05:12 갱신";
            var line = view.FindControl<TextBlock>("StatusLine")!;
            Assert.Equal("세션 3개 · 대기 중 1개 · 21:05:12 갱신", line.Text);
            Click(w, Cell(w, g, 0, 1));
            Click(w, Cell(w, g, 2, 1), mods: RawInputModifiers.Shift);
            Assert.Equal("세션 3개 · 대기 중 1개 · 3개 선택 · 21:05:12 갱신", line.Text);
            vm.SelectedSession = null;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("세션 3개 · 대기 중 1개 · 21:05:12 갱신", line.Text);
        }
        finally
        {
            w.Close();
        }
    });
}
