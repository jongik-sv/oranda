using Oranda.Core.Rollback;
using Oranda.Core.Settings;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Rollback;

// 소유: RB-21(조정자 허가로 SettingsStore 두 줄). 「rollback」 섹션 정규화와 변경 알림(설계 10.4).
public sealed class RollbackSettingsSectionTests
{
    [Fact]
    public void Normalize_clamps_rollback_section_and_change_is_reported()
    {
        var before = new AppSettings();
        var after = SettingsStore.Normalize(before with { Rollback = new RollbackSettings { RetentionDays = 9999, MaxRowsPerStatement = 0 } });

        Assert.Equal(RollbackSettings.MaxRetentionDays, after.Rollback.RetentionDays);
        Assert.Equal(RollbackSettings.MinMaxRows, after.Rollback.MaxRowsPerStatement);
        Assert.Contains(RollbackSettings.SectionName, SettingsStore.ChangedSections(before, after));
        Assert.DoesNotContain(RollbackSettings.SectionName, SettingsStore.ChangedSections(before, SettingsStore.Normalize(before)));
    }
}
