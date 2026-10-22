using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.ConnectionManager;

// 소유: TSK-13-12. 접속별 설명(comment): 편집기 칸·저장·목록 도움말·찾기·복제.

/// <summary>접속마다 여러 줄 설명을 달고, 목록 줄 도움말·보조 글·찾기에 쓰인다.</summary>
public sealed class ConnectionCommentTests
{
    private const string Note = "주문 조회 전용 계정.\n배치 시간(02~04시)에는 쓰지 않는다";

    private static ConnectionProfile Profile(string name, string? comment = null) =>
        new(name, "scott", "localhost:1521/FREEPDB1", Comment: comment);

    [Fact]
    public void Editor_loads_edits_and_builds_the_comment()
    {
        var vm = ProfileEditorViewModel.From(Profile("a", Note));
        Assert.Equal(Note, vm.Comment);
        Assert.False(vm.IsDirty);

        vm.Comment = "  " + Note + "\n ";
        Assert.True(vm.IsDirty);
        Assert.Equal(Note, vm.TryBuild(out var errors)!.Comment); // 앞뒤 공백만 뺀다(줄바꿈은 그대로)
        Assert.Empty(errors);

        vm.Comment = " \n ";
        Assert.Null(vm.TryBuild(out _)!.Comment); // 비우면 null(파일에서 키가 사라진다)

        var fresh = ProfileEditorViewModel.ForNew();
        Assert.Equal(string.Empty, fresh.Comment);
    }

    [Fact]
    public void Tree_row_shows_the_comment_as_tooltip_and_its_first_line_before_the_connect_string()
    {
        var node = new ConnectionTreeNode("a", Profile("a", Note));
        Assert.Equal(Note, node.Tip);
        Assert.Equal("주문 조회 전용 계정. · scott@localhost:1521/FREEPDB1", node.Subtitle);

        var plain = new ConnectionTreeNode("b", Profile("b"));
        Assert.Null(plain.Tip);
        Assert.Equal("scott@localhost:1521/FREEPDB1", plain.Subtitle);

        var folder = new ConnectionTreeNode("폴더", path: "폴더");
        Assert.Null(folder.Tip);
    }

    [Fact]
    public void Search_matches_comment_text()
    {
        Assert.True(ConnectionManagerViewModel.Matches(Profile("a", Note), "배치 시간"));
        Assert.False(ConnectionManagerViewModel.Matches(Profile("a"), "배치 시간"));
    }

    [Fact]
    public async Task Comment_is_saved_to_connections_json_and_duplicate_keeps_it()
    {
        using var fx = new ServiceFixture();
        var saved = await fx.Service.SaveProfileAsync(Profile("a", Note), "pw", true, CancellationToken.None);
        Assert.Contains("\"comment\"", await File.ReadAllTextAsync(fx.Paths.ConnectionsFile), StringComparison.Ordinal);
        var loaded = (await fx.Service.LoadAsync(CancellationToken.None)).Value.Profiles.Single();
        Assert.Equal(Note, loaded.Comment);

        var copy = await fx.Service.DuplicateProfileAsync(saved.Id, CancellationToken.None);
        Assert.Equal(Note, copy.Comment);

        await fx.Service.SaveProfileAsync(saved with { Comment = null }, null, true, CancellationToken.None);
        var json = await File.ReadAllTextAsync(fx.Paths.ConnectionsFile);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"comment\"")); // 사본에만 남는다
    }

    [Fact]
    public async Task Manager_window_has_a_multi_line_comment_box_and_row_tooltips() => await HeadlessHost.Run(async () =>
    {
        using var fx = new ServiceFixture();
        await fx.Service.SaveProfileAsync(Profile("설명 있음", Note), "pw", true, CancellationToken.None);
        var vm = new ConnectionManagerViewModel(fx.Service);
        await vm.LoadAsync();
        vm.Selected = vm.AllNodes().Single(n => n.Title == "설명 있음");
        var window = new ConnectionManagerWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var editor = window.FindControl<ProfileEditorView>("EditorView")!;
        var box = editor.FindControl<TextBox>("CommentBox")!;
        Assert.True(box.AcceptsReturn);
        Assert.Equal(TextWrapping.Wrap, box.TextWrapping);
        Assert.Equal(Note, box.Text);

        box.Text = "한 줄\n두 줄";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("한 줄\n두 줄", vm.Editor!.Comment);

        var tree = window.FindControl<TreeView>("ConnectionTree")!;
        var row = tree.GetVisualDescendants().OfType<Grid>().First(g => g.ColumnDefinitions.Count == 5 && g.DataContext is ConnectionTreeNode { Title: "설명 있음" });
        Assert.Equal(Note, ToolTip.GetTip(row));
        window.Close();
    });
}
