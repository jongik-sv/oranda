using Oranda.App.Shell;
using Oranda.App.Shell.Menus;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell;

/// <summary>
/// TSK-09-03(D-015, TRD 11.2): 오른쪽 클릭 하위 메뉴 문법 <c>context:&lt;표면&gt;/&lt;묶음&gt;/&lt;하위 메뉴&gt;@n</c>,
/// 결과 메뉴로 옮겨 싣기, 그리드 오른쪽 클릭 37줄을 한 단계 15줄 이하로 묶기.
/// </summary>
public class ContextSubmenuTests
{
    private static readonly IReadOnlyList<CommandDefinition> AppCommands =
        [.. ShellContributions.Scan(typeof(ShellContributions).Assembly).Commands.Select(c => c.Definition)];

    private static readonly ShellText Ko = new(language: "ko");

    private static CommandDefinition Cmd(string id, string? when = null, params string[] placements) =>
        new() { Id = id, Title = "Cmd." + id, Category = CommandCategory.Result, When = when, Placements = placements };

    /// <summary>모양을 글로: 명령 = id, 구분선 = 「-」, 하위 메뉴 = 「키[…]」.</summary>
    private static string Shape(IReadOnlyList<MenuNode> nodes) => string.Join(" ", nodes.Select(n => n.Kind switch
    {
        MenuNodeKind.Separator => "-",
        MenuNodeKind.Submenu => $"{n.Key}[{Shape(n.Children)}]",
        _ => n.CommandId,
    }));

    /// <summary>한 단계 줄 수(PRD 13.3: 구분선 제외, When 숨김 미적용).</summary>
    private static int Lines(IReadOnlyList<MenuNode> nodes) => nodes.Count(n => n.Kind != MenuNodeKind.Separator);

    private static IEnumerable<MenuNode> Flatten(IEnumerable<MenuNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

    private static IReadOnlyList<MenuNode> Grid() => MenuModelBuilder.BuildContext(AppCommands, Ko, _ => null, "grid");

    private static MenuNode ResultMenu(IEnumerable<CommandDefinition> commands) =>
        MenuModelBuilder.BuildMenuBar(commands, Ko, _ => null, [])[MenuModelBuilder.TopMenus.ToList().IndexOf("result")];

    // ---- 규칙 표 ----------------------------------------------------------------------------------

    [Theory]
    // 셋째 마디 = 그 묶음 안의 하위 메뉴, 하위 메뉴 안 구분선은 주 메뉴처럼 @n/100
    [InlineData(new[] { "a1=context:t/a@10", "a2=context:t/a/s@20", "a3=context:t/a/s@130", "b1=context:t/b@10" }, "a1 Menu.context.t.s[a2 - a3] - b1")]
    // 하위 메뉴 자리 = 안에 든 항목의 가장 작은 순서(앞 명령보다 작으면 앞에)
    [InlineData(new[] { "a1=context:t/a@10", "a2=context:t/a/s@5", "a3=context:t/a/s@30" }, "Menu.context.t.s[a2 a3] a1")]
    // 한 묶음에 하위 메뉴 둘
    [InlineData(new[] { "x=context:t/a/p@20", "y=context:t/a/q@10", "z=context:t/a/p@30" }, "Menu.context.t.q[y] Menu.context.t.p[x z]")]
    // 둘째 마디가 모두 달라도 셋째 마디가 있으면 묶음 이름 관례(하위 메뉴 하나 = 묶음 하나)
    [InlineData(new[] { "x=context:t/one@10", "y=context:t/two/s@10" }, "x - Menu.context.t.s[y]")]
    // 편집기 관례(둘째 마디가 명령마다 다름): 순서 번호로 묶음, 하위 메뉴 없음 — 지금 그대로
    [InlineData(new[] { "x=context:t/x@10", "y=context:t/y@20", "z=context:t/z@110" }, "x y - z")]
    [InlineData(new[] { "x=context:t/x@10", "y=context:t/y@12", "z=context:t/z@31" }, "x y - z")]
    // 다른 표면·다른 표면 이름의 배치는 들어오지 않는다
    [InlineData(new[] { "x=context:t/a@10", "y=context:other/a/s@10", "z=menu:result/a@10" }, "x")]
    public void Context_rule_table(string[] spec, string expected)
    {
        var commands = spec.Select(s => s.Split('=')).Select(p => Cmd(p[0], null, p[1])).ToList();
        Assert.Equal(expected, Shape(MenuModelBuilder.BuildContext(commands, Ko, _ => null, "t")));
    }

    [Fact]
    public void Submenu_title_key_is_menu_context_surface_name_in_both_languages()
    {
        var commands = new[] { Cmd("x", null, "context:grid/copy@10"), Cmd("y", null, "context:grid/copy/copyAs@20") };
        var ko = MenuModelBuilder.BuildContext(commands, Ko, _ => null, "grid");
        var en = MenuModelBuilder.BuildContext(commands, new ShellText(language: "en"), _ => null, "grid");
        Assert.Equal("복사 형식", ko[1].Title);
        Assert.Equal("Copy As", en[1].Title);
        Assert.Equal("Menu.context.grid.copyAs", ko[1].Key);
    }

    [Fact]
    public void Submenu_children_keep_shortcut_icon_when_and_enablement()
    {
        var command = Cmd("y", "gridFocus", "context:grid/copy/copyAs@20") with { Icon = "copy", Enablement = "gridHasSelection" };
        var sub = Assert.Single(MenuModelBuilder.BuildContext([Cmd("x", null, "context:grid/copy@10"), command], Ko, id => "K-" + id, "grid"), n => n.Kind == MenuNodeKind.Submenu);
        var child = Assert.Single(sub.Children);
        Assert.Equal(("y", "K-y", "copy", "gridFocus", "gridHasSelection"), (child.CommandId, child.Shortcut, child.Icon, child.When, child.Enablement));
    }

    [Fact]
    public void Empty_and_malformed_input_give_an_empty_menu()
    {
        Assert.Empty(MenuModelBuilder.BuildContext([], Ko, _ => null, "grid"));
        Assert.Empty(MenuModelBuilder.BuildContext([Cmd("x", null, "context:grid/copy/copyAs@abc", "context:")], Ko, _ => null, "grid"));
    }

    // ---- 결과 메뉴로 옮겨 싣기 ----------------------------------------------------------------------

    [Fact]
    public void Result_menu_mirrors_context_submenus_as_submenus_with_the_same_key()
    {
        var commands = new[]
        {
            Cmd("a", "gridFocus", "context:grid/copy@10"),
            Cmd("b", "gridFocus", "context:grid/copy/copyAs@20"),
            Cmd("c", "gridFocus", "context:grid/copy/copyAs@130"),
            Cmd("d", "gridFocus", "context:grid/edit/edit@10"),
            Cmd("e", "gridFocus", "context:grid/edit/edit@20", "menu:result/e@-100"), // 메뉴에 자리가 있으면 옮겨 싣지 않는다
        };
        var result = ResultMenu(commands);
        Assert.Equal("e - a Menu.context.grid.copyAs[b - c] - Menu.context.grid.edit[d]", Shape(result.Children));
        Assert.Equal("복사 형식", result.Children.Single(n => n.Key == "Menu.context.grid.copyAs").Title);
    }

    [Fact]
    public void Mirrored_submenu_header_hides_when_every_child_is_hidden()
    {
        var result = ResultMenu([Cmd("a", "gridFocus", "context:grid/copy@10"), Cmd("b", "gridFocus", "context:grid/copy/copyAs@20")]);
        var sub = result.Children.ToList().FindIndex(n => n.Kind == MenuNodeKind.Submenu);

        var hidden = MenuVisibility.Evaluate(result, FakeContextKeys.AllFalse); // SQL 탭 없음 → gridFocus 범위 밖
        Assert.False(hidden.Children[sub].Visible);
        Assert.True(hidden.ShowEmpty);

        var shown = MenuVisibility.Evaluate(result, FakeContextKeys.AllTrue);
        Assert.True(shown.Children[sub].Visible);
        Assert.True(shown.Children[sub].Children[0].Visible);
    }

    // ---- 앱의 그리드 오른쪽 클릭·결과 메뉴 -------------------------------------------------------------

    [Fact]
    public void Grid_context_menu_shows_at_most_fifteen_lines_with_frequent_commands_on_top()
    {
        var grid = Grid();
        Assert.True(Lines(grid) <= 15, $"한 단계 {Lines(grid)}줄: {Shape(grid)}");
        foreach (var id in new[] { "oranda.grid.copy", "oranda.grid.rowView", "oranda.grid.export", "oranda.grid.exportExcel" })
        {
            Assert.Contains(grid, n => n.CommandId == id);
        }

        // 묶기 전 37개 명령이 하나도 빠지지 않는다(행 보기·값 보기·내보내기 2 포함).
        Assert.Equal(37, Flatten(grid).Count(n => n.Kind == MenuNodeKind.Command));
        Assert.DoesNotContain(grid, n => n.Kind == MenuNodeKind.Submenu && n.Children.Count == 0);
    }

    [Theory]
    [InlineData("copyAs", new[] { "oranda.grid.copyWithHeaders", "oranda.grid.copyRows", "oranda.grid.copyRowsWithHeaders", "oranda.grid.copyAsCsv", "oranda.grid.copyAsInsert", "oranda.grid.copyAsInList" })]
    [InlineData("filter", new[] { "oranda.grid.toggleQuickFilter", "oranda.grid.clearLocalFilters", "oranda.grid.toggleFilterBar", "oranda.grid.filterByValue", "oranda.grid.filterExcludeValue", "oranda.grid.filterNullRows" })]
    [InlineData("edit", new[] { "oranda.grid.edit.addRow", "oranda.grid.edit.duplicateRow", "oranda.grid.edit.pasteRows", "oranda.grid.edit.deleteRow", "oranda.grid.edit.previewDml", "oranda.grid.edit.applyAndCommit", "oranda.grid.edit.apply", "oranda.grid.edit.resetRow", "oranda.grid.edit.resetAll" })]
    [InlineData("flashback", new[] { "oranda.grid.flashback.asOf", "oranda.grid.flashback.history", "oranda.grid.flashback.restore" })]
    [InlineData("columns", new[] { "oranda.grid.freezeColumns", "oranda.grid.hideColumn", "oranda.grid.showAllColumns" })]
    public void Grid_submenus_hold_their_commands_in_order(string name, string[] ids)
    {
        var sub = Assert.Single(Grid(), n => n.Key == "Menu.context.grid." + name);
        Assert.Equal(ids, sub.Children.Where(c => c.Kind == MenuNodeKind.Command).Select(c => c.CommandId!));
        Assert.All(sub.Children.Where(c => c.Kind == MenuNodeKind.Command), c => Assert.Equal("gridFocus", c.When)); // 문맥 숨김 그대로
        Assert.Equal(MenuNodeKind.Command, sub.Children[0].Kind);
        Assert.Equal(MenuNodeKind.Command, sub.Children[^1].Kind);
    }

    [Fact]
    public void Grid_context_menu_sections_are_in_the_planned_order()
    {
        Assert.Equal(
            "oranda.grid.copy Menu.context.grid.copyAs[oranda.grid.copyWithHeaders oranda.grid.copyRows oranda.grid.copyRowsWithHeaders - oranda.grid.copyAsCsv oranda.grid.copyAsInsert oranda.grid.copyAsInList]"
            + " - oranda.grid.rowView oranda.grid.viewValue"
            + " - Menu.context.grid.filter[oranda.grid.toggleQuickFilter oranda.grid.clearLocalFilters oranda.grid.toggleFilterBar - oranda.grid.filterByValue oranda.grid.filterExcludeValue oranda.grid.filterNullRows]"
            + " Menu.context.grid.columns[oranda.grid.freezeColumns oranda.grid.hideColumn oranda.grid.showAllColumns]"
            + " - oranda.grid.countRows oranda.grid.fetchAll oranda.grid.pinResultTab"
            + " - oranda.grid.followForeignKey oranda.grid.showReferencingRows"
            + " - Menu.context.grid.edit[oranda.grid.edit.addRow oranda.grid.edit.duplicateRow oranda.grid.edit.pasteRows oranda.grid.edit.deleteRow - oranda.grid.edit.previewDml oranda.grid.edit.applyAndCommit oranda.grid.edit.apply - oranda.grid.edit.resetRow oranda.grid.edit.resetAll]"
            + " Menu.context.grid.flashback[oranda.grid.flashback.asOf oranda.grid.flashback.history - oranda.grid.flashback.restore]"
            + " - oranda.grid.exportExcel oranda.grid.export",
            Shape(Grid()));
    }

    [Fact]
    public void Result_menu_shows_at_most_fifteen_lines_and_the_same_submenus()
    {
        var result = ResultMenu(AppCommands);
        Assert.True(Lines(result.Children) <= 15, $"한 단계 {Lines(result.Children)}줄: {Shape(result.Children)}");
        Assert.Equal(
            Grid().Where(n => n.Kind == MenuNodeKind.Submenu).Select(n => Shape([n])),
            result.Children.Where(n => n.Kind == MenuNodeKind.Submenu).Select(n => Shape([n])));
        Assert.Equal("oranda.grid.exportExcel", result.Children[0].CommandId);
        Assert.Contains(result.Children, n => n.CommandId == "oranda.grid.copy");
        Assert.Contains(result.Children, n => n.CommandId == "oranda.grid.rowView");
        Assert.Contains(result.Children, n => n.CommandId == "oranda.grid.export");
    }

    [Fact]
    public void Other_context_surfaces_have_no_submenus()
    {
        // 편집기·스키마·모니터 등 오른쪽 클릭 모양은 그대로(셋째 마디를 쓰는 표면은 그리드뿐).
        var surfaces = AppCommands.SelectMany(c => c.Placements)
            .Where(p => p.StartsWith("context:", StringComparison.Ordinal))
            .Select(p => p["context:".Length..].Split('@')[0].Split('/')[0])
            .Distinct()
            .Where(s => s != "grid")
            .ToList();
        Assert.Contains("editor", surfaces);
        Assert.Contains("schema", surfaces);
        Assert.Contains("monitor", surfaces);
        foreach (var surface in surfaces)
        {
            Assert.DoesNotContain(MenuModelBuilder.BuildContext(AppCommands, Ko, _ => null, surface), n => n.Kind == MenuNodeKind.Submenu);
        }
    }

    [Fact]
    public void Editor_context_menu_keeps_its_numbered_sections()
    {
        var editor = MenuModelBuilder.BuildContext(AppCommands, Ko, _ => null, "editor");
        Assert.True(Lines(editor) > 0);
        Assert.NotEqual(MenuNodeKind.Separator, editor[0].Kind);
        Assert.NotEqual(MenuNodeKind.Separator, editor[^1].Kind);
        Assert.Contains(editor, n => n.Kind == MenuNodeKind.Separator);
        for (var i = 1; i < editor.Count; i++)
        {
            Assert.False(editor[i].Kind == MenuNodeKind.Separator && editor[i - 1].Kind == MenuNodeKind.Separator);
        }
    }
}

/// <summary>
/// TSK-09-03 Headless: 앱 셸에서 그리드 오른쪽 클릭 메뉴와 「복사 형식 ▸」 하위 메뉴를 실제로 열어 ko/en 글자·잘림을 본다.
/// <c>ORANDA_RENDER_DIR</c> 가 있으면 라이트·다크 PNG(<c>grid-context-{언어}-{톤}.png</c>)를 쓴다.
/// </summary>
[Collection("UiLanguage")]
public class GridContextMenuRenderTests
{
    private static async Task SetLanguageAsync(Headless.ShellHarness h, string code)
    {
        await h.Get<Oranda.App.Shell.Settings.SettingsService>().UpdateAsync(s => s with { Ui = s.Ui with { Language = code } });
        await Headless.HeadlessHost.WaitUntil(() => h.Get<IShellText>().Language == code);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    [Theory]
    [InlineData("ko", "복사(TSV)", "복사 형식", "CSV 로 복사")]
    [InlineData("en", "Copy (TSV)", "Copy As", "Copy as CSV")]
    public Task Grid_context_menu_and_submenu_open_without_clipping(string language, string copy, string copyAs, string csv) => Headless.HeadlessHost.Run(async () =>
    {
        using var h = new Headless.ShellHarness();
        h.Window.Width = 1100;
        h.Window.Height = 760;
        var wiring = h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<Oranda.App.Features.SqlEditor.SqlEditorDockWiring>().Single();
        await wiring.RunAsync(default);
        if (!h.Get<IContextKeys>().Evaluate("sqlDocActive"))
        {
            await h.Get<ICommandRegistry>().ExecuteAsync("oranda.file.newSqlEditor", h.Get<IContextKeys>());
        }

        await SetLanguageAsync(h, language);
        try
        {
            var menu = new Avalonia.Controls.ContextMenu { ItemsSource = ShellContextMenus.Factory!("grid") };
            menu.Open(h.Window);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var lines = menu.Items.OfType<Avalonia.Controls.MenuItem>().ToList();
            Assert.Equal(copy, lines[0].Header);
            var sub = lines.Single(m => Equals(m.Header, copyAs));
            sub.IsSubMenuOpen = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var csvItem = sub.Items.OfType<Avalonia.Controls.MenuItem>().Single(m => Equals(m.Header, csv));

            // 헤드리스는 팝업을 창의 덧그림 층에 그린다: 창 하나만 보면 메뉴·하위 메뉴가 함께 있다.
            Assert.Same(h.Window, Avalonia.Controls.TopLevel.GetTopLevel(csvItem));
            var headers = lines.Concat(sub.Items.OfType<Avalonia.Controls.MenuItem>()).Select(m => $"'{m.Header}'").ToList();
            Assert.Empty(Headless.TextClipping.Find(h.Window).Where(o => headers.Any(x => o.StartsWith(x, StringComparison.Ordinal))));
            Assert.True(csvItem.IsEffectivelyVisible);
            Render(h.Window, language);
            sub.IsSubMenuOpen = false;
            menu.Close();
        }
        finally
        {
            await SetLanguageAsync(h, "ko");
        }
    });

    /// <summary>오른쪽 클릭 메뉴·하위 메뉴가 열린 주 창을 라이트·다크로 그린다.</summary>
    private static void Render(Avalonia.Controls.Window window, string language)
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (variant, tone) in new[] { (Avalonia.Styling.ThemeVariant.Light, "light"), (Avalonia.Styling.ThemeVariant.Dark, "dark") })
            {
                Avalonia.Application.Current!.RequestedThemeVariant = variant;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"grid-context-{language}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }
}
