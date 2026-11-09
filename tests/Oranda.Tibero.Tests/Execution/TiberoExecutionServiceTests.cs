using System.Data;
using System.Data.Common;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Tibero.Execution;
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
    public async Task An_uncertain_statement_that_returns_rows_keeps_its_own_dbms_output()
    {
        const string text = "CALL report_rows()";
        var table = new DataTable();
        table.Columns.Add("A", typeof(decimal));
        table.Rows.Add(1m);
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

        // 결과 집합은 E4 전이라 지원 안 함이지만, 돈 문장의 출력은 다음 문장으로 넘어가지 않고 이 결과에 붙는다.
        Assert.Equal(TiberoExecutionService.NotSupportedCode, result.Error?.Code);
        Assert.Equal(["ran!"], result.DbmsOutput);
        Assert.Single(Events<DbmsOutputReceived>());
        Assert.False(session.CommandGate.IsHeld);
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
    public async Task A_ref_cursor_out_bind_is_not_supported_yet_and_is_not_sent()
    {
        const string text = "BEGIN OPEN :c FOR SELECT 1 FROM dual; END;";
        Serve();
        var service = NewService();
        await using var session = NewSession();
        var binds = new Dictionary<string, BindValue> { ["c"] = new(BindDataType.RefCursor, null, BindDirection.Out) };

        var result = await service.ExecuteAsync(session, Stmt(text, StatementKind.PlSql) with { BindNames = ["c"] }, binds, null,
            CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(TiberoExecutionService.NotSupportedCode, result.Error?.Code);
        Assert.Empty(Sent());
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
    public async Task Queries_are_not_supported_yet_and_are_not_sent()
    {
        Serve();
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt("SELECT 1 FROM dual", StatementKind.Query), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(TiberoExecutionService.NotSupportedCode, result.Error?.Code);
        Assert.Empty(Sent());
    }

    [Fact]
    public async Task An_uncertain_statement_that_returns_rows_is_reported_as_not_supported()
    {
        const string text = "WITH x AS (SELECT 1 a FROM dual) SELECT a FROM x";
        var table = new DataTable();
        table.Columns.Add("A", typeof(decimal));
        table.Rows.Add(1m);
        Serve(cmd => cmd.CommandText switch
        {
            text => table,
            TiberoExecutionService.TransactionIdSql => "12.66.243778",
            _ => null,
        });
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(text, StatementKind.Other) with { IsClassificationUncertain = true }, NoBinds, null,
            CancellationToken.None);

        // 실행은 되었다: 트랜잭션을 갱신하고 걸린 시간을 남긴다(보내지 않은 질의 거절과 구별, App 쪽 WasSent 보완은 따로).
        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(TiberoExecutionService.NotSupportedCode, result.Error?.Code);
        Assert.Equal([text, TiberoExecutionService.TransactionIdSql], Sent());
        Assert.True(result.TransactionPending);
        Assert.True(session.HasPendingTransaction);
        Assert.True(result.Elapsed > TimeSpan.Zero);
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
    public async Task Script_execution_is_not_supported_yet()
    {
        var service = NewService();
        await using var session = NewSession();

        Assert.Throws<DbmsNotSupportedException>(() =>
            service.ExecuteScriptAsync(session, [Stmt(Block, StatementKind.PlSql)], ScriptErrorMode.Stop, null, CancellationToken.None));
    }

    private static SqlStatement Stmt(string text, StatementKind kind) => new(text, default, kind);

    /// <summary>드라이버 글자 값(OUT 값·스칼라). null 이면 IsNull 인 값. 드라이버 형식 이름은 FakeDriverHost 가 맡는다(adapter 문서 5장).</summary>
    private static object ProviderString(string? text) =>
        text is null ? FakeDriverHost.ProviderValue("TiberoString") : FakeDriverHost.ProviderValue("TiberoString", text);

    private TiberoExecutionService NewService(FakePolicy? policy = null, FakeInteraction? interaction = null, FakeSqlPlus? sqlPlus = null,
                                              bool dbmsOutput = false) =>
        new(policy ?? new FakePolicy(PolicyDecision.Allowed), interaction ?? new FakeInteraction(null), _bus, sqlPlus, dbmsOutput);

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

    private sealed class FakeInteraction(IReadOnlyDictionary<string, string>? answers) : IExecutionInteraction
    {
        public List<string> Asked { get; } = [];

        public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct)
        {
            Asked.AddRange(names);
            return Task.FromResult(answers);
        }

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) =>
            Task.FromResult(ScriptErrorAction.Stop);
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

        public bool ExitOnError(ISession s) => false;

        public Task ApplyOnErrorTransactionAsync(ISession s, CancellationToken ct) => Task.CompletedTask;

        public string? GetDefine(ISession s, string name) => Defines.TryGetValue(name, out var v) ? v : null;

        public void SetDefine(ISession s, string name, string value) => Defines[name] = value;
    }
}
