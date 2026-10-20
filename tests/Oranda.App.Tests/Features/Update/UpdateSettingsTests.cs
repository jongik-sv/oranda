using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.Update;

/// <summary>설정 &gt; 업데이트: 값 읽기·저장, 잘못된 주소 거부, http 경고, 건너뛴 버전 지우기, 저장이 백그라운드 기록을 덮지 않음.</summary>
[Collection("UiLanguage")]
public sealed class UpdateSettingsTests
{
    private static async Task<(SettingsService Settings, MemorySettingsStore Store, SettingsViewModel Vm)> Create(Func<Oranda.Core.Settings.UpdateSettings, Oranda.Core.Settings.UpdateSettings>? change = null)
    {
        var store = new MemorySettingsStore();
        var settings = new SettingsService(store);
        if (change is not null)
        {
            await settings.UpdateAsync(s => s with { Update = change(s.Update) });
        }

        return (settings, store, new SettingsViewModel(settings, new ShellText(language: "ko")));
    }

    [Fact]
    public async Task Defaults_check_on_startup_and_auto_download_with_an_empty_feed()
    {
        var (_, _, vm) = await Create();

        Assert.True(vm.UpdateCheckOnStartup);
        Assert.True(vm.UpdateAutoDownload);
        Assert.Equal(string.Empty, vm.UpdateFeedUrl);
        Assert.False(vm.HasFeedMessage);
        Assert.False(vm.HasSkippedVersion);
        Assert.Equal("없음", vm.SkippedVersionText);
        Assert.Equal("아직 확인하지 않음", vm.LastCheckedText);
    }

    [Fact]
    public async Task Save_writes_the_update_section_and_trims_the_feed()
    {
        var (_, store, vm) = await Create();
        vm.UpdateCheckOnStartup = false;
        vm.UpdateAutoDownload = false;
        vm.UpdateFeedUrl = @"  \\files\oranda  ";

        await vm.SaveAsync();

        Assert.False(store.Saved.Update.CheckOnStartup);
        Assert.False(store.Saved.Update.AutoDownload);
        Assert.Equal(@"\\files\oranda", store.Saved.Update.FeedUrl);
    }

    [Fact]
    public async Task A_blank_feed_is_saved_as_null_so_the_build_default_applies()
    {
        var (_, store, vm) = await Create(u => u with { FeedUrl = "https://x.example.com/feed" });
        vm.UpdateFeedUrl = "   ";

        await vm.SaveAsync();

        Assert.Null(store.Saved.Update.FeedUrl);
    }

    [Fact]
    public async Task An_invalid_feed_shows_the_reason_and_is_not_saved()
    {
        var (_, store, vm) = await Create();
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        vm.UpdateFeedUrl = "ftp://example.com/feed";

        Assert.True(vm.FeedMessageIsError);
        Assert.Equal("http·https 주소나 폴더 경로만 쓸 수 있습니다.", vm.FeedMessage);
        await vm.SaveAsync();

        Assert.Null(store.Saved.Update.FeedUrl);
        Assert.False(closed);
        Assert.Equal(SettingsViewModel.UpdateTabIndex, vm.SelectedTab); // 이유가 보이는 탭으로 옮긴다
    }

    [Fact]
    public async Task Plain_http_is_accepted_with_a_warning()
    {
        var (_, store, vm) = await Create();
        vm.UpdateFeedUrl = "http://intranet/oranda";

        Assert.False(vm.FeedMessageIsError);
        Assert.True(vm.HasFeedMessage);
        Assert.Contains("암호화되지 않은", vm.FeedMessage, StringComparison.Ordinal);
        await vm.SaveAsync();

        Assert.Equal("http://intranet/oranda", store.Saved.Update.FeedUrl);
    }

    [Fact]
    public async Task Save_keeps_the_skipped_version_and_last_check_the_background_check_wrote()
    {
        var (settings, store, vm) = await Create();
        var stamp = new DateTimeOffset(2026, 10, 20, 1, 2, 3, TimeSpan.Zero);
        // 설정 창을 연 뒤 백그라운드 확인이 기록했다.
        await settings.UpdateAsync(s => s with { Update = s.Update with { SkippedVersion = "0.2.0", LastCheckedUtc = stamp } });

        await vm.SaveAsync();

        Assert.Equal("0.2.0", store.Saved.Update.SkippedVersion);
        Assert.Equal(stamp, store.Saved.Update.LastCheckedUtc);
    }

    [Fact]
    public async Task Clearing_the_skipped_version_is_saved()
    {
        var (_, store, vm) = await Create(u => u with { SkippedVersion = "0.2.0" });
        Assert.True(vm.HasSkippedVersion);
        Assert.Equal("0.2.0", vm.SkippedVersionText);

        vm.ClearSkippedVersionCommand.Execute(null);
        Assert.False(vm.HasSkippedVersion);
        await vm.SaveAsync();

        Assert.Null(store.Saved.Update.SkippedVersion);
    }

    [Fact]
    public Task Settings_window_has_the_update_tab_controls() => HeadlessHost.Run(async () =>
    {
        var (_, _, vm) = await Create(u => u with { SkippedVersion = "0.2.0" });
        var window = new SettingsWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        var update = tabs.Items.OfType<TabItem>().Single(t => (string?)t.Header == "업데이트");
        tabs.SelectedItem = update;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        T Named<T>(string name)
            where T : Control => window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
        Assert.True(Named<CheckBox>("UpdateCheckOnStartupBox").IsChecked);
        Assert.True(Named<CheckBox>("UpdateAutoDownloadBox").IsChecked);
        Assert.NotNull(Named<TextBox>("UpdateFeedBox"));
        Assert.True(Named<Button>("ClearSkippedButton").IsVisible);
        Assert.Equal("지금 확인", (string?)Named<Button>("CheckUpdateNowButton").Content);

        vm.UpdateFeedUrl = "nonsense";
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<TextBlock>("UpdateFeedMessage").IsVisible);
        window.Close();
    });

    [Fact]
    public async Task Overlapping_updates_do_not_lose_each_other()
    {
        var (settings, store, _) = await Create();

        await Task.WhenAll(Enumerable.Range(0, 60).Select(_ =>
            Task.Run(() => settings.UpdateAsync(s => s with { Editor = s.Editor with { FontSize = s.Editor.FontSize + 0.5 } }))));

        Assert.Equal(13 + 30, settings.Current.Editor.FontSize);
        Assert.Equal(settings.Current, store.Saved);
    }
}
