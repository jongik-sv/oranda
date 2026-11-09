using System.Reflection;
using Oranda.App.Features.BlockerAlert;
using Oranda.App.Shell.Features;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Monitoring;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.BlockerAlert;

// 소유: Tibero U2. 모니터 서비스 구현이 없는 DBMS 에서는 막고 있는 세션 감시가 모니터 세션을 열지도, 조회하지도 않는다.
public class BlockerWatcherDbmsTests
{
    private sealed class Harness : IDisposable
    {
        public Harness(Dbms dbms, DbmsServiceTable table)
        {
            Dbms = dbms;
            var provider = new TableProvider(table);
            Watcher = new BlockerWatcher(
                Bus,
                Open,
                Query,
                Time,
                hasMonitorService: profile => DbmsServiceKeys.HasImplementation(provider, profile.Dbms, DbmsServiceArea.Monitor));
        }

        public Dbms Dbms { get; }

        public EventBus Bus { get; } = new();

        public FakeTimeProvider Time { get; } = new();

        public BlockerWatcher Watcher { get; }

        public Guid WorkId { get; } = Guid.NewGuid();

        public int MonitorOpens { get; private set; }

        public int Queries { get; private set; }

        public void OpenWorkAndStartTransaction()
        {
            Bus.Publish(new SessionOpened(WorkId, BlockerTestData.Profile() with { Dbms = Dbms }, BlockerTestData.Server(BlockerTestData.Dba), SessionPurpose.Work));
            Bus.Publish(new TransactionStateChanged(WorkId, true));
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
            MonitorOpens++;
            return Task.FromResult<ISession?>(new FakeMonitorSession(profile, BlockerTestData.Server(BlockerTestData.Dba)));
        }

        private Task<BlockerSnapshot> Query(ISession monitor, long sid, CancellationToken ct)
        {
            Queries++;
            return Task.FromResult(BlockerTestData.Snapshot(0));
        }
    }

    private static DbmsServiceTable TiberoWithoutMonitor() => new();

    private static DbmsServiceTable TiberoWithMonitor()
        => new DbmsServiceTable().Add(typeof(IMonitorService), Dbms.Tibero, () => DispatchProxy.Create<IMonitorService, NullProxy>());

    [Fact]
    public async Task Dbms_without_monitor_service_never_opens_a_monitor_session_or_polls()
    {
        using var h = new Harness(Dbms.Tibero, TiberoWithoutMonitor());
        h.OpenWorkAndStartTransaction();

        await h.TickAsync(TimeSpan.FromSeconds(45));

        Assert.Equal(0, h.MonitorOpens);
        Assert.Equal(0, h.Queries);
        Assert.False(h.Watcher.HasMonitorSession);
        Assert.Equal(BlockerAlertKind.None, h.Watcher.State.Kind); // 「감시 꺼짐」 칩도 띄우지 않는다
    }

    [Fact]
    public async Task Long_uncommitted_notice_still_works_without_any_monitor_call()
    {
        using var h = new Harness(Dbms.Tibero, TiberoWithoutMonitor());
        h.OpenWorkAndStartTransaction();

        await h.TickAsync(BlockerWatcher.LongTransaction + TimeSpan.FromSeconds(1));

        Assert.Equal(BlockerAlertKind.LongTransaction, h.Watcher.State.Kind); // 앱이 세는 미커밋 시간: DB 를 부르지 않는다
        Assert.Equal(0, h.MonitorOpens);
        Assert.Equal(0, h.Queries);
    }

    [Fact]
    public async Task Dbms_with_monitor_service_polls_as_before()
    {
        using var h = new Harness(Dbms.Tibero, TiberoWithMonitor());
        h.OpenWorkAndStartTransaction();

        await h.Watcher.WhenIdleAsync();
        await h.TickAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(1, h.MonitorOpens);
        Assert.True(h.Queries >= 1);
    }

    [Fact]
    public async Task Oracle_polls_as_before_even_when_the_selector_has_no_monitor_entry()
    {
        using var h = new Harness(Dbms.Oracle, new DbmsServiceTable());
        h.OpenWorkAndStartTransaction();

        await h.Watcher.WhenIdleAsync();
        await h.TickAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(1, h.MonitorOpens);
        Assert.True(h.Queries >= 1);
    }

    private sealed class TableProvider(IDbmsServiceSelector selector) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IDbmsServiceSelector) ? selector : null;
    }

    public class NullProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}
