using System.Security.Cryptography;
using System.Text;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.Grid;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.IntegrationTests.Grid;

// 소유: TSK-06-05. LOB 읽기(스트림)와 LOB 바인드 쓰기 — 컨테이너 26ai. 시간은 단정하지 않는다.
[Trait("Category", "Integration")]
public sealed class LobAccessorTests : IClassFixture<OracleFreeFixture>, IAsyncLifetime
{
    private const int TenMb = 10 * 1024 * 1024;

    private readonly OracleFreeFixture _db;
    private readonly ExecBus _bus = new();
    private readonly string _table = "ORANDA_LOB_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-lob-" + Guid.NewGuid().ToString("N")[..8]);
    private OracleSessionManager _manager = null!;
    private OracleExecutionService _exec = null!;

    public LobAccessorTests(OracleFreeFixture db) => _db = db;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _manager = new OracleSessionManager(_bus);
        _exec = new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), _bus, null, pageSize: 100);
        await AdminAsync($"CREATE TABLE {_table} (id NUMBER PRIMARY KEY, c CLOB, n NCLOB, b BLOB)");
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _manager.Sessions)
        {
            await s.DisposeAsync();
        }

        await AdminAsync($"DROP TABLE {_table} PURGE");
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>한글·이모지(서로게이트 쌍)가 섞인 글. 청크 경계에 쌍이 걸려도 깨지지 않아야 한다.</summary>
    private static string BuildText(int approxUtf8Bytes)
    {
        const string unit = "가나다 ABC 123 😀 라마바\n";
        var sb = new StringBuilder();
        var bytes = 0;
        var unitBytes = Encoding.UTF8.GetByteCount(unit);
        while (bytes < approxUtf8Bytes)
        {
            sb.Append(unit);
            bytes += unitBytes;
        }

        return sb.ToString();
    }

    private async Task<OracleSession> OpenAsync(bool readOnly = false)
    {
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource, ReadOnly: readOnly);
        using var cred = new SecureCredential(_db.SystemPassword);
        return (OracleSession)await _manager.OpenAsync(profile, cred, SessionPurpose.Work, default);
    }

    private async Task InsertAsync(int id, string? clob, byte[]? blob)
    {
        await using var conn = new OracleConnection(new ConnectionProfile("o", "system", _db.DataSource).BuildConnectionString(_db.SystemPassword));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.BindByName = true;
        cmd.CommandText = $"INSERT INTO {_table} (id, c, n, b) VALUES (:id, :c, :c, :b)";
        cmd.Parameters.Add("id", OracleDbType.Int32).Value = id;
        cmd.Parameters.Add("c", OracleDbType.Clob).Value = (object?)clob ?? DBNull.Value;
        cmd.Parameters.Add("b", OracleDbType.Blob).Value = (object?)blob ?? DBNull.Value;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<LobValue> FetchLobAsync(ISession s, string column, int id)
    {
        var stmt = new SqlStatement($"SELECT {column} FROM {_table} WHERE id = {id}", default, StatementKind.Query) { Verb = "SELECT" };
        var r = await _exec.ExecuteAsync(s, stmt, new Dictionary<string, BindValue>(), null, default);
        Assert.Equal(ExecutionStatus.Succeeded, r.Status);
        var page = await r.ResultSet!.GetPageAsync(0, default);
        var lob = Assert.IsType<LobValue>(page.Columns[0][0]);
        await r.ResultSet.DisposeAsync(); // 결과 집합을 닫은 뒤에도 로케이터로 읽혀야 한다
        return lob;
    }

    private static async Task<byte[]> ReadAllAsync(Stream s)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Ten_mb_clob_streamed_to_file_equals_original()
    {
        var original = BuildText(TenMb);
        Assert.True(Encoding.UTF8.GetByteCount(original) >= TenMb);
        await InsertAsync(1, original, null);
        await using var s = await OpenAsync();

        var lob = await FetchLobAsync(s, "c", 1);
        Assert.Equal(LobKind.Clob, lob.Kind);
        Assert.True(lob.IsTruncated); // 미리보기는 앞부분만

        var accessor = new OracleLobAccessor();
        var path = Path.Combine(_dir, "clob.txt");
        await using (var stream = await accessor.OpenReadAsync(s, lob, default))
        await using (var file = File.Create(path))
        {
            await stream.CopyToAsync(file);
        }

        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(original)), SHA256.HashData(await File.ReadAllBytesAsync(path)));
        Assert.Equal(original, await File.ReadAllTextAsync(path, Encoding.UTF8));
    }

    [Fact]
    public async Task Nclob_text_streams_as_utf8()
    {
        var original = BuildText(300_000);
        await InsertAsync(2, original, null);
        await using var s = await OpenAsync();

        var lob = await FetchLobAsync(s, "n", 2);
        Assert.Equal(LobKind.NClob, lob.Kind);
        await using var stream = await new OracleLobAccessor().OpenReadAsync(s, lob, default);
        Assert.Equal(original, Encoding.UTF8.GetString(await ReadAllAsync(stream)));
    }

    [Fact]
    public async Task Blob_streams_exact_bytes_and_reopens_independently()
    {
        var bytes = new byte[5 * 1024 * 1024 + 123];
        new Random(42).NextBytes(bytes);
        await InsertAsync(3, null, bytes);
        await using var s = await OpenAsync();

        var lob = await FetchLobAsync(s, "b", 3);
        Assert.Equal(LobKind.Blob, lob.Kind);
        Assert.Equal(bytes.Length, lob.Length);
        var accessor = new OracleLobAccessor();

        // 같은 LOB 를 두 번 열어도 서로 위치를 건드리지 않는다.
        await using var first = await accessor.OpenReadAsync(s, lob, default);
        var head = new byte[16];
        Assert.Equal(16, await first.ReadAsync(head));
        await using var second = await accessor.OpenReadAsync(s, lob, default);
        Assert.Equal(bytes, await ReadAllAsync(second));
        Assert.Equal(bytes[..16], head);
    }

    [Fact]
    public async Task Small_lob_preview_is_complete()
    {
        await InsertAsync(4, "짧은 글", [1, 2, 3]);
        await using var s = await OpenAsync();

        var small = await FetchLobAsync(s, "c", 4);
        Assert.False(small.IsTruncated);
        Assert.Equal("짧은 글", small.TextPreview);
        await using var stream = await new OracleLobAccessor().OpenReadAsync(s, small, default);
        Assert.Equal("짧은 글", Encoding.UTF8.GetString(await ReadAllAsync(stream)));

        var blob = await FetchLobAsync(s, "b", 4);
        Assert.Equal([1, 2, 3], blob.BinaryPreview.ToArray());
    }

    [Fact]
    public async Task Lob_without_locator_is_rejected_clearly()
    {
        await using var s = await OpenAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new OracleLobAccessor().OpenReadAsync(s, new LobValue(LobKind.Clob, 10) { TextPreview = "x", IsTruncated = true }, default));
        Assert.Contains("로케이터", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ten_mb_clob_and_blob_binds_round_trip_through_execution_engine()
    {
        var text = BuildText(TenMb);
        var bytes = new byte[TenMb];
        new Random(7).NextBytes(bytes);
        await InsertAsync(6, null, null);
        await using var s = await OpenAsync();

        var update = new SqlStatement($"UPDATE {_table} SET c = :v1, b = :v2 WHERE id = :k1", default, StatementKind.Dml)
        {
            Verb = "UPDATE",
            BindNames = ["v1", "v2", "k1"],
        };
        var binds = new Dictionary<string, BindValue>
        {
            ["v1"] = new(BindDataType.Clob, text),
            ["v2"] = new(BindDataType.Blob, bytes),
            ["k1"] = new(BindDataType.Number, 6m),
        };
        var r = await _exec.ExecuteAsync(s, update, binds, null, default);
        Assert.Equal(ExecutionStatus.Succeeded, r.Status);
        Assert.Equal(1, r.RowsAffected);
        await s.CommitAsync(default);

        var accessor = new OracleLobAccessor();
        await using (var cs = await accessor.OpenReadAsync(s, await FetchLobAsync(s, "c", 6), default))
        {
            Assert.Equal(text, Encoding.UTF8.GetString(await ReadAllAsync(cs)));
        }

        await using var bs = await accessor.OpenReadAsync(s, await FetchLobAsync(s, "b", 6), default);
        Assert.Equal(bytes, await ReadAllAsync(bs));
    }

    [Fact]
    public async Task Read_only_profile_cannot_update_lob()
    {
        await InsertAsync(7, "원본", null);
        await using var s = await OpenAsync(readOnly: true);
        var update = new SqlStatement($"UPDATE {_table} SET c = :v1 WHERE id = :k1", default, StatementKind.Dml)
        {
            Verb = "UPDATE",
            BindNames = ["v1", "k1"],
        };
        var r = await _exec.ExecuteAsync(s, update, new Dictionary<string, BindValue>
        {
            ["v1"] = new(BindDataType.Clob, "바뀜"),
            ["k1"] = new(BindDataType.Number, 7m),
        }, null, default);

        Assert.Equal(ExecutionStatus.Denied, r.Status);
        await using var s2 = await OpenAsync();
        var lob = await FetchLobAsync(s2, "c", 7);
        Assert.Equal("원본", lob.TextPreview);
    }

    private async Task AdminAsync(string sql)
    {
        await using var other = new OracleConnection(new ConnectionProfile("o", "system", _db.DataSource).BuildConnectionString(_db.SystemPassword));
        await other.OpenAsync();
        await using var cmd = other.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>DB 없이 도는 CLOB 문자 → UTF-8 스트림 변환 시험(청크 경계에 서로게이트 쌍이 걸려도 깨지지 않는다).</summary>
public class ClobUtf8StreamTests
{
    private static Func<char[], int, int, int> Reader(string text, int maxPerRead)
    {
        var pos = 0;
        return (buf, off, count) =>
        {
            var n = Math.Min(Math.Min(count, maxPerRead), text.Length - pos);
            text.CopyTo(pos, buf, off, n);
            pos += n;
            return n;
        };
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(5, 5)]
    [InlineData(32768, 7)]
    public void Utf8_bytes_equal_original_for_any_chunking(int chunkChars, int perRead)
    {
        var text = string.Concat(Enumerable.Repeat("가나다 ABC 😀 라마바\n", 2000));
        using var stream = new ClobUtf8Stream(Reader(text, perRead), null, chunkChars);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);

        Assert.Equal(Encoding.UTF8.GetBytes(text), ms.ToArray());
    }

    [Fact]
    public void Empty_clob_reads_zero_bytes_and_owner_is_disposed()
    {
        var owner = new Owner();
        using (var stream = new ClobUtf8Stream(Reader(string.Empty, 10), owner))
        {
            Assert.Equal(0, stream.Read(new byte[8], 0, 8));
        }

        Assert.True(owner.Disposed);
    }

    private sealed class Owner : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
