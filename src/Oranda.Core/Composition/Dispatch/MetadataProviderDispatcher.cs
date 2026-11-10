using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>
/// 세션의 DBMS 로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(IMetadataProvider), Dbms = …)]</c>.
/// 기본 구현이 있는 멤버도 고른 구현의 것(재정의했으면 그것, 아니면 인터페이스 기본)을 그대로 부른다.
/// </summary>
[OrandaService(typeof(IMetadataProvider))]
public sealed class MetadataProviderDispatcher : IMetadataProvider
{
    private readonly IDbmsServiceSelector _selector;

    public MetadataProviderDispatcher(IDbmsServiceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) =>
        _selector.For<IMetadataProvider>(s).ListSchemasAsync(s, ct);

    public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) =>
        _selector.For<IMetadataProvider>(s).ListObjectsAsync(s, q, ct);

    public Task<IReadOnlyList<DbObjectRef>> FindObjectsByPrefixAsync(ISession s, string namePrefix, int limit, CancellationToken ct) =>
        _selector.For<IMetadataProvider>(s).FindObjectsByPrefixAsync(s, namePrefix, limit, ct);

    public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) =>
        _selector.For<IMetadataProvider>(s).GetTableAsync(s, table, ct);

    public Task<IReadOnlyDictionary<string, string>> ListTableCommentsAsync(ISession s, string owner, CancellationToken ct) =>
        _selector.For<IMetadataProvider>(s).ListTableCommentsAsync(s, owner, ct);

    public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) =>
        _selector.For<IMetadataProvider>(s).GetProceduresAsync(s, obj, ct);

    public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) =>
        _selector.For<IMetadataProvider>(s).GetDdlAsync(s, obj, o, ct);

    public Task<ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) =>
        _selector.For<IMetadataProvider>(s).GetObjectDetailAsync(s, obj, ct);

    public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) =>
        _selector.For<IMetadataProvider>(s).GetSourceAsync(s, obj, ct);

    public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) =>
        _selector.For<IMetadataProvider>(s).GetCompileErrorsAsync(s, obj, ct);

    public Task<NameStep?> GetNameStepAsync(ISession s, string owner, string name, CancellationToken ct) =>
        _selector.For<IMetadataProvider>(s).GetNameStepAsync(s, owner, name, ct);

    public Task<IReadOnlyList<JavaObjectInfo>> ListJavaObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) =>
        _selector.For<IMetadataProvider>(s).ListJavaObjectsAsync(s, q, ct);

    public Task<bool> IsJavaAvailableAsync(ISession s, CancellationToken ct) =>
        _selector.For<IMetadataProvider>(s).IsJavaAvailableAsync(s, ct);

    public Task<DateTimeOffset?> GetObjectListRefreshedAtAsync(ISession s, CancellationToken ct) =>
        _selector.For<IMetadataProvider>(s).GetObjectListRefreshedAtAsync(s, ct);

    public Task<ObjectListChange> RefreshObjectTypeAsync(ISession s, string owner, string objectType, CancellationToken ct) =>
        _selector.For<IMetadataProvider>(s).RefreshObjectTypeAsync(s, owner, objectType, ct);

    public Task<ObjectListChange> ResetObjectListCacheAsync(ISession s, CancellationToken ct) =>
        _selector.For<IMetadataProvider>(s).ResetObjectListCacheAsync(s, ct);
}
