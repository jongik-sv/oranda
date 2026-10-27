using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Localization;
using Oranda.App.Features.MyBatisWorkspace.Folder;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Statements;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>「매퍼 쿼리」 창 후속: id 열 넓힘, 상태 칸 툴팁, 검색(쿼리 목록·폴더 트리).</summary>
public sealed class StatementsSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-mbsearch-" + Guid.NewGuid().ToString("N"));

    public StatementsSearchTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private string WriteEmp()
    {
        var path = Path.Combine(_root, "EmpMapper.xml");
        File.WriteAllText(path, MapperSamples.EmpMapper);
        return path;
    }

    private static async Task<(SqlEditorHost Host, MyBatisStatementsViewModel Vm, MyBatisStatementsView View, Window Window)> ShowAsync(string path, int debounce = 0)
    {
        var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler()) };
        var vm = new MyBatisStatementsViewModel { Host = host, Text = new ShellText(), FilterDebounceMilliseconds = debounce };
        var view = new MyBatisStatementsView { DataContext = vm };
        var window = new Window { Width = 340, Height = 560, Content = view };
        window.Show();
        Pump();
        await host.OpenWorkspaceFileAsync(path, default);
        Pump();
        window.UpdateLayout();
        return (host, vm, view, window);
    }

    private static double IdWidth(MyBatisStatementsView view)
    {
        var cell = view.FindControl<ListBox>("Tree")!.GetVisualDescendants().OfType<HighlightTextBlock>().First(b => b.IsEffectivelyVisible).GetVisualAncestors().OfType<Border>().First();
        return cell.Bounds.Width;
    }

    // ---- id 열 ------------------------------------------------------------------------------------

    [Fact]
    public Task The_id_column_takes_most_of_a_340_wide_pane_and_grows_with_the_window() => Ui.Run(async () =>
    {
        var (_, _, view, window) = await ShowAsync(WriteEmp());
        try
        {
            Assert.True(IdWidth(view) >= 250, $"id 열 {IdWidth(view)}");
            window.Width = 480;
            Pump();
            window.UpdateLayout();
            Assert.True(IdWidth(view) >= 390, $"id 열 {IdWidth(view)}");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Status_tooltip_has_one_line_per_mark_none_without_marks_and_the_header_has_a_legend() => Ui.Run(async () =>
    {
        var (_, vm, view, window) = await ShowAsync(WriteEmp());
        try
        {
            var plain = vm.Groups.SelectMany(g => g.Children).First(n => n.StatementId == "selectEmp");
            Assert.Null(plain.StatusToolTip);

            var frag = vm.Groups.SelectMany(g => g.Children).First(n => n.StatementId == "empCols");
            Assert.Equal("2", frag.UsageNumber);
            Assert.Equal("쓰는 곳 2곳 (이 sql 조각을 include 하는 쿼리)", frag.StatusToolTip);

            vm.OpenNode(plain);
            vm.Host.ActiveWorkspace!.ActiveTab!.Document.Insert(0, " ");
            Assert.Equal("● 저장 안 한 고침 있음" + Environment.NewLine + "지금 보고 있는 탭 (편집기에 열림)", plain.StatusToolTip); // 탭으로 열려 지금 보는 쿼리라 끝 줄이 붙는다

            var header = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("grid-c-header"));
            var legend = header.GetVisualDescendants().OfType<Border>().Select(ToolTip.GetTip).OfType<string>().Single();
            Assert.Contains("● 저장 안 한 고침", legend, StringComparison.Ordinal);
            Assert.Contains("붉은 숫자 = 조합 오류 수", legend, StringComparison.Ordinal);
            Assert.Contains("흐린 숫자 = sql 조각을 쓰는 곳 수", legend, StringComparison.Ordinal);
            Assert.True(LiteralTranslator.Table.ContainsKey(legend), "머리글 범례의 영어 짝이 literals-en.json 에 있어야 한다");
            Assert.Contains("Unsaved edits", LiteralTranslator.Table[legend], StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    });

    // ---- 검색 ---------------------------------------------------------------------------------

    [Fact]
    public void Filter_rules_are_case_insensitive_substring_and_all_words()
    {
        var terms = MyBatisQueryFilter.Terms("  sel   LIST ");
        Assert.Equal(["sel", "LIST"], terms);
        Assert.True(MyBatisQueryFilter.Matches("selectEmpList", terms));
        Assert.False(MyBatisQueryFilter.Matches("selectEmp", terms));
        Assert.True(MyBatisQueryFilter.Matches("anything", MyBatisQueryFilter.Terms("   ")));
        Assert.False(MyBatisQueryFilter.Matches(null, terms));
        Assert.True(MyBatisQueryFilter.Matches("조회_목록", MyBatisQueryFilter.Terms("목록")));
        Assert.Equal([(0, 3), (9, 4)], MyBatisQueryFilter.Ranges("selectEmpList", terms));
        Assert.Equal([(0, 4)], MyBatisQueryFilter.Ranges("abcd", MyBatisQueryFilter.Terms("ab bc cd"))); // 겹치면 합친다
    }

    [Fact]
    public Task Typing_filters_ids_with_counts_in_group_heads_and_clearing_restores_all() => Ui.Run(async () =>
    {
        var (_, vm, _, window) = await ShowAsync(WriteEmp());
        try
        {
            int all = vm.Rows.Count;
            vm.SearchText = "EMP";
            Assert.Equal("EMP", vm.AppliedSearch);
            var select = vm.Groups.Single(g => g.Kind == MyBatisStatementKind.Select);
            Assert.Equal(["selectEmpList", "selectEmp", "countEmp"], select.Children.Select(n => n.StatementId));
            Assert.Equal("select (3)", select.Title);
            Assert.All(vm.Groups.SelectMany(g => g.Children), n => Assert.Equal("EMP", n.Highlight));

            vm.SearchText = "select list"; // 낱말 둘: 모두 포함
            Assert.Equal(["selectEmpList"], vm.Groups.SelectMany(g => g.Children).Select(n => n.StatementId));
            Assert.Equal(2, vm.Rows.Count); // 묶음 머리 + 1
            Assert.False(vm.ShowsNoMatch);

            vm.SearchText = "zzz";
            Assert.Empty(vm.Rows);
            Assert.True(vm.ShowsNoMatch);

            vm.ClearSearch();
            Assert.Equal(string.Empty, vm.SearchText);
            Assert.Equal(all, vm.Rows.Count);
            Assert.False(vm.ShowsNoMatch);
            Assert.Equal("select", vm.Groups.Single(g => g.Kind == MyBatisStatementKind.Select).Title);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Filtered_rows_keep_selection_and_open() => Ui.Run(async () =>
    {
        var (_, vm, _, window) = await ShowAsync(WriteEmp());
        try
        {
            vm.SearchText = "countemp";
            var node = vm.FirstItem()!;
            Assert.Equal("countEmp", node.StatementId);
            vm.SelectNode(node);
            Assert.Equal("countEmp", vm.SelectedNode?.StatementId);
            vm.OpenNode(node);
            Assert.Equal("countEmp", vm.Host.ActiveWorkspace!.ActiveTab!.Title);

            vm.SearchText = "updateEmp"; // 고른 문장이 거른 목록에 없으면 목록 선택만 빠진다
            Assert.Null(vm.SelectedNode);
            vm.ClearSearch();
            Assert.Equal("countEmp", vm.SelectedNode?.StatementId); // 세션의 고른 문장이 되살아난다
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Debounce_waits_before_filtering() => Ui.Run(async () =>
    {
        var (_, vm, _, window) = await ShowAsync(WriteEmp(), debounce: 150);
        try
        {
            var all = vm.Rows.Count;
            vm.SearchText = "countEmp";
            Assert.Equal(string.Empty, vm.AppliedSearch); // 아직 안 거름
            Assert.Equal(all, vm.Rows.Count);
            vm.FlushSearch();
            Assert.Equal("countEmp", vm.AppliedSearch);
            Assert.Equal(2, vm.Rows.Count);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Keyboard_ctrl_f_focuses_search_down_selects_first_enter_opens_and_escape_clears() => Ui.Run(async () =>
    {
        var (_, vm, view, window) = await ShowAsync(WriteEmp());
        try
        {
            var box = view.FindControl<TextBox>("SearchBox")!;
            var tree = view.FindControl<ListBox>("Tree")!;
            var first = vm.FirstItem()!;
            vm.SelectNode(first);
            Pump();
            window.UpdateLayout();
            Assert.True(tree.ContainerFromItem(first)!.Focus());
            Pump();
            window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, null);
            Pump();
            Assert.True(box.IsFocused);

            vm.SearchText = "empl";
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Pump();
            Assert.Equal("selectEmpList", vm.SelectedNode?.StatementId);

            box.Focus();
            Pump();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Pump();
            Assert.Equal("selectEmpList", vm.Host.ActiveWorkspace!.ActiveTab!.Title);

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Pump();
            Assert.Equal(string.Empty, vm.SearchText);
            Assert.Equal(string.Empty, vm.AppliedSearch);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Folder_tree_filters_files_with_matching_queries_and_restores_expansion() => Ui.Run(async () =>
    {
        Directory.CreateDirectory(Path.Combine(_root, "hr"));
        Directory.CreateDirectory(Path.Combine(_root, "etc"));
        File.WriteAllText(Path.Combine(_root, "hr", "EmpMapper.xml"), MapperSamples.EmpMapper);
        File.WriteAllText(Path.Combine(_root, "etc", "OtherMapper.xml"),
            "<?xml version=\"1.0\"?><mapper namespace=\"x.Other\"><select id=\"ping\" resultType=\"int\">SELECT 1 FROM DUAL</select></mapper>");
        using var folder = new MapperFolderService(dataDirectory: null);
        await folder.OpenAsync(_root, default);
        var vm = new MapperFolderTreeViewModel(folder)
        {
            Host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler()) },
            Text = new ShellText(),
            FilterDebounceMilliseconds = 0,
        };
        Assert.Equal(["etc", "hr"], vm.Roots.Select(n => n.Name));

        vm.SearchText = "COUNT emp";
        var hr = Assert.Single(vm.Roots);
        Assert.Equal("hr", hr.Name);
        Assert.True(hr.IsExpanded);
        var file = Assert.Single(hr.Children);
        Assert.Equal("EmpMapper.xml", file.Name);
        Assert.Contains("(1)", file.Detail, StringComparison.Ordinal);
        Assert.Equal(["countEmp"], file.Children.Select(c => c.Name));
        Assert.Equal("COUNT emp", file.Children[0].Highlight);
        Assert.Equal("countEmp", vm.FirstElement()!.Name);

        vm.SearchText = "nothing-here";
        Assert.Empty(vm.Roots);
        Assert.True(vm.ShowsNoMatch);

        vm.ClearSearch();
        Assert.Equal(["etc", "hr"], vm.Roots.Select(n => n.Name));
        Assert.False(vm.Roots[1].IsExpanded); // 거르느라 펼친 상태는 남지 않는다
        Assert.False(vm.ShowsNoMatch);
        vm.Dispose();
    });
}
