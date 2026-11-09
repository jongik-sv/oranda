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

    /// <summary>이름 풀이 한 단계의 답(소유자, 이름). 없으면 사전에 없는 이름이다.</summary>
    public Dictionary<(string Owner, string Name), NameStep> NameSteps { get; } = [];

    public Task<NameStep?> GetNameStepAsync(ISession s, string owner, string name, CancellationToken ct) =>
        Task.FromResult(NameSteps.GetValueOrDefault((owner, name)));

    public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Schemas);

    /// <summary>객체 목록 조회 횟수.</summary>
    public int ListObjectsCalls { get; private set; }

    /// <summary>닫아 두면 객체 목록 조회가 열릴 때까지 끝나지 않는다(느린 첫 조회 흉내). 취소 토큰은 지킨다.</summary>
    public TaskCompletionSource? ListObjectsGate { get; set; }

    /// <summary>마지막 객체 목록 조회 조건.</summary>
    public ObjectQuery? LastObjectQuery { get; private set; }

    /// <summary>계정 → (표 이름 → 테이블 명). 없는 계정은 빈 사전.</summary>
    public Dictionary<string, Dictionary<string, string>> Comments { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyDictionary<string, string>> ListTableCommentsAsync(ISession s, string owner, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(
            Comments.TryGetValue(owner, out var byName) ? byName : new Dictionary<string, string>(StringComparer.Ordinal));

    public async Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(q);
        ListObjectsCalls++;
        LastObjectQuery = q;
        if (ListObjectsGate is { } gate)
        {
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        var result = Objects
            .Where(o => q.Owner is null || string.Equals(o.Owner, q.Owner, StringComparison.OrdinalIgnoreCase))
            .Where(o => q.ObjectTypes.Count == 0 || q.ObjectTypes.Contains(o.ObjectType, StringComparer.OrdinalIgnoreCase))
            .Take(q.Limit ?? int.MaxValue)
            .ToList();
        return result;
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
