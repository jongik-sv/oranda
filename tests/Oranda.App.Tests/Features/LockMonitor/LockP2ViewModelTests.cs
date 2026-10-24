using Oranda.App.Features.LockMonitor;
using Oranda.Core.Connection;
using Oranda.Core.Monitoring;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.LockMonitor;

/// <summary>TSK-05-04 화면 모델: 방치 트랜잭션·데드락 탭, 이력 띠, 임계치 알림·스냅샷·칩 알림, 순환 띠, 알림 설정, 상태(접속·권한).</summary>
public sealed class LockP2ViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-lockvm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>프로필마다 이력이 따로 남으므로 시험마다 새 프로필을 쓴다.</summary>
    private static LockHarness New(ServerCapabilities? caps = null) =>
        new(caps, new ConnectionProfile("MESPRD", "mesadm", "h:1521/s", Environment: ConnectionEnvironment.Production) { Id = Guid.NewGuid() });

    // ---- 상태 정보(트리·순위·잠금) ----------------------------------------------------------------

    [Fact]
    public async Task Tree_and_rank_show_the_server_status_instead_of_guessing_from_sql()
    {
        var h = New();
        h.Monitor.Custom = new LockSnapshot(FakeLockMonitor.Rows(), new BlockingTree(
        [
            new BlockingNode(FakeLockMonitor.H3, [new BlockingNode(FakeLockMonitor.W5, []) { Status = "ACTIVE", SecondsInWait = 8 }])
            {
                Username = "HR", Status = "INACTIVE", // 현재 SQL 이 없어도 서버가 말한 상태
            },
        ]), FakeLockMonitor.Now);
        await h.Vm.StartAsync();
        Assert.Equal("INACTIVE", h.Vm.TreeRows[0].StatusText);
        Assert.True(h.Vm.TreeRows[0].IsInactive);
        Assert.Equal("ACTIVE", h.Vm.TreeRows[1].StatusText);
        Assert.Equal("INACTIVE", h.Vm.Impacts[0].Cells[3]);
        Assert.Equal("ACTIVE", h.Vm.Impacts[2].Cells[3]);
    }

    [Fact]
    public async Task Status_falls_back_to_the_lock_list_and_then_to_a_dash()
    {
        var h = New();
        var rows = FakeLockMonitor.Rows();
        h.Monitor.Custom = new LockSnapshot(rows, new BlockingTree(
        [
            new BlockingNode(FakeLockMonitor.H1, [new BlockingNode(new SessionKey(9, 9, 9), []) { SecondsInWait = 1 }]),
        ]), FakeLockMonitor.Now);
        await h.Vm.StartAsync();
        Assert.Equal("INACTIVE", h.Vm.TreeRows[0].StatusText); // LockRow.Status
        Assert.Equal(LockText.Dash, h.Vm.TreeRows[1].StatusText); // 어디에도 없음
        Assert.False(h.Vm.TreeRows[1].IsInactive);
    }

    // ---- 방치 트랜잭션 ---------------------------------------------------------------------------

    [Fact]
    public async Task Idle_tab_reads_only_when_shown_with_the_60_second_filter_and_switches_the_status_line()
    {
        var h = New();
        await h.Vm.StartAsync();
        Assert.Equal(0, h.Monitor.IdleCalls);

        h.Vm.TopTabIndex = LockMonitorViewModel.IdleTab;
        await Settle(h);
        Assert.Equal(1, h.Monitor.IdleCalls);
        Assert.Equal(60, h.Monitor.LastMinIdle);
        Assert.Equal(4, h.Vm.IdleTransactions.Count);
        Assert.Equal("4", h.Vm.IdleCountText);
        Assert.Equal("방치 트랜잭션 4 · 그중 차단 중 2", h.Vm.StatusText);
        Assert.Contains("이력 1 점", h.Vm.StatusRight, StringComparison.Ordinal);

        h.Vm.IdleLongOnly = false;
        await Settle(h);
        Assert.Equal(0, h.Monitor.LastMinIdle);

        // 새로 고침마다 다시 읽는다(경과가 바뀐다).
        await h.Vm.RefreshAsync();
        Assert.Equal(3, h.Monitor.IdleCalls);

        h.Vm.TopTabIndex = LockMonitorViewModel.TreeTab;
        Assert.StartsWith("보유자 3 · 대기 5", h.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Idle_empty_list_and_no_service_support_show_the_empty_screen()
    {
        var h = New();
        h.Monitor.Idle = [];
        await h.Vm.StartAsync();
        h.Vm.TopTabIndex = LockMonitorViewModel.IdleTab;
        await Settle(h);
        Assert.False(h.Vm.HasIdleTransactions);
        Assert.Equal(string.Empty, h.Vm.IdleCountText);
        Assert.Equal("방치 트랜잭션 0", h.Vm.StatusText);
    }

    [Fact]
    public async Task Picking_an_idle_session_shows_its_last_sql_and_a_tool_hint()
    {
        var h = New();
        await h.Vm.StartAsync();
        h.Vm.TopTabIndex = LockMonitorViewModel.IdleTab;
        await Settle(h);
        h.Vm.SelectedKey = FakeLockMonitor.H2;
        await Settle(h);

        Assert.Equal("2·0644 SCOTT · INACTIVE 1,018 초 · 트랜잭션 17 분", h.Vm.HolderCaption);
        Assert.Equal("사람이 쓰는 도구(SQL Developer)에서 고친 뒤 커밋하지 않은 것으로 보입니다.", h.Vm.IdleCallout);
        Assert.True(h.Vm.HasIdleCallout);
        Assert.False(h.Vm.HolderHasNoCurrentSql);
        Assert.StartsWith("-- prev_sql_id 1m8c2v0d9s4tq\n", h.Vm.HolderSql, StringComparison.Ordinal);
        Assert.Null(h.Vm.WaiterSql);

        // 종료 확인 창은 방치 목록의 사용자·머신·프로그램을 쓴다.
        var kill = h.Vm.BuildKillRequest(disconnect: false)!;
        Assert.Contains("SCOTT", kill.ToText(), StringComparison.Ordinal);

        // 트리 탭으로 돌아가면 보유자·대기자 모양으로.
        h.Vm.TopTabIndex = LockMonitorViewModel.TreeTab;
        await Settle(h);
        Assert.Null(h.Vm.IdleCallout);
        Assert.StartsWith("보유자 · 2·0644 SCOTT · INACTIVE", h.Vm.HolderCaption, StringComparison.Ordinal);
    }

    // ---- 데드락 기록 ------------------------------------------------------------------------------

    [Fact]
    public async Task Deadlock_tab_lists_the_last_24_hours_and_reads_once_per_refresh()
    {
        var h = New();
        await h.Vm.StartAsync();
        h.Vm.TopTabIndex = LockMonitorViewModel.DeadlockTab;
        await Settle(h);
        Assert.Equal(1, h.Monitor.DeadlockCalls);
        Assert.Equal(2, h.Vm.Deadlocks.Count); // 이틀 전 것은 뺀다
        Assert.Equal("2", h.Vm.DeadlockCountText);
        Assert.Contains("최근 24 시간 · 읽기 전용 · 2건", h.Vm.DeadlockSummary, StringComparison.Ordinal);
        Assert.Equal("MESPRD2_ora_88412.trc", h.Vm.Deadlocks[0].TraceFile);
        Assert.False(h.Vm.ShowNoDeadlocks);

        await h.Vm.RefreshKeepingDetailAsync(); // 자동 새로 고침은 다시 읽지 않는다
        Assert.Equal(1, h.Monitor.DeadlockCalls);
        await h.Vm.RefreshAsync();
        Assert.Equal(2, h.Monitor.DeadlockCalls);

        string? opened = null;
        h.Vm.OpenEditor = t => opened = t;
        h.Vm.OpenTextInEditor(h.Vm.Deadlocks[0].FullText, "데드락 기록");
        Assert.Contains("ORA-00060", opened, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deadlock_tab_on_11g_explains_instead_of_reading()
    {
        var caps = LockHarness.Dba;
        var h = new LockHarness(caps);
        var session = new FakeLockSession(new ServerInfo("11.2.0.4.0", "XE", "h", "XE", "XE", null, false, 1, caps));
        var vm = new LockMonitorViewModel(h.Monitor, _ => Task.FromResult<Oranda.Core.Sessions.ISession?>(session))
        {
            PostToUi = a => a(),
            StartAutoRefresh = false,
        };
        await vm.StartAsync();
        vm.TopTabIndex = LockMonitorViewModel.DeadlockTab;
        await Task.Yield();
        Assert.Equal(0, h.Monitor.DeadlockCalls);
        Assert.True(vm.DeadlockUnsupported);
        Assert.StartsWith("11g 에서는", vm.DeadlockNotice, StringComparison.Ordinal);
        Assert.False(vm.ShowNoDeadlocks);
        Assert.False(vm.HasDeadlocks);
    }

    [Fact]
    public async Task No_deadlocks_shows_the_empty_screen()
    {
        var h = New();
        h.Monitor.DeadlockEvents = [];
        await h.Vm.StartAsync();
        h.Vm.TopTabIndex = LockMonitorViewModel.DeadlockTab;
        await Settle(h);
        Assert.True(h.Vm.ShowNoDeadlocks);
        Assert.Equal(string.Empty, h.Vm.DeadlockCountText);
    }

    // ---- 순환 띠 --------------------------------------------------------------------------------

    [Fact]
    public async Task Circular_wait_in_the_lock_list_shows_the_band_and_tree_button_selects_it()
    {
        var h = New();
        var a = new SessionKey(1, 388, 11);
        var b = new SessionKey(2, 1903, 22);
        h.Monitor.Custom = new LockSnapshot(
        [
            new LockRow(a, "TX") { RequestedMode = 6, BlockingSession = b, Username = "MES_RPT", Event = "enq: TX - row lock contention" },
            new LockRow(b, "TX") { RequestedMode = 6, BlockingSession = a, Username = "MES_BATCH", Event = "enq: TX - row lock contention" },
        ],
        new BlockingTree([new BlockingNode(a, [new BlockingNode(b, []) { SecondsInWait = 2 }])]), FakeLockMonitor.Now);
        await h.Vm.StartAsync();
        Assert.True(h.Vm.HasCycle);
        Assert.StartsWith("1·0388 → 2·1903 → 1·0388. Oracle 이 곧 한쪽 문장을 ORA-00060 으로 되돌립니다", h.Vm.CycleText, StringComparison.Ordinal);

        h.Vm.TopTabIndex = LockMonitorViewModel.RankTab;
        h.Vm.ShowCycleInTree();
        Assert.Equal(LockMonitorViewModel.TreeTab, h.Vm.TopTabIndex);
        Assert.Equal(a, h.Vm.SelectedKey);

        // 순환이 풀리면 띠가 사라진다.
        h.Monitor.Custom = null;
        await h.Vm.RefreshAsync();
        Assert.False(h.Vm.HasCycle);
        Assert.Empty(h.Vm.Cycles);
    }

    // ---- 이력 띠 --------------------------------------------------------------------------------

    [Fact]
    public async Task Each_refresh_adds_a_history_point_and_the_legend_follows()
    {
        var h = New();
        await h.Vm.StartAsync();
        Assert.Equal(1, h.Vm.History!.Count);
        Assert.Equal(new LockHistoryPoint(FakeLockMonitor.Now, 5, 287), h.Vm.HistoryPoints[0]);
        Assert.Equal("5 명", h.Vm.HistoryWaitersText);
        Assert.Equal("287 초", h.Vm.HistoryMaxWaitText);
        Assert.True(h.Vm.HistoryOverThreshold); // 기준 120 초
        Assert.Equal("· 기준 120 초 ┄", h.Vm.HistoryThresholdText);
        Assert.Equal("최근 30 분", h.Vm.HistoryLabel);
        Assert.Equal(120, h.Vm.ChartThreshold);

        h.Monitor.Quiet = true;
        await h.Vm.RefreshAsync();
        Assert.Equal(2, h.Vm.HistoryPoints.Count);
        Assert.Equal("0 명", h.Vm.HistoryWaitersText);
        Assert.StartsWith("(최고 5 · ", h.Vm.HistoryPeakText, StringComparison.Ordinal);
        Assert.False(h.Vm.HistoryOverThreshold);

        h.Vm.AlertEnabled = false;
        Assert.Equal("· 알림 꺼짐", h.Vm.HistoryThresholdText);
        Assert.Equal(0, h.Vm.ChartThreshold);
    }

    [Fact]
    public async Task History_survives_closing_and_reopening_the_same_connection()
    {
        var h = New();
        await h.Vm.StartAsync();
        await h.Vm.RefreshAsync();
        await h.Vm.StopAsync();
        Assert.Equal(2, h.Vm.History!.Count);
        await h.Vm.StartAsync();
        Assert.Equal(3, h.Vm.HistoryPoints.Count);
    }

    [Fact]
    public async Task History_file_is_written_only_when_turned_on()
    {
        var h = New();
        h.Vm.HistoryFolder = _dir;
        h.Vm.AlertEnabled = false; // 스냅샷도 같은 폴더에 쓰므로 끈다
        await h.Vm.StartAsync();
        await Task.Delay(100);
        Assert.False(Directory.Exists(_dir));

        h.Vm.SaveHistoryFile = true;
        await h.Vm.RefreshAsync();
        var file = await WaitForFileAsync("history-*.csv");
        Assert.Contains(",5,287", File.ReadAllText(file), StringComparison.Ordinal);
    }

    // ---- 임계치 알림 ------------------------------------------------------------------------------

    [Fact]
    public async Task Crossing_the_threshold_raises_one_alert_saves_a_snapshot_and_tells_the_status_chip()
    {
        var h = New();
        var published = new List<LockThresholdChanged>();
        var raised = new List<LockAlert>();
        h.Vm.HistoryFolder = _dir;
        h.Vm.PublishThreshold = published.Add;
        h.Vm.AlertDuration = Timeout.InfiniteTimeSpan;
        h.Vm.AlertRaised += (_, a) => raised.Add(a);

        await h.Vm.StartAsync();

        var alert = Assert.Single(raised);
        Assert.Equal(FakeLockMonitor.H1, alert.Holder);
        Assert.Equal(3, alert.Waiters);
        Assert.Equal(287, alert.MaxWaitSeconds);
        Assert.Same(alert, h.Vm.ActiveAlert);
        Assert.True(h.Vm.HasActiveAlert);
        Assert.True(h.Vm.HasAlertSnapshot);
        Assert.Contains("1·1287 MES_APP 이(가) 3명을 막고 있고, 가장 오래 기다린 세션이 287 초째입니다.", h.Vm.ActiveAlertMessage, StringComparison.Ordinal);
        var snapshot = File.ReadAllText(alert.SnapshotPath!);
        Assert.Contains("== 보유자 1·1287 MES_APP · INACTIVE", snapshot, StringComparison.Ordinal);
        Assert.Contains("SET EVT_STAT = 'DONE'", snapshot, StringComparison.Ordinal); // 보유자 prev SQL
        Assert.Contains("SET EVT_STAT = 'RUN'", snapshot, StringComparison.Ordinal); // 대기자 SQL
        Assert.Contains("== 대기자 2·0388 MES_RPT", snapshot, StringComparison.Ordinal); // 손자 대기자

        var chip = Assert.Single(published);
        Assert.True(chip.Exceeded);
        Assert.Equal(3, chip.Waiters);
        Assert.Equal(287, chip.MaxWaitSeconds);

        // 같은 보유자는 다시 띄우지 않는다(칩 갱신만).
        await h.Vm.RefreshAsync();
        Assert.Single(raised);
        Assert.Equal(2, published.Count);

        // 대기가 풀리면 칩을 내린다. 한 번만.
        h.Monitor.Quiet = true;
        await h.Vm.RefreshAsync();
        await h.Vm.RefreshAsync();
        Assert.Equal(3, published.Count);
        Assert.False(published[^1].Exceeded);

        // 알림 닫기·대기 트리 보기.
        h.Monitor.Quiet = false;
        await h.Vm.RefreshAsync();
        Assert.Equal(2, raised.Count);
        var front = 0;
        h.Vm.BringToFront = () =>
        {
            front++;
            return Task.CompletedTask;
        };
        h.Vm.TopTabIndex = LockMonitorViewModel.IdleTab;
        await h.Vm.ShowAlertHolderAsync();
        Assert.Equal(1, front);
        Assert.Null(h.Vm.ActiveAlert);
        Assert.Equal(LockMonitorViewModel.TreeTab, h.Vm.TopTabIndex);
        Assert.Equal(FakeLockMonitor.H1, h.Vm.SelectedKey);
    }

    [Fact]
    public async Task Alert_without_snapshot_option_or_folder_writes_nothing_and_closes_after_its_time()
    {
        var h = New();
        h.Vm.SaveSnapshot = false;
        h.Vm.HistoryFolder = _dir;
        h.Vm.AlertDuration = TimeSpan.FromMilliseconds(50);
        await h.Vm.StartAsync();
        Assert.NotNull(h.Vm.ActiveAlert);
        Assert.Null(h.Vm.ActiveAlert!.SnapshotPath);
        Assert.False(Directory.Exists(_dir));
        for (var i = 0; i < 100 && h.Vm.ActiveAlert is not null; i++)
        {
            await Task.Delay(20);
        }

        Assert.Null(h.Vm.ActiveAlert);
    }

    [Fact]
    public async Task Higher_threshold_or_turned_off_alert_stays_quiet_and_stop_clears_the_chip()
    {
        var h = New();
        var published = new List<LockThresholdChanged>();
        h.Vm.PublishThreshold = published.Add;
        h.Vm.AlertWaitSeconds = 300;
        h.Vm.AlertWaiters = 4;
        await h.Vm.StartAsync();
        Assert.Null(h.Vm.ActiveAlert); // 287 초 < 300, 3 명 < 4
        Assert.Empty(published);

        h.Vm.AlertWaiters = 3;
        await h.Vm.RefreshAsync();
        Assert.NotNull(h.Vm.ActiveAlert);
        Assert.True(published[^1].Exceeded);

        h.Vm.AlertEnabled = false; // 끄면 칩도 내린다
        Assert.False(published[^1].Exceeded);
        h.Vm.DismissAlert();
        await h.Vm.RefreshAsync();
        Assert.Null(h.Vm.ActiveAlert);

        h.Vm.AlertEnabled = true;
        await h.Vm.RefreshAsync();
        Assert.True(published[^1].Exceeded);
        await h.Vm.StopAsync();
        Assert.False(published[^1].Exceeded);
    }

    // ---- 알림 설정 -------------------------------------------------------------------------------

    [Fact]
    public void Settings_apply_without_saving_clamp_ranges_and_save_on_change()
    {
        var h = New();
        var saved = new List<LockAlertSettings>();
        h.Vm.SaveSettings = saved.Add;
        h.Vm.ApplySettings(new LockAlertSettings { Enabled = false, WaitSeconds = 0, Waiters = 5000, HistoryMinutes = 1, SaveHistoryFile = true, NotifyWhenHidden = false });
        Assert.Empty(saved);
        Assert.False(h.Vm.AlertEnabled);
        Assert.Equal(1, h.Vm.AlertWaitSeconds);
        Assert.Equal(1000, h.Vm.AlertWaiters);
        Assert.Equal(5, h.Vm.HistoryMinutes);
        Assert.Equal("최근 5 분", h.Vm.HistoryLabel);
        Assert.True(h.Vm.SaveHistoryFile);
        Assert.False(h.Vm.NotifyWhenHidden);

        h.Vm.AlertWaitSeconds = 60;
        var last = saved[^1];
        Assert.Equal(60, last.WaitSeconds);
        Assert.Equal(1000, last.Waiters);
        Assert.False(last.Enabled);
        h.Vm.HistoryMinutes = 9999;
        Assert.Equal(720, saved[^1].HistoryMinutes);

        var defaults = new LockAlertSettings();
        Assert.True(defaults.Enabled);
        Assert.Equal(120, defaults.WaitSeconds);
        Assert.Equal(5, defaults.Waiters);
        Assert.True(defaults.SaveSnapshot);
        Assert.Equal(30, defaults.HistoryMinutes);
        Assert.False(defaults.SaveHistoryFile);
    }

    [Fact]
    public async Task History_strip_can_be_hidden_and_history_minutes_trim_the_buffer()
    {
        var h = New();
        await h.Vm.StartAsync();
        Assert.True(h.Vm.ShowHistoryStrip);
        h.Vm.ShowHistoryStrip = false;
        Assert.False(h.Vm.ShowHistoryStrip);

        h.Vm.HistoryMinutes = 5;
        Assert.Equal(TimeSpan.FromMinutes(5), h.Vm.History!.Retention);
    }

    // ---- 상태 화면 -------------------------------------------------------------------------------

    [Fact]
    public async Task Without_privilege_or_connection_nothing_is_recorded_or_alerted()
    {
        var denied = New(LockHarness.NoPrivilege);
        var published = new List<LockThresholdChanged>();
        denied.Vm.PublishThreshold = published.Add;
        await denied.Vm.StartAsync();
        Assert.Equal(LockViewState.Denied, denied.Vm.State);
        Assert.Null(denied.Vm.History);
        Assert.Null(denied.Vm.ActiveAlert);
        Assert.Empty(published);

        var none = new LockMonitorViewModel(new FakeLockMonitor(), _ => Task.FromResult<Oranda.Core.Sessions.ISession?>(null)) { PostToUi = a => a() };
        await none.StartAsync();
        Assert.Equal(LockViewState.NoConnection, none.State);
        await none.LoadIdleTransactionsAsync();
        await none.LoadDeadlocksAsync();
        Assert.False(none.HasIdleTransactions);
    }

    [Fact]
    public async Task Read_failure_keeps_history_and_does_not_alert_again()
    {
        var h = New();
        h.Vm.AlertDuration = Timeout.InfiniteTimeSpan;
        await h.Vm.StartAsync();
        var count = h.Vm.History!.Count;
        h.Vm.DismissAlert();
        h.Monitor.Fail = new InvalidOperationException("ORA-03113: end-of-file on communication channel");
        await h.Vm.RefreshAsync();
        Assert.True(h.Vm.HasError);
        Assert.Equal(count, h.Vm.History.Count);
        Assert.Null(h.Vm.ActiveAlert);
    }

    private static async Task Settle(LockHarness h)
    {
        // 탭·고름이 바뀌면 화면 모델이 읽기를 띄운다(게이트 차례). 끝날 때까지 몇 번 양보한다.
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(5);
        }

        await h.Vm.LoadDetailAsync();
    }

    private async Task<string> WaitForFileAsync(string pattern)
    {
        for (var i = 0; i < 100; i++)
        {
            if (Directory.Exists(_dir) && Directory.GetFiles(_dir, pattern).FirstOrDefault() is { } f)
            {
                try
                {
                    _ = File.ReadAllText(f);
                    return f;
                }
                catch (IOException)
                {
                    // 아직 쓰는 중.
                }
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(pattern);
    }
}
