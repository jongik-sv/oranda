using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Statements;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell;
using Oranda.Core.MyBatis;
using Oranda.Core.Shell;
using Oranda.Core.Text;
using Oranda.Language.MyBatis;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// TSK-03-01 F2: 「문장」 도구 창(ViewModel·View)과 문장 명령 처리기. 세션은 <see cref="SqlEditorHost"/> 를 직접 만들고
/// 열기 훅(<see cref="MyBatisOpenHandler"/>)으로 임시 매퍼를 열어 얻는다. 화면 글자는 한국어 표(<see cref="ShellText"/> 기본)로 단정한다.
/// </summary>
public sealed class WorkspaceStatementsTests : IDisposable
{
    private static readonly string[] BadIds = ["1bad", "countEmp"];
    private static readonly string[] ToolbarButtons = ["NewButton", "RenameButton", "DeleteButton", "ExpandButton"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-mbstatements-" + Guid.NewGuid().ToString("N"));

    public WorkspaceStatementsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        MyBatisStatementExpander.EvaluatorSource = null;
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteMapper(string text = "", string name = "EmpMapper.xml")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text.Length == 0 ? MapperSamples.EmpMapper : text);
        return path;
    }

    /// <summary>화면(ViewModel)을 먼저 만들어 두고 매퍼를 연다: 열기 훅은 작업 공간을 활성으로 만든 뒤에 세션을 붙이므로 그 순서도 함께 본다.</summary>
    private async Task<Rig> OpenAsync(string mapperText = "", MyBatisHooks? hooks = null, ShellText? text = null)
    {
        var dialogs = new FakeDialogs();
        var host = new SqlEditorHost(dialogs: dialogs) { MyBatisHooks = hooks ?? MyBatisHooks.Empty };
        var vm = new MyBatisStatementsViewModel { Host = host, Text = text ?? new ShellText() };
        Assert.False(vm.HasSession);
        Assert.Empty(vm.Groups);

        var path = WriteMapper(mapperText);
        Assert.True(await new MyBatisOpenHandler().TryOpenAsync(host, path, default));
        var session = Assert.IsType<MyBatisWorkspaceSession>(host.ActiveWorkspace!.MyBatis);
        return new Rig(host, vm, session, dialogs, path);
    }

    private static MyBatisStatementNode Node(MyBatisStatementsViewModel vm, string id) =>
        vm.Groups.SelectMany(g => g.Children).First(n => n.StatementId == id);

    private static Task Run(Rig rig, string commandId) =>
        new MyBatisStatementCommands { Host = rig.Host }.ExecuteAsync(new CommandContext(commandId, FakeContextKeys.AllTrue), default);

    // ---- 열기 · 목록 -----------------------------------------------------------------------

    [Fact]
    public Task Opening_a_mapper_lists_every_element_by_kind_and_opens_the_first_statement() => Ui.Run(async () =>
    {
        var rig = await OpenAsync();

        Assert.True(rig.Vm.HasSession);
        Assert.Equal(["select", "insert", "update", "delete", "sql 조각", "resultMap"], rig.Vm.Groups.Select(g => g.Title));
        Assert.Equal([3, 1, 1, 1, 1, 1], rig.Vm.Groups.Select(g => g.Children.Count));
        Assert.Equal(rig.Session.Entries.Count, rig.Vm.Groups.Sum(g => g.Children.Count));
        Assert.Equal("EmpMapper.xml", rig.Vm.MapperFileName);
        Assert.Equal(Path.GetDirectoryName(rig.Path) + Path.DirectorySeparatorChar, rig.Vm.MapperDirectory);
        Assert.Equal("com.acme.hr.EmpMapper", rig.Vm.NamespaceText);

        var tab = Assert.Single(rig.Session.Workspace.Tabs);
        Assert.Equal("selectEmpList", tab.Title);
        Assert.Equal("selectEmpList", rig.Vm.SelectedNode?.StatementId); // 열린 탭의 문장이 고른 문장으로 따라온다
    });

    [Fact]
    public Task Elements_without_an_id_gather_in_the_other_group_and_empty_groups_are_hidden() => Ui.Run(async () =>
    {
        var rig = await OpenAsync("""
            <?xml version="1.0" encoding="UTF-8"?>
            <mapper namespace="t.Other">
              <cache/>
              <select id="a" resultType="int">SELECT 1 FROM DUAL</select>
            </mapper>
            """);

        Assert.Equal(["select", "그 밖"], rig.Vm.Groups.Select(g => g.Title));
        var cache = Assert.Single(rig.Vm.Groups[1].Children);
        Assert.Equal("<cache>", cache.Title);
        Assert.Equal("(그대로 둠)", cache.KeepText);
        Assert.Equal(0.6, cache.RowOpacity);

        rig.Vm.OpenNode(cache); // 열 수 없는 요소: 고르기만 하고 탭은 더하지 않는다
        Assert.Single(rig.Session.Workspace.Tabs);
        Assert.Equal(string.Empty, Node(rig.Vm, "a").KeepText);
    });

    [Fact]
    public Task A_sql_fragment_shows_how_many_places_include_it() => Ui.Run(async () =>
    {
        var rig = await OpenAsync();

        Assert.Equal("· 2곳에서 씀", Node(rig.Vm, "empCols").UsageText); // selectEmpList·selectEmp
        Assert.Equal(string.Empty, Node(rig.Vm, "selectEmp").UsageText);
        Assert.Equal("(그대로 둠)", Node(rig.Vm, "empMap").KeepText);
    });

    [Fact]
    public Task Statements_with_a_duplicate_id_are_marked_as_not_editable_and_commands_refuse_them() => Ui.Run(async () =>
    {
        var rig = await OpenAsync(MapperSamples.DuplicateIds);

        Assert.All(rig.Vm.Groups.SelectMany(g => g.Children), n => Assert.Equal("편집 불가", n.AmbiguousText));

        rig.Vm.SelectNode(Node(rig.Vm, "dup"));
        rig.Dialogs.Answer = "other";
        await Run(rig, "oranda.mybatis.renameStatement");

        Assert.Single(rig.Dialogs.Messages);
        Assert.All(rig.Session.Entries, e => Assert.Equal("dup", e.Id));
    });

    // ---- 고르기 · 열기 · 수정 표시 ------------------------------------------------------------

    [Fact]
    public Task Selecting_an_item_selects_the_statement_without_opening_a_tab_and_group_headers_are_ignored() => Ui.Run(async () =>
    {
        var rig = await OpenAsync();

        rig.Vm.SelectNode(Node(rig.Vm, "countEmp"));
        Assert.Equal("countEmp", rig.Session.SelectedStatementId);
        Assert.Single(rig.Session.Workspace.Tabs);

        rig.Vm.SelectNode(rig.Vm.Groups[0]); // 묶음 머리는 명령 대상이 아니다: 고른 것 없음(활성 탭 문장으로 돌아가지 않는다)
        Assert.Null(rig.Session.SelectedStatementId);
    });

    [Fact]
    public Task Double_tapping_an_item_opens_its_tab_and_editing_marks_it_modified_until_the_text_matches_again() => Ui.Run(async () =>
    {
        var rig = await OpenAsync();
        var node = Node(rig.Vm, "selectEmp");
        Assert.Equal(string.Empty, node.ModifiedText);

        rig.Vm.OpenNode(node);

        Assert.Equal(2, rig.Session.Workspace.Tabs.Count);
        var tab = rig.Session.Workspace.ActiveTab!;
        Assert.Equal("selectEmp", tab.Title);
        Assert.Equal(string.Empty, node.ModifiedText);

        tab.Document.Insert(0, " ");
        Assert.Equal("● 수정", node.ModifiedText);
        Assert.Equal(string.Empty, Node(rig.Vm, "countEmp").ModifiedText);

        tab.Document.Remove(0, 1);
        Assert.Equal(string.Empty, node.ModifiedText);
    });

    [Fact]
    public Task Typing_in_a_statement_keeps_the_same_nodes_instead_of_rebuilding_the_list() => Ui.Run(async () =>
    {
        var rig = await OpenAsync();
        var before = rig.Vm.Groups.SelectMany(g => g.Children).ToList();

        rig.Session.Workspace.ActiveTab!.Document.Insert(0, " ");

        Assert.Equal(before, rig.Vm.Groups.SelectMany(g => g.Children)); // 같은 노드 객체
    });

    // ---- 명령 -------------------------------------------------------------------------------

    [Fact]
    public Task New_statement_adds_an_item_marked_new_and_opens_its_tab() => Ui.Run(async () =>
    {
        var rig = await OpenAsync();
        rig.Dialogs.Choice = "select";
        rig.Dialogs.Answer = "selectNew";

        await Run(rig, "oranda.mybatis.newStatement");

        var select = rig.Vm.Groups[0];
        Assert.Equal(4, select.Children.Count);
        Assert.Equal("selectNew", select.Children[^1].Title); // 같은 종류의 마지막 뒤
        Assert.Equal("+ 새 쿼리", select.Children[^1].NewText);
        Assert.Equal(string.Empty, select.Children[^1].ModifiedText);
        Assert.Equal("selectNew", rig.Session.Workspace.ActiveTab!.Title);
        Assert.Equal("selectNew", rig.Vm.SelectedNode?.StatementId);
    });

    [Fact]
    public Task Renaming_an_id_changes_the_item_the_tab_title_and_the_source_text() => Ui.Run(async () =>
    {
        var rig = await OpenAsync();
        rig.Vm.OpenNode(Node(rig.Vm, "selectEmp"));
        rig.Dialogs.Answer = " selectEmployee ";

        await Run(rig, "oranda.mybatis.renameStatement");

        var node = Node(rig.Vm, "selectEmployee");
        Assert.Equal("selectEmployee", node.Title);
        Assert.Equal("● 수정", node.ModifiedText);
        var tab = rig.Session.Workspace.ActiveTab!;
        Assert.Equal("selectEmployee", tab.Title);
        Assert.Contains("id=\"selectEmployee\"", tab.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Vm.Groups.SelectMany(g => g.Children), n => n.StatementId == "selectEmp");
        Assert.Equal("selectEmp", Assert.Single(rig.Dialogs.Prompts).Initial); // 현재 id 를 채워 묻는다
    });

    [Fact]
    public Task Renaming_to_an_invalid_or_duplicate_id_tells_the_user_and_changes_nothing() => Ui.Run(async () =>
    {
        var rig = await OpenAsync();
        rig.Vm.SelectNode(Node(rig.Vm, "selectEmp"));

        foreach (var bad in BadIds)
        {
            rig.Dialogs.Answer = bad;
            await Run(rig, "oranda.mybatis.renameStatement");
        }

        Assert.Equal(2, rig.Dialogs.Messages.Count);
        Assert.Contains(rig.Session.Entries, e => e.Id == "selectEmp");
        Assert.DoesNotContain(rig.Session.Entries, e => e.Id == "1bad");
    });

    [Fact]
    public Task Deleting_a_statement_removes_the_item_and_closes_its_tab_only_after_confirmation() => Ui.Run(async () =>
    {
        var rig = await OpenAsync();
        rig.Vm.OpenNode(Node(rig.Vm, "countEmp"));
        Assert.Equal(2, rig.Session.Workspace.Tabs.Count);

        rig.Dialogs.Confirm = false;
        await Run(rig, "oranda.mybatis.deleteStatement");
        Assert.Equal(3, rig.Vm.Groups[0].Children.Count);
        Assert.Equal(2, rig.Session.Workspace.Tabs.Count);

        rig.Dialogs.Confirm = true;
        await Run(rig, "oranda.mybatis.deleteStatement");

        Assert.Equal(2, rig.Vm.Groups[0].Children.Count);
        Assert.DoesNotContain(rig.Vm.Groups.SelectMany(g => g.Children), n => n.StatementId == "countEmp");
        Assert.DoesNotContain(rig.Session.Workspace.Tabs, t => t.Title == "countEmp");
    });

    [Fact]
    public Task Expanding_to_a_sql_tab_opens_the_all_on_expansion_in_a_plain_sql_workspace() => Ui.Run(async () =>
    {
        var fake = new FakeEvaluator("SELECT 1 FROM DUAL");
        MyBatisStatementExpander.EvaluatorSource = () => fake;
        var rig = await OpenAsync();
        rig.Vm.SelectNode(Node(rig.Vm, "selectEmp"));
        var mapperTabs = rig.Session.Workspace.Tabs.Count;

        await Run(rig, "oranda.mybatis.expandToSqlTab");

        Assert.Equal("selectEmp", fake.Statement?.Id);
        Assert.True(fake.Options?.AllOn);
        Assert.Equal(0, fake.ValueCount);
        var sqlWorkspace = Assert.Single(rig.Host.Workspaces, w => w.MyBatis is null);
        var tab = Assert.Single(sqlWorkspace.Tabs);
        Assert.Equal("SELECT 1 FROM DUAL", tab.Text);
        Assert.Equal("selectEmp", tab.Title);
        Assert.Equal(SqlWorkspaceKind.Sql, sqlWorkspace.DocumentKind);
        Assert.Equal(mapperTabs, rig.Session.Workspace.Tabs.Count); // 문장 작업 공간에는 일반 탭이 들어가지 않는다
    });

    [Fact]
    public Task Expanding_without_an_evaluator_says_so_and_opens_nothing() => Ui.Run(async () =>
    {
        var rig = await OpenAsync();
        rig.Vm.SelectNode(Node(rig.Vm, "selectEmp"));

        await Run(rig, "oranda.mybatis.expandToSqlTab");

        Assert.Equal("계산기가 아직 없습니다.", Assert.Single(rig.Dialogs.Messages).Text);
        Assert.Single(rig.Host.Workspaces);
    });

    [Fact]
    public Task Expanding_the_whole_mapper_opens_a_read_only_style_workspace_without_a_source_path() => Ui.Run(async () =>
    {
        var rig = await OpenAsync();
        var expected = WorkspaceFiles.Load(rig.Path).Tabs;

        await Run(rig, "oranda.mybatis.expandMapper");

        var expanded = Assert.Single(rig.Host.Workspaces, w => w.MyBatis is null);
        Assert.Equal("EmpMapper (펼침)", expanded.Name);
        Assert.Null(expanded.SourcePath);
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Select(t => t.Title), expanded.Tabs.Select(t => t.Title));
        Assert.Equal(expected.Select(t => t.Text), expanded.Tabs.Select(t => t.Text));
        Assert.Same(expanded, rig.Host.ActiveWorkspace);
        Assert.False(rig.Vm.HasSession); // 활성이 일반 작업 공간으로 옮겨 갔다
    });

    // ---- 작업 공간 따라가기 · 배지 · 보기 모드 · 언어 ------------------------------------------

    [Fact]
    public Task The_list_follows_the_active_workspace_between_a_mapper_and_a_plain_sql_workspace() => Ui.Run(async () =>
    {
        var rig = await OpenAsync();
        var mapperWorkspace = rig.Host.ActiveWorkspace!;

        rig.Host.NewWorkspace();
        Assert.False(rig.Vm.HasSession);
        Assert.Empty(rig.Vm.Groups);
        Assert.Equal(string.Empty, rig.Vm.MapperPath);

        rig.Host.ActiveWorkspace = mapperWorkspace;
        Assert.True(rig.Vm.HasSession);
        Assert.Equal(6, rig.Vm.Groups.Count);
    });

    [Fact]
    public Task Badges_from_providers_are_drawn_per_item_and_redrawn_when_they_change() => Ui.Run(async () =>
    {
        var provider = new FakeBadges { ["updateEmpSal"] = new MyBatisStatementBadge("조합 오류 2", MyBatisBadgeKind.Error, "두 조합이 비어 있다") };
        var rig = await OpenAsync(hooks: new MyBatisHooks(badgeProviders: [provider]));

        var badge = Assert.Single(Node(rig.Vm, "updateEmpSal").Badges);
        Assert.Equal("조합 오류 2", badge.Text);
        Assert.True(badge.IsError);
        Assert.Empty(Node(rig.Vm, "selectEmp").Badges);

        provider["updateEmpSal"] = new MyBatisStatementBadge("조합 경고 1", MyBatisBadgeKind.Warning);
        provider.Raise();

        Assert.True(Assert.Single(Node(rig.Vm, "updateEmpSal").Badges).IsWarning);
    });

    [Fact]
    public Task A_view_mode_button_swaps_the_tree_for_the_mode_view_and_follows_availability() => Ui.Run(async () =>
    {
        var mode = new FakeViewMode();
        var rig = await OpenAsync(hooks: new MyBatisHooks(viewModes: [mode]));
        var view = new MyBatisStatementsView { DataContext = rig.Vm };
        var host = view.FindControl<ContentControl>("ModeHost")!;

        var button = Assert.Single(rig.Vm.ViewModes);
        Assert.Equal("폴더로 보기", button.Title);
        Assert.False(rig.Vm.IsModeActive);

        button.IsChecked = true;
        Assert.True(rig.Vm.IsModeActive);
        Assert.Same(button, rig.Vm.ActiveMode);
        Assert.Same(mode.View, host.Content);

        button.IsChecked = false;
        button.IsChecked = true;
        Assert.Equal(1, mode.Created); // 한 화면에서 껐다 켜도 View 는 한 번만 만든다

        mode.SetAvailable(false);
        Assert.False(button.IsAvailable);
        Assert.False(button.IsChecked);
        Assert.False(rig.Vm.IsModeActive);
        Assert.Null(rig.Vm.ActiveMode);
        Assert.Null(host.Content);
    });

    [Fact]
    public Task A_detached_statements_view_stops_following_the_view_model_and_catches_up_when_attached_again() => Ui.Run(async () =>
    {
        var mode = new FakeViewMode();
        var rig = await OpenAsync(hooks: new MyBatisHooks(viewModes: [mode]));
        var view = new MyBatisStatementsView { DataContext = rig.Vm };
        var host = view.FindControl<ContentControl>("ModeHost")!;
        var button = Assert.Single(rig.Vm.ViewModes);
        var window = new Window { Width = 320, Height = 480, Content = view };
        window.Show();
        button.IsChecked = true;
        Assert.Same(mode.View, host.Content);

        // 도킹 재배치로 떨어진 화면은 ViewModel 을 따라가지 않는다(오래 사는 ViewModel 이 버려진 화면을 붙잡지 않게).
        window.Content = null;
        button.IsChecked = false;
        Assert.Same(mode.View, host.Content);

        window.Content = view;
        Dispatcher.UIThread.RunJobs();
        Assert.Null(host.Content);
        window.Close();
    });

    [Fact]
    public Task Item_texts_follow_the_language_in_the_string_table() => Ui.Run(async () =>
    {
        var text = new ShellText();
        var rig = await OpenAsync(text: text);
        rig.Session.Workspace.ActiveTab!.Document.Insert(0, " ");

        text.SetLanguage("en");

        Assert.Equal("sql fragments", rig.Vm.Groups[4].Title);
        Assert.Equal("● Modified", Node(rig.Vm, "selectEmpList").ModifiedText);
        Assert.Equal("· used 2x", Node(rig.Vm, "empCols").UsageText);
        Assert.Equal("(left as is)", Node(rig.Vm, "empMap").KeepText);
    });

    // ---- 화면 ---------------------------------------------------------------------------------

    [Fact]
    public Task The_flat_rows_hold_a_header_line_then_the_items_of_each_expanded_group() => Ui.Run(async () =>
    {
        var rig = await OpenAsync();

        Assert.Equal(14, rig.Vm.Rows.Count); // 묶음 머리 6 + 요소 8
        Assert.Equal(rig.Vm.Groups[0], rig.Vm.Rows[0]);
        Assert.Equal(rig.Vm.Groups[0].Children, rig.Vm.Rows.Skip(1).Take(3));

        rig.Vm.Groups[0].IsExpanded = false; // select 묶음 접기: 항목 줄 3개만 빠진다
        Assert.Equal(11, rig.Vm.Rows.Count);
        Assert.Equal(rig.Vm.Groups[1], rig.Vm.Rows[1]);

        rig.Vm.Groups[0].IsExpanded = true;
        Assert.Equal(14, rig.Vm.Rows.Count);
        Assert.Equal(rig.Vm.Groups[0].Children, rig.Vm.Rows.Skip(1).Take(3));
    });

    [Fact]
    public Task Collapsing_a_group_keeps_the_collapsed_state_across_rebuilds_and_keeps_the_selected_statement() => Ui.Run(async () =>
    {
        var rig = await OpenAsync();
        rig.Vm.SelectNode(Node(rig.Vm, "countEmp"));
        rig.Vm.Groups[0].IsExpanded = false;

        Assert.Equal("countEmp", rig.Session.SelectedStatementId); // 접어도 대상 문장은 그대로다
        Assert.Null(rig.Vm.SelectedNode);

        rig.Dialogs.Choice = "insert";
        rig.Dialogs.Answer = "insertNew";
        await Run(rig, "oranda.mybatis.newStatement");

        Assert.False(rig.Vm.Groups[0].IsExpanded);
        Assert.DoesNotContain(rig.Vm.Groups[0].Children, rig.Vm.Rows.Contains); // 새로 지어도 접힌 채
        Assert.Contains(Node(rig.Vm, "insertNew"), rig.Vm.Rows);
    });

    [Fact]
    public Task The_view_draws_the_toolbar_with_tooltips_and_virtualizes_the_rows() => Ui.Run(async () =>
    {
        var big = new System.Text.StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<mapper namespace=\"t.Big\">\n");
        for (int i = 0; i < 400; i++)
        {
            big.Append(System.Globalization.CultureInfo.InvariantCulture, $"  <select id=\"s{i}\" resultType=\"int\">SELECT {i} FROM DUAL</select>\n");
        }

        big.Append("</mapper>\n");
        var rig = await OpenAsync(big.ToString());
        var view = new MyBatisStatementsView { DataContext = rig.Vm };
        var window = new Window { Width = 320, Height = 640, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var list = view.FindControl<ListBox>("Tree")!;
        Assert.Equal(401, list.ItemCount);
        Assert.InRange(list.GetRealizedContainers().Count(), 1, 60); // 보이는 줄만 화면 요소가 된다
        foreach (var name in ToolbarButtons)
        {
            var button = view.FindControl<Button>(name)!;
            Assert.True(button.IsVisible);
            Assert.NotNull(ToolTip.GetTip(button));
        }

        // 화면이 붙은 채로 목록을 다시 지어도(새 문장) 고른 문장이 비워지지 않는다.
        rig.Vm.SelectNode(Node(rig.Vm, "s3"));
        rig.Dialogs.Choice = "select";
        rig.Dialogs.Answer = "sNew";
        await Run(rig, "oranda.mybatis.newStatement");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("sNew", rig.Session.SelectedStatementId);
        Assert.Equal("sNew", rig.Vm.SelectedNode?.StatementId);

        // 묶음 접기: 줄이 1개로 줄고 대상 문장은 남는다. 다시 펼치면 선택도 돌아온다.
        rig.Vm.Groups[0].IsExpanded = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, list.ItemCount);
        Assert.Equal("sNew", rig.Session.SelectedStatementId);
        rig.Vm.Groups[0].IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(402, list.ItemCount);
        Assert.Equal("sNew", rig.Vm.SelectedNode?.StatementId);

        window.Close();
    });

    private sealed record Rig(SqlEditorHost Host, MyBatisStatementsViewModel Vm, MyBatisWorkspaceSession Session, FakeDialogs Dialogs, string Path);

    private sealed class FakeDialogs : IEditorDialogs
    {
        public string? Answer { get; set; }

        public string? Choice { get; set; }

        public bool Confirm { get; set; }

        public List<(string Title, string Initial)> Prompts { get; } = [];

        public List<(string Title, string Text)> Messages { get; } = [];

        public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string initial, CancellationToken ct)
        {
            Prompts.Add((title, initial));
            return Task.FromResult(Answer);
        }

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) => Task.FromResult(Choice);

        public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(Confirm);

        public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(false);

        public Task ShowMessageAsync(string title, string message, CancellationToken ct)
        {
            Messages.Add((title, message));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEvaluator(string sql) : IMyBatisEvaluator
    {
        public MyBatisElement? Statement { get; private set; }

        public MyBatisEvalOptions? Options { get; private set; }

        public int ValueCount { get; private set; } = -1;

        public EvalResult Evaluate(
            MyBatisElement statement,
            IMyBatisFragmentResolver fragments,
            IReadOnlyDictionary<string, object?> values,
            IReadOnlyDictionary<TextRange, bool>? blockOverrides,
            MyBatisEvalOptions? options = null)
        {
            Statement = statement;
            Options = options;
            ValueCount = values.Count;
            return new EvalResult(sql, [], [], [], []);
        }

        public IReadOnlyList<MyBatisParameter> ExtractParameters(MyBatisElement statement, IMyBatisFragmentResolver fragments) => [];

        public IReadOnlyList<ConditionCombination> Combinations(EvalResult allOn, int fullLimit = 8) => [];
    }

    private sealed class FakeBadges : IMyBatisStatementBadgeProvider
    {
        private readonly Dictionary<string, MyBatisStatementBadge> _byId = [];

        public MyBatisStatementBadge this[string id]
        {
            set => _byId[id] = value;
        }

        public event EventHandler? BadgesChanged;

        public IReadOnlyList<MyBatisStatementBadge> BadgesFor(IMyBatisWorkspaceSession session, string statementId) =>
            _byId.TryGetValue(statementId, out var badge) ? [badge] : [];

        public void Raise() => BadgesChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeViewMode : IMyBatisStatementsViewMode
    {
        private bool _available = true;

        public string Id => "folder";

        public string Title => "폴더로 보기";

        public string IconKey => "folder";

        public bool IsAvailable => _available;

        public int Created { get; private set; }

        public Control View { get; } = new TextBlock { Text = "folder tree" };

        public event EventHandler? AvailabilityChanged;

        public Control CreateView()
        {
            Created++;
            return View;
        }

        public void SetAvailable(bool value)
        {
            _available = value;
            AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
