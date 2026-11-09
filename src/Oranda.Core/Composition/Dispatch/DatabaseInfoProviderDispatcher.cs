using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>세션의 DBMS 로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(IDatabaseInfoProvider), Dbms = …)]</c>.</summary>
[OrandaService(typeof(IDatabaseInfoProvider))]
public sealed class DatabaseInfoProviderDispatcher : IDatabaseInfoProvider
{
    private readonly IDbmsServiceSelector _selector;

    public DatabaseInfoProviderDispatcher(IDbmsServiceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    public Task<IReadOnlyList<DetailSection>> GetAsync(ISession s, CancellationToken ct) =>
        _selector.For<IDatabaseInfoProvider>(s).GetAsync(s, ct);
}
