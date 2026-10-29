using Avalonia;
using Avalonia.Threading;
using Oranda.App.Shell;
using Oranda.App.Shell.Appearance;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Shell.Settings;

/// <summary>패널·표 글자 크기(ui.panelFontSize): 저장 왕복, 허용 범위, 앱을 다시 띄우지 않는 즉시 반영, 일반 탭 안내 링크.</summary>
// 화면 글자 리소스는 프로세스에 하나라서 다른 모양 시험과 겹치지 않게 따로 돈다.
[Collection("EditorAppearance")]
public class PanelFontSizeTests
{
    private static double Small => (double)Application.Current!.Resources[ThemeService.UiFontSizeSmallKey]!;

    [Fact]
    public Task Saved_size_round_trips_and_applies_immediately_without_the_ui_scale() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var settings = h.Get<SettingsService>();
        var original = settings.Current;
        try
        {
            var vm = new SettingsViewModel(settings, h.Get<IShellText>());
            Assert.Null(vm.PanelFontSize); // 기본: 비움 = 배율에 맞춤

            vm.UiScalePercent = 150;
            vm.PanelFontSize = 16;
            await vm.SaveAsync();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(16, settings.Current.Ui.PanelFontSize);
            Assert.Equal(16.0, Small); // 값이 있으면 배율(1.5)을 곱하지 않는다
            Assert.Equal(21.0, (double)Application.Current!.Resources[ThemeService.UiFontSizeBaseKey]!); // 기본 글자는 배율을 그대로 따른다

            Assert.Equal(16m, new SettingsViewModel(settings, h.Get<IShellText>()).PanelFontSize);

            var cleared = new SettingsViewModel(settings, h.Get<IShellText>()) { PanelFontSize = null };
            await cleared.SaveAsync();
            Dispatcher.UIThread.RunJobs();

            Assert.Null(settings.Current.Ui.PanelFontSize);
            Assert.Equal(ThemeService.SmallUiFontSize * 1.5, Small);
        }
        finally
        {
            await settings.UpdateAsync(_ => original);
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Fact]
    public Task Out_of_range_values_are_clamped_to_8_32() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var settings = h.Get<SettingsService>();
        var original = settings.Current;
        try
        {
            await settings.UpdateAsync(s => s with { Ui = s.Ui with { PanelFontSize = 99 } });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(32.0, Small);

            await settings.UpdateAsync(s => s with { Ui = s.Ui with { PanelFontSize = 2 } });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(8.0, Small);

            Assert.Equal(ThemeService.SmallUiFontSize, ThemeService.ClampPanelFontSize(double.NaN));
        }
        finally
        {
            await settings.UpdateAsync(_ => original);
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Fact]
    public Task Watermark_shows_the_auto_size_and_the_general_tab_links_jump_to_the_editor_and_grid_tabs() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var vm = new SettingsViewModel(h.Get<SettingsService>(), h.Get<IShellText>());

        vm.UiScalePercent = 150;
        Assert.Contains("18", vm.PanelFontSizeWatermark, StringComparison.Ordinal); // 12 × 1.5

        vm.ShowGridTabCommand.Execute(null);
        Assert.Equal(SettingsViewModel.GridTabIndex, vm.SelectedTab);
        vm.ShowEditorTabCommand.Execute(null);
        Assert.Equal(SettingsViewModel.EditorTabIndex, vm.SelectedTab);
        return Task.CompletedTask;
    });
}
