using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Shell;
using Oranda.App.Shell.Menus;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell;

/// <summary>
/// TSK-09-04(D-022, TRD 11.3): 툴바가 명령의 <c>When</c> 을 따른다. 커밋·롤백은 <c>!readOnlySession || txnPending</c> 일 때만 툴바·SQL 메뉴에 보인다.
/// 탭 범위(포커스 키) <c>When</c> 은 툴바를 숨기지 않는다(실행·포맷 단추는 회색으로 남는다).
/// </summary>
public class ToolbarWhenTests
{
    private static readonly IReadOnlyList<CommandDefinition> AppCommands =
        [.. ShellContributions.Scan(typeof(ShellContributions).Assembly).Commands.Select(c => c.Definition)];

    private static readonly string[] TxnCommands = ["oranda.txn.commit", "oranda.txn.rollback"];

    /// <summary>이름 → 값 표로 식을 계산하는 문맥 키(없는 이름은 거짓).</summary>
    private sealed class Keys(params string[] on) : IContextKeys
    {
        public bool Evaluate(string? expression) => ContextKeyReasons.Evaluate(expression, on.Contains, malformed: false);
    }

    private static CommandDefinition Cmd(string id, string? when, string placement) =>
        new() { Id = id, Title = "Cmd." + id, Category = CommandCategory.Sql, When = when, Placements = [placement] };

    private static IReadOnlyList<MenuNode> AppToolbar() => MenuModelBuilder.BuildToolbar(AppCommands, new ShellText(language: "ko"), _ => null);

    /// <summary>보이는 줄: 명령 id, 구분선은 「|」.</summary>
    private static string Visible(IReadOnlyList<MenuNode> nodes, IContextKeys keys)
    {
        var shown = MenuVisibility.Toolbar(nodes, keys);
        return string.Join(" ", nodes.Where((_, i) => shown[i]).Select(n => n.Kind == MenuNodeKind.Separator ? "|" : n.CommandId));
    }

    // ---- 모델 ----------------------------------------------------------------------------------

    [Fact]
    public void Toolbar_nodes_carry_when_and_enablement()
    {
        var commit = AppToolbar().Single(n => n.CommandId == "oranda.txn.commit");
        Assert.Equal("!readOnlySession || txnPending", commit.When);
        Assert.Equal("(hasSession || dedicatedSession) && txnPending && !sessionBusy", commit.Enablement);
    }

    [Theory]
    // 가운데 묶음이 모두 숨으면 그 앞뒤 구분선 중 하나만 남는다
    [InlineData(new string[0], "a | c")]
    [InlineData(new[] { "k" }, "a | b | c")]
    public void Separator_hides_when_both_sides_are_hidden(string[] on, string expected)
    {
        var nodes = MenuModelBuilder.BuildToolbar(
            [Cmd("a", null, "toolbar:main/a@10"), Cmd("b", "k", "toolbar:main/b@110"), Cmd("c", null, "toolbar:main/c@210")], new ShellText(), _ => null);
        Assert.Equal(expected, Visible(nodes, new Keys(on)));
    }

    [Theory]
    // 맨 앞·맨 뒤 묶음이 숨으면 구분선이 끝에 남지 않는다
    [InlineData("toolbar:main/x@10", "toolbar:main/y@110", "y")]
    [InlineData("toolbar:main/y@10", "toolbar:main/x@110", "y")]
    public void Edge_groups_leave_no_dangling_separator(string hidden, string shown, string expected)
    {
        var nodes = MenuModelBuilder.BuildToolbar([Cmd("x", "k", hidden), Cmd("y", null, shown)], new ShellText(), _ => null);
        Assert.Equal(expected, Visible(nodes, new Keys()));
    }

    [Fact]
    public void Everything_hidden_shows_nothing_and_an_empty_toolbar_is_fine()
    {
        var nodes = MenuModelBuilder.BuildToolbar([Cmd("x", "k", "toolbar:main/x@10"), Cmd("y", "k", "toolbar:main/y@110")], new ShellText(), _ => null);
        Assert.Equal(string.Empty, Visible(nodes, new Keys()));
        Assert.Empty(MenuVisibility.Toolbar([], new Keys()));
    }

    [Theory]
    [InlineData("sqlDocFocus")]
    [InlineData("editorFocus")]
    [InlineData("gridFocus && k")] // 탭 범위 키는 참, 상태 키는 그대로
    [InlineData("(((")] // 읽을 수 없는 식은 보인다
    public void Tab_scope_and_malformed_when_do_not_hide_toolbar_buttons(string when)
    {
        var keys = new Keys("k");
        Assert.True(MenuVisibility.InToolbarScope(when, keys));
        Assert.False(MenuVisibility.InScope("sqlDocFocus", new Keys())); // 주 메뉴는 SQL 탭이 없으면 숨긴다(비교용)
    }

    [Theory]
    [InlineData(new string[0], true)] // 쓰기 가능 접속(또는 접속 없음): 보임
    [InlineData(new[] { "readOnlySession" }, false)] // 조회 전용 Role·프로필 읽기 전용 + 미커밋 없음: 숨김
    [InlineData(new[] { "readOnlySession", "txnPending" }, true)] // 미커밋이 남아 있으면 끝낼 길이 있다
    [InlineData(new[] { "txnPending" }, true)]
    public void Commit_and_rollback_follow_read_only_and_pending(string[] on, bool visible)
    {
        var keys = new Keys(on);
        var toolbar = AppToolbar();
        var shown = MenuVisibility.Toolbar(toolbar, keys);
        foreach (var id in TxnCommands)
        {
            Assert.Equal(visible, shown[toolbar.ToList().FindIndex(n => n.CommandId == id)]);
        }

        // SQL 메뉴도 같은 규칙(주 메뉴는 When 을 원래 따랐다).
        var sql = MenuModelBuilder.BuildMenuBar(AppCommands, new ShellText(language: "ko"), _ => null, [])[MenuModelBuilder.TopMenus.ToList().IndexOf("sql")];
        var state = MenuVisibility.Evaluate(sql, keys);
        var index = sql.Children.ToList().FindIndex(n => n.CommandId == "oranda.txn.commit");
        Assert.Equal(visible, state.Children[index].Visible);
    }

    [Fact]
    public void Read_only_toolbar_loses_exactly_commit_and_rollback_and_no_double_separator()
    {
        var toolbar = AppToolbar();
        var all = MenuVisibility.Toolbar(toolbar, new Keys()).Count(v => v);
        var readOnly = MenuVisibility.Toolbar(toolbar, new Keys("readOnlySession"));
        Assert.Equal(TxnCommands, toolbar.Where((n, i) => n.Kind == MenuNodeKind.Command && !readOnly[i]).Select(n => n.CommandId!));
        var line = Visible(toolbar, new Keys("readOnlySession"));
        Assert.DoesNotContain("| |", line, StringComparison.Ordinal);
        Assert.False(line.StartsWith('|') || line.EndsWith('|'));
        Assert.True(all - readOnly.Count(v => v) is 2 or 3); // 커밋·롤백(+ 그 묶음 구분선)
    }

    [Fact]
    public void Toolbar_commands_with_when_are_listed_and_only_commit_and_rollback_can_hide()
    {
        // 결과 기록: 지금 When 이 있으면서 주 툴바에 놓인 명령. 커밋·롤백 말고는 탭 범위 키라 숨지 않는다.
        var withWhen = AppToolbar().Where(n => n.Kind == MenuNodeKind.Command && n.When is not null).ToDictionary(n => n.CommandId!, n => n.When!);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["oranda.exec.runStatement"] = "sqlDocFocus",
                ["oranda.exec.runScript"] = "sqlDocFocus",
                ["oranda.ai.tune"] = "sqlDocFocus",
                ["oranda.exec.cancel"] = "sqlDocFocus",
                ["oranda.plan.explain"] = "sqlDocFocus",
                ["oranda.format.document"] = "editorFocus",
                ["oranda.txn.commit"] = "!readOnlySession || txnPending",
                ["oranda.txn.rollback"] = "!readOnlySession || txnPending",
            }.OrderBy(p => p.Key),
            withWhen.OrderBy(p => p.Key));
        var nothing = new Keys("readOnlySession"); // 탭도 접속도 없고 조회 전용
        Assert.Equal(TxnCommands, withWhen.Where(p => !MenuVisibility.InToolbarScope(p.Value, nothing)).Select(p => p.Key).Order());
    }

    [Fact]
    public void Main_toolbar_never_takes_the_mybatis_document_toolbar_commands()
    {
        // MyBatis 문서 도구 막대(toolbar:mybatis, MyBatisDocumentToolbar)는 자기 막대가 그리고 이 규칙을 쓰지 않는다.
        var mybatis = AppCommands.Where(c => c.Placements.Any(p => p.StartsWith("toolbar:mybatis/", StringComparison.Ordinal))).Select(c => c.Id).ToList();
        Assert.NotEmpty(mybatis);
        Assert.DoesNotContain(AppToolbar(), n => n.CommandId is { } id && mybatis.Contains(id) && !AppCommands.Single(c => c.Id == id).Placements.Any(p => p.StartsWith("toolbar:main/", StringComparison.Ordinal)));
    }

    // ---- Headless: 주 창 툴바 -----------------------------------------------------------------

    private static List<string> ShownButtons(ShellHarness h) =>
        [.. h.Window.Toolbar.Children.OfType<Button>()
            .Where(b => !b.Classes.Contains(MenuControls.ToolbarOutOfScopeClass))
            .Select(b => ((RegistryCommand)b.Command!).CommandId)];

    [Fact]
    public Task Read_only_session_hides_commit_and_rollback_until_a_transaction_is_pending() => HeadlessHost.Run(() =>
    {
        // 프로필 읽기 전용과 조회 전용 Role 은 같은 키(readOnlySession)를 켠다(ExecutionState.ReadOnlySessionKey).
        using var h = new ShellHarness();
        var keys = h.Get<ContextKeyService>();
        var before = ShownButtons(h);
        Assert.Subset(before.ToHashSet(), TxnCommands.ToHashSet());

        keys.Set("readOnlySession", true);
        Dispatcher.UIThread.RunJobs();
        var readOnly = ShownButtons(h);
        Assert.Equal(before.Except(TxnCommands), readOnly);
        var commit = h.Window.Toolbar.Children.OfType<Button>().Single(b => b.Command is RegistryCommand { CommandId: "oranda.txn.commit" });
        Assert.False(commit.IsVisible);
        Assert.DoesNotContain(VisibleSqlMenuCommands(h), id => TxnCommands.Contains(id));
        AssertSeparatorsTidy(h);
        Render(h, "readonly");

        keys.Set("txnPending", true);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, ShownButtons(h));
        Assert.True(commit.IsVisible);
        Assert.Contains("oranda.txn.commit", VisibleSqlMenuCommands(h));
        Render(h, "readonly-pending");

        keys.Set("txnPending", false);
        keys.Set("readOnlySession", false);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, ShownButtons(h));
    });

    [Fact]
    public Task Hidden_buttons_stay_out_of_the_overflow_menu() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        h.Get<ContextKeyService>().Set("readOnlySession", true);
        Dispatcher.UIThread.RunJobs();
        h.Window.MinWidth = 0;
        h.Window.Width = 420; // 넘침 「…」 이 생기는 폭(주 창 최소 폭 960 을 풀어 좁힌다)
        Dispatcher.UIThread.RunJobs();
        h.Window.UpdateLayout();
        h.Window.UpdateToolbarOverflow();
        var overflow = h.Window.FindControl<Button>("OverflowButton")!;
        Assert.True(overflow.IsVisible);
        var names = ((MenuFlyout)overflow.Flyout!).Items.OfType<MenuItem>().Select(m => ((RegistryCommand)m.Command!).CommandId).ToList();
        Assert.NotEmpty(names);
        Assert.DoesNotContain(names, TxnCommands.Contains);
        Assert.All(h.Window.Toolbar.Children.Where(c => c.Classes.Contains(MenuControls.ToolbarOutOfScopeClass)), c => Assert.False(c.IsVisible));
        Dispatcher.UIThread.RunJobs();
    });

    /// <summary>ORANDA_RENDER_DIR 가 있으면 주 창 위쪽(메뉴·툴바)을 라이트·다크로 그려 <c>toolbar-{name}-{tone}.png</c> 로 쓴다.</summary>
    private static void Render(ShellHarness h, string name)
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
                Dispatcher.UIThread.RunJobs();
                h.Window.UpdateLayout();
                using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)h.Window.Bounds.Width, (int)h.Window.Bounds.Height));
                bmp.Render(h.Window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"toolbar-{name}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static List<string> VisibleSqlMenuCommands(ShellHarness h)
    {
        var sql = (MenuItem)h.Window.Menus.ItemsSource!.Cast<object>().ElementAt(MenuModelBuilder.TopMenus.ToList().IndexOf("sql"));
        return [.. All(sql).Where(i => i.IsVisible && i.Command is RegistryCommand).Select(i => ((RegistryCommand)i.Command!).CommandId)];

        static IEnumerable<MenuItem> All(MenuItem menu)
        {
            foreach (var item in menu.ItemsSource!.Cast<object>().OfType<MenuItem>())
            {
                yield return item;
                if (item.ItemsSource is not null)
                {
                    foreach (var sub in All(item))
                    {
                        yield return sub;
                    }
                }
            }
        }
    }

    /// <summary>보이는 툴바 줄에 구분선이 맨 앞·맨 뒤·연달아 오지 않는다(넘침으로 접힌 것은 보지 않는다).</summary>
    private static void AssertSeparatorsTidy(ShellHarness h)
    {
        var shown = h.Window.Toolbar.Children.Where(c => !c.Classes.Contains(MenuControls.ToolbarOutOfScopeClass)).ToList();
        Assert.False(shown[0] is Separator);
        Assert.False(shown[^1] is Separator);
        for (var i = 1; i < shown.Count; i++)
        {
            Assert.False(shown[i] is Separator && shown[i - 1] is Separator);
        }
    }
}
