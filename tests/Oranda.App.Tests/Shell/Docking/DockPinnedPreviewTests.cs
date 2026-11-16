using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Model.Controls;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Layout;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Shell.Docking;

// DK-01(2026-11-16 사용자 신고): 핀으로 접은 도구 창을 사이드바에서 펼친 채 다른 조작을 하면 창이 사라지거나 보기 메뉴 체크가 어긋났다.
// Dock 은 펼친 동안 창 Owner 를 미리보기 칸(배치 트리 밖)으로 옮긴다 — 그동안에도 원래 묶음의 핀 창으로 세고, 배치를 다시 짓기 전에 펼침을 닫는다.
public class DockPinnedPreviewTests
{
    private const string Journal = "oranda.panel.changeJournal";
    private const string Messages = "oranda.panel.messages";
    private const string Results = "oranda.panel.results";

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static IRootDock Root(DockWorkspace ws) => (IRootDock)ws.Layout!;

    private static bool IsPinned(DockWorkspace ws, string id) =>
        new[] { Root(ws).LeftPinnedDockables, Root(ws).RightPinnedDockables, Root(ws).TopPinnedDockables, Root(ws).BottomPinnedDockables }
            .Any(l => l?.Any(d => d.Id == id) == true);

    private static bool IsPreviewing(DockWorkspace ws, string id) => Root(ws).PinnedDock?.VisibleDockables?.Any(d => d.Id == id) == true;

    /// <summary>결과·메시지·변경 일지를 아래 묶음에 두고 SQL 문서를 연 뒤 변경 일지에 핀을 꽂는다(사이드바로 접힘).</summary>
    private static (DockWorkspace Ws, PanelDocument Sql, PanelDocument Detail) PinJournal(ShellHarness h)
    {
        var ws = h.Get<DockWorkspace>();
        ws.ShowTool(Messages);
        ws.ShowTool(Results);
        ws.ShowTool(Journal);
        var sql = ws.OpenDocument("oranda.panel.sqlEditor")!;
        var detail = ws.OpenDocument("oranda.panel.objectDetail")!;
        ws.Activate(sql);
        Pump();
        ws.Factory.PinDockable((PanelTool)ws.Factory.FindDockable(ws.Layout!, d => d.Id == Journal)!);
        Pump();
        Assert.True(IsPinned(ws, Journal));
        return (ws, sql, detail);
    }

    /// <summary>사이드바의 변경 일지 탭(그 창을 DataContext 로 가진 보이는 가장 바깥 컨트롤)을 마우스로 눌러 펼친다.</summary>
    private static void ClickSidebarTab(Window window)
    {
        var tab = window.GetVisualDescendants().OfType<Control>()
            .First(c => c.DataContext is PanelTool { Id: Journal } && c.IsEffectivelyVisible && c.Bounds.Width > 0
                        && c.GetVisualParent() is Control { DataContext: not PanelTool { Id: Journal } });
        var p = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, tab.Bounds.Height / 2), window)!.Value;
        window.MouseDown(p, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(p, MouseButton.Left, RawInputModifiers.None);
        Pump();
    }

    [Fact]
    public Task Rebuilding_the_layout_while_a_pinned_tool_is_slid_out_keeps_it_pinned() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var (ws, _, _) = PinJournal(h);
        ClickSidebarTab(h.Window);
        Assert.True(IsPreviewing(ws, Journal));
        Assert.True(ws.IsToolVisible(Journal)); // 펼친 동안에도 보기 메뉴 체크

        Assert.True(ws.HideTool(Messages)); // 보기 메뉴로 다른 창 끄기·켜기(창 하나만 움직인다)
        Assert.True(ws.ShowTool("oranda.panel.sessions"));
        Pump();
        Assert.True(IsPinned(ws, Journal));
        Assert.True(ws.IsToolVisible(Journal));

        ws.Rebuild(); // 배치 다시 짓기(새 묶음이 필요하거나 묶음이 빌 때의 길)
        Pump();

        Assert.True(IsPinned(ws, Journal)); // 전에는 묶음·사이드바 어디에도 없이 닫힌 창이 되었다
        Assert.False(IsPreviewing(ws, Journal));
        Assert.True(ws.IsToolVisible(Journal));
        Assert.Contains(Journal, ws.Capture().PinnedTools);
        var tool = (PanelTool)Root(ws).BottomPinnedDockables!.First(d => d.Id == Journal);
        Assert.Null(tool.OriginalOwner); // 짓기 전에 접었다: 옛 묶음을 가리키는 값이 남지 않는다

        // 다시 펼쳤다 접고 또 지어도 핀 창 그대로(옛 묶음 값이 남으면 접을 때 묶음을 잃고 다음 짓기에서 닫힌 창이 되었다)
        ClickSidebarTab(h.Window);
        Assert.True(IsPreviewing(ws, Journal));
        ws.Factory.HidePreviewingDockables(Root(ws));
        Pump();
        Assert.True(ws.IsToolVisible(Journal));
        ws.Rebuild();
        Pump();
        Assert.True(IsPinned(ws, Journal));
        Assert.True(ws.IsToolVisible(Journal));
    });

    [Fact]
    public Task A_slid_out_pinned_tool_stays_checked_and_is_saved_as_pinned() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var (ws, sql, detail) = PinJournal(h);
        Assert.True(ws.ShowTool(Journal)); // 보기 메뉴·기능이 핀 창을 부름 = 사이드바에서 펼침
        Assert.True(IsPreviewing(ws, Journal));

        ws.Activate(detail); // 바깥을 누르지 않고 문서 탭만 바꿈(펼침이 남는다)
        Pump();
        ws.Activate(sql);
        Pump();

        Assert.True(ws.IsToolVisible(Journal));
        var saved = ws.Capture(); // 이 상태로 앱을 닫으면 저장되는 배치
        Assert.Contains(Journal, saved.PinnedTools);
        Assert.DoesNotContain(Journal, saved.HiddenTools);

        using var next = new ShellHarness(); // 다음 실행: 핀으로 접힌 채 돌아온다
        var ws2 = next.Get<DockWorkspace>();
        ws2.Apply(saved);
        Pump();
        Assert.True(IsPinned(ws2, Journal));
        Assert.True(ws2.IsToolVisible(Journal));
    });

    [Fact]
    public Task Closing_a_slid_out_pinned_tool_from_the_view_menu_closes_it() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var (ws, _, _) = PinJournal(h);
        ClickSidebarTab(h.Window);

        Assert.False(ws.ToggleTool(Journal)); // 체크가 켜져 있으니 끄기
        Pump();

        Assert.False(ws.IsToolVisible(Journal));
        Assert.False(IsPinned(ws, Journal));
        Assert.False(IsPreviewing(ws, Journal));
        Assert.True(ws.ToggleTool(Journal)); // 다시 켜면 묶음에 보인다
        Pump();
        Assert.True(ws.IsToolVisible(Journal));
    });

    [Fact]
    public Task A_saved_pin_whose_window_is_missing_from_the_layout_comes_back_pinned_at_its_default_place() => HeadlessHost.Run(() =>
    {
        LayoutDocument saved;
        using (var first = new ShellHarness())
        {
            var (ws0, _, _) = PinJournal(first);
            saved = ws0.Capture();
        }

        // 망가진 배치: 핀 목록에는 있는데 트리에서는 빠졌고 닫힌 창 목록에도 없다(보기 메뉴로는 켜짐인데 어디에도 없는 모양).
        static DockNode Without(DockNode n, string id) => n with
        {
            Tools = [.. n.Tools.Where(t => t != id)],
            ActiveTool = n.ActiveTool == id ? n.Tools.FirstOrDefault(t => t != id) : n.ActiveTool,
            Children = [.. n.Children.Select(c => Without(c, id))],
        };
        var broken = saved with { Root = Without(saved.Root!, Journal), HiddenTools = [.. (saved.HiddenTools ?? []).Where(t => t != Journal)] };
        Assert.DoesNotContain(Journal, LayoutPlanner.ToolIds(broken.Root!));
        Assert.Contains(Journal, broken.PinnedTools!);

        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        ws.Apply(broken);
        Pump();

        Assert.True(ws.IsToolVisible(Journal));
        Assert.True(IsPinned(ws, Journal)); // 기본 위치 묶음에 들어가 다시 사이드바로 접힌다(어디에도 없는 채로 두지 않는다)
    });

    [Fact]
    public Task Turning_a_tool_window_on_and_off_moves_only_that_window() => HeadlessHost.Run(() =>
    {
        // 「도구 창을 켜면 왜 화면 전체가 깜박이지?」(2026-11-16): 켜기·끄기는 그 창 하나만 넣고 뺀다. 문서 칸·다른 도구 창 컨트롤은 그대로다.
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        ws.ShowTool(Messages);
        ws.ShowTool(Results);
        var sql = ws.OpenDocument("oranda.panel.sqlEditor")!;
        ws.Activate(sql);
        Pump();
        Assert.False(ws.IsToolVisible(Journal));
        var layout = ws.Layout;
        var rebuilt = 0;
        ws.LayoutRebuilt += (_, _) => rebuilt++;
        Control? ViewOf(object model) => h.Window.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => ReferenceEquals(c.DataContext, model) && c.GetType().Name is "ToolDockControl" or "DocumentDockControl");
        var results = (PanelTool)ws.Factory.FindDockable(ws.Layout!, d => d.Id == Results)!;
        var group = results.Owner!;
        var documents = sql.Owner!;
        var groupView = ViewOf(group);
        var documentView = ViewOf(documents);
        Assert.NotNull(groupView);
        Assert.NotNull(documentView);

        Assert.True(ws.ToggleTool(Journal)); // 켜기: 아래 묶음에 탭 하나가 붙는다
        Pump();
        Assert.True(ws.IsToolVisible(Journal));
        Assert.Same(group, ws.Factory.FindDockable(ws.Layout!, d => d.Id == Journal)!.Owner);
        Assert.Equal(Journal, ((Dock.Model.Core.IDock)group).ActiveDockable?.Id);

        Assert.False(ws.ToggleTool(Journal)); // 끄기: 그 탭만 빠진다
        Pump();
        Assert.False(ws.IsToolVisible(Journal));
        Assert.True(ws.IsToolVisible(Results));

        Assert.Same(layout, ws.Layout);
        Assert.Equal(0, rebuilt);
        Assert.Same(groupView, ViewOf(group));
        Assert.Same(documentView, ViewOf(documents));
        Assert.Contains(Journal, ws.Capture().HiddenTools); // 저장도 닫힘
    });

    /// <summary>보기 메뉴의 「변경 일지」 체크 항목(창 안 메뉴 막대).</summary>
    private static MenuItem ViewMenuToggle(ShellHarness h, out MenuItem view)
    {
        var index = Oranda.App.Shell.Menus.MenuModelBuilder.TopMenus.ToList().IndexOf("view");
        view = (MenuItem)h.Window.Menus.ItemsSource!.Cast<object>().ElementAt(index);
        static IEnumerable<MenuItem> All(MenuItem m) =>
            (m.ItemsSource?.Cast<object>() ?? []).OfType<MenuItem>().SelectMany(i => new[] { i }.Concat(All(i)));
        return All(view).Single(i => i.ToggleType == MenuItemToggleType.CheckBox && Equals(i.Header, "변경 일지"));
    }

    [Fact]
    public Task Closing_a_floating_window_unchecks_its_tools_and_turning_one_on_shows_it_again() => HeadlessHost.Run(() =>
    {
        // 「탭 창을 분리해서 다 닫아도 보기 > 도구 창에는 모두 체크된 상태」·「그 상태로 켜면 체크는 되는데 열리지 않음」(2026-11-16)
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        ws.ShowTool(Messages);
        ws.ShowTool(Results);
        ws.ShowTool(Journal);
        Pump();
        var item = ViewMenuToggle(h, out var view);
        Assert.True(item.IsChecked);

        ws.Factory.FloatDockable((PanelTool)ws.Factory.FindDockable(ws.Layout!, d => d.Id == Journal)!); // 탭을 떼어 내 뜬 창으로
        Pump();
        var floating = Root(ws).Windows!.Single();
        ((Window)floating.Host!).Close(); // 뜬 창의 닫기 단추
        Pump();

        Assert.False(ws.IsToolVisible(Journal));
        Assert.Contains(Journal, ws.Capture().HiddenTools);
        view.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.SubmenuOpenedEvent, view)); // 보기 메뉴를 연다
        Assert.False(item.IsChecked); // 메뉴를 다시 만들지 않아도 실제 상태를 따른다

        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent, item)); // 다시 켠다
        Pump();
        Assert.True(ws.IsToolVisible(Journal));
        Assert.True(item.IsChecked);
        var tool = ws.Factory.FindDockable(ws.Layout!, d => d.Id == Journal)!;
        Assert.True(((Dock.Model.Core.IDock)tool.Owner!).VisibleDockables!.Contains(tool)); // 메인 배치의 묶음에 실제로 붙었다
        Assert.Empty(Root(ws).Windows ?? []);
    });

    [Fact]
    public Task A_tool_closed_with_its_tab_button_comes_back_clean_from_the_view_menu() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        ws.ShowTool(Messages);
        ws.ShowTool(Results);
        ws.ShowTool(Journal);
        Pump();
        var tool = (PanelTool)ws.Factory.FindDockable(ws.Layout!, d => d.Id == Journal)!;
        var layout = ws.Layout;

        ws.Factory.CloseDockable(tool); // 탭의 × (도구 창은 Dock 이 숨김 목록으로 옮긴다)
        Pump();
        Assert.False(ws.IsToolVisible(Journal));

        Assert.True(ws.ToggleTool(Journal)); // 보기 메뉴로 다시 켠다: 창 하나만 넣는다
        Pump();
        Assert.Same(layout, ws.Layout);
        Assert.True(ws.IsToolVisible(Journal));
        Assert.Null(tool.OriginalOwner); // 옛 자리가 남으면 뒤의 핀·펼침이 옛 묶음을 쓴다
        Assert.DoesNotContain(tool, Root(ws).HiddenDockables ?? []);
    });

    [Fact]
    public Task Closing_the_last_tab_of_a_group_still_rebuilds_the_layout() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        Assert.True(ws.ShowTool("oranda.panel.binds"));
        Pump();
        var group = (Dock.Model.Core.IDock)ws.Factory.FindDockable(ws.Layout!, d => d.Id == "oranda.panel.binds")!.Owner!;
        var rebuilt = 0;
        ws.LayoutRebuilt += (_, _) => rebuilt++;
        foreach (var id in group.VisibleDockables!.OfType<PanelTool>().Select(t => t.Id).ToList())
        {
            Assert.True(ws.HideTool(id));
            Pump();
        }

        Assert.True(rebuilt > 0); // 묶음이 비면 칸을 접어야 하므로 다시 짓는다
        Assert.False(ws.IsToolVisible("oranda.panel.binds"));
    });

    [Fact]
    public Task Removing_one_tab_keeps_the_saved_order_of_temporarily_hidden_tabs() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        ws.ShowTool(Messages);
        ws.ShowTool(Results);
        var sql = ws.OpenDocument("oranda.panel.sqlEditor")!;
        ws.Activate(sql);
        Pump();
        static List<string> GroupOf(LayoutDocument d, string id)
        {
            static IEnumerable<DockNode> Groups(DockNode n) => n.Kind == DockNodeKind.ToolGroup ? [n] : n.Children.SelectMany(Groups);
            return [.. Groups(d.Root!).Single(g => g.Tools.Contains(id)).Tools];
        }

        var before = GroupOf(ws.Capture(), Messages); // MyBatis 창처럼 SQL 문서에서 잠시 숨긴 창도 제자리에 적힌다
        Assert.Contains("oranda.panel.mybatisCombos", before);
        var first = before[0];
        Assert.True(ws.HideTool(first));
        Pump();

        Assert.Equal(before.Skip(1).ToList(), GroupOf(ws.Capture(), Messages)); // 숨긴 창이 한 칸 밀려 저장되지 않는다
    });
}
