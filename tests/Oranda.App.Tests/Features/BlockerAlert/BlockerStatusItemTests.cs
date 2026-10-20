using Oranda.App.Features.BlockerAlert;
using Oranda.Core.Events;
using Oranda.Core.Monitoring;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.BlockerAlert;

/// <summary>TSK-13-02 상태 표시줄 구간: 등록(순서 7), 칩 문구·도움말, 시작 때 아무것도 하지 않음.</summary>
public class BlockerStatusItemTests
{
    private static readonly Oranda.App.Shell.ShellContributions App =
        Oranda.App.Shell.ShellContributions.Scan(typeof(BlockerStatusViewModel).Assembly);

    [Fact]
    public void Status_item_is_registered_at_order_7_and_never_collapses()
    {
        var item = Assert.Single(App.StatusBarItems, i => i.Id == "oranda.status.blocker");

        Assert.Equal(7, item.Order);
        Assert.Equal(0, item.CollapseRank);
        Assert.Equal(typeof(BlockerStatusViewModel), item.ItemType);
        Assert.NotNull(typeof(BlockerStatusViewModel).Assembly.GetType(typeof(BlockerStatusViewModel).FullName!.Replace("ViewModel", "View", StringComparison.Ordinal)));
    }

    [Fact]
    public void Item_is_hidden_and_inert_at_startup_even_without_a_container()
    {
        using var vm = new BlockerStatusViewModel(new EventBus());

        Assert.False(vm.IsVisible);
        Assert.Equal(string.Empty, vm.Text);
        Assert.Equal(BlockerAlertKind.None, vm.Kind);
    }

    [Fact]
    public void Item_without_an_event_bus_stays_hidden()
    {
        using var vm = new BlockerStatusViewModel();

        Assert.False(vm.IsVisible);
    }

    private static async Task<(BlockerStatusViewModel Vm, EventBus Bus, Guid Id, FakeTimeProvider Time, BlockerWatcher Watcher)> StartAsync(
        Func<BlockerSnapshot> snapshot, Oranda.Core.Connection.ServerCapabilities? caps = null, bool noPassword = false)
    {
        var bus = new EventBus();
        var time = new FakeTimeProvider();
        var watcher = new BlockerWatcher(bus,
            (p, _) => Task.FromResult<ISession?>(noPassword ? null : new FakeMonitorSession(p, BlockerTestData.Server(BlockerTestData.Dba))),
            (_, _, _) => Task.FromResult(snapshot()),
            time, _ => { });
        var vm = new BlockerStatusViewModel(watcher);
        var id = Guid.NewGuid();
        bus.Publish(new SessionOpened(id, BlockerTestData.Profile(), BlockerTestData.Server(caps ?? BlockerTestData.Dba), SessionPurpose.Work));
        bus.Publish(new TransactionStateChanged(id, true));
        time.Advance(TimeSpan.Zero);
        await watcher.WhenIdleAsync();
        return (vm, bus, id, time, watcher);
    }

    [Fact]
    public async Task Blocking_chip_says_how_many_sessions_and_when_the_server_will_end_this_one()
    {
        var (vm, _, _, _, watcher) = await StartAsync(() => BlockerTestData.Snapshot(3, maxIdle: 10, lastCall: 300));
        using var _ = watcher;

        Assert.True(vm.IsVisible);
        Assert.Equal(BlockerAlertKind.Blocking, vm.Kind);
        Assert.Contains("3 세션 막음", vm.Text, StringComparison.Ordinal);
        Assert.Contains("5분 뒤 서버가 이 세션을 끊음", vm.Text, StringComparison.Ordinal);
        Assert.Contains("블로킹 트리", vm.ToolTip, StringComparison.Ordinal);
        Assert.Contains("MAX_IDLE_BLOCKER_TIME", vm.ToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Long_transaction_chip_shows_minutes()
    {
        var (vm, _, _, time, watcher) = await StartAsync(() => BlockerTestData.Snapshot(0));
        using var _ = watcher;

        time.Advance(BlockerWatcher.LongTransaction);

        Assert.Equal(BlockerAlertKind.LongTransaction, vm.Kind);
        Assert.Equal("미커밋 10분", vm.Text);
    }

    [Fact]
    public async Task Gray_chip_explains_that_the_missing_permission_turned_watching_off()
    {
        var (vm, _, _, _, watcher) = await StartAsync(() => BlockerTestData.Snapshot(0), BlockerTestData.NoPrivilege);
        using var _ = watcher;

        Assert.Equal(BlockerAlertKind.Disabled, vm.Kind);
        Assert.Equal("감시 꺼짐", vm.Text);
        Assert.Equal("권한이 없어 감시 꺼짐(V$ 조회 권한 필요)", vm.ToolTip);
    }

    [Fact]
    public async Task Missing_password_and_missing_permission_have_different_tooltips()
    {
        var (noPassword, _, _, _, w1) = await StartAsync(() => BlockerTestData.Snapshot(0), noPassword: true);
        var (denied, _, _, _, w2) = await StartAsync(() => BlockerTestData.Snapshot(0), BlockerTestData.NoPrivilege);
        using var _w1 = w1;
        using var _w2 = w2;

        Assert.Equal(BlockerAlertKind.Disabled, noPassword.Kind);
        Assert.Contains("누르면 비밀번호를 넣고 켭니다", noPassword.ToolTip, StringComparison.Ordinal);
        Assert.Contains("V$", denied.ToolTip, StringComparison.Ordinal);
        Assert.NotEqual(noPassword.ToolTip, denied.ToolTip);
    }
}
