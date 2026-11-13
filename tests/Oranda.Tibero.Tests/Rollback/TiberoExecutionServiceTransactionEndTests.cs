using System.Data.Common;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Execution.Providers;
using Oranda.Core.Rollback;
using Oranda.Core.Sessions;
using Oranda.Testing.Rollback;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Rollback;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Driver;

namespace Oranda.Tibero.Tests.Rollback;

/// <summary>
/// 실행 서비스가 보낸 문장 뒤의 트랜잭션 끝(설계 7.3·7.4, 판단 = <c>RollbackFlow</c>, 가짜 드라이버 + 기록 원장·엔진).
/// </summary>
[Collection("FakeDriver")]
public sealed class TiberoExecutionServiceTransactionEndTests : IDisposable
{
    private const string Update = "UPDATE T SET A = 1";
    private const string Block = "BEGIN P; END;";

    private static readonly ServerInfo Server = new("6", "i", "h", null, "D", null, false, 7,
        new ServerCapabilities(false, false, true, false, false, Dbms.Tibero), DbUniqueName: "D", SessionUser: "U", Dbms: Dbms.Tibero);

    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();

    private readonly EventBus _bus = new();
    private readonly List<string> _sent = [];
    private readonly List<TransactionEnded> _published = [];
    private readonly RecordingRollbackLedger _ledger = new() { OpenState = new RollbackOpenState(1, 1, false, false) };
    private readonly RecordingRollbackCapture _capture = new() { Point = new ChangePoint(77, "TSN", DateTimeOffset.UnixEpoch, "m") };
    private bool _pending;

    public TiberoExecutionServiceTransactionEndTests()
    {
        FakeDriverHost.Reset();
        _bus.Subscribe<TransactionEnded>(_published, e =>
        {
            lock (_published)
            {
                _published.Add(e);
            }
        });
    }

    public void Dispose() => FakeDriverHost.Reset();

    [Fact]
    public async Task Commit_statement_saves_the_open_group_first_then_reads_the_commit_point_and_ends_the_group()
    {
        Serve();
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt("COMMIT", StatementKind.TransactionControl), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal("BeforeEndAsync", _ledger.Calls[0]);
        Assert.Equal(["ReadPoint"], _capture.Calls);
        var ended = Assert.Single(_ledger.Ends);
        Assert.Equal((TransactionEndKind.Commit, TransactionEndSource.Statement, 77L), (ended.Kind, ended.Source, ended.CommitPoint!.Number));
        Assert.Same(ended, Assert.Single(_published));
    }

    [Fact]
    public async Task Savepoint_is_marked_and_rollback_to_it_ends_only_that_part_without_saving_first()
    {
        Serve();
        var service = NewService();
        await using var session = NewSession();

        await service.ExecuteAsync(session, Stmt("SAVEPOINT SP1", StatementKind.TransactionControl), NoBinds, null, CancellationToken.None);
        await service.ExecuteAsync(session, Stmt("ROLLBACK TO SAVEPOINT SP1", StatementKind.TransactionControl), NoBinds, null, CancellationToken.None);

        Assert.Equal(["SP1"], _ledger.Savepoints.Select(s => s.Name));
        Assert.DoesNotContain("BeforeEndAsync", _ledger.Calls);
        var ended = Assert.Single(_ledger.Ends);
        Assert.Equal((TransactionEndKind.RollbackToSavepoint, "SP1"), (ended.Kind, ended.Savepoint));
        Assert.Empty(_capture.Calls.Where(c => c is "ReadPoint" or "ProbeEnd"));
    }

    [Fact]
    public async Task Plsql_that_ends_the_open_transaction_is_probed_and_recorded_as_the_probed_end()
    {
        _capture.ProbeResult = TransactionEndKind.Commit;
        Serve();
        var service = NewService();
        await using var session = NewSession();
        _pending = true;
        await service.ExecuteAsync(session, Stmt(Update, StatementKind.Dml), NoBinds, null, CancellationToken.None);
        Assert.True(session.HasPendingTransaction);

        _pending = false; // 블록 안 COMMIT
        await service.ExecuteAsync(session, Stmt(Block, StatementKind.PlSql), NoBinds, null, CancellationToken.None);

        var ended = Assert.Single(_ledger.Ends);
        Assert.Equal((TransactionEndKind.Commit, TransactionEndSource.PlSql), (ended.Kind, ended.Source));
        Assert.Equal(["ProbeEnd", "ReadPoint"], _capture.Calls.Where(c => c is "ReadPoint" or "ProbeEnd"));
    }

    [Fact]
    public async Task Ddl_without_a_pending_transaction_or_open_group_is_not_an_end_and_reads_nothing_more()
    {
        _ledger.OpenState = null;
        Serve();
        var service = NewService();
        await using var session = NewSession();

        await service.ExecuteAsync(session, Stmt("CREATE TABLE X (A NUMBER)", StatementKind.Ddl), NoBinds, null, CancellationToken.None);

        Assert.Empty(_ledger.Ends);
        Assert.Empty(_capture.Calls);
        Assert.Equal(["CREATE TABLE X (A NUMBER)", TiberoExecutionService.TransactionIdSql], Sent());
    }

    [Fact]
    public async Task Ddl_after_an_uncommitted_dml_ends_the_open_group_as_the_probed_end()
    {
        _capture.ProbeResult = TransactionEndKind.Commit;
        Serve();
        var service = NewService();
        await using var session = NewSession();
        _pending = true;
        await service.ExecuteAsync(session, Stmt(Update, StatementKind.Dml), NoBinds, null, CancellationToken.None);

        _pending = false; // DDL 앞 암묵 커밋
        await service.ExecuteAsync(session, Stmt("CREATE TABLE X (A NUMBER)", StatementKind.Ddl), NoBinds, null, CancellationToken.None);

        var ended = Assert.Single(_ledger.Ends);
        Assert.Equal((TransactionEndKind.Commit, TransactionEndSource.Ddl), (ended.Kind, ended.Source));
    }

    [Fact]
    public async Task Plsql_with_an_inner_commit_ends_the_group_it_opened_even_without_a_pending_transaction_before()
    {
        _capture.ProbeResult = TransactionEndKind.Commit;
        Serve(); // 트랜잭션 id 는 늘 NULL(블록 안 COMMIT)
        var service = NewService();
        await using var session = NewSession();

        await service.ExecuteAsync(session, Stmt(Block, StatementKind.PlSql), NoBinds, null, CancellationToken.None);

        var ended = Assert.Single(_ledger.Ends);
        Assert.Equal((TransactionEndKind.Commit, TransactionEndSource.PlSql), (ended.Kind, ended.Source));
    }

    [Fact]
    public async Task Session_closed_while_a_statement_that_can_commit_runs_ends_the_group_as_unknown_closed()
    {
        TiberoSession? running = null;
        FakeDriverHost.SetHandler(cmd =>
        {
            if (cmd.CommandText.StartsWith("CREATE", StringComparison.Ordinal))
            {
                _ = running!.DisposeAsync().AsTask(); // 다른 쪽(탭 닫기)이 닫는다
                throw FakeDriverHost.ClientError(90603, "ODP-90603: closed connection");
            }

            return null;
        });
        var service = NewService();
        running = NewSession();

        await service.ExecuteAsync(running, Stmt("CREATE TABLE X (A NUMBER)", StatementKind.Ddl), NoBinds, null, CancellationToken.None);

        for (var i = 0; i < 300 && _ledger.Ends.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        var ended = Assert.Single(_ledger.Ends);
        Assert.Equal((TransactionEndKind.Unknown, TransactionEndSource.SessionClosed), (ended.Kind, ended.Source));
    }

    private TiberoExecutionService NewService() =>
        new(new AllowAll(), new RecordingRollbackInteraction(), _bus, null, dbmsOutput: false, capture: _capture);

    private static SqlStatement Stmt(string text, StatementKind kind) => new(text, default, kind);

    private TiberoSession NewSession()
    {
        var driver = FakeDriverHost.Driver;
        var connection = driver.CreateConnection("Data Source=x");
        connection.Open();
        var profile = new ConnectionProfile("t", "u", "h:1/d", Dbms: Dbms.Tibero);
        return new TiberoSession(Guid.NewGuid(), profile, Server, SessionPurpose.Work, "D/-/U", connection, null, _bus, driver: driver,
            rollback: new TiberoRollbackHooks(_capture, _ledger, _bus));
    }

    /// <summary>트랜잭션 id 질의는 <see cref="_pending"/> 이면 id, 아니면 NULL. 그 밖의 문장은 영향 행 1.</summary>
    private void Serve() =>
        FakeDriverHost.SetHandler(cmd =>
        {
            lock (_sent)
            {
                _sent.Add(cmd.CommandText);
            }

            return cmd.CommandText == TiberoExecutionService.TransactionIdSql ? (_pending ? "1.2.3" : null) : 1;
        });

    private List<string> Sent()
    {
        lock (_sent)
        {
            return [.. _sent];
        }
    }

    private sealed class AllowAll : IExecutionPolicy
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) => ValueTask.FromResult(PolicyDecision.Allowed);
    }
}
