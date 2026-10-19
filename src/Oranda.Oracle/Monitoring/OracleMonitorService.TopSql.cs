using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Monitoring;

/// <summary>SQL 모니터(Top SQL·SQL 전문·바인드 캡처). 소유: TSK-09-02.</summary>
internal sealed partial class OracleMonitorService
{
    public Task<IReadOnlyList<TopSqlRow>> GetTopSqlAsync(ISession monitor, TopSqlCriteria c, CancellationToken ct)
        => throw new NotImplementedException("TSK-09-02");

    public Task<SqlDetail> GetSqlDetailAsync(ISession monitor, CursorRef cursor, CancellationToken ct)
        => throw new NotImplementedException("TSK-09-02");
}
