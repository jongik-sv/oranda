using System.Globalization;
using System.Text;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Sessions;
using Xunit.Abstractions;

namespace Oranda.Oracle.IntegrationTests.Execution;

// 소유: 데이터 복사 PF5(LOB 쓰기 바인드). 이 PC 에서는 컴파일까지만 확인한다. 실행은 CI·다른 PC 의 컨테이너에서 한다.

/// <summary>
/// 배열 INSERT 의 LOB 값을 LONG·LONG RAW 로 바인드하는 길(PF5, <see cref="OracleLobWrite.LongBind"/>)이 옛 길(<see cref="OracleLobWrite.Locator"/>)과 같은 값을 넣는지:
/// 크기 0·1·4000·4001·32767·32768·65537(글자·바이트), NULL 과 빈 값(EMPTY_CLOB·EMPTY_BLOB)의 구별, 한글·이모지, NCLOB. 원본을 대량 읽기로 읽어 두 길로 각각 넣고
/// 원본과 길이·<c>DBMS_LOB.COMPARE</c> 로 비교한다. LONG 길은 거부되어 옛 길로 다시 보내지 않았어야 한다(되돌림 0).
/// </summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class BulkLobWriteTests : IAsyncLifetime
{
    private static readonly int[] Sizes = [0, 1, 4000, 4001, 32767, 32768, 65537];

    private static readonly ExecutionOptions CopyOptions = new() { Source = HistorySource.DataCopy, SkipHistory = true };

    private static readonly Dictionary<string, BindValue> NoBinds = [];

    private readonly OracleFreeFixture _db;
    private readonly ITestOutputHelper _output;
    private readonly ExecBus _bus = new();
    private readonly ExecInteraction _ui = new();
    private readonly string _tag = "LOBW_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private OracleSessionManager _manager = null!;

    public BulkLobWriteTests(OracleFreeFixture db, ITestOutputHelper output)
    {
        _db = db;
        _output = output;
    }

    private string Src => _tag + "_SRC";

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
        foreach (var suffix in new[] { "_SRC", "_LONG", "_LOB" })
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

    [Fact]
    public async Task Long_bind_insert_puts_the_same_lob_values_as_the_lob_bind_for_every_size_null_and_empty()
    {
        const string Columns = "ID NUMBER PRIMARY KEY, CL CLOB, NCL NCLOB, BL BLOB";
        await using var raw = await RawAsync();
        await CreateAsync(raw, Columns);
        var id = 0;
        foreach (var size in Sizes)
        {
            // 크기마다 한 행 + 같은 크기의 CLOB 과 짧은 값이 섞인 행 하나(구간이 길·옛 길로 번갈아 나뉜다).
            await InsertSourceAsync(raw, ++id, Text(size, 'a'), KoreanText(size), Bytes(size, id));
            await InsertSourceAsync(raw, ++id, "s" + id.ToString(CultureInfo.InvariantCulture), "짧은값", Bytes(3, id));
        }

        await InsertSourceAsync(raw, ++id, null, null, null);                                   // 모두 NULL
        await InsertSourceAsync(raw, ++id, string.Empty, null, []);                             // 빈 CLOB · NULL NCLOB · 빈 BLOB
        await InsertSourceAsync(raw, ++id, null, string.Empty, null);                           // 빈 NCLOB
        await InsertSourceAsync(raw, ++id, Emoji(5000), Emoji(5000), Bytes(5000, id));          // 한글·이모지(서로게이트 짝)
        await InsertSourceAsync(raw, ++id, Emoji(40000), Emoji(40000), Bytes(40000, id));       // 상한(32767) 넘는 글자 값
        for (var i = 0; i < 50; i++)
        {
            await InsertSourceAsync(raw, ++id, "row " + i.ToString(CultureInfo.InvariantCulture), "행 " + i.ToString(CultureInfo.InvariantCulture), Bytes(i + 1, i));
        }

        await ExecAsync(raw, "COMMIT");

        var (longStats, lobStats) = await CopyBothWaysAsync(raw, "ID, CL, NCL, BL", [("C1", "NUMBER"), ("C2", "CLOB"), ("C3", "NCLOB"), ("C4", "BLOB")]);

        _output.WriteLine("PF5 fidelity long: " + longStats + " · lob: " + lobStats);
        Assert.True(longStats.LongRows > 0, longStats.ToString());
        Assert.Equal(0, longStats.RefusedRuns); // 이 모양(LOB 뒤에 다른 바인드 없음)은 거부되면 안 된다
        Assert.Equal(0, longStats.Fallbacks);
        Assert.Equal(0, lobStats.LongRows);
        Assert.Empty(await MismatchesAsync(raw, _tag + "_LONG", ["CL", "NCL", "BL"]));
        Assert.Empty(await MismatchesAsync(raw, _tag + "_LOB", ["CL", "NCL", "BL"]));
        Assert.Equal(await NullsAndEmptiesAsync(raw, Src), await NullsAndEmptiesAsync(raw, _tag + "_LONG"));
    }

    /// <summary>
    /// LOB 열 뒤에 긴 VARCHAR2 바인드가 오는 모양(ORA-24816 「LONG·LOB 바인드 뒤의 늘어난 LONG 아닌 바인드」가 날 수 있는 순서). 값은 어느 경우든 같아야 한다.
    /// 거부가 없으면 모든 행이 LONG 으로 갔다. 거부되면 여러 행 구간은 다시 보내지 않고 실패했고(한 번), 실행기처럼 되돌리고 한 행씩 넣으면 첫 거부 행 하나만
    /// 옛 길로 제자리에서 다시 보내 거부를 확정·기억하고(한 번) 나머지는 묻지 않고 옛 길로 간다 — 어느 쪽인지는 <c>PF5 probe</c> 줄로 남긴다.
    /// </summary>
    [Fact]
    public async Task A_long_varchar2_after_the_lob_columns_copies_the_same_values_by_long_bind_or_by_a_confirmed_and_remembered_fallback()
    {
        const string Columns = "ID NUMBER PRIMARY KEY, CL CLOB, BL BLOB, V VARCHAR2(4000), NV NVARCHAR2(2000)";
        await using var raw = await RawAsync();
        await CreateAsync(raw, Columns);
        for (var i = 1; i <= 20; i++)
        {
            await using var cmd = raw.CreateCommand();
            cmd.BindByName = true;
            cmd.CommandText = $"INSERT INTO {Src} (ID, CL, BL, V, NV) VALUES (:id, :cl, :bl, :v, :nv)";
            cmd.Parameters.Add(new OracleParameter("id", OracleDbType.Decimal) { Value = i });
            cmd.Parameters.Add(new OracleParameter("cl", OracleDbType.Clob) { Value = Text(i * 1500, 'c') });
            cmd.Parameters.Add(new OracleParameter("bl", OracleDbType.Blob) { Value = Bytes(i * 1500, i) });
            cmd.Parameters.Add(new OracleParameter("v", OracleDbType.Varchar2) { Value = i % 2 == 0 ? Text(4000, 'v') : KoreanText(1333) });
            cmd.Parameters.Add(new OracleParameter("nv", OracleDbType.NVarchar2) { Value = KoreanText(2000) });
            await cmd.ExecuteNonQueryAsync();
        }

        await ExecAsync(raw, "COMMIT");

        var (longStats, _) = await CopyBothWaysAsync(raw, "ID, CL, BL, V, NV",
            [("C1", "NUMBER"), ("C2", "CLOB"), ("C3", "BLOB"), ("C4", "VARCHAR2(4000)"), ("C5", "NVARCHAR2(2000)")]);

        _output.WriteLine("PF5 probe varchar after lob: " + longStats);
        if (longStats.RefusedRuns == 0)
        {
            Assert.Equal(0, longStats.Fallbacks);
            Assert.Equal(0, longStats.Remembered);
            Assert.Equal(20, longStats.LongRows);
        }
        else
        {
            Assert.Equal(1, longStats.RefusedRuns); // 묶음 한 번만 실패(다시 보내지 않음)
            Assert.Equal(1, longStats.Fallbacks);   // 한 행씩 넣을 때 첫 거부 행 하나만 제자리에서 다시 보냄
            Assert.Equal(1, longStats.Remembered);  // 그 뒤로는 이 접속·문장을 옛 길로
            Assert.Equal(20, longStats.LongRows + longStats.LocatorRows);
        }

        Assert.Empty(await MismatchesAsync(raw, _tag + "_LONG", ["CL", "BL"]));
        Assert.Equal(0m, await ScalarAsync(raw, $"SELECT COUNT(*) FROM {Src} s JOIN {_tag}_LONG d ON d.ID = s.ID WHERE s.V <> d.V OR s.NV <> d.NV"));
        Assert.Equal(20m, await ScalarAsync(raw, $"SELECT COUNT(*) FROM {_tag}_LONG"));
    }

    /// <summary>
    /// LONG 구간의 진짜 값 오류(중복 키 — 거부 번호가 아님)는 실패로 올라가고 아무것도 기억하지 않는다: 되돌린 뒤 같은 문장의 다음 호출은 다시 LONG 으로 간다.
    /// </summary>
    [Fact]
    public async Task A_duplicate_key_in_a_long_bind_run_fails_as_before_and_does_not_turn_the_long_bind_off()
    {
        await using var raw = await RawAsync();
        await CreateAsync(raw, "ID NUMBER PRIMARY KEY, CL CLOB, BL BLOB");
        await using var s = await OpenAsync();
        var service = Service(OracleLobWrite.LongBind);
        List<BulkColumn> bulk = [new("C1", "NUMBER"), new("C2", "CLOB"), new("C3", "BLOB")];
        var insert = new SqlStatement($"INSERT INTO {_tag}_LONG (ID, CL, BL) VALUES (:C1, :C2, :C3)", default, StatementKind.Dml)
        {
            Verb = "INSERT",
            BindNames = ["C1", "C2", "C3"],
        };
        List<object?[]> duplicate = [[1m, "a", new byte[] { 1 }], [2m, "b", new byte[] { 2 }], [2m, "c", new byte[] { 3 }], [3m, "d", new byte[] { 4 }]];

        var failed = await service.ExecuteArrayAsync(s, insert, bulk, duplicate, CopyOptions, CancellationToken.None);
        await s.RollbackAsync(CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, failed.Status);
        Assert.Matches("ORA-(00001|24381)", failed.Error?.Code + " " + failed.Error?.Message); // 중복 키(배치 오류 방식이면 겉이 24381)
        Assert.Equal(0, service.LobWriteStats.RefusedRuns);
        Assert.Equal(0, service.LobWriteStats.Fallbacks);
        Assert.Equal(0, service.LobWriteStats.Remembered);

        var ok = await service.ExecuteArrayAsync(s, insert, bulk, [duplicate[0], duplicate[1], duplicate[3]], CopyOptions, CancellationToken.None);
        await s.CommitAsync(CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, ok.Status);
        Assert.Equal(3, ok.RowsAffected);
        Assert.Equal(3, service.LobWriteStats.LongRows); // 앞 실패가 빠른 길을 끄지 않았다
        Assert.Equal(3m, await ScalarAsync(raw, $"SELECT COUNT(*) FROM {_tag}_LONG"));
    }

    // ---- 도우미 ----

    private async Task CreateAsync(OracleConnection raw, string columns)
    {
        foreach (var suffix in new[] { "_SRC", "_LONG", "_LOB" })
        {
            await ExecAsync(raw, $"CREATE TABLE {_tag}{suffix} ({columns})");
        }
    }

    /// <summary>원본을 대량 읽기로 읽어 LONG 길(_LONG)·옛 길(_LOB)로 각각 넣고 커밋한다. 돌려주는 것은 두 서비스의 집계.</summary>
    private async Task<(LobWriteCounters Long, LobWriteCounters Lob)> CopyBothWaysAsync(OracleConnection raw, string names, (string Bind, string Type)[] columns)
    {
        await using var s = await OpenAsync();
        var reader = Service(OracleLobWrite.Locator);
        var rows = new List<object?[]>();
        await foreach (var row in reader.StreamRowsAsync(s, Query($"SELECT {names} FROM {Src} ORDER BY ID"), NoBinds, CopyOptions, CancellationToken.None))
        {
            rows.Add(row);
        }

        var bulk = columns.Select(c => new BulkColumn(c.Bind, c.Type)).ToList();
        var binds = columns.Select(c => c.Bind).ToList();
        var values = string.Join(", ", binds.Select(b => ":" + b));
        var counters = new List<LobWriteCounters>();
        foreach (var (mode, suffix) in new[] { (OracleLobWrite.LongBind, "_LONG"), (OracleLobWrite.Locator, "_LOB") })
        {
            var service = Service(mode);
            var insert = new SqlStatement($"INSERT INTO {_tag}{suffix} ({names}) VALUES ({values})", default, StatementKind.Dml) { Verb = "INSERT", BindNames = binds };
            await InsertLikeRunnerAsync(service, s, insert, bulk, rows, mode.ToString());
            counters.Add(service.LobWriteStats);
        }

        await s.CommitAsync(CancellationToken.None);
        Assert.Equal(await ScalarAsync(raw, $"SELECT COUNT(*) FROM {Src}"), rows.Count);
        return (counters[0], counters[1]);
    }

    /// <summary>
    /// 복사 실행기처럼 넣는다: 묶음 한 번, 실패하면(LONG 거부로만 실패해야 한다) 되돌리고 한 행씩. 이 시험 세션에는 그 전에 열린 변경이 없다(되돌림이 이 묶음만 지운다).
    /// </summary>
    private static async Task InsertLikeRunnerAsync(OracleBulkExecutionService service, OracleSession s, SqlStatement insert, List<BulkColumn> bulk,
                                                    List<object?[]> rows, string label)
    {
        var result = await service.ExecuteArrayAsync(s, insert, bulk, rows, CopyOptions, CancellationToken.None);
        if (result.Status == ExecutionStatus.Succeeded)
        {
            Assert.Equal(rows.Count, result.RowsAffected);
            return;
        }

        Assert.True(result.Status == ExecutionStatus.Failed && service.LobWriteStats.RefusedRuns > 0,
            label + ": LONG 거부가 아닌 실패 " + result.Error?.Code + " " + result.Error?.Message + " · " + service.LobWriteStats);
        await s.RollbackAsync(CancellationToken.None);
        foreach (var row in rows)
        {
            var one = await service.ExecuteArrayAsync(s, insert, bulk, [row], CopyOptions, CancellationToken.None);
            Assert.True(one.Status == ExecutionStatus.Succeeded, label + " 한 행: " + one.Error?.Message + " · " + service.LobWriteStats);
        }
    }

    /// <summary>원본과 다른 행의 ID: 한쪽에만 있음, NULL 여부가 다름, 길이가 다름, 내용(<c>DBMS_LOB.COMPARE</c>)이 다름.</summary>
    private async Task<List<decimal>> MismatchesAsync(OracleConnection raw, string target, string[] lobs)
    {
        var differs = string.Join(" OR ", lobs.Select(c =>
            $"(CASE WHEN s.{c} IS NULL THEN 0 ELSE 1 END) <> (CASE WHEN d.{c} IS NULL THEN 0 ELSE 1 END)"
            + $" OR (s.{c} IS NOT NULL AND d.{c} IS NOT NULL AND (DBMS_LOB.GETLENGTH(s.{c}) <> DBMS_LOB.GETLENGTH(d.{c})"
            + $" OR (DBMS_LOB.GETLENGTH(s.{c}) > 0 AND DBMS_LOB.COMPARE(s.{c}, d.{c}) <> 0)))"));
        await using var cmd = raw.CreateCommand();
        cmd.CommandText = $"SELECT NVL(s.ID, d.ID) FROM {Src} s FULL OUTER JOIN {target} d ON d.ID = s.ID WHERE s.ID IS NULL OR d.ID IS NULL OR {differs} ORDER BY 1";
        await using var reader = await cmd.ExecuteReaderAsync();
        var ids = new List<decimal>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetDecimal(0));
        }

        return ids;
    }

    /// <summary>열마다 NULL 행 수와 빈 값(길이 0) 행 수.</summary>
    private static async Task<string> NullsAndEmptiesAsync(OracleConnection raw, string table)
    {
        var parts = new List<string>();
        foreach (var c in new[] { "CL", "NCL", "BL" })
        {
            var nulls = await ScalarAsync(raw, $"SELECT COUNT(*) FROM {table} WHERE {c} IS NULL");
            var empties = await ScalarAsync(raw, $"SELECT COUNT(*) FROM {table} WHERE {c} IS NOT NULL AND DBMS_LOB.GETLENGTH({c}) = 0");
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{c}:null={nulls},empty={empties}"));
        }

        return string.Join(" ", parts);
    }

    /// <summary>원본 행 하나. 빈 값은 <c>EMPTY_CLOB()</c>·<c>EMPTY_BLOB()</c> 로, 나머지는 LOB 형식 바인드(옛 길 — 원본은 검증된 길로 만든다)로 넣는다.</summary>
    private async Task InsertSourceAsync(OracleConnection raw, int id, string? clob, string? nclob, byte[]? blob)
    {
        await using var cmd = raw.CreateCommand();
        cmd.BindByName = true;
        var cl = clob is { Length: 0 } ? "EMPTY_CLOB()" : ":cl";
        var ncl = nclob is { Length: 0 } ? "EMPTY_CLOB()" : ":ncl";
        var bl = blob is { Length: 0 } ? "EMPTY_BLOB()" : ":bl";
        cmd.CommandText = $"INSERT INTO {Src} (ID, CL, NCL, BL) VALUES (:id, {cl}, {ncl}, {bl})";
        cmd.Parameters.Add(new OracleParameter("id", OracleDbType.Decimal) { Value = id });
        if (clob is not { Length: 0 })
        {
            cmd.Parameters.Add(new OracleParameter("cl", OracleDbType.Clob) { Value = (object?)clob ?? DBNull.Value });
        }

        if (nclob is not { Length: 0 })
        {
            cmd.Parameters.Add(new OracleParameter("ncl", OracleDbType.NClob) { Value = (object?)nclob ?? DBNull.Value });
        }

        if (blob is not { Length: 0 })
        {
            cmd.Parameters.Add(new OracleParameter("bl", OracleDbType.Blob) { Value = (object?)blob ?? DBNull.Value });
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private OracleBulkExecutionService Service(OracleLobWrite mode) => new(new ExecutionPolicy(), _ui, _bus) { LobWrite = mode };

    private async Task<OracleSession> OpenAsync()
    {
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource);
        using var cred = new SecureCredential(_db.SystemPassword);
        return (OracleSession)await _manager.OpenAsync(profile, cred, SessionPurpose.Dedicated, default);
    }

    private async Task<OracleConnection> RawAsync()
    {
        var conn = new OracleConnection(new ConnectionProfile("free-system", "system", _db.DataSource).BuildConnectionString(_db.SystemPassword));
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(OracleConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<decimal> ScalarAsync(OracleConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToDecimal(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static SqlStatement Query(string text) => new(text, default, StatementKind.Query) { Verb = "SELECT" };

    /// <summary>끝 글자를 바꿔 잘린 값과 구별한다.</summary>
    private static string Text(int chars, char fill) => chars <= 1 ? new string('Z', chars) : new string(fill, chars - 1) + "Z";

    /// <summary>한글(국가 문자 집합 2바이트, AL32UTF8 3바이트)로 채운 값.</summary>
    private static string KoreanText(int chars)
    {
        const string Pattern = "한글복사";
        var builder = new StringBuilder(chars);
        for (var i = 0; i < chars; i++)
        {
            builder.Append(Pattern[i % Pattern.Length]);
        }

        return builder.ToString();
    }

    /// <summary>한글·ASCII·이모지(서로게이트 짝)를 섞은 값. 글자 수(UTF-16 단위)는 <paramref name="chars"/>, 짝이 갈리지 않게 끝을 맞춘다.</summary>
    private static string Emoji(int chars)
    {
        const string Pattern = "가a\U0001F600나b";
        var builder = new StringBuilder(chars + Pattern.Length);
        while (builder.Length < chars)
        {
            builder.Append(Pattern);
        }

        var length = char.IsHighSurrogate(builder[chars - 1]) ? chars - 1 : chars;
        return builder.ToString(0, length);
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 31) + seed);
        }

        return bytes;
    }
}
