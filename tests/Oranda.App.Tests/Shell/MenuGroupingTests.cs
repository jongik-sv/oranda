using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Shell.Menus;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell;

/// <summary>
/// TSK-09-02(PRD 13.3, UX-01·02·05·06·07): 파일·편집·보기·SQL 메뉴 하위 메뉴 묶기, 보기 메뉴 중복 자리 빼기, PL/SQL ▸, MyBatis 명령 문맥.
/// 「모든 기능」(게이트 없음) 모델을 본다. 「한 단계 줄 수」 = 메뉴를 열었을 때 바로 보이는 줄(구분선 제외, When 숨김 미적용).
/// </summary>
public class MenuGroupingTests
{
    private static readonly IReadOnlyList<CommandDefinition> AppCommands =
        [.. ShellContributions.Scan(typeof(ShellContributions).Assembly).Commands.Select(c => c.Definition)];

    private static readonly IReadOnlyList<ToolPanelDefinition> AppPanels = ShellTestHelpers.AppContributions.Panels;

    /// <summary>보기 메뉴에서 뺀 명령(「도구 창 ▸」 토글과 같은 창을 연다).</summary>
    private static readonly string[] ViewDuplicates =
        ["oranda.view.outline", "oranda.view.output", "oranda.view.messages", "oranda.view.plan", "oranda.view.rowView", "oranda.view.compileErrors", "oranda.view.templates"];

    private static readonly string[] PlsqlCommands = ["oranda.plsql.compile", "oranda.plsql.run", "oranda.plsql.toggleSpecBody", "oranda.plsql.showServerDiff"];

    private static readonly string[] MyBatisDocOnly =
        ["oranda.mybatis.save", "oranda.mybatis.formatAll", "oranda.mybatis.undoFormatAll", "oranda.mybatis.checkCombinations", "oranda.mybatis.explainScenario"];

    private static readonly string[] MyBatisAlways = ["oranda.mybatis.openFolder", "oranda.mybatis.findStatement", "oranda.mybatis.convertWorkspace"];

    /// <summary>팔레트에서도 숨긴 저장소 작업 공간 명령(UX-05). 명령 ID 는 남는다.</summary>
    private static readonly string[] HiddenStoreCommands = ["oranda.file.saveWorkspace", "oranda.file.openWorkspace"];

    private static IReadOnlyList<MenuNode> Bar(string lang = "ko") =>
        MenuModelBuilder.BuildMenuBar(AppCommands, new ShellText(language: lang), _ => null, AppPanels);

    private static MenuNode Top(string id, string lang = "ko") => Bar(lang)[MenuModelBuilder.TopMenus.ToList().IndexOf(id)];

    private static List<MenuNode> Lines(MenuNode menu) => [.. menu.Children.Where(c => c.Kind != MenuNodeKind.Separator)];

    private static MenuNode Sub(MenuNode menu, string key) => menu.Children.Single(c => c.Kind == MenuNodeKind.Submenu && c.Key == key);

    private static List<string> Ids(MenuNode node) =>
        [.. node.Children.SelectMany(c => c.Kind == MenuNodeKind.Command ? [c.CommandId!] : Ids(c))];

    private static List<string> DirectIds(MenuNode node) =>
        [.. node.Children.Where(c => c.Kind == MenuNodeKind.Command).Select(c => c.CommandId!)];

    private static ContextKeyService Keys(params string[] trueKeys)
    {
        var keys = new ContextKeyService();
        foreach (var k in trueKeys)
        {
            keys.Set(k, true);
        }

        return keys;
    }

    /// <summary>When 을 적용한 지금 보이는 명령 ID(하위 메뉴 안까지).</summary>
    private static List<string> VisibleIds(MenuNode top, ContextKeyService keys)
    {
        var state = MenuVisibility.Evaluate(top, keys);
        var result = new List<string>();
        void Walk(IReadOnlyList<MenuNode> nodes, IReadOnlyList<MenuState> states)
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                if (!states[i].Visible)
                {
                    continue;
                }

                if (nodes[i].Kind == MenuNodeKind.Command)
                {
                    result.Add(nodes[i].CommandId!);
                }

                Walk(nodes[i].Children, states[i].Children);
            }
        }

        Walk(top.Children, state.Children);
        return result;
    }

    // ---- 한 단계 줄 수 ----------------------------------------------------------------------

    [Theory]
    [InlineData("edit", 15)]
    [InlineData("file", 15)]
    [InlineData("view", 7)]
    public void Top_menu_shows_at_most_the_target_number_of_lines(string id, int max)
    {
        var lines = Lines(Top(id));
        Assert.True(lines.Count <= max, $"{id}: {lines.Count} 줄 — {string.Join(", ", lines.Select(l => l.Title))}");
    }

    [Fact]
    public void Edit_menu_keeps_clipboard_and_find_on_top_and_groups_the_rest_in_six_submenus()
    {
        var edit = Top("edit");
        Assert.Equal(
            ["oranda.edit.undo", "oranda.edit.redo", "oranda.edit.cut", "oranda.edit.copy", "oranda.edit.paste", "oranda.edit.pasteMyBatisLog", "oranda.edit.selectAll", "oranda.edit.find", "oranda.edit.replace"],
            DirectIds(edit));
        Assert.Equal(
            ["Menu.edit.go", "Menu.edit.case", "Menu.edit.lines", "Menu.edit.format", "Menu.edit.marks", "Menu.edit.assist"],
            edit.Children.Where(c => c.Kind == MenuNodeKind.Submenu).Select(c => c.Key));

        Assert.Equal(6, Ids(Sub(edit, "Menu.edit.case")).Count);
        Assert.Equal(8, Ids(Sub(edit, "Menu.edit.lines")).Count);
        Assert.Equal(5, Ids(Sub(edit, "Menu.edit.format")).Count);
        Assert.Equal(9, Ids(Sub(edit, "Menu.edit.marks")).Count);
        Assert.Equal(3, Ids(Sub(edit, "Menu.edit.assist")).Count);

        // 「이동 ▸」 = 다음/이전 찾기·줄로 이동·짝 괄호 + 옛 「탐색」 9개. 탐색 배치는 더 남지 않는다.
        var go = Ids(Sub(edit, "Menu.edit.go"));
        Assert.Equal(13, go.Count);
        Assert.Equal(["oranda.edit.findNext", "oranda.edit.findPrevious", "oranda.edit.goToLine", "oranda.edit.goToMatchingBracket"], go.Take(4));
        Assert.Contains("oranda.nav.quickOpenObject", go);
        Assert.Contains("oranda.mybatis.findStatement", go);
        Assert.DoesNotContain(AppCommands.SelectMany(c => c.Placements), p => p.StartsWith("menu:navigate/", StringComparison.Ordinal));
    }

    [Fact]
    public void Submenus_have_separators_between_their_own_groups_but_not_at_the_edges()
    {
        foreach (var top in new[] { "edit", "file" })
        {
            foreach (var sub in Top(top).Children.Where(c => c.Kind == MenuNodeKind.Submenu))
            {
                Assert.NotEqual(MenuNodeKind.Separator, sub.Children[0].Kind);
                Assert.NotEqual(MenuNodeKind.Separator, sub.Children[^1].Kind);
            }
        }

        // 줄·주석: 주석 4 | 들여쓰기 2 | 줄 복제·삭제 2
        var lines = Sub(Top("edit"), "Menu.edit.lines").Children;
        Assert.Equal(2, lines.Count(c => c.Kind == MenuNodeKind.Separator));
    }

    [Fact]
    public void File_menu_groups_workspace_connection_tabs_and_mybatis()
    {
        var file = Top("file");
        Assert.Equal(
            ["oranda.file.newSqlEditor", "oranda.file.newDedicatedSqlEditor", "oranda.file.newPlsqlEditor", "oranda.file.open",
             "oranda.file.save", "oranda.file.saveAs", "oranda.file.saveAll", "oranda.file.close", "oranda.file.closeAll", "oranda.file.exit"],
            DirectIds(file));
        Assert.Equal(
            ["Menu.file.recentFiles", "Menu.file.workspace", "Menu.file.connection", "Menu.file.tabs", "Menu.file.mybatis"],
            file.Children.Where(c => c.Kind == MenuNodeKind.Submenu).Select(c => c.Key));

        Assert.Equal(
            ["oranda.conn.manage", "oranda.conn.switchSession", "oranda.conn.disconnect", "oranda.conn.reconnect", "oranda.conn.changePassword", "oranda.conn.import", "oranda.conn.export"],
            Ids(Sub(file, "Menu.file.connection")));
        Assert.Equal(["oranda.file.reopenClosed", "oranda.file.duplicateTab", "oranda.file.renameTab"], Ids(Sub(file, "Menu.file.tabs")));
        Assert.Equal(["oranda.mybatis.openFolder", "oranda.mybatis.save"], Ids(Sub(file, "Menu.file.mybatis")));

        var workspace = Sub(file, "Menu.file.workspace");
        var ws = Ids(workspace);
        Assert.Equal(["oranda.file.newWorkspace", "oranda.file.openWorkspaceFile"], ws.Take(2));
        Assert.Contains("oranda.file.saveWorkspaceFile", ws);
        Assert.Contains("oranda.plsql.saveWorkspaceFile", ws);
        Assert.Contains("oranda.mybatis.convertWorkspace", ws);
        Assert.Equal("oranda.file.closeWorkspace", ws[^1]);

        // 최근 작업 공간 하위 메뉴는 「작업 공간 ▸」 안으로, 공급자 키도 같이 옮겼다.
        var recent = workspace.Children.Single(c => c.Kind == MenuNodeKind.Submenu);
        Assert.Equal("Menu.file.workspace.recentWorkspaces", recent.Key);
        Assert.Equal("최근 작업 공간", recent.Title);
        Assert.Equal("Menu.file.workspace.recentWorkspaces", typeof(Oranda.App.Features.RecentFiles.RecentFilesService)
            .GetField("WorkspacesMenuKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null));
    }

    [Fact]
    public void Toolbar_places_of_regrouped_commands_did_not_move()
    {
        string[] toolbarIds = ["oranda.file.newWorkspace", "oranda.file.open", "oranda.file.save", "oranda.mybatis.save", "oranda.conn.manage", "oranda.conn.disconnect", "oranda.format.document"];
        foreach (var id in toolbarIds)
        {
            Assert.Contains(AppCommands.Single(c => c.Id == id).Placements, p => p.StartsWith("toolbar:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Main_toolbar_has_a_workspace_save_button_right_after_save()
    {
        static int Order(string id) =>
            int.Parse(AppCommands.Single(c => c.Id == id).Placements.Single(p => p.StartsWith("toolbar:main/", StringComparison.Ordinal)).Split('@')[1],
                System.Globalization.CultureInfo.InvariantCulture);

        var save = Order("oranda.file.save");
        var workspace = Order("oranda.file.saveWorkspaceFile");
        Assert.True(workspace > save);
        Assert.DoesNotContain(AppCommands.SelectMany(c => c.Placements), p => p.StartsWith("toolbar:main/", StringComparison.Ordinal)
            && int.Parse(p.Split('@')[1], System.Globalization.CultureInfo.InvariantCulture) is var o && o > save && o < workspace);
    }

    // ---- 보기 메뉴 중복 자리 ----------------------------------------------------------------

    [Fact]
    public void View_menu_has_no_command_that_opens_the_same_window_as_a_tool_window_toggle()
    {
        var view = Top("view");
        var ids = Ids(view);
        foreach (var id in ViewDuplicates)
        {
            Assert.DoesNotContain(id, ids);
            Assert.Contains(view.Children.Single(c => c.Key is null && c.Kind == MenuNodeKind.Submenu && c.Children.Any(x => x.Kind == MenuNodeKind.PanelToggle)).Children,
                t => t.PanelId == "oranda.panel." + id["oranda.view.".Length..]);
        }

        Assert.DoesNotContain("oranda.view.resetLayout", ids);
        Assert.Contains("oranda.view.resetLayout", Ids(Top("window")));
        Assert.DoesNotContain("oranda.view.templates", Ids(Top("tools")));
        Assert.Equal(["oranda.view.commandPalette", "oranda.view.zoomIn", "oranda.view.zoomOut", "oranda.view.zoomReset"], DirectIds(view));
    }

    [Fact]
    public void Removed_menu_places_keep_the_command_in_the_palette_and_keymap()
    {
        foreach (var id in ViewDuplicates.Append("oranda.view.resetLayout"))
        {
            var def = AppCommands.Single(c => c.Id == id);
            Assert.True(def.ShowInPalette, id);
        }

        // 단축키가 있던 명령은 그대로 있다(키맵은 배치와 따로).
        var keymap = KeymapLoader.LoadPreset("default").Bindings;
        Assert.Contains(keymap, b => b.Command == "oranda.view.messages" || b.Command == "oranda.view.output" || b.Command == "oranda.view.plan");
    }

    [Fact]
    public void Store_workspace_commands_stay_registered_but_leave_the_palette_too()
    {
        foreach (var id in HiddenStoreCommands)
        {
            var def = AppCommands.Single(c => c.Id == id);
            Assert.False(def.ShowInPalette);
            Assert.Empty(def.Placements);
        }
    }

    [Fact]
    public void Every_command_is_still_reachable_from_a_menu_toolbar_context_menu_palette_or_shortcut()
    {
        var bound = KeymapLoader.PresetIds.SelectMany(id => KeymapLoader.LoadPreset(id).Bindings).Select(b => b.Command).ToHashSet(StringComparer.Ordinal);
        var lost = AppCommands
            .Where(c => !HiddenStoreCommands.Contains(c.Id))
            .Where(c => c.Placements.Count == 0 && !c.ShowInPalette && !bound.Contains(c.Id))
            .Select(c => c.Id)
            .ToList();
        Assert.Empty(lost);
    }

    // ---- SQL > PL/SQL ▸ ------------------------------------------------------------------

    [Fact]
    public void Sql_menu_has_a_plsql_submenu_with_four_commands_shown_only_for_a_plsql_editor()
    {
        var sql = Top("sql");
        var plsql = Sub(sql, "Menu.sql.plsql");
        Assert.Equal("PL/SQL", plsql.Title);
        Assert.Equal(["oranda.plsql.compile", "oranda.plsql.run", "oranda.plsql.toggleSpecBody", "oranda.plsql.showServerDiff"], Ids(plsql));
        Assert.Equal(1, plsql.Children.Count(c => c.Kind == MenuNodeKind.Separator)); // 컴파일·실행 | 명세/본문·서버 차이

        var inPlsql = VisibleIds(sql, Keys("plsqlDocActive", "plsqlEditorFocus"));
        Assert.All(PlsqlCommands, id => Assert.Contains(id, inPlsql));

        var inSql = VisibleIds(sql, Keys("sqlDocActive", "sqlDocFocus"));
        Assert.All(PlsqlCommands, id => Assert.DoesNotContain(id, inSql));
        var state = MenuVisibility.Evaluate(sql, Keys("sqlDocActive"));
        Assert.False(state.Children[sql.Children.ToList().IndexOf(plsql)].Visible); // 하위 메뉴 머리도 숨는다
    }

    // ---- MyBatis 명령 문맥 ----------------------------------------------------------------

    [Fact]
    public void Mapper_only_mybatis_commands_hide_in_a_sql_tab_and_show_in_a_mybatis_document()
    {
        foreach (var id in MyBatisDocOnly)
        {
            Assert.Equal("mybatisDocFocus", AppCommands.Single(c => c.Id == id).When);
        }

        foreach (var id in MyBatisAlways)
        {
            Assert.Null(AppCommands.Single(c => c.Id == id).When);
        }

        var bar = Bar();
        var sqlTab = Keys("sqlDocActive", "hasSession");
        var mybatisTab = Keys("sqlDocActive", "mybatisDocActive", "hasSession");
        var inSql = bar.SelectMany(top => VisibleIds(top, sqlTab)).ToList();
        var inMyBatis = bar.SelectMany(top => VisibleIds(top, mybatisTab)).ToList();
        Assert.All(MyBatisDocOnly, id => Assert.DoesNotContain(id, inSql));
        Assert.All(MyBatisDocOnly, id => Assert.Contains(id, inMyBatis));
        Assert.All(MyBatisAlways, id => Assert.Contains(id, inSql));

        // SQL 탭에서 「MyBatis ▸」 에는 매퍼 폴더 열기만 남는다.
        var file = bar[MenuModelBuilder.TopMenus.ToList().IndexOf("file")];
        var visibleInFile = VisibleIds(file, sqlTab);
        Assert.Contains("oranda.mybatis.openFolder", visibleInFile);
        Assert.DoesNotContain("oranda.mybatis.save", visibleInFile);
    }

    // ---- 문구 ----------------------------------------------------------------------------

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void Every_submenu_title_of_the_regrouped_menus_comes_from_the_resources(string lang)
    {
        foreach (var top in new[] { "file", "edit", "view", "sql" })
        {
            foreach (var sub in Top(top, lang).Children.Where(c => c.Kind == MenuNodeKind.Submenu && c.Key is not null))
            {
                Assert.NotNull(StringCatalog.Get(lang, sub.Key!));
                Assert.Equal(StringCatalog.Get(lang, sub.Key!), sub.Title);
            }
        }
    }
}
