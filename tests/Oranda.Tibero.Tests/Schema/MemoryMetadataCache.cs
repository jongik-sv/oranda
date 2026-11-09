using Oranda.Core.Events;
using Oranda.Core.Metadata;
using Oranda.Core.Settings;
using Oranda.Core.Storage;

namespace Oranda.Tibero.Tests.Schema;

/// <summary>시험용 메모리 캐시. <see cref="IMetadataCache"/> 의 뜻(Invalidate 는 목록을 낡게 만든다)을 따른다.</summary>
internal sealed class MemoryMetadataCache : IMetadataCache
{
    private readonly Dictionary<string, MetaSource> _sources = [];
    private readonly Dictionary<(string, string, string, string), MetaObject> _objects = [];

    public Task<MetaSource?> GetSourceAsync(string dbKey, CancellationToken ct) => Task.FromResult(_sources.GetValueOrDefault(dbKey));

    public Task<IReadOnlyList<MetaObject>> GetObjectsAsync(string dbKey, string? owner, string? type, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<MetaObject>>([.. _objects.Values.Where(o => o.DbKey == dbKey && (owner is null || o.Owner == owner) && (type is null || o.Type == type))]);

    /// <summary>SQLite 판과 같은 뜻: 이름·소유자·유형 순, <paramref name="limit"/> 0 이하 = 제한 없음.</summary>
    public Task<IReadOnlyList<MetaObject>> FindByPrefixAsync(string dbKey, string namePrefix, int limit, CancellationToken ct)
    {
        var found = _objects.Values
            .Where(o => o.DbKey == dbKey && o.Name.StartsWith(namePrefix, StringComparison.Ordinal))
            .OrderBy(o => o.Name, StringComparer.Ordinal).ThenBy(o => o.Owner, StringComparer.Ordinal).ThenBy(o => o.Type, StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyList<MetaObject>>([.. limit > 0 ? found.Take(limit) : found]);
    }

    public Task UpsertAsync(MetaSource source, IReadOnlyList<MetaObject> objects, CancellationToken ct)
    {
        var old = _sources.GetValueOrDefault(source.DbKey);
        _sources[source.DbKey] = source with { RefreshedAt = source.RefreshedAt ?? old?.RefreshedAt };
        foreach (var o in objects)
        {
            _objects[(source.DbKey, o.Owner, o.Name, o.Type)] = o with { DbKey = source.DbKey };
        }

        return Task.CompletedTask;
    }

    public Task InvalidateAsync(string dbKey, IReadOnlyList<DbObjectRef> objects, CancellationToken ct)
    {
        foreach (var key in _objects.Keys.Where(k => k.Item1 == dbKey && (objects.Count == 0 || objects.Any(r => r.Owner == k.Item2 && r.Name == k.Item3 && (r.ObjectType.Length == 0 || r.ObjectType == k.Item4)))).ToList())
        {
            _objects.Remove(key);
        }

        if (_sources.TryGetValue(dbKey, out var s))
        {
            _sources[dbKey] = s with { RefreshedAt = null };
        }

        return Task.CompletedTask;
    }

    public Task PruneAsync(MetadataCacheSettings settings, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>시험용 버스: 발행한 이벤트를 기록하고 구독자는 두지 않는다(Oranda.Testing 을 참조하지 않으려는 작은 대역).</summary>
internal sealed class RecordingEventBus : IEventBus
{
    public List<OrandaEvent> Published { get; } = [];

    public IDisposable Subscribe<TEvent>(object owner, Action<TEvent> handler)
        where TEvent : OrandaEvent => new Nothing();

    public void Publish<TEvent>(TEvent e)
        where TEvent : OrandaEvent => Published.Add(e);

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
