using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Driver;

namespace Oranda.Tibero.Tests.Execution;

/// <summary>
/// 대량 실행 서비스 <see cref="TiberoBulkExecutionService.ExecuteArrayAsync"/> 단위 시험(TB1 설계 4.3). 가짜 드라이버의 처리기가 보낸 명령을 기록하고
/// 행 수(int)를 돌려준다. 처리기가 정적이라 FakeDriver 컬렉션에 둔다(병렬 금지). BLOB 한 행씩 경로(커밋 6)도 여기서 본다. StreamRowsAsync 는 TiberoBulkStreamTests 에서 본다.
/// </summary>
[Collection("FakeDriver")]
public sealed class TiberoBulkExecutionServiceTests : IDisposable
{
    private static readonly ServerInfo Server = new("6", "i", "h", null, "D", null, false, 7,
        new ServerCapabilities(false, false, true, false, false, Dbms.Tibero), DbUniqueName: "D", SessionUser: "U", Dbms: Dbms.Tibero);

    private static readonly BulkColumn[] Columns =
    [
        new("C1", "NUMBER(38,10)"),
        new("C2", "VARCHAR2(10 CHAR)"),
        new("C3", "DATE"),
        new("C4", "TIMESTAMP(9)"),
        new("C5", "RAW(16)"),
        new("C6", "CLOB"),
    ];

    private static readonly string Insert = "INSERT INTO \"T\" (\"A\", \"B\", \"C\", \"D\", \"E\", \"F\") VALUES ("
        + string.Join(", ", Columns.Select(c => TiberoBulkValueCodec.BindExpression(c.BindName, c.ServerType))) + ")";

    private static readonly BulkColumn[] BlobColumns = [new("C1", "NUMBER"), new("C2", "BLOB")];

    /// <summary>트랜잭션 조회(<see cref="TiberoExecutionService.TransactionIdSql"/>)에 돌려주는 값 = 열린 트랜잭션이 있다.</summary>
    private const string OpenTransactionId = "12.66.243778";

    private const string BlobInsert = "INSERT INTO \"T\" (\"A\", \"B\") VALUES (:C1, :C2)";

    private readonly List<Sent> _sent = [];
    private readonly EventBus _bus = new();
    private readonly List<OrandaEvent> _events = [];

    public TiberoBulkExecutionServiceTests()
    {
        FakeDriverHost.Reset();
        _bus.Subscribe<OrandaEvent>(_events, e =>
        {
            lock (_events)
            {
                _events.Add(e);
            }
        });
    }

    public void Dispose() => FakeDriverHost.Reset();

    [Fact]
    public async Task Rows_go_as_one_array_bind_with_a_text_array_per_column_and_the_kind_per_server_type()
    {
        Serve(cmd => cmd.CommandText == Insert ? 3 : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(Insert), Columns, Rows(3), ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(3, result.RowsAffected);
        Assert.Equal([Insert, TiberoExecutionService.TransactionIdSql], Texts());
        var sent = _sent[0];
        Assert.Equal(3, sent.ArrayBindCount);
        Assert.Equal(["C1", "C2", "C3", "C4", "C5", "C6"], sent.Parameters.Select(p => p.Name));
        Assert.Equal(["Decimal", "Varchar2", "Varchar2", "Varchar2", "Varchar2", "Clob"], sent.Parameters.Select(p => p.Kind));
        Assert.All(sent.Parameters, p => Assert.Equal(3, Assert.IsType<string?[]>(p.Value).Length));
        Assert.Equal(["1.5", null, "-0.0000001"], (string?[])sent.Parameters[0].Value!);
        Assert.Equal(["a", null, "c"], (string?[])sent.Parameters[1].Value!);
        Assert.Equal(["2026-01-02 03:04:05", null, "0001-01-01 00:00:00"], (string?[])sent.Parameters[2].Value!);
        Assert.Equal(["2026-01-02 03:04:05.123456789", null, "9999-12-31 23:59:59.000000000"], (string?[])sent.Parameters[3].Value!);
        Assert.Equal(["0AFF", null, ""], (string?[])sent.Parameters[4].Value!);
        Assert.Equal(["long", null, ""], (string?[])sent.Parameters[5].Value!);

        var executed = Assert.Single(Events<StatementExecuted>());
        Assert.Equal(ExecutionStatus.Succeeded, executed.Result.Status);
        Assert.Equal(3, executed.Result.RowsAffected);
        Assert.Equal(SessionState.Idle, session.State);
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task An_insert_array_that_stops_after_k_rows_without_an_error_fails_as_partial_and_tracks_the_open_transaction()
    {
        Serve(cmd => cmd.CommandText == Insert ? 1 : cmd.CommandText == TiberoExecutionService.TransactionIdSql ? OpenTransactionId : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(Insert), Columns, Rows(3), ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(0, result.RowsAffected);
        Assert.Equal(TiberoBulkExecutionService.PartialCode, result.Error?.Code);
        Assert.Equal(Dbms.Tibero, result.Error?.Dbms);
        Assert.Contains("3행 중 앞 1행", result.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("2번째 행에서", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal([Insert, TiberoExecutionService.TransactionIdSql], Texts());
        var executed = Assert.Single(Events<StatementExecuted>());
        Assert.Equal(ExecutionStatus.Failed, executed.Result.Status);
        Assert.Null(executed.Result.RowsAffected);
        Assert.True(executed.Result.TransactionPending); // 앞 행이 열린 트랜잭션에 남았다
        Assert.True(session.HasPendingTransaction);
    }

    [Fact]
    public async Task A_merge_array_with_fewer_rows_than_sent_fails_as_partial()
    {
        const string merge = "MERGE INTO \"T\" T USING (SELECT :C1 AS \"A\" FROM DUAL) S ON (T.\"A\" = S.\"A\") "
                             + "WHEN NOT MATCHED THEN INSERT (\"A\") VALUES (S.\"A\")";
        Serve(cmd => cmd.CommandText == merge ? 2 : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(merge), [new("C1", "NUMBER")], [[1m], [2m], [3m]],
            ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(TiberoBulkExecutionService.PartialCode, result.Error?.Code);
        Assert.Contains("3행 중 앞 2행", result.Error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("번째 행에서", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_merge_array_that_reports_more_rows_than_sent_fails_as_partial_with_both_counts()
    {
        const string merge = "MERGE INTO \"T\" T USING (SELECT :C1 AS \"A\" FROM DUAL) S ON (T.\"A\" = S.\"A\") "
                             + "WHEN MATCHED THEN UPDATE SET T.\"B\" = 1 WHEN NOT MATCHED THEN INSERT (\"A\") VALUES (S.\"A\")";
        Serve(cmd => cmd.CommandText == merge ? 5 : cmd.CommandText == TiberoExecutionService.TransactionIdSql ? OpenTransactionId : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(merge), [new("C1", "NUMBER")], [[1m], [2m], [3m]],
            ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(0, result.RowsAffected);
        Assert.Equal(TiberoBulkExecutionService.PartialCode, result.Error?.Code);
        Assert.Contains("일치하지 않습니다", result.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("처리 5행", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("보낸 3행", result.Error.Message, StringComparison.Ordinal);
        Assert.True(Assert.Single(Events<StatementExecuted>()).Result.TransactionPending);
    }

    [Fact]
    public async Task An_array_that_reports_exactly_the_rows_sent_succeeds_and_a_negative_count_fails_naming_the_unknown_count()
    {
        Serve(cmd => cmd.CommandText == Insert ? 3 : null);
        var service = NewService();
        await using var session = NewSession();
        var ok = await service.ExecuteArrayAsync(session, Dml(Insert), Columns, Rows(3), ExecutionOptions.Default, CancellationToken.None);

        Serve(cmd => cmd.CommandText == Insert ? -1 : null);
        var unknown = await service.ExecuteArrayAsync(session, Dml(Insert), Columns, Rows(3), ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, ok.Status);
        Assert.Equal(3, ok.RowsAffected);
        Assert.Equal(ExecutionStatus.Failed, unknown.Status);
        Assert.Equal(TiberoBulkExecutionService.PartialCode, unknown.Error?.Code);
        Assert.Contains("처리 행 수를 알리지 않았습니다", unknown.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("3행", unknown.Error.Message, StringComparison.Ordinal);
        Assert.Contains("-1", unknown.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_update_array_is_not_row_count_checked()
    {
        const string update = "UPDATE \"T\" SET \"A\" = :C1";
        Serve(cmd => cmd.CommandText == update ? 0 : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(update), [new("C1", "NUMBER")], [[1m], [2m]],
            ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(0, result.RowsAffected);
    }

    [Fact]
    public async Task One_row_goes_as_scalar_binds_without_array_bind_count()
    {
        Serve(cmd => cmd.CommandText == Insert ? 1 : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(Insert), Columns, [Rows(3)[1]], ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(1, result.RowsAffected);
        var sent = _sent[0];
        Assert.Equal(0, sent.ArrayBindCount);
        Assert.All(sent.Parameters, p => Assert.Same(DBNull.Value, p.Value)); // 가운데 행 = 모두 NULL

        _sent.Clear();
        await service.ExecuteArrayAsync(session, Dml(Insert), Columns, [Rows(3)[0]], ExecutionOptions.Default, CancellationToken.None);
        Assert.Equal("1.5", _sent[0].Parameters[0].Value);
        Assert.Equal("2026-01-02 03:04:05", _sent[0].Parameters[2].Value);
        Assert.Equal("Varchar2", _sent[0].Parameters[2].Kind);
    }

    [Fact]
    public async Task A_server_error_on_a_one_row_insert_is_classified_so_the_failed_row_gets_the_reason()
    {
        Serve(cmd => cmd.CommandText == Insert ? throw FakeDriverHost.ServerError(-10007, "TBR-10007: UNIQUE constraint violation ('U'.'PK').") : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(Insert), Columns, [Rows(3)[0]], ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(0, result.RowsAffected);
        Assert.Equal("TBR-10007", result.Error?.Code);
        Assert.Equal(-10007, result.Error?.Number);
        Assert.Equal(DbErrorKind.ConstraintViolation, result.Error?.Kind);
        Assert.Equal(Dbms.Tibero, result.Error?.Dbms);
        Assert.Equal([Insert, TiberoExecutionService.TransactionIdSql], Texts()); // 실패 뒤에도 트랜잭션 상태를 본다
        Assert.Single(Events<StatementExecuted>());
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task A_one_row_key_only_merge_that_matches_nothing_succeeds_with_zero_but_a_one_row_insert_of_zero_fails()
    {
        const string merge = "MERGE INTO \"T\" T USING (SELECT :C1 AS \"A\" FROM DUAL) S ON (T.\"A\" = S.\"A\") "
                             + "WHEN NOT MATCHED THEN INSERT (\"A\") VALUES (S.\"A\")";
        const string insert = "INSERT INTO \"T\" (\"A\") VALUES (:C1)";
        Serve(cmd => cmd.CommandText is merge or insert ? 0 : null);
        var service = NewService();
        await using var session = NewSession();
        BulkColumn[] columns = [new("C1", "NUMBER")];

        var merged = await service.ExecuteArrayAsync(session, Dml(merge), columns, [[1m]], ExecutionOptions.Default, CancellationToken.None);
        var inserted = await service.ExecuteArrayAsync(session, Dml(insert), columns, [[1m]], ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, merged.Status);
        Assert.Equal(0, merged.RowsAffected);
        Assert.Equal(ExecutionStatus.Failed, inserted.Status);
        Assert.Equal(TiberoBulkExecutionService.PartialCode, inserted.Error?.Code);
        Assert.Equal(2, Events<StatementExecuted>().Count);
    }

    [Theory]
    [InlineData("MERGE INTO \"T\" T USING (SELECT :C1 AS \"A\" FROM DUAL) S ON (T.\"A\" = S.\"A\") WHEN MATCHED THEN UPDATE SET T.\"B\" = 1 "
                + "WHEN NOT MATCHED THEN INSERT (\"A\") VALUES (S.\"A\")")]
    [InlineData("merge into \"T\" t using (select :C1 as \"A\" from dual) s on (t.\"A\" = s.\"A\") when   matched then update set t.\"B\" = 1")]
    public async Task A_one_row_upsert_merge_that_reports_zero_rows_fails_as_partial(string merge)
    {
        Serve(cmd => cmd.CommandText == merge ? 0 : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(merge), [new("C1", "NUMBER")], [[1m]], ExecutionOptions.Default,
            CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(TiberoBulkExecutionService.PartialCode, result.Error?.Code);
        Assert.Contains("MERGE", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_statement_without_columns_runs_once()
    {
        const string savepoint = "SAVEPOINT ORANDA_COPY_BATCH";
        Serve();
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, new SqlStatement(savepoint, default, StatementKind.TransactionControl), [], [],
            ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(savepoint, Texts()[0]);
        Assert.Empty(_sent[0].Parameters);
        Assert.Equal(0, _sent[0].ArrayBindCount);
        Assert.Single(Events<StatementExecuted>());
    }

    [Fact]
    public async Task Cancelled_before_start_publishes_cancelled_and_calls_no_driver()
    {
        Serve(_ => 3);
        var service = NewService();
        await using var session = NewSession();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await service.ExecuteArrayAsync(session, Dml(Insert), Columns, Rows(3), ExecutionOptions.Default, cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Empty(_sent);
        Assert.Equal(ExecutionStatus.Cancelled, Assert.Single(Events<StatementExecuted>()).Result.Status);
    }

    [Fact]
    public async Task A_driver_cancel_error_after_the_token_is_cancelled_is_cancelled_not_failed()
    {
        using var cts = new CancellationTokenSource();
        Serve(cmd =>
        {
            if (cmd.CommandText != Insert)
            {
                return null;
            }

            cts.Cancel(); // 실행 중 사용자가 취소 → 드라이버가 TBR-12040(실측 4장)
            throw FakeDriverHost.ServerError(-12040, "TBR-12040: Statement was canceled.");
        });
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(Insert), Columns, Rows(3), ExecutionOptions.Default, cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Null(result.Error);
        Assert.Equal([Insert, TiberoExecutionService.TransactionIdSql], Texts());
        Assert.Single(Events<StatementExecuted>());
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task A_driver_cancel_error_without_our_cancel_is_a_failure()
    {
        Serve(cmd => cmd.CommandText == Insert ? throw FakeDriverHost.ServerError(-12040, "TBR-12040: Statement was canceled.") : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(Insert), Columns, Rows(3), ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("TBR-12040", result.Error?.Code);
    }

    [Theory]
    [InlineData("DATE", "INSERT INTO \"T\" (\"A\") VALUES (:C1)")]
    [InlineData("TIMESTAMP(6)", "INSERT INTO \"T\" (\"A\") VALUES (TO_TIMESTAMP(:C1, 'YYYY-MM-DD HH24:MI:SS.FF7'))")]
    [InlineData("RAW(10)", "INSERT INTO \"T\" (\"A\") VALUES (:C1)")]
    public async Task A_value_that_needs_a_conversion_function_is_refused_when_the_text_does_not_wrap_it(string serverType, string text)
    {
        Serve(_ => 1);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(text), [new("C1", serverType)], [[null], [null]],
            ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("ORANDA-EXEC", result.Error?.Code);
        Assert.Contains(TiberoBulkValueCodec.BindExpression("C1", serverType), result.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(_sent);
        Assert.Single(Events<StatementExecuted>());
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task With_ten_columns_an_unwrapped_first_bind_is_refused_even_when_the_tenth_is_wrapped_and_all_wrapped_passes()
    {
        BulkColumn[] columns = [.. Enumerable.Range(1, 10).Select(i => new BulkColumn("C" + i, "DATE"))];
        string Text(bool wrapFirst) => "INSERT INTO \"T\" VALUES ("
            + string.Join(", ", columns.Select((c, i) => i == 0 && !wrapFirst ? ":C1" : TiberoBulkValueCodec.BindExpression(c.BindName, c.ServerType))) + ")";
        List<object?[]> rows = [new object?[10], new object?[10]];
        Serve(cmd => cmd.CommandText == Text(true) ? 2 : null);
        var service = NewService();
        await using var session = NewSession();

        var refused = await service.ExecuteArrayAsync(session, Dml(Text(false)), columns, rows, ExecutionOptions.Default, CancellationToken.None);
        Assert.Empty(_sent);
        var passed = await service.ExecuteArrayAsync(session, Dml(Text(true)), columns, rows, ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, refused.Status);
        Assert.Equal("ORANDA-EXEC", refused.Error?.Code);
        Assert.Contains(TiberoBulkValueCodec.BindExpression("C1", "DATE"), refused.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(ExecutionStatus.Succeeded, passed.Status); // :C1 은 :C10 에 걸리지 않는다
        Assert.Equal(2, passed.RowsAffected);
    }

    [Fact]
    public async Task A_wrapped_bind_that_also_appears_unwrapped_elsewhere_in_the_text_is_refused()
    {
        var wrapped = TiberoBulkValueCodec.BindExpression("C1", "DATE");
        var text = $"MERGE INTO \"T\" T USING (SELECT {wrapped} AS \"A\" FROM DUAL) S ON (T.\"A\" = S.\"A\" AND T.\"B\" = :C1) "
                   + "WHEN NOT MATCHED THEN INSERT (\"A\") VALUES (S.\"A\")";
        Serve(_ => 2);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(text), [new("C1", "DATE")], [[null], [null]], ExecutionOptions.Default,
            CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("ORANDA-EXEC", result.Error?.Code);
        Assert.Empty(_sent);
        Assert.Single(Events<StatementExecuted>());
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task A_column_whose_type_or_name_the_codec_cannot_describe_is_a_refusal_not_an_exception()
    {
        const string text = "INSERT INTO \"T\" (\"A\") VALUES (:C1)";
        Serve(_ => 1);
        var service = NewService();
        await using var session = NewSession();

        var noName = await service.ExecuteArrayAsync(session, Dml(text), [new("", "DATE")], [[null], [null]], ExecutionOptions.Default,
            CancellationToken.None);
        var noType = await service.ExecuteArrayAsync(session, Dml(text), [new("C1", null!)], [[null], [null]], ExecutionOptions.Default,
            CancellationToken.None);

        Assert.All(new[] { noName, noType }, r =>
        {
            Assert.Equal(ExecutionStatus.Failed, r.Status);
            Assert.Equal("ORANDA-EXEC", r.Error?.Code);
        });
        Assert.Empty(_sent);
        Assert.Equal(2, Events<StatementExecuted>().Count);
        Assert.False(session.CommandGate.IsHeld);
    }

    [Theory]
    [InlineData("XMLTYPE")]
    [InlineData("BINARY_DOUBLE")]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE")]
    public async Task A_column_of_an_unsupported_type_is_refused_without_a_driver_call(string serverType)
    {
        const string text = "INSERT INTO \"T\" (\"A\") VALUES (:C1)";
        Serve(_ => 1);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(text), [new("C1", serverType)], [["x"]], ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("ORANDA-EXEC", result.Error?.Code);
        Assert.Contains(serverType, result.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task A_value_of_the_wrong_type_is_refused_with_the_column_and_row_before_anything_is_sent()
    {
        var text = "INSERT INTO \"T\" (\"A\") VALUES (" + TiberoBulkValueCodec.BindExpression("C1", "DATE") + ")";
        Serve(_ => 2);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(text), [new("C1", "DATE")], [[new DateTime(2026, 1, 1)], ["2026-01-01"]],
            ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("ORANDA-EXEC", result.Error?.Code);
        Assert.Contains("1번째 열 C1", result.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("2번째 행", result.Error.Message, StringComparison.Ordinal);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task A_driver_invalid_cast_is_an_oranda_exec_failure()
    {
        Serve(cmd => cmd.CommandText == Insert ? throw new InvalidCastException("bad value") : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(Insert), Columns, Rows(3), ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("ORANDA-EXEC", result.Error?.Code);
        Assert.Equal("bad value", result.Error?.Message);
        Assert.Single(Events<StatementExecuted>());
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task A_driver_overflow_after_the_statement_was_sent_is_an_oranda_exec_failure_that_refreshes_the_pending_transaction()
    {
        Serve(cmd => cmd.CommandText == Insert
            ? throw new OverflowException("too big")
            : cmd.CommandText == TiberoExecutionService.TransactionIdSql ? OpenTransactionId : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(Insert), Columns, Rows(3), ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("ORANDA-EXEC", result.Error?.Code);
        Assert.Equal("too big", result.Error?.Message);
        Assert.Equal([Insert, TiberoExecutionService.TransactionIdSql], Texts()); // 문장이 나간 뒤의 실패 = 트랜잭션 상태를 다시 본다
        Assert.True(Assert.Single(Events<StatementExecuted>()).Result.TransactionPending);
        Assert.True(session.HasPendingTransaction);
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task Cancelling_while_another_party_holds_the_command_gate_gives_one_cancelled_result_and_does_not_leak_the_gate()
    {
        Serve(cmd => cmd.CommandText == Insert ? 3 : null);
        var service = NewService();
        await using var session = NewSession();
        await session.CommandGate.EnterAsync(CancellationToken.None); // 다른 쪽이 쥔다
        using var cts = new CancellationTokenSource();

        var running = service.ExecuteArrayAsync(session, Dml(Insert), Columns, Rows(3), ExecutionOptions.Default, cts.Token);
        await Task.Delay(200);
        Assert.False(running.IsCompleted); // 잠금을 기다린다
        await cts.CancelAsync();
        var result = await running;

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Null(result.Error);
        Assert.Empty(_sent);
        Assert.Equal(ExecutionStatus.Cancelled, Assert.Single(Events<StatementExecuted>()).Result.Status);
        Assert.True(session.CommandGate.IsHeld); // 쥔 쪽의 것 — 서비스가 놓지도 더 잡지도 않았다
        session.CommandGate.Release();
        Assert.False(session.CommandGate.IsHeld);

        var next = await service.ExecuteArrayAsync(session, Dml(Insert), Columns, Rows(3), ExecutionOptions.Default, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Succeeded, next.Status);
        Assert.False(session.CommandGate.IsHeld);
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task A_blob_upsert_merge_that_reports_zero_on_a_row_fails_as_partial_naming_the_row()
    {
        const string merge = "MERGE INTO \"T\" T USING (SELECT :C1 AS \"A\", :C2 AS \"B\" FROM DUAL) S ON (T.\"A\" = S.\"A\") "
                             + "WHEN MATCHED THEN UPDATE SET T.\"B\" = S.\"B\"";
        var calls = 0;
        Serve(cmd => cmd.CommandText == merge ? (++calls == 3 ? 0 : 1) : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(merge), BlobColumns, BlobRows(), ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(TiberoBulkExecutionService.PartialCode, result.Error?.Code);
        Assert.Contains("3번째 행", result.Error!.Message, StringComparison.Ordinal);
        Assert.Equal([merge, merge, merge, TiberoExecutionService.TransactionIdSql], Texts());
    }

    [Fact]
    public async Task A_blob_table_with_many_rows_runs_row_by_row_on_a_new_command_per_row_each_carrying_only_its_own_values()
    {
        Serve(cmd => cmd.CommandText == BlobInsert ? 1 : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(BlobInsert), BlobColumns, BlobRows(), ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(3, result.RowsAffected);
        Assert.Equal([BlobInsert, BlobInsert, BlobInsert, TiberoExecutionService.TransactionIdSql], Texts());
        var rows = _sent.Take(3).ToList();
        Assert.Equal(3, rows.Select(s => s.CommandId).Distinct().Count()); // 행마다 다른 명령 객체(재사용하면 큰 BLOB 뒤 TBR-590727, 실측)
        Assert.All(rows, s => Assert.Equal(2, s.Parameters.Count)); // 명령마다 그 행의 값 한 벌만
        Assert.All(rows, s => Assert.Equal(0, s.ArrayBindCount));
        Assert.All(rows, s => Assert.Equal(["C1", "C2"], s.Parameters.Select(p => p.Name)));
        Assert.All(rows, s => Assert.Equal(["Decimal", "Blob"], s.Parameters.Select(p => p.Kind)));
        Assert.Equal(["1", "2", "3"], rows.Select(s => s.Parameters[0].Value));
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(rows[0].Parameters[1].Value));
        Assert.Same(DBNull.Value, rows[1].Parameters[1].Value);
        Assert.Equal(new byte[] { 9 }, Assert.IsType<byte[]>(rows[2].Parameters[1].Value));

        var executed = Assert.Single(Events<StatementExecuted>());
        Assert.Equal(ExecutionStatus.Succeeded, executed.Result.Status);
        Assert.Equal(3, executed.Result.RowsAffected);
        Assert.Equal(SessionState.Idle, session.State);
        Assert.False(session.CommandGate.IsHeld);
    }

    [Theory]
    [InlineData(TiberoBulkExecutionService.ArrayClobMaxChars, 1)]
    [InlineData(TiberoBulkExecutionService.ArrayClobMaxChars + 1, 3)]
    public async Task A_batch_with_a_clob_longer_than_the_array_limit_runs_row_by_row_and_a_shorter_one_stays_one_array(int length, int commands)
    {
        // 100000자 CLOB 이 다른 열과 함께 배열에 들어가면 서버가 연결을 끊는다(win144 #82) → 상한을 넘는 값이 있는 묶음만 한 행씩.
        const string insert = "INSERT INTO \"T\" (\"A\", \"B\", \"C\") VALUES (:C1, :C2, :C3)";
        Serve(cmd => cmd.CommandText == insert ? (FakeDriverHost.Get(cmd, "ArrayBindCount") is 3 ? 3 : 1) : null);
        var service = NewService();
        await using var session = NewSession();
        BulkColumn[] columns = [new("C1", "NUMBER"), new("C2", "CLOB"), new("C3", "VARCHAR2(10)")];
        object?[][] rows = [[1m, "short", "a"], [2m, new string('x', length), "b"], [3m, null, "c"]];

        var result = await service.ExecuteArrayAsync(session, Dml(insert), columns, rows, ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(3, result.RowsAffected);
        var sent = _sent.Where(s => s.Text == insert).ToList();
        Assert.Equal(commands, sent.Count);
        Assert.All(sent, s => Assert.Equal(commands == 1 ? 3 : 0, s.ArrayBindCount));
        Assert.Equal(length, commands == 1 ? ((string?[])sent[0].Parameters[1].Value!)[1]!.Length : ((string)sent[1].Parameters[1].Value!).Length);
    }

    [Fact]
    public async Task A_server_error_on_the_second_blob_row_stops_there_with_the_classified_error()
    {
        var calls = 0;
        Serve(cmd => cmd.CommandText != BlobInsert
            ? null
            : ++calls == 2 ? throw FakeDriverHost.ServerError(-10007, "TBR-10007: UNIQUE constraint violation ('U'.'PK').") : 1);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(BlobInsert), BlobColumns, BlobRows(), ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(0, result.RowsAffected);
        Assert.Equal("TBR-10007", result.Error?.Code);
        Assert.Equal(DbErrorKind.ConstraintViolation, result.Error?.Kind);
        Assert.Equal(Dbms.Tibero, result.Error?.Dbms);
        Assert.Equal([BlobInsert, BlobInsert, TiberoExecutionService.TransactionIdSql], Texts()); // 세 번째 행은 보내지 않는다
        Assert.Single(Events<StatementExecuted>());
        Assert.Equal(SessionState.Idle, session.State);
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task A_blob_insert_that_returns_zero_on_the_second_row_fails_as_partial_naming_the_row()
    {
        var calls = 0;
        Serve(cmd => cmd.CommandText == BlobInsert ? (++calls == 2 ? 0 : 1) : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(BlobInsert), BlobColumns, BlobRows(), ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(0, result.RowsAffected);
        Assert.Equal(TiberoBulkExecutionService.PartialCode, result.Error?.Code);
        Assert.Contains("2번째 행", result.Error!.Message, StringComparison.Ordinal);
        Assert.Equal([BlobInsert, BlobInsert, TiberoExecutionService.TransactionIdSql], Texts()); // 앞 행이 열린 트랜잭션에 남아 조회한다
        Assert.Equal(ExecutionStatus.Failed, Assert.Single(Events<StatementExecuted>()).Result.Status);
    }

    [Fact]
    public async Task A_blob_merge_that_matches_nothing_on_every_row_is_a_success()
    {
        const string merge = "MERGE INTO \"T\" T USING (SELECT :C1 AS \"A\", :C2 AS \"B\" FROM DUAL) S ON (T.\"A\" = S.\"A\") "
                             + "WHEN NOT MATCHED THEN INSERT (\"A\", \"B\") VALUES (S.\"A\", S.\"B\")";
        Serve(cmd => cmd.CommandText == merge ? 0 : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(merge), BlobColumns, BlobRows(), ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(0, result.RowsAffected);
        Assert.Equal([merge, merge, merge, TiberoExecutionService.TransactionIdSql], Texts());
    }

    [Fact]
    public async Task Cancelling_during_the_blob_rows_stops_before_the_remaining_rows_are_sent()
    {
        using var cts = new CancellationTokenSource();
        Serve(cmd =>
        {
            if (cmd.CommandText != BlobInsert)
            {
                return null;
            }

            cts.Cancel(); // 첫 행을 보내는 중 사용자가 취소
            return 1;
        });
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(BlobInsert), BlobColumns, BlobRows(), ExecutionOptions.Default, cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Null(result.Error);
        Assert.Equal([BlobInsert, TiberoExecutionService.TransactionIdSql], Texts());
        Assert.Equal(ExecutionStatus.Cancelled, Assert.Single(Events<StatementExecuted>()).Result.Status);
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task A_driver_cancel_error_during_a_blob_row_after_our_cancel_is_cancelled_and_the_remaining_rows_are_not_sent()
    {
        // 한 행씩 경로의 우리 취소: 드라이버가 TBR-12040 을 내면 실패가 아니라 취소다(복사 실행기는 Cancelled + 취소된 토큰을 OperationCanceledException 으로 바꾼다).
        using var cts = new CancellationTokenSource();
        Serve(cmd =>
        {
            if (cmd.CommandText != BlobInsert)
            {
                return null;
            }

            cts.Cancel();
            throw FakeDriverHost.ServerError(-12040, "ODP-12040:Statement canceled.");
        });
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(BlobInsert), BlobColumns, BlobRows(), ExecutionOptions.Default, cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Null(result.Error);
        Assert.Equal([BlobInsert, TiberoExecutionService.TransactionIdSql], Texts());
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task A_driver_cancel_error_inside_a_wrapper_after_our_cancel_is_still_cancelled()
    {
        using var cts = new CancellationTokenSource();
        Serve(cmd =>
        {
            if (cmd.CommandText != Insert)
            {
                return null;
            }

            cts.Cancel();
            throw new InvalidOperationException("driver wrapper", FakeDriverHost.ServerError(-12040, "ODP-12040:Statement canceled."));
        });
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(Insert), Columns, Rows(3), ExecutionOptions.Default, cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task A_single_blob_row_is_one_scalar_execution()
    {
        Serve(cmd => cmd.CommandText == BlobInsert ? 1 : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(BlobInsert), BlobColumns, [BlobRows()[0]], ExecutionOptions.Default,
            CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(1, result.RowsAffected);
        Assert.Equal(0, _sent[0].ArrayBindCount);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(_sent[0].Parameters[1].Value));
    }

    [Fact]
    public async Task Policy_deny_and_a_declined_confirmation_send_nothing()
    {
        Serve(_ => 3);
        await using var session = NewSession();
        var deny = NewService(new FakePolicy(new PolicyDecision(PolicyDecisionKind.Deny, PolicyRule.ProductionChange, "no")));
        var confirm = NewService(new FakePolicy(new PolicyDecision(PolicyDecisionKind.Confirm, PolicyRule.ProductionChange, "sure?")),
            new FakeInteraction(confirm: false));

        var denied = await deny.ExecuteArrayAsync(session, Dml(Insert), Columns, Rows(3), ExecutionOptions.Default, CancellationToken.None);
        var declined = await confirm.ExecuteArrayAsync(session, Dml(Insert), Columns, Rows(3), ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Denied, denied.Status);
        Assert.Equal(PolicyRule.ProductionChange, denied.Policy?.Rule);
        Assert.Equal(ExecutionStatus.Cancelled, declined.Status);
        Assert.Empty(_sent);
        Assert.Equal(2, Events<StatementExecuted>().Count);
    }

    [Fact]
    public async Task A_read_only_profile_refuses_a_write_but_lets_commit_and_the_execution_service_read_only_block_through()
    {
        Serve();
        var service = NewService();
        await using var session = NewSession(readOnly: true);
        var tc = StatementKind.TransactionControl;

        var write = await service.ExecuteArrayAsync(session, Dml(Insert), Columns, Rows(3), ExecutionOptions.Default, CancellationToken.None);
        var commit = await service.ExecuteArrayAsync(session, new SqlStatement("COMMIT", default, tc), [], [], ExecutionOptions.Default,
            CancellationToken.None);
        var block = await service.ExecuteArrayAsync(session, new SqlStatement(TiberoExecutionService.ReadOnlyBlock, default, tc),
            [], [], ExecutionOptions.Default, CancellationToken.None);
        var oldBlock = await service.ExecuteArrayAsync(session, new SqlStatement("BEGIN SET TRANSACTION READ ONLY; END;", default, tc),
            [], [], ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Denied, write.Status);
        Assert.Equal(PolicyRule.ReadOnlyProfile, write.Policy?.Rule);
        Assert.Equal(ExecutionStatus.Succeeded, commit.Status);
        Assert.Equal(ExecutionStatus.Succeeded, block.Status);
        Assert.Equal(ExecutionStatus.Denied, oldBlock.Status); // 글자 그대로 ReadOnlyBlock 인 것만 지난다
        Assert.Equal(PolicyRule.ReadOnlyProfile, oldBlock.Policy?.Rule);
        Assert.Equal(["COMMIT", TiberoExecutionService.ReadOnlyBlock], Texts()); // 읽기 전용은 트랜잭션 조회도 안 한다
        Assert.Equal(4, Events<StatementExecuted>().Count);
    }

    [Fact]
    public async Task A_read_only_profile_commit_sends_only_the_commit_and_never_the_read_only_block_on_its_own()
    {
        Serve();
        var service = NewService();
        await using var session = NewSession(readOnly: true);

        var commit = await service.ExecuteArrayAsync(session, new SqlStatement("COMMIT", default, StatementKind.TransactionControl), [], [],
            ExecutionOptions.Default, CancellationToken.None);
        var write = await service.ExecuteArrayAsync(session, Dml(Insert), Columns, Rows(3), ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, commit.Status);
        Assert.Equal(ExecutionStatus.Denied, write.Status);
        Assert.Equal(["COMMIT"], Texts());
        Assert.DoesNotContain(TiberoExecutionService.ReadOnlyBlock, Texts());
    }

    [Fact]
    public async Task Argument_checks_reject_ddl_sqlplus_ragged_rows_and_many_rows_without_columns()
    {
        Serve();
        var service = NewService();
        await using var session = NewSession();
        var o = ExecutionOptions.Default;
        var none = CancellationToken.None;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExecuteArrayAsync(session, new SqlStatement("DROP TABLE T", default, StatementKind.Ddl), [], [], o, none));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExecuteArrayAsync(session, new SqlStatement("SET ECHO ON", default, StatementKind.SqlPlus), [], [], o, none));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExecuteArrayAsync(session, Dml(Insert), Columns, [new object?[2]], o, none));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExecuteArrayAsync(session, Dml("COMMIT"), [], [[], []], o, none));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.ExecuteArrayAsync(session, Dml(Insert), Columns, null!, o, none));

        Assert.Empty(_sent);
        Assert.Empty(Events<StatementExecuted>());
    }

    [Fact]
    public async Task Columns_without_rows_do_nothing_and_publish_nothing()
    {
        Serve(_ => 1);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteArrayAsync(session, Dml(Insert), Columns, [], ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(0, result.RowsAffected);
        Assert.Empty(_sent);
        Assert.Empty(Events<StatementExecuted>());
        Assert.Empty(Events<PolicyDecisionMade>());
    }

    [Fact]
    public async Task A_broken_session_fails_without_a_driver_call()
    {
        Serve(_ => 3);
        var service = NewService();
        await using var session = NewSession();
        Assert.True(session.ReportFailure(FakeDriverHost.ClientError(90403, "ODP-90403: connection broken")));

        var result = await service.ExecuteArrayAsync(session, Dml(Insert), Columns, Rows(3), ExecutionOptions.Default, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("ORANDA-SESSION", result.Error?.Code);
        Assert.Empty(_sent);
        Assert.Single(Events<StatementExecuted>());
    }

    [Fact]
    public void Read_only_safe_text_is_transaction_control_or_exactly_the_execution_service_read_only_block()
    {
        Assert.True(TiberoBulkExecutionService.IsReadOnlySafeText("COMMIT"));
        Assert.True(TiberoBulkExecutionService.IsReadOnlySafeText("rollback to savepoint x"));
        Assert.True(TiberoBulkExecutionService.IsReadOnlySafeText("/* c */ SET TRANSACTION READ ONLY"));
        Assert.True(TiberoBulkExecutionService.IsReadOnlySafeText(" " + TiberoExecutionService.ReadOnlyBlock + "\n"));
        Assert.False(TiberoBulkExecutionService.IsReadOnlySafeText("BEGIN SET TRANSACTION READ ONLY; END;")); // 옛 글
        Assert.False(TiberoBulkExecutionService.IsReadOnlySafeText(TiberoExecutionService.ReadOnlyBlock.ToLowerInvariant()));
        Assert.False(TiberoBulkExecutionService.IsReadOnlySafeText("BEGIN DELETE FROM T; END;"));
        Assert.False(TiberoBulkExecutionService.IsReadOnlySafeText("INSERT INTO T VALUES (1)"));
        Assert.Equal(("INSERT", "INTO"), TiberoBulkExecutionService.LeadingWords("-- x\n (insert into t"));
        Assert.Equal((null, null), TiberoBulkExecutionService.LeadingWords("  "));
    }

    /// <summary>3행: 값 있음 / 모두 NULL / 끝값. 열 순서 = <see cref="Columns"/>.</summary>
    private static List<object?[]> Rows(int count)
    {
        List<object?[]> all =
        [
            [
                TiberoBulkNumber.Parse("1.5"), "a", new DateTime(2026, 1, 2, 3, 4, 5),
                new TiberoBulkTimestamp(new DateTime(2026, 1, 2, 3, 4, 5), 123_456_789), new byte[] { 0x0A, 0xFF }, "long",
            ],
            [null, null, null, null, null, null],
            [-0.0000001m, "c", DateTime.MinValue, new DateTime(9999, 12, 31, 23, 59, 59), Array.Empty<byte>(), ""],
        ];
        return all.Take(count).ToList();
    }

    /// <summary>BLOB 표 3행: 값 있음 / BLOB NULL / 한 바이트. 열 순서 = <see cref="BlobColumns"/>.</summary>
    private static List<object?[]> BlobRows() => [[1m, new byte[] { 1, 2, 3 }], [2m, null], [3m, new byte[] { 9 }]];

    private static SqlStatement Dml(string text) => new(text, default, StatementKind.Dml);

    private TiberoBulkExecutionService NewService(FakePolicy? policy = null, FakeInteraction? interaction = null) =>
        new(policy ?? new FakePolicy(PolicyDecision.Allowed), interaction ?? new FakeInteraction(confirm: true), _bus);

    private TiberoSession NewSession(bool readOnly = false)
    {
        var driver = FakeDriverHost.Driver;
        var connection = driver.CreateConnection("Data Source=x");
        connection.Open();
        var profile = new ConnectionProfile("t", "u", "h:1/d", Dbms: Dbms.Tibero, ReadOnly: readOnly);
        return new TiberoSession(Guid.NewGuid(), profile, Server, SessionPurpose.Work, "D/-/U", connection, null, _bus, driver: driver);
    }

    /// <summary>보낸 명령(글·ArrayBindCount·매개변수)을 그 순간 그대로 기록하고 <paramref name="respond"/> 의 답을 돌려준다.</summary>
    private void Serve(Func<DbCommand, object?>? respond = null) =>
        FakeDriverHost.SetHandler(cmd =>
        {
            var parameters = cmd.Parameters.Cast<DbParameter>()
                .Select(p => new SentParameter(p.ParameterName, FakeDriverHost.Get(p, "TiberoDbType")!.ToString()!, p.Value))
                .ToList();
            lock (_sent)
            {
                _sent.Add(new Sent(cmd.CommandText, (int)FakeDriverHost.Get(cmd, "ArrayBindCount")!, parameters, RuntimeHelpers.GetHashCode(cmd)));
            }

            return respond?.Invoke(cmd);
        });

    private List<string> Texts()
    {
        lock (_sent)
        {
            return [.. _sent.Select(s => s.Text)];
        }
    }

    private List<T> Events<T>()
        where T : OrandaEvent
    {
        lock (_events)
        {
            return [.. _events.OfType<T>()];
        }
    }

    private sealed record Sent(string Text, int ArrayBindCount, IReadOnlyList<SentParameter> Parameters, int CommandId = 0);

    private sealed record SentParameter(string Name, string Kind, object? Value);

    private sealed class FakePolicy(PolicyDecision decision) : IExecutionPolicy
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) => ValueTask.FromResult(decision);
    }

    private sealed class FakeInteraction(bool confirm) : IExecutionInteraction
    {
        public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct) => Task.FromResult(confirm);

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) =>
            Task.FromResult(ScriptErrorAction.Stop);
    }
}
