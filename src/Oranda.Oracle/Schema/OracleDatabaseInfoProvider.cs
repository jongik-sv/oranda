using Oranda.Core.Composition;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Schema;

/// <summary>DB 정보(인스턴스·버전·옵션·파라미터·NLS). 소유: TSK-07-04. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(IDatabaseInfoProvider))]
internal sealed class OracleDatabaseInfoProvider : IDatabaseInfoProvider
{
    public Task<IReadOnlyList<DetailSection>> GetAsync(ISession s, CancellationToken ct)
        => throw new NotImplementedException("TSK-07-04");
}
