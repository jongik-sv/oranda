using Oranda.App.Features.Update;

namespace Oranda.App.Tests.Features.Update;

/// <summary>업데이트 상태 기계(가짜 업데이트 구현·가짜 닫기 흐름): 시작 확인 규칙, 자동 내려받기, 적용 시점.</summary>
public class UpdateServiceTests
{
    // ---- 설치 안 됨·피드 없음 ----

    [Fact]
    public async Task Not_installed_skips_startup_check_and_manual_check_only_reports_the_state()
    {
        var h = new UpdateHarness();
        h.Updater.IsInstalled = false;

        await h.Service.RunStartupAsync(TimeSpan.Zero);
        Assert.Equal(0, h.Updater.CheckCalls);
        Assert.Equal(UpdatePhase.Idle, h.Service.Phase);

        await h.Service.CheckAsync(manual: true); // 예외 없이 상태만 바뀐다
        Assert.Equal(UpdatePhase.NotInstalled, h.Service.Phase);
        Assert.Equal(0, h.Updater.CheckCalls);
    }

    [Fact]
    public async Task Missing_feed_is_quiet_at_startup_and_guides_a_manual_check()
    {
        var h = new UpdateHarness();
        h.Service.DefaultFeed = null;

        await h.Service.RunStartupAsync(TimeSpan.Zero);
        Assert.Equal(0, h.Updater.CheckCalls);
        Assert.Equal(UpdatePhase.Idle, h.Service.Phase);

        await h.Service.CheckAsync(manual: true);
        Assert.Equal(UpdatePhase.NoFeed, h.Service.Phase);
        Assert.Equal(0, h.Updater.CheckCalls);
    }

    [Fact]
    public async Task Invalid_configured_feed_reports_the_reason()
    {
        var h = new UpdateHarness();
        await h.Configure(u => u with { FeedUrl = "ftp://x/y" });

        await h.Service.CheckAsync(manual: true);

        Assert.Equal(UpdatePhase.InvalidFeed, h.Service.Phase);
        Assert.Equal(UpdateFeedError.UnsupportedScheme, h.Service.FeedError);
    }

    [Fact]
    public async Task Configured_feed_wins_over_the_build_default()
    {
        var h = new UpdateHarness();
        Assert.Equal("https://updates.example.com/oranda", h.Service.ResolveFeed().Feed!.Location);

        await h.Configure(u => u with { FeedUrl = @"\\files\oranda" });

        var feed = h.Service.ResolveFeed().Feed!;
        Assert.Equal(UpdateFeedKind.Folder, feed.Kind);
        await h.Service.CheckAsync(manual: true);
        Assert.Equal(feed, h.Updater.LastFeed);
    }

    // ---- 시작 확인 규칙 ----

    [Fact]
    public async Task Startup_check_respects_the_24_hour_rule_and_remembers_the_time()
    {
        var h = new UpdateHarness();
        await h.Configure(u => u with { AutoDownload = false });

        await h.Service.RunStartupAsync(TimeSpan.Zero);
        Assert.Equal(1, h.Updater.CheckCalls);
        Assert.Equal(h.Time.Now, h.Store.Saved.Update.LastCheckedUtc);

        h.Time.Now += TimeSpan.FromHours(23);
        await h.Service.RunStartupAsync(TimeSpan.Zero);
        Assert.Equal(1, h.Updater.CheckCalls); // 24시간 전에는 다시 확인하지 않는다

        h.Time.Now += TimeSpan.FromHours(2);
        await h.Service.RunStartupAsync(TimeSpan.Zero);
        Assert.Equal(2, h.Updater.CheckCalls);
    }

    [Fact]
    public async Task Startup_check_can_be_turned_off()
    {
        var h = new UpdateHarness();
        await h.Configure(u => u with { CheckOnStartup = false });

        await h.Service.RunStartupAsync(TimeSpan.Zero);

        Assert.Equal(0, h.Updater.CheckCalls);
    }

    [Fact]
    public async Task Skipped_version_is_not_announced_at_startup_but_a_manual_check_shows_it()
    {
        var h = new UpdateHarness();
        await h.Configure(u => u with { SkippedVersion = "0.1.9" });

        await h.Service.RunStartupAsync(TimeSpan.Zero);
        Assert.Equal(UpdatePhase.Idle, h.Service.Phase); // 알리지도 받지도 않는다
        Assert.Equal(0, h.Updater.DownloadCalls);

        await h.Service.CheckAsync(manual: true);
        Assert.Equal(UpdatePhase.Available, h.Service.Phase);
        Assert.True(h.Service.IsSkipped);
    }

    [Fact]
    public async Task A_newer_version_than_the_skipped_one_is_announced()
    {
        var h = new UpdateHarness();
        await h.Configure(u => u with { SkippedVersion = "0.1.8", AutoDownload = false });

        await h.Service.RunStartupAsync(TimeSpan.Zero);

        Assert.Equal(UpdatePhase.Available, h.Service.Phase);
        Assert.False(h.Service.IsSkipped);
    }

    [Fact]
    public async Task Skipping_a_version_is_saved()
    {
        var h = new UpdateHarness();
        await h.Service.CheckAsync(manual: true);

        await h.Service.SkipVersionAsync();

        Assert.Equal("0.1.9", h.Store.Saved.Update.SkippedVersion);
        Assert.True(h.Service.IsSkipped);
    }

    [Fact]
    public async Task Offline_failure_at_startup_is_silent_and_a_manual_failure_shows_the_cause()
    {
        var h = new UpdateHarness();
        h.Updater.CheckFails = new HttpRequestException("name resolution failed");

        await h.Service.RunStartupAsync(TimeSpan.Zero);
        Assert.Equal(UpdatePhase.Idle, h.Service.Phase);
        Assert.Null(h.Store.Saved.Update.LastCheckedUtc); // 실패한 확인은 24시간 규칙에 세지 않는다

        await h.Service.CheckAsync(manual: true);
        Assert.Equal(UpdatePhase.Error, h.Service.Phase);
        Assert.Contains("name resolution failed", h.Service.ErrorDetail, StringComparison.Ordinal);
        Assert.False(h.Service.ErrorWhileDownloading);
    }

    [Fact]
    public async Task Up_to_date_is_shown_for_a_manual_check_and_quiet_at_startup()
    {
        var h = new UpdateHarness();
        h.Updater.Offer = null;

        await h.Service.RunStartupAsync(TimeSpan.Zero);
        Assert.Equal(UpdatePhase.Idle, h.Service.Phase);

        await h.Service.CheckAsync(manual: true);
        Assert.Equal(UpdatePhase.UpToDate, h.Service.Phase);
    }

    // ---- 내려받기 ----

    [Fact]
    public async Task Auto_download_on_downloads_and_marks_ready()
    {
        var h = new UpdateHarness();

        await h.Service.RunStartupAsync(TimeSpan.Zero);

        Assert.Equal(1, h.Updater.DownloadCalls);
        Assert.Equal(UpdatePhase.Ready, h.Service.Phase);
        Assert.Equal("0.1.9", h.Service.ReadyVersion);
        Assert.Equal(100, h.Service.Progress);
    }

    [Fact]
    public async Task Auto_download_off_only_announces_availability()
    {
        var h = new UpdateHarness();
        await h.Configure(u => u with { AutoDownload = false });

        await h.Service.RunStartupAsync(TimeSpan.Zero);

        Assert.Equal(UpdatePhase.Available, h.Service.Phase);
        Assert.Equal(0, h.Updater.DownloadCalls);

        await h.Service.DownloadAsync();
        Assert.Equal(UpdatePhase.Ready, h.Service.Phase);
    }

    [Fact]
    public async Task Cancelling_a_download_returns_to_available()
    {
        var h = new UpdateHarness();
        h.Updater.DownloadGate = new TaskCompletionSource();
        await h.Service.CheckAsync(manual: true);

        var download = h.Service.DownloadAsync();
        Assert.Equal(UpdatePhase.Downloading, h.Service.Phase);
        h.Service.CancelDownload();
        await download;

        Assert.Equal(UpdatePhase.Available, h.Service.Phase);
        Assert.Equal(0, h.Updater.ApplyOnExitCalls + h.Updater.ApplyAndRestartCalls);
    }

    [Fact]
    public async Task A_failed_download_can_be_retried()
    {
        var h = new UpdateHarness();
        h.Updater.DownloadFails = new IOException("disk full");
        await h.Service.CheckAsync(manual: true);

        await h.Service.DownloadAsync();
        Assert.Equal(UpdatePhase.Error, h.Service.Phase);
        Assert.True(h.Service.ErrorWhileDownloading);

        h.Updater.DownloadFails = null;
        await h.Service.DownloadAsync();
        Assert.Equal(UpdatePhase.Ready, h.Service.Phase);
    }

    [Fact]
    public async Task An_update_downloaded_in_an_earlier_run_is_ready_without_checking()
    {
        var h = new UpdateHarness();
        h.Updater.PendingRestart = "0.1.9";

        await h.Service.RunStartupAsync(TimeSpan.Zero);

        Assert.Equal(UpdatePhase.Ready, h.Service.Phase);
        Assert.Equal(0, h.Updater.CheckCalls);
    }

    // ---- 적용 시점 ----

    [Fact]
    public async Task Restart_apply_runs_nothing_when_the_close_flow_is_cancelled()
    {
        var h = new UpdateHarness();
        await h.Service.RunStartupAsync(TimeSpan.Zero);
        h.Exit.Passes = false;

        var passed = await h.Service.ApplyAndRestartAsync();

        Assert.False(passed);
        Assert.Equal(1, h.Exit.Requests);
        Assert.Equal(0, h.Updater.ApplyAndRestartCalls);
        Assert.Equal(0, h.Updater.ApplyOnExitCalls);
        Assert.Equal(UpdateApplyMode.OnExit, h.Service.ApplyMode); // 취소했으니 예전 선택으로 돌아간다

        h.Exit.Quit(); // 나중의 평범한 종료는 다시 띄우지 않는다
        h.Service.CompleteExit();
        Assert.Equal(0, h.Updater.ApplyAndRestartCalls);
        Assert.Equal(1, h.Updater.ApplyOnExitCalls);
    }

    [Fact]
    public async Task Restart_apply_runs_once_after_the_close_flow_passes()
    {
        var h = new UpdateHarness();
        await h.Service.RunStartupAsync(TimeSpan.Zero);

        var passed = await h.Service.ApplyAndRestartAsync();

        Assert.True(passed);
        Assert.Equal(0, h.Updater.ApplyAndRestartCalls); // 닫기 흐름만 통과했다: 갱신기는 앱이 완전히 끝난 뒤에 띄운다
        h.Service.CompleteExit();
        Assert.Equal(1, h.Updater.ApplyAndRestartCalls);
        Assert.Equal(0, h.Updater.ApplyOnExitCalls);
        h.Service.CompleteExit(); // 두 번 부르지 않는다
        Assert.Equal(1, h.Updater.ApplyAndRestartCalls);
    }

    [Fact]
    public async Task Apply_on_exit_waits_for_the_exit_flow_and_never_restarts()
    {
        var h = new UpdateHarness();
        await h.Service.RunStartupAsync(TimeSpan.Zero);
        h.Service.ApplyOnExit();
        Assert.Equal(0, h.Updater.ApplyOnExitCalls); // 고른 것만으로는 적용하지 않는다

        h.Exit.Quit();
        Assert.Equal(0, h.Updater.ApplyOnExitCalls); // 종료 확인을 통과했어도 갱신기는 프로세스가 끝나기 직전에야 띄운다

        h.Service.CompleteExit();

        Assert.Equal(1, h.Updater.ApplyOnExitCalls);
        Assert.Equal(0, h.Updater.ApplyAndRestartCalls);
    }

    [Fact]
    public async Task Nothing_is_applied_unless_the_exit_flow_passed()
    {
        var h = new UpdateHarness();
        await h.Service.RunStartupAsync(TimeSpan.Zero);
        h.Service.ApplyOnExit();

        h.Service.CompleteExit(); // 닫기 흐름을 통과하지 않은 채 불려도 예약된 것이 없다

        Assert.Equal(0, h.Updater.ApplyOnExitCalls + h.Updater.ApplyAndRestartCalls);
    }

    [Fact]
    public async Task Quitting_before_an_update_is_downloaded_applies_nothing()
    {
        var h = new UpdateHarness();
        await h.Configure(u => u with { AutoDownload = false });
        await h.Service.RunStartupAsync(TimeSpan.Zero);

        h.Exit.Quit();
        h.Service.CompleteExit();
        var passed = await h.Service.ApplyAndRestartAsync(); // 받은 것이 없으면 닫기도 요청하지 않는다

        Assert.False(passed);
        Assert.Equal(0, h.Exit.Requests);
        Assert.Equal(0, h.Updater.ApplyOnExitCalls + h.Updater.ApplyAndRestartCalls);
    }

    // ---- 겹침·시계·지연 ----

    [Fact]
    public async Task A_manual_check_during_a_silent_one_takes_over_and_shows_the_result()
    {
        var h = new UpdateHarness();
        h.Updater.CheckGate = new TaskCompletionSource();
        var silent = h.Service.CheckAsync(manual: false);
        Assert.Equal(UpdatePhase.Idle, h.Service.Phase);

        var manual = h.Service.CheckAsync(manual: true);
        Assert.Equal(UpdatePhase.Checking, h.Service.Phase); // 사용자가 누른 확인은 진행 중으로 보인다
        h.Updater.CheckGate.SetResult();
        await Task.WhenAll(silent, manual);

        Assert.Equal(1, h.Updater.CheckCalls);
        Assert.Equal(UpdatePhase.Available, h.Service.Phase);
        Assert.Equal(0, h.Updater.DownloadCalls); // 사용자의 확인이라 자동으로 내려받지 않는다
    }

    [Fact]
    public async Task A_manual_check_during_the_startup_delay_stops_the_silent_one()
    {
        var h = new UpdateHarness();
        h.Updater.Offer = null;
        var startup = h.Service.RunStartupAsync(TimeSpan.FromMilliseconds(80));

        await h.Service.CheckAsync(manual: true);
        await startup;

        Assert.Equal(1, h.Updater.CheckCalls);
        Assert.Equal(UpdatePhase.UpToDate, h.Service.Phase); // 지연 뒤 조용한 확인이 결과를 지우지 않는다
    }

    [Fact]
    public async Task A_last_check_time_in_the_future_does_not_block_startup_checks()
    {
        var h = new UpdateHarness();
        await h.Configure(u => u with { LastCheckedUtc = h.Time.Now + TimeSpan.FromDays(30), AutoDownload = false });

        await h.Service.RunStartupAsync(TimeSpan.Zero);

        Assert.Equal(1, h.Updater.CheckCalls);
    }

    [Fact]
    public async Task A_hanging_server_ends_in_an_error_instead_of_waiting_forever()
    {
        var h = new UpdateHarness();
        h.Updater.CheckGate = new TaskCompletionSource(); // 끝내지 않는다
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await h.Service.CheckAsync(manual: true, cts.Token);

        Assert.Equal(UpdatePhase.Idle, h.Service.Phase); // 취소로 끝나면 확인 중 표시가 남지 않는다
    }

    [Fact]
    public async Task A_throwing_change_handler_does_not_wedge_the_state_machine()
    {
        var h = new UpdateHarness();
        await h.Configure(u => u with { AutoDownload = false });
        await h.Service.CheckAsync(manual: true);
        h.Service.Changed += (_, _) => throw new InvalidOperationException("handler bug");

        await h.Service.DownloadAsync();
        Assert.Equal(UpdatePhase.Ready, h.Service.Phase);
    }
}
