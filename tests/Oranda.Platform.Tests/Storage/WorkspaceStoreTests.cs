using Oranda.Core.Storage;
using Oranda.Core.Workspaces;

namespace Oranda.Platform.Tests.Storage;

// 소유: TSK-04-02. 작업 공간·자동 저장·비정상 종료 복구(data-model 2.6, 4.1).
public sealed class WorkspaceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-ws-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private IWorkspaceStore NewStore()
    {
        var type = typeof(PlatformAssembly).Assembly.GetType("Oranda.Platform.Storage.WorkspaceStore")!;
        return (IWorkspaceStore)Activator.CreateInstance(type, new TestPaths(_root))!;
    }

    private static Workspace Sample(string id = "default") => new()
    {
        Id = id,
        Name = "기본",
        SavedAt = new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero),
        ActiveTabId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Tabs =
        [
            new WorkspaceTab
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Title = "SQL 1",
                Content = "select '한글' from dual;",
                IsDirty = true,
                Caret = new TextPosition(3, 7),
                SelectionStart = new TextPosition(1, 1),
                ScrollLine = 12,
            },
            new WorkspaceTab { Id = Guid.NewGuid(), Title = "a.sql", FilePath = @"C:\x\a.sql" },
        ],
    };

    [Fact]
    public async Task Save_then_load_round_trips_all_fields()
    {
        var store = NewStore();
        await store.SaveAsync(Sample(), default);

        var loaded = await store.LoadAsync("default", default);

        Assert.NotNull(loaded);
        Assert.False(loaded.IsReadOnly);
        Assert.Equal(2, loaded.Value.Tabs.Count);
        var t = loaded.Value.Tabs[0];
        Assert.Equal("select '한글' from dual;", t.Content);
        Assert.True(t.IsDirty);
        Assert.Equal(new TextPosition(3, 7), t.Caret);
        Assert.Equal(new TextPosition(1, 1), t.SelectionStart);
        Assert.Equal(12, t.ScrollLine);
        Assert.Null(loaded.Value.Tabs[1].Content);
        Assert.Equal(Sample().ActiveTabId, loaded.Value.ActiveTabId);
    }

    [Fact]
    public async Task Load_missing_returns_null()
    {
        Assert.Null(await NewStore().LoadAsync("none", default));
        Assert.Null(await NewStore().LoadAutosaveAsync("none", default));
    }

    [Fact]
    public async Task Written_file_is_camel_case_without_nulls_and_leaves_no_tmp()
    {
        await NewStore().SaveAsync(Sample(), default);

        var file = Path.Combine(_root, "workspaces", "default.json");
        var text = await File.ReadAllTextAsync(file);
        Assert.Contains("\"schemaVersion\": 1", text);
        Assert.Contains("\"activeTabId\"", text);
        Assert.DoesNotContain("null", text);
        Assert.False(File.Exists(file + ".tmp"));
    }

    [Fact]
    public async Task Autosave_survives_without_delete_and_is_removed_on_clean_exit()
    {
        var store = NewStore();
        await store.AutosaveAsync(Sample(), default);

        // 새 인스턴스 = 앱 재시작. 지우지 않았으니 남아 있다(비정상 종료 흔적).
        var recovered = await NewStore().LoadAutosaveAsync("default", default);
        Assert.NotNull(recovered);
        Assert.Equal("select '한글' from dual;", recovered.Tabs[0].Content);

        await store.DeleteAutosaveAsync("default", default);
        Assert.Null(await NewStore().LoadAutosaveAsync("default", default));
        await store.DeleteAutosaveAsync("default", default); // 두 번째 삭제도 예외 없음
    }

    [Fact]
    public async Task Autosave_overwrites_previous_snapshot()
    {
        var store = NewStore();
        await store.AutosaveAsync(Sample(), default);
        await store.AutosaveAsync(Sample() with { Tabs = [] }, default);

        Assert.Empty((await store.LoadAutosaveAsync("default", default))!.Tabs);
    }

    [Fact]
    public async Task Concurrent_autosaves_never_corrupt_file()
    {
        var store = NewStore();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            store.AutosaveAsync(Sample() with { Name = "n" + i }, default)));

        Assert.NotNull(await store.LoadAutosaveAsync("default", default));
    }

    [Fact]
    public async Task Corrupt_file_is_moved_to_backup_and_treated_as_missing()
    {
        var dir = Path.Combine(_root, "workspaces");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), "{ not json");

        var loaded = await NewStore().LoadAsync("default", default);

        Assert.Null(loaded);
        Assert.False(File.Exists(Path.Combine(dir, "default.json")));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "backup"), "default.json.corrupt-*"));
    }

    [Fact]
    public async Task Missing_schema_version_is_corrupt()
    {
        var dir = Path.Combine(_root, "autosave");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), "{\"id\":\"default\",\"name\":\"x\"}");

        Assert.Null(await NewStore().LoadAutosaveAsync("default", default));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "backup")));
    }

    [Fact]
    public async Task Future_version_opens_read_only_and_is_never_overwritten()
    {
        var dir = Path.Combine(_root, "workspaces");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "default.json");
        var original = "{\"schemaVersion\":9,\"id\":\"default\",\"name\":\"미래\",\"savedAt\":\"2026-10-19T08:00:00Z\",\"newField\":1,\"tabs\":[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"title\":\"t\"}]}";
        await File.WriteAllTextAsync(file, original);
        var store = NewStore();

        var loaded = await store.LoadAsync("default", default);

        Assert.NotNull(loaded);
        Assert.True(loaded.IsReadOnly);
        Assert.NotNull(loaded.Notice);
        Assert.Equal("미래", loaded.Value.Name);

        await store.SaveAsync(loaded.Value with { Name = "덮어쓰기" }, default);
        Assert.Equal(original, await File.ReadAllTextAsync(file));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("Default")]
    [InlineData("")]
    [InlineData("a b")]
    public async Task Invalid_id_is_rejected(string id)
    {
        var store = NewStore();
        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadAsync(id, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AutosaveAsync(Sample(id), default));
    }

    private sealed class TestPaths(string root) : IAppDataPaths
    {
        public string DataDirectory => root;

        public string SettingsFile => Path.Combine(root, "settings.json");

        public string ConnectionsFile => Path.Combine(root, "connections.json");

        public string LayoutFile => Path.Combine(root, "layout.json");

        public string FormatterDirectory => Path.Combine(root, "formatter");

        public string KeymapDirectory => Path.Combine(root, "keymaps");

        public string WorkspacesDirectory => Path.Combine(root, "workspaces");

        public string AutosaveDirectory => Path.Combine(root, "autosave");

        public string DatabaseFile => Path.Combine(root, "oranda.db");

        public string CredentialsDirectory => Path.Combine(root, "credentials");

        public string LogsDirectory => Path.Combine(root, "logs");

        public string BackupDirectory => Path.Combine(root, "backup");
    }
}
