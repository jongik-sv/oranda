using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Rollback;
using Oranda.Testing.Rollback;

namespace Oranda.App.Tests.Shell.Settings;

/// <summary>설정 창 「되돌리기」 탭(되돌리기 지점 설계 10.4): 저장 왕복과 범위, 안전한 저장소 없음, 보관 요약·줄인 보관 기간 안내·모두 지우기.</summary>
public class RollbackSettingsPageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public Task Tab_values_are_saved_to_the_rollback_section_clamped_and_read_back() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var settings = h.Get<SettingsService>();
        var text = h.Get<IShellText>();
        var original = settings.Current;
        try
        {
            var vm = new SettingsViewModel(settings, text, rollback: new RollbackSettingsPage(settings.Current.Rollback, text, null, null));
            Assert.True(vm.Rollback.RecordPoint);
            Assert.Equal(RollbackSettings.DefaultMaxRows, vm.Rollback.MaxRowsPerStatement);
            Assert.Equal(5, SettingsViewModel.RollbackTabIndex);

            vm.Rollback.RecordPoint = false;
            vm.Rollback.WarnRowCountMismatch = false;
            vm.Rollback.MaxRowsPerStatement = 5_000_000; // 범위 밖은 상한으로
            vm.Rollback.RetentionDays = 7;
            await vm.SaveAsync();

            var saved = settings.Current.Rollback;
            Assert.False(saved.RecordPoint);
            Assert.False(saved.WarnRowCountMismatch);
            Assert.True(saved.ScriptOnProduction);
            Assert.Equal(RollbackSettings.MaxMaxRows, saved.MaxRowsPerStatement);
            Assert.Equal(7, saved.RetentionDays);
            Assert.Equal(7, new RollbackSettingsPage(saved, text, null, null).RetentionDays);
        }
        finally
        {
            await settings.UpdateAsync(_ => original);
        }
    });

    [Fact]
    public Task Without_a_secure_store_the_script_option_is_dimmed_and_explained() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var text = h.Get<IShellText>();
        var none = new RollbackSettingsPage(new RollbackSettings(), text, null, null);
        Assert.False(none.CanStoreScripts);
        Assert.True(none.NoSecureStore);

        var volatileStore = new RollbackSettingsPage(new RollbackSettings(), text, new InMemoryRollbackStore { CanStoreScripts = false }, null);
        Assert.True(volatileStore.NoSecureStore);
        Assert.True(new RollbackSettingsPage(new RollbackSettings(), text, new InMemoryRollbackStore(), null).CanStoreScripts);
        Assert.False(string.IsNullOrWhiteSpace(text.Lookup("Shell.settings.rollback.noSecureStore")));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Summary_counts_groups_warns_when_retention_shrinks_and_clear_all_deletes_them() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var text = h.Get<IShellText>();
        var store = new InMemoryRollbackStore();
        await store.SaveAsync(Group(Now.AddDays(-20)), null, default);
        await store.SaveAsync(Group(Now.AddDays(-2)), null, default);
        var page = new RollbackSettingsPage(new RollbackSettings(), text, store, null, () => Now);

        await page.RefreshSummaryAsync();
        Assert.Contains("2", page.StorageSummary, StringComparison.Ordinal);
        Assert.False(page.HasRetentionNotice); // 30일 그대로

        page.RetentionDays = 10;
        Assert.True(page.HasRetentionNotice);
        Assert.Contains("1", page.RetentionNotice, StringComparison.Ordinal); // 20일 전 묶음 하나

        Assert.True(page.AskClearCommand.CanExecute(null));
        page.AskClearCommand.Execute(null);
        Assert.True(page.IsConfirmingClear); // 바로 지우지 않고 묻는다
        await page.ConfirmClearCommand.ExecuteAsync(null);
        Assert.False(page.IsConfirmingClear);
        Assert.Empty(store.Groups);
        Assert.False(page.HasStoredGroups);
    });

    private static RollbackGroup Group(DateTimeOffset ended) => new()
    {
        ProfileId = Guid.NewGuid(),
        ProfileName = "prod-example",
        DbKey = "prod-example",
        StartedAt = ended.AddMinutes(-1),
        EndedAt = ended,
        State = RollbackScriptState.Confirmed,
    };
}
