using Oranda.App.Features.TableEdit;
using Oranda.App.Tests.Features.BlockerAlert;
using Oranda.Core.Connection;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.App.Tests.Features.TableEdit;

// 소유: te-l1(T16b). 새 테이블 작업 저장 — 30초 자동 저장·닫기·버리기·되살리기 목록·두 인스턴스(설계 18.7, TE-43).
public sealed class TableDraftAutosaveTests
{
    private const string Me = "100@1000";
    private const string LiveOther = "200@1000";
    private const string DeadOther = "300@1000";

    private readonly FakeTableDraftStore _store = new();
    private readonly FakeTimeProvider _time = new();
    private readonly TableDraftLibrary _library;
    private TableDraft _current = Empty();

    public TableDraftAutosaveTests()
        => _library = new TableDraftLibrary(_store) { Time = _time, Instance = Me, IsInstanceAlive = i => i is Me or LiveOther };

    private static TableDraft Empty() => new("SAMPLE", string.Empty) { IsNewTable = true };

    private static TableDraft Named(string name) => Empty() with
    {
        Name = name,
        Columns = [new ColumnDraft("n1", "ID") { DataType = "NUMBER" }],
    };

    private TableDraftAutosave BeginNew() => _library.Begin("dev-example", Dbms.Oracle, _current, () => _current);

    private async Task TickAsync(TableDraftAutosave autosave)
    {
        _time.Advance(TableDraftAutosave.Interval);
        await autosave.LastTick;
    }

    private async Task<TableDraftEntry> SeedAsync(string name, string? openBy, string owner = "SAMPLE")
        => (await _store.SaveAsync(null, "dev-example", Dbms.Oracle, Named(name) with { Owner = owner }, openBy ?? "seed", openBy is not null, default))!;

    [Fact]
    public async Task Untouched_new_tab_writes_nothing()
    {
        var autosave = BeginNew();

        await TickAsync(autosave);
        await TickAsync(autosave);

        Assert.Equal(0, _store.Saves);
        Assert.Null(autosave.DraftId);
        Assert.False(autosave.HasUnsavedChanges);
    }

    [Fact]
    public async Task Timer_saves_every_30_seconds_only_when_changed()
    {
        var autosave = BeginNew();
        _current = Named("ORDERS");

        _time.Advance(TimeSpan.FromSeconds(29));
        await autosave.LastTick;
        Assert.Equal(0, _store.Saves); // 아직 30초 전

        _time.Advance(TimeSpan.FromSeconds(1));
        await autosave.LastTick;
        Assert.Equal(1, _store.Saves);
        var id = Assert.IsType<long>(autosave.DraftId);
        Assert.Equal(Me, _store.Rows[id].Entry.OpenBy);
        Assert.Equal("ORDERS", _store.Rows[id].Entry.Name);

        await TickAsync(autosave); // 그대로면 안 씀
        Assert.Equal(1, _store.Saves);

        _current = Named("ORDER_ITEMS");
        Assert.True(autosave.HasUnsavedChanges);
        await TickAsync(autosave);
        Assert.Equal(2, _store.Saves);
        Assert.Equal(id, autosave.DraftId); // 같은 행을 덮어씀
        Assert.Single(_store.Rows);
    }

    [Fact]
    public async Task Close_keep_saves_and_releases()
    {
        var autosave = BeginNew();
        _current = Named("ORDERS");

        await autosave.CloseAsync(keep: true, default);

        var row = Assert.Single(_store.Rows.Values);
        Assert.Null(row.Entry.OpenBy);
        Assert.Equal("ORDERS", row.Draft.Name);
        Assert.True(autosave.IsEnded);
        Assert.Equal(0, _time.ActiveTimers);

        _current = Named("LATER");
        _time.Advance(TableDraftAutosave.Interval * 3);
        Assert.Equal(1, _store.Saves); // 닫은 뒤 타이머 없음
    }

    [Fact]
    public async Task Close_keep_without_changes_only_releases()
    {
        var autosave = BeginNew();
        _current = Named("ORDERS");
        await TickAsync(autosave);

        await autosave.CloseAsync(keep: true, default);

        Assert.Equal(1, _store.Saves);
        Assert.Null(Assert.Single(_store.Rows.Values).Entry.OpenBy);
    }

    [Fact]
    public async Task Discard_and_successful_run_delete_the_row()
    {
        var autosave = BeginNew();
        _current = Named("ORDERS");
        await TickAsync(autosave);
        Assert.Single(_store.Rows);

        await autosave.DiscardAsync(default); // 「버리기」·실행 성공

        Assert.Empty(_store.Rows);
        Assert.True(autosave.IsEnded);
        Assert.False(await autosave.SaveIfChangedAsync(default)); // 끝난 뒤 다시 쓰지 않음
        await autosave.CloseAsync(keep: true, default);
        Assert.Empty(_store.Rows);
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public async Task Close_discard_of_a_never_saved_tab_touches_nothing()
    {
        var autosave = BeginNew();
        _current = Named("ORDERS");

        await autosave.CloseAsync(keep: false, default);

        Assert.Equal(0, _store.Saves);
        Assert.Equal(0, _store.Deletes);
    }

    [Fact]
    public async Task Failed_tick_is_retried_on_the_next_tick()
    {
        var autosave = BeginNew();
        _current = Named("ORDERS");
        _store.FailNext = true;

        await TickAsync(autosave); // 실패는 삼킨다
        Assert.Empty(_store.Rows);
        Assert.True(autosave.HasUnsavedChanges);

        await TickAsync(autosave);
        Assert.Single(_store.Rows);
    }

    [Fact]
    public async Task Failed_close_or_discard_throws_and_keeps_autosave_running()
    {
        var autosave = BeginNew();
        _current = Named("ORDERS");
        await TickAsync(autosave);

        _store.FailNext = true;
        await Assert.ThrowsAsync<IOException>(() => autosave.DiscardAsync(default));
        Assert.False(autosave.IsEnded);
        Assert.Single(_store.Rows);

        _current = Named("ORDERS2");
        _store.FailNext = true;
        await Assert.ThrowsAsync<IOException>(() => autosave.CloseAsync(keep: true, default));
        Assert.False(autosave.IsEnded);
        Assert.Equal(1, _time.ActiveTimers);

        await TickAsync(autosave); // 탭이 남으면 자동 저장도 계속
        Assert.Equal("ORDERS2", Assert.Single(_store.Rows.Values).Draft.Name);
        await autosave.DiscardAsync(default); // 다시 하면 된다
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task Read_only_store_keeps_trying_without_an_id()
    {
        _store.Writable = false;
        var autosave = BeginNew();
        _current = Named("ORDERS");

        Assert.False(await autosave.SaveIfChangedAsync(default));
        Assert.Null(autosave.DraftId);
        Assert.True(autosave.HasUnsavedChanges);
    }

    [Fact]
    public async Task Tick_while_closing_does_not_write_after_the_close()
    {
        var autosave = BeginNew();
        _current = Named("ORDERS");
        await TickAsync(autosave);
        _current = Named("ORDERS2");

        var hold = new TaskCompletionSource();
        _store.Hold = hold;
        var closing = autosave.CloseAsync(keep: true, default); // 저장소에서 멈춤(게이트를 쥔 채)
        _time.Advance(TableDraftAutosave.Interval); // 그 사이 타이머
        var tick = autosave.LastTick;
        Assert.False(tick.IsCompleted); // 게이트에서 기다림
        hold.SetResult();
        await closing;
        await tick;

        Assert.Equal(2, _store.Saves); // 닫기 저장 하나만 더
        Assert.Null(Assert.Single(_store.Rows.Values).Entry.OpenBy); // 틱이 다시 쥐지 않음
    }

    [Fact]
    public async Task Restore_list_has_rows_left_open_by_ended_instances_only()
    {
        var dead = await SeedAsync("DEAD", DeadOther);
        await SeedAsync("LIVE", LiveOther);
        await SeedAsync("MINE", Me);
        await SeedAsync("KEPT", null);

        Assert.Equal([dead.Id], (await _library.ListForRestoreAsync(default)).Select(e => e.Id));
    }

    [Fact]
    public async Task Owner_list_has_kept_and_dead_rows_of_that_owner()
    {
        var kept = await SeedAsync("KEPT", null);
        var dead = await SeedAsync("DEAD", DeadOther);
        await SeedAsync("LIVE", LiveOther);
        await SeedAsync("MINE", Me);
        await SeedAsync("OTHER_OWNER", null, owner: "OTHER");

        var ids = (await _library.ListForOwnerAsync("dev-example", "SAMPLE", default)).Select(e => e.Id).Order();

        Assert.Equal(new[] { kept.Id, dead.Id }.Order(), ids);
    }

    [Fact]
    public async Task Resume_claims_the_row_once_and_overwrites_it()
    {
        var kept = await SeedAsync("KEPT", null);
        var stored = (await _library.GetAsync(kept.Id, default))!;
        _current = stored.Draft;

        var autosave = await _library.ResumeAsync(stored, () => _current, default);

        Assert.NotNull(autosave);
        Assert.Equal(Me, _store.Rows[kept.Id].Entry.OpenBy); // 앱을 끄면 다음에 탭으로 되살아난다
        Assert.Null(await _library.ResumeAsync(stored, () => _current, default)); // 두 번째 탭은 못 엶
        Assert.Empty(await _library.ListForOwnerAsync("dev-example", "SAMPLE", default));

        await TickAsync(autosave);
        Assert.Equal(1, _store.Saves); // 되살린 그대로면 안 씀
        _current = Named("KEPT2");
        await TickAsync(autosave);
        Assert.Equal(kept.Id, autosave.DraftId);
        Assert.Equal("KEPT2", _store.Rows[kept.Id].Draft.Name);
        Assert.Single(_store.Rows);
    }

    [Fact]
    public async Task Resume_fails_when_another_instance_took_the_row_after_listing()
    {
        var dead = await SeedAsync("DEAD", DeadOther);
        var stored = (await _library.GetAsync(dead.Id, default))!;
        Assert.True(await _store.ClaimAsync(dead.Id, DeadOther, LiveOther, default)); // 다른 앱이 먼저 되살림

        Assert.Null(await _library.ResumeAsync(stored, () => stored.Draft, default));
        Assert.Equal(LiveOther, _store.Rows[dead.Id].Entry.OpenBy);
    }

    [Fact]
    public async Task Delete_from_list_skips_rows_held_by_live_instances_or_changed_since_listing()
    {
        var kept = await SeedAsync("KEPT", null);
        var live = await SeedAsync("LIVE", LiveOther);
        var dead = await SeedAsync("DEAD", DeadOther);

        Assert.False(await _library.DeleteAsync(live, default));
        Assert.True(await _library.DeleteAsync(dead, default));
        Assert.True(await _store.ClaimAsync(kept.Id, null, LiveOther, default)); // 목록을 읽은 뒤 다른 앱이 엶
        Assert.False(await _library.DeleteAsync(kept, default));
        Assert.Equal(2, _store.Rows.Count);
    }

    [Fact]
    public async Task Save_after_another_instance_took_the_row_goes_to_a_new_row()
    {
        var autosave = BeginNew();
        _current = Named("ORDERS");
        await TickAsync(autosave);
        var first = autosave.DraftId!.Value;
        _store.Rows[first] = _store.Rows[first] with { Entry = _store.Rows[first].Entry with { OpenBy = LiveOther } };

        _current = Named("ORDERS2");
        await TickAsync(autosave);

        Assert.NotEqual(first, autosave.DraftId);
        Assert.Equal("ORDERS", _store.Rows[first].Draft.Name); // 남의 것은 덮지 않음
        Assert.Equal("ORDERS2", _store.Rows[autosave.DraftId!.Value].Draft.Name);
    }

    [Fact]
    public async Task Dispose_at_app_exit_leaves_the_row_held_for_the_next_run()
    {
        var autosave = BeginNew();
        _current = Named("ORDERS");
        await TickAsync(autosave);
        var id = autosave.DraftId!.Value;
        _current = Named("ORDERS2");

        await autosave.SaveIfChangedAsync(default); // 앱 끌 때: 쓰고 타이머만 끈다
        await autosave.DisposeAsync();

        Assert.Equal(Me, _store.Rows[id].Entry.OpenBy);
        Assert.Equal("ORDERS2", _store.Rows[id].Draft.Name);
        Assert.Equal(0, _time.ActiveTimers);
        var nextRun = new TableDraftLibrary(_store) { Instance = "101@2000", IsInstanceAlive = i => i == "101@2000" };
        Assert.Equal([id], (await nextRun.ListForRestoreAsync(default)).Select(e => e.Id));
    }

    [Fact]
    public void Instance_of_this_process_is_alive_and_others_are_not()
    {
        Assert.True(TableDraftInstance.IsAlive(TableDraftInstance.Current));
        Assert.StartsWith(Environment.ProcessId + "@", TableDraftInstance.Current, StringComparison.Ordinal);
        Assert.False(TableDraftInstance.IsAlive(TableDraftInstance.Format(Environment.ProcessId, 1))); // 같은 pid, 다른 시작 = 다시 쓰인 pid
        Assert.False(TableDraftInstance.IsAlive(TableDraftInstance.Format(int.MaxValue, 1)));
        Assert.False(TableDraftInstance.IsAlive("garbage"));
        Assert.False(TableDraftInstance.IsAlive(null));
    }
}

/// <summary>메모리 저장소(<c>open_by</c> 조건을 Sqlite 저장소처럼 지킨다). 저장·지우기 횟수를 센다.</summary>
internal sealed class FakeTableDraftStore : ITableDraftStore
{
    private long _next;
    private int _clock;

    public Dictionary<long, StoredTableDraft> Rows { get; } = [];

    public int Saves { get; private set; }

    public int Deletes { get; private set; }

    public bool Writable { get; set; } = true;

    /// <summary>다음 쓰기 한 번을 예외로.</summary>
    public bool FailNext { get; set; }

    /// <summary>값이 있으면 다음 쓰기가 이것이 끝날 때까지 멈춘다.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public bool IsWritable => Writable;

    public Task<IReadOnlyList<TableDraftEntry>> ListAsync(string? connectionName, string? owner, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TableDraftEntry>>(Rows.Values.Select(r => r.Entry)
            .Where(e => (connectionName is null || e.ConnectionName == connectionName) && (owner is null || e.Owner == owner))
            .OrderByDescending(e => e.UpdatedAt).ThenByDescending(e => e.Id).ToList());

    public Task<StoredTableDraft?> GetAsync(long id, CancellationToken ct) => Task.FromResult(Rows.GetValueOrDefault(id));

    public async Task<TableDraftEntry?> SaveAsync(long? id, string connectionName, Dbms dbms, TableDraft draft, string instance, bool keepOpen, CancellationToken ct)
    {
        await BeforeWriteAsync();
        if (!Writable)
        {
            return null;
        }

        Saves++;
        var key = id is { } existing && Rows.TryGetValue(existing, out var row) && row.Entry.OpenBy == instance ? existing : ++_next;
        var entry = new TableDraftEntry(key, connectionName, dbms, draft.Owner, draft.Name, DateTimeOffset.UnixEpoch.AddSeconds(++_clock),
            keepOpen ? instance : null);
        Rows[key] = new StoredTableDraft(entry, draft);
        return entry;
    }

    public async Task<bool> ClaimAsync(long id, string? expectedOpenBy, string instance, CancellationToken ct)
    {
        await BeforeWriteAsync();
        if (!Writable || !Rows.TryGetValue(id, out var row) || row.Entry.OpenBy != expectedOpenBy)
        {
            return false;
        }

        Rows[id] = row with { Entry = row.Entry with { OpenBy = instance } };
        return true;
    }

    public async Task ReleaseAsync(long id, string instance, CancellationToken ct)
    {
        await BeforeWriteAsync();
        if (Writable && Rows.TryGetValue(id, out var row) && row.Entry.OpenBy == instance)
        {
            Rows[id] = row with { Entry = row.Entry with { OpenBy = null } };
        }
    }

    public async Task<bool> DeleteAsync(long id, string? expectedOpenBy, CancellationToken ct)
    {
        await BeforeWriteAsync();
        Deletes++;
        return Writable && Rows.TryGetValue(id, out var row) && row.Entry.OpenBy == expectedOpenBy && Rows.Remove(id);
    }

    private async Task BeforeWriteAsync()
    {
        if (Hold is { } hold)
        {
            Hold = null;
            await hold.Task;
        }

        if (FailNext)
        {
            FailNext = false;
            throw new IOException("disk full");
        }
    }
}
