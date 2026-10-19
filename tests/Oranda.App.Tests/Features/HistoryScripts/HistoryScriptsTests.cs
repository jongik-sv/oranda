using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.HistoryScripts;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Settings;
using Oranda.Core.Shell;
using Oranda.Core.Storage;

namespace Oranda.App.Tests.Features.HistoryScripts;

// 소유: TSK-06-06. 이력 창·스크립트 창·명령(모델 + Headless).
public sealed class HistoryScriptsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-scripts-" + Guid.NewGuid().ToString("N"));

    public HistoryScriptsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    private static HistoryEntry Entry(long id, string sql, Guid? profile = null, HistoryStatus status = HistoryStatus.Succeeded) => new()
    {
        Id = id,
        ProfileId = profile ?? Guid.Empty,
        ProfileName = "개발",
        ExecutedAt = new DateTimeOffset(2026, 10, 1, 12, 0, (int)id % 60, TimeSpan.Zero),
        SqlText = sql,
        StatementKind = StatementKind.Query,
        Source = HistorySource.Editor,
        Status = status,
        ElapsedMs = 42,
        RowsAffected = 3,
    };

    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new ScriptsViewModel());
        Assert.NotNull(new HistoryViewModel());
    }

    // ---- 이력 창 ----

    [Fact]
    public async Task History_reload_applies_search_status_and_profile_filters()
    {
        var store = new ListStore([Entry(1, "SELECT 1 FROM dual"), Entry(2, "DELETE FROM t", status: HistoryStatus.Failed)]);
        var profile = Guid.NewGuid();
        using var vm = new HistoryViewModel(store, () => profile);

        await vm.ReloadAsync();
        Assert.Equal(2, vm.Items.Count);
        Assert.Equal(profile, store.LastQuery!.ProfileId); // 기본은 이 접속만

        vm.CurrentConnectionOnly = false;
        vm.SearchText = "delete";
        vm.StatusFilter = vm.StatusFilters.First(f => f.Status == HistoryStatus.Failed);
        await vm.ReloadAsync();
        Assert.Null(store.LastQuery!.ProfileId);
        Assert.Equal("delete", store.LastQuery.Text);
        Assert.Equal(HistoryStatus.Failed, store.LastQuery.Status);
    }

    [Fact]
    public async Task History_delete_clear_and_load_more_go_through_the_store()
    {
        var entries = Enumerable.Range(1, HistoryViewModel.PageSize + 5).Select(i => Entry(i, $"SELECT {i} FROM dual")).Reverse().ToList();
        var store = new ListStore(entries);
        using var vm = new HistoryViewModel(store, () => null);
        vm.CurrentConnectionOnly = false;

        await vm.ReloadAsync();
        Assert.Equal(HistoryViewModel.PageSize, vm.Items.Count);
        Assert.True(vm.CanLoadMore);

        await vm.LoadMoreAsync();
        Assert.Equal(HistoryViewModel.PageSize + 5, vm.Items.Count);
        Assert.False(vm.CanLoadMore);

        var victim = vm.Items[0];
        await vm.DeleteAsync([victim]);
        Assert.DoesNotContain(victim, vm.Items);
        Assert.Equal([victim.Id], store.Deleted);

        await vm.ClearAsync(currentConnectionOnly: false);
        Assert.True(store.Cleared);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void History_item_shows_a_one_line_preview_and_error()
    {
        var item = new HistoryItem(Entry(1, "\n  SELECT *\n  FROM emp", status: HistoryStatus.Failed) with { ErrorCode = "ORA-00942", ErrorMessage = "no table" });

        Assert.Equal("SELECT *", item.SqlPreview);
        Assert.Equal("실패", item.StatusText);
        Assert.Equal("42 ms", item.ElapsedText);
        Assert.Equal("ORA-00942: no table", item.ErrorText);
    }

    [Fact]
    public async Task History_window_lists_entries_headless()
    {
        var store = new ListStore([Entry(1, "SELECT 1 FROM dual"), Entry(2, "SELECT 2 FROM dual")]);
        using var vm = new HistoryViewModel(store, () => null);
        await vm.ReloadAsync();

        await HeadlessHost.Run(() =>
        {
            var window = new Window { Content = new HistoryView { DataContext = vm }, Width = 900, Height = 300 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var list = ((HistoryView)window.Content).FindControl<ListBox>("List")!;
            Assert.Equal(2, list.ItemCount);
            Assert.NotNull(((HistoryView)window.Content).FindControl<Button>("RerunButton"));
            window.Close();
        });
    }

    // ---- 스크립트 창 ----

    [Fact]
    public void Scripts_tree_lists_folders_first_and_only_script_files()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "b_sub"));
        File.WriteAllText(Path.Combine(_dir, "b_sub", "inner.sql"), "SELECT 1 FROM dual;");
        File.WriteAllText(Path.Combine(_dir, "a.sql"), "SELECT 1 FROM dual;");
        File.WriteAllText(Path.Combine(_dir, "pic.png"), "x");
        var vm = new ScriptsViewModel(new ScriptFolderList());

        Assert.True(vm.AddFolder(_dir));
        var root = Assert.Single(vm.Roots);
        Assert.True(root.IsExpanded);
        Assert.Equal(["b_sub", "a.sql"], root.Children.Select(c => c.Name));

        root.Children[0].IsExpanded = true;
        Assert.Equal(["inner.sql"], root.Children[0].Children.Select(c => c.Name));

        vm.SelectedNode = root.Children[1];
        Assert.Equal(Path.Combine(_dir, "a.sql"), vm.SelectedFilePath);
        Assert.Equal(Path.Combine(_dir, "a.sql"), HistoryScriptsCommands.SelectedScriptPath);
        vm.SelectedNode = root;
        Assert.Null(vm.SelectedFilePath);
    }

    [Fact]
    public void Script_folder_links_persist_and_survive_a_broken_file()
    {
        var data = Path.Combine(_dir, "data");
        var scripts = Path.Combine(_dir, "scripts");
        Directory.CreateDirectory(scripts);

        var first = new ScriptFolderList(data);
        Assert.True(first.Add(scripts));
        Assert.False(first.Add(scripts)); // 같은 폴더는 한 번만

        Assert.Equal([Path.GetFullPath(scripts)], new ScriptFolderList(data).Folders);

        File.WriteAllText(Path.Combine(data, "script-folders.json"), "{ not json");
        Assert.Empty(new ScriptFolderList(data).Folders);

        Assert.True(new ScriptFolderList(data).Add(scripts));
        var list = new ScriptFolderList(data);
        Assert.True(list.Remove(scripts));
        Assert.Empty(new ScriptFolderList(data).Folders);
    }

    [Fact]
    public void Adding_a_missing_folder_is_refused()
    {
        var vm = new ScriptsViewModel(new ScriptFolderList());

        Assert.False(vm.AddFolder(Path.Combine(_dir, "nope")));
        Assert.Empty(vm.Roots);
    }

    [Fact]
    public async Task Opening_a_script_raises_open_requested()
    {
        File.WriteAllText(Path.Combine(_dir, "x.sql"), "SELECT 1 FROM dual;");
        var vm = new ScriptsViewModel(new ScriptFolderList());
        vm.AddFolder(_dir);
        string? opened = null;
        vm.OpenRequested += (_, p) => opened = p;

        await vm.OpenAsync(vm.Roots[0].Children.Single(c => c.Name == "x.sql"));

        Assert.Equal(Path.Combine(_dir, "x.sql"), opened);
    }

    [Fact]
    public async Task Scripts_window_shows_connected_folder_headless()
    {
        File.WriteAllText(Path.Combine(_dir, "x.sql"), "SELECT 1 FROM dual;");
        var vm = new ScriptsViewModel(new ScriptFolderList());
        vm.AddFolder(_dir);

        await HeadlessHost.Run(() =>
        {
            var view = new ScriptsView { DataContext = vm };
            var window = new Window { Content = view, Width = 400, Height = 400 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, view.FindControl<TreeView>("Tree")!.ItemCount);
            Assert.NotNull(view.FindControl<Button>("AddButton"));
            window.Close();
        });
    }

    // ---- 명령 ----

    [Fact]
    public async Task Command_handler_without_services_does_nothing()
    {
        var handler = new HistoryScriptsCommandHandler();

        await handler.ExecuteAsync(new CommandContext("oranda.exec.toggleStatistics", new EmptyKeys()), default);

        Assert.False(HistoryScriptsCommands.StatisticsEnabled);
    }

    private sealed class EmptyKeys : IContextKeys
    {
        public bool Evaluate(string? expression) => true;
    }

    private sealed class ListStore(List<HistoryEntry> entries) : IHistoryStore
    {
        public ListStore(IEnumerable<HistoryEntry> items)
            : this([.. items])
        {
        }

        public HistoryQuery? LastQuery { get; private set; }

        public List<long> Deleted { get; } = [];

        public bool Cleared { get; private set; }

        public Task<long> AddAsync(HistoryEntry entry, CancellationToken ct) => Task.FromResult(0L);

        public Task<IReadOnlyList<HistoryEntry>> SearchAsync(HistoryQuery query, CancellationToken ct)
        {
            LastQuery = query;
            var rows = entries.Where(e => query.BeforeId is null || e.Id < query.BeforeId)
                .OrderByDescending(e => e.Id).Take(query.Limit).ToList();
            return Task.FromResult<IReadOnlyList<HistoryEntry>>(rows);
        }

        public Task DeleteAsync(IReadOnlyList<long> ids, CancellationToken ct)
        {
            Deleted.AddRange(ids);
            entries.RemoveAll(e => ids.Contains(e.Id));
            return Task.CompletedTask;
        }

        public Task ClearAsync(Guid? profileId, CancellationToken ct)
        {
            Cleared = true;
            entries.Clear();
            return Task.CompletedTask;
        }

        public Task PruneAsync(HistorySettings settings, CancellationToken ct) => Task.CompletedTask;
    }
}
