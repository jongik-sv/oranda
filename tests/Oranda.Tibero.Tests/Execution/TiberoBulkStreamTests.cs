using System.Data;
using System.Data.Common;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Results;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Driver;

namespace Oranda.Tibero.Tests.Execution;

/// <summary>
/// 대량 읽기 <see cref="TiberoBulkExecutionService.StreamRowsAsync"/> 단위 시험(TB1 설계 4.3 StreamRowsAsync). 가짜 드라이버의 처리기가 스키마 표 붙은
/// DataTable(<see cref="FakeDriverHost.ProviderColumn"/>)을 돌려주고 보낸 명령·매개변수를 기록한다. 처리기가 정적이라 FakeDriver 컬렉션에 둔다.
/// </summary>
[Collection("FakeDriver")]
public sealed class TiberoBulkStreamTests : IDisposable
{
    private const string Select = "SELECT * FROM \"T\"";

    private static readonly ServerInfo Server = new("6", "i", "h", null, "D", null, false, 7,
        new ServerCapabilities(false, false, true, false, false, Dbms.Tibero), DbUniqueName: "D", SessionUser: "U", Dbms: Dbms.Tibero);

    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();

    private readonly List<Sent> _sent = [];
    private readonly EventBus _bus = new();
    private readonly List<OrandaEvent> _events = [];

    public TiberoBulkStreamTests()
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
    public async Task A_statement_that_is_not_a_query_is_denied_before_the_policy_is_asked()
    {
        Serve(_ => new DataTable());
        var policy = new FakePolicy(PolicyDecision.Allowed);
        var service = NewService(policy);
        await using var session = NewSession();

        // 분류가 Query 로 잘못 붙은 DELETE, 분류가 DML 인 SELECT 모두 거부한다.
        var misclassified = await Assert.ThrowsAsync<BulkDeniedException>(() =>
            Collect(service.StreamRowsAsync(session, new SqlStatement("DELETE FROM T", default, StatementKind.Query), NoBinds,
                ExecutionOptions.Default, CancellationToken.None)));
        await Assert.ThrowsAsync<BulkDeniedException>(() =>
            Collect(service.StreamRowsAsync(session, new SqlStatement(Select, default, StatementKind.Dml), NoBinds,
                ExecutionOptions.Default, CancellationToken.None)));

        Assert.Equal(PolicyDecisionKind.Deny, misclassified.Decision.Kind);
        Assert.Contains("DELETE", misclassified.Message, StringComparison.Ordinal);
        Assert.Equal(0, policy.Calls);
        Assert.Empty(_sent);
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task A_policy_deny_is_bulk_denied_and_a_declined_confirmation_is_a_cancellation()
    {
        Serve(_ => new DataTable());
        await using var session = NewSession(environment: ConnectionEnvironment.Test); // 개발 접속은 확인을 묻지 않는다(D-065)
        var deny = NewService(new FakePolicy(new PolicyDecision(PolicyDecisionKind.Deny, PolicyRule.ProductionChange, "no")));
        var confirm = NewService(new FakePolicy(new PolicyDecision(PolicyDecisionKind.Confirm, PolicyRule.ProductionChange, "sure?")),
            new FakeInteraction(confirm: false));

        var denied = await Assert.ThrowsAsync<BulkDeniedException>(() =>
            Collect(deny.StreamRowsAsync(session, Query(), NoBinds, ExecutionOptions.Default, CancellationToken.None)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Collect(confirm.StreamRowsAsync(session, Query(), NoBinds, ExecutionOptions.Default, CancellationToken.None)));

        Assert.Equal(PolicyRule.ProductionChange, denied.Decision.Rule);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Values_come_out_per_column_type_as_lossless_tibero_values_without_driver_types()
    {
        var clob = new string('가', 20_000); // 읽기 조각(8192자)을 여러 번 넘는다
        var blob = Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray();
        var table = new DataTable();
        FakeDriverHost.ProviderColumn(table, "N", typeof(byte[]), TiberoColumnTypes.PtDecimal);
        FakeDriverHost.ProviderColumn(table, "N29", typeof(byte[]), TiberoColumnTypes.PtInt64); // 정수 형식 열도 원시 바이트
        FakeDriverHost.ProviderColumn(table, "ND", typeof(decimal), TiberoColumnTypes.PtDecimal); // 원시 바이트가 없으면 GetValue 글자
        FakeDriverHost.ProviderColumn(table, "BD", typeof(byte[]), TiberoColumnTypes.PtBinaryDouble); // 원시 바이트(R1x: 드라이버 GetValue 는 음수가 틀림)
        FakeDriverHost.ProviderColumn(table, "BF", typeof(byte[]), TiberoColumnTypes.PtBinaryFloat);
        FakeDriverHost.ProviderColumn(table, "V", typeof(string), TiberoColumnTypes.PtVarchar2);
        FakeDriverHost.ProviderColumn(table, "C", typeof(string), TiberoColumnTypes.PtChar);
        FakeDriverHost.ProviderColumn(table, "CL", typeof(string), TiberoColumnTypes.PtClob);
        FakeDriverHost.ProviderColumn(table, "BL", typeof(byte[]), TiberoColumnTypes.PtBlob);
        FakeDriverHost.ProviderColumn(table, "R", typeof(byte[]), TiberoColumnTypes.PtRaw);
        FakeDriverHost.ProviderColumn(table, "D", typeof(DateTime), TiberoColumnTypes.PtDate);
        FakeDriverHost.ProviderColumn(table, "TS", typeof(byte[]), TiberoColumnTypes.PtTimeStamp);
        table.Rows.Add(
            Hex("04 3E 7F 4E F0"), // -1.5
            Hex("10 CF 81 97 AD C3 D9 81 97 AD C3 D9 81 97 AD C3 D9"), // 29자리
            1.50m, Hex("3F FD FF FF FF FF FF FF"), Hex("3F EF FF FF"), "v", "ab   ", clob, blob, new byte[] { 0x0A, 0xFF }, new DateTime(2026, 1, 2, 3, 4, 5),
            Hex("78 7E 01 02 03 04 05 00 07 5B CD 15")); // 2026-01-02 03:04:05.123456789
        table.Rows.Add(Enumerable.Repeat<object>(DBNull.Value, table.Columns.Count).ToArray());
        Serve(cmd => cmd.CommandText == Select ? table : null);
        var service = NewService();
        await using var session = NewSession();

        var rows = await Collect(service.StreamRowsAsync(session, Query(), NoBinds, ExecutionOptions.Default, CancellationToken.None));

        Assert.Equal(2, rows.Count);
        var row = rows[0];
        Assert.Equal("-1.5", Assert.IsType<TiberoBulkNumber>(row[0]).Text);
        Assert.Equal("12345678901234567890123456789", Assert.IsType<TiberoBulkNumber>(row[1]).Text);
        Assert.Equal("1.5", Assert.IsType<TiberoBulkNumber>(row[2]).Text);
        Assert.Equal(-2.25d, Assert.IsType<double>(row[3]));
        Assert.Equal(-2.25f, Assert.IsType<float>(row[4]));
        Assert.Equal("v", row[5]);
        Assert.Equal("ab   ", row[6]); // CHAR 끝 공백 그대로
        Assert.Equal(clob, row[7]);
        Assert.Equal(blob, Assert.IsType<byte[]>(row[8]));
        Assert.Equal(new byte[] { 0x0A, 0xFF }, row[9]);
        var date = Assert.IsType<DateTime>(row[10]);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), date);
        Assert.Equal(DateTimeKind.Unspecified, date.Kind);
        Assert.Equal(new TiberoBulkTimestamp(new DateTime(2026, 1, 2, 3, 4, 5), 123_456_789), row[11]);
        Assert.All(rows[1], v => Assert.Null(v));

        Assert.Equal([Select], Texts());
        Assert.False(session.CommandGate.IsHeld);
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task Binary_float_double_decode_the_raw_bytes_and_long_and_long_raw_read_through_get_value()
    {
        // R1x 실측(win144, Tibero 6, 드라이버 1.1.35): BINARY_* 는 원시 바이트로 풀고, LONG 은 GetValue(가짜 GetString 은 "" 를 준다),
        // LONG RAW 도 GetValue(가짜 ReadRawBytes 는 72바이트 서술자를 준다).
        var longText = new string('L', 40_000);
        var longRaw = Enumerable.Range(0, 40_000).Select(i => (byte)i).ToArray();
        var table = new DataTable();
        FakeDriverHost.ProviderColumn(table, "BF", typeof(byte[]), TiberoColumnTypes.PtBinaryFloat);
        FakeDriverHost.ProviderColumn(table, "BD", typeof(byte[]), TiberoColumnTypes.PtBinaryDouble);
        FakeDriverHost.ProviderColumn(table, "LG", typeof(string), TiberoColumnTypes.PtLong);
        FakeDriverHost.ProviderColumn(table, "LR", typeof(byte[]), TiberoColumnTypes.PtLongRaw);
        table.Rows.Add(Hex("3F EF FF FF"), Hex("3F FD FF FF FF FF FF FF"), "x", new byte[] { 0x01 }); // -2.25
        table.Rows.Add(Hex("C0 10 00 00"), Hex("C0 02 00 00 00 00 00 00"), longText, longRaw); // 2.25
        table.Rows.Add(Hex("FF C0 00 00"), Hex("80 00 00 00 00 00 00 00"), "y", new byte[] { 0xFF }); // NaN, 0
        table.Rows.Add(Hex("FF 7F FF FF"), Hex("FF EF FF FF FF FF FF FF"), "z", new byte[] { 0x00 }); // +max
        table.Rows.Add(Hex("00 80 00 00"), Hex("00 10 00 00 00 00 00 00"), "w", new byte[] { 0x02 }); // -max
        Serve(cmd => cmd.CommandText == Select ? table : null);
        var service = NewService();
        await using var session = NewSession();

        var rows = await Collect(service.StreamRowsAsync(session, Query(), NoBinds, ExecutionOptions.Default, CancellationToken.None));

        Assert.Equal(5, rows.Count);
        Assert.Equal(-2.25f, Assert.IsType<float>(rows[0][0]));
        Assert.Equal(-2.25d, Assert.IsType<double>(rows[0][1]));
        Assert.Equal(2.25f, Assert.IsType<float>(rows[1][0]));
        Assert.Equal(2.25d, Assert.IsType<double>(rows[1][1]));
        Assert.True(float.IsNaN(Assert.IsType<float>(rows[2][0])));
        Assert.Equal(0d, Assert.IsType<double>(rows[2][1]));
        Assert.Equal(float.MaxValue, Assert.IsType<float>(rows[3][0]));
        Assert.Equal(double.MaxValue, Assert.IsType<double>(rows[3][1]));
        Assert.Equal(-float.MaxValue, Assert.IsType<float>(rows[4][0]));
        Assert.Equal(-double.MaxValue, Assert.IsType<double>(rows[4][1]));
        Assert.Equal("x", rows[0][2]);
        Assert.Equal(longText, rows[1][2]);
        Assert.Equal(new byte[] { 0x01 }, rows[0][3]);
        Assert.Equal(longRaw, Assert.IsType<byte[]>(rows[1][3]));
    }

    [Fact]
    public async Task A_binary_float_column_whose_raw_bytes_have_the_wrong_length_is_a_format_error_naming_the_column()
    {
        var table = new DataTable();
        FakeDriverHost.ProviderColumn(table, "BF", typeof(byte[]), TiberoColumnTypes.PtBinaryFloat);
        table.Rows.Add(new byte[] { 1, 2, 3 });
        Serve(cmd => cmd.CommandText == Select ? table : null);
        var service = NewService();
        await using var session = NewSession();

        var ex = await Assert.ThrowsAsync<FormatException>(() =>
            Collect(service.StreamRowsAsync(session, Query(), NoBinds, ExecutionOptions.Default, CancellationToken.None)));

        Assert.Contains("BF", ex.Message, StringComparison.Ordinal);
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task Rows_beyond_one_read_batch_all_come_through_in_order()
    {
        var table = new DataTable();
        FakeDriverHost.ProviderColumn(table, "V", typeof(string), TiberoColumnTypes.PtVarchar2);
        for (var i = 0; i < (2 * TiberoBulkReader.BatchRows) + 1; i++)
        {
            table.Rows.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Serve(cmd => cmd.CommandText == Select ? table : null);
        var service = NewService();
        await using var session = NewSession();

        var rows = await Collect(service.StreamRowsAsync(session, Query(), NoBinds, ExecutionOptions.Default, CancellationToken.None));

        Assert.Equal(Enumerable.Range(0, (2 * TiberoBulkReader.BatchRows) + 1).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            rows.Select(r => (string)r[0]!));
    }

    [Fact]
    public async Task A_read_only_profile_streams_a_query_and_the_service_never_sends_the_read_only_block_itself()
    {
        var table = new DataTable();
        FakeDriverHost.ProviderColumn(table, "V", typeof(string), TiberoColumnTypes.PtVarchar2);
        table.Rows.Add("a");
        table.Rows.Add("b");
        Serve(cmd => cmd.CommandText == Select ? table : null);
        var service = NewService();
        await using var session = NewSession(readOnly: true);

        var rows = await Collect(service.StreamRowsAsync(session, Query(), NoBinds, ExecutionOptions.Default, CancellationToken.None));

        Assert.Equal(2, rows.Count);
        Assert.Equal([Select], Texts()); // 읽기 전용 트랜잭션은 호출한 쪽이 연다 — 서비스는 문장 하나만 보낸다
        Assert.DoesNotContain(TiberoExecutionService.ReadOnlyBlock, Texts());
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task Rowid_and_bfile_columns_are_not_supported_naming_the_column_and_the_gate_is_released()
    {
        var rowid = new DataTable();
        FakeDriverHost.ProviderColumn(rowid, "ID", typeof(string), TiberoColumnTypes.PtVarchar2);
        FakeDriverHost.ProviderColumn(rowid, "RID", typeof(string), TiberoColumnTypes.PtVarchar2, isRowId: true);
        var bfile = new DataTable();
        FakeDriverHost.ProviderColumn(bfile, "F", typeof(byte[]), TiberoColumnTypes.PtBFile);
        const string selectBfile = "SELECT F FROM \"T\"";
        Serve(cmd => cmd.CommandText == Select ? rowid : bfile);
        var service = NewService();
        await using var session = NewSession();

        var r = await Assert.ThrowsAsync<NotSupportedException>(() =>
            Collect(service.StreamRowsAsync(session, Query(), NoBinds, ExecutionOptions.Default, CancellationToken.None)));
        var f = await Assert.ThrowsAsync<NotSupportedException>(() =>
            Collect(service.StreamRowsAsync(session, Query(selectBfile), NoBinds, ExecutionOptions.Default, CancellationToken.None)));

        Assert.Contains("RID", r.Message, StringComparison.Ordinal);
        Assert.Contains("ROWID", r.Message, StringComparison.Ordinal);
        Assert.Contains("F (", f.Message, StringComparison.Ordinal);
        Assert.False(session.CommandGate.IsHeld);
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task Without_a_schema_table_the_data_type_name_decides_and_an_unknown_one_is_not_supported()
    {
        var numbers = new DataTable();
        numbers.Columns.Add("N", typeof(decimal)); // 스키마 표 없음 → GetDataTypeName "Decimal" → NUMBER
        numbers.Rows.Add(12.5m);
        var texts = new DataTable();
        texts.Columns.Add("S", typeof(Guid)); // 모르는 이름 → 문자로 가정하지 않는다
        texts.Rows.Add(Guid.Empty);
        const string selectTexts = "SELECT S FROM \"T\"";
        Serve(cmd => cmd.CommandText == Select ? numbers : texts);
        var service = NewService();
        await using var session = NewSession();

        var rows = await Collect(service.StreamRowsAsync(session, Query(), NoBinds, ExecutionOptions.Default, CancellationToken.None));
        var unknown = await Assert.ThrowsAsync<NotSupportedException>(() =>
            Collect(service.StreamRowsAsync(session, Query(selectTexts), NoBinds, ExecutionOptions.Default, CancellationToken.None)));

        Assert.Equal("12.5", Assert.IsType<TiberoBulkNumber>(Assert.Single(rows)[0]).Text);
        Assert.Contains("S (Guid)", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_command_on_the_same_session_waits_until_the_enumeration_is_disposed()
    {
        var table = new DataTable();
        FakeDriverHost.ProviderColumn(table, "V", typeof(string), TiberoColumnTypes.PtVarchar2);
        table.Rows.Add("a");
        table.Rows.Add("b");
        Serve(cmd => cmd.CommandText == Select ? table : null);
        var service = NewService();
        await using var session = NewSession();

        var rows = service.StreamRowsAsync(session, Query(), NoBinds, ExecutionOptions.Default, CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await rows.MoveNextAsync());
        Assert.True(session.CommandGate.IsHeld);
        Assert.Equal(SessionState.Busy, session.State);

        var commit = service.ExecuteArrayAsync(session, new SqlStatement("COMMIT", default, StatementKind.TransactionControl), [], [],
            ExecutionOptions.Default, CancellationToken.None);
        await Task.Delay(200);
        Assert.False(commit.IsCompleted);

        await rows.DisposeAsync(); // 조기 중단(break)과 같다
        var committed = await commit;

        Assert.Equal(ExecutionStatus.Succeeded, committed.Status);
        Assert.Equal([Select, "COMMIT"], Texts().Take(2));
        Assert.False(session.CommandGate.IsHeld);
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task Cancelling_between_rows_or_during_execution_is_an_operation_cancelled_and_releases_the_gate()
    {
        var table = new DataTable();
        FakeDriverHost.ProviderColumn(table, "V", typeof(string), TiberoColumnTypes.PtVarchar2);
        table.Rows.Add("a");
        table.Rows.Add("b");
        const string slow = "SELECT * FROM \"SLOW\"";
        using var between = new CancellationTokenSource();
        using var during = new CancellationTokenSource();
        Serve(cmd =>
        {
            if (cmd.CommandText != slow)
            {
                return table;
            }

            during.Cancel(); // 실행 중 사용자가 취소 → 드라이버가 TBR-12040(실측 4장)
            throw FakeDriverHost.ServerError(-12040, "TBR-12040: Statement was canceled.");
        });
        var service = NewService();
        await using var session = NewSession();

        var seen = new List<object?[]>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var row in service.StreamRowsAsync(session, Query(), NoBinds, ExecutionOptions.Default, between.Token))
            {
                seen.Add(row);
                between.Cancel();
            }
        });
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Collect(service.StreamRowsAsync(session, Query(slow), NoBinds, ExecutionOptions.Default, during.Token)));

        Assert.Single(seen); // 둘째 행은 내지 않는다
        Assert.NotNull(cancelled.InnerException); // 드라이버 취소 오류를 감쌌다
        Assert.False(session.CommandGate.IsHeld);
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task A_driver_error_is_reported_to_the_session_and_rethrown_unchanged()
    {
        var broken = FakeDriverHost.ClientError(90403, "ODP-90403: connection broken");
        Serve(cmd => cmd.CommandText == Select ? throw broken : null);
        var service = NewService();
        await using var session = NewSession();

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() =>
            Collect(service.StreamRowsAsync(session, Query(), NoBinds, ExecutionOptions.Default, CancellationToken.None)));

        Assert.Same(broken, thrown);
        Assert.Equal(SessionState.Broken, session.State);
        Assert.False(session.CommandGate.IsHeld);
        await Assert.ThrowsAsync<InvalidOperationException>(() => // 끊긴 세션은 다시 읽지 않는다
            Collect(service.StreamRowsAsync(session, Query(), NoBinds, ExecutionOptions.Default, CancellationToken.None)));
    }

    [Fact]
    public async Task Key_binds_from_key_text_keep_number_digits_and_timestamp_nanoseconds_in_the_parameter_values()
    {
        var codec = TiberoBulkValueCodec.Instance;
        var stamp = new TiberoBulkTimestamp(new DateTime(2026, 1, 2, 3, 4, 5), 123_456_789);
        var stampKey = codec.FromKeyText(codec.ToKeyText(stamp, "TIMESTAMP(9)"), "TIMESTAMP(9)");
        var since = new DateTime(2026, 1, 1, 0, 0, 0);
        const string keyed = "SELECT * FROM \"T\" WHERE \"A\" = :K1 AND \"B\" = :K2 AND \"C\" = :K3 AND \"D\" >= :P1";
        var query = new SqlStatement(keyed, default, StatementKind.Query) { BindNames = ["K1", "K2", "K3", "P1"] };
        var binds = new Dictionary<string, BindValue>
        {
            ["K1"] = new(codec.BindTypeOf("NUMBER"), codec.FromKeyText("12345678901234567890123456789", "NUMBER")),
            ["K2"] = new(codec.BindTypeOf("NUMBER(10,2)"), codec.FromKeyText("-1.50", "NUMBER(10,2)")),
            ["K3"] = new(codec.BindTypeOf("TIMESTAMP(9)"), stampKey),
            ["P1"] = new(BindDataType.Date, since),
        };
        Serve(_ => new DataTable());
        var service = NewService();
        await using var session = NewSession();

        var rows = await Collect(service.StreamRowsAsync(session, query, binds, ExecutionOptions.Default, CancellationToken.None));

        Assert.Empty(rows);
        Assert.Equal(stamp, stampKey);
        var parameters = Assert.Single(_sent).Parameters;
        Assert.Equal(["K1", "K2", "K3", "P1"], parameters.Select(p => p.Name));
        Assert.Equal(["Decimal", "Decimal", "TimeStamp"], parameters.Take(3).Select(p => p.Kind));
        Assert.Equal("12345678901234567890123456789", Assert.IsType<string>(parameters[0].Value)); // 28자리 넘음 → 글자
        Assert.Equal(-1.5m, Assert.IsType<decimal>(parameters[1].Value));
        Assert.Equal(123_456_789, FakeDriverHost.Get(parameters[2].Value!, "Nanosecond"));
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), FakeDriverHost.Get(parameters[2].Value!, "Value"));
        Assert.Equal(since, parameters[3].Value); // 그 밖은 TiberoBindBuilder 그대로

        _sent.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => // 값이 없는 바인드 → 보내지 않는다
            Collect(service.StreamRowsAsync(session, query, new Dictionary<string, BindValue>(), ExecutionOptions.Default, CancellationToken.None)));
        Assert.Empty(_sent);
        Assert.False(session.CommandGate.IsHeld);
    }

    private static async Task<List<object?[]>> Collect(IAsyncEnumerable<object?[]> rows)
    {
        var list = new List<object?[]>();
        await foreach (var row in rows)
        {
            list.Add(row);
        }

        return list;
    }

    private static SqlStatement Query(string text = Select) => new(text, default, StatementKind.Query);

    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));

    private TiberoBulkExecutionService NewService(FakePolicy? policy = null, FakeInteraction? interaction = null) =>
        new(policy ?? new FakePolicy(PolicyDecision.Allowed), interaction ?? new FakeInteraction(confirm: true), _bus);

    private TiberoSession NewSession(bool readOnly = false, ConnectionEnvironment environment = ConnectionEnvironment.Development)
    {
        var driver = FakeDriverHost.Driver;
        var connection = driver.CreateConnection("Data Source=x");
        connection.Open();
        var profile = new ConnectionProfile("t", "u", "h:1/d", Dbms: Dbms.Tibero, ReadOnly: readOnly, Environment: environment);
        return new TiberoSession(Guid.NewGuid(), profile, Server, SessionPurpose.Work, "D/-/U", connection, null, _bus, driver: driver);
    }

    /// <summary>보낸 명령(글·매개변수)을 그 순간 그대로 기록하고 <paramref name="respond"/> 의 답을 돌려준다.</summary>
    private void Serve(Func<DbCommand, object?> respond) =>
        FakeDriverHost.SetHandler(cmd =>
        {
            var parameters = cmd.Parameters.Cast<DbParameter>()
                .Select(p => new SentParameter(p.ParameterName, FakeDriverHost.Get(p, "TiberoDbType")!.ToString()!, p.Value))
                .ToList();
            lock (_sent)
            {
                _sent.Add(new Sent(cmd.CommandText, parameters));
            }

            return respond(cmd);
        });

    private List<string> Texts()
    {
        lock (_sent)
        {
            return [.. _sent.Select(s => s.Text)];
        }
    }

    private sealed record Sent(string Text, IReadOnlyList<SentParameter> Parameters);

    private sealed record SentParameter(string Name, string Kind, object? Value);

    private sealed class FakePolicy(PolicyDecision decision) : IExecutionPolicy
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult(decision);
        }
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
