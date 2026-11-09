using Oranda.Core.Grid;
using Oranda.Core.Sessions;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>세션의 DBMS 로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(ILobAccessor), Dbms = …)]</c>.</summary>
[OrandaService(typeof(ILobAccessor))]
public sealed class LobAccessorDispatcher : ILobAccessor
{
    private readonly IDbmsServiceSelector _selector;

    public LobAccessorDispatcher(IDbmsServiceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    public Task<Stream> OpenReadAsync(ISession s, LobValue lob, CancellationToken ct) =>
        _selector.For<ILobAccessor>(s).OpenReadAsync(s, lob, ct);
}
