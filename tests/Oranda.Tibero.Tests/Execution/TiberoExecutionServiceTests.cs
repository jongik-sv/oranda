using System.Data;
using System.Data.Common;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Sessions;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Results;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Driver;

namespace Oranda.Tibero.Tests.Execution;

/// <summary>
/// 실행 서비스 E1 단위 시험. 서버 없이 가짜 드라이버(<see cref="FakeDriverHost"/>)의 연결로 세션을 만들고, 정적 처리기로 보낸 문장을 기록하고 답을 정한다.
/// 처리기가 정적이라 FakeDriver 컬렉션에 둔다(병렬 금지).
/// </summary>
[Collection("FakeDriver")]
public sealed class TiberoExecutionServiceTests : IDisposable
{
    private const string Block = "BEGIN NULL; END;";

    private static readonly ServerInfo Server = new("6", "i", "h", null, "D", null, false, 7,
        new ServerCapabilities(false, false, true, false, false, Dbms.Tibero), DbUniqueName: "D", SessionUser: "U", Dbms: Dbms.Tibero);

    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();

    private readonly List<string> _sent = [];
    private readonly EventBus _bus = new();
    private readonly List<OrandaEvent> _events = [];

    public TiberoExecutionServiceTests()
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
    public async Task Every_statement_is_tracked_and_the_dbms_output_option_off_sends_no_extra_statement()
    {
        Serve();
        var service = NewService();
        await using var session = NewSession();

        var first = await service.ExecuteAsync(session, Stmt(Block, StatementKind.PlSql), NoBinds, null, CancellationToken.None);
        var second = await service.ExecuteAsync(session, Stmt(Block, StatementKind.PlSql), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, first.Status);
        Assert.Equal(ExecutionStatus.Succeeded, second.Status);
        Assert.Equal([Block, TiberoExecutionService.TransactionIdSql, Block, TiberoExecutionService.TransactionIdSql], Sent());
        Assert.False(service.DbmsOutputEnabled);
        Assert.Equal(2, Events<StatementExecuted>().Count);
        Assert.Equal(SessionState.Idle, session.State);
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task Dbms_output_is_on_by_default_enabled_once_per_session_and_collected_after_every_statement()
    {
        Serve();
        var service = new TiberoExecutionService(new FakePolicy(PolicyDecision.Allowed), new FakeInteraction(null), _bus, new FakeSqlPlus());
        await using var session = NewSession();

        var first = await service.ExecuteAsync(session, Stmt(Block, StatementKind.PlSql), NoBinds, null, CancellationToken.None);
        await service.ExecuteAsync(session, Stmt(Block, StatementKind.PlSql), NoBinds, null, CancellationToken.None);

        Assert.True(service.DbmsOutputEnabled);
        Assert.Equal(
            [
                TiberoDbmsOutput.EnableBlock,
                Block, TiberoExecutionService.TransactionIdSql, TiberoDbmsOutput.CollectBlock,
                Block, TiberoExecutionService.TransactionIdSql, TiberoDbmsOutput.CollectBlock,
            ],
            Sent());
        Assert.Empty(first.DbmsOutput);
        Assert.Empty(Events<DbmsOutputReceived>());
    }

    [Fact]
    public async Task Collected_lines_are_decoded_returned_and_published_with_the_session_id()
    {
        ServeOutput("1:a\n4:한글 둘\n-\n5:a:b:c\n", 4);
        var service = NewService(dbmsOutput: true);
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(Block, StatementKind.PlSql), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(["a", "한글 둘", "", "a:b:c"], result.DbmsOutput);
        var received = Assert.Single(Events<DbmsOutputReceived>());
        Assert.Equal(session.Id, received.SessionId);
        Assert.Equal(["a", "한글 둘", "", "a:b:c"], received.Lines);
        Assert.Equal(1, Sent().Count(s => s == TiberoDbmsOutput.CollectBlock));
    }

    [Fact]
    public async Task An_unreadable_count_falls_back_to_the_decoded_lines_so_a_full_batch_still_continues()
    {
        var batches = new Queue<(object Count, string Packed)>(new (object Count, string Packed)[]
        {
            (ProviderString(null), string.Concat(Enumerable.Range(0, TiberoDbmsOutput.Batch).Select(i => $"1:{i % 10}\n"))),
            (ProviderString(null), "1:z\n"),
        });
        Serve(cmd =>
        {
            if (cmd.CommandText != TiberoDbmsOutput.CollectBlock)
            {
                return null;
            }

            var (count, packed) = batches.Dequeue();
            cmd.Parameters["n"].Value = count;
            cmd.Parameters["r"].Value = ProviderString(packed);
            return -1;
        });
        var service = NewService(dbmsOutput: true);
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(Block, StatementKind.PlSql), NoBinds, null, CancellationToken.None);

        Assert.Equal(TiberoDbmsOutput.Batch + 1, result.DbmsOutput.Count);
        Assert.Equal("z", result.DbmsOutput[^1]);
        Assert.Empty(batches);
    }

    [Fact]
    public async Task An_uncertain_statement_that_returns_rows_leaves_dbms_output_for_the_next_statement()
    {
        const string text = "CALL report_rows()";
        var table = Table(["A"], ["1"]);
        Serve(cmd =>
        {
            if (cmd.CommandText == text)
            {
                return table;
            }

            if (cmd.CommandText == TiberoDbmsOutput.CollectBlock)
            {
                cmd.Parameters["n"].Value = ProviderString("1");
                cmd.Parameters["r"].Value = ProviderString("4:ran!\n");
                return -1;
            }

            return null;
        });
        var service = NewService(dbmsOutput: true);
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(text, StatementKind.Other) with { IsClassificationUncertain = true }, NoBinds, null,
            CancellationToken.None);

        // 리더가 열린 채로는 거두지 않는다(실서버: 같은 연결의 CLOB OUT 읽기가 드라이버 안에서 NullReferenceException). 출력은 버퍼에 남는다.
        await using (result.ResultSet)
        {
            Assert.Equal(ExecutionStatus.Succeeded, result.Status);
            Assert.NotNull(result.ResultSet);
            Assert.Empty(result.DbmsOutput);
            Assert.DoesNotContain(TiberoDbmsOutput.CollectBlock, Sent());
        }

        var next = await service.ExecuteAsync(session, Stmt(Block, StatementKind.PlSql), NoBinds, null, CancellationToken.None);

        Assert.Equal(["ran!"], next.DbmsOutput);
        Assert.Single(Events<DbmsOutputReceived>());
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task A_plain_query_with_dbms_output_on_sends_only_the_enable_block_and_the_query()
    {
        const string text = "SELECT a FROM t";
        Serve(cmd => cmd.CommandText == text ? Table(["A"], ["1"]) : null);
        var service = NewService(dbmsOutput: true);
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(text, StatementKind.Query), NoBinds, null, CancellationToken.None);

        await using (result.ResultSet)
        {
            // 순수 질의 뒤엔 트랜잭션 확인도 거두기도 없다(Oracle 과 같다: 직전 커서를 바꾸지 않는다).
            Assert.Equal([TiberoDbmsOutput.EnableBlock, text], Sent());
        }
    }

    [Fact]
    public async Task A_full_batch_is_followed_by_another_collect_until_fewer_lines_come_back()
    {
        var batches = new Queue<(int Count, string Packed)>(new (int Count, string Packed)[]
        {
            (TiberoDbmsOutput.Batch, string.Concat(Enumerable.Range(0, TiberoDbmsOutput.Batch).Select(i => $"1:{i % 10}\n"))),
            (2, "1:y\n1:z\n"),
        });
        var maxSeen = new List<object?>();
        Serve(cmd =>
        {
            if (cmd.CommandText != TiberoDbmsOutput.CollectBlock)
            {
                return null;
            }

            maxSeen.Add(cmd.Parameters["max"].Value);
            var (count, packed) = batches.Dequeue();
            cmd.Parameters["n"].Value = ProviderString(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            cmd.Parameters["r"].Value = ProviderString(packed);
            return -1;
        });
        var service = NewService(dbmsOutput: true);
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(Block, StatementKind.PlSql), NoBinds, null, CancellationToken.None);

        Assert.Equal(TiberoDbmsOutput.Batch + 2, result.DbmsOutput.Count);
        Assert.Equal(["y", "z"], result.DbmsOutput.TakeLast(2));
        Assert.Empty(batches);
        Assert.Equal(2, Sent().Count(s => s == TiberoDbmsOutput.CollectBlock));
        Assert.Equal(new object?[] { 1000m, 1000m }, maxSeen);
        Assert.Single(Events<DbmsOutputReceived>());
    }

    [Fact]
    public async Task An_empty_buffer_gives_no_lines_and_no_event()
    {
        Serve(cmd =>
        {
            if (cmd.CommandText != TiberoDbmsOutput.CollectBlock)
            {
                return null;
            }

            cmd.Parameters["n"].Value = ProviderString("0");
            cmd.Parameters["r"].Value = ProviderString(null);
            return -1;
        });
        var service = NewService(dbmsOutput: true);
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(Block, StatementKind.PlSql), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.DbmsOutput);
        Assert.Empty(Events<DbmsOutputReceived>());
        Assert.Equal(1, Sent().Count(s => s == TiberoDbmsOutput.CollectBlock));
    }

    [Fact]
    public async Task Output_written_before_a_server_error_is_still_collected()
    {
        const string text = "BEGIN DBMS_OUTPUT.PUT_LINE('x'); RAISE_APPLICATION_ERROR(-20001, 'boom'); END;";
        Serve(cmd =>
        {
            if (cmd.CommandText == text)
            {
                throw FakeDriverHost.ServerError(-20001, "TBR-20001: boom");
            }

            if (cmd.CommandText != TiberoDbmsOutput.CollectBlock)
            {
                return null;
            }

            cmd.Parameters["n"].Value = ProviderString("1");
            cmd.Parameters["r"].Value = ProviderString("1:x\n");
            return -1;
        });
        var service = NewService(dbmsOutput: true);
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(text, StatementKind.PlSql), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(["x"], result.DbmsOutput);
        Assert.Equal(["x"], Assert.Single(Events<DbmsOutputReceived>()).Lines);
        Assert.Equal([TiberoDbmsOutput.EnableBlock, text, TiberoExecutionService.TransactionIdSql, TiberoDbmsOutput.CollectBlock], Sent());
        Assert.Equal(SessionState.Idle, session.State);
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task A_failing_collect_does_not_break_the_statement_result()
    {
        Serve(cmd => cmd.CommandText == TiberoDbmsOutput.CollectBlock
            ? throw FakeDriverHost.ServerError(-8033, "TBR-8033: not found")
            : null);
        var service = NewService(dbmsOutput: true);
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(Block, StatementKind.PlSql), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.DbmsOutput);
        Assert.Empty(Events<DbmsOutputReceived>());
        Assert.Equal(SessionState.Idle, session.State);
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task Read_only_profile_starts_a_read_only_transaction_inside_a_block_before_every_statement()
    {
        Serve(_ => "12.66.243778"); // 읽기 전용이면 트랜잭션 id 를 묻지 않는다 — 물으면 미커밋이 켜져 시험이 실패한다.
        var service = NewService();
        await using var session = NewSession(readOnly: true);

        var first = await service.ExecuteAsync(session, Stmt(Block, StatementKind.PlSql), NoBinds, null, CancellationToken.None);
        await service.ExecuteAsync(session, Stmt(Block, StatementKind.PlSql), NoBinds, null, CancellationToken.None);

        Assert.Equal(
            [TiberoExecutionService.ReadOnlyBlock, Block, TiberoExecutionService.ReadOnlyBlock, Block],
            Sent());
        Assert.Equal("BEGIN ROLLBACK; SET TRANSACTION READ ONLY; END;", TiberoExecutionService.ReadOnlyBlock);
        Assert.False(first.TransactionPending);
        Assert.False(session.HasPendingTransaction);
    }

    [Fact]
    public async Task Dml_reports_affected_rows_and_a_pending_transaction()
    {
        const string dml = "UPDATE t SET a = 1";
        Serve(cmd => cmd.CommandText == TiberoExecutionService.TransactionIdSql ? "12.66.243778" : 3);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(dml, StatementKind.Dml), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(3, result.RowsAffected);
        Assert.True(result.TransactionPending);
        Assert.True(session.HasPendingTransaction);
        Assert.Equal([true], Events<TransactionStateChanged>().Select(e => e.Pending));
        Assert.Empty(Events<MetadataInvalidated>());
    }

    [Fact]
    public async Task A_provider_null_transaction_id_clears_the_pending_flag()
    {
        var providerNull = ProviderString(null);
        Serve(cmd => cmd.CommandText == TiberoExecutionService.TransactionIdSql ? providerNull : -1);
        var service = NewService();
        await using var session = NewSession();
        session.SetPending(true);

        var result = await service.ExecuteAsync(session, Stmt("COMMIT", StatementKind.TransactionControl), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Null(result.RowsAffected);
        Assert.False(result.TransactionPending);
        Assert.False(session.HasPendingTransaction);
    }

    [Fact]
    public async Task Ddl_publishes_metadata_invalidated_for_the_session_database()
    {
        Serve();
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt("CREATE TABLE ort_a (a NUMBER)", StatementKind.Ddl), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Null(result.RowsAffected);
        var invalidated = Assert.Single(Events<MetadataInvalidated>());
        Assert.Equal("D/-/U", invalidated.DbKey);
    }

    [Fact]
    public async Task A_ddl_change_journals_one_not_captured_row_with_the_reason_and_reads_nothing_from_the_server()
    {
        const string text = "DROP TABLE ort_a";
        Serve();
        var service = NewService();
        await using var session = NewSession();
        var stmt = Stmt(text, StatementKind.Ddl) with
        {
            ChangeTarget = new Oranda.Core.Journal.ChangeTarget(null, "ORT_A", "TABLE", Oranda.Core.Journal.ChangeOperation.Drop),
        };

        var result = await service.ExecuteAsync(session, stmt, NoBinds, null, CancellationToken.None);

        // D4: T2 는 이전 DDL 을 보관하지 않고 그 이유를 일지에 남긴다. 서버로는 DDL 과 트랜잭션 확인만 간다.
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var entry = Assert.Single(Events<Oranda.Core.Journal.ChangeSnapshotCaptured>()).Entry;
        Assert.Equal(TiberoDdlSource.Reason, entry.FailureReason);
        Assert.Equal(("U", "ORT_A", "TABLE", "D/-/U"), (entry.Owner, entry.Name, entry.ObjectType, entry.DbKey));
        Assert.Equal([text, TiberoExecutionService.TransactionIdSql], Sent());
    }

    [Fact]
    public async Task Named_binds_are_typed_through_the_adapter_and_out_values_come_back_as_clr_values()
    {
        const string text = "BEGIN :msg := 'hi ' || :name; :n := :k * 2; :d := NULL; END;";
        var seen = new Dictionary<string, (string Kind, ParameterDirection Direction, int Size, object? Value)>();
        object? bindByName = null;
        Serve(cmd =>
        {
            if (cmd.CommandText != text)
            {
                return null;
            }

            bindByName = FakeDriverHost.Get(cmd, "BindByName");
            foreach (DbParameter p in cmd.Parameters)
            {
                seen[p.ParameterName] = (FakeDriverHost.Get(p, "TiberoDbType")!.ToString()!, p.Direction, p.Size, p.Value);
            }

            // 매개변수 형식(TiberoDbType)을 정하면 OUT 값은 그 형식의 공급자 값으로 온다(values 문서 6장). NULL 은 IsNull 인 값.
            // NUMBER OUT 은 Varchar2 로 받는다(결정40: 28자리 넘는 값에서 드라이버가 OverflowException) → 글자 값.
            cmd.Parameters["msg"].Value = ProviderString("hi kim");
            cmd.Parameters["n"].Value = ProviderString("42");
            cmd.Parameters["d"].Value = FakeDriverHost.ProviderValue("TiberoDate");
            return -1;
        });
        var service = NewService();
        await using var session = NewSession();
        var stmt = Stmt(text, StatementKind.PlSql) with { BindNames = ["msg", "name", "n", "k", "d"] };
        var binds = new Dictionary<string, BindValue>
        {
            ["msg"] = new(BindDataType.Varchar2, null, BindDirection.Out),
            ["NAME"] = new(BindDataType.Varchar2, "kim"),
            ["n"] = new(BindDataType.Number, null, BindDirection.Out),
            ["k"] = new(BindDataType.Number, 21m),
            ["d"] = new(BindDataType.Date, null, BindDirection.Out),
        };

        var result = await service.ExecuteAsync(session, stmt, binds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(true, bindByName);
        Assert.Equal(("Varchar2", ParameterDirection.Output, 32767), (seen["msg"].Kind, seen["msg"].Direction, seen["msg"].Size));
        Assert.Equal(("Varchar2", ParameterDirection.Input, (object?)"kim"), (seen["name"].Kind, seen["name"].Direction, seen["name"].Value));
        Assert.Equal(("Decimal", (object?)21m), (seen["k"].Kind, seen["k"].Value));
        Assert.Equal(("Varchar2", ParameterDirection.Output), (seen["n"].Kind, seen["n"].Direction));
        Assert.Equal(("Date", (object?)DBNull.Value), (seen["d"].Kind, seen["d"].Value));
        Assert.Equal("hi kim", result.OutValues["msg"]);
        Assert.Equal(42m, result.OutValues["n"]);
        Assert.True(result.OutValues.ContainsKey("d"));
        Assert.Null(result.OutValues["d"]);
        Assert.False(result.OutValues.ContainsKey("name"));
    }

    [Fact]
    public async Task Boolean_and_timestamp_out_values_come_back_typed_and_tz_out_as_server_text()
    {
        const string text = "BEGIN :b := TRUE; :ts := SYSTIMESTAMP; :tz := SYSTIMESTAMP; END;";
        var stamp = new DateTime(2026, 11, 9, 20, 39, 47);
        var kinds = new Dictionary<string, string>();
        Serve(cmd =>
        {
            if (cmd.CommandText != text)
            {
                return null;
            }

            foreach (DbParameter p in cmd.Parameters)
            {
                kinds[p.ParameterName] = FakeDriverHost.Get(p, "TiberoDbType")!.ToString()!;
            }

            // BOOLEAN·TZ 는 OUT 으로 못 받아 VARCHAR2 로 받는다 → 서버가 바꾼 글자가 온다(values 문서 6장).
            cmd.Parameters["b"].Value = ProviderString("1");
            cmd.Parameters["ts"].Value = FakeDriverHost.ProviderValue("TiberoTimeStamp", stamp, 123_456_789);
            cmd.Parameters["tz"].Value = ProviderString("2026/11/09 20:39:47.790407000 Asia/Seoul");
            return -1;
        });
        var service = NewService();
        await using var session = NewSession();
        var stmt = Stmt(text, StatementKind.PlSql) with { BindNames = ["b", "ts", "tz"] };
        var binds = new Dictionary<string, BindValue>
        {
            ["b"] = new(BindDataType.Boolean, null, BindDirection.Out),
            ["ts"] = new(BindDataType.Timestamp, null, BindDirection.Out),
            ["tz"] = new(BindDataType.TimestampTz, null, BindDirection.Out),
        };

        var result = await service.ExecuteAsync(session, stmt, binds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(("Varchar2", "TimeStamp", "Varchar2"), (kinds["b"], kinds["ts"], kinds["tz"]));
        Assert.Equal(true, result.OutValues["b"]);
        Assert.Equal(stamp.AddTicks(1_234_567), result.OutValues["ts"]);
        // R1b: TZ·LTZ·BINARY_DOUBLE·INTERVAL OUT 은 서버 글자 그대로(되돌려 읽기는 R1b 몫).
        Assert.Equal("2026/11/09 20:39:47.790407000 Asia/Seoul", result.OutValues["tz"]);
    }

    [Fact]
    public async Task A_ref_cursor_out_bind_comes_back_as_a_result_set_that_reads_its_rows()
    {
        const string text = "BEGIN OPEN :c FOR SELECT 1 FROM dual; END;";
        var table = Table(["N"], ["1"], ["2"]);
        Serve(cmd =>
        {
            if (cmd.CommandText != text)
            {
                return null;
            }

            cmd.Parameters["c"].Value = FakeDriverHost.ProviderValue("TiberoRefCursor", table);
            return -1;
        });
        var service = NewService();
        await using var session = NewSession();
        var binds = new Dictionary<string, BindValue> { ["c"] = new(BindDataType.RefCursor, null, BindDirection.Out) };

        var result = await service.ExecuteAsync(session, Stmt(text, StatementKind.PlSql) with { BindNames = ["c"] }, binds, null,
            CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Null(result.ResultSet); // 문장 자체의 결과 집합은 없다. 커서는 OUT 값으로 온다.
        await using var cursor = Assert.IsType<TiberoResultSet>(result.OutValues["c"]);
        Assert.False(session.CommandGate.IsHeld); // 결과 집합은 페이지를 읽을 때만 잠금을 잡는다
        var page = await cursor.GetPageAsync(0, CancellationToken.None);
        Assert.Equal(["1", "2"], page.Columns[0].Select(v => (string)v!));
        Assert.Equal([text, TiberoExecutionService.TransactionIdSql], Sent());
        Assert.False(session.CommandGate.IsHeld);
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task A_missing_bind_value_fails_without_sending_the_statement()
    {
        const string text = "DELETE FROM t WHERE id = :id";
        Serve();
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(text, StatementKind.Dml) with { BindNames = ["id"] }, NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("ORANDA-EXEC", result.Error?.Code);
        Assert.Equal(TimeSpan.Zero, result.Elapsed);
        Assert.DoesNotContain(text, Sent());
        Assert.DoesNotContain(TiberoExecutionService.TransactionIdSql, Sent());
        Assert.False(session.CommandGate.IsHeld);
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)", StatementKind.Dml, false)]
    [InlineData("CREATE TABLE ort_x (a NUMBER)", StatementKind.Ddl, false)]
    [InlineData("BEGIN DELETE FROM t; END;", StatementKind.PlSql, false)]
    [InlineData("CALL p()", StatementKind.Other, true)]
    public async Task A_read_only_tibero_profile_is_protected_only_by_the_policy_gate_and_writes_never_reach_the_server(
        string text, StatementKind kind, bool uncertain)
    {
        // 실측 #28: Tibero 는 READ ONLY 트랜잭션 안에서도 INSERT·DDL 을 받아들인다(Oracle 은 ORA-01456). 서버 쪽 막이가 없으므로
        // 읽기 전용 프로필의 쓰기 차단은 실제 정책 관문(ExecutionPolicy ReadOnlyProfile)뿐이다 — 이 시험이 그 막이를 고정한다.
        Serve();
        var service = new TiberoExecutionService(new ExecutionPolicy(), new FakeInteraction(null), _bus, null, dbmsOutput: false);
        await using var session = NewSession(readOnly: true);

        var result = await service.ExecuteAsync(session, Stmt(text, kind) with { IsClassificationUncertain = uncertain }, NoBinds, null,
            CancellationToken.None);

        Assert.Equal(ExecutionStatus.Denied, result.Status);
        Assert.Equal(PolicyRule.ReadOnlyProfile, result.Policy?.Rule);
        Assert.Empty(Sent()); // 준비 블록(읽기 전용 트랜잭션)조차 보내지 않는다
    }

    [Fact]
    public async Task Policy_denial_never_reaches_the_driver()
    {
        Serve();
        var deny = new PolicyDecision(PolicyDecisionKind.Deny, PolicyRule.ReadOnlyProfile, "읽기 전용");
        var service = NewService(policy: new FakePolicy(deny));
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt("DELETE FROM t", StatementKind.Dml), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Denied, result.Status);
        Assert.Same(deny, result.Policy);
        Assert.Empty(Sent());
        Assert.Single(Events<PolicyDecisionMade>());
        Assert.Single(Events<StatementExecuted>());
    }

    [Fact]
    public async Task Sql_plus_commands_go_to_the_processor_and_not_to_the_driver()
    {
        Serve();
        var sqlPlus = new FakeSqlPlus();
        var service = NewService(sqlPlus: sqlPlus);
        await using var session = NewSession();
        var stmt = Stmt("PROMPT hello", StatementKind.SqlPlus);

        var result = await service.ExecuteAsync(session, stmt, NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal([stmt], sqlPlus.Executed);
        Assert.Empty(Sent());
    }

    [Fact]
    public async Task Substitution_uses_sql_plus_defines_and_asks_only_for_the_rest()
    {
        Serve();
        var sqlPlus = new FakeSqlPlus();
        sqlPlus.Defines["tab"] = "ort_emp";
        var interaction = new FakeInteraction(new Dictionary<string, string> { ["id"] = "5" });
        var service = NewService(sqlPlus: sqlPlus, interaction: interaction);
        await using var session = NewSession();
        var stmt = Stmt("DELETE FROM &tab WHERE id = &id", StatementKind.Dml) with { SubstitutionNames = ["tab", "id"] };

        var result = await service.ExecuteAsync(session, stmt, NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(["id"], interaction.Asked);
        Assert.Contains("DELETE FROM ort_emp WHERE id = 5", Sent());
        Assert.Equal("DELETE FROM ort_emp WHERE id = 5", result.Statement.Text);
    }

    [Fact]
    public async Task A_query_returns_a_result_set_with_its_columns_and_rows_and_sends_nothing_else()
    {
        const string text = "SELECT a, b FROM t";
        Serve(cmd => cmd.CommandText == text ? Table(["A", "B"], ["1", "x"], ["2", "y"]) : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(text, StatementKind.Query), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        await using var resultSet = result.ResultSet;
        Assert.NotNull(resultSet);
        Assert.Equal(["A", "B"], resultSet.Columns.Select(c => c.Name));
        Assert.Null(result.RowsAffected);
        Assert.Empty(result.DbmsOutput);
        Assert.False(result.TransactionPending);
        // 순수 질의 뒤에는 트랜잭션 확인도 출력 거두기도 보내지 않는다(Oracle 과 같다).
        Assert.Equal([text], Sent());
        Assert.False(session.CommandGate.IsHeld);
        Assert.Equal(SessionState.Idle, session.State);

        var page = await resultSet.GetPageAsync(0, CancellationToken.None);
        Assert.Equal(2, page.RowCount);
        Assert.Equal(["1", "2"], page.Columns[0].Select(v => (string)v!));
        Assert.Equal(["x", "y"], page.Columns[1].Select(v => (string)v!));
        Assert.True(page.IsLast);
        Assert.Single(Events<StatementExecuted>());
    }

    [Fact]
    public async Task The_result_set_owns_the_gate_per_page_and_disposing_it_releases_everything()
    {
        const string text = "SELECT a FROM t";
        Serve(cmd => cmd.CommandText == text ? Table(["A"], ["1"], ["2"], ["3"]) : null);
        var service = NewService(pageSize: 2);
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(text, StatementKind.Query), NoBinds, null, CancellationToken.None);
        var resultSet = result.ResultSet!;

        // 실행은 리더를 연 뒤 잠금을 놓았다: 다른 기능이 같은 연결을 쓸 수 있다.
        Assert.False(session.CommandGate.IsHeld);
        Assert.Equal(2, resultSet.PageSize);

        await session.CommandGate.EnterAsync(CancellationToken.None);
        var pending = resultSet.GetPageAsync(0, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(pending.IsCompleted); // 페이지 가져오기는 잠금을 기다린다
        session.CommandGate.Release();

        var first = await pending;
        Assert.Equal(2, first.RowCount);
        Assert.False(first.IsLast);
        Assert.False(session.CommandGate.IsHeld);

        await resultSet.DisposeAsync();
        Assert.False(session.CommandGate.IsHeld);
        Assert.Equal(SessionState.Idle, session.State);
        await session.CommandGate.EnterAsync(CancellationToken.None); // 잠금이 잠긴 채 남았다면 여기서 멈춘다
        session.CommandGate.Release();
    }

    [Fact]
    public async Task The_grid_settings_decide_the_page_size_at_each_execution()
    {
        const string text = "SELECT a FROM t";
        Serve(cmd => cmd.CommandText == text ? Table(["A"], ["1"], ["2"], ["3"]) : null);
        var grid = new FakeGrid(3, 4000);
        var service = NewService(pageSize: 1, grid: grid);
        await using var session = NewSession();

        var first = await service.ExecuteAsync(session, Stmt(text, StatementKind.Query), NoBinds, null, CancellationToken.None);
        await using (first.ResultSet!)
        {
            Assert.Equal(3, first.ResultSet!.PageSize);
        }

        grid.PageSize = 2;
        var second = await service.ExecuteAsync(session, Stmt(text, StatementKind.Query), NoBinds, null, CancellationToken.None);
        await using (second.ResultSet!)
        {
            Assert.Equal(2, second.ResultSet!.PageSize);
        }
    }

    [Fact]
    public async Task A_for_update_query_still_tracks_the_transaction()
    {
        const string text = "SELECT a FROM t FOR UPDATE";
        Serve(cmd => cmd.CommandText switch
        {
            text => Table(["A"], ["1"]),
            TiberoExecutionService.TransactionIdSql => "12.66.243778",
            _ => null,
        });
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(text, StatementKind.Query), NoBinds, null, CancellationToken.None);

        await using var resultSet = result.ResultSet;
        Assert.NotNull(resultSet);
        Assert.Equal([text, TiberoExecutionService.TransactionIdSql], Sent());
        Assert.True(result.TransactionPending);
        Assert.True(session.HasPendingTransaction);
    }

    [Fact]
    public async Task A_server_error_on_a_query_fails_without_a_result_set_and_leaves_the_session_usable()
    {
        const string text = "SELECT * FROM nope";
        Serve(cmd => cmd.CommandText == text
            ? throw FakeDriverHost.ServerError(-8033, "TBR-8033: Specified schema object was not found.")
            : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(text, StatementKind.Query), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("TBR-8033", result.Error?.Code);
        Assert.Null(result.ResultSet);
        Assert.False(session.CommandGate.IsHeld);
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task An_uncertain_statement_that_returns_rows_gives_a_result_set_and_tracks_the_transaction()
    {
        const string text = "WITH x AS (SELECT 1 a FROM dual) SELECT a FROM x";
        Serve(cmd => cmd.CommandText switch
        {
            text => Table(["A"], ["1"]),
            TiberoExecutionService.TransactionIdSql => "12.66.243778",
            _ => null,
        });
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(text, StatementKind.Other) with { IsClassificationUncertain = true }, NoBinds, null,
            CancellationToken.None);

        // 분류가 불확실하면 문장이 트랜잭션을 바꿨을 수 있다 → 질의로 보고 건너뛰지 않고 늘 묻는다.
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        await using var resultSet = result.ResultSet;
        Assert.NotNull(resultSet);
        Assert.Equal([text, TiberoExecutionService.TransactionIdSql], Sent());
        Assert.True(result.TransactionPending);
        Assert.True(session.HasPendingTransaction);
        Assert.True(result.Elapsed > TimeSpan.Zero);
        Assert.False(session.CommandGate.IsHeld);
        Assert.Equal("1", (await resultSet.GetPageAsync(0, CancellationToken.None)).Columns[0][0]);
    }

    [Fact]
    public async Task An_uncertain_statement_without_rows_has_no_result_set()
    {
        const string text = "CALL do_something()";
        Serve(cmd => cmd.CommandText == text ? new DataTable() : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(text, StatementKind.Other) with { IsClassificationUncertain = true }, NoBinds, null,
            CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Null(result.ResultSet);
        Assert.Null(result.RowsAffected); // 가짜 리더의 RecordsAffected 는 -1
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task A_server_error_is_mapped_to_tbr_and_the_transaction_is_still_tracked()
    {
        const string text = "DELETE FROM nope";
        Serve(cmd => cmd.CommandText == text
            ? throw FakeDriverHost.ServerError(-8033, "TBR-8033: Specified schema object was not found.")
            : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(text, StatementKind.Dml), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("TBR-8033", result.Error?.Code);
        Assert.Equal(DbErrorKind.ObjectNotFound, result.Error?.Kind);
        Assert.Equal(Dbms.Tibero, result.Error?.Dbms);
        Assert.Equal(TiberoExecutionService.TransactionIdSql, Sent()[^1]);
        Assert.Equal(SessionState.Idle, session.State);
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task A_disconnect_breaks_the_session_without_tracking()
    {
        const string text = "DELETE FROM t";
        Serve(cmd => cmd.CommandText == text ? throw FakeDriverHost.ClientError(90403, "ODP-90403: connection broken") : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(text, StatementKind.Dml), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(DbErrorKind.Disconnected, result.Error?.Kind);
        Assert.Equal(SessionState.Broken, session.State);
        Assert.Single(Events<SessionBroken>());
        Assert.DoesNotContain(TiberoExecutionService.TransactionIdSql, Sent());
        Assert.False(session.CommandGate.IsHeld); // Busy 는 Broken 이 덮었다(Busy 로 남지 않음)
    }

    [Fact]
    public async Task A_cancel_during_execution_reports_cancelled_and_still_tracks_the_transaction()
    {
        const string text = "UPDATE t SET a = 1";
        using var cts = new CancellationTokenSource();
        Serve(cmd =>
        {
            if (cmd.CommandText == text)
            {
                // 사용자가 실행 중에 취소 → 드라이버가 TBR-12040 을 낸다(실측 4장).
                cts.Cancel();
                throw FakeDriverHost.ServerError(-12040, "TBR-12040: Statement was canceled.");
            }

            return cmd.CommandText == TiberoExecutionService.TransactionIdSql ? "12.66.243778" : null;
        });
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(text, StatementKind.Dml), NoBinds, null, cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Null(result.Error);
        Assert.Equal([text, TiberoExecutionService.TransactionIdSql], Sent());
        Assert.True(result.TransactionPending);
        Assert.True(session.HasPendingTransaction);
        Assert.True(result.Elapsed > TimeSpan.Zero);
        Assert.False(session.CommandGate.IsHeld);
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task A_cancel_keeps_the_call_open_until_the_driver_returns_then_reports_cancelled()
    {
        // 서버가 늦게 멈추는 상황(실측 4장: 10ms~58초). 취소를 눌러도 드라이버가 돌아올 때까지 호출은 끝나지 않고 세션은 Busy 다(화면의 「취소 중」).
        const string text = "SELECT COUNT(*) FROM all_objects a, all_objects b, all_objects d";
        using var cts = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        using var serverStops = new SemaphoreSlim(0);
        Serve(cmd =>
        {
            if (cmd.CommandText == text)
            {
                started.Set();
                serverStops.Wait(TimeSpan.FromSeconds(30));
                throw FakeDriverHost.ServerError(-12040, "TBR-12040: Statement was canceled.");
            }

            return cmd.CommandText == TiberoExecutionService.TransactionIdSql ? "12.66.243778" : null;
        });
        var service = NewService();
        await using var session = NewSession();

        var run = service.ExecuteAsync(session, Stmt(text, StatementKind.Query), NoBinds, null, cts.Token);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        cts.Cancel();
        await Task.Delay(100);

        Assert.False(run.IsCompleted);
        Assert.Equal(SessionState.Busy, session.State);
        Assert.True(session.CommandGate.IsHeld);

        serverStops.Release();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Null(result.Error);
        Assert.Null(result.ResultSet);
        Assert.Equal(SessionState.Idle, session.State);
        Assert.False(session.CommandGate.IsHeld);
        Assert.Equal([text, TiberoExecutionService.TransactionIdSql], Sent());
    }

    [Fact]
    public async Task A_token_that_a_caller_cancels_after_a_time_limit_is_a_cancel_like_any_other()
    {
        // Oracle 서비스에는 문장 시간 제한이 없다: 호출한 쪽이 CancelAfter 로 건 토큰이 취소되면 사용자 취소와 같은 Cancelled 다(구분 없음).
        const string text = "UPDATE t SET a = 1";
        using var limit = new CancellationTokenSource();
        limit.CancelAfter(TimeSpan.FromMilliseconds(100));
        Serve(cmd =>
        {
            if (cmd.CommandText == text)
            {
                limit.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                throw FakeDriverHost.ServerError(-12040, "TBR-12040: Statement was canceled.");
            }

            return cmd.CommandText == TiberoExecutionService.TransactionIdSql ? "12.66.243778" : null;
        });
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(text, StatementKind.Dml), NoBinds, null, limit.Token).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Null(result.Error);
        Assert.True(result.TransactionPending);
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task A_cancel_error_without_a_cancelled_token_is_a_failure_and_does_not_break_the_session()
    {
        // 우리가 취소하지 않았는데 온 TBR-12040(다른 세션의 KILL 등)은 Oracle 의 ORA-01013 처럼 실패로 낸다.
        const string text = "UPDATE t SET a = 1";
        Serve(cmd => cmd.CommandText == text ? throw FakeDriverHost.ServerError(-12040, "TBR-12040: Statement was canceled.") : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(text, StatementKind.Dml), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(-12040, result.Error?.Number);
        Assert.Equal("TBR-12040", result.Error?.Code);
        Assert.Equal(DbErrorKind.Cancelled, result.Error?.Kind);
        Assert.Equal(SessionState.Idle, session.State);
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task A_cancel_while_preparing_the_session_reports_zero_elapsed_and_never_sends_the_statement()
    {
        const string text = "UPDATE t SET a = 1";
        using var cts = new CancellationTokenSource();
        Serve(cmd =>
        {
            if (cmd.CommandText == TiberoExecutionService.ReadOnlyBlock)
            {
                cts.Cancel();
                throw FakeDriverHost.ServerError(-12040, "TBR-12040: Statement was canceled.");
            }

            return null;
        });
        var service = NewService();
        await using var session = NewSession(readOnly: true);

        var result = await service.ExecuteAsync(session, Stmt(text, StatementKind.Dml), NoBinds, null, cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Equal(TimeSpan.Zero, result.Elapsed);
        Assert.Equal([TiberoExecutionService.ReadOnlyBlock], Sent());
        Assert.False(session.CommandGate.IsHeld);
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task A_cancelled_token_returns_cancelled_without_sending()
    {
        Serve();
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(Block, StatementKind.PlSql), NoBinds, null, new CancellationToken(canceled: true));

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Empty(Sent());
    }

    [Fact]
    public async Task A_script_runs_every_statement_in_order_and_reports_each_result()
    {
        Serve();
        var service = NewService();
        await using var session = NewSession();

        var results = await RunScriptAsync(service, session, ScriptErrorMode.Stop, Stmt(Block, StatementKind.PlSql), Stmt("COMMIT", StatementKind.TransactionControl));

        Assert.Equal([ExecutionStatus.Succeeded, ExecutionStatus.Succeeded], results.Select(r => r.Status));
        Assert.Equal([Block, "COMMIT"], results.Select(r => r.Statement.Text));
        Assert.Equal(
            [Block, TiberoExecutionService.TransactionIdSql, "COMMIT", TiberoExecutionService.TransactionIdSql],
            Sent());
        Assert.Equal(2, Events<StatementExecuted>().Count);
        Assert.All(Events<StatementExecuted>(), e => Assert.Equal(HistorySource.Script, e.Options.Source));
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task A_script_stops_at_the_first_failure_in_stop_mode_and_keeps_going_in_continue_mode()
    {
        const string bad = "DELETE FROM nope";
        Serve(cmd => cmd.CommandText == bad ? throw FakeDriverHost.ServerError(-8033, "TBR-8033: not found") : null);
        var service = NewService();
        await using var session = NewSession();
        SqlStatement[] script = [Stmt(bad, StatementKind.Dml), Stmt(Block, StatementKind.PlSql)];

        var stopped = await RunScriptAsync(service, session, ScriptErrorMode.Stop, script);
        var continued = await RunScriptAsync(service, session, ScriptErrorMode.Continue, script);

        Assert.Equal([ExecutionStatus.Failed], stopped.Select(r => r.Status));
        Assert.Equal([ExecutionStatus.Failed, ExecutionStatus.Succeeded], continued.Select(r => r.Status));
    }

    [Fact]
    public async Task In_ask_mode_the_user_decides_and_continue_all_stops_the_questions()
    {
        const string bad = "DELETE FROM nope";
        Serve(cmd => cmd.CommandText == bad ? throw FakeDriverHost.ServerError(-8033, "TBR-8033: not found") : null);
        var interaction = new FakeInteraction(null, ScriptErrorAction.ContinueAll);
        var service = NewService(interaction: interaction);
        await using var session = NewSession();

        var results = await RunScriptAsync(service, session, ScriptErrorMode.Ask,
            Stmt(bad, StatementKind.Dml), Stmt(bad, StatementKind.Dml), Stmt(Block, StatementKind.PlSql));

        Assert.Equal([ExecutionStatus.Failed, ExecutionStatus.Failed, ExecutionStatus.Succeeded], results.Select(r => r.Status));
        Assert.Equal(1, interaction.AskedErrors); // 첫 실패에서 「이후 계속」을 골랐으니 두 번째 실패는 묻지 않는다
        Assert.Equal("TBR-8033", interaction.LastFailure?.Code);

        var stop = new FakeInteraction(null, ScriptErrorAction.Stop);
        var stopped = await RunScriptAsync(NewService(interaction: stop), session, ScriptErrorMode.Ask, Stmt(bad, StatementKind.Dml), Stmt(Block, StatementKind.PlSql));
        Assert.Single(stopped);
    }

    [Fact]
    public async Task Whenever_sqlerror_exit_stops_the_script_even_in_continue_mode_and_ends_the_transaction()
    {
        const string bad = "DELETE FROM nope";
        Serve(cmd => cmd.CommandText == bad ? throw FakeDriverHost.ServerError(-8033, "TBR-8033: not found") : null);
        var sqlPlus = new FakeSqlPlus { Exit = true };
        var service = NewService(sqlPlus: sqlPlus);
        await using var session = NewSession();

        var results = await RunScriptAsync(service, session, ScriptErrorMode.Continue, Stmt(bad, StatementKind.Dml), Stmt(Block, StatementKind.PlSql));

        Assert.Equal([ExecutionStatus.Failed], results.Select(r => r.Status));
        Assert.Equal(1, sqlPlus.OnErrorApplied);
        Assert.DoesNotContain(Block, Sent());
    }

    [Fact]
    public async Task A_cancelled_script_yields_a_cancelled_result_and_stops()
    {
        Serve();
        var service = NewService();
        await using var session = NewSession();
        var results = new List<ExecutionResult>();

        await foreach (var r in service.ExecuteScriptAsync(session, [Stmt(Block, StatementKind.PlSql), Stmt(Block, StatementKind.PlSql)],
                           ScriptErrorMode.Continue, null, new CancellationToken(canceled: true)))
        {
            results.Add(r);
        }

        Assert.Equal([ExecutionStatus.Cancelled], results.Select(r => r.Status));
        Assert.Empty(Sent());
    }

    private static async Task<List<ExecutionResult>> RunScriptAsync(TiberoExecutionService service, ISession session, ScriptErrorMode mode,
                                                                    params SqlStatement[] statements)
    {
        var results = new List<ExecutionResult>();
        await foreach (var r in service.ExecuteScriptAsync(session, statements, mode, null, CancellationToken.None))
        {
            results.Add(r);
        }

        return results;
    }

    /// <summary>문자열 열만 가진 DataTable(스키마 표가 없는 가짜 리더는 모든 열을 글자로 읽는다).</summary>
    private static DataTable Table(string[] columns, params string[][] rows)
    {
        var table = new DataTable();
        foreach (var c in columns)
        {
            table.Columns.Add(c, typeof(string));
        }

        foreach (var row in rows)
        {
            table.Rows.Add([.. row]);
        }

        return table;
    }

    private static SqlStatement Stmt(string text, StatementKind kind) => new(text, default, kind);

    /// <summary>드라이버 글자 값(OUT 값·스칼라). null 이면 IsNull 인 값. 드라이버 형식 이름은 FakeDriverHost 가 맡는다(adapter 문서 5장).</summary>
    private static object ProviderString(string? text) =>
        text is null ? FakeDriverHost.ProviderValue("TiberoString") : FakeDriverHost.ProviderValue("TiberoString", text);

    private TiberoExecutionService NewService(FakePolicy? policy = null, FakeInteraction? interaction = null, FakeSqlPlus? sqlPlus = null,
                                              bool dbmsOutput = false, int pageSize = 500, IGridFetchSettings? grid = null) =>
        new(policy ?? new FakePolicy(PolicyDecision.Allowed), interaction ?? new FakeInteraction(null), _bus, sqlPlus, dbmsOutput, pageSize, grid);

    private TiberoSession NewSession(bool readOnly = false)
    {
        var driver = FakeDriverHost.Driver;
        var connection = driver.CreateConnection("Data Source=x");
        connection.Open();
        var profile = new ConnectionProfile("t", "u", "h:1/d", Dbms: Dbms.Tibero, ReadOnly: readOnly);
        return new TiberoSession(Guid.NewGuid(), profile, Server, SessionPurpose.Work, "D/-/U", connection, null, _bus, driver: driver);
    }

    /// <summary>보낸 문장을 기록하고 <paramref name="respond"/> 의 답(정수 = 행 수, 값 = 스칼라, DataTable = 리더)을 돌려준다.</summary>
    private void Serve(Func<DbCommand, object?>? respond = null) =>
        FakeDriverHost.SetHandler(cmd =>
        {
            lock (_sent)
            {
                _sent.Add(cmd.CommandText);
            }

            return respond?.Invoke(cmd);
        });

    /// <summary>거두기 블록에 <paramref name="count"/> 줄·<paramref name="packed"/> 묶음으로 답한다(그 밖의 문장은 답 없음).</summary>
    private void ServeOutput(string packed, int count) =>
        Serve(cmd =>
        {
            if (cmd.CommandText != TiberoDbmsOutput.CollectBlock)
            {
                return null;
            }

            cmd.Parameters["n"].Value = ProviderString(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            cmd.Parameters["r"].Value = ProviderString(packed);
            return -1;
        });

    private List<string> Sent()
    {
        lock (_sent)
        {
            return [.. _sent];
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

    private sealed class FakePolicy(PolicyDecision decision) : IExecutionPolicy
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) => ValueTask.FromResult(decision);
    }

    private sealed class FakeInteraction(IReadOnlyDictionary<string, string>? answers, ScriptErrorAction onScriptError = ScriptErrorAction.Stop) : IExecutionInteraction
    {
        public List<string> Asked { get; } = [];

        public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct)
        {
            Asked.AddRange(names);
            return Task.FromResult(answers);
        }

        public int AskedErrors { get; private set; }

        public DbError? LastFailure { get; private set; }

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct)
        {
            AskedErrors++;
            LastFailure = failure;
            return Task.FromResult(onScriptError);
        }
    }

    private sealed class FakeGrid(int pageSize, int lobFetchSize) : IGridFetchSettings
    {
        public int PageSize { get; set; } = pageSize;

        public int InitialLobFetchSize { get; set; } = lobFetchSize;
    }

    private sealed class FakeSqlPlus : ISqlPlusCommandProcessor, IScriptDirectives
    {
        public List<SqlStatement> Executed { get; } = [];

        public Dictionary<string, string> Defines { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, CancellationToken ct)
        {
            Executed.Add(stmt);
            return Task.FromResult(new ExecutionResult(stmt, ExecutionStatus.Succeeded));
        }

        public bool Exit { get; init; }

        public int OnErrorApplied { get; private set; }

        public bool ExitOnError(ISession s) => Exit;

        public Task ApplyOnErrorTransactionAsync(ISession s, CancellationToken ct)
        {
            OnErrorApplied++;
            return Task.CompletedTask;
        }

        public string? GetDefine(ISession s, string name) => Defines.TryGetValue(name, out var v) ? v : null;

        public void SetDefine(ISession s, string name, string value) => Defines[name] = value;
    }
}
