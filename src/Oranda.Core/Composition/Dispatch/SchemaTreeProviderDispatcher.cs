using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>세션의 DBMS 로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(ISchemaTreeProvider), Dbms = …)]</c>.</summary>
[OrandaService(typeof(ISchemaTreeProvider))]
public sealed class SchemaTreeProviderDispatcher : ISchemaTreeProvider
{
    private readonly IDbmsServiceSelector _selector;

    public SchemaTreeProviderDispatcher(IDbmsServiceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    public Task<SchemaFolderResult<TreeTrigger>> ListTriggersAsync(ISession s, DbObjectRef table, CancellationToken ct) =>
        _selector.For<ISchemaTreeProvider>(s).ListTriggersAsync(s, table, ct);

    public Task<SchemaFolderResult<SnapshotLogRef>> ListSnapshotLogsAsync(ISession s, DbObjectRef table, CancellationToken ct) =>
        _selector.For<ISchemaTreeProvider>(s).ListSnapshotLogsAsync(s, table, ct);
}
