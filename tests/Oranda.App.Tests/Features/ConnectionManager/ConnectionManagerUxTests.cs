using System.IO.Compression;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.ConnectionManager;

// 소유: TSK-03-04. 접속 관리자 사용성 개선(찾기·묶기·정렬, 행 동작·단축키, 접속 시험, 연결 문자열, 편집 상태, 배치) 시험.
// 서비스·가짜 저장소는 ConnectionManagerTests.cs 의 ServiceFixture 를 같이 쓴다.

public sealed class ConnectionManagerUxTests
{
    private static readonly DateTimeOffset Yesterday = DateTimeOffset.UtcNow.AddDays(-1);

    /// <summary>찾기·정렬 시험용 접속 넷: 그룹 둘(고객사·ADB), 그룹 없음 둘.</summary>
    private static async Task<ConnectionManagerViewModel> SeedAsync(ServiceFixture fx)
    {
        var s = fx.Service;
        var ct = CancellationToken.None;
        await s.SaveProfileAsync(new ConnectionProfile("ft-dev", "ft", "localhost:1522/FREEPDB1", LastConnectedAt: Yesterday), "pw", true, ct);
        await s.SaveProfileAsync(new ConnectionProfile("zeta", "hr", "db1.example.com:1521/HRPDB", LastConnectedAt: DateTimeOffset.UtcNow), null, false, ct);
        await s.SaveProfileAsync(new ConnectionProfile("운영DB", "scott", "prod.example.com:1521/ORCL", Environment: ConnectionEnvironment.Production, Group: "고객사"), null, false, ct);
        await s.SaveProfileAsync(new ConnectionProfile("sales", "admin", "SALES_HIGH", TnsAdmin: "/w", Group: "ADB"), null, false, ct);
        var vm = new ConnectionManagerViewModel(s);
        await vm.LoadAsync();
        return vm;
    }

    private static List<string> Titles(ConnectionManagerViewModel vm) =>
        [.. vm.AllNodes().Select(n => n.IsFolder ? "[" + n.Title + "]" : n.Title)];

    // ---------- 1. 찾기·묶기·정렬·마지막 접속 ----------

    [Fact]
    public async Task Groups_become_folders_on_top_and_ungrouped_profiles_stay_at_the_bottom()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);

        Assert.Equal(["[ADB]", "sales", "[고객사]", "운영DB", "ft-dev", "zeta"], Titles(vm));
        Assert.Equal("(1)", vm.Tree[0].Subtitle);
        Assert.All(vm.Tree.Where(n => n.IsFolder), f => Assert.True(f.IsExpanded));
    }

    [Theory]
    [InlineData("freepdb", "ft-dev")]
    [InlineData("SALES_high", "sales")]
    [InlineData("고객", "운영DB")]
    [InlineData("SCOTT", "운영DB")]
    [InlineData("db1.EXAMPLE", "zeta")]
    [InlineData("1522", "ft-dev")]
    public async Task Search_filters_by_name_user_host_service_alias_and_group_ignoring_case(string search, string expected)
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);

        vm.SearchText = search;

        var hit = Assert.Single(vm.AllNodes(), n => !n.IsFolder);
        Assert.Equal(expected, hit.Title);

        vm.SearchText = string.Empty;
        Assert.Equal(4, vm.AllNodes().Count(n => !n.IsFolder));
    }

    [Fact]
    public async Task Collapsed_folder_stays_collapsed_across_rebuilds_but_opens_while_searching()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);

        vm.Tree.Single(n => n.Title == "고객사").IsExpanded = false;
        vm.SortOrder = ConnectionSortOrder.RecentlyConnected;
        Assert.False(vm.Tree.Single(n => n.Title == "고객사").IsExpanded);

        vm.SearchText = "scott";
        Assert.True(vm.Tree.Single(n => n.Title == "고객사").IsExpanded);

        vm.SearchText = string.Empty;
        Assert.False(vm.Tree.Single(n => n.Title == "고객사").IsExpanded);
        Assert.True(vm.Tree.Single(n => n.Title == "ADB").IsExpanded);
    }

    [Fact]
    public async Task Sort_by_recent_connection_puts_latest_first_and_never_connected_last()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        Assert.Equal(["ft-dev", "zeta"], vm.Tree.Where(n => !n.IsFolder).Select(n => n.Title));

        vm.SortOrder = ConnectionSortOrder.RecentlyConnected;

        Assert.Equal(["zeta", "ft-dev"], vm.Tree.Where(n => !n.IsFolder).Select(n => n.Title));
        var zeta = vm.Tree.Single(n => n.Title == "zeta");
        Assert.Matches(@"^\d\d:\d\d$", zeta.LastConnectedText);
        Assert.NotNull(zeta.LastConnectedTip);
        Assert.Empty(vm.AllNodes().Single(n => n.Title == "sales").LastConnectedText);
    }

    [Fact]
    public void Last_connected_text_is_short_for_today_this_year_and_older()
    {
        var now = new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.FromHours(9));
        Assert.Equal("09:05", ConnectionTreeNode.FormatWhen(new DateTimeOffset(2026, 10, 20, 0, 5, 0, TimeSpan.Zero), now));
        Assert.Equal("03-02", ConnectionTreeNode.FormatWhen(new DateTimeOffset(2026, 3, 2, 3, 0, 0, TimeSpan.FromHours(9)), now));
        Assert.Equal("2025-12-31", ConnectionTreeNode.FormatWhen(new DateTimeOffset(2025, 12, 31, 3, 0, 0, TimeSpan.FromHours(9)), now));
    }

    [Fact]
    public async Task Successful_connect_records_last_connected_time_shown_after_reload()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        vm.AskPassword = _ => Task.FromResult<string?>("pw");
        vm.Selected = vm.AllNodes().Single(n => n.Title == "sales");
        Assert.Empty(vm.Selected!.LastConnectedText);

        var outcome = await vm.ConnectWithPromptsAsync(vm.Selected.Profile!);
        Assert.True(outcome?.IsConnected);

        await vm.LoadAsync();
        Assert.NotEmpty(vm.AllNodes().Single(n => n.Title == "sales").LastConnectedText);
    }

    // ---------- 2. 행 동작(복제·가져오기·연결 문자열 복사)과 단축키 ----------

    [Fact]
    public async Task Duplicate_appends_copy_suffix_keeps_original_password_and_copies_none()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        var original = vm.AllNodes().Single(n => n.Title == "ft-dev").Profile!;
        var storedBefore = fx.Credentials.Items.Count;
        vm.Selected = vm.AllNodes().Single(n => n.Title == "ft-dev");

        await vm.DuplicateCommand.ExecuteAsync(null);

        var copy = vm.Selected!.Profile!;
        Assert.Equal("ft-dev (사본)", copy.Name);
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Null(copy.CredentialRef);
        Assert.Null(copy.LastConnectedAt);
        Assert.Equal(original.DataSource, copy.DataSource);
        Assert.Equal(storedBefore, fx.Credentials.Items.Count);
        Assert.Equal("pw", fx.Credentials.Text(original.CredentialRef!));
        Assert.Same(vm.Selected.Profile, vm.Editor!.Original);
        Assert.False(vm.Editor.SavePassword);
        Assert.DoesNotContain("pw", fx.ConnectionsJson, StringComparison.Ordinal);

        vm.Selected = vm.AllNodes().Single(n => n.Title == "ft-dev");
        await vm.DuplicateCommand.ExecuteAsync(null);
        Assert.Equal("ft-dev (사본) (2)", vm.Selected!.Title);
    }

    [Fact]
    public async Task Duplicating_an_imported_wallet_copies_the_wallet_folder_so_deleting_the_original_keeps_the_copy_working()
    {
        using var fx = new ServiceFixture();
        var zip = Path.Combine(fx.Directory, "Wallet_ADB.zip");
        using (var fs = File.Create(zip))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            void Add(string entry, string text)
            {
                using var w = new StreamWriter(archive.CreateEntry(entry).Open(), new UTF8Encoding(false));
                w.Write(text);
            }

            Add("tnsnames.ora", "adb_high = (description=(address=(protocol=tcps)(port=1522)(host=h))(connect_data=(service_name=s)))\n");
            Add("sqlnet.ora", "WALLET_LOCATION = (SOURCE = (METHOD = file) (METHOD_DATA = (DIRECTORY=\"?/network/admin\")))\n");
            Add("cwallet.sso", "sso");
        }

        var original = await fx.Service.ImportWalletAsync(zip, null, CancellationToken.None);
        var copy = await fx.Service.DuplicateProfileAsync(original.Id, CancellationToken.None);

        Assert.NotEqual(original.WalletLocation, copy.WalletLocation);
        Assert.Equal(copy.WalletLocation, copy.TnsAdmin);
        Assert.True(File.Exists(Path.Combine(copy.WalletLocation!, "cwallet.sso")));
        Assert.Contains(copy.WalletLocation!, File.ReadAllText(Path.Combine(copy.WalletLocation!, "sqlnet.ora")), StringComparison.OrdinalIgnoreCase);

        await fx.Service.DeleteProfileAsync(original.Id, CancellationToken.None);
        Assert.False(Directory.Exists(original.WalletLocation));
        Assert.True(File.Exists(Path.Combine(copy.WalletLocation!, "tnsnames.ora")));
    }

    [Fact]
    public async Task Import_button_reuses_the_import_flow_and_reloads_the_list()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        Assert.False(vm.ImportCommand.CanExecute(null));

        vm.ImportConnections = async () =>
        {
            await fx.Service.SaveProfileAsync(new ConnectionProfile("imported", "u", "h:1521/s"), null, false, CancellationToken.None);
            return true;
        };
        Assert.True(vm.ImportCommand.CanExecute(null));

        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Contains(vm.AllNodes(), n => n.Title == "imported");
    }

    [Fact]
    public async Task Copy_connection_string_puts_user_at_target_on_the_clipboard_without_password()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        var copied = new List<string>();
        vm.CopyText = text =>
        {
            copied.Add(text);
            return Task.CompletedTask;
        };
        vm.Selected = vm.AllNodes().Single(n => n.Title == "ft-dev");
        vm.Editor!.Password = "secret-typed";

        await vm.CopyConnectionStringCommand.ExecuteAsync(null);

        Assert.Equal(["ft@localhost:1522/FREEPDB1"], copied);
        Assert.False(vm.StatusIsError);
        Assert.DoesNotContain(copied, t => t.Contains("pw", StringComparison.Ordinal) || t.Contains("secret", StringComparison.Ordinal));
    }

    private static TreeViewItem ItemFor(TreeView tree, ConnectionTreeNode node)
    {
        tree.UpdateLayout();
        return (TreeViewItem)tree.TreeContainerFromItem(node)!;
    }

    [Fact]
    public async Task Enter_on_a_row_connects_once_and_closes_the_window()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        await HeadlessHost.Run(async () =>
        {
            var window = new ConnectionManagerWindow { DataContext = vm, Width = 900, Height = 640 };
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();
            var tree = window.FindControl<TreeView>("ConnectionTree")!;
            var node = vm.AllNodes().Single(n => n.Title == "ft-dev");
            vm.Selected = node;
            Dispatcher.UIThread.RunJobs();
            ItemFor(tree, node).Focus();

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

            await HeadlessHost.WaitUntil(() => closed);
            Assert.Single(fx.Sessions.Calls);
            Assert.Equal("pw", fx.Sessions.Calls[0].Password);
        });
    }

    [Fact]
    public async Task Delete_key_asks_then_deletes_and_f2_focuses_the_name_box_and_escape_closes()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        var asked = new List<string>();
        vm.Confirm = message =>
        {
            asked.Add(message);
            return Task.FromResult(true);
        };
        await HeadlessHost.Run(async () =>
        {
            var window = new ConnectionManagerWindow { DataContext = vm, Width = 900, Height = 640 };
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();
            var tree = window.FindControl<TreeView>("ConnectionTree")!;

            var node = vm.AllNodes().Single(n => n.Title == "zeta");
            vm.Selected = node;
            Dispatcher.UIThread.RunJobs();
            ItemFor(tree, node).Focus();
            window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
            Dispatcher.UIThread.RunJobs();
            var nameBox = window.FindControl<ProfileEditorView>("EditorView")!.FindControl<TextBox>("NameBox")!;
            Assert.True(nameBox.IsFocused);

            // 이름 칸의 Delete 는 글자 지우기다(접속을 지우지 않는다).
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(asked);

            ItemFor(tree, node).Focus();
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            await HeadlessHost.WaitUntil(() => vm.AllNodes().All(n => n.Title != "zeta"));
            Assert.Contains("zeta", Assert.Single(asked), StringComparison.Ordinal);

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await HeadlessHost.WaitUntil(() => closed);
            Assert.Empty(fx.Sessions.Calls);
        });
    }

    [Fact]
    public async Task Context_menu_offers_connect_duplicate_delete_and_copy_connect_string()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        await HeadlessHost.Run(() =>
        {
            var window = new ConnectionManagerWindow { DataContext = vm, Width = 900, Height = 640 };
            window.Show();
            var tree = window.FindControl<TreeView>("ConnectionTree")!;
            var menu = tree.ContextMenu!;
            menu.Open(tree);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["접속", "복제", "삭제", "연결 문자열 복사"], menu.Items.OfType<MenuItem>().Select(m => (string)m.Header!));
            Assert.All(menu.Items.OfType<MenuItem>(), m => Assert.False(m.IsEffectivelyEnabled));

            vm.Selected = vm.AllNodes().Single(n => n.Title == "ft-dev");
            Dispatcher.UIThread.RunJobs();
            Assert.All(menu.Items.OfType<MenuItem>(), m => Assert.True(m.IsEffectivelyEnabled));
            Assert.Same(vm.DuplicateCommand, menu.Items.OfType<MenuItem>().Single(m => m.Name == "MenuDuplicate").Command);
            Assert.True(window.FindControl<Button>("DuplicateButton")!.IsEffectivelyEnabled);
            menu.Close();
            window.Close();
        });
    }
}
