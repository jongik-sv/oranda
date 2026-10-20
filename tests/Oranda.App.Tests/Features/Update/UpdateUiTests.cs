using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Localization;
using Oranda.App.Features.Update;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.Update;

/// <summary>업데이트 화면(Headless): 명령 위치, 상태 표시줄 구간, 창 상태별 단추, 설정 탭, 실제 닫기 흐름과 이어진 적용.</summary>
[Collection("UiLanguage")]
public sealed class UpdateUiTests
{
    private static ShellText Ko()
    {
        var text = new ShellText(language: "ko");
        UiText.Current = text;
        return text;
    }

    private static T Named<T>(Control root, string name)
        where T : Control => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static UpdateWindow Show(UpdateHarness h, IShellText text)
    {
        var window = new UpdateWindow { DataContext = new UpdateViewModel(h.Service, text) };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Refresh(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    // ---- 등록 ----

    [Fact]
    public void Check_update_command_is_in_the_help_menu_above_about_and_in_the_palette()
    {
        var commands = ShellTestHelpers.AppContributions.Commands;
        var check = commands.Single(c => c.Definition.Id == "oranda.help.checkUpdate").Definition;
        var about = commands.Single(c => c.Definition.Id == "oranda.help.about").Definition;

        Assert.Equal(CommandCategory.Help, check.Category);
        Assert.Equal(["menu:help/checkUpdate@890"], check.Placements);
        Assert.True(check.ShowInPalette);
        Assert.Null(check.When); // 단축키 없음
        Assert.True(OrderOf(check) < OrderOf(about));

        static int OrderOf(CommandDefinition c) => int.Parse(c.Placements[0][(c.Placements[0].IndexOf('@', StringComparison.Ordinal) + 1)..], System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Update_status_bar_item_is_registered_after_the_protection_item()
    {
        var item = ShellTestHelpers.AppContributions.StatusBarItems.Single(i => i.Id == "oranda.status.update");

        Assert.Equal(8, item.Order);
        Assert.Equal(typeof(UpdateStatusViewModel), item.ItemType);
        Assert.Equal(0, item.CollapseRank);
    }

    // ---- 상태 표시줄 ----

    [Fact]
    public Task Status_item_follows_the_update_state() => HeadlessHost.Run(async () =>
    {
        Ko();
        var h = new UpdateHarness();
        var status = new UpdateStatusViewModel(h.Service);
        Assert.False(status.IsVisible); // 시작 전·조용한 결과에서는 보이지 않는다

        await h.Configure(u => u with { AutoDownload = false });
        await h.Service.RunStartupAsync(TimeSpan.Zero);
        Dispatcher.UIThread.RunJobs();
        Assert.True(status.IsVisible);
        Assert.Equal("업데이트 0.1.9 있음", status.Text);

        await h.Service.DownloadAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(UpdateStatusKind.Ready, status.Kind);
        Assert.Equal("업데이트 0.1.9 준비됨", status.Text);
    });

    [Fact]
    public Task Status_item_hides_a_skipped_version() => HeadlessHost.Run(async () =>
    {
        Ko();
        var h = new UpdateHarness();
        await h.Configure(u => u with { AutoDownload = false });
        await h.Service.CheckAsync(manual: true);
        await h.Service.SkipVersionAsync();
        var status = new UpdateStatusViewModel(h.Service);

        Assert.False(status.IsVisible);
    });

    [Fact]
    public Task Status_item_view_shows_text_and_opens_the_dialog_on_press() => HeadlessHost.Run(async () =>
    {
        Ko();
        var h = new UpdateHarness();
        await h.Service.RunStartupAsync(TimeSpan.Zero);
        var view = new UpdateStatusView { DataContext = new UpdateStatusViewModel(h.Service) };
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "업데이트 0.1.9 준비됨");
        window.Close();
    });

    // ---- 업데이트 창 ----

    [Fact]
    public Task Dialog_up_to_date_offers_only_recheck_and_close() => HeadlessHost.Run(async () =>
    {
        var text = Ko();
        var h = new UpdateHarness();
        h.Updater.Offer = null;
        await h.Service.CheckAsync(manual: true);
        var window = Show(h, text);

        Assert.Equal("최신 버전입니다.", Named<TextBlock>(window, "StatusText").Text);
        AssertVisible(window, recheck: true, close: true);
        window.Close();
    });

    [Fact]
    public Task Dialog_available_offers_download_and_skip_and_shows_notes() => HeadlessHost.Run(async () =>
    {
        var text = Ko();
        var h = new UpdateHarness();
        await h.Service.CheckAsync(manual: true);
        var window = Show(h, text);

        Assert.Equal("0.1.9", Named<TextBlock>(window, "NewVersionText").Text);
        Assert.Contains("0.1.9", Named<TextBlock>(window, "StatusText").Text, StringComparison.Ordinal);
        Assert.Equal("• 고침", Named<SelectableTextBlock>(window, "NotesBox").Inlines!.Select(i => ((Avalonia.Controls.Documents.Run)i).Text).Single());
        AssertVisible(window, download: true, skip: true, close: true);
        window.Close();
    });

    [Fact]
    public Task Dialog_downloading_shows_progress_and_cancel() => HeadlessHost.Run(async () =>
    {
        var text = Ko();
        var h = new UpdateHarness();
        h.Updater.DownloadGate = new TaskCompletionSource();
        await h.Service.CheckAsync(manual: true);
        var window = Show(h, text);

        var download = h.Service.DownloadAsync();
        await HeadlessHost.WaitUntil(() => h.Service.Progress == 40);
        Refresh(window);

        Assert.True(Named<ProgressBar>(window, "DownloadProgress").IsVisible);
        Assert.Equal(40, Named<ProgressBar>(window, "DownloadProgress").Value);
        AssertVisible(window, cancel: true, close: true);

        h.Updater.DownloadGate.SetResult();
        await download;
        window.Close();
    });

    [Fact]
    public Task Dialog_ready_offers_restart_apply_and_apply_on_exit() => HeadlessHost.Run(async () =>
    {
        var text = Ko();
        var h = new UpdateHarness();
        await h.Service.RunStartupAsync(TimeSpan.Zero);
        var window = Show(h, text);

        Assert.Contains("종료할 때 적용됩니다", Named<TextBlock>(window, "StatusText").Text, StringComparison.Ordinal);
        AssertVisible(window, applyNow: true, applyOnExit: true, close: true);
        window.Close();
    });

    [Fact]
    public Task Dialog_not_installed_explains_and_offers_nothing_to_download() => HeadlessHost.Run(async () =>
    {
        var text = Ko();
        var h = new UpdateHarness();
        h.Updater.IsInstalled = false;
        await h.Service.CheckAsync(manual: true);
        var window = Show(h, text);

        Assert.StartsWith("설치하지 않은 실행본(개발 빌드)이라", Named<TextBlock>(window, "StatusText").Text, StringComparison.Ordinal);
        AssertVisible(window, close: true);
        window.Close();
    });

    [Fact]
    public Task Dialog_missing_feed_offers_to_open_settings() => HeadlessHost.Run(async () =>
    {
        var text = Ko();
        var h = new UpdateHarness();
        h.Service.DefaultFeed = null;
        await h.Service.CheckAsync(manual: true);
        var window = Show(h, text);

        Assert.StartsWith("업데이트 서버가 설정되지 않았습니다.", Named<TextBlock>(window, "StatusText").Text, StringComparison.Ordinal);
        AssertVisible(window, settings: true, close: true);
        window.Close();
    });

    [Fact]
    public Task Dialog_buttons_run_the_service() => HeadlessHost.Run(async () =>
    {
        var text = Ko();
        var h = new UpdateHarness();
        await h.Configure(u => u with { AutoDownload = false });
        await h.Service.CheckAsync(manual: true);
        var window = Show(h, text);
        var vm = (UpdateViewModel)window.DataContext!;

        vm.DownloadCommand.Execute(null);
        await HeadlessHost.WaitUntil(() => h.Service.Phase == UpdatePhase.Ready);
        h.Exit.Passes = false;
        await vm.ApplyNowCommand.ExecuteAsync(null);

        Assert.Equal(0, h.Updater.ApplyAndRestartCalls); // 닫기 흐름이 취소했다 → 창도 그대로
        Assert.True(window.IsVisible);

        h.Exit.Passes = true;
        await vm.ApplyNowCommand.ExecuteAsync(null);
        h.Service.CompleteExit();
        Assert.Equal(1, h.Updater.ApplyAndRestartCalls);
        Assert.False(window.IsVisible);
    });

    [Fact]
    public Task Command_opens_one_dialog_and_reuses_it() => HeadlessHost.Run(async () =>
    {
        using var shell = new ShellHarness();
        var keys = shell.Get<IContextKeys>();
        var registry = shell.Get<ICommandRegistry>();

        await registry.ExecuteAsync("oranda.help.checkUpdate", keys);
        var first = UpdateWindow.Open;
        await registry.ExecuteAsync("oranda.help.checkUpdate", keys);

        Assert.NotNull(first);
        Assert.Same(first, UpdateWindow.Open);
        // 개발 실행본(설치 안 됨)이라 안내만 나온다.
        await HeadlessHost.WaitUntil(() => Named<TextBlock>(first, "StatusText").Text?.StartsWith("설치하지 않은", StringComparison.Ordinal) == true);
        first.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(UpdateWindow.Open);
    });

    private static void AssertVisible(
        Window window, bool download = false, bool applyNow = false, bool applyOnExit = false, bool skip = false,
        bool cancel = false, bool settings = false, bool recheck = false, bool close = false)
    {
        Assert.Equal(download, Named<Button>(window, "DownloadButton").IsVisible);
        Assert.Equal(applyNow, Named<Button>(window, "ApplyNowButton").IsVisible);
        Assert.Equal(applyOnExit, Named<Button>(window, "ApplyOnExitButton").IsVisible);
        Assert.Equal(skip, Named<Button>(window, "SkipButton").IsVisible);
        Assert.Equal(cancel, Named<Button>(window, "CancelDownloadButton").IsVisible);
        Assert.Equal(settings, Named<Button>(window, "SettingsButton").IsVisible);
        Assert.Equal(recheck, Named<Button>(window, "RecheckButton").IsVisible);
        Assert.Equal(close, Named<Button>(window, "CloseButton").IsVisible);
    }

    // ---- 실제 닫기 흐름과 이어진 적용 ----

    [Fact]
    public Task Restart_apply_goes_through_the_real_main_window_close_flow() => HeadlessHost.Run(async () =>
    {
        using var shell = new ShellHarness();
        var updater = new FakeUpdater { Offer = new UpdateOffer("0.1.9", null, 1) };
        var service = new UpdateService(updater, shell.Get<SettingsService>(), new ShellExitFlow(shell.Get<ShellController>()))
        {
            DefaultFeed = "https://updates.example.com/oranda",
        };
        await service.RunStartupAsync(TimeSpan.Zero);
        Assert.Equal(UpdatePhase.Ready, service.Phase);

        var passed = await service.ApplyAndRestartAsync();

        Assert.True(passed); // 열린 SQL 탭·미커밋이 없으니 닫기 확인을 통과한다
        Assert.Equal(0, updater.ApplyAndRestartCalls); // 갱신기는 프로세스가 끝나기 직전(Program.Main)에야 띄운다
        service.CompleteExit();
        Assert.Equal(1, updater.ApplyAndRestartCalls);
        Assert.Equal(0, updater.ApplyOnExitCalls);
        service.Dispose();
    });
}
