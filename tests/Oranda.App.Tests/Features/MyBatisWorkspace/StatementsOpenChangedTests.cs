using Avalonia.Controls;
using Avalonia.VisualTree;
using Oranda.App.Features.MyBatisWorkspace.Folder;
using Oranda.App.Features.MyBatisWorkspace.Statements;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Tests.Features.MyBatisWorkspace.Flow;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// 「매퍼 쿼리」 목록에서 편집기 탭으로 열린 쿼리·지금 보는 탭의 쿼리·저장 안 한 고침·xml 에 저장해 연 때와 달라진 쿼리를 한눈에 구분한다.
/// 앱 조립(ShellHarness) 위에서 매퍼를 열고, 탭 열기·고치기·저장·닫기·되돌리기마다 목록 표시가 사건으로 바로 바뀌는지 본다.
/// </summary>
public sealed class StatementsOpenChangedTests
{
    private const string Count = "countEmp";
    private const string Delete = "deleteEmp";
    private const string List = "selectEmpList";

    private static void Edit(SqlEditorViewModel tab, string from, string to)
    {
        var at = tab.Document.Text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(at >= 0, from);
        tab.Document.Replace(at, from.Length, to);
    }

    private static MyBatisStatementNode NodeOf(MyBatisStatementsViewModel vm, string id) =>
        vm.Groups.SelectMany(g => g.Children).Single(n => n.StatementId == id);

    private static (MyBatisStatementsViewModel Vm, MyBatisStatementsView View, Window Window) ShowList(MyBatisFlowRig rig)
    {
        var vm = new MyBatisStatementsViewModel { Host = rig.Host, Text = new ShellText(), FilterDebounceMilliseconds = 0 };
        var view = new MyBatisStatementsView { DataContext = vm };
        var window = new Window { Width = 340, Height = 560, Content = view };
        window.Show();
        MyBatisFlowRig.Pump();
        window.UpdateLayout();
        return (vm, view, window);
    }

    /// <summary>매퍼를 열 때 저절로 열린 첫 쿼리 탭을 닫는다.</summary>
    private static void CloseFirst(MyBatisFlowRig rig, SqlWorkspaceViewModel ws, Window window)
    {
        rig.Host.Close(ws.ActiveTab!);
        Settle(window);
    }

    private static void Settle(Window window)
    {
        MyBatisFlowRig.Pump();
        window.UpdateLayout();
    }

    [Fact]
    public Task Open_current_unsaved_and_saved_marks_follow_tabs_edits_saves_and_closes() => Ui.Run(async () =>
    {
        using var rig = MyBatisFlowRig.Create();
        var path = rig.Write("EmpMapper.xml", MapperSamples.EmpMapper);
        var (ws, session) = await rig.OpenAsync(path);
        var (vm, view, window) = ShowList(rig);
        try
        {
            // 열면 첫 쿼리 탭이 저절로 열려 지금 탭이다. 닫으면 아무것도 안 열렸고 바뀐 것도 없다(기본 모양)
            Assert.Equal((1, 0), (vm.OpenCount, vm.ChangedCount));
            Assert.True(NodeOf(vm, List).IsCurrent);
            CloseFirst(rig, ws, window);
            Assert.Equal((0, 0), (vm.OpenCount, vm.ChangedCount));
            Assert.All(vm.Groups.SelectMany(g => g.Children), n => Assert.False(n.IsOpen || n.IsCurrent));
            Assert.Equal(1, NodeOf(vm, List).RowOpacity); // 열리지 않음 = 흐리지 않다(흐림은 편집 불가 뜻)

            // 탭 둘을 열고 하나를 고친다 → 열림 2 · 변경 1(저장 안 함), 지금 보는 탭 = 마지막에 연 것
            var count = session.OpenStatement(Count)!;
            var delete = session.OpenStatement(Delete)!;
            Edit(count, "COUNT(*)", "COUNT(EMPNO)");
            Settle(window);
            Assert.Equal((2, 1, 1, 0), (vm.OpenCount, vm.ChangedCount, vm.UnsavedCount, vm.SavedCount));
            Assert.True(NodeOf(vm, Count).IsOpen);
            Assert.False(NodeOf(vm, Count).IsCurrent);
            Assert.True(NodeOf(vm, Delete).IsCurrent);
            Assert.NotEmpty(NodeOf(vm, Count).ModifiedText); // ● = 탭 머리 * 와 같은 뜻
            Assert.Empty(NodeOf(vm, Count).SavedText);
            Assert.Empty(NodeOf(vm, Delete).ModifiedText);
            Assert.False(NodeOf(vm, List).IsOpen);
            Assert.Contains("저장 안 한", NodeOf(vm, Count).StatusToolTip, StringComparison.Ordinal);
            Assert.Contains("탭", NodeOf(vm, Delete).StatusToolTip, StringComparison.Ordinal);

            // 화면: 열린 줄의 id 는 굵게, 지금 탭의 줄은 따로 막대로 강조(목록 선택과 따로)
            var idBlocks = view.GetVisualDescendants().OfType<HighlightTextBlock>().ToList();
            Assert.Contains("open", idBlocks.Single(b => b.Source == Count).Classes);
            Assert.DoesNotContain("open", idBlocks.Single(b => b.Source == List).Classes);
            var bars = view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("open-bar")).ToList();
            Assert.Contains(bars, b => b.Classes.Contains("current") && (b.DataContext as MyBatisStatementNode)?.StatementId == Delete);
            Assert.DoesNotContain(bars, b => b.Classes.Contains("current") && (b.DataContext as MyBatisStatementNode)?.StatementId == Count);
            MyBatisFlowRig.Render(window, "mbst-open-unsaved", 340, 560);

            // 거르기: 열린 것만 → 둘, 변경된 것만 → 하나
            vm.ShowOpenOnly = true;
            Assert.Equal([Count, Delete], vm.Groups.SelectMany(g => g.Children).Select(n => n.StatementId!).Order(StringComparer.Ordinal));
            vm.ShowOpenOnly = false;
            vm.ShowChangedOnly = true;
            Assert.Equal([Count], vm.Groups.SelectMany(g => g.Children).Select(n => n.StatementId));
            Settle(window);
            MyBatisFlowRig.Render(window, "mbst-changed-only", 340, 560);
            vm.ShowChangedOnly = false;

            // xml 에 저장 → ● 가 ○(저장함, 연 때와 다름)로 바뀐다
            Assert.True(await rig.Save.SaveAsync(ws, default), string.Join(" | ", rig.Dialogs.Messages)); // 실패하면 저장 처리기가 알린 이유를 보인다
            Settle(window);
            Assert.Empty(NodeOf(vm, Count).ModifiedText);
            Assert.NotEmpty(NodeOf(vm, Count).SavedText);
            Assert.Equal((2, 1, 0, 1), (vm.OpenCount, vm.ChangedCount, vm.UnsavedCount, vm.SavedCount));
            Assert.Contains("xml", NodeOf(vm, Count).StatusToolTip, StringComparison.Ordinal);

            // 다른 탭으로 가면 지금 탭 표시가 옮겨 간다
            ws.ActiveTab = count;
            Settle(window);
            Assert.True(NodeOf(vm, Count).IsCurrent);
            Assert.False(NodeOf(vm, Delete).IsCurrent);
            MyBatisFlowRig.Render(window, "mbst-saved-current", 340, 560);

            // 뒤에 있는 탭 닫기(활성 탭은 그대로) → 그 쿼리만 열림 해제
            rig.Host.Close(delete);
            Settle(window);
            Assert.Same(count, ws.ActiveTab);
            Assert.False(NodeOf(vm, Delete).IsOpen);
            Assert.Equal((1, 1), (vm.OpenCount, vm.ChangedCount));

            // 지금 탭 닫기 → 열림 해제(저장한 변경 표시는 남는다)
            rig.Host.Close(count);
            Settle(window);
            Assert.False(NodeOf(vm, Count).IsOpen);
            Assert.False(NodeOf(vm, Count).IsCurrent);
            Assert.NotEmpty(NodeOf(vm, Count).SavedText);
            Assert.Equal((0, 1), (vm.OpenCount, vm.ChangedCount));

            // 되돌려 저장하면 연 때와 같아져 표시가 사라진다
            var again = session.OpenStatement(Count)!;
            Edit(again, "COUNT(EMPNO)", "COUNT(*)");
            Settle(window);
            Assert.NotEmpty(NodeOf(vm, Count).ModifiedText);
            Assert.True(await rig.Save.SaveAsync(ws, default), string.Join(" | ", rig.Dialogs.Messages)); // 실패하면 저장 처리기가 알린 이유를 보인다
            Settle(window);
            Assert.Empty(NodeOf(vm, Count).ModifiedText);
            Assert.Empty(NodeOf(vm, Count).SavedText);
            Assert.Equal(0, vm.ChangedCount);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Undoing_an_edit_in_the_tab_clears_the_unsaved_mark_at_once() => Ui.Run(async () =>
    {
        using var rig = MyBatisFlowRig.Create();
        var path = rig.Write("EmpMapper.xml", MapperSamples.EmpMapper);
        var (ws, session) = await rig.OpenAsync(path);
        var (vm, _, window) = ShowList(rig);
        try
        {
            CloseFirst(rig, ws, window);
            var tab = session.OpenStatement(Count)!;
            Edit(tab, "COUNT(*)", "COUNT(1)");
            Settle(window);
            Assert.Equal(1, vm.UnsavedCount);
            tab.Document.UndoStack.Undo();
            Settle(window);
            Assert.Equal((1, 0), (vm.OpenCount, vm.ChangedCount));
            Assert.Empty(NodeOf(vm, Count).ModifiedText);
            Assert.Empty(NodeOf(vm, Count).SavedText);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task A_new_query_saved_to_xml_shows_as_saved_change_and_the_counts_text_is_localized() => Ui.Run(async () =>
    {
        using var rig = MyBatisFlowRig.Create();
        var path = rig.Write("EmpMapper.xml", MapperSamples.EmpMapper);
        var (ws, session) = await rig.OpenAsync(path);
        var (vm, _, window) = ShowList(rig);
        try
        {
            CloseFirst(rig, ws, window);
            session.CreateStatement(MyBatisStatementKind.Select, "selectNew");
            Settle(window);
            Assert.NotEmpty(NodeOf(vm, "selectNew").NewText);
            Assert.True(NodeOf(vm, "selectNew").IsCurrent);
            Assert.Equal("열림 1", vm.OpenFilterText);
            Assert.Equal("변경 1", vm.ChangedFilterText);

            Assert.True(await rig.Save.SaveAsync(ws, default), string.Join(" | ", rig.Dialogs.Messages)); // 실패하면 저장 처리기가 알린 이유를 보인다
            Settle(window);
            var node = NodeOf(vm, "selectNew");
            Assert.Empty(node.NewText);
            Assert.NotEmpty(node.SavedText); // 연 때는 없던 쿼리 = 연 때와 다름
            Assert.True(session.Find("selectNew")!.IsSavedChange);
            Assert.False(session.Find(List)!.IsSavedChange);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task The_mapper_folder_tree_marks_open_current_unsaved_and_saved_statements_too() => Ui.Run(async () =>
    {
        using var rig = MyBatisFlowRig.Create();
        var path = rig.Write("EmpMapper.xml", MapperSamples.EmpMapper, "hr");
        using var folder = new MapperFolderService(dataDirectory: null);
        await folder.OpenAsync(rig.Root, default);
        using var tree = new MapperFolderTreeViewModel(folder) { Host = rig.Host, Text = new ShellText() };
        // 폴더 훑기·파일 감시가 트리를 다시 지을 수 있어 노드는 볼 때마다 지금 트리에서 찾는다(다시 지은 노드도 표시를 받아야 한다).
        MapperFolderTreeNode File() => tree.Roots.Single(n => n.Name == "hr").Children.Single();
        MapperFolderTreeNode Node(string id) => File().Children.Single(n => n.ElementId == id);
        Assert.All(File().Children, n => Assert.False(n.IsOpen || n.IsUnsaved || n.IsSaved)); // 아직 매퍼를 열지 않았다

        var (ws, session) = await rig.OpenAsync(path);
        MyBatisFlowRig.Pump();
        Assert.True(Node(List).IsCurrent); // 열면 첫 쿼리 탭이 저절로 열린다
        var count = session.OpenStatement(Count)!;
        Edit(count, "COUNT(*)", "COUNT(EMPNO)");
        MyBatisFlowRig.Pump();
        Assert.Equal((true, true, true), (Node(Count).IsOpen, Node(Count).IsCurrent, Node(Count).IsUnsaved));
        Assert.True(Node(List).IsOpen);
        Assert.False(Node(List).IsCurrent || Node(List).IsUnsaved);
        Assert.False(Node(Delete).IsOpen);

        tree.Roots.Single(n => n.Name == "hr").IsExpanded = true;
        File().IsExpanded = true;
        var view = new MapperFolderTreeView { DataContext = tree };
        var window = new Window { Width = 340, Height = 420, Content = view };
        window.Show();
        MyBatisFlowRig.Pump();
        window.UpdateLayout();
        try
        {
            MyBatisFlowRig.Render(window, "mbst-folder-unsaved", 340, 420);

            Assert.True(await rig.Save.SaveAsync(ws, default), string.Join(" | ", rig.Dialogs.Messages)); // 실패하면 저장 처리기가 알린 이유를 보인다
            MyBatisFlowRig.Pump();
            Assert.False(Node(Count).IsUnsaved);
            Assert.True(Node(Count).IsSaved);

            rig.Host.Close(count);
            MyBatisFlowRig.Pump();
            Assert.False(Node(Count).IsOpen);
            Assert.True(Node(Count).IsSaved);
            MyBatisFlowRig.Render(window, "mbst-folder-saved", 340, 420);

            await rig.Host.CloseWorkspaceAsync(ws, default); // 작업 공간을 닫으면 그 매퍼의 표시가 모두 꺼진다
            MyBatisFlowRig.Pump();
            Assert.All(File().Children, n => Assert.False(n.IsOpen || n.IsCurrent || n.IsUnsaved || n.IsSaved));
        }
        finally
        {
            window.Close();
        }
    });
}
