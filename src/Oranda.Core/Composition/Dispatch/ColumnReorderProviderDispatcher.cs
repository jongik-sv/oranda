using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>세션의 DBMS 로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(IColumnReorderProvider), Dbms = …)]</c>.</summary>
[OrandaService(typeof(IColumnReorderProvider))]
public sealed class ColumnReorderProviderDispatcher : IColumnReorderProvider
{
    private readonly IDbmsServiceSelector _selector;

    public ColumnReorderProviderDispatcher(IDbmsServiceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    public Task<ColumnReorderTraits> GetTraitsAsync(ISession s, DbObjectRef table, CancellationToken ct) =>
        _selector.For<IColumnReorderProvider>(s).GetTraitsAsync(s, table, ct);

    public Task<IReadOnlyList<SourceSearchHit>> FindReferencingSourceLinesAsync(ISession s, DbObjectRef table, CancellationToken ct) =>
        _selector.For<IColumnReorderProvider>(s).FindReferencingSourceLinesAsync(s, table, ct);
}
