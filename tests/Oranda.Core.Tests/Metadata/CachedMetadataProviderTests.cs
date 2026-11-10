using Oranda.Core.Events;
using Oranda.Core.Execution.Providers;
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
        Assert.Empty(_bus.PublishedOf<MetadataInvalidated>());

        // 다음 조회는 캐시로 답한다.
        await provider.ListObjectsAsync(_session, new ObjectQuery(), default);
        Assert.Single(provider.Fetched);
    }

    [Fact]
    public async Task Second_refresh_publishes_added_changed_and_vanished_objects()
    {
        var provider = NewProvider(Obj("HR", "KEEP"), Obj("HR", "ALTERED"), Obj("HR", "STATUS"), Obj("HR", "DROPPED"));
        await provider.RefreshAsync(_session);
        Assert.Empty(_bus.PublishedOf<MetadataInvalidated>());

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

        Assert.Empty(_bus.PublishedOf<MetadataInvalidated>());
        Assert.Equal(2, _bus.PublishedOf<ObjectListRefreshed>().Count()); // 목록 기준 시각은 바뀐 것이 없어도 알린다
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

    // ---- 뒤 갱신이 바빠 멈춘 뒤 다시 하기(MC1): 간격을 켠 채로 본다 ----

    [Fact]
    public async Task Background_refresh_stopped_by_a_busy_session_runs_again_and_brings_new_objects()
    {
        await FillCacheAsync(Obj("HR", "EMP"));
        var provider = new FakeProvider(_cache, _bus, TimeSpan.FromMinutes(10))
        {
            Server = [Obj("HR", "EMP"), Obj("HR", "NEW_F", "FUNCTION")],
            BusyTimes = 2,
            RetryDelay = TimeSpan.FromMilliseconds(10),
        };

        var first = await provider.ListObjectsAsync(_session, new ObjectQuery { Owner = "HR" }, default);
        Assert.Equal(["EMP"], first.Select(o => o.Name)); // 캐시로 바로 답한다
        await provider.WhenBackgroundDoneAsync(_session.DbKey);

        Assert.Equal(3, provider.Fetched.Count); // 바빠 두 번 멈추고 세 번째에 끝났다
        Assert.True((await _cache.GetSourceAsync(_session.DbKey, default))!.RefreshedAt > T0);
        var done = Assert.Single(_bus.PublishedOf<ObjectListRefreshed>());
        Assert.Equal(new ObjectListChange(1, 0, 0), done.Change);
        var functions = await provider.ListObjectsAsync(_session, new ObjectQuery { Owner = "HR", ObjectTypes = ["FUNCTION"] }, default);
        Assert.Equal(["NEW_F"], functions.Select(o => o.Name));
        Assert.Equal(3, provider.Fetched.Count); // 간격(10분) 안이라 다시 받지 않는다
    }

    [Fact]
    public async Task Background_refresh_left_over_after_the_retry_limit_starts_again_on_the_next_list()
    {
        await FillCacheAsync(Obj("HR", "EMP"));
        var provider = new FakeProvider(_cache, _bus, TimeSpan.FromHours(1))
        {
            Server = [Obj("HR", "EMP"), Obj("HR", "NEW_T")],
            BusyTimes = int.MaxValue,
            RetryDelay = TimeSpan.FromMilliseconds(1),
            RetryLimit = 1,
        };

        await provider.ListObjectsAsync(_session, new ObjectQuery(), default);
        await provider.WhenBackgroundDoneAsync(_session.DbKey);
        Assert.Equal(2, provider.Fetched.Count); // 처음 + 다시 한 번, 둘 다 바빠 멈춤
        Assert.Equal(T0, (await _cache.GetSourceAsync(_session.DbKey, default))!.RefreshedAt);

        provider.BusyTimes = 0;
        await provider.ListObjectsAsync(_session, new ObjectQuery(), default); // 간격(1시간) 안이지만 남은 일이 있다
        await provider.WhenBackgroundDoneAsync(_session.DbKey);

        Assert.Equal(3, provider.Fetched.Count);
        Assert.Contains("NEW_T", (await _cache.GetObjectsAsync(_session.DbKey, "HR", null, default)).Select(o => o.Name));
        Assert.True((await _cache.GetSourceAsync(_session.DbKey, default))!.RefreshedAt > T0);
    }

    [Fact]
    public async Task List_older_than_the_max_age_is_refreshed_without_waiting_for_the_slow_read_interval()
    {
        _cache.MaxAge = TimeSpan.FromDays(1);
        var provider = new FakeProvider(_cache, _bus, TimeSpan.FromMilliseconds(200))
        {
            Server = [Obj("HR", "EMP")],
            FetchDelay = TimeSpan.FromMilliseconds(100), // 읽는 데 걸린 시간의 30 배(3초 이상)가 다음 간격이 된다
        };
        await provider.RefreshAsync(_session);
        await Task.Delay(300); // 기본 간격(0.2초)은 지났다

        await provider.ListObjectsAsync(_session, new ObjectQuery(), default);
        await provider.WhenBackgroundDoneAsync(_session.DbKey);
        Assert.Single(provider.Fetched); // 목록이 새것이면 긴 간격을 지킨다

        var old = DateTimeOffset.UtcNow.AddDays(-2);
        await _cache.UpsertAsync(new MetaSource { DbKey = _session.DbKey, RefreshedAt = old, LastUsedAt = old }, [], default);
        await provider.ListObjectsAsync(_session, new ObjectQuery(), default);
        await provider.WhenBackgroundDoneAsync(_session.DbKey);

        Assert.Equal(2, provider.Fetched.Count);
        Assert.True((await _cache.GetSourceAsync(_session.DbKey, default))!.RefreshedAt > old);
    }

    // ---- 유형 새로 고침·캐시 초기화(MC1) ----

    [Fact]
    public async Task Type_refresh_replaces_only_that_type_and_keeps_the_list_time()
    {
        await FillCacheAsync(Obj("HR", "F1", "FUNCTION"), Obj("HR", "F_OLD", "FUNCTION"), Obj("HR", "EMP"), Obj("APP", "F9", "FUNCTION"));
        var provider = NewProvider(Obj("HR", "F1", "FUNCTION"), Obj("HR", "F_NEW", "FUNCTION"), Obj("HR", "EMP"), Obj("HR", "EMP2"), Obj("APP", "F9", "FUNCTION"));

        var change = await provider.RefreshObjectTypeAsync(_session, "HR", "FUNCTION", default);

        Assert.Equal(new ObjectListChange(1, 0, 1), change);
        var asked = Assert.Single(provider.Fetched);
        Assert.Equal("HR", asked.Owner);
        Assert.Equal(["FUNCTION"], asked.ObjectTypes);
        var cached = await _cache.GetObjectsAsync(_session.DbKey, null, null, default);
        // 다른 유형(EMP2 는 아직 모른다)·다른 소유자는 그대로
        Assert.Equal(["APP.F9", "HR.EMP", "HR.F1", "HR.F_NEW"], cached.Select(o => o.Owner + "." + o.Name).Order(StringComparer.Ordinal));
        Assert.Equal(T0, (await _cache.GetSourceAsync(_session.DbKey, default))!.RefreshedAt);
        var e = Assert.Single(_bus.PublishedOf<MetadataInvalidated>());
        Assert.Equal(["F_NEW", "F_OLD"], e.Objects.Select(o => o.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Reset_empties_the_cache_and_reads_the_whole_list_again()
    {
        await FillCacheAsync(Obj("HR", "GONE"), Obj("HR", "EMP"));
        var provider = NewProvider(Obj("HR", "EMP"), Obj("HR", "NEW_F", "FUNCTION"));

        var change = await provider.ResetObjectListCacheAsync(_session, default);

        Assert.Equal(new ObjectListChange(2, 0, 0), change); // 비운 뒤 받았으니 모두 새것
        Assert.Equal(["EMP", "NEW_F"], (await _cache.GetObjectsAsync(_session.DbKey, null, null, default)).Select(o => o.Name).Order(StringComparer.Ordinal));
        Assert.True(await provider.GetObjectListRefreshedAtAsync(_session, default) > T0);
        Assert.Empty(Assert.Single(_bus.PublishedOf<MetadataInvalidated>()).Objects); // 그 DB 전체
    }

    // ---- 리뷰 수정(MC1 후속): 시작 잠금 0·이어 읽기 기다림, 갱신 잠금 공유, 비용, 세션 닫힘·넘기기, 남은 일 정리 ----

    [Fact]
    public async Task Background_refresh_waits_for_a_short_hold_between_rows_and_finishes_without_a_retry()
    {
        await FillCacheAsync(Obj("HR", "EMP"));
        var gate = new SessionCommandGate();
        Task? hold = null;
        var provider = new FakeProvider(_cache, _bus, TimeSpan.FromMinutes(10))
        {
            Server = [Obj("HR", "EMP"), Obj("HR", "NEW_T")],
            Gate = gate,
            ResumeWait = TimeSpan.FromSeconds(5),
            RetryDelay = TimeSpan.FromMinutes(10),
        };
        // 첫 행을 읽은 뒤 사용자의 짧은 질의(0.2초)가 잠금을 쥔다: 이어 읽기는 그것을 기다려 끝까지 읽는다.
        provider.AfterRow = row => row == 0 ? HoldGateAsync(gate, TimeSpan.FromMilliseconds(200), h => hold = h) : Task.CompletedTask;

        await provider.ListObjectsAsync(_session, new ObjectQuery(), default);
        await provider.WhenBackgroundDoneAsync(_session.DbKey).WaitAsync(Quick);

        Assert.NotNull(hold);
        Assert.True(hold!.IsCompletedSuccessfully);
        Assert.Single(provider.Fetched); // 바쁨으로 멈추고 다시 하지 않았다
        Assert.Equal(0, provider.BusySeen);
        Assert.Contains("NEW_T", (await _cache.GetObjectsAsync(_session.DbKey, "HR", null, default)).Select(o => o.Name));
    }

    [Fact]
    public async Task Background_refresh_does_not_queue_for_the_gate_when_starting_a_query_and_retries_instead()
    {
        await FillCacheAsync(Obj("HR", "EMP"));
        var gate = new SessionCommandGate();
        var provider = new FakeProvider(_cache, _bus, TimeSpan.FromMinutes(10))
        {
            Server = [Obj("HR", "EMP"), Obj("HR", "NEW_T")],
            Gate = gate,
            ResumeWait = TimeSpan.FromSeconds(5), // 시작도 이만큼 기다리면 아래 한도를 넘는다
            RetryDelay = TimeSpan.FromMilliseconds(50),
        };
        Assert.True(gate.TryEnter()); // 사용자의 실행이 잠금을 쥔 상태

        var timer = System.Diagnostics.Stopwatch.StartNew();
        await provider.ListObjectsAsync(_session, new ObjectQuery(), default);
        await provider.FirstBusy.Task.WaitAsync(Quick);
        var firstBusyAfter = timer.Elapsed;
        gate.Release();
        await provider.WhenBackgroundDoneAsync(_session.DbKey).WaitAsync(Quick);

        Assert.True(firstBusyAfter < TimeSpan.FromSeconds(2.5), $"시작 잠금을 {firstBusyAfter} 기다렸다"); // 줄을 서지 않고 바로 바쁨
        Assert.True(provider.Fetched.Count >= 2); // 바빠 멈춘 뒤 다시 해서 끝냈다
        Assert.Contains("NEW_T", (await _cache.GetObjectsAsync(_session.DbKey, "HR", null, default)).Select(o => o.Name));
    }

    [Fact]
    public async Task Background_refresh_is_not_tried_while_the_session_state_is_busy()
    {
        await FillCacheAsync(Obj("HR", "EMP"));
        var provider = new FakeProvider(_cache, _bus, TimeSpan.FromMinutes(10))
        {
            Server = [Obj("HR", "EMP"), Obj("HR", "NEW_T")],
            RetryDelay = TimeSpan.FromMilliseconds(20),
        };
        _session.State = SessionState.Busy; // 사용자의 실행이 도는 중

        await provider.ListObjectsAsync(_session, new ObjectQuery(), default);
        await Task.Delay(200);
        Assert.Empty(provider.Fetched); // 서버에 묻지도 않았다

        _session.State = SessionState.Idle;
        await provider.WhenBackgroundDoneAsync(_session.DbKey).WaitAsync(Quick);
        Assert.Single(provider.Fetched);
        Assert.Contains("NEW_T", (await _cache.GetObjectsAsync(_session.DbKey, "HR", null, default)).Select(o => o.Name));
    }

    [Fact]
    public async Task Closing_the_session_ends_the_retry_wait_and_a_reconnected_session_with_the_same_key_refreshes()
    {
        await FillCacheAsync(Obj("HR", "EMP"));
        var provider = new FakeProvider(_cache, _bus, TimeSpan.FromHours(1))
        {
            Server = [Obj("HR", "EMP"), Obj("HR", "NEW_T")],
            BusyTimes = 1,
            RetryDelay = TimeSpan.FromMinutes(10),
        };
        await provider.ListObjectsAsync(_session, new ObjectQuery(), default);
        await provider.FirstBusy.Task.WaitAsync(Quick);
        var waiting = provider.WhenBackgroundDoneAsync(_session.DbKey);

        await _session.DisposeAsync();
        _bus.Publish(new SessionClosed(_session.Id));
        await waiting.WaitAsync(Quick); // 다시 하기 10분을 기다리지 않는다
        Assert.Equal(T0, (await _cache.GetSourceAsync(_session.DbKey, default))!.RefreshedAt);

        await using var again = new FakeSession { DbKey = _session.DbKey }; // 같은 DB 로 다시 접속
        await provider.ListObjectsAsync(again, new ObjectQuery(), default); // 간격(1시간) 안이지만 남은 일이 있다
        await provider.WhenBackgroundDoneAsync(again.DbKey).WaitAsync(Quick);

        Assert.Equal([_session.Id, again.Id], provider.Sessions);
        Assert.Contains("NEW_T", (await _cache.GetObjectsAsync(again.DbKey, "HR", null, default)).Select(o => o.Name));
    }

    [Fact]
    public async Task Retry_waiting_on_a_busy_session_moves_to_another_idle_session_with_the_same_key()
    {
        await FillCacheAsync(Obj("HR", "EMP"));
        var provider = new FakeProvider(_cache, _bus, TimeSpan.FromHours(1))
        {
            Server = [Obj("HR", "EMP"), Obj("HR", "NEW_T")],
            RetryDelay = TimeSpan.FromMinutes(10),
        };
        _session.State = SessionState.Busy;
        await using var idle = new FakeSession { DbKey = _session.DbKey };

        await provider.ListObjectsAsync(_session, new ObjectQuery(), default);
        await provider.ListObjectsAsync(idle, new ObjectQuery(), default); // 같은 DB 의 다른 세션이 묻는다: 그쪽으로 넘긴다
        await provider.WhenBackgroundDoneAsync(_session.DbKey).WaitAsync(Quick);

        Assert.Equal([idle.Id], provider.Sessions);
        Assert.Contains("NEW_T", (await _cache.GetObjectsAsync(_session.DbKey, "HR", null, default)).Select(o => o.Name));
    }

    [Fact]
    public async Task Failure_after_a_busy_stop_clears_the_left_over_work_and_does_not_fault_the_refresh()
    {
        await FillCacheAsync(Obj("HR", "EMP"));
        var provider = new FakeProvider(_cache, _bus, TimeSpan.FromHours(1))
        {
            Server = [Obj("HR", "EMP")],
            BusyTimes = 1,
            FailWith = new NotSupportedException("카탈로그 변형 없음"),
            RetryDelay = TimeSpan.FromMilliseconds(1),
        };

        await provider.ListObjectsAsync(_session, new ObjectQuery(), default);
        await provider.WhenBackgroundDoneAsync(_session.DbKey).WaitAsync(Quick); // 예외로 끝나지 않는다(관찰했다)
        Assert.Equal(2, provider.Fetched.Count);

        await provider.ListObjectsAsync(_session, new ObjectQuery(), default);
        await provider.WhenBackgroundDoneAsync(_session.DbKey).WaitAsync(Quick);
        Assert.Equal(2, provider.Fetched.Count); // 남은 일이 지워져 간격(1시간)을 지킨다
    }

    [Fact]
    public async Task Type_refresh_waits_for_a_running_full_refresh_so_the_older_list_does_not_remove_the_new_rows()
    {
        await FillCacheAsync(Obj("HR", "EMP"));
        var provider = NewProvider(Obj("HR", "EMP"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.FetchHook = q =>
        {
            if (q.ObjectTypes.Count == 0)
            {
                started.TrySetResult();
                return release.Task;
            }

            return Task.CompletedTask;
        };

        var full = Task.Run(() => provider.RefreshAsync(_session)); // 전체 목록을 읽는 중(F_NEW 를 만들기 전 스냅샷)
        await started.Task.WaitAsync(Quick);
        provider.Server = [Obj("HR", "EMP"), Obj("HR", "F_NEW", "FUNCTION")]; // 그 사이 다른 도구로 함수를 만들었다
        var type = provider.RefreshObjectTypeAsync(_session, "HR", "FUNCTION", default);
        await Task.Delay(100);
        Assert.False(type.IsCompleted); // 전체 갱신이 끝나기를 기다린다
        Assert.Single(provider.Fetched);

        release.SetResult();
        await full.WaitAsync(Quick);
        Assert.Equal(new ObjectListChange(1, 0, 0), await type.WaitAsync(Quick));
        Assert.Contains("F_NEW", (await _cache.GetObjectsAsync(_session.DbKey, "HR", "FUNCTION", default)).Select(o => o.Name));
    }

    [Fact]
    public async Task Full_refresh_does_not_remove_or_replace_rows_fetched_after_its_snapshot()
    {
        var later = DateTimeOffset.UtcNow.AddHours(1); // 이 읽기를 시작한 뒤에 다른 갱신이 받은 행
        await FillCacheAsync(Obj("HR", "EMP"), Obj("HR", "OLD"));
        await _cache.UpsertAsync(new MetaSource { DbKey = _session.DbKey, LastUsedAt = later },
            [Obj("HR", "F_NEW", "FUNCTION") with { DbKey = _session.DbKey, FetchedAt = later }, Obj("HR", "ALTERED", ddl: later) with { DbKey = _session.DbKey, FetchedAt = later }], default);
        var provider = NewProvider(Obj("HR", "EMP"), Obj("HR", "ALTERED")); // 오래된 스냅샷: F_NEW 가 없고 ALTERED 는 옛 DDL 시각

        await provider.RefreshAsync(_session);

        var cached = await _cache.GetObjectsAsync(_session.DbKey, "HR", null, default);
        Assert.Equal(["ALTERED", "EMP", "F_NEW"], cached.Select(o => o.Name).Order(StringComparer.Ordinal)); // OLD 만 지웠다
        Assert.Equal(later, cached.Single(o => o.Name == "ALTERED").LastDdlTime);
    }

    [Fact]
    public async Task Type_refresh_keeps_a_list_time_cleared_while_it_read_the_server()
    {
        await FillCacheAsync(Obj("HR", "F1", "FUNCTION"), Obj("HR", "EMP"));
        var provider = NewProvider(Obj("HR", "F1", "FUNCTION"), Obj("HR", "EMP"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.FetchHook = _ =>
        {
            started.TrySetResult();
            return release.Task;
        };

        var type = Task.Run(() => provider.RefreshObjectTypeAsync(_session, "HR", "FUNCTION", default));
        await started.Task.WaitAsync(Quick);
        await _cache.InvalidateAsync(_session.DbKey, [new DbObjectRef("HR", "EMP", "TABLE")], default); // 다른 곳(열 순서 바꾸기 등)이 무효화했다
        release.SetResult();
        await type.WaitAsync(Quick);

        Assert.Null((await _cache.GetSourceAsync(_session.DbKey, default))!.RefreshedAt); // 옛 기준 시각을 되살리지 않는다
    }

    [Fact]
    public async Task Refresh_cost_leaves_out_the_time_spent_waiting_for_the_session_gate()
    {
        var gate = new SessionCommandGate();
        var provider = new FakeProvider(_cache, _bus, TimeSpan.FromMilliseconds(500))
        {
            Server = [Obj("HR", "EMP")],
            Gate = gate,
        };
        Assert.True(gate.TryEnter()); // 긴 실행이 잠금을 쥔 중에 「캐시 초기화」 같은 동기 갱신
        var refresh = provider.RefreshAsync(_session);
        await Task.Delay(600);
        gate.Release();
        await refresh.WaitAsync(Quick);
        await Task.Delay(700); // 기본 간격(0.5초)은 지났다. 기다린 0.6초를 비용으로 셌다면 간격은 18초다

        await provider.ListObjectsAsync(_session, new ObjectQuery(), default);
        await provider.WhenBackgroundDoneAsync(_session.DbKey).WaitAsync(Quick);

        Assert.Equal(2, provider.Fetched.Count);
    }

    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(10);

    /// <summary>뒤 갱신과 다른 흐름(사용자 명령)에서 잠금을 <paramref name="duration"/> 동안 쥔다. 잠금을 잡은 뒤 돌아온다.</summary>
    private static async Task HoldGateAsync(SessionCommandGate gate, TimeSpan duration, Action<Task> started)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task hold;
        using (ExecutionContext.SuppressFlow()) // 뒤 갱신의 흐름 값(잠금 한도·쥔 시간 재기)을 물려받지 않는다
        {
            hold = Task.Run(async () =>
            {
                Assert.True(gate.TryEnter());
                entered.SetResult();
                await Task.Delay(duration);
                gate.Release();
            });
        }

        started(hold);
        await entered.Task;
    }

    private FakeProvider NewProvider(params MetaObject[] server) => new(_cache, _bus) { Server = [.. server] };

    private Task FillCacheAsync(params MetaObject[] objects)
        => _cache.UpsertAsync(new MetaSource { DbKey = _session.DbKey, RefreshedAt = T0, LastUsedAt = T0 }, [.. objects.Select(o => o with { DbKey = _session.DbKey })], default);

    private static MetaObject Obj(string owner, string name, string type = "TABLE", string status = "VALID", DateTimeOffset? ddl = null)
        => new() { DbKey = string.Empty, Owner = owner, Name = name, Type = type, Status = status, LastDdlTime = ddl ?? T0, FetchedAt = T0 };

    /// <summary>서버 목록을 메모리에서 거르는 하위 형식. 보호 멤버를 시험이 부를 수 있게 연다.</summary>
    private sealed class FakeProvider(IMetadataCache cache, IEventBus events, TimeSpan? interval = null)
        : CachedMetadataProvider(cache, events, interval ?? Timeout.InfiniteTimeSpan)
    {
        public List<MetaObject> Server { get; set; } = [];

        public List<ObjectQuery> Fetched { get; } = [];

        /// <summary>서버 목록 읽기가 이 횟수만큼 세션 바쁨(<see cref="SessionBusyException"/>)으로 실패한다.</summary>
        public int BusyTimes { get; set; }

        /// <summary>서버 목록 읽기에 걸리는 시간(갱신 간격이 그 30 배로 늘어난다).</summary>
        public TimeSpan FetchDelay { get; set; }

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

        /// <summary>뒤 갱신의 이어 읽기 잠금 기다림(<see cref="CachedMetadataProvider.BackgroundBusyWait"/>).</summary>
        public TimeSpan ResumeWait
        {
            get => BackgroundBusyWait;
            set => BackgroundBusyWait = value;
        }

        /// <summary>
        /// 있으면 Oracle 처럼 읽는다: 질의 시작(커서 열기)에 잠금을 한 번 잡고 놓은 뒤, <see cref="SessionWait.Resume"/> 범위에서 행마다 다시 잡는다.
        /// </summary>
        public SessionCommandGate? Gate { get; set; }

        /// <summary>행 하나를 읽고 잠금을 놓은 뒤 부른다(행 번호). <see cref="Gate"/> 가 있을 때만.</summary>
        public Func<int, Task>? AfterRow { get; set; }

        /// <summary>서버 목록 스냅샷을 잡은 뒤(읽기 시작) 부른다. 돌려준 작업이 끝나야 읽기가 끝난다.</summary>
        public Func<ObjectQuery, Task>? FetchHook { get; set; }

        /// <summary><see cref="BusyTimes"/> 를 다 쓴 다음 읽기가 이 예외로 실패한다(한 번).</summary>
        public Exception? FailWith { get; set; }

        /// <summary>읽기마다 쓴 세션 Id.</summary>
        public List<Guid> Sessions { get; } = [];

        /// <summary>바쁨(<see cref="SessionBusyException"/>)으로 끝난 읽기 수.</summary>
        public int BusySeen { get; private set; }

        /// <summary>처음 바쁨으로 끝난 때.</summary>
        public TaskCompletionSource FirstBusy { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WhenBackgroundDoneAsync(string dbKey) => BackgroundRefreshCompletion(dbKey);

        public Task<IReadOnlyList<MetaObject>> RefreshAsync(ISession s) => RefreshObjectListAsync(s, default);

        public async Task StoreAsync<T>(ISession s, DbObjectRef obj, string field, T value)
            => await StorePayloadAsync(s, await FindCachedAsync(s, obj, default), field, value, default);

        public Task<MetaObject?> FindAsync(ISession s, DbObjectRef obj) => FindCachedAsync(s, obj, default);

        public bool Read<T>(string json, string field, out T? value) => TryReadPayload(json, field, out value);

        protected override async Task<IReadOnlyList<MetaObject>> FetchObjectsAsync(ISession s, ObjectQuery q, DateTimeOffset fetchedAt, CancellationToken ct)
        {
            Fetched.Add(q);
            Sessions.Add(s.Id);
            var snapshot = Server.ToList();
            if (FetchHook is not null)
            {
                await FetchHook(q);
            }

            if (FetchDelay > TimeSpan.Zero)
            {
                await Task.Delay(FetchDelay, ct);
            }

            if (BusyTimes > 0)
            {
                BusyTimes--;
                throw Busy(new SessionBusyException());
            }

            if (FailWith is { } fail)
            {
                FailWith = null;
                throw fail;
            }

            List<MetaObject> rows =
            [
                .. snapshot
                    .Where(o => (q.Owner is null || o.Owner == q.Owner) && (!q.InvalidOnly || o.Status != "VALID")
                        && (q.ObjectTypes.Count == 0 || q.ObjectTypes.Contains(o.Type)))
                    .Select(o => o with { DbKey = s.DbKey, FetchedAt = fetchedAt }),
            ];
            if (Gate is { } gate)
            {
                await ReadThroughGateAsync(gate, rows.Count, ct);
            }

            return rows;
        }

        /// <summary>Oracle QueryAsync 처럼: 커서를 여는 잠금(지금 한도) → 놓기 → 이어 읽기 범위에서 행마다(끝 확인 포함) 잡고 놓기.</summary>
        private async Task ReadThroughGateAsync(SessionCommandGate gate, int count, CancellationToken ct)
        {
            try
            {
                await gate.EnterAsync(ct);
                gate.Release();
                using var resume = SessionWait.Resume();
                for (var i = 0; i <= count; i++)
                {
                    await gate.EnterAsync(ct);
                    gate.Release();
                    if (i < count && AfterRow is not null)
                    {
                        await AfterRow(i);
                    }
                }
            }
            catch (SessionBusyException ex)
            {
                Busy(ex);
                throw;
            }
        }

        private SessionBusyException Busy(SessionBusyException ex)
        {
            BusySeen++;
            FirstBusy.TrySetResult();
            return ex;
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

        /// <summary>객체 목록의 최대 유효기간(설정). null = 기한 없음.</summary>
        public TimeSpan? MaxAge { get; set; }

        public TimeSpan? ObjectListMaxAge => MaxAge;

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
