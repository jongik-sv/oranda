using System.Text.Json;
using Oranda.Core.Features;
using Oranda.Core.Serialization;
using Oranda.Core.Settings;
using Oranda.Perf.Common;

namespace Oranda.Perf.Tests;

/// <summary>TSK-07-07: Perf 도구 --features 선택지 해석, 데이터 폴더 settings.json features 쓰기, Trace 로그 개수 읽기.</summary>
public class FeaturePresetTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "oranda-perf-fp-" + Guid.NewGuid().ToString("N"));

    // ---- 앱 카탈로그와 같은 표 ----

    [Fact]
    public void Id_tables_match_the_app_catalog()
    {
        Assert.Equal(FeatureIds.All.Order(StringComparer.Ordinal), FeaturePreset.FeatureIdList.Order(StringComparer.Ordinal));
        Assert.Equal(FeatureCatalog.Roles.Select(r => r.Id).Order(StringComparer.Ordinal), FeaturePreset.RoleIdList.Order(StringComparer.Ordinal));
    }

    // ---- 해석 ----

    [Fact]
    public void All_means_no_features_section()
    {
        var p = FeaturePreset.Parse("all");
        Assert.Null(p.Roles);
        Assert.True(p.IsDefault);
        Assert.Null(p.ToSettingsJson());
        Assert.True(FeaturePreset.Parse("ALL").IsDefault);
    }

    [Theory]
    [InlineData("sqlDev", new[] { "sqlDev" })]
    [InlineData("R1", new[] { "sqlDev" })]
    [InlineData("myBatisDev", new[] { "myBatisDev" })]
    [InlineData("tuner,dba", new[] { "tuner", "dba" })]
    [InlineData("r4, r5", new[] { "tuner", "dba" })]
    [InlineData("viewer", new[] { "viewer" })]
    [InlineData("VIEWER,viewer", new[] { "viewer" })]
    public void Role_lists_are_parsed_with_aliases_and_dedupe(string text, string[] expected)
    {
        var p = FeaturePreset.Parse(text);
        Assert.Equal(expected, p.Roles);
        Assert.False(p.IsDefault);
    }

    [Fact]
    public void None_is_an_empty_role_list_not_the_default()
    {
        var p = FeaturePreset.Parse("none");
        Assert.NotNull(p.Roles);
        Assert.Empty(p.Roles!);
        Assert.False(p.IsDefault);
    }

    [Fact]
    public void Plus_and_minus_add_direct_overrides()
    {
        var p = FeaturePreset.Parse("sqlDev,-blockerAlert,+TRACE");
        Assert.Equal(["sqlDev"], p.Roles);
        Assert.Equal(["trace"], p.Enabled);
        Assert.Equal(["blockerAlert"], p.Disabled);
        var all = FeaturePreset.Parse("all,-blockerAlert");
        Assert.Null(all.Roles);
        Assert.False(all.IsDefault); // 직접 끈 기능이 있으면 섹션을 쓴다
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nope")]
    [InlineData("sqlDev,nope")]
    [InlineData("all,sqlDev")]
    [InlineData("all,none")]
    [InlineData("none,viewer")]
    [InlineData("+trace")]
    [InlineData("-blockerAlert")]
    [InlineData("sqlDev,+notAFeature")]
    [InlineData("sqlDev,-")]
    [InlineData("r7")]
    [InlineData("r0")]
    public void Bad_values_are_rejected(string? text)
    {
        Assert.Throws<ArgumentException>(() => FeaturePreset.Parse(text));
    }

    // ---- settings.json 쓰기 ----

    [Fact]
    public void Written_settings_are_read_back_by_the_app_model()
    {
        var dir = TempDir();
        try
        {
            FeaturePreset.Parse("tuner,dba,-blockerAlert").WriteSettings(dir);
            var json = File.ReadAllText(Path.Combine(dir, "settings.json"));
            var settings = JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!;
            Assert.Equal(SchemaVersions.Settings, settings.SchemaVersion);
            Assert.Equal(["tuner", "dba"], settings.Features.Roles);
            Assert.Equal(["blockerAlert"], settings.Features.Disabled);
            Assert.Empty(settings.Features.Enabled);
            var on = FeatureCatalog.Resolve(settings.Features);
            Assert.DoesNotContain(FeatureIds.BlockerAlert, on);
            Assert.Contains(FeatureIds.SqlMonitor, on);
            Assert.DoesNotContain(FeatureIds.MyBatis, on);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("sqlDev")]
    [InlineData("myBatisDev")]
    [InlineData("tuner,dba")]
    [InlineData("viewer")]
    [InlineData("none")]
    public void Every_preset_resolves_in_the_app_to_its_role_defaults(string text)
    {
        var dir = TempDir();
        try
        {
            var preset = FeaturePreset.Parse(text);
            preset.WriteSettings(dir);
            var settings = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(dir, "settings.json")), OrandaJsonContext.Default.AppSettings)!;
            var on = FeatureCatalog.Resolve(settings.Features);
            var expected = FeatureCatalog.Resolve(new FeaturesSettings { Roles = preset.Roles });
            Assert.True(expected.SetEquals(on));
            Assert.Equal(preset.Roles is { Count: 1 } r && r[0] == "viewer", FeatureCatalog.IsReadOnlyRole(settings.Features.Roles));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void All_with_null_roles_and_a_disable_turns_off_only_that_feature()
    {
        var dir = TempDir();
        try
        {
            FeaturePreset.Parse("all,-blockerAlert").WriteSettings(dir);
            var settings = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(dir, "settings.json")), OrandaJsonContext.Default.AppSettings)!;
            Assert.Null(settings.Features.Roles);
            var on = FeatureCatalog.Resolve(settings.Features);
            Assert.Equal(FeatureIds.All.Count - 1, on.Count);
            Assert.DoesNotContain(FeatureIds.BlockerAlert, on);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Default_preset_removes_a_leftover_settings_file_and_creates_the_folder()
    {
        var dir = TempDir();
        try
        {
            FeaturePreset.Parse("viewer").WriteSettings(dir);
            Assert.True(File.Exists(Path.Combine(dir, "settings.json")));
            FeaturePreset.Parse("all").WriteSettings(dir);
            Assert.False(File.Exists(Path.Combine(dir, "settings.json")));
            var fresh = Path.Combine(dir, "new", "sub");
            FeaturePreset.Parse("all").WriteSettings(fresh);
            Assert.True(Directory.Exists(fresh));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Writing_twice_replaces_the_previous_configuration()
    {
        var dir = TempDir();
        try
        {
            FeaturePreset.Parse("viewer").WriteSettings(dir);
            FeaturePreset.Parse("sqlDev").WriteSettings(dir);
            var settings = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(dir, "settings.json")), OrandaJsonContext.Default.AppSettings)!;
            Assert.Equal(["sqlDev"], settings.Features.Roles);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Write_rejects_an_empty_folder_name()
    {
        Assert.Throws<ArgumentException>(() => FeaturePreset.Parse("all").WriteSettings(""));
    }

    // ---- 명령줄 옵션 ----

    [Fact]
    public void Startup_options_accept_features_and_reject_bad_ones()
    {
        Assert.Equal("tuner,dba", StartupOptions.Parse(["--features", "tuner,dba"]).Features);
        Assert.Null(StartupOptions.Parse([]).Features);
        Assert.Throws<ArgumentException>(() => StartupOptions.Parse(["--features", "nope"]));
        Assert.Throws<ArgumentException>(() => StartupOptions.Parse(["--features"]));
    }

    [Fact]
    public void Input_grace_option_is_parsed()
    {
        Assert.Equal(1, StartupOptions.Parse([]).InputGraceSeconds);
        Assert.Equal(20, StartupOptions.Parse(["--input-grace-s", "20"]).InputGraceSeconds);
        Assert.Equal(0, StartupOptions.Parse(["--input-grace-s", "0"]).InputGraceSeconds);
        Assert.Throws<ArgumentException>(() => StartupOptions.Parse(["--input-grace-s", "-1"]));
    }

    [Fact]
    public void Ui_options_accept_features_and_the_new_scenarios()
    {
        var o = UiOptions.Parse(["roles", "--features", "viewer", "--seconds", "30"]);
        Assert.Equal("viewer", o.Features);
        Assert.Equal(30, o.Seconds);
        Assert.False(o.NeedsDisplay);
        Assert.False(UiOptions.Parse(["sessions"]).NeedsDisplay);
        Assert.Throws<ArgumentException>(() => UiOptions.Parse(["roles", "--features", "r9"]));
    }

    // ---- Trace 로그 ----

    [Fact]
    public void Log_counts_are_read_from_the_trace_lines()
    {
        const string log = """
            Oranda.App Information: 0 : 시작 작업 건너뜀: Oranda.App.Features.MyBatis.MapperWatchStartup
                DateTime=2026-10-25T05:00:00.0000000Z
            Oranda.App Information: 0 : 상태 칸 건너뜀: Oranda.App.Features.BlockerAlert.BlockerStatusItem
            Oranda.App Information: 0 : 상태 칸: 5개, 건너뜀 2개
            Oranda.App Information: 0 : 시작 작업: 돈 3개, 건너뜀 6개
            """;
        var c = LogCounts.Parse(log);
        Assert.Equal(3, c.TasksRan);
        Assert.Equal(6, c.TasksSkipped);
        Assert.Equal(5, c.StatusShown);
        Assert.Equal(1, c.StatusSkipped);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("아무 줄")]
    [InlineData("시작 작업: 돈 abc개, 건너뜀 1개")]
    [InlineData("시작 작업: 돈 3개")]
    public void Log_counts_ignore_missing_or_broken_lines(string? text)
    {
        var c = LogCounts.Parse(text);
        Assert.Null(c.TasksRan);
        Assert.Null(c.TasksSkipped);
        Assert.Null(c.StatusShown);
        Assert.Equal(0, c.StatusSkipped);
    }

    [Fact]
    public void The_last_summary_line_wins()
    {
        var c = LogCounts.Parse("시작 작업: 돈 1개, 건너뜀 2개\n시작 작업: 돈 4개, 건너뜀 5개\n");
        Assert.Equal(4, c.TasksRan);
        Assert.Equal(5, c.TasksSkipped);
    }
}
