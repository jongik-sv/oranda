using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Localization;
using Oranda.App.Features.Update;
using Oranda.App.Shell;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.Update;

/// <summary>업데이트 알림 띠(Headless): 준비됨·찾음에서 보이고, 「나중에」로 닫히며, 같은 버전은 다시 뜨지 않는다.</summary>
[Collection("UiLanguage")]
public sealed class UpdateBannerTests
{
    private static ShellText Ko(string language = "ko")
    {
        var text = new ShellText(language: language);
        UiText.Current = text;
        return text;
    }

    private static T Named<T>(Control root, string name)
        where T : Control => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static Window Show(UpdateBannerViewModel vm, out UpdateBanner banner)
    {
        banner = new UpdateBanner { DataContext = vm };
        var window = new Window { Width = 900, Height = 120, Content = new DockPanel { Children = { banner } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Render(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR") is not { Length: > 0 } dir)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        using var bmp = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)window.Bounds.Width), Math.Max(1, (int)window.Bounds.Height)));
        bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
        bmp.Save(Path.Combine(dir, name + ".png"));
#pragma warning restore CS0618
    }

    [Fact]
    public Task Ready_state_shows_the_banner_and_later_closes_it_for_that_version() => HeadlessHost.Run(async () =>
    {
        Ko();
        var h = new UpdateHarness();
        var vm = new UpdateBannerViewModel(h.Service);
        var window = Show(vm, out var banner);
        Assert.False(banner.IsVisible); // 알릴 것이 없으면 보이지 않는다

        await h.Service.RunStartupAsync(TimeSpan.Zero); // 자동 내려받기 → 준비됨
        Dispatcher.UIThread.RunJobs();

        Assert.True(banner.IsVisible);
        Assert.Contains(banner.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Oranda 0.1.9 이 준비되었습니다");
        Assert.Equal("다시 시작해 적용", (string?)Named<Button>(banner, "UpdateBannerPrimaryButton").Content);
        Assert.Equal("자세히", (string?)Named<Button>(banner, "UpdateBannerDetailsButton").Content);
        Render(window, "update-banner-ready-ko");

        Named<Button>(banner, "UpdateBannerLaterButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.False(banner.IsVisible);

        // 같은 버전은 이 실행에서 다시 띄우지 않는다(상태가 다시 바뀌어도).
        h.Service.RefreshSettings();
        Dispatcher.UIThread.RunJobs();
        Assert.False(banner.IsVisible);
        Assert.False(vm.IsVisible);
        window.Close();
    });

    [Fact]
    public Task A_newer_version_after_dismissal_is_announced_again() => HeadlessHost.Run(async () =>
    {
        Ko();
        var h = new UpdateHarness();
        await h.Configure(u => u with { AutoDownload = false });
        var vm = new UpdateBannerViewModel(h.Service);
        await h.Service.RunStartupAsync(TimeSpan.Zero);
        Assert.True(vm.IsVisible);
        vm.Dismiss();
        Assert.False(vm.IsVisible);

        h.Updater.Offer = new UpdateOffer("0.1.10", "- 더 고침", 1234);
        await h.Service.CheckAsync(manual: true);

        Assert.True(vm.IsVisible);
        Assert.Equal("0.1.10", vm.Version);
    });

    [Fact]
    public Task Found_version_with_auto_download_off_offers_a_download_button() => HeadlessHost.Run(async () =>
    {
        Ko();
        var h = new UpdateHarness();
        await h.Configure(u => u with { AutoDownload = false });
        var vm = new UpdateBannerViewModel(h.Service);
        var window = Show(vm, out var banner);

        await h.Service.RunStartupAsync(TimeSpan.Zero);
        Dispatcher.UIThread.RunJobs();

        Assert.True(banner.IsVisible);
        Assert.Contains(banner.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Oranda 0.1.9 을 내려받을 수 있습니다");
        Assert.Equal("내려받기", (string?)Named<Button>(banner, "UpdateBannerPrimaryButton").Content);

        await vm.PrimaryAsync(); // 내려받기 → 준비됨
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(UpdatePhase.Ready, h.Service.Phase);
        Assert.Contains(banner.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Oranda 0.1.9 이 준비되었습니다");
        window.Close();
    });

    [Fact]
    public Task Auto_download_on_does_not_flash_a_found_banner() => HeadlessHost.Run(async () =>
    {
        Ko();
        var h = new UpdateHarness();
        h.Updater.DownloadGate = new TaskCompletionSource();
        var vm = new UpdateBannerViewModel(h.Service);

        var startup = h.Service.RunStartupAsync(TimeSpan.Zero);
        await Task.Delay(100);
        Assert.Equal(UpdatePhase.Downloading, h.Service.Phase);
        Assert.False(vm.IsVisible); // 받는 중에는 띄우지 않는다

        h.Updater.DownloadGate.SetResult();
        await startup;
        Assert.True(vm.IsVisible);
    });

    [Fact]
    public Task Skipped_version_and_quiet_results_show_nothing() => HeadlessHost.Run(async () =>
    {
        Ko();
        var h = new UpdateHarness();
        await h.Configure(u => u with { AutoDownload = false, SkippedVersion = "0.1.9" });
        var vm = new UpdateBannerViewModel(h.Service);

        await h.Service.RunStartupAsync(TimeSpan.Zero);
        Assert.False(vm.IsVisible);

        await h.Service.CheckAsync(manual: true); // 건너뛴 버전은 창에는 보여도 띠는 띄우지 않는다
        Assert.True(h.Service.IsSkipped);
        Assert.False(vm.IsVisible);
    });

    [Fact]
    public Task Restart_button_asks_the_exit_flow_to_apply() => HeadlessHost.Run(async () =>
    {
        Ko();
        var h = new UpdateHarness();
        var vm = new UpdateBannerViewModel(h.Service);
        await h.Service.RunStartupAsync(TimeSpan.Zero);

        await vm.PrimaryAsync();

        Assert.Equal(1, h.Exit.Requests);
        Assert.Equal(UpdateApplyMode.Restart, h.Service.ApplyMode);
    });

    [Fact]
    public Task English_labels_and_render() => HeadlessHost.Run(async () =>
    {
        Ko("en");
        try
        {
            var h = new UpdateHarness();
            var vm = new UpdateBannerViewModel(h.Service);
            var window = Show(vm, out var banner);
            await h.Service.RunStartupAsync(TimeSpan.Zero);
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(banner.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Oranda 0.1.9 is ready");
            Assert.Equal("Restart to apply", (string?)Named<Button>(banner, "UpdateBannerPrimaryButton").Content);
            Assert.Equal("Later", (string?)Named<Button>(banner, "UpdateBannerLaterButton").Content);
            Render(window, "update-banner-ready-en");
            window.Close();
        }
        finally
        {
            Ko();
        }
    });
}
