using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Menus;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell.Headless;

/// <summary>
/// TSK-13-36: 앱과 같은 셸에서 주 메뉴의 숨김·회색 이유 툴팁·빈 메뉴 안내가 그려지는지, 문맥 키가 바뀌면 다시 계산되는지.
/// 오른쪽 클릭 메뉴는 그대로인지도 본다.
/// </summary>
public class MenuStateHeadlessTests
{
    private static readonly string[] DatabaseCommands = ["oranda.db.info", "oranda.db.ddlExtract", "oranda.schema.objectSearch", "oranda.schema.compileInvalid"];

    private static readonly string[] ContextSurfaces = ["monitor", "grid", "schema"];

    private static MenuItem Top(ShellHarness h, string id)
    {
        var index = MenuModelBuilder.TopMenus.ToList().IndexOf(id);
        return (MenuItem)h.Window.Menus.ItemsSource!.Cast<object>().ElementAt(index);
    }

    private static IEnumerable<object> Items(MenuItem menu) => menu.ItemsSource!.Cast<object>();

    private static IEnumerable<MenuItem> AllItems(MenuItem menu)
    {
        foreach (var item in Items(menu).OfType<MenuItem>())
        {
            yield return item;
            if (item.ItemsSource is not null)
            {
                foreach (var sub in AllItems(item))
                {
                    yield return sub;
                }
            }
        }
    }

    private static MenuItem Command(ShellHarness h, string top, string commandId) =>
        AllItems(Top(h, top)).Single(i => i.Command is RegistryCommand c && c.CommandId == commandId);

    private static List<string> VisibleCommandIds(MenuItem menu) =>
        [.. AllItems(menu).Where(i => i.IsVisible && i.Command is RegistryCommand).Select(i => ((RegistryCommand)i.Command!).CommandId)];

    private static MenuItem EmptyNotice(MenuItem menu) => Items(menu).OfType<MenuItem>().Single(i => i.Classes.Contains("menu-empty"));

    private static void Settle() => Dispatcher.UIThread.RunJobs();

    [Fact]
    public Task Monitor_menu_hides_tab_only_commands_until_a_monitor_is_open() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var keys = h.Get<ContextKeyService>();
        var monitor = Top(h, "monitor");
        Assert.Equal(["oranda.monitor.sessions", "oranda.monitor.locks"], VisibleCommandIds(monitor));
        Assert.All(Items(monitor).OfType<Separator>(), s => Assert.False(s.IsVisible));
        Assert.False(EmptyNotice(monitor).IsVisible);

        keys.Set("monitorSessionOpen", true);
        Settle();
        var kill = Command(h, "monitor", "oranda.monitor.killSession");
        Assert.True(kill.IsVisible);
        Assert.False(kill.Command!.CanExecute(null));
        Assert.Equal("세션 목록에서 행을 고르세요", ToolTip.GetTip(kill));
        Assert.True(ToolTip.GetShowOnDisabled(kill));
        var refresh = Command(h, "monitor", "oranda.monitor.refresh");
        Assert.True(refresh.Command!.CanExecute(null));
        Assert.Null(ToolTip.GetTip(refresh));

        // 행을 고르면 다음 이유(권한), 모니터를 닫으면 다시 숨는다.
        keys.Set("monitorSessionSelected", true);
        Settle();
        Assert.Equal("ALTER SYSTEM 권한이 필요합니다", ToolTip.GetTip(kill));
        keys.Set("monitorSessionOpen", false);
        keys.Set("monitorSessionSelected", false);
        Settle();
        Assert.False(kill.IsVisible);
    });

    [Fact]
    public Task Database_menu_items_are_gray_with_a_connect_tooltip_without_a_connection() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        foreach (var id in DatabaseCommands)
        {
            var item = Command(h, "database", id);
            Assert.True(item.IsVisible, id);
            Assert.False(item.Command!.CanExecute(null), id);
            Assert.Equal("DB 에 접속해야 쓸 수 있습니다", ToolTip.GetTip(item));
            Assert.True(ToolTip.GetShowOnDisabled(item));
        }

        // 접속하면 툴팁이 걷힌다(읽기 전용이면 컴파일만 이유가 바뀐다).
        var keys = h.Get<ContextKeyService>();
        keys.Set("hasSession", true);
        keys.Set("readOnlySession", true);
        Settle();
        Assert.Null(ToolTip.GetTip(Command(h, "database", "oranda.db.info")));
        Assert.Equal("읽기 전용 접속이나 조회 전용 Role 에서는 쓸 수 없습니다", ToolTip.GetTip(Command(h, "database", "oranda.schema.compileInvalid")));
    });

    [Fact]
    public Task Result_menu_shows_the_empty_notice_until_a_sql_tab_opens() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var wiring = h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
        await wiring.RunAsync(default);
        Settle();
        var result = Top(h, "result");
        var notice = EmptyNotice(result);
        if (h.Get<IContextKeys>().Evaluate("sqlDocActive"))
        {
            // 시작 때 SQL 탭이 이미 열렸으면 모두 닫고 본다.
            await h.Get<ICommandRegistry>().ExecuteAsync("oranda.file.closeAll", h.Get<IContextKeys>());
            Settle();
        }

        Assert.True(notice.IsVisible);
        Assert.False(notice.IsEnabled);
        Assert.Equal("(SQL 편집기 탭을 열면 쓸 수 있습니다)", notice.Header);
        Assert.Empty(VisibleCommandIds(result));

        await h.Get<ICommandRegistry>().ExecuteAsync("oranda.file.newSqlEditor", h.Get<IContextKeys>());
        Settle();
        Assert.False(notice.IsVisible);
        Assert.Contains("oranda.grid.exportExcel", VisibleCommandIds(result));
        Assert.Equal("쿼리 결과가 있어야 쓸 수 있습니다", ToolTip.GetTip(Command(h, "result", "oranda.grid.exportExcel")));
    });

    [Fact]
    public Task Native_menu_uses_the_same_hiding_and_reasons() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness(nativeMenu: true);
        var root = h.Window.NativeMenuBar!;
        NativeMenuItem Find(string id) => All(root).Single(i => i.Command is NativeMenuCommand c && c.CommandId == id);

        Assert.False(Find("oranda.monitor.killSession").IsVisible);
        Assert.True(Find("oranda.monitor.sessions").IsVisible);
        var info = Find("oranda.db.info");
        Assert.True(info.IsVisible);
        Assert.Equal("DB 에 접속해야 쓸 수 있습니다", info.ToolTip);

        h.Get<ContextKeyService>().Set("monitorSessionOpen", true);
        Settle();
        Assert.True(Find("oranda.monitor.killSession").IsVisible);
        Assert.Equal("세션 목록에서 행을 고르세요", Find("oranda.monitor.killSession").ToolTip);

        static IEnumerable<NativeMenuItem> All(NativeMenu menu)
        {
            foreach (var item in menu.Items.OfType<NativeMenuItem>())
            {
                yield return item;
                if (item.Menu is { } sub)
                {
                    foreach (var s in All(sub))
                    {
                        yield return s;
                    }
                }
            }
        }
    });

    [Fact]
    public Task Context_menus_still_show_every_item_with_no_reason_tooltip() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        foreach (var surface in ContextSurfaces)
        {
            var items = ShellContextMenus.Factory!(surface);
            Assert.NotEmpty(items);
            Assert.All(items.OfType<Control>(), c => Assert.True(c.IsVisible));
            Assert.All(items.OfType<MenuItem>(), m => Assert.Null(ToolTip.GetTip(m)));
        }
    });

    [Fact]
    public Task Edit_menu_shows_editor_commands_for_a_visible_plsql_tab_even_after_focus_moves_to_the_schema_tree() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var keys = h.Get<ContextKeyService>();
        Assert.DoesNotContain("oranda.edit.find", VisibleCommandIds(Top(h, "edit")));

        h.Get<Oranda.App.Shell.Docking.DockWorkspace>().OpenDocument("oranda.panel.plsqlEditor");
        keys.SetFocusSlot("schema"); // 작업 공간 라우터가 아직 없어 실제 plsqlDocActive 는 꺼진 채이고(주 메뉴는 도킹에서 직접 채운다), 포커스는 스키마 트리로 옮겨 갔다
        Settle();
        Top(h, "edit").IsSubMenuOpen = true;
        Settle();
        Assert.False(keys.Evaluate("plsqlDocActive"));
        Assert.Contains("oranda.edit.find", VisibleCommandIds(Top(h, "edit")));
        Top(h, "edit").IsSubMenuOpen = false;
        Settle();
    });

    [Fact]
    public Task Palette_still_lists_tab_only_commands_as_gray() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var palette = new Oranda.App.Shell.Palette.CommandPaletteViewModel(h.Get<ICommandRegistry>(), h.Get<IContextKeys>(), h.Get<IShellText>(), _ => null);
        var kill = Assert.Single(palette.Search(string.Empty), r => r.Id == "oranda.monitor.killSession");
        Assert.False(kill.IsEnabled);
    });
}
