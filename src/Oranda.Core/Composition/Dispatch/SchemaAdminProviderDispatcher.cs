using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>세션의 DBMS 로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(ISchemaAdminProvider), Dbms = …)]</c>.</summary>
[OrandaService(typeof(ISchemaAdminProvider))]
public sealed class SchemaAdminProviderDispatcher : ISchemaAdminProvider
{
    private readonly IDbmsServiceSelector _selector;

    public SchemaAdminProviderDispatcher(IDbmsServiceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    public Task<IReadOnlyList<ObjectSearchRow>> SearchObjectsAsync(ISession s, ObjectSearchQuery q, CancellationToken ct) =>
        _selector.For<ISchemaAdminProvider>(s).SearchObjectsAsync(s, q, ct);

    public Task<SearchHits<SourceSearchHit>> SearchSourceAsync(ISession s, SourceSearchQuery q, CancellationToken ct) =>
        _selector.For<ISchemaAdminProvider>(s).SearchSourceAsync(s, q, ct);

    public Task<SearchHits<ColumnSearchHit>> SearchColumnsAsync(ISession s, ColumnSearchQuery q, CancellationToken ct) =>
        _selector.For<ISchemaAdminProvider>(s).SearchColumnsAsync(s, q, ct);

    public Task<IReadOnlyList<ObjectReference>> GetReferencesAsync(ISession s, DbObjectRef obj, CancellationToken ct) =>
        _selector.For<ISchemaAdminProvider>(s).GetReferencesAsync(s, obj, ct);

    public Task<IReadOnlyList<RecycleBinItem>> ListRecycleBinAsync(ISession s, CancellationToken ct) =>
        _selector.For<ISchemaAdminProvider>(s).ListRecycleBinAsync(s, ct);
}
