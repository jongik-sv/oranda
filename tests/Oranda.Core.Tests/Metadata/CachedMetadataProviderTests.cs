using Oranda.Core.Events;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Testing;

namespace Oranda.Core.Tests.Metadata;

/// <summary>
/// 객체 목록 캐시 흐름(<see cref="CachedMetadataProvider"/>)을 DBMS 없이 본다. 가짜 하위 형식이 「서버 목록」 을 메모리에서 돌려주고 읽은 조건을 적는다.
/// </summary>
public sealed class CachedMetadataProviderTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeSession _session = new();
    private readonly MemoryCache _cache = new();
    private readonly FakeEventBus _bus = new();

    public ValueTask DisposeAsync() => _session.DisposeAsync();

    [Theory]
    [InlineData("EMP%", "EMP", true)]
    [InlineData("EMP%", "EMPLOYEES", true)]
    [InlineData("EMP%", "DEMP", false)]
    [InlineData("E_P", "EMP", true)]
    [InlineData("E_P", "EMMP", false)]
    [InlineData("%", "ANY", true)]
    [InlineData("A\\_B%", "A_BC", true)]
    [InlineData("A\\_B%", "AXBC", false)]
    [InlineData("a%", "ABC", false)]
    [InlineData("A%B", "A\nB", true)]
    public void Like_pattern_translates_like_the_server(string pattern, string name, bool expected)
        => Assert.Equal(expected, CachedMetadataProvider.LikeToRegex(pattern).IsMatch(name));

    [Fact]
    public async Task Filled_cache_answers_without_reading_the_server()
    {
        await FillCacheAsync(Obj("HR", "EMP"), Obj("HR", "EMP_V", "VIEW"), Obj("HR", "DEPT"), Obj("APP", "EMP2"));
        var provider = NewProvider();

        var found = await provider.ListObjectsAsync(_session, new ObjectQuery { Owner = "HR", ObjectTypes = ["TABLE"], NamePattern = "E%" }, default);

        Assert.Equal([new DbObjectRef("HR", "EMP", "TABLE")], found);
        Assert.Empty(provider.Fetched);
    }

    [Fact]
    public async Task Cached_answer_is_ordered_by_owner_type_name_and_limited()
    {
        await FillCacheAsync(Obj("HR", "B"), Obj("APP", "Z", "VIEW"), Obj("HR", "A"), Obj("APP", "Y"));
        var provider = NewProvider();

        var found = await provider.ListObjectsAsync(_session, new ObjectQuery { Limit = 3 }, default);

        Assert.Equal(["APP.TABLE.Y", "APP.VIEW.Z", "HR.TABLE.A"], found.Select(o => $"{o.Owner}.{o.ObjectType}.{o.Name}"));
    }

    [Fact]
    public async Task First_list_without_a_cache_fills_it_from_the_server_and_publishes_nothing()
    {
        var provider = NewProvider(Obj("HR", "EMP"), Obj("HR", "DEPT"));

        var found = await provider.ListObjectsAsync(_session, new ObjectQuery { Owner = "HR" }, default);

        Assert.Equal(["DEPT", "EMP"], found.Select(o => o.Name));
        var asked = Assert.Single(provider.Fetched); // 소유자 조건과 상관없이 전체 목록을 한 번 받는다
        Assert.Null(asked.Owner);
        Assert.Empty(asked.ObjectTypes);
        var source = await _cache.GetSourceAsync(_session.DbKey, default);
        Assert.NotNull(source?.RefreshedAt);
        Assert.Equal(_session.Server.Version, source!.ServerVersion);
        Assert.Equal(2, (await _cache.GetObjectsAsync(_session.DbKey, null, null, default)).Count);
        Assert.Empty(_bus.Published);

        // 다음 조회는 캐시로 답한다.
        await provider.ListObjectsAsync(_session, new ObjectQuery(), default);
        Assert.Single(provider.Fetched);
    }

    [Fact]
    public async Task Second_refresh_publishes_added_changed_and_vanished_objects()
    {
        var provider = NewProvider(Obj("HR", "KEEP"), Obj("HR", "ALTERED"), Obj("HR", "STATUS"), Obj("HR", "DROPPED"));
        await provider.RefreshAsync(_session);
        Assert.Empty(_bus.Published);

        provider.Server =
        [
            Obj("HR", "KEEP"),
            Obj("HR", "ALTERED", ddl: T0.AddHours(1)),
            Obj("HR", "STATUS", status: "INVALID"),
            Obj("HR", "CREATED"),
        ];
        await provider.RefreshAsync(_session);

        var e = Assert.Single(_bus.PublishedOf<MetadataInvalidated>());
        Assert.Equal(_session.DbKey, e.DbKey);
        Assert.Equal(["ALTERED", "CREATED", "DROPPED", "STATUS"], e.Objects.Select(o => o.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["ALTERED", "CREATED", "KEEP", "STATUS"], (await _cache.GetObjectsAsync(_session.DbKey, null, null, default)).Select(o => o.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Refresh_without_changes_publishes_nothing()
    {
        var provider = NewProvider(Obj("HR", "EMP"));
        await provider.RefreshAsync(_session);
        await provider.RefreshAsync(_session);

        Assert.Empty(_bus.Published);
    }

    [Fact]
    public async Task Refresh_keeps_the_payload_of_unchanged_objects_and_drops_it_for_changed_ones()
    {
        var provider = NewProvider(Obj("HR", "EMP"), Obj("HR", "DEPT"));
        await provider.RefreshAsync(_session);
        var info = new TableInfo(new DbObjectRef("HR", "EMP", "TABLE"), [new ColumnInfo("ID", 1, "NUMBER")]);
        await provider.StoreAsync(_session, new DbObjectRef("HR", "EMP", "TABLE"), "table", info);
        await provider.StoreAsync(_session, new DbObjectRef("HR", "DEPT", "TABLE"), "table", info);

        provider.Server = [Obj("HR", "EMP"), Obj("HR", "DEPT", ddl: T0.AddDays(1))];
        var all = await provider.RefreshAsync(_session);

        Assert.NotNull(all.Single(o => o.Name == "EMP").PayloadJson);
        Assert.Null(all.Single(o => o.Name == "DEPT").PayloadJson);
        var emp = await provider.FindAsync(_session, new DbObjectRef("HR", "EMP", "TABLE"));
        Assert.True(provider.Read(emp!.PayloadJson!, "table", out TableInfo? hit));
        Assert.Equal("ID", Assert.Single(hit!.Columns).Name);
        Assert.Null((await provider.FindAsync(_session, new DbObjectRef("HR", "DEPT", "TABLE")))!.PayloadJson);
    }

    [Fact]
    public void Payload_of_another_version_or_field_is_not_read()
    {
        var provider = NewProvider();

        Assert.False(provider.Read("""{"v":1,"table":{}}""", "table", out TableInfo? _));
        Assert.False(provider.Read("""{"v":2,"procs":[]}""", "table", out TableInfo? _));
        Assert.False(provider.Read("not json", "table", out TableInfo? _));
    }

    [Fact]
    public async Task Payload_is_not_stored_for_an_object_missing_from_the_list()
    {
        var provider = NewProvider(Obj("HR", "EMP"));
        await provider.RefreshAsync(_session);
        var upserts = _cache.UpsertCalls;

        await provider.StoreAsync(_session, new DbObjectRef("HR", "NOPE", "TABLE"), "table", "x");

        Assert.Equal(upserts, _cache.UpsertCalls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Created_after_and_invalid_only_ask_the_server_even_with_a_cache(bool invalidOnly, bool createdAfter)
    {
        await FillCacheAsync(Obj("HR", "EMP", status: "VALID"));
        var provider = NewProvider(Obj("HR", "BROKEN", status: "INVALID"));
        var q = new ObjectQuery { Owner = "HR", InvalidOnly = invalidOnly, CreatedAfter = createdAfter ? T0 : null };

        var found = await provider.ListObjectsAsync(_session, q, default);

        Assert.Equal([new DbObjectRef("HR", "BROKEN", "TABLE")], found);
        Assert.Equal([q], provider.Fetched);
        Assert.Equal(["EMP"], (await _cache.GetObjectsAsync(_session.DbKey, null, null, default)).Select(o => o.Name)); // 캐시는 건드리지 않는다
    }

    [Fact]
    public async Task Prefix_search_answers_from_the_cache_across_owners()
    {
        await FillCacheAsync(Obj("HR", "JOBS"), Obj("APP", "JOBS2"), Obj("HR", "EMP"));
        var provider = NewProvider();

        var found = await provider.FindObjectsByPrefixAsync(_session, "JOB", 10, default);

        Assert.Equal(["APP.JOBS2", "HR.JOBS"], found.Select(o => o.Owner + "." + o.Name).Order(StringComparer.Ordinal));
        Assert.Empty(provider.Fetched);
    }

    /// <summary>빈 목록으로 답하면 부른 쪽 메모리에 남아, 캐시가 찬 뒤에도 그 접두어의 후보가 나오지 않는다.</summary>
    [Fact]
    public async Task Prefix_search_without_a_cache_fails_and_does_not_read_the_server()
    {
        var provider = NewProvider(Obj("HR", "JOBS"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.FindObjectsByPrefixAsync(_session, "JOB", 10, default));
        Assert.Empty(provider.Fetched);
    }

    [Fact]
    public async Task Cache_failure_falls_back_to_the_server_list()
    {
        _cache.Broken = true;
        var provider = NewProvider(Obj("HR", "EMP"));

        var found = await provider.ListObjectsAsync(_session, new ObjectQuery(), default);

        Assert.Equal([new DbObjectRef("HR", "EMP", "TABLE")], found);
        Assert.Empty(_bus.Published);
    }

    private FakeProvider NewProvider(params MetaObject[] server) => new(_cache, _bus) { Server = [.. server] };

    private Task FillCacheAsync(params MetaObject[] objects)
        => _cache.UpsertAsync(new MetaSource { DbKey = _session.DbKey, RefreshedAt = T0, LastUsedAt = T0 }, [.. objects.Select(o => o with { DbKey = _session.DbKey })], default);

    private static MetaObject Obj(string owner, string name, string type = "TABLE", string status = "VALID", DateTimeOffset? ddl = null)
        => new() { DbKey = string.Empty, Owner = owner, Name = name, Type = type, Status = status, LastDdlTime = ddl ?? T0, FetchedAt = T0 };

    /// <summary>서버 목록을 메모리에서 거르는 하위 형식. 보호 멤버를 시험이 부를 수 있게 연다.</summary>
    private sealed class FakeProvider(IMetadataCache cache, IEventBus events)
        : CachedMetadataProvider(cache, events, Timeout.InfiniteTimeSpan)
    {
        public List<MetaObject> Server { get; set; } = [];

        public List<ObjectQuery> Fetched { get; } = [];

        public Task<IReadOnlyList<MetaObject>> RefreshAsync(ISession s) => RefreshObjectListAsync(s, default);

        public async Task StoreAsync<T>(ISession s, DbObjectRef obj, string field, T value)
            => await StorePayloadAsync(s, await FindCachedAsync(s, obj, default), field, value, default);

        public Task<MetaObject?> FindAsync(ISession s, DbObjectRef obj) => FindCachedAsync(s, obj, default);

        public bool Read<T>(string json, string field, out T? value) => TryReadPayload(json, field, out value);

        protected override Task<IReadOnlyList<MetaObject>> FetchObjectsAsync(ISession s, ObjectQuery q, DateTimeOffset fetchedAt, CancellationToken ct)
        {
            Fetched.Add(q);
            IReadOnlyList<MetaObject> rows =
            [
                .. Server
                    .Where(o => (q.Owner is null || o.Owner == q.Owner) && (!q.InvalidOnly || o.Status != "VALID"))
                    .Select(o => o with { DbKey = s.DbKey, FetchedAt = fetchedAt }),
            ];
            return Task.FromResult(rows);
        }

        protected override bool IsIgnorableBackgroundRefreshFailure(Exception ex) => ex is InvalidOperationException;
    }

    /// <summary>시험용 메모리 캐시. <see cref="IMetadataCache"/> 의 뜻(Invalidate 는 목록을 낡게 만든다)을 따른다. <see cref="Broken"/> 이면 모든 호출이 실패한다.</summary>
    private sealed class MemoryCache : IMetadataCache
    {
        private readonly Dictionary<string, MetaSource> _sources = [];
        private readonly Dictionary<(string, string, string, string), MetaObject> _objects = [];

        public int UpsertCalls { get; private set; }

        public bool Broken { get; set; }

        public Task<MetaSource?> GetSourceAsync(string dbKey, CancellationToken ct)
        {
            ThrowIfBroken();
            return Task.FromResult(_sources.GetValueOrDefault(dbKey));
        }

        public Task<IReadOnlyList<MetaObject>> GetObjectsAsync(string dbKey, string? owner, string? type, CancellationToken ct)
        {
            ThrowIfBroken();
            return Task.FromResult<IReadOnlyList<MetaObject>>([.. _objects.Values.Where(o => o.DbKey == dbKey && (owner is null || o.Owner == owner) && (type is null || o.Type == type))]);
        }

        public Task<IReadOnlyList<MetaObject>> FindByPrefixAsync(string dbKey, string namePrefix, int limit, CancellationToken ct)
        {
            ThrowIfBroken();
            return Task.FromResult<IReadOnlyList<MetaObject>>([.. _objects.Values.Where(o => o.DbKey == dbKey && o.Name.StartsWith(namePrefix, StringComparison.Ordinal)).Take(limit)]);
        }

        public Task UpsertAsync(MetaSource source, IReadOnlyList<MetaObject> objects, CancellationToken ct)
        {
            ThrowIfBroken();
            UpsertCalls++;
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
            ThrowIfBroken();
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

        private void ThrowIfBroken()
        {
            if (Broken)
            {
                throw new IOException("캐시 파일을 열 수 없다.");
            }
        }
    }
}
