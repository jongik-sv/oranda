using Oranda.Core.Events;
using Oranda.Core.Features;
using Oranda.Core.Settings;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Storage;

// 소유: TSK-07-01. settings.json 의 features 섹션(TRD 10.3): 정규화·바뀜 알림·파일 왕복·옛 파일.
public sealed partial class SettingsStoreTests
{
    [Fact]
    public void Normalize_removes_blanks_and_duplicates_and_disabled_wins_over_enabled()
    {
        var f = SettingsStore.NormalizeFeatures(new FeaturesSettings
        {
            Roles = ["sqlDev", "sqlDev", " tuner ", ""],
            Enabled = ["trace", "flashback", "trace", " "],
            Disabled = ["flashback", "myBatis", "myBatis"],
        });

        Assert.Equal(["sqlDev", "tuner"], f.Roles);
        Assert.Equal(["trace"], f.Enabled);
        Assert.Equal(["flashback", "myBatis"], f.Disabled);
    }

    [Fact]
    public void Normalize_keeps_unknown_ids_and_null_versus_empty_roles()
    {
        var f = SettingsStore.NormalizeFeatures(new FeaturesSettings { Roles = ["nextRole"], Enabled = ["aiChat"], Disabled = ["oldThing"] });
        Assert.Equal(["nextRole"], f.Roles);
        Assert.Equal(["aiChat"], f.Enabled);
        Assert.Equal(["oldThing"], f.Disabled);

        Assert.Null(SettingsStore.NormalizeFeatures(new FeaturesSettings()).Roles);
        Assert.Empty(SettingsStore.NormalizeFeatures(new FeaturesSettings { Roles = [] }).Roles!);
        Assert.Equal(new FeaturesSettings(), SettingsStore.Normalize(new AppSettings()).Features);
    }

    [Fact]
    public void Normalize_keeps_the_all_features_flag_and_drops_roles_written_beside_it()
    {
        var f = SettingsStore.NormalizeFeatures(new FeaturesSettings { AllFeatures = true, Roles = ["viewer"], Disabled = ["trace", "trace"] });
        Assert.True(f.AllFeatures);
        Assert.Null(f.Roles);
        Assert.Equal(["trace"], f.Disabled);

        // 옛 「모든 기능 쓰기」 + 직접 끔(roles 없음)은 정규화가 바꾸지 않는다(해석이 모든 기능 바탕으로 읽는다).
        var legacy = SettingsStore.NormalizeFeatures(new FeaturesSettings { Disabled = ["trace"] });
        Assert.False(legacy.AllFeatures);
        Assert.Null(legacy.Roles);
        Assert.Equal(FeatureIds.All.Count - 1, FeatureCatalog.Resolve(legacy).Count);
    }

    [Fact]
    public void Changed_sections_reports_features_when_only_the_all_features_flag_changes()
    {
        Assert.Equal(["features"], SettingsStore.ChangedSections(new AppSettings(), new AppSettings { Features = new FeaturesSettings { AllFeatures = true } }));
        Assert.Empty(SettingsStore.ChangedSections(
            new AppSettings { Features = new FeaturesSettings { AllFeatures = true } },
            new AppSettings { Features = new FeaturesSettings { AllFeatures = true } }));
    }

    [Fact]
    public async Task All_features_flag_round_trips_through_the_file_and_switching_back_to_a_role_clears_it()
    {
        var store = NewStore();
        await store.LoadAsync(default);

        await store.SaveAsync(new AppSettings { Features = new FeaturesSettings { AllFeatures = true, Disabled = [FeatureIds.Trace] } }, default);
        var text = await File.ReadAllTextAsync(Paths.SettingsFile);
        Assert.Contains("\"allFeatures\": true", text, StringComparison.Ordinal);
        var loaded = (await NewStore().LoadAsync(default)).Value.Features;
        Assert.True(loaded.AllFeatures);
        Assert.Equal([FeatureIds.Trace], loaded.Disabled);
        Assert.Equal(FeatureIds.All.Count - 1, FeatureCatalog.Resolve(loaded).Count);

        await store.SaveAsync(new AppSettings { Features = new FeaturesSettings { Roles = [RoleIds.Tuner] } }, default);
        var back = (await NewStore().LoadAsync(default)).Value.Features;
        Assert.False(back.AllFeatures);
        Assert.Equal([RoleIds.Tuner], back.Roles);
        Assert.Equal(["features", "features"], _bus.PublishedOf<SettingsChanged>().Select(e => e.Section));
    }

    [Fact]
    public async Task A_file_saved_before_the_flag_existed_reads_the_same_as_before()
    {
        // D-013 전 형식: roles 를 고른 사용자는 그대로, 「모든 기능」 + 직접 끔도 그대로, 아무것도 없으면 기본 Role.
        await File.WriteAllTextAsync(Paths.SettingsFile, """{ "schemaVersion": 1, "features": { "roles": ["dba"], "enabled": [], "disabled": [] } }""");
        Assert.Equal(FeatureCatalog.RoleDefaults([RoleIds.Dba]).Count, FeatureCatalog.Resolve((await NewStore().LoadAsync(default)).Value.Features).Count);

        await File.WriteAllTextAsync(Paths.SettingsFile, """{ "schemaVersion": 1, "features": { "enabled": [], "disabled": ["myBatis"] } }""");
        Assert.Equal(FeatureIds.All.Count - 1, FeatureCatalog.Resolve((await NewStore().LoadAsync(default)).Value.Features).Count);

        await File.WriteAllTextAsync(Paths.SettingsFile, """{ "schemaVersion": 1, "features": { "enabled": [], "disabled": [] } }""");
        Assert.True(FeatureCatalog.IsUnchosen((await NewStore().LoadAsync(default)).Value.Features));
    }

    [Fact]
    public void Changed_sections_reports_features_only_when_the_content_changes()
    {
        var before = new AppSettings { Features = new FeaturesSettings { Roles = ["sqlDev", "tuner"] } };

        Assert.Empty(SettingsStore.ChangedSections(before, before with { Features = new FeaturesSettings { Roles = ["tuner", "sqlDev"] } }));
        Assert.Equal(["features"], SettingsStore.ChangedSections(before, before with { Features = new FeaturesSettings { Roles = ["sqlDev"] } }));
        Assert.Equal(["features"], SettingsStore.ChangedSections(before, new AppSettings()));
        Assert.Equal(["features"], SettingsStore.ChangedSections(new AppSettings(), new AppSettings { Features = new FeaturesSettings { Roles = [] } }));
    }

    [Fact]
    public async Task Features_round_trip_through_the_file_and_publish_their_own_section()
    {
        var store = NewStore();
        await store.LoadAsync(default);
        var features = new FeaturesSettings { Roles = [RoleIds.SqlDev, RoleIds.Tuner], Enabled = [FeatureIds.Trace], Disabled = [FeatureIds.Flashback] };

        await store.SaveAsync(new AppSettings { Features = features }, default);

        Assert.Equal(["features"], _bus.PublishedOf<SettingsChanged>().Select(e => e.Section));
        var text = await File.ReadAllTextAsync(Paths.SettingsFile);
        Assert.Contains("\"features\": {", text, StringComparison.Ordinal);
        Assert.Contains("\"roles\": [", text, StringComparison.Ordinal);
        Assert.Equal(features, (await NewStore().LoadAsync(default)).Value.Features);

        // 같은 내용(순서만 다름)은 다시 알리지 않는다.
        await store.SaveAsync(new AppSettings { Features = features with { Roles = [RoleIds.Tuner, RoleIds.SqlDev] } }, default);
        Assert.Single(_bus.PublishedOf<SettingsChanged>());
    }

    [Fact]
    public async Task Saving_overlapping_lists_writes_the_normalized_section()
    {
        var store = NewStore();
        await store.SaveAsync(new AppSettings { Features = new FeaturesSettings { Roles = [RoleIds.SqlDev], Enabled = ["trace", "flashback"], Disabled = ["flashback"] } }, default);

        var loaded = (await NewStore().LoadAsync(default)).Value.Features;
        Assert.Equal(["trace"], loaded.Enabled);
        Assert.Equal(["flashback"], loaded.Disabled);
    }

    [Fact]
    public async Task Old_settings_file_without_features_loads_as_the_default_role_and_keeps_its_version()
    {
        await File.WriteAllTextAsync(Paths.SettingsFile, """{ "schemaVersion": 1, "ui": { "theme": "Dark" } }""");

        var doc = await NewStore().LoadAsync(default);

        Assert.False(doc.IsReadOnly);
        Assert.Equal(Theme.Dark, doc.Value.Ui.Theme);
        Assert.Equal(new FeaturesSettings(), doc.Value.Features);
        Assert.True(FeatureCatalog.IsUnchosen(doc.Value.Features)); // D-013: 기본 Role = SQL 개발자
        Assert.Equal(FeatureCatalog.RoleDefaults([RoleIds.SqlDev]).Count, FeatureCatalog.Resolve(doc.Value.Features).Count);
        Assert.Equal(SchemaVersions.Settings, doc.Value.SchemaVersion);
        Assert.Equal(1, SchemaVersions.Settings); // features 는 새 섹션이라 버전을 올리지 않는다
    }
}
