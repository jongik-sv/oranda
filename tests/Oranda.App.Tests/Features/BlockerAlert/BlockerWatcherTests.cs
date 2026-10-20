using Oranda.App.Features.BlockerAlert;
using Oranda.Core.Catalog;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Monitoring;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.BlockerAlert;

/// <summary>TSK-13-02 감시기: 미커밋일 때만 15초 조회, 막힌 수 표시, 10분 알림, 권한 오류 꺼짐, 접속 끊김 정리.</summary>
public class BlockerWatcherTests
{
    private sealed class Harness : IDisposable
    {
        public Harness(ServerCapabilities? caps = null)
        {
            Caps = caps ?? BlockerTestData.Dba;
            Watcher = new BlockerWatcher(Bus, Open, Query, Time, log => Logs.Add(log));
            Watcher.LongTransactionNoticed += (_, _) => Notices++;
        }

        public EventBus Bus { get; } = new();

        public FakeTimeProvider Time { get; } = new();

        public ServerCapabilities Caps { get; }

        public BlockerWatcher Watcher { get; }

        public Guid WorkId { get; } = Guid.NewGuid();

        public List<FakeMonitorSession> Monitors { get; } = [];

        public List<string> Logs { get; } = [];

        public List<long> QueriedSids { get; } = [];

        public int Queries { get; private set; }

        public int Notices { get; private set; }

        public Func<BlockerSnapshot> Next { get; set; } = () => BlockerTestData.Snapshot(0);

        public Exception? Fail { get; set; }

        public bool CredentialMissing { get; set; }

        public void OpenWork() =>
            Bus.Publish(new SessionOpened(WorkId, BlockerTestData.Profile(), BlockerTestData.Server(Caps), SessionPurpose.Work));

        /// <summary>미커밋이 시작되면 첫 조회 타이머(기한 0)가 바로 울리도록 시계를 0만큼 돌린다.</summary>
        public void Pending(bool pending)
        {
            Bus.Publish(new TransactionStateChanged(WorkId, pending));
            Time.Advance(TimeSpan.Zero);
        }

        public async Task TickAsync(TimeSpan by)
        {
            Time.Advance(by);
            await Watcher.WhenIdleAsync();
        }

        public void Dispose() => Watcher.Dispose();

        private Task<ISession?> Open(ConnectionProfile profile, CancellationToken ct)
        {
            if (CredentialMissing)
            {
                return Task.FromResult<ISession?>(null);
            }

            var s = new FakeMonitorSession(profile, BlockerTestData.Server(Caps));
            Monitors.Add(s);
            return Task.FromResult<ISession?>(s);
        }

        private Task<BlockerSnapshot> Query(ISession monitor, long sid, CancellationToken ct)
        {
            Queries++;
            QueriedSids.Add(sid);
            return Fail is { } ex ? Task.FromException<BlockerSnapshot>(ex) : Task.FromResult(Next());
        }
    }

    [Fact]
    public async Task No_query_and_no_monitor_session_while_nothing_is_uncommitted()
    {
        using var h = new Harness();
        h.OpenWork();

        await h.TickAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(0, h.Queries);
        Assert.Empty(h.Monitors);
        Assert.Equal(BlockerAlertKind.None, h.Watcher.State.Kind);
    }

    [Fact]
    public async Task Uncommitted_transaction_polls_every_15_seconds_with_my_sid_and_stops_when_committed()
    {
        using var h = new Harness();
        h.OpenWork();
        h.Pending(true);
        await h.Watcher.WhenIdleAsync();
        Assert.Equal(1, h.Queries);

        await h.TickAsync(TimeSpan.FromSeconds(14));
        Assert.Equal(1, h.Queries);
        await h.TickAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, h.Queries);
        await h.TickAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(3, h.Queries);
        Assert.All(h.QueriedSids, sid => Assert.Equal(42, sid));
        Assert.Single(h.Monitors);

        h.Pending(false);
        await h.TickAsync(TimeSpan.FromSeconds(45));
        Assert.Equal(3, h.Queries);
    }

    [Fact]
    public async Task Blocked_count_shows_and_disappears_when_released()
    {
        using var h = new Harness();
        h.Next = () => BlockerTestData.Snapshot(2);
        h.OpenWork();
        h.Pending(true);
        await h.Watcher.WhenIdleAsync();

        Assert.Equal(BlockerAlertKind.Blocking, h.Watcher.State.Kind);
        Assert.Equal(2, h.Watcher.State.BlockedCount);

        h.Next = () => BlockerTestData.Snapshot(0);
        await h.TickAsync(BlockerWatcher.PollInterval);

        Assert.Equal(BlockerAlertKind.None, h.Watcher.State.Kind);
    }

    [Fact]
    public async Task Server_idle_blocker_limit_reports_minutes_left_only_while_blocking()
    {
        using var h = new Harness();
        h.Next = () => BlockerTestData.Snapshot(1, maxIdle: 10, lastCall: 4 * 60 + 1);
        h.OpenWork();
        h.Pending(true);
        await h.Watcher.WhenIdleAsync();

        Assert.Equal(6, h.Watcher.State.MinutesUntilKilled);

        h.Next = () => BlockerTestData.Snapshot(0, maxIdle: 10, lastCall: 60);
        await h.TickAsync(BlockerWatcher.PollInterval);

        Assert.Null(h.Watcher.State.MinutesUntilKilled);
    }

    [Fact]
    public async Task Long_uncommitted_transaction_is_noticed_once_per_transaction()
    {
        using var h = new Harness();
        h.OpenWork();
        h.Pending(true);
        await h.TickAsync(TimeSpan.FromMinutes(9));
        Assert.Equal(0, h.Notices);
        Assert.Equal(BlockerAlertKind.None, h.Watcher.State.Kind);

        await h.TickAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(1, h.Notices);
        Assert.Equal(BlockerAlertKind.LongTransaction, h.Watcher.State.Kind);

        await h.TickAsync(TimeSpan.FromMinutes(20));
        Assert.Equal(1, h.Notices);

        h.Pending(false);
        Assert.Equal(BlockerAlertKind.None, h.Watcher.State.Kind);
        h.Pending(true);
        await h.TickAsync(TimeSpan.FromMinutes(10));
        Assert.Equal(2, h.Notices);
    }

    [Fact]
    public async Task Blocking_chip_wins_over_long_transaction_chip()
    {
        using var h = new Harness();
        h.Next = () => BlockerTestData.Snapshot(1);
        h.OpenWork();
        h.Pending(true);
        await h.TickAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(BlockerAlertKind.Blocking, h.Watcher.State.Kind);
    }

    [Theory]
    [InlineData("ORA-00942: table or view does not exist")]
    [InlineData("ORA-01031: insufficient privileges")]
    public async Task Privilege_error_turns_watching_off_with_gray_chip_and_one_log_line(string message)
    {
        using var h = new Harness();
        h.Fail = new InvalidOperationException(message);
        h.OpenWork();
        h.Pending(true);
        await h.Watcher.WhenIdleAsync();

        Assert.Equal(BlockerAlertKind.Disabled, h.Watcher.State.Kind);
        Assert.Equal(BlockerDisabledReason.NoPrivilege, h.Watcher.State.Reason);
        Assert.Single(h.Logs);

        await h.TickAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(1, h.Queries);
        Assert.Single(h.Logs);
        Assert.False(h.Watcher.HasMonitorSession);
        Assert.Equal(1, h.Monitors[0].Disposed);
    }

    [Fact]
    public async Task Account_without_v_views_is_off_from_the_start_without_opening_a_monitor_session()
    {
        using var h = new Harness(BlockerTestData.NoPrivilege);
        h.OpenWork();
        h.Pending(true);
        await h.TickAsync(TimeSpan.FromMinutes(1));

        Assert.Equal(BlockerAlertKind.Disabled, h.Watcher.State.Kind);
        Assert.Equal(0, h.Queries);
        Assert.Empty(h.Monitors);
    }

    [Fact]
    public async Task Gray_chip_is_shown_only_the_first_time_for_a_connection()
    {
        using var h = new Harness(BlockerTestData.NoPrivilege);
        h.OpenWork();
        h.Pending(true);
        Assert.Equal(BlockerAlertKind.Disabled, h.Watcher.State.Kind);

        h.Pending(false);
        h.Pending(true);

        Assert.Equal(BlockerAlertKind.None, h.Watcher.State.Kind);
        await h.TickAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(0, h.Queries);
    }

    [Fact]
    public async Task Missing_saved_password_turns_watching_off_without_prompting()
    {
        using var h = new Harness();
        h.CredentialMissing = true;
        h.OpenWork();
        h.Pending(true);
        await h.Watcher.WhenIdleAsync();

        Assert.Equal(BlockerAlertKind.Disabled, h.Watcher.State.Kind);
        Assert.Equal(BlockerDisabledReason.NoCredential, h.Watcher.State.Reason);
        Assert.Equal(0, h.Queries);
    }

    [Fact]
    public async Task Server_without_the_query_variant_hides_the_chip_and_stops_polling()
    {
        using var h = new Harness();
        h.Fail = new CatalogQueryNotFoundException("session.my_blocking", 11);
        h.OpenWork();
        h.Pending(true);
        await h.Watcher.WhenIdleAsync();

        Assert.Equal(BlockerAlertKind.None, h.Watcher.State.Kind);
        await h.TickAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(1, h.Queries);
    }

    [Fact]
    public async Task Other_failures_are_retried_on_the_next_tick()
    {
        using var h = new Harness();
        h.Fail = new TimeoutException("network");
        h.OpenWork();
        h.Pending(true);
        await h.Watcher.WhenIdleAsync();
        h.Fail = null;
        h.Next = () => BlockerTestData.Snapshot(3);

        await h.TickAsync(BlockerWatcher.PollInterval);

        Assert.Equal(BlockerAlertKind.Blocking, h.Watcher.State.Kind);
        Assert.Equal(2, h.Monitors.Count);
        Assert.Equal(1, h.Monitors[0].Disposed);
    }

    [Fact]
    public async Task Monitor_session_closes_after_the_idle_grace_and_not_before()
    {
        using var h = new Harness();
        h.OpenWork();
        h.Pending(true);
        await h.Watcher.WhenIdleAsync();
        h.Pending(false);

        await h.TickAsync(BlockerWatcher.MonitorIdleClose - TimeSpan.FromSeconds(1));
        Assert.True(h.Watcher.HasMonitorSession);

        await h.TickAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        Assert.False(h.Watcher.HasMonitorSession);
        Assert.Equal(1, h.Monitors[0].Disposed);
    }

    [Fact]
    public async Task New_uncommitted_transaction_within_the_grace_reuses_the_monitor_session()
    {
        using var h = new Harness();
        h.OpenWork();
        h.Pending(true);
        await h.Watcher.WhenIdleAsync();
        h.Pending(false);
        await h.TickAsync(TimeSpan.FromSeconds(30));
        h.Pending(true);
        await h.Watcher.WhenIdleAsync();

        await h.TickAsync(TimeSpan.FromMinutes(2));

        Assert.Single(h.Monitors);
        Assert.Equal(0, h.Monitors[0].Disposed);
    }

    [Fact]
    public async Task Disconnect_closes_the_monitor_session_and_clears_the_chip()
    {
        using var h = new Harness();
        h.Next = () => BlockerTestData.Snapshot(1);
        h.OpenWork();
        h.Pending(true);
        await h.Watcher.WhenIdleAsync();
        Assert.Equal(BlockerAlertKind.Blocking, h.Watcher.State.Kind);

        h.Bus.Publish(new SessionClosed(h.WorkId));
        await Task.Delay(50);

        Assert.Equal(BlockerAlertKind.None, h.Watcher.State.Kind);
        Assert.False(h.Watcher.HasMonitorSession);
        Assert.Equal(1, h.Monitors[0].Disposed);
        var queries = h.Queries;
        await h.TickAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(queries, h.Queries);
    }

    [Fact]
    public async Task Broken_session_is_treated_like_a_disconnect()
    {
        using var h = new Harness();
        h.OpenWork();
        h.Pending(true);
        await h.Watcher.WhenIdleAsync();

        h.Bus.Publish(new SessionBroken(h.WorkId, true, null));
        await Task.Delay(50);

        Assert.False(h.Watcher.HasMonitorSession);
    }

    [Fact]
    public async Task Other_sessions_and_monitor_sessions_do_not_trigger_watching()
    {
        using var h = new Harness();
        h.OpenWork();
        var other = Guid.NewGuid();
        h.Bus.Publish(new SessionOpened(other, BlockerTestData.Profile(), BlockerTestData.Server(h.Caps), SessionPurpose.Dedicated));
        h.Bus.Publish(new TransactionStateChanged(other, true));

        await h.TickAsync(TimeSpan.FromMinutes(1));

        Assert.Equal(0, h.Queries);
    }

    [Fact]
    public async Task Clicking_the_gray_chip_asks_for_the_password_and_starts_watching()
    {
        using var h = new Harness();
        h.CredentialMissing = true;
        h.Next = () => BlockerTestData.Snapshot(1);
        h.OpenWork();
        h.Pending(true);
        await h.Watcher.WhenIdleAsync();
        Assert.Equal(BlockerDisabledReason.NoCredential, h.Watcher.State.Reason);

        var enabled = await h.Watcher.EnableAsync((p, _) => Task.FromResult<ISession?>(new FakeMonitorSession(p, BlockerTestData.Server(h.Caps))));
        h.Time.Advance(TimeSpan.Zero);
        await h.Watcher.WhenIdleAsync();

        Assert.True(enabled);
        Assert.Equal(BlockerAlertKind.Blocking, h.Watcher.State.Kind);
        Assert.Equal(1, h.Queries);
    }

    [Fact]
    public async Task Cancelling_the_password_prompt_keeps_the_gray_chip()
    {
        using var h = new Harness();
        h.CredentialMissing = true;
        h.OpenWork();
        h.Pending(true);
        await h.Watcher.WhenIdleAsync();

        var enabled = await h.Watcher.EnableAsync((_, _) => Task.FromResult<ISession?>(null));

        Assert.False(enabled);
        Assert.Equal(BlockerAlertKind.Disabled, h.Watcher.State.Kind);
        Assert.Equal(0, h.Queries);
    }

    [Fact]
    public async Task Permission_denied_chip_cannot_be_turned_on_by_a_password()
    {
        using var h = new Harness(BlockerTestData.NoPrivilege);
        h.OpenWork();
        h.Pending(true);

        var enabled = await h.Watcher.EnableAsync((p, _) => Task.FromResult<ISession?>(new FakeMonitorSession(p, BlockerTestData.Server(h.Caps))));

        Assert.False(enabled);
        Assert.Equal(BlockerDisabledReason.NoPrivilege, h.Watcher.State.Reason);
    }
}
