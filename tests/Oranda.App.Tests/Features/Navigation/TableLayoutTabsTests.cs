using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Navigation;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.Navigation;

/// <summary>테이블 레이아웃 창의 탭: 열기·이동, 마지막 포커스 창, 탭 분리·합치기와 상태 유지, Esc, 편집기 탭 닫힘, 끌기·화면 중앙 판정.</summary>
public class TableLayoutTabsTests
{
    private static readonly TableLayoutRow[] Rows =
    [
        new(1, "ID", "NUMBER", true, null, null, true),
        new(2, "NAME", "VARCHAR2(30)", true, null, null, false),
        new(3, "MEMO", "VARCHAR2(200)", false, null, null, false),
    ];

    private static readonly Guid ConnA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ConnB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static SqlEditorViewModel Editor(Guid? connection = null)
    {
        var tab = new SqlEditorViewModel(Guid.NewGuid(), "select ") { ConnectionProfileId = connection ?? ConnA };
        tab.UpdateCaret(7, 7, 0);
        return tab;
    }

    private static TableLayoutSession Session(SqlEditorViewModel tab, string title)
        => new(tab, new TableLayoutModel(title, Rows), new TableLayoutOptions("a", false, false, true, false));

    private static void CloseAll(TableLayoutWindows hub)
    {
        foreach (var w in hub.Windows.ToList())
        {
            w.Close();
        }

        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public Task Second_table_opens_as_a_new_tab_in_the_same_window() => HeadlessHost.Run(() =>
    {
        var hub = new TableLayoutWindows();
        var tab = Editor();
        try
        {
            var first = hub.Open(Session(tab, "SCOTT.A"), null, null);
            var second = hub.Open(Session(tab, "SCOTT.B"), null, null);
            var window = Assert.Single(hub.Windows);
            Assert.Equal([first, second], window.Views);
            Assert.Same(second, window.Current);
            Assert.Equal("SCOTT.B", window.Title);
        }
        finally
        {
            CloseAll(hub);
        }
    });

    [Fact]
    public Task Same_table_goes_to_its_tab_and_retargets_the_insert_to_the_new_editor_tab() => HeadlessHost.Run(() =>
    {
        var hub = new TableLayoutWindows();
        var editorA = Editor();
        var editorB = Editor();
        try
        {
            var a = hub.Open(Session(editorA, "SCOTT.A"), null, null);
            hub.Open(Session(editorA, "SCOTT.B"), null, null);
            a.FilterBox.Text = "na";
            Dispatcher.UIThread.RunJobs();

            var again = hub.Open(Session(editorB, "SCOTT.A"), null, null);
            var window = Assert.Single(hub.Windows);
            Assert.Same(a, again);
            Assert.Equal(2, window.Views.Count);
            Assert.Same(a, window.Current);
            Assert.Same(editorB, a.Session.Tab);
            Assert.Equal("na", a.FilterBox.Text); // 탭 상태는 그대로
        }
        finally
        {
            CloseAll(hub);
        }
    });

    [Fact]
    public Task Same_name_on_another_connection_is_a_different_tab() => HeadlessHost.Run(() =>
    {
        var hub = new TableLayoutWindows();
        try
        {
            hub.Open(Session(Editor(ConnA), "SCOTT.A"), null, null);
            hub.Open(Session(Editor(ConnB), "SCOTT.A"), null, null);
            Assert.Equal(2, Assert.Single(hub.Windows).Views.Count);
        }
        finally
        {
            CloseAll(hub);
        }
    });

    [Fact]
    public Task New_table_joins_the_window_that_had_focus_last() => HeadlessHost.Run(() =>
    {
        var hub = new TableLayoutWindows();
        var tab = Editor();
        try
        {
            hub.Open(Session(tab, "SCOTT.A"), null, null);
            var first = hub.Windows[0];
            var moved = hub.Open(Session(tab, "SCOTT.B"), null, null);
            var second = first.SplitOff(moved, new PixelPoint(300, 300));
            Assert.NotNull(second);
            Assert.Equal(2, hub.Windows.Count);

            hub.Touch(first);
            var third = hub.Open(Session(tab, "SCOTT.C"), null, null);
            Assert.Equal(2, first.Views.Count);
            Assert.Contains(third, first.Views);
            Assert.Single(second!.Views);

            hub.Touch(second);
            var fourth = hub.Open(Session(tab, "SCOTT.D"), null, null);
            Assert.Contains(fourth, second.Views);
        }
        finally
        {
            CloseAll(hub);
        }
    });

    [Fact]
    public Task Tab_state_survives_splitting_off_and_merging_back() => HeadlessHost.Run(() =>
    {
        var hub = new TableLayoutWindows();
        var tab = Editor();
        try
        {
            var a = hub.Open(Session(tab, "SCOTT.A"), null, null);
            var b = hub.Open(Session(tab, "SCOTT.B"), null, null);
            var home = hub.Windows[0];
            a.FilterBox.Text = "mem";
            Dispatcher.UIThread.RunJobs();
            a.Select(a.VisibleRows);
            var picked = a.SelectedRows.Select(r => r.Name).ToArray();
            Assert.Equal(["MEMO"], picked);

            var split = home.SplitOff(a, new PixelPoint(400, 200));
            Assert.NotNull(split);
            Assert.Equal([b], home.Views);
            Assert.Equal([a], split!.Views);
            Assert.Equal("SCOTT.A", split.Title);
            Assert.Equal("mem", a.FilterBox.Text);
            Assert.Equal(picked, a.SelectedRows.Select(r => r.Name).ToArray());

            split.MoveViewTo(a, home, 0);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([a, b], home.Views);
            Assert.Same(a, home.Current);
            Assert.Equal("mem", a.FilterBox.Text);
            Assert.Equal(picked, a.SelectedRows.Select(r => r.Name).ToArray());
            Assert.Single(hub.Windows); // 탭을 모두 내준 창은 닫힌다
        }
        finally
        {
            CloseAll(hub);
        }
    });

    [Fact]
    public Task Splitting_the_only_tab_is_refused() => HeadlessHost.Run(() =>
    {
        var hub = new TableLayoutWindows();
        try
        {
            var only = hub.Open(Session(Editor(), "SCOTT.A"), null, null);
            Assert.Null(hub.Windows[0].SplitOff(only));
            Assert.Equal([only], hub.Windows[0].Views);
        }
        finally
        {
            CloseAll(hub);
        }
    });

    [Fact]
    public Task Escape_closes_only_the_current_tab_and_the_last_tab_closes_the_window() => HeadlessHost.Run(() =>
    {
        var hub = new TableLayoutWindows();
        var tab = Editor();
        try
        {
            var a = hub.Open(Session(tab, "SCOTT.A"), null, null);
            hub.Open(Session(tab, "SCOTT.B"), null, null);
            var window = hub.Windows[0];

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([a], window.Views);
            Assert.Equal("SCOTT.A", window.Title);
            Assert.Single(hub.Windows);

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(hub.Windows);
        }
        finally
        {
            CloseAll(hub);
        }
    });

    [Fact]
    public Task Closing_the_editor_tab_disables_insert_but_keeps_copy() => HeadlessHost.Run(async () =>
    {
        var hub = new TableLayoutWindows();
        var host = new SqlEditorHost();
        var editor = host.NewDocument("select ");
        editor.UpdateCaret(7, 7, 0);
        var other = host.NewDocument("x");
        try
        {
            var view = hub.Open(Session(editor, "SCOTT.A"), null, host);
            view.Select(view.VisibleRows);
            Assert.True(view.InsertButton.IsEnabled);

            Assert.True(await host.CloseAsync(editor, CancellationToken.None));
            Dispatcher.UIThread.RunJobs();

            Assert.True(view.Session.IsTargetClosed);
            Assert.False(view.InsertButton.IsEnabled);
            Assert.False(view.PasteSelection());
            Assert.False(view.PasteDoubleClicked(Rows[0]));
            Assert.StartsWith(TableLayoutSession.TargetClosedNotice, view.StatusText, StringComparison.Ordinal);
            view.Select([Rows[1]]); // 다음 행을 골라 상태 줄이 새로 쓰여도 안내는 남는다
            Assert.StartsWith(TableLayoutSession.TargetClosedNotice, view.StatusText, StringComparison.Ordinal);
            Assert.NotEmpty(view.Session.TextFor(view.SelectedRows)); // 복사 글은 그대로 만들어진다

            // 같은 표를 다른 편집기 탭에서 다시 열면 넣기가 되살아난다.
            hub.Open(Session(other, "SCOTT.A"), null, host);
            Assert.False(view.Session.IsTargetClosed);
            Assert.True(view.InsertButton.IsEnabled);
        }
        finally
        {
            CloseAll(hub);
        }
    });

    [Fact]
    public Task The_host_subscription_is_dropped_when_the_last_window_closes() => HeadlessHost.Run(async () =>
    {
        var hub = new TableLayoutWindows();
        var host = new SqlEditorHost();
        var editor = host.NewDocument("select ");
        var view = hub.Open(Session(editor, "SCOTT.A"), null, host);
        hub.Windows[0].Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(hub.Windows);

        Assert.True(await host.CloseAsync(editor, CancellationToken.None));
        Assert.False(view.Session.IsTargetClosed); // 창이 없으니 구독도 없다
    });

    [Fact]
    public void First_window_is_centered_on_the_work_area_of_the_apps_monitor()
    {
        var second = new PixelRect(1920, 40, 2560, 1400);
        Assert.Equal(new PixelPoint(1920 + ((2560 - 1520) / 2), 40 + ((1400 - 900) / 2)), TableLayoutDrag.Centered(second, new PixelSize(1520, 900)));
        Assert.Equal(new PixelPoint(0, 25), TableLayoutDrag.Centered(new PixelRect(0, 25, 800, 600), new PixelSize(1000, 700))); // 작업 영역보다 크면 모서리에 붙인다
    }

    private static TabFrame Frame(int x, int y, int tabs = 2)
        => new(
            new PixelRect(x, y, 800, 600),
            new PixelRect(x, y + 30, 800, 26),
            Enumerable.Range(0, tabs).Select(i => new PixelRect(x + (i * 100), y + 30, 100, 26)).ToList());

    [Fact]
    public void Drag_needs_a_threshold_and_drops_on_a_strip_reorder_or_merge()
    {
        Assert.False(TableLayoutDrag.Exceeds(new PixelPoint(10, 10), new PixelPoint(14, 13), 6));
        Assert.True(TableLayoutDrag.Exceeds(new PixelPoint(10, 10), new PixelPoint(10, 16), 6));

        var frames = new[] { Frame(0, 0), Frame(1000, 0, 3) };
        Assert.Equal(new TabDrop(TabDropKind.Reorder, 0, 2), TableLayoutDrag.Decide(new PixelPoint(190, 40), frames, 0, onlyTab: false));
        Assert.Equal(new TabDrop(TabDropKind.Merge, 1, 1), TableLayoutDrag.Decide(new PixelPoint(1130, 40), frames, 0, onlyTab: false));
        Assert.Equal(new TabDrop(TabDropKind.Merge, 1, 3), TableLayoutDrag.Decide(new PixelPoint(1700, 40), frames, 0, onlyTab: false));
        Assert.Equal(1, TableLayoutDrag.ReorderedIndex(0, 2));
        Assert.Equal(0, TableLayoutDrag.ReorderedIndex(1, 0));
    }

    [Fact]
    public void Dropping_outside_every_window_detaches_or_moves_the_window_and_inside_does_nothing()
    {
        var frames = new[] { Frame(0, 0), Frame(1000, 0) };
        Assert.Equal(TabDropKind.Stay, TableLayoutDrag.Decide(new PixelPoint(300, 300), frames, 0, onlyTab: false).Kind);
        Assert.Equal(TabDropKind.Detach, TableLayoutDrag.Decide(new PixelPoint(900, 900), frames, 0, onlyTab: false).Kind);
        Assert.Equal(TabDropKind.MoveWindow, TableLayoutDrag.Decide(new PixelPoint(900, 900), frames, 0, onlyTab: true).Kind);
        Assert.Equal(new PixelPoint(860, 880), TableLayoutDrag.Dropped(new PixelPoint(900, 900), new PixelPoint(40, 20)));
    }

    [Fact]
    public void A_window_on_top_hides_the_tab_strip_of_the_window_under_it()
    {
        // 위 창(0번)이 아래 창(1번)의 탭 줄을 덮는다: 위 창 본문에 놓으면 아래 창에 합쳐지지 않는다.
        var top = new TabFrame(new PixelRect(0, 0, 800, 600), new PixelRect(0, 30, 800, 26), [new PixelRect(0, 30, 100, 26)]);
        var under = new TabFrame(new PixelRect(-20, -20, 800, 600), new PixelRect(-20, 10, 800, 26), [new PixelRect(-20, 10, 100, 26)]);
        Assert.Equal(TabDropKind.Stay, TableLayoutDrag.Decide(new PixelPoint(300, 15), [top, under], 0, onlyTab: false).Kind);
        Assert.Equal(new TabDrop(TabDropKind.Merge, 1, 0), TableLayoutDrag.Decide(new PixelPoint(-10, 20), [top, under], 0, onlyTab: false)); // 위 창 밖의 아래 창 탭 줄
    }

    [Fact]
    public Task Frame_strip_matches_the_header_screen_position_and_split_windows_keep_the_owner() => HeadlessHost.Run(() =>
    {
        var hub = new TableLayoutWindows();
        var owner = new Window { Width = 900, Height = 700 };
        owner.Show();
        Dispatcher.UIThread.RunJobs();
        var tab = Editor();
        try
        {
            hub.Open(Session(tab, "SCOTT.A"), owner, null);
            var moved = hub.Open(Session(tab, "SCOTT.B"), owner, null);
            var home = hub.Windows[0];
            home.UpdateLayout();
            var frame = home.Frame();
            Assert.Equal(2, frame.Tabs.Count);
            Assert.True(frame.Window.Contains(frame.Strip.TopLeft));
            Assert.True(frame.Strip.Contains(frame.Tabs[0].TopLeft));
            Assert.True(frame.Tabs[1].X >= frame.Tabs[0].Right - 1);
            Assert.Equal(frame.Strip.Y, frame.Tabs[0].Y);

            var split = home.SplitOff(moved, new PixelPoint(200, 200));
            Assert.Same(owner, split!.Owner);
        }
        finally
        {
            CloseAll(hub);
            owner.Close();
        }
    });

    [Fact]
    public Task Reopening_a_table_refreshes_columns_comma_and_audit_but_keeps_filter_alias_and_selection() => HeadlessHost.Run(() =>
    {
        var hub = new TableLayoutWindows();
        var tab = Editor();
        try
        {
            var first = Session(tab, "SCOTT.A");
            var view = hub.Open(first, null, null);
            view.AliasBox.Text = "zz";
            view.FilterBox.Text = "na";
            Dispatcher.UIThread.RunJobs();
            view.Select([Rows[1]]);

            TableLayoutRow[] fresh = [Rows[0], Rows[1], new(3, "NATION", "VARCHAR2(2)", false, null, null, false)];
            var reopened = new TableLayoutSession(tab, new TableLayoutModel("SCOTT.A", fresh), new TableLayoutOptions("a", false, false, true, false) { Comma = Oranda.Core.Formatting.CommaPosition.Leading, AuditColumns = "NATION" });
            Assert.Same(view, hub.Open(reopened, null, null));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(["NAME", "NATION"], view.VisibleRows.Select(r => r.Name).ToArray());
            Assert.Equal(["NAME"], view.SelectedRows.Select(r => r.Name).ToArray());
            Assert.Equal("na", view.FilterBox.Text);
            Assert.Equal("zz", view.Session.Options.Alias);
            Assert.Equal(Oranda.Core.Formatting.CommaPosition.Leading, view.Session.Options.Comma);
            Assert.Equal("NATION", view.Session.Options.AuditColumns);
            Assert.True(view.AuditBox.IsEnabled);
        }
        finally
        {
            CloseAll(hub);
        }
    });

    [Fact]
    public void Dropping_on_the_body_of_another_window_merges_at_the_end_and_inside_the_own_window_does_nothing()
    {
        var frames = new[] { Frame(0, 0), Frame(1000, 0, 3) };
        Assert.Equal(new TabDrop(TabDropKind.Merge, 1, 3), TableLayoutDrag.Decide(new PixelPoint(1400, 400), frames, 0, onlyTab: false));
        Assert.Equal(new TabDrop(TabDropKind.Merge, 1, 3), TableLayoutDrag.Decide(new PixelPoint(1400, 10), frames, 0, onlyTab: false)); // 제목 줄
        Assert.Equal(TabDropKind.Stay, TableLayoutDrag.Decide(new PixelPoint(400, 400), frames, 0, onlyTab: false).Kind);
    }

    [Fact]
    public Task A_minimized_last_window_is_restored_when_a_table_is_added_to_it() => HeadlessHost.Run(() =>
    {
        var hub = new TableLayoutWindows();
        var tab = Editor();
        try
        {
            hub.Open(Session(tab, "SCOTT.A"), null, null);
            var window = hub.Windows[0];
            window.WindowState = WindowState.Minimized;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(WindowState.Minimized, window.WindowState);

            hub.Open(Session(tab, "SCOTT.B"), null, null);
            Assert.NotEqual(WindowState.Minimized, window.WindowState);

            window.WindowState = WindowState.Minimized;
            hub.Open(Session(tab, "SCOTT.A"), null, null); // 이미 열린 표로 가는 경우도 같다
            Assert.NotEqual(WindowState.Minimized, window.WindowState);
        }
        finally
        {
            CloseAll(hub);
        }
    });

    [Fact]
    public Task Closing_the_main_window_closes_the_split_off_windows_too() => HeadlessHost.Run(() =>
    {
        var hub = new TableLayoutWindows();
        var owner = new Window { Width = 900, Height = 700 };
        owner.Show();
        var tab = Editor();
        try
        {
            hub.Open(Session(tab, "SCOTT.A"), owner, null);
            var b = hub.Open(Session(tab, "SCOTT.B"), owner, null);
            Assert.NotNull(hub.Windows[0].SplitOff(b, new PixelPoint(200, 200)));
            Assert.Equal(2, hub.Windows.Count);

            owner.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(hub.Windows);
        }
        finally
        {
            CloseAll(hub);
        }
    });

    [Fact]
    public Task Activating_a_window_makes_it_the_last_focused() => HeadlessHost.Run(() =>
    {
        var hub = new TableLayoutWindows();
        var tab = Editor();
        try
        {
            hub.Open(Session(tab, "SCOTT.A"), null, null);
            var home = hub.Windows[0];
            var split = home.SplitOff(hub.Open(Session(tab, "SCOTT.B"), null, null), new PixelPoint(300, 300))!;
            Assert.Same(split, hub.LastActive);

            home.Activate();
            Dispatcher.UIThread.RunJobs();
            Assert.Same(home, hub.LastActive);
        }
        finally
        {
            CloseAll(hub);
        }
    });

    [Fact]
    public void Detached_windows_stay_inside_the_work_area_so_the_title_bar_is_reachable()
    {
        var area = new PixelRect(0, 25, 1920, 1055);
        var size = new PixelSize(760, 700);
        Assert.Equal(new PixelPoint(300, 25), TableLayoutDrag.ClampToArea(new PixelPoint(300, -40), size, area)); // 위로 나가지 않는다
        Assert.Equal(new PixelPoint(1920 - 80, 25 + 1055 - 80), TableLayoutDrag.ClampToArea(new PixelPoint(5000, 5000), size, area));
        Assert.Equal(new PixelPoint(-760 + 80, 100), TableLayoutDrag.ClampToArea(new PixelPoint(-4000, 100), size, area));
        Assert.Equal(new PixelPoint(400, 300), TableLayoutDrag.ClampToArea(new PixelPoint(400, 300), size, area));
    }

    [Fact]
    public void The_connection_that_read_the_table_is_the_key_even_if_the_editor_tab_changes_it_later()
    {
        var tab = Editor(ConnA);
        var session = new TableLayoutSession(tab, new TableLayoutModel("SCOTT.A", Rows), new TableLayoutOptions("a", false, false, true, false), connectionId: ConnA);
        tab.ConnectionProfileId = ConnB;
        Assert.Equal((ConnA, "SCOTT.A"), (session.Key.Connection, session.Key.Title));
    }

    [Fact]
    public Task Releasing_another_mouse_button_while_dragging_a_tab_does_not_split_it_off() => HeadlessHost.Run(() =>
    {
        var hub = new TableLayoutWindows();
        var tab = Editor();
        try
        {
            hub.Open(Session(tab, "SCOTT.A"), null, null);
            hub.Open(Session(tab, "SCOTT.B"), null, null);
            var home = hub.Windows[0];
            home.UpdateLayout();
            var header = home.GetVisualDescendants().OfType<Border>().First(b => b.Tag is TableLayoutView v && v.TabTitle == "SCOTT.B");
            var start = header.TranslatePoint(new Point(10, 8), home)!.Value;
            home.MouseDown(start, MouseButton.Left);
            home.MouseMove(new Point(start.X + 40, start.Y + 3000));
            home.MouseUp(new Point(start.X + 40, start.Y + 3000), MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(hub.Windows);
            Assert.Equal(2, home.Views.Count);
        }
        finally
        {
            CloseAll(hub);
        }
    });

    [Fact]
    public Task Reopening_after_the_audit_list_was_emptied_does_not_overwrite_the_saved_hide_audit_setting() => HeadlessHost.Run(() =>
    {
        var hub = new TableLayoutWindows();
        var tab = Editor();
        var saved = new List<Oranda.Core.Settings.TableLayoutSettings>();
        try
        {
            var first = new TableLayoutSession(tab, new TableLayoutModel("SCOTT.A", Rows), new TableLayoutOptions("a", false, false, true, false) { HideAudit = true, AuditColumns = "MEMO" }, saved.Add);
            var view = hub.Open(first, null, null);
            Assert.True(view.AuditBox.IsChecked);

            // 설정 창에서 감사 컬럼 목록을 비운 뒤 같은 표를 다시 연다(확인란은 목록이 없어 꺼지지만 저장된 설정은 건드리지 않는다).
            hub.Open(new TableLayoutSession(tab, new TableLayoutModel("SCOTT.A", Rows), new TableLayoutOptions("a", false, false, true, false) { HideAudit = true, AuditColumns = string.Empty }), null, null);
            Dispatcher.UIThread.RunJobs();

            Assert.False(view.AuditBox.IsEnabled);
            Assert.Empty(saved);
            Assert.True(view.Session.Options.HideAudit);
        }
        finally
        {
            CloseAll(hub);
        }
    });
}
