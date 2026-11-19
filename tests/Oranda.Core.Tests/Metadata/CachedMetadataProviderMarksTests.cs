using Oranda.Core.Events;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Testing;

namespace Oranda.Core.Tests.Metadata;

/// <summary>
/// 세부 종류·상태 표식 흐름(<see cref="CachedMetadataProvider.ListObjectMarksAsync"/>): 캐시로 바로 답하고 뒤에서 보조 질의를 읽어 캐시에 넣은 뒤 알린다.
/// 보조 질의가 실패해도 그 표식만 빈다. 세션이 바쁘면 다시 한다.
/// </summary>
public sealed class CachedMetadataProviderMarksTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeSession _session = new();
    private readonly MarkCache _cache = new();
    private readonly FakeEventBus _bus = new();

    public ValueTask DisposeAsync() => _session.DisposeAsync();

    [Fact]
    public async Task First_ask_answers_empty_from_the_cache_then_reads_the_marks_behind_and_publishes()
    {
        await FillAsync(Obj("HR", "GTT"), Obj("HR", "HEAP"), Obj("HR", "TRG", "TRIGGER"));
        var provider = NewProvider();
        provider.Rows[ObjectMarkRows.Tables] = [["GTT", "Y", "SYS$SESSION", null, null, null, "NO", "NO"]];
        provider.Rows[ObjectMarkRows.Triggers] = [["TRG", "BEFORE EACH ROW", "TABLE", "INSERT", "DISABLED"]];

        var first = await provider.ListObjectMarksAsync(_session, "HR", default);

        Assert.Empty(first); // 서버를 기다리지 않는다
        await WaitAsync(() => _bus.PublishedOf<ObjectMarksRefreshed>().Any());
        var published = _bus.PublishedOf<ObjectMarksRefreshed>().Single();
        Assert.Equal((_session.DbKey, "HR"), (published.DbKey, published.Owner));
        Assert.Equal([ObjectMarkRows.Tables, ObjectMarkRows.Triggers, ObjectMarkRows.Synonyms], provider.Asked.Select(a => a.Id));
        Assert.All(provider.Asked, a => Assert.Equal("HR", a.Owner));

        var second = await provider.ListObjectMarksAsync(_session, "HR", default);
        Assert.Equal(new ObjectMark("GTT_SESSION", null), second[new DbObjectRef("HR", "GTT", "TABLE")]);
        Assert.Equal(new ObjectMark("BEFORE ROW", "DISABLED"), second[new DbObjectRef("HR", "TRG", "TRIGGER")]);
        Assert.False(second.ContainsKey(new DbObjectRef("HR", "HEAP", "TABLE")));
        Assert.Equal(3, provider.Asked.Count); // 바로 다시 물어도 다시 읽지 않는다
    }

    [Fact]
    public async Task A_failing_query_only_leaves_its_own_marks_empty()
    {
        await FillAsync(Obj("HR", "GTT"), Obj("HR", "S", "SYNONYM"));
        var provider = NewProvider();
        provider.Rows[ObjectMarkRows.Tables] = [["GTT", "Y", "SYS$TRANSACTION", null, null, null, "NO", "NO"]];
        provider.Fail[ObjectMarkRows.Synonyms] = new InvalidOperationException("ORA-00942: table or view does not exist");

        await provider.ListObjectMarksAsync(_session, "HR", default);
        await WaitAsync(() => _bus.PublishedOf<ObjectMarksRefreshed>().Any());

        var marks = await provider.ListObjectMarksAsync(_session, "HR", default);
        Assert.Equal("GTT_TRANSACTION", Assert.Single(marks).Value.SubKind);
    }

    [Fact]
    public async Task Busy_session_retries_and_never_writes_half_a_result()
    {
        await FillAsync(Obj("HR", "GTT"));
        var provider = NewProvider();
        provider.Rows[ObjectMarkRows.Tables] = [["GTT", "Y", "SYS$SESSION", null, null, null, "NO", "NO"]];
        provider.BusyTimes = 2;

        await provider.ListObjectMarksAsync(_session, "HR", default);
        await WaitAsync(() => _bus.PublishedOf<ObjectMarksRefreshed>().Any());

        Assert.Equal(1, _cache.SetMarksCalls); // 바빠 멈춘 시도는 캐시를 건드리지 않았다
        Assert.Single(await provider.ListObjectMarksAsync(_session, "HR", default));
    }

    [Fact]
    public async Task Busy_or_closed_session_is_not_tried()
    {
        await FillAsync(Obj("HR", "GTT"));
        var provider = NewProvider();
        provider.RetryLimit = 0;
        _session.State = SessionState.Busy;

        await provider.ListObjectMarksAsync(_session, "HR", default);
        await WaitAsync(() => !provider.Running(_session.DbKey, "HR"));

        Assert.Empty(provider.Asked); // D-041: 사용자 실행 뒤에 줄을 서지 않는다
        Assert.Empty(_bus.PublishedOf<ObjectMarksRefreshed>());
    }

    [Fact]
    public async Task No_cache_yet_answers_empty_and_reads_nothing()
    {
        var provider = NewProvider();

        Assert.Empty(await provider.ListObjectMarksAsync(_session, "HR", default));
        Assert.False(provider.Running(_session.DbKey, "HR"));
        Assert.Empty(provider.Asked);
    }

    [Fact]
    public async Task A_new_list_reads_the_marks_again_only_after_the_interval_unless_forgotten()
    {
        await FillAsync(Obj("HR", "GTT"));
        var provider = NewProvider();
        await provider.ListObjectMarksAsync(_session, "HR", default);
        await WaitAsync(() => _bus.PublishedOf<ObjectMarksRefreshed>().Count() == 1);

        // 목록 기준 시각이 바뀌어도 간격(기본 5분) 안이면 다시 읽지 않는다.
        await _cache.UpsertAsync(new MetaSource { DbKey = _session.DbKey, RefreshedAt = T0.AddHours(1), LastUsedAt = T0 }, [], default);
        await provider.ListObjectMarksAsync(_session, "HR", default);
        Assert.False(provider.Running(_session.DbKey, "HR"));
        Assert.Single(_bus.PublishedOf<ObjectMarksRefreshed>());

        // 간격이 지나면 다시 읽는다.
        provider.Interval = TimeSpan.Zero;
        await provider.ListObjectMarksAsync(_session, "HR", default);
        await WaitAsync(() => _bus.PublishedOf<ObjectMarksRefreshed>().Count() == 2);
    }

    [Fact]
    public async Task Provider_without_mark_queries_reads_nothing()
    {
        await FillAsync(Obj("HR", "GTT"));
        var provider = new MarkProvider(_cache, _bus) { Ids = [] };

        Assert.Empty(await provider.ListObjectMarksAsync(_session, "HR", default));
        Assert.False(provider.Running(_session.DbKey, "HR"));
    }

    [Fact]
    public async Task Default_cache_methods_keep_marks_off_and_filter_marked_rows()
    {
        IMetadataCache plain = new PlainCache(
        [
            new MetaObject { DbKey = "K", Owner = "HR", Name = "A", Type = "TABLE" },
            new MetaObject { DbKey = "K", Owner = "HR", Name = "B", Type = "TABLE", SubKind = "IOT" },
        ]);

        await plain.SetMarksAsync("K", "HR", [], default); // 기본: 아무것도 하지 않는다
        Assert.Equal(["B"], (await plain.GetMarkedAsync("K", "HR", default)).Select(o => o.Name));
    }

    private MarkProvider NewProvider() => new(_cache, _bus) { RetryDelay = TimeSpan.FromMilliseconds(5) };

    private Task FillAsync(params MetaObject[] objects)
        => _cache.UpsertAsync(new MetaSource { DbKey = _session.DbKey, RefreshedAt = T0, LastUsedAt = T0 }, [.. objects.Select(o => o with { DbKey = _session.DbKey })], default);

    private static MetaObject Obj(string owner, string name, string type = "TABLE")
        => new() { DbKey = string.Empty, Owner = owner, Name = name, Type = type, Status = "VALID", LastDdlTime = T0, FetchedAt = T0 };

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class MarkProvider(IMetadataCache cache, IEventBus events) : CachedMetadataProvider(cache, events, Timeout.InfiniteTimeSpan)
    {
        public IReadOnlyList<string> Ids { get; init; } = [ObjectMarkRows.Tables, ObjectMarkRows.Triggers, ObjectMarkRows.Synonyms];

        public Dictionary<string, List<object?[]>> Rows { get; } = [];

        public Dictionary<string, Exception> Fail { get; } = [];

        public List<(string Id, string Owner)> Asked { get; } = [];

        public int BusyTimes { get; set; }

        public TimeSpan RetryDelay
        {
            get => BackgroundRetryDelay;
            set => BackgroundRetryDelay = value;
        }

        public int RetryLimit
        {
            get => BackgroundRetryLimit;
            set => BackgroundRetryLimit = value;
        }

        public TimeSpan Interval
        {
            get => MarksRefreshInterval;
            set => MarksRefreshInterval = value;
        }

        public bool Running(string dbKey, string owner) => MarksRefreshRunning(dbKey, owner);

        protected override IReadOnlyList<string> MarkQueryIds => Ids;

        protected override Task<IReadOnlyList<object?[]>> RunMarkQueryAsync(ISession s, string id, string owner, CancellationToken ct)
        {
            if (BusyTimes > 0)
            {
                BusyTimes--;
                throw new SessionBusyException();
            }

            Asked.Add((id, owner));
            return Fail.TryGetValue(id, out var ex) ? Task.FromException<IReadOnlyList<object?[]>>(ex)
                : Task.FromResult<IReadOnlyList<object?[]>>(Rows.GetValueOrDefault(id) ?? []);
        }

        protected override Task<IReadOnlyList<MetaObject>> FetchObjectsAsync(ISession s, ObjectQuery q, DateTimeOffset fetchedAt, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MetaObject>>([]);

        protected override bool IsIgnorableBackgroundRefreshFailure(Exception ex) => ex is InvalidOperationException;
    }

    /// <summary>표식을 다루는 메모리 캐시(SQLite 판과 같은 뜻: Upsert 는 있는 행의 표식을 두고, SetMarks 는 소유자 표식을 비운 뒤 다시 쓴다).</summary>
    private sealed class MarkCache : IMetadataCache
    {
        private readonly Dictionary<string, MetaSource> _sources = [];
        private readonly Dictionary<(string, string, string, string), MetaObject> _objects = [];

        public int SetMarksCalls { get; private set; }

        public Task<MetaSource?> GetSourceAsync(string dbKey, CancellationToken ct) => Task.FromResult(_sources.GetValueOrDefault(dbKey));

        public Task<IReadOnlyList<MetaObject>> GetObjectsAsync(string dbKey, string? owner, string? type, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MetaObject>>([.. _objects.Values.Where(o => o.DbKey == dbKey && (owner is null || o.Owner == owner) && (type is null || o.Type == type))]);

        public Task<IReadOnlyList<MetaObject>> FindByPrefixAsync(string dbKey, string namePrefix, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MetaObject>>([.. _objects.Values.Where(o => o.DbKey == dbKey && o.Name.StartsWith(namePrefix, StringComparison.Ordinal)).Take(limit)]);

        public Task UpsertAsync(MetaSource source, IReadOnlyList<MetaObject> objects, CancellationToken ct)
        {
            var old = _sources.GetValueOrDefault(source.DbKey);
            _sources[source.DbKey] = source with { RefreshedAt = source.RefreshedAt ?? old?.RefreshedAt };
            foreach (var o in objects)
            {
                var key = (source.DbKey, o.Owner, o.Name, o.Type);
                _objects[key] = _objects.TryGetValue(key, out var was)
                    ? o with { DbKey = source.DbKey, SubKind = was.SubKind, StateMark = was.StateMark }
                    : o with { DbKey = source.DbKey };
            }

            return Task.CompletedTask;
        }

        public Task SetMarksAsync(string dbKey, string owner, IReadOnlyList<MetaObject> marked, CancellationToken ct)
        {
            SetMarksCalls++;
            foreach (var key in _objects.Keys.Where(k => k.Item1 == dbKey && k.Item2 == owner).ToList())
            {
                _objects[key] = _objects[key] with { SubKind = null, StateMark = null };
            }

            foreach (var m in marked)
            {
                var key = (dbKey, m.Owner, m.Name, m.Type);
                if (_objects.TryGetValue(key, out var row))
                {
                    _objects[key] = row with { SubKind = m.SubKind, StateMark = m.StateMark };
                }
            }

            return Task.CompletedTask;
        }

        public Task InvalidateAsync(string dbKey, IReadOnlyList<DbObjectRef> objects, CancellationToken ct) => Task.CompletedTask;

        public Task PruneAsync(MetadataCacheSettings settings, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>표식 메서드를 구현하지 않은 캐시(기본 구현을 본다).</summary>
    private sealed class PlainCache(IReadOnlyList<MetaObject> rows) : IMetadataCache
    {
        public Task<MetaSource?> GetSourceAsync(string dbKey, CancellationToken ct) => Task.FromResult<MetaSource?>(null);

        public Task<IReadOnlyList<MetaObject>> GetObjectsAsync(string dbKey, string? owner, string? type, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MetaObject>>([.. rows.Where(o => owner is null || o.Owner == owner)]);

        public Task<IReadOnlyList<MetaObject>> FindByPrefixAsync(string dbKey, string namePrefix, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MetaObject>>([]);

        public Task UpsertAsync(MetaSource source, IReadOnlyList<MetaObject> objects, CancellationToken ct) => Task.CompletedTask;

        public Task InvalidateAsync(string dbKey, IReadOnlyList<DbObjectRef> objects, CancellationToken ct) => Task.CompletedTask;

        public Task PruneAsync(MetadataCacheSettings settings, CancellationToken ct) => Task.CompletedTask;
    }
}
