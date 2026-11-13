using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Capture;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.IntegrationTests.Metadata;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Rollback;
using Oranda.Oracle.Sessions;
using Oranda.Testing;
using Oranda.Testing.Rollback;
using Xunit.Abstractions;

namespace Oranda.Oracle.IntegrationTests.Rollback;

// 소유: rb-m2-oracle RB-12. 이 PC 에서는 컴파일까지만 확인한다. 실행은 CI 의 Oracle 컨테이너에서 한다.

/// <summary>
/// 운영 접속의 원복 캡처를 실제 서버에서(설계 6.2·6.3·16.2): 캡처 엔진(RB-02)·Oracle 통로·방언·실행 서비스 연결을 한 줄로 돌린다.
/// 원장은 부른 것을 기록하는 가짜(RB-04 머지 전)이고, 문장 모양은 만든 쪽이 준 것(<see cref="SqlStatement.Dml"/>, RB-30 머지 전)이다.
/// <list type="bullet">
/// <item>UPDATE·DELETE 끝까지: 실행 → 커밋 → 다른 세션이 한 행을 바꿈 → 원복 문장 실행 → 바꾸지 않은 행만 돌아오고 바꾼 행은 0행.</item>
/// <item>사용자 문장이 실패하면 내부 저장점으로 되돌려 잠금 읽기의 잠금이 풀린다(다른 세션 NOWAIT 성공, RD-5).</item>
/// <item>한 행 INSERT 는 <c>RETURNING ROWID INTO :ORANDA_RID</c> 출력 바인드로 넣은 행을 찾고, 사용자 결과의 OUT 값에는 그 바인드가 없다(RD-1).</item>
/// <item>VERSIONS_XID 와 <c>LOCAL_TRANSACTION_ID</c>(usn.slot.sqn) 의 바이트 순서(README 「확신이 낮은 지점」) 측정.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class RollbackCaptureIt(OracleFreeFixture db, ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly Dictionary<string, BindValue> NoBinds = [];
    private readonly string _table = "RBIT_CAP_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly FakeEventBus _bus = new();
    private readonly RecordingRollbackLedger _ledger = new();
    private readonly RecordingRollbackInteraction _ui = new();
    private OracleSessionManager _manager = null!;
    private OracleExecutionService _exec = null!;
    private OracleSession _session = null!;

    public async Task InitializeAsync()
    {
        var metadata = new OracleMetadataProvider(new MemoryMetadataCache(), _bus, Timeout.InfiniteTimeSpan);
        var engine = new RollbackCaptureEngine(_ledger, () => new RollbackSettings(), metadata, new InMemoryRollbackStore());
        _manager = new OracleSessionManager(_bus, engine, _ledger);
        _exec = new OracleExecutionService(new ExecutionPolicy(), _ui, _bus, null, pageSize: 500);
        _session = await OpenAsync(ConnectionEnvironment.Production);
        await ExecAsync(_session, $"CREATE TABLE {_table} (ID NUMBER PRIMARY KEY, NAME VARCHAR2(40), PRICE NUMBER(10,2) CHECK (PRICE >= 0), MEMO CLOB)");
        await ExecAsync(_session, $"INSERT INTO {_table} SELECT LEVEL, 'item ' || LEVEL, LEVEL * 100, NULL FROM DUAL CONNECT BY LEVEL <= 5");
        await ExecAsync(_session, "COMMIT");
    }

    public async Task DisposeAsync()
    {
        try
        {
            await ExecAsync(_session, "ROLLBACK");
            await ExecAsync(_session, $"DROP TABLE {_table} PURGE");
        }
        finally
        {
            foreach (var s in _manager.Sessions)
            {
                await s.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Update_reverses_only_rows_nobody_changed_after_commit()
    {
        var update = Write($"UPDATE {_table} SET PRICE = PRICE * 1.1 WHERE ID <= 3", DmlVerb.Update, set: ["PRICE"], where: "ID <= 3");

        var result = await RunAsync(update);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(3, result.RowsAffected);
        output.WriteLine(result.Rollback?.LogLine);
        var record = Assert.Single(_ledger.Records);
        Assert.Equal(3, record.LockedRows);
        Assert.Equal(3, record.Steps.Count);
        Assert.NotNull(record.BeforePoint);
        Assert.False(string.IsNullOrEmpty(record.TransactionId));
        await _session.CommitAsync(CancellationToken.None);

        await using var other = await OpenAsync(ConnectionEnvironment.Development);
        await ExecAsync(other, $"UPDATE {_table} SET PRICE = 999 WHERE ID = 2");
        await ExecAsync(other, "COMMIT");

        var restored = new List<int>();
        foreach (var step in record.Steps)
        {
            output.WriteLine(step.Sql);
            restored.Add(await ExecAsync(_session, step.Sql));
        }

        Assert.Equal([0, 1, 1], restored.Order().ToList()); // 다른 사람이 바꾼 행 하나만 0행
        Assert.Equal(100m, await PriceAsync(1));
        Assert.Equal(999m, await PriceAsync(2)); // 덮어쓰지 않았다
        Assert.Equal(300m, await PriceAsync(3));
    }

    [Fact]
    public async Task Delete_reinserts_the_deleted_rows()
    {
        var delete = Write($"DELETE FROM {_table} WHERE ID IN (4, 5)", DmlVerb.Delete, where: "ID IN (4, 5)");

        var result = await RunAsync(delete);
        Assert.Equal(2, result.RowsAffected);
        var record = Assert.Single(_ledger.Records);
        Assert.Equal(2, record.Steps.Count);
        await _session.CommitAsync(CancellationToken.None);

        foreach (var step in record.Steps)
        {
            Assert.Equal(1, await ExecAsync(_session, step.Sql));
        }

        Assert.Equal(400m, await PriceAsync(4));
        Assert.Equal(500m, await PriceAsync(5));
    }

    [Fact]
    public async Task Failed_statement_releases_the_capture_locks()
    {
        var failing = Write($"UPDATE {_table} SET PRICE = -1 WHERE ID = 1", DmlVerb.Update, set: ["PRICE"], where: "ID = 1"); // CHECK 위반

        var result = await RunAsync(failing);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Empty(_ledger.Records);
        await using var other = await OpenAsync(ConnectionEnvironment.Development);
        // 잠금 읽기가 잡은 행 잠금이 남았다면 ORA-00054 로 실패한다.
        Assert.Equal(1, await LockNowaitAsync(other, "ID = 1"));
        await ExecAsync(other, "ROLLBACK");
    }

    [Fact]
    public async Task Single_row_insert_uses_returning_rowid_and_hides_the_bind()
    {
        var insert = Write($"INSERT INTO {_table} (ID, NAME, PRICE) VALUES (2001, 'tea', 6200)", DmlVerb.InsertValues,
            insert: ["ID", "NAME", "PRICE"]);

        var result = await RunAsync(insert);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.True(result.Rollback!.ReturningAppended);
        Assert.False(result.OutValues.ContainsKey(RollbackCaptureEngine.ReturningBind));
        Assert.Equal($"INSERT INTO {_table} (ID, NAME, PRICE) VALUES (2001, 'tea', 6200)", result.Statement.Text);
        var record = Assert.Single(_ledger.Records);
        var step = Assert.Single(record.Steps);
        output.WriteLine(step.Sql);
        Assert.StartsWith("DELETE", step.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await ExecAsync(_session, step.Sql));
        await ExecAsync(_session, "ROLLBACK");
    }

    /// <summary>
    /// 측정: 같은 트랜잭션의 <c>LOCAL_TRANSACTION_ID</c>(usn.slot.sqn, 10진)와 <c>VERSIONS_XID</c>(RAW 8바이트) 사이의 바이트 순서.
    /// 기대 규칙(후보): usn 2바이트·slot 2바이트·sqn 4바이트를 각각 little-endian 으로 이어 붙인다. 결과를 출력해 RB-31(화면 3) 이 쓰도록 보고한다.
    /// </summary>
    [Fact]
    public async Task Versions_xid_matches_the_local_transaction_id_bytes()
    {
        await Task.Delay(TimeSpan.FromSeconds(8)); // 표를 만든 직후 SCN 으로는 VERSIONS 를 읽지 못한다(ORA-01466, 대응 간격 몇 초)
        var before = await ScalarAsync(_session, "SELECT DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER FROM DUAL");
        // 실행 서비스로 보낸다: 연결의 자동 커밋을 끄므로(드라이버 기본은 문장마다 커밋) 트랜잭션이 열린 채 id 를 읽을 수 있다.
        var update = await _exec.ExecuteAsync(_session, new SqlStatement($"UPDATE {_table} SET NAME = 'xid' WHERE ID = 3", default, StatementKind.Dml)
            { Verb = "UPDATE" }, NoBinds, new ExecutionOptions { Recording = ChangeRecording.None }, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Succeeded, update.Status);
        var local = await ScalarAsync(_session, "SELECT DBMS_TRANSACTION.LOCAL_TRANSACTION_ID FROM DUAL");
        await ExecAsync(_session, "COMMIT");
        Assert.False(string.IsNullOrEmpty(local), "커밋 전 LOCAL_TRANSACTION_ID 가 비었다");

        var versions = await RowsAsync(_session,
            $"SELECT RAWTOHEX(VERSIONS_XID), VERSIONS_OPERATION FROM {_table} VERSIONS BETWEEN SCN {before} AND MAXVALUE WHERE ID = 3");
        output.WriteLine($"LOCAL_TRANSACTION_ID {local} → VERSIONS {string.Join(", ", versions)} · 후보(LE) {LittleEndianXid(local!)}");
        WriteSummary($"[RB-12] xid: LOCAL_TRANSACTION_ID {local} → VERSIONS {string.Join(", ", versions)} · little-endian 후보 {LittleEndianXid(local!)}");
        var xid = versions.Where(v => v.EndsWith(" U", StringComparison.Ordinal)).Select(v => v[..^2]).Single();
        Assert.Equal(xid, LittleEndianXid(local!));
    }

    /// <summary>
    /// CI 에서 한 줄 덧붙인다: 작업 요약(<c>GITHUB_STEP_SUMMARY</c>)과 <c>$RUNNER_TEMP/oranda-rb90.txt</c>(다음 단계가 로그로 출력해 API 로 읽게).
    /// 통과한 시험의 출력은 로그에 나오지 않는다. 둘 다 없으면(로컬) 아무것도 하지 않는다.
    /// </summary>
    internal static void WriteSummary(string line)
    {
        Append(Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY"), line);
        if (Environment.GetEnvironmentVariable("RUNNER_TEMP") is { Length: > 0 } temp)
        {
            Append(Path.Combine(temp, "oranda-rb90.txt"), line);
        }
    }

    private static void Append(string? file, string line)
    {
        if (string.IsNullOrEmpty(file))
        {
            return;
        }

        try
        {
            File.AppendAllText(file, line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 수치 남기기는 덤이다.
        }
    }

    private static async Task<List<string>> RowsAsync(OracleSession session, string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync();
            var rows = new List<string>();
            while (await reader.ReadAsync())
            {
                rows.Add((reader.IsDBNull(0) ? "-" : reader.GetString(0)) + " " + (reader.IsDBNull(1) ? "-" : reader.GetString(1)));
            }

            return rows;
        }
        finally
        {
            session.CommandGate.Release();
        }
    }

    /// <summary>후보 규칙: usn·slot 은 2바이트, sqn 은 4바이트, 각각 little-endian.</summary>
    private static string LittleEndianXid(string local)
    {
        var parts = local.Split('.').Select(p => uint.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes((ushort)parts[0]).Take(2));
        bytes.AddRange(BitConverter.GetBytes((ushort)parts[1]).Take(2));
        bytes.AddRange(BitConverter.GetBytes(parts[2]).Take(4));
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException();
        }

        return Convert.ToHexString(bytes.ToArray());
    }

    private SqlStatement Write(string text, DmlVerb verb, string[]? set = null, string? where = null, string[]? insert = null) =>
        new(text, default, StatementKind.Dml)
        {
            Verb = text.Split(' ')[0],
            Dml = new DmlShape(verb)
            {
                Owner = "SYSTEM",
                Name = _table,
                TableText = _table,
                WhereText = where,
                SetColumns = set ?? [],
                InsertColumns = insert ?? [],
            },
        };

    private Task<ExecutionResult> RunAsync(SqlStatement stmt) =>
        _exec.ExecuteAsync(_session, stmt, NoBinds, null, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2));

    private async Task<OracleSession> OpenAsync(ConnectionEnvironment environment)
    {
        var profile = new ConnectionProfile(environment == ConnectionEnvironment.Production ? "prod-example" : "dev-example", "system",
            db.DataSource, Environment: environment);
        using var cred = new SecureCredential(db.SystemPassword);
        return (OracleSession)await _manager.OpenAsync(profile, cred, SessionPurpose.Work, default);
    }

    private async Task<decimal> PriceAsync(int id) =>
        decimal.Parse((await ScalarAsync(_session, $"SELECT TO_CHAR(PRICE, 'TM9') FROM {_table} WHERE ID = {id}"))!, CultureInfo.InvariantCulture);

    private static async Task<string?> ScalarAsync(OracleSession session, string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.CommandText = sql;
            var value = await cmd.ExecuteScalarAsync();
            return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        finally
        {
            session.CommandGate.Release();
        }
    }

    /// <summary>다른 세션에서 <c>SELECT … FOR UPDATE NOWAIT</c>: 잠긴 행이 있으면 ORA-00054 로 실패한다. 잠근 행 수.</summary>
    private async Task<int> LockNowaitAsync(OracleSession session, string where)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.CommandText = $"SELECT ID FROM {_table} WHERE {where} FOR UPDATE NOWAIT";
            await using var reader = await cmd.ExecuteReaderAsync();
            var n = 0;
            while (await reader.ReadAsync())
            {
                n++;
            }

            return n;
        }
        finally
        {
            session.CommandGate.Release();
        }
    }

    private static async Task<int> ExecAsync(OracleSession session, string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.CommandText = sql;
            return await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            session.CommandGate.Release();
        }
    }
}
