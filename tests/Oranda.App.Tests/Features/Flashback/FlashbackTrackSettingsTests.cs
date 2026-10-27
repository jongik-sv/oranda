using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.Update;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Flashback;

/// <summary>TSK-13-44: 설정 창 「결과 그리드」 탭의 「변경 추적하기」 켜기와 「변경 추적 기간」(최근 N일 / 가능한 전체 기간).</summary>
[Collection("UiLanguage")]
public sealed class FlashbackTrackSettingsTests
{
    private static (MemorySettingsStore Store, SettingsViewModel Vm) Create(GridSettings? grid = null)
    {
        var store = new MemorySettingsStore();
        var settings = new SettingsService(store);
        if (grid is not null)
        {
            settings.UpdateAsync(s => s with { Grid = grid }).GetAwaiter().GetResult();
        }

        return (store, new SettingsViewModel(settings, new ShellText(language: "ko")));
    }

    [Fact]
    public void Defaults_are_on_and_recent_one_and_a_half_days()
    {
        var (_, vm) = Create();

        Assert.True(vm.FlashbackTrackEnabled);
        Assert.True(vm.FlashbackTrackRecent);
        Assert.False(vm.FlashbackTrackAll);
        Assert.Equal(1.5m, vm.FlashbackTrackDays);
    }

    [Fact]
    public async Task Save_writes_the_switch_the_range_and_days_clamped()
    {
        var (store, vm) = Create();
        vm.FlashbackTrackEnabled = false;
        vm.FlashbackTrackAll = true;
        vm.FlashbackTrackDays = 9999m;

        await vm.SaveAsync();

        Assert.False(store.Saved.Grid.FlashbackTrackEnabled);
        Assert.Equal(FlashbackTrackRange.All, store.Saved.Grid.FlashbackTrackRange);
        Assert.Equal(GridSettings.MaxFlashbackTrackDays, store.Saved.Grid.FlashbackTrackDays);

        var (_, again) = Create(store.Saved.Grid);
        Assert.False(again.FlashbackTrackEnabled);
        Assert.False(again.FlashbackTrackRecent);
    }

    [Fact]
    public Task Turning_track_changes_off_greys_out_the_period_options() => HeadlessHost.Run(() =>
    {
        var (_, vm) = Create();
        var window = new SettingsWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => (string?)t.Header == "결과 그리드");
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        T Named<T>(string name)
            where T : Control => window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
        Assert.True(Named<CheckBox>("FlashbackTrackBox").IsChecked);
        Assert.True(Named<StackPanel>("FlashbackRangePanel").IsEnabled);
        Assert.True(Named<NumericUpDown>("FlashbackDaysBox").IsEffectivelyEnabled);

        vm.FlashbackTrackAll = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(Named<NumericUpDown>("FlashbackDaysBox").IsEffectivelyEnabled); // 전체 기간이면 일수 칸은 쓰지 않는다

        vm.FlashbackTrackRecent = true;
        vm.FlashbackTrackEnabled = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(Named<StackPanel>("FlashbackRangePanel").IsEnabled);
        Assert.False(Named<NumericUpDown>("FlashbackDaysBox").IsEffectivelyEnabled);
        window.Close();
    });
}
