using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Features.SqlMonitor;
using Oranda.App.Shell.Controls;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.SessionMonitor;

/// <summary>TSK-13-37: 세션 모니터·SQL 모니터의 표가 C안 격자(DetailTable)로 보이고, 선택 바인딩·새로 고침 뒤 선택 유지·Ctrl+C·오른쪽 클릭이 동작한다.</summary>
public class MonitorGridTests
{
    private static RawInputModifiers Primary
        => Application.Current!.PlatformSettings!.HotkeyConfiguration.CommandModifiers.HasFlag(KeyModifiers.Meta) ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static SessionItem Sess(int sid, string status, string user) => new(new SessionRow(new SessionKey(1, sid, sid * 10), status) { Username = user, Program = "SQLPLUS" });

    private static (Window Window, SessionMonitorView View, SessionMonitorViewModel Vm, DetailTable Table) OpenSessions()
    {
        var vm = new SessionMonitorViewModel();
        vm.Sessions.Add(Sess(10, "INACTIVE", "SCOTT"));
        vm.Sessions.Add(Sess(20, "ACTIVE", "SCOTT"));
        vm.Sessions.Add(Sess(30, "ACTIVE", "HR"));
        var view = new SessionMonitorView { DataContext = vm };
        var window = new Window { Content = view, Width = 1100, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm, view.FindControl<DetailTable>("SessionList")!);
    }

    private static ListBoxItem Item(DetailTable t, int index) => t.List.GetVisualDescendants().OfType<ListBoxItem>().ElementAt(index);

    private static void Click(Window w, DetailTable t, int index, MouseButton button = MouseButton.Left, RawInputModifiers mods = RawInputModifiers.None)
    {
        var item = Item(t, index);
        var p = item.TranslatePoint(new Point(40 + index, item.Bounds.Height / 2), w)!.Value;
        w.MouseDown(p, button, mods);
        w.MouseUp(p, button, mods);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public Task Session_grid_shows_headers_rows_and_a_status_line() => HeadlessHost.Run(() =>
    {
        var (w, _, _, t) = OpenSessions();
        try
        {
            Assert.Equal(["SID,SERIAL#", "사용자", "상태", "경과", "블로커", "대기 이벤트", "SQL ID", "프로그램", "델타"], t.Columns);
            Assert.Equal(3, t.List.GetVisualDescendants().OfType<ListBoxItem>().Count());
            Assert.Equal("3행", t.StatusText);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Clicking_a_row_selects_the_session_and_setting_it_from_code_moves_the_marker() => HeadlessHost.Run(() =>
    {
        var (w, _, vm, t) = OpenSessions();
        try
        {
            Click(w, t, 1);
            Assert.Same(vm.Sessions[1], vm.SelectedSession);
            Assert.Equal(1, t.CurrentRowIndex);

            vm.SelectedSession = vm.Sessions[2];
            Assert.Equal(2, t.CurrentRowIndex);
            Assert.Single(t.SelectedRows);

            vm.SelectedSession = null;
            Assert.Equal(-1, t.CurrentRowIndex);
            Assert.Empty(t.SelectedRows);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task A_selection_set_from_code_is_replaced_by_a_plain_click() => HeadlessHost.Run(() =>
    {
        var vm = new SessionMonitorViewModel();
        vm.Sessions.Add(Sess(10, "INACTIVE", "SCOTT"));
        vm.Sessions.Add(Sess(20, "ACTIVE", "SCOTT"));
        vm.Sessions.Add(Sess(30, "ACTIVE", "HR"));
        vm.SelectedSession = vm.Sessions[1];
        var view = new SessionMonitorView { DataContext = vm };
        var w = new Window { Content = view, Width = 1100, Height = 500 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var t = view.FindControl<DetailTable>("SessionList")!;
            Assert.Equal("3행 · 1개 선택", t.StatusText);
            Click(w, t, 0);
            Assert.Equal("3행 · 1개 선택", t.StatusText);
            Assert.Same(vm.Sessions[0], vm.SelectedSession);
            Assert.Equal(["SCOTT"], t.SelectedRows.Select(r => (string)r[1]!));
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Refreshing_the_list_keeps_the_picked_rows_and_the_current_session() => HeadlessHost.Run(() =>
    {
        var (w, _, vm, t) = OpenSessions();
        try
        {
            Click(w, t, 2);
            Click(w, t, 0, mods: Primary);
            Assert.Equal("3행 · 2개 선택", t.StatusText);
            // 자동 새로 고침이 하는 일: 목록을 비우고 새 항목으로 채운 뒤 같은 키의 항목을 다시 고른다. 맨 앞에 새 세션이 끼어든다.
            var key = vm.SelectedSession!.Row.Key;
            vm.Sessions.Clear();
            foreach (var s in new[] { Sess(5, "ACTIVE", "SYS"), Sess(10, "INACTIVE", "SCOTT"), Sess(20, "ACTIVE", "SCOTT"), Sess(30, "ACTIVE", "HR") })
            {
                vm.Sessions.Add(s);
            }

            vm.SelectedSession = vm.Sessions.First(s => s.Row.Key == key);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("4행 · 2개 선택", t.StatusText);
            Assert.Equal(key, vm.SelectedSession.Row.Key);
            Assert.Equal(["SCOTT", "HR"], t.SelectedRows.Select(r => (string)r[1]!));
            Assert.Equal(1, t.CurrentRowIndex);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Ctrl_C_and_copy_with_headers_put_the_picked_sessions_on_the_clipboard() => HeadlessHost.Run(async () =>
    {
        var (w, _, _, t) = OpenSessions();
        try
        {
            Click(w, t, 0);
            Click(w, t, 2, mods: Primary);
            t.List.Focus();
            w.KeyPress(Key.C, Primary, PhysicalKey.C, "c");
            await HeadlessHost.WaitUntil(() => true);
            Assert.Equal("10,100\tSCOTT\tINACTIVE\t\t\t\t\tSQLPLUS\t\n30,300\tHR\tACTIVE\t\t\t\t\tSQLPLUS\t\n", await w.Clipboard!.TryGetTextAsync());

            Assert.StartsWith("SID,SERIAL#\t사용자\t상태\t경과\t블로커\t대기 이벤트\tSQL ID\t프로그램\t델타\n", t.BuildTsv(withHeaders: true), StringComparison.Ordinal);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Right_click_on_an_unpicked_row_makes_it_the_selected_session_and_the_menu_gets_the_monitor_items() => HeadlessHost.Run(() =>
    {
        var (w, view, vm, t) = OpenSessions();
        try
        {
            Click(w, t, 0);
            Assert.NotNull(t.ExtraMenuItems);
            Assert.NotNull(t.List.ContextMenu);

            Click(w, t, 2, MouseButton.Right);
            Assert.Same(vm.Sessions[2], vm.SelectedSession);

            // 이미 고른 행 위에서 오른쪽 클릭하면 여러 행 선택을 깨지 않는다.
            Click(w, t, 1, mods: Primary);
            Click(w, t, 1, MouseButton.Right);
            Assert.Equal(2, t.SelectedRows.Count);
            Assert.NotNull(view);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Sql_grid_shows_headers_rows_and_binds_the_selected_sql() => HeadlessHost.Run(() =>
    {
        var vm = new SqlMonitorViewModel();
        vm.Results.Add(new TopSqlItem(new TopSqlRow(new CursorRef("sql1", 0, 1), "select 1 from dual") { Executions = 3, ElapsedSeconds = 2.5, BufferGets = 20_000, ParsingSchema = "SCOTT" }));
        vm.Results.Add(new TopSqlItem(new TopSqlRow(new CursorRef("sql2", 1, 1), "select 2 from emp") { Executions = 1, ElapsedSeconds = 1.2, BufferGets = 12_000, DiskReads = 5 }));
        var view = new SqlMonitorView { DataContext = vm };
        var w = new Window { Content = view, Width = 1100, Height = 600 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var t = view.FindControl<DetailTable>("ResultList")!;
            Assert.Equal(["SQL ID", "자식", "소요(s)", "CPU(s)", "실행", "읽은 블록", "디스크", "스키마", "SQL"], t.Columns);
            Assert.Equal("2행", t.StatusText);

            Click(w, t, 1);
            Assert.Same(vm.Results[1], vm.SelectedSql);

            // 새로 고침 뒤에도 같은 커서를 다시 고른다.
            var cursor = vm.SelectedSql!.Row.Cursor;
            vm.Results.Clear();
            vm.Results.Add(new TopSqlItem(new TopSqlRow(new CursorRef("sql2", 1, 1), "select 2 from emp") { ElapsedSeconds = 9 }));
            vm.Results.Add(new TopSqlItem(new TopSqlRow(new CursorRef("sql1", 0, 1), "select 1 from dual")));
            vm.SelectedSql = vm.Results.First(i => i.Row.Cursor == cursor);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, t.CurrentRowIndex);
            Assert.Equal("sql2", (string)t.SelectedRows.Single()[0]!);
            Assert.Equal("sql2\t1\t9.00\t0.00\t0\t0\t0\t\tselect 2 from emp\n", t.BuildTsv(withHeaders: false));
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
    public Task Session_monitor_shows_one_status_line_with_the_selection_count() => HeadlessHost.Run(() =>
    {
        var (w, view, vm, t) = OpenSessions();
        try
        {
            vm.StatusText = "세션 3개 · 대기 중 1개 · 21:05:12 갱신";
            var line = view.FindControl<TextBlock>("StatusLine")!;
            Assert.Equal("세션 3개 · 대기 중 1개 · 21:05:12 갱신", line.Text);
            Assert.DoesNotContain(t.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("panel-status") && b.IsEffectivelyVisible);
            Click(w, t, 0);
            Click(w, t, 2, mods: Primary);
            Assert.Equal("세션 3개 · 대기 중 1개 · 2개 선택 · 21:05:12 갱신", line.Text);
            vm.SelectedSession = null;
            Assert.Equal("세션 3개 · 대기 중 1개 · 21:05:12 갱신", line.Text);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Session_monitor_colours_the_blocker_cell_and_sets_ids_in_monospace() => HeadlessHost.Run(() =>
    {
        var vm = new SessionMonitorViewModel();
        vm.Sessions.Add(Sess(10, "INACTIVE", "SCOTT"));
        vm.Sessions.Add(new SessionItem(new SessionRow(new SessionKey(1, 20, 200), "ACTIVE") { Username = "HR", BlockingSession = new SessionKey(1, 10, 100), SqlId = "7ctxk2vq" }));
        var view = new SessionMonitorView { DataContext = vm };
        var w = new Window { Content = view, Width = 1100, Height = 500 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.True(Application.Current!.TryGetResource("OrandaDangerForegroundBrush", Application.Current.ActualThemeVariant, out var danger));
            Assert.True(Application.Current.TryGetResource("OrandaGridFontFamily", Application.Current.ActualThemeVariant, out var mono));
            var t = view.FindControl<DetailTable>("SessionList")!;
            var cells = Item(t, 1).GetVisualDescendants().OfType<TextBlock>().ToList();
            Assert.Equal(danger, cells.First(b => b.Text == "10").Foreground); // 블로커 칸
            Assert.Equal(mono, cells.First(b => b.Text == "20,200").FontFamily);
            Assert.Equal(mono, cells.First(b => b.Text == "7ctxk2vq").FontFamily);
            Assert.NotEqual(mono, cells.First(b => b.Text == "HR").FontFamily);
            // 블로커가 없는 행은 빨갛지 않다.
            Assert.DoesNotContain(Item(t, 0).GetVisualDescendants().OfType<TextBlock>(), b => Equals(b.Foreground, danger));
        }
        finally
        {
            w.Close();
        }
    });
}
