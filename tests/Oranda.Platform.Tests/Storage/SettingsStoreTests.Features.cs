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
    public async Task Old_settings_file_without_features_loads_as_all_features_and_keeps_its_version()
    {
        await File.WriteAllTextAsync(Paths.SettingsFile, """{ "schemaVersion": 1, "ui": { "theme": "Dark" } }""");

        var doc = await NewStore().LoadAsync(default);

        Assert.False(doc.IsReadOnly);
        Assert.Equal(Theme.Dark, doc.Value.Ui.Theme);
        Assert.Equal(new FeaturesSettings(), doc.Value.Features);
        Assert.Equal(FeatureIds.All.Count, FeatureCatalog.Resolve(doc.Value.Features).Count);
        Assert.Equal(SchemaVersions.Settings, doc.Value.SchemaVersion);
        Assert.Equal(1, SchemaVersions.Settings); // features 는 새 섹션이라 버전을 올리지 않는다
    }
}
