using Oranda.App.Shell;
using Oranda.App.Shell.Menus;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell;

/// <summary>TSK-13-36: 주 메뉴의 숨김(When)·회색 이유 툴팁(Enablement) 규칙. Avalonia 없이 모델만 본다.</summary>
public class MenuVisibilityTests
{
    private static readonly IReadOnlyList<CommandDefinition> AppCommands =
        [.. ShellContributions.Scan(typeof(ShellContributions).Assembly).Commands.Select(c => c.Definition)];

    private static readonly string[] DatabaseCommands =
        ["oranda.db.info", "oranda.db.ddlExtract", "oranda.schema.objectSearch", "oranda.schema.compileInvalid", "oranda.schema.refresh"];

    private static readonly Dictionary<string, string> Phrases = new(StringComparer.Ordinal)
    {
        ["Menu.disabled.hasSession"] = "접속",
        ["Menu.disabled.not.readOnlySession"] = "읽기 전용",
        ["Menu.disabled.not.sessionBusy"] = "실행 중",
        ["Menu.disabled.monitorSessionSelected"] = "행 고르기",
        ["Menu.disabled.canKillSession"] = "권한",
    };

    private static string? Phrase(string key) => Phrases.GetValueOrDefault(key);

    private static Func<string, bool> Values(params string[] trueKeys) => k => trueKeys.Contains(k);

    private static ContextKeyService Keys(params string[] trueKeys)
    {
        var keys = new ContextKeyService();
        foreach (var k in trueKeys)
        {
            keys.Set(k, true);
        }

        return keys;
    }

    private static MenuNode Cmd(string id, string? when = null, string? enablement = null) =>
        new(MenuNodeKind.Command, id, 0) { CommandId = id, When = when, Enablement = enablement };

    private static MenuNode Sep() => MenuNode.Separator(0);

    private static MenuNode Top(params MenuNode[] children) =>
        new(MenuNodeKind.Submenu, "top", 0) { Children = children, EmptyText = "(빈 메뉴)", ReasonText = Phrase };

    private static IReadOnlyList<MenuNode> Bar(ShellText? text = null) =>
        MenuModelBuilder.BuildMenuBar(AppCommands, text ?? new ShellText(language: "ko"), _ => null, []);

    private static MenuNode TopOf(IReadOnlyList<MenuNode> bar, string id) => bar[MenuModelBuilder.TopMenus.ToList().IndexOf(id)];

    /// <summary>최상위 메뉴에서 보이는 명령 ID(하위 메뉴 안 포함, 차례대로).</summary>
    private static List<string> VisibleCommands(MenuNode top, MenuState state)
    {
        var ids = new List<string>();
        Walk(top.Children, state.Children);
        return ids;

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
                    ids.Add(nodes[i].CommandId!);
                }

                Walk(nodes[i].Children, states[i].Children);
            }
        }
    }

    private static MenuState StateOf(MenuNode top, MenuState state, string commandId)
    {
        return Find(top.Children, state.Children) ?? throw new Xunit.Sdk.XunitException($"{commandId} 가 메뉴에 없다.");

        MenuState? Find(IReadOnlyList<MenuNode> nodes, IReadOnlyList<MenuState> states)
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].CommandId == commandId)
                {
                    return states[i];
                }

                if (Find(nodes[i].Children, states[i].Children) is { } found)
                {
                    return found;
                }
            }

            return null;
        }
    }

    // ── 이유 문구(ContextKeyReasons) ──

    [Fact]
    public void Reason_is_null_when_the_expression_is_true_empty_or_has_no_phrase()
    {
        Assert.Null(ContextKeyReasons.Explain("hasSession", Values("hasSession"), Phrase));
        Assert.Null(ContextKeyReasons.Explain(null, Values(), Phrase));
        Assert.Null(ContextKeyReasons.Explain("  ", Values(), Phrase));
        Assert.Null(ContextKeyReasons.Explain("unknownKey", Values(), Phrase)); // 표에 없는 키는 툴팁 없음
        Assert.Null(ContextKeyReasons.Explain("hasSession &&", Values(), Phrase)); // 읽을 수 없는 식
    }

    [Theory]
    [InlineData("hasSession", new string[0], "접속")]
    [InlineData("hasSession && !readOnlySession", new[] { "readOnlySession" }, "접속")] // && 는 앞의 거짓 항
    [InlineData("hasSession && !readOnlySession", new[] { "hasSession", "readOnlySession" }, "읽기 전용")] // !x 거짓 = not.x
    [InlineData("(hasSession || dedicatedSession || tabConnectionMissing) && !sessionBusy", new string[0], "접속")] // || 는 문구가 있는 첫 항
    [InlineData("(dedicatedSession || hasSession) && !sessionBusy", new[] { "hasSession", "sessionBusy" }, "실행 중")]
    [InlineData("monitorSessionSelected && canKillSession", new string[0], "행 고르기")]
    [InlineData("monitorSessionSelected && canKillSession", new[] { "monitorSessionSelected" }, "권한")]
    [InlineData("tabConnectionMissing && hasSession", new string[0], "접속")] // 앞 항에 문구가 없으면 다음 거짓 항
    public void Reason_names_the_first_false_key_that_has_a_phrase(string expression, string[] trueKeys, string expected)
        => Assert.Equal(expected, ContextKeyReasons.Explain(expression, Values(trueKeys), Phrase));

    [Fact]
    public void Negated_group_has_no_phrase()
        => Assert.Null(ContextKeyReasons.Explain("!(hasSession || readOnlySession)", Values("hasSession"), Phrase));

    [Theory]
    [InlineData("a && (b || !c)", new[] { "a" }, true)]
    [InlineData("a && (b || !c)", new[] { "a", "c" }, false)]
    [InlineData("!a || b", new[] { "a" }, false)]
    [InlineData(null, new string[0], true)]
    public void Evaluate_follows_the_same_grammar_as_the_context_key_service(string? expression, string[] trueKeys, bool expected)
    {
        Assert.Equal(expected, ContextKeyReasons.Evaluate(expression, Values(trueKeys)));
        Assert.Equal(expected, Keys(trueKeys).Evaluate(expression));
    }

    [Fact]
    public void Malformed_expression_evaluates_to_the_given_fallback()
    {
        Assert.True(ContextKeyReasons.Evaluate("a &&", Values()));
        Assert.False(ContextKeyReasons.Evaluate("(a", Values(), malformed: false));
    }

    // ── When → 탭·패널 범위 ──

    [Fact]
    public void Monitor_commands_are_in_scope_only_while_a_monitor_tab_is_open()
    {
        Assert.False(MenuVisibility.InScope("monitorFocus", Keys()));
        Assert.True(MenuVisibility.InScope("monitorFocus", Keys("monitorSessionOpen")));
        Assert.True(MenuVisibility.InScope("monitorFocus", Keys("monitorSqlOpen")));

        // 포커스 자리만 남아 있어도(모니터 탭을 닫은 뒤 등) 보이지 않는다.
        var stale = Keys();
        stale.SetFocusSlot("monitor");
        Assert.False(MenuVisibility.InScope("monitorFocus", stale));
    }

    [Theory]
    [InlineData("sqlDocFocus")]
    [InlineData("sqlEditorFocus")]
    [InlineData("gridFocus")]
    [InlineData("editorFocus")]
    public void Sql_editor_commands_follow_the_active_sql_tab_not_the_last_focus(string when)
    {
        var keys = Keys("sqlDocActive");
        keys.SetFocusSlot("schema"); // 스키마 트리를 누른 뒤 SQL 메뉴를 열어도
        Assert.True(MenuVisibility.InScope(when, keys));
        Assert.False(MenuVisibility.InScope(when, Keys()));
    }

    [Fact]
    public void Editor_commands_are_in_scope_in_a_plsql_editor()
    {
        var keys = Keys();
        keys.SetFocusSlot("plsql");
        Assert.True(MenuVisibility.InScope("editorFocus", keys));
        Assert.True(MenuVisibility.InScope("plsqlEditorFocus", keys));
        Assert.True(MenuVisibility.InScope("plsqlEditorFocus", Keys("plsqlDocActive")));
        Assert.False(MenuVisibility.InScope("plsqlEditorFocus", Keys("sqlDocActive")));
    }

    [Fact]
    public void Schema_commands_and_commands_without_when_are_always_in_scope()
    {
        Assert.True(MenuVisibility.InScope("schemaFocus", Keys()));
        Assert.True(MenuVisibility.InScope(null, Keys()));
        Assert.True(MenuVisibility.InScope(string.Empty, Keys()));
        Assert.True(MenuVisibility.InScope("broken &&", Keys())); // 읽을 수 없는 식은 숨기지 않는다
    }

    [Fact]
    public void Non_focus_keys_in_when_use_their_own_value()
    {
        Assert.False(MenuVisibility.InScope("devBuild", Keys()));
        Assert.True(MenuVisibility.InScope("devBuild", new ContextKeyService(devBuild: true)));
    }

    [Fact]
    public void Every_focus_key_has_a_tab_scope()
        => Assert.All(Oranda.App.Shell.Keymaps.FocusScopes.Keys, k => Assert.True(MenuVisibility.TabScopes.ContainsKey(k), k));

    // ── 구분선·빈 메뉴 ──

    [Fact]
    public void Separators_never_lead_trail_or_double_up()
    {
        var top = Top(Sep(), Cmd("a", "monitorFocus"), Sep(), Cmd("b"), Sep(), Cmd("c", "monitorFocus"), Sep(), Sep(), Cmd("d"), Sep(), Cmd("e", "monitorFocus"));
        var state = MenuVisibility.Evaluate(top, Keys());

        Assert.Equal(
            [false, false, false, true, true, false, false, false, true, false, false],
            state.Children.Select(c => c.Visible));
        Assert.False(state.ShowEmpty);
    }

    [Fact]
    public void Submenu_hides_when_all_its_commands_are_hidden_and_its_separator_goes_with_it()
    {
        var sub = new MenuNode(MenuNodeKind.Submenu, "sub", 0) { Children = [Cmd("x", "monitorFocus"), Sep(), Cmd("y", "monitorFocus")] };
        var top = Top(Cmd("a"), Sep(), sub);

        var hidden = MenuVisibility.Evaluate(top, Keys());
        Assert.Equal([true, false, false], hidden.Children.Select(c => c.Visible));

        var shown = MenuVisibility.Evaluate(top, Keys("monitorSessionOpen"));
        Assert.Equal([true, true, true], shown.Children.Select(c => c.Visible));
        Assert.Equal([true, true, true], shown.Children[2].Children.Select(c => c.Visible));
    }

    [Fact]
    public void Menu_with_every_command_hidden_shows_the_empty_notice()
    {
        var state = MenuVisibility.Evaluate(Top(Cmd("a", "monitorFocus"), Sep(), Cmd("b", "gridFocus")), Keys());
        Assert.True(state.Visible); // 최상위 메뉴는 남긴다
        Assert.True(state.ShowEmpty);
        Assert.All(state.Children, c => Assert.False(c.Visible));

        Assert.True(MenuVisibility.Evaluate(Top(), Keys()).ShowEmpty);
    }

    [Fact]
    public void Panel_toggles_stay_visible()
    {
        var panel = new MenuNode(MenuNodeKind.PanelToggle, "p", 0) { PanelId = "p" };
        var state = MenuVisibility.Evaluate(Top(Cmd("a", "monitorFocus"), Sep(), panel), Keys());
        Assert.Equal([false, false, true], state.Children.Select(c => c.Visible));
        Assert.False(state.ShowEmpty);
    }

    [Fact]
    public void Disabled_command_in_scope_gets_a_reason_and_enabled_or_hidden_ones_do_not()
    {
        var top = Top(Cmd("a", null, "hasSession"), Cmd("b", null, "hasSession && !readOnlySession"), Cmd("c", "monitorFocus", "hasSession"), Cmd("d"));
        var off = MenuVisibility.Evaluate(top, Keys());
        Assert.Equal(["접속", "접속", null, null], off.Children.Select(c => c.Reason));

        var readOnly = MenuVisibility.Evaluate(top, Keys("hasSession", "readOnlySession"));
        Assert.Equal([null, "읽기 전용", null, null], readOnly.Children.Select(c => c.Reason));
    }

    // ── 실제 명령으로 만든 메뉴 막대 ──

    [Fact]
    public void Without_a_connection_or_monitor_tab_the_monitor_menu_shows_only_the_session_opener()
    {
        var monitor = TopOf(Bar(), "monitor");
        var state = MenuVisibility.Evaluate(monitor, Keys());
        Assert.Equal(["oranda.monitor.sessions"], VisibleCommands(monitor, state));
        Assert.False(state.ShowEmpty);
    }

    [Fact]
    public void With_a_session_monitor_open_its_commands_show_and_unusable_ones_explain_why()
    {
        var monitor = TopOf(Bar(), "monitor");
        var state = MenuVisibility.Evaluate(monitor, Keys("hasSession", "monitorSessionOpen"));
        var visible = VisibleCommands(monitor, state);
        Assert.Contains("oranda.monitor.killSession", visible);
        Assert.Contains("oranda.monitor.refresh", visible);
        Assert.Equal("세션 목록에서 행을 고르세요", StateOf(monitor, state, "oranda.monitor.killSession").Reason);
        Assert.Null(StateOf(monitor, state, "oranda.monitor.refresh").Reason);

        var selected = MenuVisibility.Evaluate(monitor, Keys("hasSession", "monitorSessionOpen", "monitorSessionSelected"));
        Assert.Equal("ALTER SYSTEM 권한이 필요합니다", StateOf(monitor, selected, "oranda.monitor.killSession").Reason);
        Assert.Null(StateOf(monitor, selected, "oranda.monitor.sessionDetail").Reason);
    }

    [Fact]
    public void Without_a_connection_database_commands_are_visible_and_say_to_connect()
    {
        var database = TopOf(Bar(), "database");
        var state = MenuVisibility.Evaluate(database, Keys());
        foreach (var id in DatabaseCommands)
        {
            var s = StateOf(database, state, id);
            Assert.True(s.Visible, id);
            Assert.Equal("DB 에 접속해야 쓸 수 있습니다", s.Reason);
        }
    }

    [Fact]
    public void Reasons_follow_the_language()
    {
        var database = TopOf(Bar(new ShellText(language: "en")), "database");
        var state = MenuVisibility.Evaluate(database, Keys());
        Assert.Equal("Connect to a database first", StateOf(database, state, "oranda.db.info").Reason);
    }

    [Fact]
    public void Without_a_sql_tab_the_result_menu_shows_its_empty_notice()
    {
        var result = TopOf(Bar(), "result");
        Assert.Equal("(SQL 편집기 탭을 열면 쓸 수 있습니다)", result.EmptyText);
        Assert.True(MenuVisibility.Evaluate(result, Keys()).ShowEmpty);

        var open = MenuVisibility.Evaluate(result, Keys("sqlDocActive"));
        Assert.False(open.ShowEmpty);
        Assert.Equal("쿼리 결과가 있어야 쓸 수 있습니다", StateOf(result, open, "oranda.grid.exportExcel").Reason);
    }

    [Fact]
    public void Sql_menu_hides_editor_only_commands_without_a_sql_tab_and_shows_them_with_one()
    {
        var sql = TopOf(Bar(), "sql");
        var none = VisibleCommands(sql, MenuVisibility.Evaluate(sql, Keys()));
        Assert.DoesNotContain("oranda.exec.runStatement", none);
        Assert.Contains("oranda.txn.commit", none); // When 이 없는 명령은 남는다

        var open = MenuVisibility.Evaluate(sql, Keys("sqlDocActive"));
        Assert.Contains("oranda.exec.runStatement", VisibleCommands(sql, open));
        Assert.Equal("DB 에 접속해야 쓸 수 있습니다", StateOf(sql, open, "oranda.exec.runStatement").Reason);
    }

    [Fact]
    public void No_menu_of_the_real_bar_shows_a_stray_separator_in_any_key_state()
    {
        string[][] states = [[], ["sqlDocActive"], ["hasSession", "monitorSessionOpen"], ["sqlDocActive", "hasSession", "monitorSqlOpen"]];
        foreach (var trueKeys in states)
        {
            foreach (var top in Bar())
            {
                AssertTidy(top.Children, MenuVisibility.Evaluate(top, Keys(trueKeys)).Children, top.Title);
            }
        }

        static void AssertTidy(IReadOnlyList<MenuNode> nodes, IReadOnlyList<MenuState> states, string where)
        {
            var shown = nodes.Zip(states).Where(x => x.Second.Visible).ToList();
            if (shown.Count > 0)
            {
                Assert.NotEqual(MenuNodeKind.Separator, shown[0].First.Kind);
                Assert.NotEqual(MenuNodeKind.Separator, shown[^1].First.Kind);
            }

            for (var i = 1; i < shown.Count; i++)
            {
                Assert.False(shown[i].First.Kind == MenuNodeKind.Separator && shown[i - 1].First.Kind == MenuNodeKind.Separator, where);
            }

            foreach (var (node, state) in shown)
            {
                AssertTidy(node.Children, state.Children, where + " > " + node.Title);
            }
        }
    }

    [Fact]
    public void Context_menus_keep_every_command_regardless_of_keys()
    {
        // 오른쪽 클릭 메뉴는 주 메뉴 규칙을 쓰지 않는다: 문맥 키와 상관없이 같은 항목이다(숨김 계산이 끼지 않음).
        var monitor = MenuModelBuilder.BuildContext(AppCommands, new ShellText(language: "ko"), _ => null, "monitor");
        var expected = AppCommands.Count(c => c.Placements.Any(p => p.StartsWith("context:monitor/", StringComparison.Ordinal)));
        Assert.Equal(expected, monitor.Count(n => n.Kind == MenuNodeKind.Command));
    }
}
