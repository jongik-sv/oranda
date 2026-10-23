using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.FormatterProfiles;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.Update;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Formatting;
using Oranda.Core.Storage;

namespace Oranda.App.Tests.Shell;

/// <summary>설정 창 크기: 열 때 모든 범주 중 가장 큰 쪽에 맞추고(폭은 설계 상한 안에서), 범주를 바꾸거나 미리보기가 바뀌어도 창 크기는 그대로.</summary>
[Collection("UiLanguage")]
public sealed class SettingsWindowSizeTests
{
    private static async Task<SettingsViewModel> OpenVm()
    {
        var type = typeof(Oranda.Language.LanguageAssembly).Assembly.GetType("Oranda.Language.Formatting.SqlFormatter")!;
        var formatter = (ISqlFormatter)Activator.CreateInstance(type, nonPublic: true)!;
        var settings = new SettingsService(new MemorySettingsStore());
        var text = new ShellText(language: "ko");
        var page = new SqlFormatPage(new SqlFormatSettingsTests.ProfileStore(), formatter, settings.Current.FormatterProfileId, text);
        await page.Loaded;
        return new SettingsViewModel(settings, text, sqlFormat: page);
    }

    [Fact]
    public Task Window_size_stays_the_same_when_switching_categories_and_changing_format_options() => HeadlessHost.Run(async () =>
    {
        var vm = await OpenVm();
        var window = new SettingsWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var size = window.Bounds.Size;
        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        Assert.Equal(0, tabs.SelectedIndex);

        for (var i = 0; i < tabs.ItemCount; i++)
        {
            tabs.SelectedIndex = i;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(size, window.Bounds.Size);

            // 창은 그 범주 내용이 들어갈 만큼 크다(스크롤 없이 보인다). 다만 폭 상한에 닿은 창은 넘치는 범주가 상한 안에서 줄어든다.
            var content = (Control)((TabItem)tabs.Items[i]!).Content!;
            content.Measure(Size.Infinity);
            var atCap = window.Bounds.Width + 0.5 >= SettingsWindow.MaxBodyWidth + 24;
            Assert.True(atCap || content.Bounds.Width + 0.5 >= content.DesiredSize.Width, $"tab {i} width {content.Bounds.Width} < {content.DesiredSize.Width}, window {window.Bounds.Size}");
        }

        tabs.SelectedIndex = SettingsViewModel.FormatTabIndex;
        Dispatcher.UIThread.RunJobs();
        vm.SqlFormat.CommaLeading = true;
        vm.SqlFormat.ByListMulti = true;
        vm.SqlFormat.CaseLower = true;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Equal(size, window.Bounds.Size);
        window.Close();
    });

    [Theory]
    [InlineData(2560)]
    [InlineData(600)]
    public Task Window_width_is_capped_even_when_the_main_window_is_very_wide(double ownerWidth) => HeadlessHost.Run(async () =>
    {
        var vm = await OpenVm();
        var owner = new Window { Width = ownerWidth, Height = 900 };
        owner.Show();
        Dispatcher.UIThread.RunJobs();
        var window = new SettingsWindow { DataContext = vm };
        window.Show(owner);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        // 본문 상한(MaxBodyWidth)과 주 창 폭 중 작은 값 + 바깥 여백 12*2. 상한이 없으면 가장 넓은 범주(편집기 886)까지 커진다.
        var cap = Math.Min(SettingsWindow.MaxBodyWidth + 24, ownerWidth);
        Assert.True(window.Bounds.Width <= cap + 0.5, $"window {window.Bounds.Width} > cap {cap}");

        // 상한에 걸려도 범주를 바꿔 크기가 흔들리지 않고, 넘치는 쪽은 가로로 밀려 나가지 않는다(본문 가로 스크롤 없음).
        var size = window.Bounds.Size;
        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        for (var i = 0; i < tabs.ItemCount; i++)
        {
            tabs.SelectedIndex = i;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(size, window.Bounds.Size);
        }

        window.Close();
        owner.Close();
    });
}
