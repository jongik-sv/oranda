using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.Sessions;
using Xunit.Abstractions;

namespace Oranda.Oracle.IntegrationTests.Execution;

// te-followup 회귀(레인 te-w30-verify): 앱에서 열을 더하거나 지운 뒤 같은 세션에서 같은 질의를 실행 엔진으로 다시 읽으면,
// ODP 메타데이터 캐시(GetSchemaTable)가 옛 열 목록을 돌려 데이터 탭이 IndexOutOfRangeException 으로 실패했다(MetadataPooling=false 로 고침).
// 캐시가 다시 켜지면 SchemaOrNull 이 스키마 표를 건너뛰어 ROWID 분류·열 출처가 사라지므로, 그 둘을 단언해 회귀를 잡는다.
// 상주 컨테이너: ORANDA_IT_HOST, ORANDA_IT_PORT(기본 1521), ORANDA_IT_SERVICE, ORANDA_IT_ADMIN_USER, ORANDA_IT_ADMIN_PASSWORD.
// 26ai = 1521/FREEPDB1, 11g = ORANDA_IT_PORT=1522 · ORANDA_IT_SERVICE=XE 로 한 번 더 돌린다. 시험 계정은 실행마다 고유 이름(RR_xxxxxxxx)으로 만들고 지운다.
[Trait("Category", "Integration")]
public sealed class ColumnChangeRereadIntegrationTests : IAsyncLifetime
{
    private static readonly Dictionary<string, BindValue> NoBinds = [];

    private readonly ITestOutputHelper _output;
    private readonly string _owner = "RR_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _password = "Rr" + Guid.NewGuid().ToString("N")[..16] + "x1";
    private readonly ExecBus _bus = new();
    private readonly List<ISession> _sessions = [];
    private bool _enabled;

    public ColumnChangeRereadIntegrationTests(ITestOutputHelper output) => _output = output;

    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    private static string DataSource => $"{Env("HOST")}:{Env("PORT") ?? "1521"}/{Env("SERVICE")}";

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

        await using var admin = Connect(Env("ADMIN_USER")!, Env("ADMIN_PASSWORD")!);
        await admin.OpenAsync();
        await ExecAsync(admin, $"CREATE USER {_owner} IDENTIFIED BY \"{_password}\" DEFAULT TABLESPACE users QUOTA UNLIMITED ON users");
        await ExecAsync(admin, $"GRANT CREATE SESSION, CREATE TABLE TO {_owner}");
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions)
        {
            await s.DisposeAsync();
        }

        if (!_enabled)
        {
            return;
        }

        await using var admin = Connect(Env("ADMIN_USER")!, Env("ADMIN_PASSWORD")!);
        await admin.OpenAsync();
        await ExecAsync(admin, $"DROP USER {_owner} CASCADE");
    }

    [Fact]
    public async Task Same_session_rereads_the_table_after_columns_are_added_and_dropped()
    {
        if (Skip())
        {
            return;
        }

        var s = await OpenAsync();
        var service = new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), _bus, null, pageSize: 500);
        await RunAsync(service, s, "CREATE TABLE ort_te_rr (id NUMBER PRIMARY KEY, name VARCHAR2(20), old_c VARCHAR2(5))", StatementKind.Ddl);
        await RunAsync(service, s, "INSERT INTO ort_te_rr VALUES (1, 'a', 'x')", StatementKind.Dml);
        await RunAsync(service, s, "INSERT INTO ort_te_rr VALUES (2, 'b', 'y')", StatementKind.Dml);
        await RunAsync(service, s, "COMMIT", StatementKind.TransactionControl);
        const string query = "SELECT t.ROWID, t.* FROM ort_te_rr t ORDER BY t.id";

        var first = await ReadAsync(service, s, query);
        Assert.Equal(["ROWID", "ID", "NAME", "OLD_C"], first.Select(c => c.Name));

        await RunAsync(service, s, "ALTER TABLE ort_te_rr ADD (v1 NUMBER GENERATED ALWAYS AS (id * 2) VIRTUAL)", StatementKind.Ddl);
        var afterVirtual = await ReadAsync(service, s, query, cols => Assert.Equal(2m, cols[^1][0]));
        Assert.Equal(["ROWID", "ID", "NAME", "OLD_C", "V1"], afterVirtual.Select(c => c.Name));
        Assert.Equal("V1", afterVirtual[^1].BaseColumn); // 스키마 표를 새로 읽었다(캐시면 새 열의 출처가 빈다)

        await RunAsync(service, s, "ALTER TABLE ort_te_rr ADD (note VARCHAR2(30) DEFAULT 'n')", StatementKind.Ddl);
        var afterAdd = await ReadAsync(service, s, query, cols => Assert.Equal("n", cols[^1][0]));
        Assert.Equal(["ROWID", "ID", "NAME", "OLD_C", "V1", "NOTE"], afterAdd.Select(c => c.Name));

        await RunAsync(service, s, "ALTER TABLE ort_te_rr DROP COLUMN old_c", StatementKind.Ddl);
        Assert.Equal(["ROWID", "ID", "NAME", "V1", "NOTE"], (await ReadAsync(service, s, query)).Select(c => c.Name));

        // 열 수는 같고 형식만 바뀜(지우고 다른 이름·같은 이름 DATE 로 다시 더하기). 지운 열의 값이 새 열에 남지 않는다(Tibero 는 가상 열이 있으면 남는다).
        await RunAsync(service, s, "ALTER TABLE ort_te_rr DROP COLUMN note", StatementKind.Ddl);
        await RunAsync(service, s, "ALTER TABLE ort_te_rr ADD (note2 VARCHAR2(30))", StatementKind.Ddl);
        var other = await ReadAsync(service, s, query, cols => Assert.All(cols[^1], Assert.Null));
        Assert.Equal("NOTE2", other[^1].BaseColumn);

        await RunAsync(service, s, "ALTER TABLE ort_te_rr DROP COLUMN note2", StatementKind.Ddl);
        await RunAsync(service, s, "ALTER TABLE ort_te_rr ADD (note DATE)", StatementKind.Ddl);
        var date = await ReadAsync(service, s, query, cols => Assert.All(cols[^1], Assert.Null));
        Assert.Equal(["ROWID", "ID", "NAME", "V1", "NOTE"], date.Select(c => c.Name));
        Assert.Equal(ColumnDataKind.Date, date[^1].Kind);
    }

    private async Task RunAsync(OracleExecutionService service, ISession s, string sql, StatementKind kind)
    {
        var stmt = new SqlStatement(sql, default, kind) { Verb = sql.Split(' ')[0].ToUpperInvariant() };
        var r = await service.ExecuteAsync(s, stmt, NoBinds, null, default);
        _output.WriteLine($"{r.Status} :: {sql}");
        Assert.True(r.Status == ExecutionStatus.Succeeded, $"{r.Status} {r.Error?.Code} {r.Error?.Message} :: {sql}");
        if (r.ResultSet is { } rs)
        {
            await rs.DisposeAsync();
        }
    }

    /// <summary>실행 엔진으로 질의를 돌려 첫 쪽을 읽는다. ROWID 열은 ROWID 분류여야 한다(스키마 표를 읽었다는 뜻). <paramref name="check"/> = 열마다 칸 값.</summary>
    private async Task<IReadOnlyList<ResultColumn>> ReadAsync(OracleExecutionService service, ISession s, string sql, Action<IReadOnlyList<IReadOnlyList<object?>>>? check = null)
    {
        var r = await service.ExecuteAsync(s, new SqlStatement(sql, default, StatementKind.Query) { Verb = "SELECT" }, NoBinds, null, default);
        Assert.True(r.Status == ExecutionStatus.Succeeded, $"{r.Status} {r.Error?.Code} {r.Error?.Message}");
        await using var rs = Assert.IsType<OracleResultSet>(r.ResultSet);
        _output.WriteLine("columns = " + string.Join(", ", rs.Columns.Select(c => $"{c.Name}:{c.DbTypeName}:{c.Kind}:{c.BaseColumn}")));
        Assert.Equal(ColumnDataKind.RowId, rs.Columns[0].Kind);
        var page = await rs.GetPageAsync(0, default);
        Assert.Equal(2, page.RowCount);
        Assert.Equal(rs.Columns.Count, page.Columns.Count);
        check?.Invoke([.. page.Columns.Select(col => (IReadOnlyList<object?>)[.. Enumerable.Range(0, page.RowCount).Select(i => col[i])])]);
        return rs.Columns;
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

    private async Task<ISession> OpenAsync()
    {
        var manager = new OracleSessionManager(_bus);
        using var cred = new SecureCredential(_password);
        var session = await manager.OpenAsync(new ConnectionProfile("t-reread", _owner, DataSource), cred, SessionPurpose.Work, default);
        _sessions.Add(session);
        return session;
    }

    private static OracleConnection Connect(string user, string password) =>
        new(new ConnectionProfile("t-reread", user, DataSource).BuildConnectionString(password));

    private static async Task ExecAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
