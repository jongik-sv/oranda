using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>세션의 DBMS 로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(ITableKeyDetailProvider), Dbms = …)]</c>.</summary>
[OrandaService(typeof(ITableKeyDetailProvider))]
public sealed class TableKeyDetailProviderDispatcher : ITableKeyDetailProvider
{
    private readonly IDbmsServiceSelector _selector;

    public TableKeyDetailProviderDispatcher(IDbmsServiceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    public Task<SchemaFolderResult<IndexColumnDetail>> GetIndexColumnsAsync(ISession s, DbObjectRef table, string indexName, CancellationToken ct) =>
        _selector.For<ITableKeyDetailProvider>(s).GetIndexColumnsAsync(s, table, indexName, ct);

    public Task<SchemaFolderResult<ConstraintColumnDetail>> GetConstraintColumnsAsync(ISession s, DbObjectRef table, string constraintName, CancellationToken ct) =>
        _selector.For<ITableKeyDetailProvider>(s).GetConstraintColumnsAsync(s, table, constraintName, ct);
}
