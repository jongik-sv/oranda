using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Shell.Menus;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell.Headless;

// macOS 시스템 메뉴 막대(NativeMenu): 창 안 Menu 와 같은 MenuModel 에서 만들어지는지, 앱 메뉴 항목, 활성·체크 갱신.
// 플랫폼과 무관하게 돌도록 macOS 분기는 ShellHarness(nativeMenu: true) 로 강제로 켠다.
public class NativeMenuTests
{
    private static readonly string[] TopTitles = ["파일", "편집", "보기", "SQL", "결과", "데이터베이스", "모니터", "도구", "창", "도움말"];

    private static IReadOnlyList<MenuNode> Model(ShellHarness h) =>
        MenuModelBuilder.BuildMenuBar(
            h.Get<ICommandRegistry>().Commands,
            h.Get<ShellController>().Text,
            h.Get<KeymapService>().ShortcutText,
            h.Get<IToolPanelRegistry>().Panels);

    private static NativeMenuItem FindCommandItem(NativeMenu menu, string commandId)
    {
        foreach (var item in menu.Items.OfType<NativeMenuItem>())
        {
            if (item.Command is NativeMenuCommand c && c.CommandId == commandId)
            {
                return item;
            }

            if (item.Menu is { } sub && TryFind(sub, commandId) is { } found)
            {
                return found;
            }
        }

        throw new Xunit.Sdk.XunitException($"시스템 메뉴에 명령 {commandId} 항목이 없다.");
    }

    private static NativeMenuItem? TryFind(NativeMenu menu, string commandId)
    {
        foreach (var item in menu.Items.OfType<NativeMenuItem>())
        {
            if (item.Command is NativeMenuCommand c && c.CommandId == commandId)
            {
                return item;
            }

            if (item.Menu is { } sub && TryFind(sub, commandId) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>키맵의 첫 단축키. 지금 문맥에서 키맵이 그 키를 다른 명령(또는 연속 키 첫 단)으로 보내면 메뉴는 키를 가져가지 않으므로 null.</summary>
    private static KeyGesture? ExpectedGesture(ShellHarness h, string commandId)
    {
        var table = h.Get<KeymapService>().Table;
        var g = table.SingleGesture(commandId);
        if (g is null)
        {
            return null;
        }

        var match = table.Find([(g.Key, g.KeyModifiers)], h.Get<ContextKeyService>());
        return match.Kind == KeyMatchKind.Match && match.CommandId == commandId ? g : null;
    }

    /// <summary>모델과 NativeMenu 트리를 같은 순서로 맞춰 본다(구분선·항목 수·글자·단축키).</summary>
    private static void AssertSameStructure(IReadOnlyList<MenuNode> nodes, NativeMenu menu, Func<string, KeyGesture?> gesture)
    {
        Assert.Equal(nodes.Count, menu.Items.Count);
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var item = menu.Items[i];
            switch (node.Kind)
            {
                case MenuNodeKind.Separator:
                    Assert.IsType<NativeMenuItemSeparator>(item);
                    break;
                case MenuNodeKind.Submenu:
                    var sub = Assert.IsType<NativeMenuItem>(item);
                    Assert.Equal(node.Title, sub.Header);
                    AssertSameStructure(node.Children, sub.Menu!, gesture);
                    break;
                case MenuNodeKind.PanelToggle:
                    var toggle = Assert.IsType<NativeMenuItem>(item);
                    Assert.Equal(node.Title, toggle.Header);
                    Assert.Equal(MenuItemToggleType.CheckBox, toggle.ToggleType);
                    break;
                default:
                    var cmd = Assert.IsType<NativeMenuItem>(item);
                    Assert.Equal(node.Title.Replace("_", "__", StringComparison.Ordinal), cmd.Header);
                    Assert.Equal(node.CommandId, Assert.IsType<NativeMenuCommand>(cmd.Command).CommandId);
                    Assert.Equal(gesture(node.CommandId!), cmd.Gesture);
                    break;
            }
        }
    }

    [Fact]
    public Task Native_menu_mirrors_the_menu_model_and_hides_the_in_window_menu() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness(nativeMenu: true);

        var native = h.Window.NativeMenuBar;
        Assert.NotNull(native);
        Assert.Equal(TopTitles, native.Items.OfType<NativeMenuItem>().Select(m => m.Header!));
        AssertSameStructure(Model(h), native, id => ExpectedGesture(h, id));

        // 창 안 메뉴는 보이지 않고 키보드 이동에서도 빠진다.
        Assert.False(h.Window.Menus.IsVisible);
        Assert.False(h.Window.Menus.Focusable);
        Assert.Null(h.Window.Menus.ItemsSource);
    });

    [Fact]
    public Task Rebuilding_menus_keeps_the_same_native_menu_instance() => HeadlessHost.Run(() =>
    {
        // Avalonia 는 창에 처음 붙은 NativeMenu 가 아닌 메뉴로는 갱신하지 않는다(ArgumentException). 다시 만들 때 항목만 갈아 끼운다.
        using var h = new ShellHarness(nativeMenu: true);
        var first = h.Window.NativeMenuBar;

        h.Window.RebuildMenus();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(first, h.Window.NativeMenuBar);
        Assert.Equal(TopTitles, h.Window.NativeMenuBar!.Items.OfType<NativeMenuItem>().Select(m => m.Header!));
    });

    [Fact]
    public Task Floating_dock_window_gets_its_own_native_menu_with_the_same_items() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness(nativeMenu: true);
        var ws = h.Get<DockWorkspace>();
        ws.OpenDocument("oranda.panel.sqlEditor");
        var b = ws.OpenDocument("oranda.panel.sqlEditor")!;
        Dispatcher.UIThread.RunJobs();
        var tab = h.Window.GetVisualDescendants().OfType<DocumentTabStripItem>().First(t => ReferenceEquals(t.DataContext, b));
        var center = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, tab.Bounds.Height / 2), h.Window)!.Value;
        for (var i = 0; i < 2; i++)
        {
            h.Window.MouseDown(center, MouseButton.Left);
            h.Window.MouseUp(center, MouseButton.Left);
        }

        await HeadlessHost.WaitUntil(() => ((IRootDock)ws.Layout!).Windows?.Count == 1);

        var host = (Window)((IRootDock)ws.Layout!).Windows![0].Host!;
        var floating = NativeMenu.GetMenu(host);
        Assert.NotNull(floating);
        Assert.NotSame(h.Window.NativeMenuBar, floating); // 창마다 자기 뿌리 메뉴 인스턴스
        Assert.Equal(TopTitles, floating.Items.OfType<NativeMenuItem>().Select(m => m.Header!));

        // 메뉴를 다시 만들어도 두 창의 인스턴스는 유지되고 항목은 같이 갱신된다.
        var mainRoot = h.Window.NativeMenuBar;
        h.Window.RebuildMenus();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(mainRoot, h.Window.NativeMenuBar);
        Assert.Same(floating, NativeMenu.GetMenu(host));
        AssertSameStructure(Model(h), floating, id => ExpectedGesture(h, id));
    });

    [Fact]
    public Task Native_shortcut_is_claimed_only_where_the_keymap_routes_the_key_to_that_command() => HeadlessHost.Run(() =>
    {
        // 편집 > 복사(편집기 포커스)와 결과 > 복사(그리드 포커스)는 같은 ⌘C 다. 메뉴가 엉뚱한 쪽을 가져가면 안 된다.
        using var h = new ShellHarness(nativeMenu: true);
        var keys = h.Get<ContextKeyService>();
        var table = h.Get<KeymapService>().Table;
        var editCopy = FindCommandItem(h.Window.NativeMenuBar!, "oranda.edit.copy");
        var editGesture = table.SingleGesture("oranda.edit.copy");
        Assert.NotNull(editGesture);

        keys.SetFocusSlot("sqlText");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(editGesture, editCopy.Gesture);

        keys.SetFocusSlot("sqlGrid");
        Dispatcher.UIThread.RunJobs();
        Assert.Null(editCopy.Gesture);
        if (TryFind(h.Window.NativeMenuBar!, "oranda.grid.copy") is { } gridCopy)
        {
            Assert.Equal(table.SingleGesture("oranda.grid.copy"), gridCopy.Gesture);
        }
    });

    [Fact]
    public Task Native_menu_has_no_access_key_markers() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness(nativeMenu: true);

        var top = h.Window.NativeMenuBar!.Items.OfType<NativeMenuItem>().Select(m => m.Header!).ToList();
        Assert.DoesNotContain(top, t => t.Contains("(_", StringComparison.Ordinal) || t.StartsWith('_'));
    });

    [Fact]
    public Task Windows_and_Linux_branch_keeps_the_in_window_menu() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();

        Assert.Null(h.Window.NativeMenuBar);
        Assert.True(h.Window.Menus.IsVisible);
        Assert.Equal(10, h.Window.Menus.Items.OfType<MenuItem>().Count());
    });

    [Fact]
    public Task Native_menu_enablement_follows_the_context_keys() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness(nativeMenu: true);
        var keys = h.Get<ContextKeyService>();
        var close = FindCommandItem(h.Window.NativeMenuBar!, "oranda.file.close");

        keys.Set("hasDocument", false);
        Dispatcher.UIThread.RunJobs();
        Assert.False(close.IsEnabled);

        keys.Set("hasDocument", true);
        Dispatcher.UIThread.RunJobs();
        Assert.True(close.IsEnabled);
    });

    [Fact]
    public Task Native_panel_toggle_checks_follow_the_workspace() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness(nativeMenu: true);
        var workspace = h.Get<ShellController>().Docking;
        var panelId = h.Get<IToolPanelRegistry>().Panels.Where(p => p.Kind == ToolPanelKind.Tool)
            .OrderBy(p => p.DefaultLocation).ThenBy(p => p.Order).ThenBy(p => p.Id, StringComparer.Ordinal).First().Id;

        // 도구 창 토글을 바꾸면 도킹이 다시 짜이며 메뉴도 다시 만들어지므로 항목은 매번 새로 찾는다.
        NativeMenuItem FirstToggle()
        {
            var view = h.Window.NativeMenuBar!.Items.OfType<NativeMenuItem>().Single(m => m.Header == "보기");
            var panels = view.Menu!.Items.OfType<NativeMenuItem>().Single(m => m.Header == "도구 창").Menu!;
            return panels.Items.OfType<NativeMenuItem>().First();
        }

        var toggle = FirstToggle();
        Assert.Equal(MenuItemToggleType.CheckBox, toggle.ToggleType);
        Assert.Equal(workspace.IsToolVisible(panelId), toggle.IsChecked);
        var before = toggle.IsChecked;

        ((INativeMenuItemExporterEventsImplBridge)toggle).RaiseClicked();
        Dispatcher.UIThread.RunJobs();

        var after = FirstToggle();
        Assert.Equal(workspace.IsToolVisible(panelId), after.IsChecked);
        Assert.NotEqual(before, after.IsChecked);
    });

    [Fact]
    public Task App_menu_has_about_update_and_settings_with_command_key() => HeadlessHost.Run(() =>
    {
        var text = new ShellText(language: "ko");
        var app = new MacAppMenu();
        app.Bind(text);

        var items = app.Menu.Items;
        Assert.Equal(4, items.Count);
        var about = Assert.IsType<NativeMenuItem>(items[0]);
        var update = Assert.IsType<NativeMenuItem>(items[1]); // 「Oranda 정보」 바로 아래(macOS 관례)
        Assert.IsType<NativeMenuItemSeparator>(items[2]);
        var settings = Assert.IsType<NativeMenuItem>(items[3]);
        Assert.Equal("Oranda 정보", about.Header);
        Assert.Equal("업데이트 확인…", update.Header);
        Assert.Equal("설정…", settings.Header);
        Assert.Equal(new KeyGesture(Key.OemComma, KeyModifiers.Meta), settings.Gesture);

        text.SetLanguage("en");
        Assert.Equal("About Oranda", about.Header);
        Assert.Equal("Check for Updates…", update.Header);
        Assert.Equal("Settings…", settings.Header);
    });

    [Fact]
    public Task App_menu_localizes_the_default_items_Avalonia_appends() => HeadlessHost.Run(() =>
    {
        // Avalonia 가 AfterSetup 에서 영어로 붙이는 기본 항목을 같은 모양으로 흉내 낸다.
        var app = new MacAppMenu();
        app.Menu.Add(new NativeMenuItemSeparator());
        var services = new NativeMenuItem("Services");
        var hide = new NativeMenuItem("Hide Oranda");
        var hideOthers = new NativeMenuItem("Hide Others") { Gesture = new KeyGesture(Key.Q, KeyModifiers.Meta | KeyModifiers.Alt) };
        var showAll = new NativeMenuItem("Show All");
        var quit = new NativeMenuItem("Quit") { Gesture = new KeyGesture(Key.Q, KeyModifiers.Meta) };
        foreach (var i in new[] { services, hide, hideOthers, showAll, quit })
        {
            app.Menu.Add(i);
        }

        var text = new ShellText(language: "ko");
        app.Bind(text);

        Assert.Equal(["서비스", "Oranda 가리기", "기타 가리기", "모두 보기", "Oranda 종료"], new[] { services, hide, hideOthers, showAll, quit }.Select(i => i.Header));
        Assert.Equal(new KeyGesture(Key.H, KeyModifiers.Meta | KeyModifiers.Alt), hideOthers.Gesture);
        Assert.Equal(new KeyGesture(Key.Q, KeyModifiers.Meta), quit.Gesture);

        text.SetLanguage("en");
        Assert.Equal(["Services", "Hide Oranda", "Hide Others", "Show All", "Quit Oranda"], new[] { services, hide, hideOthers, showAll, quit }.Select(i => i.Header));
    });

    [Fact]
    public Task App_menu_items_run_their_commands() => HeadlessHost.Run(() =>
    {
        var ran = new List<string>();
        var previous = ShellCommandRunner.Runner;
        ShellCommandRunner.Runner = id =>
        {
            ran.Add(id);
            return Task.CompletedTask;
        };
        try
        {
            var app = new MacAppMenu();
            ((INativeMenuItemExporterEventsImplBridge)app.Menu.Items[0]).RaiseClicked();
            ((INativeMenuItemExporterEventsImplBridge)app.Menu.Items[1]).RaiseClicked();
            ((INativeMenuItemExporterEventsImplBridge)app.Menu.Items[3]).RaiseClicked();
        }
        finally
        {
            ShellCommandRunner.Runner = previous;
        }

        Assert.Equal(["oranda.help.about", "oranda.help.checkUpdate", "oranda.tools.settings"], ran);
    });

    [Fact]
    public Task Application_name_is_Oranda() => HeadlessHost.Run(() =>
    {
        Assert.Equal("Oranda", Application.Current!.Name);
    });

    [Fact]
    public Task Text_editing_key_goes_to_the_focused_text_box_not_the_command() => HeadlessHost.Run(() =>
    {
        var box = new TextBox { Text = "abc" };
        Assert.True(NativeMenuCommand.TryEdit(box, new KeyGesture(Key.A, KeyModifiers.Meta)));
        Assert.Equal(0, box.SelectionStart);
        Assert.Equal(3, box.SelectionEnd);

        // 편집 키가 아닌 단축키(⇧⌘P)는 입력 칸이 가져가지 않는다.
        Assert.False(NativeMenuCommand.TryEdit(box, new KeyGesture(Key.P, KeyModifiers.Meta | KeyModifiers.Shift)));
    });
}
