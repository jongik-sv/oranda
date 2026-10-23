using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.MyBatisWorkspace.Folder;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.LogPaste;
using Oranda.App.Features.MyBatisWorkspace.Statements;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.MyBatis;
using Oranda.Core.Shell;
using Oranda.Language.MyBatis;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// TSK-03-07 acceptance: 매퍼 2개 이상인 폴더에서 다른 파일 조각을 include 한 문장의 펼친 SQL 이 맞다 /
/// 폴더 훑기(mapper 아닌 xml 거름), 다른 파일 include·순환, 문장 찾기, 폴더 안 파일 바뀜 감시.
/// 그 밖에 쓰는 곳 찾기, 최근 폴더, 「문장」 창 폴더 트리 모드(세션 없이 보임), 문맥 키. 성능(매퍼 2,000개)은 TSK-04-02.
/// </summary>
public sealed class MapperFolderTests : IDisposable
{
    private const string CommonMapper = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE mapper PUBLIC "-//mybatis.org//DTD Mapper 3.0//EN" "http://mybatis.org/dtd/mybatis-3-mapper.dtd">
        <mapper namespace="com.acme.common.CommonMapper">
          <sql id="empCols">E.EMPNO, E.ENAME<include refid="extraCols"/></sql>
          <sql id="pageTail">FETCH FIRST 10 ROWS ONLY</sql>
          <resultMap id="empMap" type="map"><id column="EMPNO" property="empNo"/></resultMap>
          <select id="countAll" resultType="int">SELECT COUNT(*) FROM EMP <include refid="pageTail"/></select>
        </mapper>
        """;

    private const string EmpMapper = """
        <?xml version="1.0" encoding="UTF-8"?>
        <mapper namespace="com.acme.hr.EmpMapper">
          <sql id="extraCols">, E.SAL</sql>
          <select id="selectEmpList" resultMap="com.acme.common.CommonMapper.empMap">
            SELECT <include refid="com.acme.common.CommonMapper.empCols"/> FROM EMP E
            <include refid="com.acme.common.CommonMapper.pageTail"/>
          </select>
          <select id="selectEmp" resultType="map">SELECT * FROM EMP WHERE EMPNO = #{empNo}</select>
        </mapper>
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-mbfolder-" + Guid.NewGuid().ToString("N"));

    public MapperFolderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "hr"));
        Directory.CreateDirectory(Path.Combine(_root, "common"));
        File.WriteAllText(Path.Combine(_root, "common", "CommonMapper.xml"), CommonMapper);
        File.WriteAllText(Path.Combine(_root, "hr", "EmpMapper.xml"), EmpMapper);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string EmpPath => Path.Combine(_root, "hr", "EmpMapper.xml");

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    // 「문장」 창 화면을 창에 띄워 그 화면이 만든 폴더 트리를 꺼낸다(모드 View 는 화면마다 만든다).
    private static (Window Window, MapperFolderTreeView Tree) ShowTree(MyBatisStatementsViewModel vm)
    {
        var window = new Window { Width = 320, Height = 480, Content = new MyBatisStatementsView { DataContext = vm } };
        window.Show();
        Pump();
        window.UpdateLayout();
        return (window, Assert.Single(((Visual)window.Content!).GetVisualDescendants().OfType<MapperFolderTreeView>()));
    }

    private static SqlEditorHost Host(MapperFolderService folder, FakeDialogs? dialogs = null)
    {
        var host = new SqlEditorHost
        {
            MyBatisHooks = new MyBatisHooks(
                openHandler: new MyBatisOpenHandler(),
                viewModes: [new FolderTreeViewMode(folder)],
                statementLocators: [new FolderStatementLocator(folder)],
                fragmentResolvers: [new FolderFragmentResolver(folder)]),
        };
        if (dialogs is not null)
        {
            host.Dialogs = dialogs;
        }

        return host;
    }

    // ---- 훑기 -----------------------------------------------------------------------------------

    [Fact]
    public async Task Scanning_keeps_only_xml_whose_root_is_mapper()
    {
        File.WriteAllText(Path.Combine(_root, "pom.xml"), "<?xml version=\"1.0\"?>\n<!-- <mapper> 가 주석에 있어도 -->\n<project><mapper/></project>");
        File.WriteAllText(Path.Combine(_root, "common", "mybatis-config.xml"), "<?xml version=\"1.0\"?><!DOCTYPE configuration><configuration/>");
        File.WriteAllText(Path.Combine(_root, "hr", "Broken.xml"), "<mapper namespace=\"x\"><select id=\"a\">");
        File.WriteAllText(Path.Combine(_root, "hr", "notes.txt"), "<mapper namespace=\"y\"/>");

        var index = await MapperFolderIndex.ScanAsync(_root, default);

        Assert.Equal(["CommonMapper.xml", "EmpMapper.xml"], index.Files.Select(f => Path.GetFileName(f.Path)));
        Assert.Equal(3, index.SkippedCount);
        Assert.Equal("com.acme.hr.EmpMapper", index.Files[1].Namespace);
        Assert.Equal(["extraCols", "selectEmpList", "selectEmp"], index.Files[1].Elements.Select(e => e.Id));
    }

    [Fact]
    public void Build_output_and_hidden_folders_are_not_scanned()
    {
        Directory.CreateDirectory(Path.Combine(_root, "target", "classes"));
        Directory.CreateDirectory(Path.Combine(_root, ".idea"));
        File.WriteAllText(Path.Combine(_root, "target", "classes", "EmpMapper.xml"), EmpMapper); // 빌드 복사본
        File.WriteAllText(Path.Combine(_root, ".idea", "workspace.xml"), "<project/>");

        var index = MapperFolderIndex.Scan(_root, default);

        Assert.Equal(2, index.Files.Count);
        Assert.Equal(0, index.SkippedCount);
        Assert.True(index.IsExcluded(Path.Combine(_root, "target", "classes", "EmpMapper.xml")));
    }

    [Fact]
    public Task A_file_changed_while_scanning_is_read_again_when_the_scan_ends() => Ui.Run(async () =>
    {
        using var folder = new MapperFolderService(dataDirectory: null) { DebounceMilliseconds = 600_000 };
        var opening = folder.OpenAsync(_root, default);
        Assert.True(folder.IsScanning);

        File.WriteAllText(EmpPath, EmpMapper.Replace("selectEmp\"", "selectEmpNew\"", StringComparison.Ordinal));
        folder.Reload(EmpPath); // 감시가 알린 것과 같다: 훑는 중이면 끝난 뒤 다시 읽는다

        Assert.True(await opening);
        Assert.Single(folder.Index!.FindStatements("com.acme.hr.EmpMapper.selectEmpNew", exact: true));
    });

    // ---- 다른 파일 include ----------------------------------------------------------------------

    [Fact]
    public Task A_statement_including_fragments_of_another_mapper_expands_to_the_right_sql() => Ui.Run(async () =>
    {
        using var folder = new MapperFolderService(dataDirectory: null);
        Assert.True(await folder.OpenAsync(_root, default));
        var host = Host(folder);

        await host.OpenWorkspaceFileAsync(EmpPath, default);
        var session = (MyBatisWorkspaceSession)host.ActiveWorkspace!.MyBatis!;
        var statement = session.Document.Find("selectEmpList")!;
        var result = new MyBatisEvaluator().Evaluate(statement, session.Fragments, new Dictionary<string, object?>(), null);

        // 다른 파일 조각(empCols·pageTail) + 그 조각 안 중첩 include 는 문장 매퍼(EmpMapper)의 extraCols 로 찾는다(MyBatis 와 같다).
        Assert.Empty(result.Issues);
        Assert.Equal("SELECT E.EMPNO, E.ENAME , E.SAL FROM EMP E FETCH FIRST 10 ROWS ONLY", Normalize(result.Sql)); // 조각 앞뒤 공백은 계산기(02-02) 몫
    });

    [Fact]
    public void Fragments_resolve_by_qualified_name_or_current_namespace_and_always_carry_their_namespace()
    {
        var index = MapperFolderIndex.Scan(_root, default);

        var qualified = index.ResolveFragment("com.acme.common.CommonMapper.pageTail", "com.acme.hr.EmpMapper")!;
        Assert.Equal("com.acme.common.CommonMapper", qualified.Namespace);
        Assert.EndsWith("CommonMapper.xml", qualified.MapperPath, StringComparison.Ordinal);
        Assert.Equal("extraCols", index.ResolveFragment("extraCols", "com.acme.hr.EmpMapper")!.Id);
        Assert.Null(index.ResolveFragment("pageTail", "com.acme.hr.EmpMapper")); // 이름만 쓰면 지금 매퍼 안에서만 찾는다
        Assert.Null(index.ResolveFragment("com.acme.none.NoMapper.x", null));
    }

    [Fact]
    public Task An_include_cycle_across_two_mappers_is_reported_not_followed() => Ui.Run(async () =>
    {
        File.WriteAllText(Path.Combine(_root, "hr", "A.xml"), """
            <mapper namespace="a.A">
              <sql id="f">A1 <include refid="b.B.g"/></sql>
              <select id="q">SELECT <include refid="a.A.f"/></select>
            </mapper>
            """);
        File.WriteAllText(Path.Combine(_root, "hr", "B.xml"), """
            <mapper namespace="b.B">
              <sql id="g">B1 <include refid="a.A.f"/></sql>
            </mapper>
            """);
        using var folder = new MapperFolderService(dataDirectory: null);
        await folder.OpenAsync(_root, default);
        var host = Host(folder);

        await host.OpenWorkspaceFileAsync(Path.Combine(_root, "hr", "A.xml"), default);
        var session = (MyBatisWorkspaceSession)host.ActiveWorkspace!.MyBatis!;
        var result = new MyBatisEvaluator().Evaluate(session.Document.Find("q")!, session.Fragments, new Dictionary<string, object?>(), null);

        var cycle = Assert.Single(result.Issues, i => i.Kind == MyBatisEvalIssueKind.IncludeCycle);
        Assert.Contains("a.A.f → b.B.g → a.A.f", cycle.Reason, StringComparison.Ordinal);
    });

    // ---- 쓰는 곳 찾기 ---------------------------------------------------------------------------

    [Fact]
    public void Usages_of_a_fragment_and_a_result_map_are_found_across_the_folder()
    {
        var index = MapperFolderIndex.Scan(_root, default);
        const string ns = "com.acme.common.CommonMapper";

        var pageTail = index.FindUsages(ns, "pageTail", MyBatisStatementKind.Sql);
        Assert.Equal(["countAll", "selectEmpList"], pageTail.Select(u => u.ElementId).Order(StringComparer.Ordinal));
        Assert.Equal(["selectEmpList"], index.FindUsages(ns, "empMap", MyBatisStatementKind.ResultMap).Select(u => u.ElementId));
        // 이름만 쓴 include 는 그 글이 든 매퍼의 조각으로 센다: CommonMapper.empCols 안 extraCols 는 EmpMapper 의 쓰는 곳이 아니다.
        Assert.Empty(index.FindUsages("com.acme.hr.EmpMapper", "extraCols", MyBatisStatementKind.Sql));
    }

    // ---- 문장 찾기 ------------------------------------------------------------------------------

    [Fact]
    public async Task Statements_are_found_by_qualified_name_or_part_of_the_id()
    {
        using var folder = new MapperFolderService(dataDirectory: null);
        await folder.OpenAsync(_root, default);
        var locator = new FolderStatementLocator(folder);

        var exact = Assert.Single(await locator.FindAsync("com.acme.hr.EmpMapper.selectEmp", exact: true, default));
        Assert.Equal(("selectEmp", "folder"), (exact.StatementId, exact.Source));
        Assert.Equal(EmpPath, exact.MapperPath);
        Assert.Equal(["selectEmpList", "selectEmp"], (await locator.FindAsync("selectemp", exact: false, default)).Select(l => l.StatementId));
        Assert.Empty(await locator.FindAsync("extraCols", exact: false, default)); // sql 조각은 문장이 아니다
    }

    [Fact]
    public Task Find_statement_merges_locators_and_opens_the_one_picked() => Ui.Run(async () =>
    {
        using var folder = new MapperFolderService(dataDirectory: null);
        await folder.OpenAsync(_root, default);
        var dialogs = new FakeDialogs { Answer = "selectEmp" };
        var host = Host(folder, dialogs);
        host.MyBatisHooks = new MyBatisHooks(
            openHandler: new MyBatisOpenHandler(),
            statementLocators: [new FolderStatementLocator(folder), new FolderStatementLocator(folder)]); // 같은 결과 둘 = 하나로
        dialogs.Choose = items => items.Single(i => i.StartsWith("com.acme.hr.EmpMapper.selectEmp —", StringComparison.Ordinal));

        await new MyBatisFolderCommands { Host = host, Folder = folder }.ExecuteAsync(new CommandContext("oranda.mybatis.findStatement", new ContextKeyService()), default);

        Assert.Equal(2, dialogs.LastItems!.Count); // selectEmpList · selectEmp
        var session = (MyBatisWorkspaceSession)host.ActiveWorkspace!.MyBatis!;
        Assert.Equal(EmpPath, session.MapperPath);
        Assert.Equal("selectEmp", session.SelectedStatementId);
        Assert.Contains(host.ActiveWorkspace.Tabs, t => t == host.ActiveWorkspace.ActiveTab && t.Text.Contains("WHERE EMPNO", StringComparison.Ordinal));
    });

    [Fact]
    public Task Pasting_a_log_finds_a_statement_in_an_unopened_mapper_of_the_folder_and_opens_it() => Ui.Run(async () =>
    {
        using var folder = new MapperFolderService(dataDirectory: null);
        await folder.OpenAsync(_root, default);
        var host = Host(folder);
        await host.OpenWorkspaceFileAsync(Path.Combine(_root, "common", "CommonMapper.xml"), default);
        var current = host.ActiveWorkspace!.MyBatis!;
        var store = new FakeMyBatisScenarioStore();
        const string log = """
            14:05:01.123 [main] DEBUG com.acme.hr.EmpMapper.selectEmp - ==>  Preparing: SELECT * FROM EMP WHERE EMPNO = ?
            14:05:01.130 [main] DEBUG com.acme.hr.EmpMapper.selectEmp - ==> Parameters: 7499(Integer)
            """;

        var result = await new MyBatisLogPasteFlow(host, new MyBatisEvaluator(), store, new FakeDialogs()).RunAsync(log, current, default);

        var opened = Assert.Single(result!.Opened);
        Assert.Equal((EmpPath, "selectEmp"), (opened.MapperPath, opened.StatementId));
        var session = (MyBatisWorkspaceSession)host.Workspaces.Single(w => w.SourcePath == EmpPath).MyBatis!;
        Assert.Equal("selectEmp", session.SelectedStatementId);
        Assert.Equal(new MyBatisScenarioValue(MyBatisScenarioValueKind.Number, "7499"), Assert.Single(store.All).Values["empNo"]);
        Assert.Equal(0, result.Fallback);
    });

    // ---- 감시 -----------------------------------------------------------------------------------

    [Fact]
    public async Task Reloading_a_changed_file_updates_added_removed_and_no_longer_mapper_files()
    {
        using var folder = new MapperFolderService(dataDirectory: null) { DebounceMilliseconds = 600_000 }; // 실제 감시 사건은 이 시험에 끼지 않게
        await folder.OpenAsync(_root, default);
        int changed = 0;
        folder.Changed += (_, _) => changed++;

        File.WriteAllText(EmpPath, EmpMapper.Replace("<select id=\"selectEmp\"", "<delete id=\"deleteEmp\">DELETE FROM EMP</delete>\n  <select id=\"selectEmp\"", StringComparison.Ordinal));
        folder.Reload(EmpPath);
        Assert.Contains(folder.Index!.Files[1].Elements, e => e.Id == "deleteEmp");

        var dept = Path.Combine(_root, "hr", "DeptMapper.xml");
        File.WriteAllText(dept, "<mapper namespace=\"com.acme.hr.DeptMapper\"><select id=\"selectDept\">SELECT 1</select></mapper>");
        folder.Reload(dept);
        Assert.Equal(3, folder.Index.Files.Count);

        File.WriteAllText(dept, "<beans/>");
        folder.Reload(dept);
        Assert.Equal(2, folder.Index.Files.Count);
        Assert.Equal(1, folder.Index.SkippedCount);

        File.Delete(dept);
        folder.Reload(dept);
        Assert.Equal(0, folder.Index.SkippedCount);
        folder.Reload(Path.Combine(Path.GetTempPath(), "Outside.xml")); // 폴더 밖은 무시
        Assert.Equal(4, changed);
    }

    [Fact]
    public async Task The_folder_watcher_picks_up_a_new_mapper_written_into_the_folder()
    {
        using var folder = new MapperFolderService(dataDirectory: null) { DebounceMilliseconds = 50 };
        await folder.OpenAsync(_root, default);

        File.WriteAllText(Path.Combine(_root, "hr", "DeptMapper.xml"), "<mapper namespace=\"com.acme.hr.DeptMapper\"><select id=\"selectDept\">SELECT 1</select></mapper>");

        // 감시 사건(macOS FSEvents)은 늦게 온다: 몇 초 안에 들어오면 된다. 폴더를 연 스레드에 문맥이 없으면 그 자리에서 색인을 바꾼다.
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until && folder.Index!.Files.Count < 3)
        {
            await Task.Delay(50);
        }

        Assert.Contains(folder.Index!.FindStatements("com.acme.hr.DeptMapper.selectDept", exact: true), l => l.StatementId == "selectDept");
    }

    // ---- 최근 폴더·문맥 키 ---------------------------------------------------------------------

    [Fact]
    public async Task Opened_folders_are_remembered_most_recent_first_and_the_open_key_follows_the_folder()
    {
        var data = Path.Combine(_root, "data");
        var other = Path.Combine(_root, "hr");
        var keys = new ContextKeyService();
        using (var folder = new MapperFolderService(data, keys))
        {
            await folder.OpenAsync(_root, default);
            Assert.True(keys.Get(MapperFolderService.FolderOpenKey));
            await folder.OpenAsync(other, default);
            await folder.OpenAsync(_root, default);
            folder.Close();
            Assert.False(keys.Get(MapperFolderService.FolderOpenKey));
            Assert.False(await folder.OpenAsync(Path.Combine(_root, "none"), default));
        }

        using var again = new MapperFolderService(data);
        Assert.Equal([Path.GetFullPath(_root), Path.GetFullPath(other)], again.RecentFolders);
    }

    [Fact]
    public Task Open_folder_offers_recent_folders_and_opens_the_chosen_one() => Ui.Run(async () =>
    {
        using var folder = new MapperFolderService(Path.Combine(_root, "data"));
        await folder.OpenAsync(Path.Combine(_root, "hr"), default);
        folder.Close();
        var dialogs = new FakeDialogs();
        dialogs.Choose = items => items[^1]; // 「다른 폴더…」
        var host = Host(folder, dialogs);

        await new MyBatisFolderCommands { Host = host, Folder = folder, PickFolder = _ => Task.FromResult<string?>(_root) }
            .ExecuteAsync(new CommandContext("oranda.mybatis.openFolder", new ContextKeyService()), default);

        Assert.Equal([Path.GetFullPath(Path.Combine(_root, "hr")), MyBatisFolderCommands.OtherFolderItem], dialogs.LastItems);
        Assert.Equal(Path.GetFullPath(_root), folder.Index!.Root);
        Assert.Equal(2, folder.Index.Files.Count);
    });

    // ---- 「문장」 창 폴더 트리 모드 -------------------------------------------------------------

    [Fact]
    public Task The_statements_panel_shows_the_folder_tree_without_a_mybatis_document_and_hides_it_when_the_folder_closes() => Ui.Run(async () =>
    {
        using var folder = new MapperFolderService(dataDirectory: null);
        var host = Host(folder);
        var vm = new MyBatisStatementsViewModel { Host = host, Text = new ShellText() };
        vm.Attach();
        Assert.False(vm.ShowsBody);

        await folder.OpenAsync(_root, default);

        Assert.True(vm.ShowsBody);
        Assert.False(vm.HasSession);
        var mode = Assert.Single(vm.ViewModes);
        Assert.True(mode.IsChecked);
        Assert.True(vm.IsModeActive);
        Assert.Same(mode, vm.ActiveMode);
        var (window, tree) = ShowTree(vm);
        var treeVm = Assert.IsType<MapperFolderTreeViewModel>(tree.DataContext);
        Assert.Equal(["common", "hr"], treeVm.Roots.Select(n => n.Name));

        folder.Close();
        Assert.False(vm.ShowsBody);
        Assert.False(vm.IsModeActive);
        Assert.Null(window.GetVisualDescendants().OfType<MapperFolderTreeView>().FirstOrDefault());
        window.Close();
        vm.Dispose();
        treeVm.Dispose();
    });

    [Fact]
    public Task A_statements_panel_created_after_the_folder_opened_still_switches_to_the_folder_tree() => Ui.Run(async () =>
    {
        using var folder = new MapperFolderService(dataDirectory: null);
        var host = Host(folder);
        await folder.OpenAsync(_root, default);

        // 폴더 열기가 창을 띄우고 창은 다음 그리기에 붙는다: 그때 처음 모드를 싣는다.
        var vm = new MyBatisStatementsViewModel { Host = host, Text = new ShellText() };
        vm.Attach();
        Pump();

        Assert.True(Assert.Single(vm.ViewModes).IsChecked);
        var (window, tree) = ShowTree(vm);
        window.Close();
        ((MapperFolderTreeViewModel)tree.DataContext!).Dispose();
        vm.Dispose();
    });

    [Fact]
    public Task Two_statements_views_on_one_view_model_each_show_their_own_folder_tree() => Ui.Run(async () =>
    {
        using var folder = new MapperFolderService(dataDirectory: null);
        var vm = new MyBatisStatementsViewModel { Host = Host(folder), Text = new ShellText() };
        await folder.OpenAsync(_root, default);

        // 도킹 재배치·떠 있는 창처럼 같은 ViewModel 을 보는 「문장」 창 화면이 둘이다(2026-10-24 앱 크래시: 트리 화면 하나를 두 부모에 붙임).
        var (first, a) = ShowTree(vm);
        var (second, b) = ShowTree(vm);
        first.UpdateLayout();

        Assert.True(vm.IsModeActive);
        Assert.NotSame(a, b);
        Assert.Same(a, Assert.Single(first.GetVisualDescendants().OfType<MapperFolderTreeView>()));
        Assert.True(a.IsEffectivelyVisible);
        Assert.True(b.IsEffectivelyVisible);

        first.Close();
        second.Close();
        vm.Dispose();
        ((MapperFolderTreeViewModel)a.DataContext!).Dispose();
        ((MapperFolderTreeViewModel)b.DataContext!).Dispose();
    });

    [Fact]
    public Task The_folder_tree_lists_folder_file_and_statements_and_opens_a_statement_on_double_click() => Ui.Run(async () =>
    {
        using var folder = new MapperFolderService(dataDirectory: null);
        await folder.OpenAsync(_root, default);
        var host = Host(folder);
        using var tree = new MapperFolderTreeViewModel(folder) { Host = host, Text = new ShellText() };

        var hr = tree.Roots.Single(n => n.Name == "hr");
        var emp = Assert.Single(hr.Children);
        Assert.Equal(("EmpMapper.xml", "com.acme.hr.EmpMapper"), (emp.Name, emp.Detail));
        Assert.Equal(["extraCols", "selectEmpList", "selectEmp"], emp.Children.Select(n => n.Name));
        Assert.StartsWith("매퍼 2개", tree.StatusText, StringComparison.Ordinal);

        await tree.OpenNodeAsync(emp.Children.Single(n => n.Name == "selectEmpList"));
        Pump();

        var session = (MyBatisWorkspaceSession)host.ActiveWorkspace!.MyBatis!;
        Assert.Equal(("selectEmpList", EmpPath), (session.SelectedStatementId, session.MapperPath));

        // 쓰는 곳 찾기: CommonMapper 의 pageTail
        var common = tree.Roots.Single(n => n.Name == "common").Children.Single();
        tree.FindUsages(common.Children.Single(n => n.Name == "pageTail"));
        Assert.True(tree.HasUsages);
        Assert.Equal(2, tree.Usages.Count);

        // 펼침은 다시 훑어도 남는다
        hr.IsExpanded = true;
        await tree.RefreshAsync();
        Assert.True(tree.Roots.Single(n => n.Name == "hr").IsExpanded);
    });

    private static string Normalize(string sql) => string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed class FakeDialogs : IEditorDialogs
    {
        public string? Answer { get; set; }

        public Func<IReadOnlyList<string>, string?>? Choose { get; set; }

        public IReadOnlyList<string>? LastItems { get; private set; }

        public List<string> Messages { get; } = [];

        public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult(Answer);

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct)
        {
            LastItems = items;
            return Task.FromResult(Choose?.Invoke(items));
        }

        public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(false);

        public Task ShowMessageAsync(string title, string message, CancellationToken ct)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
