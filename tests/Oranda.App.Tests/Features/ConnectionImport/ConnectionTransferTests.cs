using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ConnectionImport;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Tests.Features.ConnectionManager;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.ConnectionImport;

// 소유: TSK-13-13. 접속 내보내기(고른 것만)·Oranda 파일 가져오기(이미 있음 표시·새 Id)·고르기 창.

public sealed class ConnectionTransferTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oranda-transfer-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static ConnectionProfile Orders => new(
        "운영-주문DB", "app_ro", "tcps://db01:1522/ORDERS", Environment: ConnectionEnvironment.Production, Group: "주문",
        Comment: "조회 전용\n배치 때 쓰지 않음");

    private static ConnectionProfile Local => new("개발-로컬", "scott", "localhost:1521/FREEPDB1", Color: "#0063B1", Group: "개발");

    private static ConnectionProfile Qa => new("검증-TNS", "system", "QA19C", Auth: AuthMode.SysDba, Environment: ConnectionEnvironment.Test);

    private static async Task<IReadOnlyList<ConnectionProfile>> SeedAsync(ServiceFixture fx, params ConnectionProfile[] profiles)
    {
        var saved = new List<ConnectionProfile>();
        foreach (var p in profiles)
        {
            saved.Add(await fx.Service.SaveProfileAsync(p, "pw", true, CancellationToken.None));
        }

        return saved;
    }

    // ---------- 고르기 모델 ----------

    [Fact]
    public void Export_list_starts_all_checked_and_select_all_or_none_toggles_every_row()
    {
        var vm = ConnectionPickViewModel.ForExport([Orders, Local, Qa]);
        Assert.Equal(3, vm.Items.Count);
        Assert.All(vm.Items, i => Assert.True(i.IsChecked));
        Assert.Equal(3, vm.CheckedCount);
        Assert.True(vm.CanConfirm);
        Assert.Equal("주문", vm.Items[0].Folder);

        vm.SelectNoneCommand.Execute(null);
        Assert.Equal(0, vm.CheckedCount);
        Assert.False(vm.CanConfirm);
        Assert.Empty(vm.Chosen);

        vm.Items[2].IsChecked = true;
        Assert.Equal(1, vm.CheckedCount);
        Assert.True(vm.CanConfirm);
        Assert.Equal([Qa], vm.Chosen);

        vm.SelectAllCommand.Execute(null);
        Assert.Equal(3, vm.CheckedCount);
    }

    [Fact]
    public void Import_list_flags_existing_and_invalid_rows_and_leaves_them_unchecked()
    {
        var existing = new[] { Local with { User = "SCOTT", Id = Guid.NewGuid() } };
        var bad = new ConnectionProfile("비밀 섞임", "u", "h:1521/s;password=tiger");
        var vm = ConnectionPickViewModel.ForImport([Orders, Local, bad], existing);

        Assert.True(vm.Items[0].IsChecked);
        Assert.False(vm.Items[0].Exists);
        Assert.False(vm.Items[1].IsChecked); // 이미 있음
        Assert.True(vm.Items[1].Exists);
        Assert.NotEmpty(vm.Items[1].Note);
        Assert.True(vm.Items[1].CanCheck); // 굳이 고를 수는 있다

        Assert.False(vm.Items[2].IsChecked); // 저장할 수 없는 값
        Assert.False(vm.Items[2].CanCheck);
        Assert.Contains("비밀번호", vm.Items[2].Note, StringComparison.Ordinal);
        vm.SelectAllCommand.Execute(null);
        Assert.False(vm.Items[2].IsChecked); // 모두 선택도 고르지 않는다
        Assert.Equal(2, vm.CheckedCount);
    }

    // ---------- 내보내기·가져오기 ----------

    [Fact]
    public async Task Export_writes_only_checked_profiles_and_cancel_writes_nothing()
    {
        using var fx = new ServiceFixture();
        await SeedAsync(fx, Orders, Local, Qa);
        var path = Path.Combine(_dir, "out.json");

        var cancelled = await ConnectionTransfer.ExportAsync(fx.Service, _ => Task.FromResult(false), () => Task.FromResult<string?>(path), CancellationToken.None);
        Assert.Null(cancelled);
        Assert.False(File.Exists(path));

        var noPath = await ConnectionTransfer.ExportAsync(fx.Service, _ => Task.FromResult(true), () => Task.FromResult<string?>(null), CancellationToken.None);
        Assert.Null(noPath);

        var count = await ConnectionTransfer.ExportAsync(
            fx.Service,
            vm =>
            {
                vm.Items.Single(i => i.Name == Local.Name).IsChecked = false;
                return Task.FromResult(true);
            },
            () => Task.FromResult<string?>(path),
            CancellationToken.None);

        Assert.Equal(2, count);
        var json = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("credentialRef", json, StringComparison.Ordinal);
        Assert.Equal([Orders.Name, Qa.Name], ConnectionExport.Read(json).Select(p => p.Name));
    }

    [Fact]
    public async Task Export_with_no_connections_reports_zero_without_asking()
    {
        using var fx = new ServiceFixture();
        var asked = false;
        var count = await ConnectionTransfer.ExportAsync(fx.Service, _ => Task.FromResult(asked = true), () => Task.FromResult<string?>(null), CancellationToken.None);
        Assert.Equal(0, count);
        Assert.False(asked);
    }

    [Fact]
    public async Task Exported_file_imports_into_another_data_folder_with_new_ids_and_existing_rows_flagged()
    {
        using var source = new ServiceFixture();
        var saved = await SeedAsync(source, Orders, Local, Qa);
        var path = Path.Combine(_dir, "oranda-connections.json");
        await ConnectionTransfer.ExportAsync(source.Service, _ => Task.FromResult(true), () => Task.FromResult<string?>(path), CancellationToken.None);
        var json = await File.ReadAllTextAsync(path);

        using var target = new ServiceFixture();
        await SeedAsync(target, Local); // 이미 있는 접속
        ConnectionPickViewModel? shown = null;
        var summary = await ConnectionTransfer.ImportFileAsync(
            target.Service,
            json,
            vm =>
            {
                shown = vm;
                Assert.False(vm.Items.Single(i => i.Name == Local.Name).IsChecked);
                vm.Items.Single(i => i.Name == Local.Name).IsChecked = true; // 굳이 가져오면 「이름 (2)」
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.NotNull(shown);
        Assert.Equal(3, summary!.Added);
        var all = (await target.Service.LoadAsync(CancellationToken.None)).Value.Profiles;
        Assert.Equal(4, all.Count);
        var orders = all.Single(p => p.Name == Orders.Name);
        Assert.Equal("주문", orders.Group);
        Assert.Equal(ConnectionEnvironment.Production, orders.Environment);
        Assert.Equal(Orders.Comment, orders.Comment);
        Assert.Null(orders.CredentialRef);
        Assert.Null(orders.LastConnectedAt);
        Assert.NotEqual(Guid.Empty, orders.Id);
        Assert.DoesNotContain(saved, s => s.Id == orders.Id);
        Assert.Equal("#0063B1", all.Single(p => p.Name == Local.Name + " (2)").Color);
        Assert.Equal(AuthMode.SysDba, all.Single(p => p.Name == Qa.Name).Auth);

        var cancelled = await ConnectionTransfer.ImportFileAsync(target.Service, json, _ => Task.FromResult(false), CancellationToken.None);
        Assert.Null(cancelled);
        Assert.Equal(4, (await target.Service.LoadAsync(CancellationToken.None)).Value.Profiles.Count);
    }

    [Fact]
    public async Task Imported_last_connected_time_is_dropped_even_if_the_file_was_edited_by_hand()
    {
        using var fx = new ServiceFixture();
        var json = """
            { "format": "oranda.connections", "schemaVersion": 1,
              "profiles": [ { "name": "손으로", "user": "u", "dataSource": "h:1521/s", "credentialRef": "x", "lastConnectedAt": "2026-10-01T00:00:00Z" } ] }
            """;
        await ConnectionTransfer.ImportFileAsync(fx.Service, json, _ => Task.FromResult(true), CancellationToken.None);
        var p = (await fx.Service.LoadAsync(CancellationToken.None)).Value.Profiles.Single();
        Assert.Null(p.LastConnectedAt);
        Assert.Null(p.CredentialRef);
    }

    // ---------- 고르기 창 ----------

    [Fact]
    public Task Pick_dialog_checks_two_rows_and_confirm_returns_exactly_those() => HeadlessHost.Run(async () =>
    {
        var owner = new Window();
        owner.Show();
        var vm = ConnectionPickViewModel.ForExport([Orders, Local, Qa]);
        var dialog = new ConnectionPickDialog { DataContext = vm };
        var result = dialog.ShowDialog<bool>(owner);
        Dispatcher.UIThread.RunJobs();

        var confirm = dialog.FindControl<Button>("ConfirmButton")!;
        Assert.True(confirm.IsEffectivelyEnabled);
        var selectNone = dialog.FindControl<Button>("SelectNoneButton")!;
        selectNone.Command!.Execute(selectNone.CommandParameter); // 단추에 묶인 명령
        Dispatcher.UIThread.RunJobs();
        Assert.False(confirm.IsEffectivelyEnabled);

        var boxes = dialog.FindControl<ItemsControl>("PickList")!.GetVisualDescendants().OfType<CheckBox>().ToList();
        Assert.Equal(3, boxes.Count);
        Assert.Equal(Orders.Name, Avalonia.Automation.AutomationProperties.GetName(boxes[0]));
        boxes[0].IsChecked = true;
        boxes[2].IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(confirm.IsEffectivelyEnabled);
        Assert.Contains("2", dialog.FindControl<TextBlock>("CountText")!.Text, StringComparison.Ordinal);

        confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(await result);
        Assert.Equal([Orders.Name, Qa.Name], vm.Chosen.Select(p => p.Name));
        owner.Close();
    });

    [Fact]
    public Task Pick_dialog_cancel_returns_false() => HeadlessHost.Run(async () =>
    {
        var owner = new Window();
        owner.Show();
        var dialog = new ConnectionPickDialog { DataContext = ConnectionPickViewModel.ForExport([Local]) };
        var result = dialog.ShowDialog<bool>(owner);
        Dispatcher.UIThread.RunJobs();
        dialog.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(await result);
        owner.Close();
    });

    [Fact]
    public void Export_command_is_declared()
    {
        Assert.Contains("oranda.conn.export", ConnectionExportCommands.Ids);
    }

    [Fact]
    public async Task Manager_export_button_runs_the_export_hook()
    {
        using var fx = new ServiceFixture();
        var vm = new ConnectionManagerViewModel(fx.Service);
        await vm.LoadAsync();
        Assert.False(vm.ExportCommand.CanExecute(null));

        var ran = false;
        vm.ExportConnections = () =>
        {
            ran = true;
            return Task.FromResult(true);
        };
        Assert.True(vm.ExportCommand.CanExecute(null));
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.True(ran);
    }
}
