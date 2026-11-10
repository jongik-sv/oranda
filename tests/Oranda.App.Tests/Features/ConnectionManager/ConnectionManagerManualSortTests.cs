using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.ConnectionManager;

// CM1: 접속 관리자 정렬 콤보가 실제 목록 순서를 바꾼다 + 새 정렬 「직접 정렬」(같은 폴더 안 끌어 순서 바꾸기, 파일에 저장).

public sealed class ConnectionManagerManualSortTests
{
    private static readonly DateTimeOffset Old = DateTimeOffset.UtcNow.AddDays(-3);
    private static readonly DateTimeOffset Recent = DateTimeOffset.UtcNow.AddDays(-1);

    /// <summary>폴더 둘(F1·F2), 폴더 없는 접속 셋(a·b·c; 최근 접속은 c &gt; a, b 는 없음).</summary>
    private static async Task<ConnectionManagerViewModel> SeedAsync(ServiceFixture fx)
    {
        var s = fx.Service;
        var ct = CancellationToken.None;
        await s.SaveProfileAsync(new ConnectionProfile("a", "u", "h1:1521/A", LastConnectedAt: Old), null, false, ct);
        await s.SaveProfileAsync(new ConnectionProfile("b", "u", "h2:1521/B"), null, false, ct);
        await s.SaveProfileAsync(new ConnectionProfile("c", "u", "h3:1521/C", LastConnectedAt: Recent), null, false, ct);
        await s.UpdateFoldersAsync(d => ConnectionFolders.Create(ConnectionFolders.Create(d, "F1"), "F2"), ct);
        var vm = new ConnectionManagerViewModel(s);
        await vm.LoadAsync();
        return vm;
    }

    private static List<string> TopTitles(ConnectionManagerViewModel vm) => [.. vm.Tree.Select(n => n.IsFolder ? "[" + n.Title + "]" : n.Title)];

    private static ConnectionTreeNode Node(ConnectionManagerViewModel vm, string title) => vm.AllNodes().Single(n => n.Title == title);

    // ---------- (1) 정렬 콤보 ----------

    [Fact]
    public async Task Picking_a_sort_in_the_combo_reorders_the_tree()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);

        await HeadlessHost.Run(() =>
        {
            var window = new ConnectionManagerWindow { DataContext = vm, Width = 900, Height = 640 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var combo = window.FindControl<ComboBox>("SortBox")!;
            Assert.Equal(3, combo.ItemCount); // 이름순 · 최근 접속순 · 직접 정렬
            Assert.Equal(["[F1]", "[F2]", "a", "b", "c"], TopTitles(vm));

            combo.SelectedIndex = 1; // 최근 접속순
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ConnectionSortOrder.RecentlyConnected, vm.SortOrder);
            Assert.Equal(["[F1]", "[F2]", "c", "a", "b"], TopTitles(vm));

            combo.SelectedIndex = 0; // 이름순으로 되돌림
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["[F1]", "[F2]", "a", "b", "c"], TopTitles(vm));
            window.Close();
            return Task.CompletedTask;
        });
    }

    // ---------- (2) 직접 정렬 ----------

    [Fact]
    public async Task Manual_sort_starts_in_name_order_for_an_old_file()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);

        vm.SortOrder = ConnectionSortOrder.Manual;

        Assert.Equal(["[F1]", "[F2]", "a", "b", "c"], TopTitles(vm)); // 옛 파일(순서 값 없음) = 이름순으로 시작
        Assert.All((await fx.Service.LoadAsync(CancellationToken.None)).Value.Profiles, p => Assert.Null(p.SortIndex));
    }

    [Fact]
    public async Task Dragging_a_profile_between_rows_saves_the_order_and_a_reopened_manager_keeps_it()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        vm.SortOrder = ConnectionSortOrder.Manual;

        Assert.True(await vm.ReorderAsync(Node(vm, "c"), Node(vm, "a"), after: false)); // c 를 a 앞으로
        Assert.Equal(["[F1]", "[F2]", "c", "a", "b"], TopTitles(vm));

        var reopened = new ConnectionManagerViewModel(fx.Service) { SortOrder = ConnectionSortOrder.Manual };
        await reopened.LoadAsync();
        Assert.Equal(["[F1]", "[F2]", "c", "a", "b"], TopTitles(reopened));

        Assert.True(await reopened.ReorderAsync(Node(reopened, "c"), Node(reopened, "b"), after: true)); // c 를 b 뒤로
        Assert.Equal(["[F1]", "[F2]", "a", "b", "c"], TopTitles(reopened));
    }

    [Fact]
    public async Task Dragging_a_folder_between_folders_saves_the_folder_order()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        vm.SortOrder = ConnectionSortOrder.Manual;

        Assert.True(await vm.ReorderAsync(Node(vm, "F2"), Node(vm, "F1"), after: false));
        Assert.Equal(["[F2]", "[F1]", "a", "b", "c"], TopTitles(vm));

        var reopened = new ConnectionManagerViewModel(fx.Service) { SortOrder = ConnectionSortOrder.Manual };
        await reopened.LoadAsync();
        Assert.Equal(["[F2]", "[F1]", "a", "b", "c"], TopTitles(reopened));

        reopened.SortOrder = ConnectionSortOrder.Name; // 다른 정렬에서는 저장된 순서를 쓰지 않는다
        Assert.Equal(["[F1]", "[F2]", "a", "b", "c"], TopTitles(reopened));
    }

    [Fact]
    public async Task Reorder_is_refused_outside_manual_sort_across_kinds_or_across_folders()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);

        Assert.False(await vm.ReorderAsync(Node(vm, "c"), Node(vm, "a"), after: false)); // 이름순에서는 안 바뀐다
        vm.SortOrder = ConnectionSortOrder.Manual;
        Assert.False(await vm.ReorderAsync(Node(vm, "c"), Node(vm, "F1"), after: false)); // 접속과 폴더
        Assert.Equal(["[F1]", "[F2]", "a", "b", "c"], TopTitles(vm));
    }

    [Fact]
    public async Task Resolve_drop_reorders_between_rows_and_moves_into_a_folder_on_a_folder_row()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        var c = Node(vm, "c");
        var a = Node(vm, "a");
        var f1 = Node(vm, "F1");

        Assert.Equal(DropPlacement.None, vm.ResolveDrop(c, a, 0.1).Placement); // 이름순: 같은 폴더 안 접속 위는 놓을 수 없다
        Assert.Equal(new DropIntent(DropPlacement.IntoFolder, "F1"), vm.ResolveDrop(c, f1, 0.1)); // 폴더 위는 지금처럼 폴더로 옮기기

        vm.SortOrder = ConnectionSortOrder.Manual;
        Assert.Equal(DropPlacement.Before, vm.ResolveDrop(c, a, 0.2).Placement);
        Assert.Equal(DropPlacement.After, vm.ResolveDrop(c, a, 0.8).Placement);
        Assert.Equal(new DropIntent(DropPlacement.IntoFolder, "F1"), vm.ResolveDrop(c, f1, 0.1)); // 접속을 폴더 줄에 놓으면 줄 사이가 아니라 폴더로
        Assert.Equal(DropPlacement.After, vm.ResolveDrop(Node(vm, "F2"), f1, 0.9).Placement);
        Assert.Equal(new DropIntent(DropPlacement.IntoFolder, "F1"), vm.ResolveDrop(Node(vm, "F2"), f1, 0.5)); // 폴더 줄 가운데 = 폴더 안으로

        vm.SearchText = "a"; // 찾는 중에는 순서를 바꾸지 않는다
        Assert.NotEqual(DropPlacement.Before, vm.ResolveDrop(c, a, 0.2).Placement);
    }

    [Fact]
    public async Task Moving_a_profile_to_another_folder_puts_it_last_there_and_renaming_a_folder_keeps_its_place()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        vm.SortOrder = ConnectionSortOrder.Manual;
        Assert.True(await vm.ReorderAsync(Node(vm, "F2"), Node(vm, "F1"), after: false));
        Assert.True(await vm.ReorderAsync(Node(vm, "c"), Node(vm, "a"), after: false));

        await vm.MoveNodeAsync(Node(vm, "a"), "F1");
        await vm.MoveNodeAsync(Node(vm, "b"), "F1");
        Assert.Equal(["a", "b"], Node(vm, "F1").Children.Select(n => n.Title)); // 새 폴더에서는 뒤(이름순)

        var doc = (await fx.Service.LoadAsync(CancellationToken.None)).Value;
        var renamed = ConnectionFolders.Move(doc, "F2", "G2");
        Assert.Equal(["G2", "F1"], ConnectionFolders.OrderManually(["F1", "G2"], renamed.FolderOrder));
    }
}
