using System.Collections.Concurrent;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Language.Completion;

/// <summary>
/// 자동완성용 메모리 캐시(TRD 4.5 「메모리 + SQLite 캐시」 의 메모리 층). <see cref="IMetadataProvider"/> 만 부른다(SQLite 캐시는 제공자 안쪽).
/// 조회는 진행 중인 <see cref="Task"/> 를 키별로 한 번만 만든다. <see cref="TryPeek{T}"/> 는 끝난 결과만 돌려줘 시간 예산을 넘긴 응답이 기다리지 않게 한다.
/// 실패한 조회는 다음에 다시 시도하도록 지운다.
/// </summary>
internal sealed class MetadataLookup(IMetadataProvider metadata)
{
    /// <summary>객체 목록에 넣는 유형(테이블·뷰·시노님·패키지 등 자동완성 대상).</summary>
    public static readonly IReadOnlyList<string> ObjectTypes =
        ["TABLE", "VIEW", "MATERIALIZED VIEW", "SYNONYM", "PACKAGE", "PROCEDURE", "FUNCTION", "SEQUENCE", "TYPE"];

    private const int MaxEntries = 4000;

    private readonly ConcurrentDictionary<string, Task> _entries = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public Task<IReadOnlyList<string>> SchemasAsync(ISession s)
        => Get(SchemasKey(s), () => metadata.ListSchemasAsync(s, CancellationToken.None));

    public Task<IReadOnlyList<DbObjectRef>> ObjectsAsync(ISession s, string owner)
        => Get(ObjectsKey(s, owner), () => metadata.ListObjectsAsync(s, new ObjectQuery { Owner = owner, ObjectTypes = ObjectTypes }, CancellationToken.None));

    public Task<TableInfo> TableAsync(ISession s, DbObjectRef table)
        => Get(TableKey(s, table.Owner, table.Name), () => metadata.GetTableAsync(s, table, CancellationToken.None));

    public Task<IReadOnlyList<ProcedureInfo>> ProceduresAsync(ISession s, DbObjectRef obj)
        => Get(ProceduresKey(s, obj.Owner, obj.Name), () => metadata.GetProceduresAsync(s, obj, CancellationToken.None));

    /// <summary>끝난 조회 결과가 메모리에 있으면 true(조회를 새로 시작하지 않는다).</summary>
    public bool TryPeek<T>(string key, out T value)
    {
        if (_entries.TryGetValue(key, out var t) && t.IsCompletedSuccessfully && t is Task<T> typed)
        {
            value = typed.Result;
            return true;
        }

        value = default!;
        return false;
    }

    public static string SchemasKey(ISession s) => s.DbKey + "|schemas";

    public static string ObjectsKey(ISession s, string owner) => s.DbKey + "|objects|" + owner;

    public static string TableKey(ISession s, string owner, string name) => s.DbKey + "|table|" + owner + "." + name;

    public static string ProceduresKey(ISession s, string owner, string name) => s.DbKey + "|procs|" + owner + "." + name;

    /// <summary>접속 하나(<paramref name="dbKey"/>)의 메모리 캐시를 지운다(<c>MetadataInvalidated</c>).</summary>
    public void Invalidate(string dbKey)
    {
        var prefix = dbKey + "|";
        foreach (var key in _entries.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    public void Clear() => _entries.Clear();

    private Task<T> Get<T>(string key, Func<Task<T>> fetch)
    {
        if (_entries.TryGetValue(key, out var existing) && existing is Task<T> hit && !hit.IsFaulted && !hit.IsCanceled)
        {
            return hit;
        }

        if (_entries.Count >= MaxEntries)
        {
            _entries.Clear();
        }

        var task = Start(fetch);
        _entries[key] = task;
        _ = task.ContinueWith(
            t =>
            {
                _ = t.Exception; // 관찰해 둔다(미관찰 예외 방지)
                _entries.TryRemove(new KeyValuePair<string, Task>(key, t));
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    private static Task<T> Start<T>(Func<Task<T>> fetch)
    {
        try
        {
            return fetch();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Task.FromException<T>(e);
        }
    }
}
