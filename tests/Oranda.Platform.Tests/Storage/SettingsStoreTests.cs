using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Oranda.Core.Events;
using Oranda.Core.Settings;
using Oranda.Platform.Storage;
using Oranda.Testing;

namespace Oranda.Platform.Tests.Storage;

// 소유: TSK-03-01. 경로·JSON 공통·settings.json.
public sealed partial class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeEventBus _bus = new();

    public SettingsStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AppDataPaths Paths => new(_dir);

    private SettingsStore NewStore() => new(Paths, _bus, []);

    // ---- AppDataPaths ----

    [Fact]
    public void Paths_follow_data_model_layout()
    {
        var p = Paths;
        Assert.Equal(Path.Combine(_dir, "settings.json"), p.SettingsFile);
        Assert.Equal(Path.Combine(_dir, "connections.json"), p.ConnectionsFile);
        Assert.Equal(Path.Combine(_dir, "layout.json"), p.LayoutFile);
        Assert.Equal(Path.Combine(_dir, "formatter"), p.FormatterDirectory);
        Assert.Equal(Path.Combine(_dir, "keymaps"), p.KeymapDirectory);
        Assert.Equal(Path.Combine(_dir, "workspaces"), p.WorkspacesDirectory);
        Assert.Equal(Path.Combine(_dir, "autosave"), p.AutosaveDirectory);
        Assert.Equal(Path.Combine(_dir, "oranda.db"), p.DatabaseFile);
        Assert.Equal(Path.Combine(_dir, "credentials"), p.CredentialsDirectory);
        Assert.Equal(Path.Combine(_dir, "logs"), p.LogsDirectory);
        Assert.Equal(Path.Combine(_dir, "backup"), p.BackupDirectory);
    }

    [Fact]
    public void Environment_variable_overrides_data_directory()
    {
        var dir = AppDataPaths.ResolveDataDirectory(k => k == "ORANDA_DATA_DIR" ? _dir : null);
        Assert.Equal(_dir, dir);
    }

    [Fact]
    public void Default_directory_is_named_Oranda_and_not_created()
    {
        var dir = AppDataPaths.ResolveDataDirectory(_ => null);
        Assert.Equal("Oranda", Path.GetFileName(dir));
    }

    // ---- JsonDocumentFile ----

    [Fact]
    public async Task Missing_file_gives_default_without_creating_it()
    {
        var r = await Load(Path.Combine(_dir, "doc.json"));
        Assert.Equal(JsonLoadState.Missing, r.State);
        Assert.Equal("default", r.Value.Name);
        Assert.False(File.Exists(Path.Combine(_dir, "doc.json")));
    }

    [Fact]
    public async Task Write_is_atomic_leaves_no_tmp_and_round_trips_utf8_without_bom()
    {
        var path = Path.Combine(_dir, "sub", "doc.json");
        await JsonDocumentFile.WriteAsync(path, new TestDoc { Name = "가나다" }, TestJson.Default.TestDoc, default);

        Assert.False(File.Exists(path + ".tmp"));
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.NotEqual(0xEF, bytes[0]);
        var r = await Load(path);
        Assert.Equal(JsonLoadState.Current, r.State);
        Assert.Equal("가나다", r.Value.Name);
        Assert.Contains("\n  \"", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task Failed_write_keeps_original()
    {
        var path = Path.Combine(_dir, "doc.json");
        await JsonDocumentFile.WriteAsync(path, new TestDoc { Name = "old" }, TestJson.Default.TestDoc, default);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => JsonDocumentFile.WriteAsync(path, new TestDoc { Name = "new" }, TestJson.Default.TestDoc, cts.Token));

        Assert.Equal("old", (await Load(path)).Value.Name);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData("not json {")]
    [InlineData("{ \"name\": \"x\" }")]
    [InlineData("{ \"schemaVersion\": \"two\" }")]
    [InlineData("[1,2]")]
    public async Task Corrupt_file_is_moved_to_backup_and_default_is_used(string content)
    {
        var path = Path.Combine(_dir, "doc.json");
        await File.WriteAllTextAsync(path, content);

        var r = await Load(path);

        Assert.Equal(JsonLoadState.Corrupt, r.State);
        Assert.False(r.IsReadOnly);
        Assert.NotNull(r.Notice);
        Assert.Equal("default", r.Value.Name);
        Assert.False(File.Exists(path));
        var moved = Assert.Single(Directory.GetFiles(Path.Combine(_dir, "backup")));
        Assert.Contains("doc.json.corrupt-20260101000000", moved);
    }

    [Fact]
    public async Task Older_version_is_backed_up_migrated_stepwise_and_rewritten()
    {
        var path = Path.Combine(_dir, "doc.json");
        await File.WriteAllTextAsync(path, """{ "schemaVersion": 1, "title": "옛이름" }""");

        var r = await Load(path);

        Assert.Equal(JsonLoadState.Migrated, r.State);
        Assert.Equal("옛이름", r.Value.Name);
        Assert.Contains("\"schemaVersion\": 3", await File.ReadAllTextAsync(path));
        Assert.Contains(Directory.GetFiles(Path.Combine(_dir, "backup")), f => f.EndsWith("doc.json.v1.20260101000000.bak", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_migration_step_makes_file_read_only_and_keeps_original()
    {
        var path = Path.Combine(_dir, "doc.json");
        const string original = """{ "schemaVersion": 1, "title": "x" }""";
        await File.WriteAllTextAsync(path, original);

        var r = await JsonDocumentFile.LoadAsync(path, "test", 3, TestJson.Default.TestDoc, () => new TestDoc(), [new Rename()], Path.Combine(_dir, "backup"), default);

        Assert.Equal(JsonLoadState.MigrationFailed, r.State);
        Assert.True(r.IsReadOnly);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Future_version_is_read_only_and_known_fields_are_read()
    {
        var path = Path.Combine(_dir, "doc.json");
        await File.WriteAllTextAsync(path, """{ "schemaVersion": 9, "name": "미래", "novel": 1 }""");

        var r = await Load(path);

        Assert.Equal(JsonLoadState.FutureVersion, r.State);
        Assert.True(r.IsReadOnly);
        Assert.Equal("미래", r.Value.Name);
    }

    [Fact]
    public async Task Only_three_latest_backups_are_kept()
    {
        var path = Path.Combine(_dir, "doc.json");
        for (var i = 0; i < 5; i++)
        {
            await File.WriteAllTextAsync(path, """{ "schemaVersion": 1, "title": "x" }""");
            var at = new DateTimeOffset(2026, 1, 1, 0, 0, i, TimeSpan.Zero);
            await JsonDocumentFile.LoadAsync(path, "test", 3, TestJson.Default.TestDoc, () => new TestDoc(), [new Rename(), new Bump()], Path.Combine(_dir, "backup"), default, () => at);
        }

        Assert.Equal(JsonDocumentFile.BackupsToKeep, Directory.GetFiles(Path.Combine(_dir, "backup"), "doc.json.v*.bak").Length);
    }

    // ---- SettingsStore ----

    [Fact]
    public async Task Load_without_file_gives_defaults_and_does_not_write()
    {
        var doc = await NewStore().LoadAsync(default);

        Assert.Equal(new AppSettings(), doc.Value);
        Assert.False(doc.IsReadOnly);
        Assert.False(File.Exists(Paths.SettingsFile));
    }

    [Fact]
    public async Task Save_then_load_round_trips_with_data_model_json_shape()
    {
        var store = NewStore();
        var settings = new AppSettings
        {
            Ui = new UiSettings { Theme = Theme.Dark, Language = "ko" },
            Editor = new EditorSettings { FontFamily = "Consolas", FontSize = 15, TabSize = 2 },
        };

        await store.SaveAsync(settings, default);
        var text = await File.ReadAllTextAsync(Paths.SettingsFile);
        var loaded = await NewStore().LoadAsync(default);

        Assert.Equal(settings, loaded.Value);
        Assert.Contains("\"theme\": \"Dark\"", text);
        Assert.Contains("\"fontFamily\": \"Consolas\"", text);
        Assert.StartsWith("{\n  \"schemaVersion\": 1", text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Null_font_family_is_not_written_and_reads_back_null()
    {
        var store = NewStore();
        await store.SaveAsync(new AppSettings(), default);

        Assert.DoesNotContain("fontFamily", await File.ReadAllTextAsync(Paths.SettingsFile));
        Assert.Null((await NewStore().LoadAsync(default)).Value.Editor.FontFamily);
    }

    [Fact]
    public async Task Save_publishes_SettingsChanged_only_for_changed_sections()
    {
        var store = NewStore();
        await store.LoadAsync(default);

        await store.SaveAsync(new AppSettings { Ui = new UiSettings { Theme = Theme.Light }, KeymapId = "orange5" }, default);

        Assert.Equal(["ui", "keymapId"], _bus.PublishedOf<SettingsChanged>().Select(e => e.Section));

        await store.SaveAsync(new AppSettings { Ui = new UiSettings { Theme = Theme.Light }, KeymapId = "orange5" }, default);
        Assert.Equal(2, _bus.PublishedOf<SettingsChanged>().Count());
    }

    [Fact]
    public async Task Schema_browser_preview_setting_defaults_on_and_publishes_its_own_section()
    {
        var store = NewStore();
        Assert.True((await store.LoadAsync(default)).Value.SchemaBrowser.SingleClickPreview);

        await store.SaveAsync(new AppSettings { SchemaBrowser = new SchemaBrowserSettings { SingleClickPreview = false } }, default);

        Assert.Equal(["schemaBrowser"], _bus.PublishedOf<SettingsChanged>().Select(e => e.Section));
        Assert.False((await NewStore().LoadAsync(default)).Value.SchemaBrowser.SingleClickPreview);
    }

    [Fact]
    public async Task Saved_schema_filters_round_trip_and_same_content_publishes_nothing()
    {
        var store = NewStore();
        await store.LoadAsync(default);
        var filter = new SchemaBrowserFilter
        {
            Name = "APUser",
            Scope = SchemaFilterScope.Selected,
            Schemas = ["EAI_USER", "APP_USER"],
            NamePatterns = new Dictionary<string, string> { ["TABLE"] = "TB_ORD*" },
        };
        var settings = new AppSettings
        {
            SchemaBrowser = new SchemaBrowserSettings { Filters = [filter], ActiveFilters = new Dictionary<string, string> { ["p"] = filter.Id } },
        };

        await store.SaveAsync(settings, default);
        var reloaded = (await NewStore().LoadAsync(default)).Value;
        await store.SaveAsync(reloaded, default);

        Assert.Equal(settings.SchemaBrowser, reloaded.SchemaBrowser);
        Assert.Equal(["schemaBrowser"], _bus.PublishedOf<SettingsChanged>().Select(e => e.Section));
    }

    [Fact]
    public async Task Flashback_track_settings_default_on_recent_one_and_a_half_days_and_round_trip()
    {
        // TSK-13-44: 변경 추적하기 사용·기간(최근 N일 / 가능한 전체 기간). 옛 파일(키 없음) = 켬·최근 1.5일.
        await File.WriteAllTextAsync(Paths.SettingsFile, """{ "schemaVersion": 1, "grid": { "pageSize": 300 } }""");
        var old = (await NewStore().LoadAsync(default)).Value.Grid;
        Assert.True(old.FlashbackTrackEnabled);
        Assert.Equal(FlashbackTrackRange.RecentDays, old.FlashbackTrackRange);
        Assert.Equal(1.5, old.FlashbackTrackDays);

        var store = NewStore();
        await store.LoadAsync(default);
        await store.SaveAsync(new AppSettings { Grid = new GridSettings { FlashbackTrackEnabled = false, FlashbackTrackRange = FlashbackTrackRange.All, FlashbackTrackDays = 2.25 } }, default);

        var json = JsonNode.Parse(await File.ReadAllTextAsync(Paths.SettingsFile))!["grid"]!;
        Assert.False(json["flashbackTrackEnabled"]!.GetValue<bool>());
        Assert.Equal("All", json["flashbackTrackRange"]!.GetValue<string>());
        Assert.Equal(2.25, json["flashbackTrackDays"]!.GetValue<double>());
        var back = (await NewStore().LoadAsync(default)).Value.Grid;
        Assert.False(back.FlashbackTrackEnabled);
        Assert.Equal(FlashbackTrackRange.All, back.FlashbackTrackRange);
        Assert.Equal(2.25, back.FlashbackTrackDays);
        Assert.Contains("grid", _bus.PublishedOf<SettingsChanged>().Select(e => e.Section));
    }

    [Theory]
    [InlineData(0.0, 0.01)]
    [InlineData(-3.0, 0.01)]
    [InlineData(1000.0, 365.0)]
    [InlineData(1.234, 1.23)]
    [InlineData(double.NaN, 1.5)]
    public void Flashback_track_days_are_kept_in_range(double saved, double expected)
    {
        var v = SettingsStore.Normalize(new AppSettings { Grid = new GridSettings { FlashbackTrackDays = saved } });

        Assert.Equal(expected, v.Grid.FlashbackTrackDays);
    }

    [Fact]
    public async Task Values_are_clamped_to_documented_ranges()
    {
        var store = NewStore();
        await store.SaveAsync(
            new AppSettings
            {
                Monitor = new MonitorSettings { RefreshSeconds = 1 },
                Workspace = new WorkspaceSettings { AutosaveSeconds = 99 },
                Editor = new EditorSettings { FontSize = 100 },
            },
            default);

        var v = (await NewStore().LoadAsync(default)).Value;
        Assert.Equal(2, v.Monitor.RefreshSeconds);
        Assert.Equal(10, v.Workspace.AutosaveSeconds);
        Assert.Equal(32, v.Editor.FontSize);
    }

    [Fact]
    public async Task Future_version_settings_are_read_only_and_never_overwritten()
    {
        const string original = """{ "schemaVersion": 7, "ui": { "theme": "Dark" } }""";
        await File.WriteAllTextAsync(Paths.SettingsFile, original);
        var store = NewStore();

        var doc = await store.LoadAsync(default);
        await store.SaveAsync(new AppSettings(), default);

        Assert.True(doc.IsReadOnly);
        Assert.Equal(Theme.Dark, doc.Value.Ui.Theme);
        Assert.NotNull(doc.Notice);
        Assert.Equal(original, await File.ReadAllTextAsync(Paths.SettingsFile));
        Assert.Empty(_bus.PublishedOf<SettingsChanged>());
    }

    [Fact]
    public async Task Corrupt_settings_start_from_defaults_with_notice()
    {
        await File.WriteAllTextAsync(Paths.SettingsFile, "{{{");

        var doc = await NewStore().LoadAsync(default);

        Assert.Equal(new AppSettings(), doc.Value);
        Assert.False(doc.IsReadOnly);
        Assert.NotNull(doc.Notice);
        Assert.Single(Directory.GetFiles(Paths.BackupDirectory));
    }

    [Fact]
    public async Task Unknown_fields_in_same_version_file_are_ignored()
    {
        await File.WriteAllTextAsync(Paths.SettingsFile, """{ "schemaVersion": 1, "future": 1, "ui": { "theme": "Light", "x": 2 } }""");

        var doc = await NewStore().LoadAsync(default);

        Assert.Equal(Theme.Light, doc.Value.Ui.Theme);
        Assert.Null(doc.Notice);
        Assert.Equal("default", doc.Value.KeymapId);
        Assert.Equal(13, doc.Value.Editor.FontSize);
        Assert.Equal(500, doc.Value.Grid.PageSize);
    }

    private Task<JsonLoadResult<TestDoc>> Load(string path) =>
        JsonDocumentFile.LoadAsync(
            path,
            "test",
            3,
            TestJson.Default.TestDoc,
            () => new TestDoc { Name = "default" },
            [new Rename(), new Bump()],
            Path.Combine(_dir, "backup"),
            default,
            () => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    public sealed record TestDoc
    {
        public int SchemaVersion { get; init; } = 3;

        public string Name { get; init; } = "";
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
    [JsonSerializable(typeof(TestDoc))]
    internal sealed partial class TestJson : JsonSerializerContext
    {
    }

    private sealed class Rename : ISettingsMigration
    {
        public string FileKind => "test";

        public int FromVersion => 1;

        public JsonNode Apply(JsonNode document)
        {
            var o = document.AsObject();
            o["name"] = o["title"]?.DeepClone();
            o.Remove("title");
            return o;
        }
    }

    private sealed class Bump : ISettingsMigration
    {
        public string FileKind => "test";

        public int FromVersion => 2;

        public JsonNode Apply(JsonNode document) => document;
    }
}
