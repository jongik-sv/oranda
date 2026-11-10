using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.Update;
using Oranda.Core.Settings;
using Oranda.Core.Transfer.Copy.ExternalLoad;

namespace Oranda.App.Tests.Shell.Settings;

/// <summary>설정 「드라이버」 탭의 「외부 적재 도구」(XL1·XL3): SQL*Loader·tbLoader 경로 칸·자동 찾기 워터마크·[도구 확인]·찾아보기·저장.</summary>
[Collection("UiLanguage")]
public sealed class ExternalToolSettingsTests
{
    private sealed class FakeProbe : IExternalToolProbe
    {
        public Func<ExternalLoaderKind, string?, ExternalToolLocation> OnLocate { get; set; } =
            (kind, _) => new ExternalToolLocation(kind, null, false, ["PATH"]);

        public Func<ExternalLoaderKind, string?, ExternalToolInfo> OnCheck { get; set; } =
            (kind, path) => new ExternalToolInfo(kind, null, null, null, "도구를 찾지 못했습니다.");

        public List<(ExternalLoaderKind Kind, string? Path)> Checked { get; } = [];

        public ExternalToolLocation Locate(ExternalLoaderKind kind, string? configuredPath) => OnLocate(kind, configuredPath);

        public Task<ExternalToolInfo> CheckAsync(ExternalLoaderKind kind, string? configuredPath, CancellationToken ct)
        {
            Checked.Add((kind, configuredPath));
            return Task.FromResult(OnCheck(kind, configuredPath));
        }
    }

    private static async Task<(SettingsService Settings, MemorySettingsStore Store)> Create(DriverSettings? drivers = null)
    {
        var store = new MemorySettingsStore();
        var settings = new SettingsService(store);
        if (drivers is not null)
        {
            await settings.UpdateAsync(s => s with { Drivers = drivers });
        }

        return (settings, store);
    }

    private static DriverSettingsPage Page(SettingsService settings, IExternalToolProbe? probe, bool tiberoIncluded = true, string language = "ko") =>
        new(settings, new ShellText(language: language), status: null, tiberoIncluded, probe);

    [Fact]
    public async Task Fields_open_with_the_saved_paths_and_auto_find_shows_the_found_tool_as_watermark()
    {
        var (settings, _) = await Create(new DriverSettings { TbLoaderPath = @"D:\tibero\client\bin" });
        var probe = new FakeProbe
        {
            OnLocate = (kind, _) => kind == ExternalLoaderKind.SqlLoader
                ? new ExternalToolLocation(kind, @"C:\oracle\instantclient_23_5\sqlldr.exe", false, ["PATH"])
                : new ExternalToolLocation(kind, null, false, ["PATH", "TB_HOME"]),
        };

        var page = Page(settings, probe);

        Assert.Equal(string.Empty, page.SqlLoader.Path);
        Assert.Equal(@"D:\tibero\client\bin", page.TbLoader.Path);
        Assert.Equal(@"비워 두면 자동으로 찾기 · 찾은 위치: C:\oracle\instantclient_23_5\sqlldr.exe", page.SqlLoader.Placeholder);
        Assert.Equal("비워 두면 자동으로 찾기 · 지금은 찾지 못함", page.TbLoader.Placeholder);
        Assert.False(page.IsDirty);
    }

    [Fact]
    public async Task Check_shows_the_found_path_and_version_line()
    {
        var (settings, _) = await Create();
        var probe = new FakeProbe
        {
            OnCheck = (kind, _) => new ExternalToolInfo(kind, @"C:\ic\sqlldr.exe", 19, "SQL*Loader: Release 19.0.0.0.0 - Production", null),
        };
        var page = Page(settings, probe);
        page.SqlLoader.Path = @"  C:\ic  ";

        await page.SqlLoader.CheckCommand.ExecuteAsync(null);

        Assert.Equal((ExternalLoaderKind.SqlLoader, @"C:\ic"), Assert.Single(probe.Checked));
        Assert.Equal(@"사용할 수 있음 — C:\ic\sqlldr.exe", page.SqlLoader.StatusText);
        Assert.Equal("SQL*Loader: Release 19.0.0.0.0 - Production", page.SqlLoader.StatusDetail);
        Assert.False(page.SqlLoader.StatusIsError);
    }

    [Fact]
    public async Task Check_with_an_empty_field_passes_null_and_shows_the_problem_and_where_it_looked()
    {
        var (settings, _) = await Create();
        var probe = new FakeProbe
        {
            OnLocate = (kind, _) => new ExternalToolLocation(kind, null, false, ["PATH", @"TB_HOME\client\bin"]),
            OnCheck = (kind, _) => new ExternalToolInfo(kind, null, null, null, "tbloader 를 찾지 못했습니다."),
        };
        var page = Page(settings, probe);

        await page.TbLoader.CheckCommand.ExecuteAsync(null);

        Assert.Equal((ExternalLoaderKind.TbLoader, (string?)null), Assert.Single(probe.Checked));
        Assert.Equal("tbloader 를 찾지 못했습니다.", page.TbLoader.StatusText);
        Assert.Equal(@"살펴본 곳: PATH, TB_HOME\client\bin", page.TbLoader.StatusDetail);
        Assert.True(page.TbLoader.StatusIsError);
    }

    [Fact]
    public async Task A_probe_exception_is_shown_and_does_not_escape()
    {
        var (settings, _) = await Create();
        var probe = new FakeProbe { OnCheck = (_, _) => throw new InvalidOperationException("boom") };
        var page = Page(settings, probe);

        await page.SqlLoader.CheckCommand.ExecuteAsync(null);

        Assert.Equal("도구를 확인하지 못했습니다: boom", page.SqlLoader.StatusText);
        Assert.True(page.SqlLoader.StatusIsError);
    }

    [Fact]
    public async Task Without_a_probe_the_check_button_is_disabled_and_says_why()
    {
        var (settings, _) = await Create();
        var page = Page(settings, probe: null);

        Assert.False(page.SqlLoader.CheckCommand.CanExecute(null));
        Assert.Equal("도구를 확인하는 서비스가 아직 준비되지 않았습니다.", page.SqlLoader.StatusText);
        Assert.Equal("비워 두면 자동으로 찾기", page.SqlLoader.Placeholder);
    }

    [Fact]
    public async Task Browse_fills_the_field_and_checks_the_picked_tool()
    {
        var (settings, _) = await Create();
        var probe = new FakeProbe { OnCheck = (kind, path) => new ExternalToolInfo(kind, path, 6, "tbLoader 6", null) };
        var page = Page(settings, probe);
        page.TbLoader.PickFile = () => Task.FromResult<string?>(@"C:\tibero\client\bin\tbloader.exe");

        await page.TbLoader.BrowseCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\tibero\client\bin\tbloader.exe", page.TbLoader.Path);
        Assert.Equal((ExternalLoaderKind.TbLoader, @"C:\tibero\client\bin\tbloader.exe"), Assert.Single(probe.Checked));
        Assert.True(page.IsDirty);
    }

    [Fact]
    public async Task Cancelling_the_browse_dialog_changes_nothing()
    {
        var (settings, _) = await Create();
        var probe = new FakeProbe();
        var page = Page(settings, probe);
        page.SqlLoader.PickFile = () => Task.FromResult<string?>(null);

        await page.SqlLoader.BrowseCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, page.SqlLoader.Path);
        Assert.Empty(probe.Checked);
    }

    [Fact]
    public async Task Settings_save_writes_both_tool_paths_trimmed_with_the_page()
    {
        var (settings, store) = await Create(new DriverSettings { TiberoPath = @"C:\tdp" });
        var vm = new SettingsViewModel(settings, new ShellText(language: "ko"), drivers: Page(settings, new FakeProbe()));
        vm.Drivers.SqlLoader.Path = @"  C:\ic\sqlldr.exe ";
        vm.Drivers.TbLoader.Path = @"D:\tb\bin";
        Assert.True(vm.Drivers.IsDirty);

        await vm.SaveAsync();

        Assert.Equal(@"C:\ic\sqlldr.exe", store.Saved.Drivers.SqlLoaderPath);
        Assert.Equal(@"D:\tb\bin", store.Saved.Drivers.TbLoaderPath);
        Assert.Equal(@"C:\tdp", store.Saved.Drivers.TiberoPath);
        Assert.False(vm.Drivers.IsDirty);
        Assert.Equal(new ExternalToolPaths(@"C:\ic\sqlldr.exe", @"D:\tb\bin"), store.Saved.Drivers.ToExternalToolPaths());
    }

    [Fact]
    public async Task Emptying_a_field_saves_null_for_auto_find()
    {
        var (settings, store) = await Create(new DriverSettings { SqlLoaderPath = @"C:\ic" });
        var vm = new SettingsViewModel(settings, new ShellText(language: "ko"), drivers: Page(settings, new FakeProbe()));
        vm.Drivers.SqlLoader.Path = "   ";

        await vm.SaveAsync();

        Assert.Null(store.Saved.Drivers.SqlLoaderPath);
    }

    [Fact]
    public async Task Settings_save_keeps_tool_paths_changed_elsewhere_when_the_fields_were_not_touched()
    {
        var (settings, store) = await Create(new DriverSettings { SqlLoaderPath = @"C:\a" });
        var vm = new SettingsViewModel(settings, new ShellText(language: "ko"), drivers: Page(settings, new FakeProbe()));
        await settings.UpdateAsync(s => s with { Drivers = s.Drivers with { SqlLoaderPath = @"E:\other", TbLoaderPath = @"E:\tb" } });

        await vm.SaveAsync();

        Assert.Equal(@"E:\other", store.Saved.Drivers.SqlLoaderPath);
        Assert.Equal(@"E:\tb", store.Saved.Drivers.TbLoaderPath);
    }

    [Fact]
    public void Tool_paths_round_trip_under_the_drivers_keys_and_old_files_read_as_auto_find()
    {
        var settings = new AppSettings { Drivers = new DriverSettings { SqlLoaderPath = @"C:\ic", TbLoaderPath = @"D:\tb" } };

        var json = System.Text.Json.JsonSerializer.Serialize(settings, Oranda.Core.Serialization.OrandaJsonContext.Default.AppSettings);
        var loaded = System.Text.Json.JsonSerializer.Deserialize(json, Oranda.Core.Serialization.OrandaJsonContext.Default.AppSettings)!;
        var old = System.Text.Json.JsonSerializer.Deserialize("""{ "schemaVersion": 1, "drivers": { "tiberoPath": "x" } }""", Oranda.Core.Serialization.OrandaJsonContext.Default.AppSettings)!;

        Assert.Contains("\"sqlLoaderPath\"", json, StringComparison.Ordinal);
        Assert.Contains("\"tbLoaderPath\"", json, StringComparison.Ordinal);
        Assert.Equal(@"C:\ic", loaded.Drivers.SqlLoaderPath);
        Assert.Equal(@"D:\tb", loaded.Drivers.TbLoaderPath);
        Assert.Equal(ExternalToolPaths.None, old.Drivers.ToExternalToolPaths());
    }

    [Fact]
    public async Task Tibero_free_builds_show_only_sql_loader()
    {
        var (settings, _) = await Create();

        Assert.Equal([ExternalLoaderKind.SqlLoader, ExternalLoaderKind.TbLoader], Page(settings, new FakeProbe()).ExternalTools.Select(t => t.Kind));
        Assert.Equal([ExternalLoaderKind.SqlLoader], Page(settings, new FakeProbe(), tiberoIncluded: false).ExternalTools.Select(t => t.Kind));
    }

    [Fact]
    public async Task English_screen_uses_the_literal_table_for_every_tool_text()
    {
        var (settings, _) = await Create();
        var page = Page(settings, new FakeProbe(), language: "en");

        Assert.Equal("External bulk-load tools", page.ExternalTitle);
        Assert.DoesNotMatch("[\uAC00-\uD7A3]", page.ExternalNotice);
        foreach (var tool in page.ExternalTools)
        {
            foreach (var text in new[] { tool.Title, tool.PathName, tool.BrowseText, tool.CheckText, tool.GuideHeader, tool.FileDialogTitle, tool.FileTypeName, tool.Placeholder }.Concat(tool.GuideSteps))
            {
                Assert.DoesNotMatch("[\uAC00-\uD7A3]", text);
            }
        }
    }

    [Fact]
    public void Every_korean_tool_text_has_an_english_entry()
    {
        string[] fixedTexts =
        [
            ExternalToolEntry.AutoPlain, ExternalToolEntry.AutoFound, ExternalToolEntry.AutoNotFound, ExternalToolEntry.NoProbe,
            ExternalToolEntry.Checking, ExternalToolEntry.Usable, ExternalToolEntry.NotFound, ExternalToolEntry.SearchedPlaces,
            ExternalToolEntry.CheckFailed, DriverSettingsPage.ExternalNoticeKo,
        ];

        foreach (var korean in fixedTexts.Concat(ExternalToolEntry.SqlLoaderGuide).Concat(ExternalToolEntry.TbLoaderGuide))
        {
            Assert.True(LiteralTranslator.Table.ContainsKey(korean), "literals-en.json 에 없음: " + korean);
        }
    }

    [Fact]
    public void Install_guide_names_the_packages_and_where_the_tools_live()
    {
        var sql = string.Join("\n", ExternalToolEntry.SqlLoaderGuide);
        var tb = string.Join("\n", ExternalToolEntry.TbLoaderGuide);

        Assert.Contains("Basic", sql, StringComparison.Ordinal);
        Assert.Contains("Tools", sql, StringComparison.Ordinal);
        Assert.Contains("Visual C++", sql, StringComparison.Ordinal);
        Assert.Contains("TNS_ADMIN", sql, StringComparison.Ordinal);
        Assert.Contains(@"TB_HOME\client\bin\tbloader.exe", tb, StringComparison.Ordinal);
    }
}
