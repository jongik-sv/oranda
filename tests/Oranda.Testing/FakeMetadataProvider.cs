using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Testing;

/// <summary>
/// 시험이 채운 사전으로 답하는 메타데이터 제공자. 없는 테이블은 <see cref="KeyNotFoundException"/>.
/// </summary>
public sealed class FakeMetadataProvider : IMetadataProvider
{
    public List<string> Schemas { get; } = [];

    public List<DbObjectRef> Objects { get; } = [];

    public Dictionary<DbObjectRef, TableInfo> Tables { get; } = [];

    public Dictionary<DbObjectRef, IReadOnlyList<ProcedureInfo>> Procedures { get; } = [];

    public Dictionary<DbObjectRef, string> Sources { get; } = [];

    public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Schemas);

    public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(q);
        var result = Objects
            .Where(o => q.Owner is null || string.Equals(o.Owner, q.Owner, StringComparison.OrdinalIgnoreCase))
            .Where(o => q.ObjectTypes.Count == 0 || q.ObjectTypes.Contains(o.ObjectType, StringComparer.OrdinalIgnoreCase))
            .Take(q.Limit ?? int.MaxValue)
            .ToList();
        return Task.FromResult<IReadOnlyList<DbObjectRef>>(result);
    }

    public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) =>
        Task.FromResult(Tables[table]);

    public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) =>
        Task.FromResult(Procedures.TryGetValue(obj, out var list) ? list : []);

    public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) =>
        Task.FromResult($"-- DDL {obj.Owner}.{obj.Name}");

    public Task<ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) =>
        Task.FromResult(new ObjectDetail(obj, []));

    public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) =>
        Task.FromResult(Sources.TryGetValue(obj, out var text) ? text : string.Empty);

    public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CompileError>>([]);
}
