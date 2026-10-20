using Oranda.App.Features.ConnectionManager;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.ConnectionManager;

// 접속 관리자 폴더: 새 폴더(이름 겹침·하위), 이름 편집, 옮기기(끌어 놓기·폴더로 이동), 삭제(접속은 부모로 올림).
public sealed class ConnectionManagerFolderTests
{
    private static async Task<ConnectionManagerViewModel> SeedAsync(ServiceFixture fx)
    {
        var s = fx.Service;
        var ct = CancellationToken.None;
        await s.SaveProfileAsync(new ConnectionProfile("ft-dev", "ft", "localhost:1522/FREEPDB1"), "pw", true, ct);
        await s.SaveProfileAsync(new ConnectionProfile("sales", "admin", "SALES_HIGH", TnsAdmin: "/w", Group: "ADB"), null, false, ct);
        var vm = new ConnectionManagerViewModel(s);
        await vm.LoadAsync();
        return vm;
    }

    private static ConnectionTreeNode Node(ConnectionManagerViewModel vm, string title) => vm.AllNodes().Single(n => n.Title == title);

    private static async Task<ConnectionsDocument> StoredAsync(ServiceFixture fx) => (await fx.Service.LoadAsync(CancellationToken.None)).Value;

    private static string? GroupOf(ConnectionsDocument doc, string name) => doc.Profiles.Single(p => p.Name == name).Group;

    [Fact]
    public async Task New_folder_is_saved_empty_named_without_collisions_and_starts_in_edit_mode()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        ConnectionTreeNode? requested = null;
        vm.FolderEditRequested += (_, n) => requested = n;

        await vm.NewFolderCommand.ExecuteAsync(null);

        var first = Node(vm, "새 폴더");
        Assert.True(first.IsEditing);
        Assert.Same(first, requested);
        Assert.Same(first, vm.Selected);
        Assert.Equal("(0)", first.Subtitle);
        Assert.Contains("새 폴더", (await StoredAsync(fx)).Folders);

        ConnectionManagerViewModel.CancelFolderEdit(first);
        vm.Selected = null;
        await vm.NewFolderCommand.ExecuteAsync(null);
        Assert.Contains(vm.AllNodes(), n => n.Title == "새 폴더 (2)");
    }

    [Fact]
    public async Task New_folder_goes_inside_the_selected_folder_or_the_folder_of_the_selected_connection()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);

        vm.Selected = Node(vm, "ADB");
        await vm.NewFolderCommand.ExecuteAsync(null);
        Assert.Contains("ADB/새 폴더", (await StoredAsync(fx)).Folders);
        var sub = vm.AllNodes().Single(n => n.IsFolder && n.Path == "ADB/새 폴더");
        Assert.Equal("새 폴더", sub.Title);
        Assert.Contains(sub, Node(vm, "ADB").Children);

        vm.Selected = Node(vm, "sales");
        await vm.NewFolderCommand.ExecuteAsync(null);
        Assert.Contains("ADB/새 폴더 (2)", (await StoredAsync(fx)).Folders);

        vm.Selected = Node(vm, "ft-dev");
        await vm.NewFolderCommand.ExecuteAsync(null);
        Assert.Contains("새 폴더", (await StoredAsync(fx)).Folders);
    }

    [Fact]
    public async Task Renaming_a_folder_rewrites_connection_groups_and_subfolder_paths()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        vm.Selected = Node(vm, "ADB");
        await vm.NewFolderCommand.ExecuteAsync(null);
        ConnectionManagerViewModel.CancelFolderEdit(Node(vm, "새 폴더"));

        var adb = Node(vm, "ADB");
        vm.Selected = adb;
        vm.RenameFolderCommand.Execute(null);
        Assert.True(adb.IsEditing);
        adb.EditText = "클라우드";
        await vm.CommitFolderEditAsync(adb);

        var doc = await StoredAsync(fx);
        Assert.Equal("클라우드", GroupOf(doc, "sales"));
        Assert.Contains("클라우드/새 폴더", doc.Folders);
        Assert.DoesNotContain(doc.Folders, f => f.StartsWith("ADB", StringComparison.Ordinal));
        Assert.Contains(vm.AllNodes(), n => n.Path == "클라우드/새 폴더");
    }

    [Fact]
    public async Task Rename_to_an_existing_folder_or_with_a_slash_is_refused_and_keeps_data()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        await vm.NewFolderCommand.ExecuteAsync(null);
        var created = Node(vm, "새 폴더");

        created.EditText = "ADB";
        await vm.CommitFolderEditAsync(created);
        Assert.True(vm.StatusIsError);

        created.IsEditing = true;
        created.EditText = "a/b";
        await vm.CommitFolderEditAsync(created);
        Assert.True(vm.StatusIsError);
        Assert.Contains("새 폴더", (await StoredAsync(fx)).Folders);
    }

    [Fact]
    public async Task Dropping_a_connection_on_a_folder_or_the_top_changes_only_its_group()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        vm.Selected = Node(vm, "ft-dev");
        var editorBefore = vm.Editor;
        Assert.True(ConnectionManagerViewModel.CanDrop(Node(vm, "ft-dev"), "ADB"));
        Assert.False(ConnectionManagerViewModel.CanDrop(Node(vm, "ft-dev"), null));

        Assert.True(await vm.MoveNodeAsync(Node(vm, "ft-dev"), "ADB"));

        var doc = await StoredAsync(fx);
        Assert.Equal("ADB", GroupOf(doc, "ft-dev"));
        Assert.Equal("ft-dev", vm.Selected!.Title);
        Assert.Equal("ADB", vm.Editor!.Group);
        Assert.Equal("oranda:conn:" + doc.Profiles.Single(p => p.Name == "ft-dev").Id.ToString("D"), doc.Profiles.Single(p => p.Name == "ft-dev").CredentialRef);
        Assert.NotNull(editorBefore);
        Assert.Equal("(2)", Node(vm, "ADB").Subtitle);

        Assert.True(await vm.MoveNodeAsync(Node(vm, "ft-dev"), null));
        Assert.Null(GroupOf(await StoredAsync(fx), "ft-dev"));
    }

    [Fact]
    public async Task Dropping_a_folder_on_another_folder_nests_it_but_never_into_itself()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        await vm.NewFolderCommand.ExecuteAsync(null);
        var created = Node(vm, "새 폴더");
        ConnectionManagerViewModel.CancelFolderEdit(created);

        Assert.False(ConnectionManagerViewModel.CanDrop(Node(vm, "ADB"), "ADB"));
        Assert.False(ConnectionManagerViewModel.CanDrop(Node(vm, "ADB"), "ADB/sub"));
        Assert.True(await vm.MoveNodeAsync(created, "ADB"));
        Assert.False(ConnectionManagerViewModel.CanDrop(Node(vm, "ADB"), "ADB/새 폴더"));

        var doc = await StoredAsync(fx);
        Assert.Contains("ADB/새 폴더", doc.Folders);
        Assert.False(await vm.MoveNodeAsync(Node(vm, "ADB"), "ADB/새 폴더"));
        Assert.Equal("ADB", GroupOf(await StoredAsync(fx), "sales"));
    }

    [Fact]
    public async Task Move_to_folder_uses_the_picker_and_none_means_the_top()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        IReadOnlyList<string>? offered = null;
        FolderChoice? answer = new("ADB");
        vm.AskFolder = (folders, _) =>
        {
            offered = folders;
            return Task.FromResult<FolderChoice?>(answer);
        };

        vm.Selected = Node(vm, "ft-dev");
        await vm.MoveToFolderCommand.ExecuteAsync(null);
        Assert.Equal(["ADB"], offered!.ToArray());
        Assert.Equal("ADB", GroupOf(await StoredAsync(fx), "ft-dev"));

        answer = new FolderChoice(null);
        await vm.MoveToFolderCommand.ExecuteAsync(null);
        Assert.Null(GroupOf(await StoredAsync(fx), "ft-dev"));

        answer = null;
        vm.Selected = Node(vm, "sales");
        await vm.MoveToFolderCommand.ExecuteAsync(null);
        Assert.Equal("ADB", GroupOf(await StoredAsync(fx), "sales"));
    }

    [Fact]
    public async Task Deleting_an_empty_folder_needs_no_confirmation()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        var asked = false;
        vm.Confirm = _ =>
        {
            asked = true;
            return Task.FromResult(true);
        };
        await vm.NewFolderCommand.ExecuteAsync(null);
        ConnectionManagerViewModel.CancelFolderEdit(Node(vm, "새 폴더"));

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.False(asked);
        Assert.DoesNotContain("새 폴더", (await StoredAsync(fx)).Folders);
        Assert.DoesNotContain(vm.AllNodes(), n => n.Title == "새 폴더");
    }

    [Fact]
    public async Task Deleting_a_non_empty_folder_asks_then_lifts_connections_and_subfolders_to_the_parent()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        await fx.Service.UpdateFoldersAsync(d => ConnectionFolders.MoveProfile(ConnectionFolders.Create(d, "ADB/내부"), d.Profiles.Single(p => p.Name == "sales").Id, "ADB/내부"), CancellationToken.None);
        await vm.LoadAsync();
        string? message = null;
        var answer = false;
        vm.Confirm = m =>
        {
            message = m;
            return Task.FromResult(answer);
        };

        vm.Selected = Node(vm, "ADB");
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.NotNull(message);
        Assert.Equal("ADB/내부", GroupOf(await StoredAsync(fx), "sales"));

        answer = true;
        await vm.DeleteCommand.ExecuteAsync(null);
        var doc = await StoredAsync(fx);
        Assert.Equal("내부", GroupOf(doc, "sales"));
        Assert.Equal(2, doc.Profiles.Count);
        Assert.DoesNotContain("ADB", ConnectionFolders.All(doc));
    }

    [Fact]
    public async Task Folders_sort_by_name_above_connections_and_count_nested_connections()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        await fx.Service.UpdateFoldersAsync(d => ConnectionFolders.Create(ConnectionFolders.Create(d, "가나다"), "ADB/하위"), CancellationToken.None);
        await vm.LoadAsync();

        Assert.Equal(["ADB", "가나다", "ft-dev"], vm.Tree.Select(n => n.Title).ToArray());
        Assert.Equal("(1)", Node(vm, "ADB").Subtitle);
        Assert.Equal(["하위", "sales"], Node(vm, "ADB").Children.Select(n => n.Title).ToArray());
    }

    [Fact]
    public async Task Editor_group_box_suggests_existing_folders()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        await vm.NewFolderCommand.ExecuteAsync(null);
        vm.Selected = Node(vm, "ft-dev");
        await vm.PendingSelection;

        Assert.Equal(["ADB", "새 폴더"], vm.Editor!.KnownGroups.OrderBy(f => f, StringComparer.Ordinal).ToArray());
    }
}
