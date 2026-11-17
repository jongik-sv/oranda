using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Connection;
using Oranda.Oracle.IntegrationTests.Metadata;
using Oranda.Oracle.IntegrationTests.Sessions;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Schema;
using Oranda.Oracle.Sessions;
using Xunit.Abstractions;

namespace Oranda.Oracle.IntegrationTests.Schema;

// 테이블 편집기 「저장」 페이지(T13c, 설계 18.2): 메타데이터 공급자가 실제 Oracle 에서 저장 속성(meta.table_props 끝 열)과
// LOB 저장 절(meta.table_lobs)을 TableInfo.Storage·Lobs 에 채우고, 그 값으로 만든 편집 모델이 「안 고치면 0문장」·「고친 문장 실행 뒤 다시 읽으면 0문장」인지 본다.
// 상주 컨테이너: ORANDA_IT_HOST, ORANDA_IT_PORT(기본 1521), ORANDA_IT_SERVICE, ORANDA_IT_ADMIN_USER, ORANDA_IT_ADMIN_PASSWORD.
// 26ai = 1521/FREEPDB1, 11g = ORANDA_IT_PORT=1522 · ORANDA_IT_SERVICE=XE 로 한 번 더 돌린다. 시험 계정은 실행마다 고유 이름(TS_xxxxxxxx)으로 만들고 지운다.
[Trait("Category", "Integration")]
public sealed class TableStorageIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly string _owner = "TS_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _password = "Ts" + Guid.NewGuid().ToString("N")[..16] + "x1";
    private readonly List<ISession> _sessions = [];
    private bool _enabled;
    private int _major;

    public TableStorageIntegrationTests(ITestOutputHelper output) => _output = output;

    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    private static string DataSource => $"{Env("HOST")}:{Env("PORT") ?? "1521"}/{Env("SERVICE")}";

    private DbObjectRef Orders => new(_owner, "ORDERS", "TABLE");

    public async Task InitializeAsync()
    {
        _enabled = Env("HOST") is not null && Env("SERVICE") is not null && Env("ADMIN_USER") is not null && Env("ADMIN_PASSWORD") is not null;
        if (!_enabled)
        {
            if (Env("REQUIRE") == "1")
            {
                throw new InvalidOperationException("ORANDA_IT_HOST/SERVICE/ADMIN_USER/ADMIN_PASSWORD 가 없다.");
            }

            return;
        }

        await using (var admin = Connect(Env("ADMIN_USER")!, Env("ADMIN_PASSWORD")!))
        {
            await admin.OpenAsync();
            _major = int.Parse(admin.ServerVersion.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
            await ExecAsync(admin, $"CREATE USER {_owner} IDENTIFIED BY \"{_password}\" DEFAULT TABLESPACE users QUOTA UNLIMITED ON users");
            await ExecAsync(admin, $"GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW TO {_owner}");
        }

        try
        {
            await using var owner = Connect(_owner, _password);
            await owner.OpenAsync();

            // 12c 이상만 기본 압축(11g XE 는 COMPRESS BASIC 이 ORA-00439). SECUREFILE 은 11g 도 된다.
            var compress = _major >= 12 ? " ROW STORE COMPRESS BASIC" : string.Empty;
            await ExecAsync(owner, $"""
                CREATE TABLE orders (id NUMBER CONSTRAINT pk_orders PRIMARY KEY, memo CLOB, doc CLOB, pic BLOB)
                  PCTFREE 20 INITRANS 4 STORAGE (INITIAL 256K) NOLOGGING PARALLEL 4{compress}
                  LOB (memo) STORE AS SECUREFILE (DISABLE STORAGE IN ROW)
                  LOB (doc) STORE AS BASICFILE
                """);
            await ExecAsync(owner, "CREATE INDEX ix_orders_01 ON orders (id, 1)");
            await ExecAsync(owner, "CREATE TABLE plain (id NUMBER)");
            await ExecAsync(owner, "CREATE VIEW v_orders AS SELECT id FROM orders");
        }
        catch
        {
            await DropOwnerAsync(); // InitializeAsync 가 실패하면 xUnit 은 DisposeAsync 를 부르지 않는다
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions)
        {
            await s.DisposeAsync();
        }

        if (_enabled)
        {
            await DropOwnerAsync();
        }
    }

    [Fact]
    public async Task Metadata_reads_storage_attributes_and_lob_storage()
    {
        if (Skip())
        {
            return;
        }

        var s = await OpenAsync();
        var provider = NewProvider();
        var orders = await provider.GetTableAsync(s, Orders, default);

        _output.WriteLine($"{orders.Storage} | {string.Join(" | ", orders.Lobs)}");
        var storage = Assert.IsType<TableStorage>(orders.Storage);
        Assert.Equal((20, 4, false, 4), (storage.PctFree, storage.InitTrans, storage.Logging, storage.Parallel));
        Assert.Equal(_major >= 12 ? TableCompression.Basic : TableCompression.None, storage.Compression);
        Assert.Equal(((string?)null, (string?)null), (storage.InitialExtent, storage.NextExtent)); // 익스텐트는 읽지 않는다(TE-39)

        Assert.Equal(["DOC", "MEMO", "PIC"], orders.Lobs.Select(l => l.Column));
        var lobs = orders.Lobs.ToDictionary(l => l.Column, l => l.Storage);
        Assert.Equal((LobStorageKind.SecureFile, false, LobCompression.None, false), (lobs["MEMO"].Kind, lobs["MEMO"].InRow, lobs["MEMO"].Compression, lobs["MEMO"].Deduplicate));
        Assert.Equal((LobStorageKind.BasicFile, true, (LobCompression?)null, (bool?)null), (lobs["DOC"].Kind, lobs["DOC"].InRow, lobs["DOC"].Compression, lobs["DOC"].Deduplicate));
        Assert.Equal("USERS", lobs["MEMO"].Tablespace);

        var plain = await provider.GetTableAsync(s, new DbObjectRef(_owner, "PLAIN", "TABLE"), default);
        Assert.Equal((TableCompression.None, true, 1), (plain.Storage!.Compression, plain.Storage.Logging, plain.Storage.Parallel));
        Assert.Empty(plain.Lobs);

        var view = await provider.GetTableAsync(s, new DbObjectRef(_owner, "V_ORDERS", "VIEW"), default);
        Assert.Null(view.Storage);
        Assert.Empty(view.Lobs);
    }

    [Fact]
    public async Task Storage_edits_run_and_the_reread_table_has_no_differences()
    {
        if (Skip())
        {
            return;
        }

        var s = await OpenAsync();
        var dialect = new OracleTableEditDialect();
        var original = TableDraftFactory.FromTable(await NewProvider().GetTableAsync(s, Orders, default), dialect);

        // 안 고치면 0문장: 따로 두 번 읽은 모델끼리(같은 객체 비교는 늘 비어 있다).
        var second = TableDraftFactory.FromTable(await NewProvider().GetTableAsync(s, Orders, default), dialect);
        var untouched = TableEditScriptBuilder.Build(original, second, dialect, s.Server);
        Assert.True(untouched.IsEmpty, untouched.PreviewText());

        // PCTFREE·LOGGING·PARALLEL 은 MOVE 없이, LOB 행 안 저장은 MOVE(11g = UPDATE INDEXES 없음 → 인덱스 REBUILD).
        var edited = original with
        {
            Storage = original.Storage with { PctFree = 30, Logging = true, Parallel = 1 },
            Columns = [.. original.Columns.Select(c => c.Name == "MEMO" ? c with { Lob = c.Lob! with { InRow = true } } : c)],
        };
        var script = TableEditScriptBuilder.Build(original, edited, dialect, s.Server);
        _output.WriteLine(script.PreviewText());
        Assert.False(script.IsEmpty);

        await using (var owner = Connect(_owner, _password))
        {
            await owner.OpenAsync();
            foreach (var statement in script.SqlStatements)
            {
                try
                {
                    await ExecAsync(owner, statement.Text);
                }
                catch (OracleException ex)
                {
                    Assert.Fail($"{statement.Text}{Environment.NewLine}{ex.Message}");
                }
            }
        }

        var reread = await NewProvider().GetTableAsync(s, Orders, default);
        Assert.Equal((30, true, 1), (reread.Storage!.PctFree, reread.Storage.Logging, reread.Storage.Parallel));
        Assert.True(reread.Lobs.Single(l => l.Column == "MEMO").Storage.InRow);
        // 다시 읽은 모델 → 편집 모델: 서버가 편집대로면 0문장(줄 키는 FromTable 이 같은 순서로 매긴다).
        var again = TableEditScriptBuilder.Build(TableDraftFactory.FromTable(reread, dialect), edited, dialect, s.Server);
        Assert.True(again.IsEmpty, again.PreviewText());
        Assert.True(TableDraftDiff.Compute(TableDraftFactory.FromTable(reread, dialect), edited, dialect).IsEmpty);

        // MOVE 뒤 인덱스가 쓸 수 있는 상태(UPDATE INDEXES 또는 REBUILD).
        await using var check = Connect(_owner, _password);
        await check.OpenAsync();
        await using var cmd = check.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM user_indexes WHERE table_name = 'ORDERS' AND status NOT IN ('VALID', 'N/A')";
        Assert.Equal(0m, (decimal)(await cmd.ExecuteScalarAsync())!);
    }

    private bool Skip()
    {
        if (_enabled)
        {
            return false;
        }

        _output.WriteLine("건너뜀: ORANDA_IT_HOST/SERVICE/ADMIN_USER/ADMIN_PASSWORD 가 없다");
        return true;
    }

    private async Task DropOwnerAsync()
    {
        await using var admin = Connect(Env("ADMIN_USER")!, Env("ADMIN_PASSWORD")!);
        await admin.OpenAsync();
        await ExecAsync(admin, $"DROP USER {_owner} CASCADE");
    }

    private static OracleMetadataProvider NewProvider() => new(new MemoryMetadataCache(), new RecordingBus(), Timeout.InfiniteTimeSpan);

    private async Task<ISession> OpenAsync()
    {
        var manager = new OracleSessionManager(new RecordingBus());
        using var cred = new SecureCredential(_password);
        var session = await manager.OpenAsync(new ConnectionProfile("t-table-storage", _owner, DataSource), cred, SessionPurpose.Work, default);
        _sessions.Add(session);
        return session;
    }

    private static OracleConnection Connect(string user, string password) =>
        new(new ConnectionProfile("t-table-storage", user, DataSource).BuildConnectionString(password));

    private static async Task ExecAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
