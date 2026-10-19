using Oranda.Core.Sessions;

namespace Oranda.Core.Metadata;

/// <summary>DB 정보 화면(인스턴스·버전·옵션·파라미터·NLS). 구현: Oranda.Oracle(TSK-07-04).</summary>
public interface IDatabaseInfoProvider
{
    Task<IReadOnlyList<DetailSection>> GetAsync(ISession s, CancellationToken ct);
}
