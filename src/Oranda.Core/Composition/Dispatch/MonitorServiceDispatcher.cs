using Oranda.Core.Execution;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>
/// 세션의 DBMS 로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(IMonitorService), Dbms = …)]</c>.
/// DBMS 는 모니터 세션(<c>monitor</c>)의 프로필로 고른다. 기본 구현이 있는 멤버도 고른 구현의 것을 그대로 부른다.
/// </summary>
[OrandaService(typeof(IMonitorService))]
public sealed class MonitorServiceDispatcher : IMonitorService
{
    private readonly IDbmsServiceSelector _selector;

    public MonitorServiceDispatcher(IDbmsServiceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    public Task<IReadOnlyList<SessionRow>> GetSessionsAsync(ISession monitor, SessionFilter f, CancellationToken ct) =>
        _selector.For<IMonitorService>(monitor).GetSessionsAsync(monitor, f, ct);

    public Task<BlockingTree> GetBlockingTreeAsync(ISession monitor, CancellationToken ct) =>
        _selector.For<IMonitorService>(monitor).GetBlockingTreeAsync(monitor, ct);

    public Task<SessionDetail> GetSessionDetailAsync(ISession monitor, SessionKey target, CancellationToken ct) =>
        _selector.For<IMonitorService>(monitor).GetSessionDetailAsync(monitor, target, ct);

    public Task KillAsync(ISession monitor, SessionKey target, bool immediate, CancellationToken ct) =>
        _selector.For<IMonitorService>(monitor).KillAsync(monitor, target, immediate, ct);

    public SqlStatement PreviewKill(ISession monitor, SessionKey target, bool immediate) =>
        _selector.For<IMonitorService>(monitor).PreviewKill(monitor, target, immediate);

    public Task<string?> SetTraceAsync(ISession monitor, SessionKey target, bool enable, TraceOptions options, CancellationToken ct) =>
        _selector.For<IMonitorService>(monitor).SetTraceAsync(monitor, target, enable, options, ct);

    public Task<IReadOnlyList<TopSqlRow>> GetTopSqlAsync(ISession monitor, TopSqlCriteria c, CancellationToken ct) =>
        _selector.For<IMonitorService>(monitor).GetTopSqlAsync(monitor, c, ct);

    public Task<SqlDetail> GetSqlDetailAsync(ISession monitor, CursorRef cursor, CancellationToken ct) =>
        _selector.For<IMonitorService>(monitor).GetSqlDetailAsync(monitor, cursor, ct);

    public Task<BlockerSnapshot> GetMyBlockingAsync(ISession monitor, long workSid, CancellationToken ct) =>
        _selector.For<IMonitorService>(monitor).GetMyBlockingAsync(monitor, workSid, ct);

    public Task<LockSnapshot> GetLocksAsync(ISession monitor, LockFilter filter, CancellationToken ct) =>
        _selector.For<IMonitorService>(monitor).GetLocksAsync(monitor, filter, ct);

    public Task<IReadOnlyList<BlockerImpact>> GetBlockerImpactAsync(ISession monitor, CancellationToken ct) =>
        _selector.For<IMonitorService>(monitor).GetBlockerImpactAsync(monitor, ct);

    public Task<LockedRowInfo?> GetLockedRowAsync(ISession monitor, SessionKey waiter, CancellationToken ct) =>
        _selector.For<IMonitorService>(monitor).GetLockedRowAsync(monitor, waiter, ct);

    public Task<IReadOnlyList<DdlLockRow>> GetDdlLocksAsync(ISession monitor, CancellationToken ct) =>
        _selector.For<IMonitorService>(monitor).GetDdlLocksAsync(monitor, ct);

    public Task<IReadOnlyList<WaitingBind>> GetWaitingBindsAsync(ISession monitor, WaitingBindTarget target, CancellationToken ct) =>
        _selector.For<IMonitorService>(monitor).GetWaitingBindsAsync(monitor, target, ct);

    public Task<IReadOnlyList<IdleTransaction>> GetIdleTransactionsAsync(ISession monitor, int minIdleSeconds, CancellationToken ct) =>
        _selector.For<IMonitorService>(monitor).GetIdleTransactionsAsync(monitor, minIdleSeconds, ct);

    public Task<IReadOnlyList<DeadlockEvent>> GetRecentDeadlocksAsync(ISession monitor, int max, CancellationToken ct) =>
        _selector.For<IMonitorService>(monitor).GetRecentDeadlocksAsync(monitor, max, ct);
}
