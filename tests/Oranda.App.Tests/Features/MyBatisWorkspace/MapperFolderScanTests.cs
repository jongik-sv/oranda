using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.MyBatisWorkspace.Folder;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Language.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// TSK-03-11~13 성능 고침의 기능 시험(시간은 재지 않는다 — 수치는 TSK-04-02 결과서).
/// 03-13: 병렬 훑기 결과가 차례 훑기와 같은 순서·같은 거름, 파일 한 번 읽기(앞부분 판정·큰 파일), 취소·읽을 수 없는 파일.
/// 03-12: 파일 노드의 요소 자식은 처음 읽을 때 만든다, 트리는 최상위 노드를 몇 개씩 붙이고 Pump 뒤에는 다 붙는다.
/// 03-11: 매퍼를 열면 파서 데우기가 시작된다.
/// </summary>
public sealed class MapperFolderScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-mbscan-" + Guid.NewGuid().ToString("N"));

    public MapperFolderScanTests() => Directory.CreateDirectory(_root);

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

    private static string Mapper(string ns, int statements = 2) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<mapper namespace=\"{ns}\">\n"
        + string.Concat(Enumerable.Range(0, statements).Select(i => $"  <select id=\"s{i}\">SELECT {i} FROM dual</select>\n"))
        + "</mapper>\n";

    private string Write(string relative, string text, bool bom = false)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(bom));
        return path;
    }

    // 하위 폴더 여럿에 매퍼·매퍼 아닌 xml·깨진 xml·빈 xml·큰 매퍼·큰 매퍼 아닌 xml 을 섞는다.
    private void WriteMixedFolder()
    {
        for (var i = 0; i < 60; i++)
        {
            var folder = "m" + (i % 7).ToString(CultureInfo.InvariantCulture);
            Write(Path.Combine(folder, $"Map{i:D3}.xml"), Mapper("t.M" + i.ToString(CultureInfo.InvariantCulture)), bom: i % 5 == 0);
        }

        Write(Path.Combine("m1", "pom.xml"), "<?xml version=\"1.0\"?><project><mapper/></project>");
        Write(Path.Combine("m2", "Broken.xml"), "<mapper namespace=\"b\"><select id=\"a\">");
        Write(Path.Combine("m3", "Empty.xml"), string.Empty);
        Write(Path.Combine("m4", "BigMapper.xml"), Mapper("t.Big", 400)); // 앞부분(8 KB)보다 크다
        Write(Path.Combine("m5", "BigData.xml"), "<?xml version=\"1.0\"?><data>" + new string('x', 200_000) + "</data>");
        Write("한글 폴더/Mapper 한글.xml", Mapper("t.한글"));
    }

    [Fact]
    public void A_parallel_scan_gives_the_same_files_in_path_order_and_the_same_skips_as_a_sequential_scan()
    {
        WriteMixedFolder();

        var sequential = MapperFolderIndex.Scan(_root, 1, default);
        var parallel = MapperFolderIndex.Scan(_root, 8, default);

        Assert.Equal(62, parallel.Files.Count);
        Assert.Equal(sequential.Files.Select(f => f.Path), parallel.Files.Select(f => f.Path));
        Assert.Equal(sequential.Files.Select(f => f.Namespace), parallel.Files.Select(f => f.Namespace));
        Assert.Equal(sequential.Files.Select(f => f.Elements.Count), parallel.Files.Select(f => f.Elements.Count));
        Assert.Equal(parallel.Files.Select(f => f.Path).Order(MapperFolderIndex.PathComparer), parallel.Files.Select(f => f.Path));
        Assert.Equal(sequential.Skipped.Order(), parallel.Skipped.Order());
        Assert.Equal(["BigData.xml", "Broken.xml", "Empty.xml", "pom.xml"], parallel.Skipped.Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_mapper_larger_than_the_sniffed_head_is_read_to_the_end_with_one_open()
    {
        var path = Write("Big.xml", Mapper("t.Big", 400), bom: true);
        Assert.True(new FileInfo(path).Length > 8 * 1024);

        var file = MapperFolderIndex.ReadFile(path)!;

        Assert.Equal("t.Big", file.Namespace);
        Assert.Equal(400, file.Elements.Count);
        Assert.Equal("<select id=\"s399\">SELECT 399 FROM dual</select>", file.Elements[^1].Text);
    }

    [Theory]
    [InlineData(8 * 1024 - 1)]
    [InlineData(8 * 1024)]
    [InlineData(8 * 1024 + 1)]
    public void Mappers_around_the_head_size_are_read_whole(int size)
    {
        var head = "<mapper namespace=\"t.Edge\"><select id=\"a\">SELECT 1 FROM dual</select><!--";
        var tail = "--></mapper>";
        var text = head + new string('x', size - head.Length - tail.Length) + tail;
        var path = Write("Edge.xml", text);
        Assert.Equal(size, new FileInfo(path).Length);

        var file = MapperFolderIndex.ReadFile(path)!;

        Assert.Equal("t.Edge", file.Namespace);
        Assert.Equal("a", Assert.Single(file.Elements).Id);
    }

    [Fact]
    public void A_big_xml_that_is_not_a_mapper_is_skipped_from_its_head()
    {
        var path = Write("Data.xml", "<?xml version=\"1.0\"?><data>" + new string('x', 1_000_000) + "</data>");
        Assert.Null(MapperFolderIndex.ReadFile(path));
    }

    [Fact]
    public void A_missing_file_reads_as_null()
    {
        Assert.Null(MapperFolderIndex.ReadFile(Path.Combine(_root, "none.xml")));
    }

    [Fact]
    public async Task A_cancelled_scan_stops_with_operation_cancelled()
    {
        WriteMixedFolder();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.ThrowsAny<OperationCanceledException>(() => MapperFolderIndex.Scan(_root, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MapperFolderIndex.ScanAsync(_root, cts.Token));
    }

    [Fact]
    public void Parallelism_below_one_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MapperFolderIndex.Scan(_root, 0, default));
    }

    [Fact]
    public void A_file_locked_by_another_program_is_skipped_and_the_scan_finishes()
    {
        Write("Ok.xml", Mapper("t.Ok"));
        var locked = Write("Locked.xml", Mapper("t.Locked"));
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var index = MapperFolderIndex.Scan(_root, default);

            Assert.Contains(index.Files, f => f.Namespace == "t.Ok");
            if (OperatingSystem.IsWindows())
            {
                // 잠금은 Windows 만 강제한다(다른 OS 는 권고 잠금이라 읽힐 수 있다).
                Assert.Contains(Path.GetFullPath(locked), index.Skipped);
            }
        }
    }

    [Fact]
    public Task Statement_nodes_of_a_file_are_made_only_when_the_file_children_are_first_read() => Ui.Run(async () =>
    {
        Write(Path.Combine("a", "A.xml"), Mapper("t.A", 3));
        using var folder = new MapperFolderService(dataDirectory: null) { DebounceMilliseconds = 600_000 };
        Assert.True(await folder.OpenAsync(_root, default));
        using var tree = new MapperFolderTreeViewModel(folder);

        var file = Assert.Single(Assert.Single(tree.Roots).Children);
        Assert.False(file.HasChildren);

        Assert.Equal(["s0", "s1", "s2"], file.Children.Select(n => n.Name));
        Assert.True(file.HasChildren);
        Assert.Equal("t.A.s1", file.Children[1].ToolTipText);

        // 다시 훑으면 새 노드는 다시 만들지 않은 상태로 시작하고, 접은 파일의 펼침 기억도 그대로다.
        await tree.RefreshAsync();
        var again = Assert.Single(Assert.Single(tree.Roots).Children);
        Assert.False(again.HasChildren);
        Assert.False(again.IsExpanded);
    });

    [Fact]
    public Task Search_filters_lazily_made_statement_nodes_by_the_search_at_build_time() => Ui.Run(async () =>
    {
        Write(Path.Combine("a", "A.xml"), Mapper("t.A", 12));
        using var folder = new MapperFolderService(dataDirectory: null) { DebounceMilliseconds = 600_000 };
        Assert.True(await folder.OpenAsync(_root, default));
        using var tree = new MapperFolderTreeViewModel(folder) { FilterDebounceMilliseconds = 0 };

        tree.SearchText = "s1";
        var file = Assert.Single(Assert.Single(tree.Roots).Children);
        Assert.EndsWith("(3)", file.Detail, StringComparison.Ordinal); // s1·s10·s11

        tree.SearchText = string.Empty; // 검색을 바꿔도 이미 지은 노드의 자식은 지을 때의 검색 글을 따른다
        Assert.Equal(["s1", "s10", "s11"], file.Children.Select(n => n.Name));
        Assert.Equal(12, Assert.Single(Assert.Single(tree.Roots).Children).Children.Count);
    });

    [Fact]
    public Task The_tree_attaches_top_nodes_a_few_at_a_time_and_has_them_all_after_pumping() => Ui.Run(async () =>
    {
        for (var i = 0; i < 11; i++)
        {
            Write(Path.Combine("f" + i.ToString("D2", CultureInfo.InvariantCulture), "M.xml"), Mapper("t.F" + i.ToString(CultureInfo.InvariantCulture)));
        }

        using var folder = new MapperFolderService(dataDirectory: null) { DebounceMilliseconds = 600_000 };
        Assert.True(await folder.OpenAsync(_root, default));
        using var vm = new MapperFolderTreeViewModel(folder);
        var view = new MapperFolderTreeView { DataContext = vm };
        var window = new Window { Width = 320, Height = 400, Content = view };
        window.Show();
        var tree = view.FindControl<TreeView>("Tree")!;
        try
        {
            Assert.Equal(11, vm.Roots.Count);
            Assert.Empty(tree.Items); // 붙이기는 배경 우선순위로 미룬다(사건 처리 안에서 다 붙이지 않는다)

            view.SyncOnce();
            Assert.Equal(MapperFolderTreeView.ShowBatch, tree.ItemCount);
            Assert.False(view.IsShowingAll);

            Pump();
            Assert.True(view.IsShowingAll);
            Assert.Equal(vm.Roots, tree.Items.Cast<MapperFolderTreeNode>());

            // 다시 지으면(검색) 새 목록으로 바뀌고, 첫 쿼리를 키로 고를 때처럼 다 붙인 뒤 고르면 선택이 남는다.
            vm.SearchText = "s0";
            vm.FlushSearch();
            view.ShowAll();
            var first = vm.FirstElement()!;
            vm.SelectedNode = first;
            Pump();
            Assert.True(view.IsShowingAll);
            Assert.Same(first, vm.SelectedNode);

            // 폴더를 닫으면 트리가 빈다.
            folder.Close();
            Pump();
            Assert.Equal(0, tree.ItemCount);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Opening_a_mapper_starts_the_parser_warm_up_for_its_statements() => Ui.Run(async () =>
    {
        var path = Write("Warm.xml", Mapper("t.Warm", 3));
        var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler()) };
        var before = MapperParseWarmUp.Last;

        await host.OpenWorkspaceFileAsync(path, default);

        var warm = MapperParseWarmUp.Last;
        Assert.NotSame(before, warm);
        try
        {
            Assert.Equal(3, await warm.WaitAsync(TimeSpan.FromSeconds(60)));
        }
        catch (TaskCanceledException)
        {
            // 같은 때 다른 시험이 매퍼를 열면 앞 데우기는 취소된다(데우기는 앱에 하나).
        }
    });
}
