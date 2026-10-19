using Oranda.Core.Composition;
using Oranda.Core.Monitoring;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Monitoring;

/// <summary>세션 모니터(목록·델타·블로킹 트리·상세·Kill·트레이스). Top SQL 부분은 .TopSql.cs(TSK-09-02). 소유: TSK-09-01. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(IMonitorService))]
internal sealed partial class OracleMonitorService : IMonitorService
{
    public Task<IReadOnlyList<SessionRow>> GetSessionsAsync(ISession monitor, SessionFilter f, CancellationToken ct)
        => throw new NotImplementedException("TSK-09-01");

    public Task<BlockingTree> GetBlockingTreeAsync(ISession monitor, CancellationToken ct)
        => throw new NotImplementedException("TSK-09-01");

    public Task<SessionDetail> GetSessionDetailAsync(ISession monitor, SessionKey target, CancellationToken ct)
        => throw new NotImplementedException("TSK-09-01");

    public Task KillAsync(ISession monitor, SessionKey target, bool immediate, CancellationToken ct)
        => throw new NotImplementedException("TSK-09-01");

    public Task<string?> SetTraceAsync(ISession monitor, SessionKey target, bool enable, TraceOptions options, CancellationToken ct)
        => throw new NotImplementedException("TSK-09-01");
}
