using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Ledger;
using Oranda.Testing;
using Oranda.Testing.Rollback;

namespace Oranda.Core.Tests.Rollback;

/// <summary>원장(되돌리기 지점 설계 7.3·7.4·11.2, 16.1 「원장」).</summary>
public class RollbackLedgerTests
{
    private static readonly Guid Sid = Guid.NewGuid();
    private static readonly DbObjectRef Products = new("SAMPLE", "PRODUCTS", "TABLE");

    private static RollbackStatementRecord Stmt(string text, RollbackScriptState state = RollbackScriptState.Open, int steps = 1,
                                                IReadOnlyList<string>? touched = null, long? locked = null, long rows = 1) => new()
    {
        SessionId = Sid, ProfileId = Guid.Empty, ProfileName = "prod-example", DbKey = "K", Environment = ConnectionEnvironment.Production,
        Text = text, Verb = DmlVerb.Update, Table = Products, State = state, RowsAffected = rows, LockedRows = locked,
        Steps = [.. Enumerable.Range(0, steps).Select(i => new RollbackStep($"UPDATE /*{text}{i}*/"))],
        Probes = [new RollbackProbe("c " + text, "r " + text)],
        BeforePoint = new ChangePoint(100, "SCN", DateTimeOffset.UnixEpoch, "q"),
        TouchedRowIds = touched ?? [],
    };

    private static TransactionEnded End(TransactionEndKind kind, TransactionEndSource source = TransactionEndSource.Statement, string? sp = null) =>
        new(Sid, kind, source) { Savepoint = sp, CommitPoint = new ChangePoint(200, "SCN", DateTimeOffset.UnixEpoch, "q") };

    private static (RollbackLedger Ledger, InMemoryRollbackStore Store, FakeEventBus Bus) Make()
    {
        var store = new InMemoryRollbackStore();
        var bus = new FakeEventBus();
        return (new RollbackLedger(store, bus), store, bus);
    }

    [Fact]
    public async Task Commit_confirms_group_with_points_and_document()
    {
        var (ledger, store, bus) = Make();
        ledger.Record(Stmt("A"));
        ledger.Record(Stmt("B", steps: 2));
        Assert.Equal(new RollbackOpenState(2, 3, false, false), ledger.Open(Sid));

        await ledger.EndAsync(End(TransactionEndKind.Commit), default);

        var g = Assert.Single(store.Groups);
        Assert.Equal((RollbackScriptState.Confirmed, 3, 100L, 200L), (g.State, g.StepCount, g.BasePoint!.Number, g.CommitPoint!.Number));
        Assert.Equal([1, 2], store.Document(g.Id)!.Statements.Select(s => s.Seq));
        Assert.Null(ledger.Open(Sid));
        Assert.Contains(bus.Published, e => e is RollbackGroupSaved { State: RollbackScriptState.Confirmed });
    }

    [Fact]
    public async Task Rollback_invalidates_without_document()
    {
        var (ledger, store, _) = Make();
        ledger.Record(Stmt("A"));
        await ledger.EndAsync(End(TransactionEndKind.Rollback), default);

        var g = Assert.Single(store.Groups);
        Assert.Equal(RollbackScriptState.Invalid, g.State);
        Assert.Null(store.Document(g.Id));
    }

    [Fact]
    public async Task Savepoints_invalidate_only_later_statements_and_move_on_reuse()
    {
        var (ledger, store, _) = Make();
        ledger.Record(Stmt("A"));
        ledger.MarkSavepoint(Sid, "SP1");
        ledger.Record(Stmt("B"));
        ledger.MarkSavepoint(Sid, "SP2");
        ledger.Record(Stmt("C"));
        ledger.MarkSavepoint(Sid, "SP1"); // 같은 이름 = 자리 옮김
        ledger.Record(Stmt("D"));
        await ledger.EndAsync(End(TransactionEndKind.RollbackToSavepoint, sp: "SP1"), default);
        Assert.Equal(3, ledger.Open(Sid)!.Statements);

        await ledger.EndAsync(End(TransactionEndKind.RollbackToSavepoint, sp: "SP2"), default);
        Assert.Equal(2, ledger.Open(Sid)!.Statements);
        await ledger.EndAsync(End(TransactionEndKind.Commit), default);

        var g = Assert.Single(store.Groups);
        Assert.Equal(RollbackScriptState.Confirmed, g.State);
        Assert.Equal([false, false, true, true], g.Statements.Select(s => s.Invalidated));
        Assert.Equal("ROLLBACK TO SP2 로 되돌림", g.Statements[2].Reason);
        Assert.True(store.Document(g.Id)!.Statements[3].Invalidated);
    }

    [Fact]
    public async Task Unknown_savepoint_invalidates_whole_group()
    {
        var (ledger, store, _) = Make();
        ledger.Record(Stmt("A"));
        await ledger.EndAsync(End(TransactionEndKind.RollbackToSavepoint, sp: "OUTER"), default);
        await ledger.EndAsync(End(TransactionEndKind.Commit), default);
        Assert.Equal(RollbackScriptState.Invalid, Assert.Single(store.Groups).State);
    }

    [Theory]
    [InlineData(TransactionEndKind.Commit, RollbackScriptState.Confirmed)]
    [InlineData(TransactionEndKind.Rollback, RollbackScriptState.Invalid)]
    [InlineData(TransactionEndKind.Unknown, RollbackScriptState.Unverified)]
    public async Task Probed_end_of_ddl_or_plsql(TransactionEndKind probed, RollbackScriptState expected)
    {
        var (ledger, store, _) = Make();
        ledger.Record(Stmt("A"));
        Assert.Equal(["c A"], ledger.Probes(Sid).Select(p => p.CommittedIfOne));
        await ledger.EndAsync(End(probed, TransactionEndSource.Ddl), default);
        Assert.Equal(expected, Assert.Single(store.Groups).State);
    }

    [Fact]
    public async Task Unknown_end_without_script_is_assumed_commit()
    {
        var (ledger, store, _) = Make();
        ledger.Record(Stmt("A", RollbackScriptState.NotCreated, steps: 0) with { Reason = NotCreatedReason.NotProduction });
        await ledger.EndAsync(End(TransactionEndKind.Unknown, TransactionEndSource.SessionClosed), default);

        var g = Assert.Single(store.Groups);
        Assert.Equal((RollbackScriptState.NotCreated, TransactionEndKind.Commit, "커밋(추정)"), (g.State, g.EndKind, g.StateReason));
        Assert.Equal(NotCreatedReason.NotProduction, g.Reason);
    }

    [Fact]
    public async Task Session_broken_invalidates_and_closed_with_script_needs_check()
    {
        var (ledger, store, _) = Make();
        ledger.Record(Stmt("A"));
        await ledger.EndAsync(End(TransactionEndKind.Rollback, TransactionEndSource.SessionBroken), default);
        ledger.Record(Stmt("B"));
        await ledger.EndAsync(End(TransactionEndKind.Unknown, TransactionEndSource.SessionClosed), default);

        Assert.Equal([RollbackScriptState.Invalid, RollbackScriptState.Unverified], store.Groups.Select(g => g.State));
        Assert.Equal("접속 닫힘", store.Groups[1].StateReason);
    }

    [Fact]
    public async Task Before_end_saves_open_group_only_when_changed()
    {
        var (ledger, store, _) = Make();
        await ledger.BeforeEndAsync(Sid, default);
        Assert.Equal(0, store.SaveCount);

        ledger.Record(Stmt("A"));
        await ledger.BeforeEndAsync(Sid, default);
        await ledger.BeforeEndAsync(Sid, default);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(RollbackScriptState.Open, Assert.Single(store.Groups).State);

        await ledger.EndAsync(End(TransactionEndKind.Commit), default);
        var g = Assert.Single(store.Groups); // 같은 Id 를 덮어씀
        Assert.Equal(RollbackScriptState.Confirmed, g.State);
    }

    [Fact]
    public async Task Save_failure_is_swallowed()
    {
        var (ledger, store, _) = Make();
        store.FailSaves = true;
        ledger.Record(Stmt("A"));
        await ledger.BeforeEndAsync(Sid, default);
        await ledger.EndAsync(End(TransactionEndKind.Commit), default);
        Assert.Empty(store.Groups);
    }

    [Fact]
    public async Task Incomplete_mismatch_and_no_pk_overlap_aggregate()
    {
        var (ledger, store, _) = Make();
        ledger.Record(Stmt("A", locked: 12, rows: 13));
        Assert.True(ledger.Open(Sid)!.HasMismatch);
        ledger.Record(Stmt("B", touched: ["R1", "R2"]));
        ledger.Record(Stmt("C", touched: ["R2"]));
        Assert.True(ledger.Open(Sid)!.HasIncomplete);
        await ledger.EndAsync(End(TransactionEndKind.Commit), default);

        var g = Assert.Single(store.Groups);
        Assert.Equal(RollbackScriptState.Incomplete, g.State);
        Assert.Contains("PK 없는 행을 두 번 바꿈", g.StateReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Derived_group_follows_next_end_with_origin()
    {
        var (ledger, store, _) = Make();
        ledger.Record(Stmt("A"));
        ledger.RecordDerived(Sid, RollbackOrigin.RollbackRun, 318, Stmt("rev"));
        await ledger.EndAsync(End(TransactionEndKind.Commit), default);

        Assert.Equal(2, store.Groups.Count);
        var derived = Assert.Single(store.Groups, g => g.Origin == RollbackOrigin.RollbackRun);
        Assert.Equal((318L, RollbackScriptState.Confirmed), (derived.OriginGroupId!.Value, derived.State));
    }

    [Fact]
    public async Task Rollback_after_open_save_empties_the_script_file()
    {
        var (ledger, store, _) = Make();
        ledger.Record(Stmt("A"));
        await ledger.BeforeEndAsync(Sid, default);
        var id = Assert.Single(store.Groups).Id;
        Assert.NotEmpty(store.Document(id)!.Statements);

        await ledger.EndAsync(End(TransactionEndKind.Rollback), default);
        Assert.Equal(RollbackScriptState.Invalid, Assert.Single(store.Groups).State);
        Assert.Empty(store.Document(id)!.Statements);
    }

    [Fact]
    public async Task Statement_without_script_next_to_scripted_ones_makes_group_incomplete()
    {
        var (ledger, store, _) = Make();
        ledger.Record(Stmt("A"));
        ledger.Record(Stmt("MERGE", RollbackScriptState.NotCreated, steps: 0) with { Reason = NotCreatedReason.NotTarget });
        await ledger.EndAsync(End(TransactionEndKind.Commit), default);

        var g = Assert.Single(store.Groups);
        Assert.Equal(RollbackScriptState.Incomplete, g.State);
        Assert.Contains("원복 문장을 만들지 않은 문장 1개", g.StateReason, StringComparison.Ordinal);
        Assert.Equal("대상 아님", g.Statements[1].Reason);
    }

    [Fact]
    public async Task Savepoint_names_ignore_case_later_savepoints_vanish_and_commit_clears_them()
    {
        var (ledger, store, _) = Make();
        ledger.MarkSavepoint(Sid, "sp1");
        ledger.MarkSavepoint(Sid, "SP2"); // 같은 자리, 뒤에 건 저장점
        ledger.Record(Stmt("A"));
        await ledger.EndAsync(End(TransactionEndKind.RollbackToSavepoint, sp: "SP1"), default);
        Assert.Null(ledger.Open(Sid)); // 모든 문장 무효 = 열린 묶음 없음으로 본다

        ledger.Record(Stmt("B"));
        await ledger.EndAsync(End(TransactionEndKind.RollbackToSavepoint, sp: "SP2"), default); // SP2 는 없어졌다 = 모르는 저장점
        await ledger.EndAsync(End(TransactionEndKind.Commit), default);
        Assert.Equal(RollbackScriptState.Invalid, Assert.Single(store.Groups).State);
        Assert.Equal("모든 문장을 저장점까지 롤백함", store.Groups[0].StateReason);

        ledger.Record(Stmt("C"));
        await ledger.EndAsync(End(TransactionEndKind.RollbackToSavepoint, sp: "SP1"), default); // 커밋이 저장점을 지웠다
        await ledger.EndAsync(End(TransactionEndKind.Commit), default);
        Assert.Equal(RollbackScriptState.Invalid, store.Groups[1].State);
    }

    [Fact]
    public async Task Prune_failure_does_not_lose_the_saved_id()
    {
        var (ledger, store, bus) = Make();
        store.FailPrunes = true;
        for (var i = 0; i < RollbackLedger.PruneEvery - 1; i++)
        {
            ledger.Record(Stmt("x" + i));
            await ledger.EndAsync(End(TransactionEndKind.Commit), default);
        }

        ledger.Record(Stmt("last"));
        await ledger.BeforeEndAsync(Sid, default); // 100번째 저장 + 정리 실패
        await ledger.EndAsync(End(TransactionEndKind.Commit), default);

        Assert.Equal(RollbackLedger.PruneEvery, store.Groups.Count);
        Assert.Equal(RollbackLedger.PruneEvery, bus.PublishedOf<RollbackGroupSaved>().Count());
    }

    [Fact]
    public async Task Without_store_nothing_is_saved_and_nothing_throws()
    {
        var ledger = new RollbackLedger(null, null);
        ledger.Record(Stmt("A"));
        await ledger.BeforeEndAsync(Sid, default);
        await ledger.EndAsync(End(TransactionEndKind.Commit), default);
        Assert.Null(ledger.Open(Sid));
    }
}
