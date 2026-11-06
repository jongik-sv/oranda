using System.Globalization;
using System.Text;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.IntegrationTests.Execution;

// 소유: 데이터 복사 Task 6. 대량 실행 경로(정책 1회 + 바인드 배열 + LOB 끝까지 읽기).
// 이 경로가 안 되면 사전 점검·실행기(T8·T9) 설계를 다시 정하므로 win144 에서 가장 먼저 돌린다.

/// <summary>26ai 컨테이너: 지원 타입 왕복, 큰 LOB 바이트 동일, 바인드 배열 MERGE, 조회 아닌 읽기 거부, 읽기 전용 프로필 쓰기 거부.</summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class BulkExecutionTests : IAsyncLifetime
{
    private const int LargeClobChars = 100_000;
    private const int LargeBlobBytes = 200_000;

    /// <summary>설계 8.3 1차 지원 타입 열(LOB 제외: 집합 연산에 넣을 수 없어 따로 비교한다).</summary>
    private static readonly (string Name, string Type)[] ScalarColumns =
    [
        ("ID", "NUMBER"),
        ("V2", "VARCHAR2(50)"),
        ("CH", "CHAR(5)"),
        ("NV", "NVARCHAR2(50)"),
        ("NC", "NCHAR(5)"),
        ("NUM", "NUMBER(38,10)"),

        // 정밀도가 작은 NUMBER: ODP.NET 이 .NET Single·Double 로 알려도 OracleDecimal 로 읽어 값이 그대로여야 한다(MERGE ON·키 다시 읽기).
        ("N52", "NUMBER(5,2)"),
        ("N146", "NUMBER(14,6)"),
        ("N10", "NUMBER(10)"),
        ("BF", "BINARY_FLOAT"),
        ("BD", "BINARY_DOUBLE"),
        ("DT", "DATE"),
        ("TS", "TIMESTAMP(6)"),
        ("TSTZ", "TIMESTAMP(6) WITH TIME ZONE"),
        ("TSLTZ", "TIMESTAMP(6) WITH LOCAL TIME ZONE"),
        ("IDS", "INTERVAL DAY(2) TO SECOND(6)"),
        ("IYM", "INTERVAL YEAR(2) TO MONTH"),
        ("RW", "RAW(16)"),
    ];

    private static readonly (string Name, string Type)[] LobColumns = [("CL", "CLOB"), ("NCL", "NCLOB"), ("BL", "BLOB")];

    private static readonly ExecutionOptions CopyOptions = new() { Source = HistorySource.DataCopy, SkipHistory = true };

    private static readonly Dictionary<string, BindValue> NoBinds = [];

    private readonly OracleFreeFixture _db;
    private readonly ExecBus _bus = new();
    private readonly ExecInteraction _ui = new();
    private readonly string _tag = "BULK_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private OracleSessionManager _manager = null!;

    public BulkExecutionTests(OracleFreeFixture db) => _db = db;

    public Task InitializeAsync()
    {
        _manager = new OracleSessionManager(_bus);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _manager.Sessions)
        {
            await s.DisposeAsync();
        }

        await using var raw = await RawAsync();
        foreach (var suffix in new[] { "_SRC", "_DST" })
        {
            try
            {
                await ExecAsync(raw, "DROP TABLE " + _tag + suffix + " PURGE");
            }
            catch (OracleException)
            {
                // 만들지 않은 표는 무시.
            }
        }
    }

    private string Src => _tag + "_SRC";

    private string Dst => _tag + "_DST";

    private OracleBulkExecutionService Service() => new(new ExecutionPolicy(), _ui, _bus);

    private async Task<OracleSession> OpenAsync(bool readOnly = false)
    {
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource, ReadOnly: readOnly);
        using var cred = new SecureCredential(_db.SystemPassword);
        return (OracleSession)await _manager.OpenAsync(profile, cred, SessionPurpose.Dedicated, default);
    }

    private async Task<OracleConnection> RawAsync()
    {
        var conn = new OracleConnection(new ConnectionProfile("free-system", "system", _db.DataSource).BuildConnectionString(_db.SystemPassword));
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(OracleConnection c, string sql, params OracleParameter[] parameters)
    {
        await using var cmd = c.CreateCommand();
        cmd.BindByName = true;
        cmd.CommandText = sql;
        cmd.Parameters.AddRange(parameters);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<decimal> ScalarAsync(OracleConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToDecimal(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static SqlStatement Query(string text) => new(text, default, StatementKind.Query) { Verb = "SELECT" };

    private static SqlStatement Dml(string text, string verb, IReadOnlyList<string> binds) =>
        new(text, default, StatementKind.Dml) { Verb = verb, BindNames = binds };

    private static string Ddl((string Name, string Type)[] columns) =>
        string.Join(", ", columns.Select(c => c.Name + " " + c.Type));

    /// <summary>바인드 이름 C1..Cn, 서버 형식은 열 정의 그대로(<c>ColumnInfo.DataType</c> 처럼 정밀도 포함).</summary>
    private static List<BulkColumn> Bulk((string Name, string Type)[] columns) =>
        [.. columns.Select((c, i) => new BulkColumn("C" + (i + 1).ToString(CultureInfo.InvariantCulture), c.Type))];

    private static string Insert(string table, (string Name, string Type)[] columns) =>
        $"INSERT INTO {table} ({string.Join(", ", columns.Select(c => c.Name))}) VALUES ({string.Join(", ", columns.Select((_, i) => ":C" + (i + 1).ToString(CultureInfo.InvariantCulture)))})";

    private static async Task<List<object?[]>> ReadAllAsync(OracleBulkExecutionService bulk, ISession s, string sql)
    {
        var rows = new List<object?[]>();
        await foreach (var row in bulk.StreamRowsAsync(s, Query(sql), NoBinds, CopyOptions, CancellationToken.None))
        {
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>한글(3바이트)과 ASCII 를 섞어 문자 수와 바이트 수가 다르게 한다.</summary>
    private static string LargeText(int chars, char seed)
    {
        var sb = new StringBuilder(chars);
        const string pattern = "가나다라마bcdef0123456789";
        while (sb.Length < chars)
        {
            sb.Append(seed).Append(pattern);
        }

        return sb.ToString(0, chars);
    }

    private static byte[] LargeBytes(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31 + 7) % 251);
        }

        return bytes;
    }

    [Fact]
    public async Task Array_insert_round_trips_all_supported_types()
    {
        await using var raw = await RawAsync();
        var all = ScalarColumns.Concat(LobColumns).ToArray();
        await ExecAsync(raw, $"CREATE TABLE {Src} ({Ddl(all)})");
        await ExecAsync(raw, $"CREATE TABLE {Dst} ({Ddl(all)})");
        await ExecAsync(raw, $"""
            INSERT INTO {Src} VALUES (1, 'abc', 'xy', N'한글', N'가', 12345678901234567890.0123456789, 123.45, 1.997748, 1234567890, 1.5, 2.25,
              DATE '2026-03-05', TIMESTAMP '2026-03-05 14:30:15.123456', TIMESTAMP '2026-03-05 14:30:15.123456 +09:00',
              TIMESTAMP '2026-03-05 14:30:15.123456', INTERVAL '3 04:05:06.789012' DAY TO SECOND, INTERVAL '2-11' YEAR TO MONTH,
              HEXTORAW('00FF10'), 'clob 값', N'nclob 한글', HEXTORAW('DEADBEEF'))
            """);
        await ExecAsync(raw, $"""
            INSERT INTO {Src} VALUES (2, 'O''Neil', 'z', N'x', N'y', -0.0000000001, -999.99, -12345678.123456, -9999999999, -3.4E38, 1.25E100,
              DATE '1999-12-31', TIMESTAMP '1999-12-31 23:59:59.999999', TIMESTAMP '1999-12-31 23:59:59.999999 -05:00',
              TIMESTAMP '1999-12-31 23:59:59.999999', INTERVAL '-1 00:00:00.000001' DAY TO SECOND, INTERVAL '-1-0' YEAR TO MONTH,
              HEXTORAW('01'), 'x', N'y', HEXTORAW('00'))
            """);
        await ExecAsync(raw, $"INSERT INTO {Src} (ID) VALUES (3)"); // 나머지 열 NULL
        await ExecAsync(raw, "COMMIT");

        await using var s = await OpenAsync();
        var bulk = Service();
        var names = string.Join(", ", all.Select(c => c.Name));
        var rows = await ReadAllAsync(bulk, s, $"SELECT {names} FROM {Src} ORDER BY ID");
        Assert.Equal(3, rows.Count);
        var n146 = Array.FindIndex(all, c => c.Name == "N146");
        Assert.Equal("1.997748", Assert.IsType<OracleDecimal>(rows[0][n146]).ToString()); // 1.9977479999999999 로 바뀌지 않았다
        Assert.IsType<OracleDecimal>(rows[0][n146 - 1]);
        Assert.IsType<OracleDecimal>(rows[0][n146 + 1]);

        var result = await bulk.ExecuteArrayAsync(s, Dml(Insert(Dst, all), "INSERT", [.. Bulk(all).Select(b => b.BindName)]), Bulk(all), rows,
            CopyOptions, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(3, result.RowsAffected);
        await s.CommitAsync(CancellationToken.None);

        var scalars = string.Join(", ", ScalarColumns.Select(c => c.Name));
        Assert.Equal(0m, await ScalarAsync(raw, $"SELECT COUNT(*) FROM (SELECT {scalars} FROM {Src} MINUS SELECT {scalars} FROM {Dst})"));
        Assert.Equal(0m, await ScalarAsync(raw, $"SELECT COUNT(*) FROM (SELECT {scalars} FROM {Dst} MINUS SELECT {scalars} FROM {Src})"));
        Assert.Equal(0m, await ScalarAsync(raw, $"""
            SELECT COUNT(*) FROM {Src} s JOIN {Dst} d ON d.ID = s.ID
             WHERE NVL(DBMS_LOB.COMPARE(s.CL, d.CL), -1) <> 0 AND NOT (s.CL IS NULL AND d.CL IS NULL)
                OR NVL(DBMS_LOB.COMPARE(s.NCL, d.NCL), -1) <> 0 AND NOT (s.NCL IS NULL AND d.NCL IS NULL)
                OR NVL(DBMS_LOB.COMPARE(s.BL, d.BL), -1) <> 0 AND NOT (s.BL IS NULL AND d.BL IS NULL)
            """));
        Assert.Equal(3m, await ScalarAsync(raw, $"SELECT COUNT(*) FROM {Dst}"));
    }

    [Fact]
    public async Task Array_insert_round_trips_large_lobs_byte_identical()
    {
        await using var raw = await RawAsync();
        (string Name, string Type)[] columns = [("ID", "NUMBER"), ("CL", "CLOB"), ("BL", "BLOB")];
        await ExecAsync(raw, $"CREATE TABLE {Src} ({Ddl(columns)})");
        await ExecAsync(raw, $"CREATE TABLE {Dst} ({Ddl(columns)})");
        for (var id = 1; id <= 2; id++)
        {
            await ExecAsync(raw, $"INSERT INTO {Src} VALUES (:id, :cl, :bl)",
                new OracleParameter("id", OracleDbType.Decimal) { Value = id },
                new OracleParameter("cl", OracleDbType.Clob) { Value = LargeText(LargeClobChars, (char)('A' + id)) },
                new OracleParameter("bl", OracleDbType.Blob) { Value = LargeBytes(LargeBlobBytes + id) });
        }

        await ExecAsync(raw, "COMMIT");

        await using var s = await OpenAsync();
        var bulk = Service();
        var rows = await ReadAllAsync(bulk, s, $"SELECT ID, CL, BL FROM {Src} ORDER BY ID");
        Assert.Equal(LargeClobChars, Assert.IsType<string>(rows[0][1]).Length); // 미리보기(4,000자)로 잘리지 않았다
        Assert.Equal(LargeBlobBytes + 1, Assert.IsType<byte[]>(rows[0][2]).Length);

        var result = await bulk.ExecuteArrayAsync(s, Dml(Insert(Dst, columns), "INSERT", ["C1", "C2", "C3"]), Bulk(columns), rows,
            CopyOptions, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        await s.CommitAsync(CancellationToken.None);

        Assert.Equal(0m, await ScalarAsync(raw, $"""
            SELECT COUNT(*) FROM {Src} s JOIN {Dst} d ON d.ID = s.ID
             WHERE NVL(DBMS_LOB.COMPARE(s.CL, d.CL), -1) <> 0 OR NVL(DBMS_LOB.COMPARE(s.BL, d.BL), -1) <> 0
                OR NVL(DBMS_LOB.GETLENGTH(d.CL), -1) <> {LargeClobChars}
                OR NVL(DBMS_LOB.GETLENGTH(d.BL), -1) <> {LargeBlobBytes} + s.ID
            """));
        Assert.Equal(2m, await ScalarAsync(raw, $"SELECT COUNT(*) FROM {Dst}"));
    }

    [Fact]
    public async Task Array_merge_updates_existing_and_inserts_new()
    {
        await using var raw = await RawAsync();
        await ExecAsync(raw, $"CREATE TABLE {Dst} (ID NUMBER PRIMARY KEY, NAME VARCHAR2(30), CL CLOB)");
        await ExecAsync(raw, $"INSERT INTO {Dst} VALUES (1, 'old', 'old clob')");
        await ExecAsync(raw, "COMMIT");

        var first = LargeText(LargeClobChars, 'M');
        var second = LargeText(LargeClobChars, 'N');
        (string Name, string Type)[] columns = [("ID", "NUMBER"), ("NAME", "VARCHAR2"), ("CL", "CLOB")];
        var merge = Dml($"""
            MERGE INTO {Dst} d
            USING (SELECT :C1 AS ID, :C2 AS NAME, :C3 AS CL FROM DUAL) s
               ON (d.ID = s.ID)
             WHEN MATCHED THEN UPDATE SET d.NAME = s.NAME, d.CL = s.CL
             WHEN NOT MATCHED THEN INSERT (ID, NAME, CL) VALUES (s.ID, s.NAME, s.CL)
            """, "MERGE", ["C1", "C2", "C3"]);

        await using var s = await OpenAsync();
        var result = await Service().ExecuteArrayAsync(s, merge, Bulk(columns), [[1m, "new", first], [2m, "added", second]],
            CopyOptions, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(2, result.RowsAffected);
        await s.CommitAsync(CancellationToken.None);

        Assert.Equal(2m, await ScalarAsync(raw, $"SELECT COUNT(*) FROM {Dst}"));
        Assert.Equal(1m, await ScalarAsync(raw, $"SELECT COUNT(*) FROM {Dst} WHERE ID = 1 AND NAME = 'new'"));
        Assert.Equal(1m, await ScalarAsync(raw, $"SELECT COUNT(*) FROM {Dst} WHERE ID = 2 AND NAME = 'added'"));
        Assert.Equal(2m, await ScalarAsync(raw, $"SELECT COUNT(*) FROM {Dst} WHERE DBMS_LOB.GETLENGTH(CL) = {LargeClobChars}"));

        await using var cmd = raw.CreateCommand();
        cmd.CommandText = $"SELECT CL FROM {Dst} ORDER BY ID";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(first, reader.GetString(0));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(second, reader.GetString(0));
    }

    /// <summary>계획 밖 추가: 빈 LOB 이 NULL 로 바뀌지 않는지(바뀌면 NOT NULL DEFAULT EMPTY_CLOB() 열에서 ORA-01400). 미확인 동작이라 따로 둔다.</summary>
    [Fact]
    public async Task Array_insert_keeps_empty_lobs_distinct_from_null()
    {
        await using var raw = await RawAsync();
        (string Name, string Type)[] columns = [("ID", "NUMBER"), ("CL", "CLOB"), ("BL", "BLOB")];
        await ExecAsync(raw, $"CREATE TABLE {Src} ({Ddl(columns)})");
        await ExecAsync(raw, $"CREATE TABLE {Dst} (ID NUMBER, CL CLOB DEFAULT EMPTY_CLOB() NOT NULL, BL BLOB DEFAULT EMPTY_BLOB() NOT NULL)");
        await ExecAsync(raw, $"INSERT INTO {Src} VALUES (1, EMPTY_CLOB(), EMPTY_BLOB())");
        await ExecAsync(raw, "COMMIT");

        await using var s = await OpenAsync();
        var bulk = Service();
        var rows = await ReadAllAsync(bulk, s, $"SELECT ID, CL, BL FROM {Src}");
        var result = await bulk.ExecuteArrayAsync(s, Dml(Insert(Dst, columns), "INSERT", ["C1", "C2", "C3"]), Bulk(columns), rows,
            CopyOptions, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        await s.CommitAsync(CancellationToken.None);
        Assert.Equal(1m, await ScalarAsync(raw, $"SELECT COUNT(*) FROM {Dst} WHERE DBMS_LOB.GETLENGTH(CL) = 0 AND DBMS_LOB.GETLENGTH(BL) = 0"));
    }

    /// <summary>계획 밖 추가: 열거를 중간에 빠져나오면 세션 잠금이 풀려 같은 세션을 바로 다시 쓸 수 있다.</summary>
    [Fact]
    public async Task Breaking_out_of_stream_releases_the_session()
    {
        await using var raw = await RawAsync();
        await ExecAsync(raw, $"CREATE TABLE {Src} (ID NUMBER)");
        await ExecAsync(raw, $"INSERT INTO {Src} SELECT LEVEL FROM DUAL CONNECT BY LEVEL <= 5000");
        await ExecAsync(raw, $"CREATE TABLE {Dst} (ID NUMBER)");
        await ExecAsync(raw, "COMMIT");

        await using var s = await OpenAsync();
        var bulk = Service();
        await foreach (var _ in bulk.StreamRowsAsync(s, Query($"SELECT ID FROM {Src}"), NoBinds, CopyOptions, CancellationToken.None))
        {
            break;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await bulk.ExecuteArrayAsync(s, Dml($"INSERT INTO {Dst} (ID) VALUES (:C1)", "INSERT", ["C1"]), [new BulkColumn("C1", "NUMBER")],
            [[1m]], CopyOptions, timeout.Token);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(SessionState.Idle, s.State);
    }

    [Fact]
    public async Task Stream_rows_rejects_non_query()
    {
        await using var raw = await RawAsync();
        await ExecAsync(raw, $"CREATE TABLE {Src} (ID NUMBER)");
        await ExecAsync(raw, $"INSERT INTO {Src} VALUES (1)");
        await ExecAsync(raw, "COMMIT");

        await using var s = await OpenAsync();
        var delete = new SqlStatement($"DELETE FROM {Src}", default, StatementKind.Dml) { Verb = "DELETE", IsUnfilteredDml = true };
        await Assert.ThrowsAsync<BulkDeniedException>(async () =>
        {
            await foreach (var _ in Service().StreamRowsAsync(s, delete, NoBinds, CopyOptions, CancellationToken.None))
            {
            }
        });

        Assert.Empty(_ui.Confirms);
        Assert.Equal(1m, await ScalarAsync(raw, $"SELECT COUNT(*) FROM {Src}"));
    }

    [Fact]
    public async Task Readonly_source_profile_denies_writes()
    {
        await using var raw = await RawAsync();
        await ExecAsync(raw, $"CREATE TABLE {Src} (ID NUMBER, NAME VARCHAR2(10))");

        await using var s = await OpenAsync(readOnly: true);
        (string Name, string Type)[] columns = [("ID", "NUMBER"), ("NAME", "VARCHAR2")];
        var result = await Service().ExecuteArrayAsync(s, Dml(Insert(Src, columns), "INSERT", ["C1", "C2"]), Bulk(columns),
            [[1m, "a"], [2m, "b"]], CopyOptions, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Denied, result.Status);
        Assert.Equal(PolicyRule.ReadOnlyProfile, result.Policy!.Rule);
        Assert.Equal(0m, await ScalarAsync(raw, $"SELECT COUNT(*) FROM {Src}"));
    }
}
