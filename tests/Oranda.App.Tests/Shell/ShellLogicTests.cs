using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Layout;
using Oranda.App.Shell.Menus;
using Oranda.Core.Composition;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell;

// TSK-03-01: 화면 없이 도는 셸 로직(문맥 키·서비스 컨테이너·메뉴 모델·창 위치·레이아웃 계획).
public class ShellLogicTests
{
    // ---- ContextKeyService ----

    [Fact]
    public void Context_expressions_follow_the_documented_grammar()
    {
        var keys = new ContextKeyService();
        keys.Set("a", true);
        keys.Set("b", false);
        keys.Set("c", true);

        Assert.True(keys.Evaluate(null));
        Assert.True(keys.Evaluate(""));
        Assert.True(keys.Evaluate("a"));
        Assert.False(keys.Evaluate("b"));
        Assert.False(keys.Evaluate("unknown"));
        Assert.True(keys.Evaluate("!b"));
        Assert.True(keys.Evaluate("a && c"));
        Assert.False(keys.Evaluate("a && b"));
        Assert.True(keys.Evaluate("b || c"));
        Assert.True(keys.Evaluate("(a || b) && !b"));
        Assert.False(keys.Evaluate("!(a || b)"));
        Assert.True(keys.Evaluate("b || a && c"));
    }

    [Fact]
    public void Focus_slot_makes_exactly_the_matching_focus_keys_true()
    {
        var keys = new ContextKeyService();
        keys.SetFocusSlot("sqlText");

        Assert.True(keys.Evaluate("sqlEditorFocus"));
        Assert.True(keys.Evaluate("sqlDocFocus"));
        Assert.True(keys.Evaluate("editorFocus"));
        Assert.False(keys.Evaluate("gridFocus"));
        Assert.False(keys.Evaluate("schemaFocus"));

        keys.SetFocusSlot("sqlGrid");
        Assert.False(keys.Evaluate("sqlEditorFocus"));
        Assert.True(keys.Evaluate("gridFocus"));
        Assert.True(keys.Evaluate("sqlDocFocus"));
    }

    [Fact]
    public void Changing_a_key_raises_Changed_only_when_the_value_changes()
    {
        var keys = new ContextKeyService();
        var count = 0;
        keys.Changed += (_, _) => count++;

        keys.Set("hasDocument", true);
        keys.Set("hasDocument", true);
        keys.Set("hasDocument", false);

        Assert.Equal(2, count);
    }

    [Fact]
    public void Dev_build_sets_devBuild_key()
    {
        Assert.True(new ContextKeyService(devBuild: true).Evaluate("devBuild"));
        Assert.False(new ContextKeyService().Evaluate("devBuild"));
    }

    // ---- ServiceContainer ----

    public interface IThing;

    public sealed class Thing : IThing;

    public sealed class Consumer(IThing thing, IEnumerable<IListed> listed)
    {
        public IThing Thing { get; } = thing;

        public IReadOnlyList<IListed> Listed { get; } = [.. listed];
    }

    public interface IListed;

    public interface IListedProbe;

    public sealed class ListedA : IListed;

    public sealed class ListedB : IListed;

    [Fact]
    public void Container_injects_constructor_arguments_and_lists()
    {
        var c = new ServiceContainer();
        c.AddType(typeof(IThing), typeof(Thing));
        c.AddType(typeof(IListed), typeof(ListedA));
        c.AddType(typeof(IListed), typeof(ListedB));

        var consumer = (Consumer)c.Create(typeof(Consumer));

        Assert.IsType<Thing>(consumer.Thing);
        Assert.Equal(2, consumer.Listed.Count);
    }

    [Fact]
    public void Container_singleton_is_shared_and_transient_is_not()
    {
        var c = new ServiceContainer();
        c.AddType(typeof(IThing), typeof(Thing));
        c.AddType(typeof(IListed), typeof(ListedA), ServiceLifetimeKind.Transient);

        Assert.Same(c.GetRequiredService<IThing>(), c.GetRequiredService<IThing>());
        Assert.NotSame(c.GetRequiredService<IListed>(), c.GetRequiredService<IListed>());
    }

    [Fact]
    public void Container_returns_empty_list_when_nothing_is_registered()
    {
        var c = new ServiceContainer();
        Assert.Empty(c.GetRequiredService<IEnumerable<IListed>>());
    }

    [Fact]
    public void Container_does_not_scan_deferred_assemblies_until_an_unknown_service_is_requested()
    {
        var c = new ServiceContainer();
        c.AddType(typeof(IThing), typeof(Thing));
        c.AddDeferredAssembly("Oranda.Language");

        _ = c.GetRequiredService<IThing>();
        _ = c.GetService<IListedProbe>();

        // IListed 는 모르는 형식이라 지연 어셈블리를 훑는다(찾지 못하면 null).
        Assert.True(c.DeferredScanned);
    }

    [Fact]
    public void GetRegisteredOrDefault_does_not_scan_deferred_assemblies_and_sees_late_registrations()
    {
        var c = new ServiceContainer();
        c.AddDeferredAssembly("Oranda.Language");

        // 등록 안 된 형식은 null 이고 지연 어셈블리를 깨우지 않는다(GetService 는 깨운다).
        Assert.Null(c.GetRegisteredOrDefault<IThing>());
        Assert.False(c.DeferredScanned, c.DeferredScanTrigger);

        // 나중에 등록되면 바로 보인다(패널이 접속 뒤 처음 만들어져도 지금 접속을 따르는 길).
        var thing = new Thing();
        c.AddInstance<IThing>(thing);
        Assert.Same(thing, c.GetRegisteredOrDefault<IThing>());
        Assert.False(c.DeferredScanned);
    }

    [Fact]
    public void Container_does_not_scan_deferred_assemblies_for_framework_types()
    {
        var c = new ServiceContainer();
        c.AddDeferredAssembly("Oranda.Language");

        Assert.Null(c.GetService<TimeProvider>());
        Assert.False(c.DeferredScanned, c.DeferredScanTrigger);
    }

    [Fact]
    public void Starting_the_composition_does_not_scan_deferred_assemblies()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-comp-" + Guid.NewGuid().ToString("N"));
        using var c = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir });

        // 시작 경로에서 쓰는 서비스를 모두 만들어 본다.
        _ = c.GetRequiredService<ShellController>();
        _ = c.GetRequiredService<Oranda.Core.Storage.ISettingsStore>();
        _ = c.GetRequiredService<ICommandRegistry>();
        _ = c.GetRequiredService<IToolPanelRegistry>();

        Assert.False(c.DeferredScanned, c.DeferredScanTrigger);
    }

    [Fact]
    public void Composition_registers_platform_services_from_attributes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-comp-" + Guid.NewGuid().ToString("N"));
        using var c = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir });

        var paths = c.GetRequiredService<Oranda.Core.Storage.IAppDataPaths>();

        Assert.Equal(Path.GetFullPath(dir), paths.DataDirectory);
        Assert.NotNull(c.GetRequiredService<Oranda.Core.Storage.ISettingsStore>());
    }

    [Fact]
    public void Composition_registers_exactly_one_credential_store_for_this_os()
    {
        // OS 비밀 저장소가 없는 OS(Linux)도 대체 저장소가 있어야 접속 명령이 죽지 않는다(CI integration 은 Linux 에서 돈다).
        var dir = Path.Combine(Path.GetTempPath(), "oranda-comp-" + Guid.NewGuid().ToString("N"));
        using var c = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir });

        var store = Assert.Single(c.GetRequiredService<IEnumerable<Oranda.Core.Security.ICredentialStore>>());
        var expected = OperatingSystem.IsWindows() ? "DpapiCredentialStore" : OperatingSystem.IsMacOS() ? "KeychainCredentialStore" : "VolatileCredentialStore";
        Assert.Equal(expected, store.GetType().Name);
        Assert.Same(store, c.GetRequiredService<Oranda.Core.Security.ICredentialStore>());
    }

    // ---- 메뉴 모델 ----

    [Theory]
    [InlineData("menu:sql/run@10", "menu", new[] { "sql", "run" }, 10)]
    [InlineData("toolbar:main/run@150", "toolbar", new[] { "main", "run" }, 150)]
    [InlineData("context:grid/copy", "context", new[] { "grid", "copy" }, 1000)]
    [InlineData("menu:file/recent/open@5", "menu", new[] { "file", "recent", "open" }, 5)]
    public void Placement_parses(string text, string surface, string[] path, int order)
    {
        Assert.True(Placement.TryParse(text, out var p));
        Assert.Equal(surface, p.Surface);
        Assert.Equal(path, p.Path);
        Assert.Equal(order, p.Order);
    }

    [Theory]
    [InlineData("")]
    [InlineData("menu")]
    [InlineData("menu:")]
    [InlineData("popup:file/x@1")]
    [InlineData("menu:file/x@abc")]
    public void Placement_rejects_malformed_text(string text)
    {
        Assert.False(Placement.TryParse(text, out _));
    }

    private static CommandDefinition Cmd(string id, CommandCategory cat, params string[] placements) =>
        new() { Id = id, Title = "Cmd." + id, Category = cat, Placements = placements };

    [Fact]
    public void Menu_bar_has_eleven_menus_and_orders_items_with_separators_between_groups()
    {
        var commands = new[]
        {
            Cmd("oranda.x.b", CommandCategory.Sql, "menu:sql/b@120"),
            Cmd("oranda.x.a", CommandCategory.Sql, "menu:sql/a@10"),
            Cmd("oranda.x.c", CommandCategory.Sql, "menu:sql/c@20"),
            Cmd("oranda.x.sub1", CommandCategory.File, "menu:file/recent/one@10"),
            Cmd("oranda.x.sub2", CommandCategory.File, "menu:file/recent/two@20"),
            Cmd("oranda.x.nowhere", CommandCategory.File, "menu:nosuchmenu/x@10", "toolbar:main/x@10"),
        };

        var bar = MenuModelBuilder.BuildMenuBar(commands, new ShellText(), _ => null, []);

        Assert.Equal(11, bar.Count);
        var sql = bar.Single(n => n.Title == "SQL");
        Assert.Equal(["oranda.x.a", "oranda.x.c", null, "oranda.x.b"], sql.Children.Select(n => n.CommandId));
        Assert.Equal(MenuNodeKind.Separator, sql.Children[2].Kind);

        var file = bar.Single(n => n.Title == "파일");
        var recent = Assert.Single(file.Children);
        Assert.Equal(MenuNodeKind.Submenu, recent.Kind);
        Assert.Equal(["oranda.x.sub1", "oranda.x.sub2"], recent.Children.Select(n => n.CommandId));
    }

    [Fact]
    public void View_menu_lists_tool_panels_automatically()
    {
        var panels = new[]
        {
            new ToolPanelDefinition { Id = "oranda.panel.b", Title = "Panel.b", Kind = ToolPanelKind.Tool, DefaultLocation = DockLocation.Bottom, ViewModelType = typeof(object), Order = 1 },
            new ToolPanelDefinition { Id = "oranda.panel.a", Title = "Panel.a", Kind = ToolPanelKind.Tool, DefaultLocation = DockLocation.Left, ViewModelType = typeof(object), Order = 1 },
            new ToolPanelDefinition { Id = "oranda.panel.doc", Title = "Panel.doc", Kind = ToolPanelKind.Document, DefaultLocation = DockLocation.Document, ViewModelType = typeof(object) },
        };

        var bar = MenuModelBuilder.BuildMenuBar([], new ShellText(), _ => null, panels);

        var view = bar.Single(n => n.Title == "보기");
        var submenu = view.Children.Single(n => n.Kind == MenuNodeKind.Submenu);
        Assert.Equal(["oranda.panel.a", "oranda.panel.b"], submenu.Children.Select(n => n.PanelId));
    }

    [Fact]
    public void Toolbar_takes_only_toolbar_main_placements()
    {
        var commands = new[]
        {
            Cmd("oranda.x.run", CommandCategory.Sql, "menu:sql/run@10", "toolbar:main/run@20"),
            Cmd("oranda.x.copy", CommandCategory.Result, "context:grid/copy@10"),
        };

        var tb = MenuModelBuilder.BuildToolbar(commands, new ShellText(), id => id == "oranda.x.run" ? "F5" : null);

        var item = Assert.Single(tb);
        Assert.Equal("oranda.x.run", item.CommandId);
        Assert.Equal("F5", item.Shortcut);
    }

    [Theory]
    [InlineData(new double[] { 40, 40, 40 }, 200, 0, 40, 3)]
    [InlineData(new double[] { 40, 40, 40 }, 100, 0, 30, 1)]
    [InlineData(new double[] { 40, 40, 40 }, 20, 0, 30, 0)]
    [InlineData(new double[0], 0, 0, 30, 0)]
    public void Toolbar_overflow_folds_from_the_right(double[] widths, double available, double spacing, double overflowButton, int expected)
    {
        Assert.Equal(expected, ToolbarOverflow.VisibleCount(widths, available, spacing, overflowButton));
    }

    // ---- 창 위치 ----

    private static readonly ScreenArea Hd = new("A", 0, 0, 1920, 1040, true);
    private static readonly ScreenArea Second = new("B", 1920, 0, 1280, 1000, false);

    [Fact]
    public void First_run_is_eighty_percent_of_work_area_capped_and_centered()
    {
        var r = WindowPlacementPolicy.Resolve(null, [Hd]);

        Assert.Equal(1280, r.Width);
        Assert.Equal(800, r.Height);
        Assert.Equal((1920 - 1280) / 2, r.X);
        Assert.Equal((1040 - 800) / 2, r.Y);
        Assert.Equal(WindowStateKind.Normal, r.State);
    }

    [Fact]
    public void First_run_on_a_small_work_area_is_eighty_percent()
    {
        var small = new ScreenArea("S", 0, 0, 768, 432, true); // 1920×1080 화면, 배율 250 %
        var r = WindowPlacementPolicy.Resolve(null, [small]);

        Assert.Equal(768, r.Width); // 80 % = 614 → 최소 크기가 작업 영역(768)으로 줄어든 값 768 에 맞춘다
        Assert.True(r.Width <= small.Width && r.Height <= small.Height);
    }

    [Fact]
    public void Saved_placement_on_existing_screen_is_kept()
    {
        var saved = new WindowPlacement { X = 2000, Y = 100, Width = 1100, Height = 700, Screen = "B", State = WindowStateKind.Maximized };

        var r = WindowPlacementPolicy.Resolve(saved, [Hd, Second]);

        Assert.Equal((2000, 100, 1100, 700), (r.X, r.Y, r.Width, r.Height));
        Assert.Equal(WindowStateKind.Maximized, r.State);
        Assert.Equal("B", r.Screen);
    }

    [Fact]
    public void Saved_screen_that_is_gone_falls_back_to_primary_center()
    {
        var saved = new WindowPlacement { X = 2000, Y = 100, Width = 1100, Height = 700, Screen = "B" };

        var r = WindowPlacementPolicy.Resolve(saved, [Hd]);

        Assert.Equal("A", r.Screen);
        Assert.Equal((1920 - 1100) / 2, r.X);
        Assert.Equal((1040 - 700) / 2, r.Y);
    }

    [Fact]
    public void Saved_position_off_every_screen_is_centered_on_primary()
    {
        var saved = new WindowPlacement { X = 9000, Y = 9000, Width = 1000, Height = 650 };

        var r = WindowPlacementPolicy.Resolve(saved, [Hd]);

        Assert.True(r.X >= 0 && r.X + r.Width <= 1920);
        Assert.True(r.Y >= 0 && r.Y + r.Height <= 1040);
    }

    [Fact]
    public void Placement_is_never_smaller_than_minimum_nor_larger_than_the_screen()
    {
        var tiny = new WindowPlacement { X = 0, Y = 0, Width = 100, Height = 100 };
        var huge = new WindowPlacement { X = 0, Y = 0, Width = 9000, Height = 9000 };

        var a = WindowPlacementPolicy.Resolve(tiny, [Hd]);
        var b = WindowPlacementPolicy.Resolve(huge, [Hd]);

        Assert.Equal((960, 600), (a.Width, a.Height));
        Assert.Equal((1920, 1040), (b.Width, b.Height));
    }

    [Fact]
    public void Minimum_size_shrinks_to_the_work_area_on_a_scaled_screen()
    {
        var area = new ScreenArea("S", 0, 0, 768, 432, true);

        Assert.Equal((768, 432), WindowPlacementPolicy.MinimumFor(area));
    }

    [Fact]
    public void No_screens_still_gives_a_usable_placement()
    {
        var r = WindowPlacementPolicy.Resolve(null, []);

        Assert.True(r.Width >= 960 && r.Height >= 600);
    }
}
