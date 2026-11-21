using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.IntegrationTests.Tuning;

// 소유: SG-1 스키마 브라우저 통계 수집 실행. 앱과 같은 길(IExecutionService + PreConfirmed, 이력 출처 SchemaBrowser)로 보낸다.

/// <summary>공통 몸통: 표를 만들고 옵션 조합마다 실행해 LAST_ANALYZED 를 본다.</summary>
internal static class GatherStatsIt
{
    public static ExecutionOptions Options(string sql) =>
        new() { Source = HistorySource.SchemaBrowser, PreConfirmed = new HashSet<string>(StringComparer.Ordinal) { sql } };

    public static SqlStatement Statement(string sql) => new(sql, default, StatementKind.PlSql) { Verb = "BEGIN" };

    public static async Task ExecAsync(OracleConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<object?> ScalarAsync(OracleConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync();
        return v is DBNull ? null : v;
    }

    public static async Task RunCasesAsync(OracleConnection raw, ISession session, IExecutionService exec, string owner, string table, IEnumerable<GatherStatsOptions> cases)
    {
        await ExecAsync(raw, $"CREATE TABLE {table} (id NUMBER PRIMARY KEY, cd VARCHAR2(10))");
        try
        {
            await ExecAsync(raw, $"INSERT INTO {table} SELECT level, MOD(level, 7) FROM dual CONNECT BY level <= 500");
            await ExecAsync(raw, "COMMIT");
            Assert.Null(await ScalarAsync(raw, $"SELECT last_analyzed FROM all_tables WHERE owner = '{owner}' AND table_name = '{table}'"));
            var dialect = GatherStatsDialect.For(Dbms.Oracle);
            foreach (var options in cases)
            {
                var sql = dialect.Build(owner, table, options);
                var result = await exec.ExecuteAsync(session, Statement(sql), new Dictionary<string, BindValue>(), Options(sql), default);
                Assert.True(result.Status == ExecutionStatus.Succeeded, $"{options}: {result.Status} {result.Error?.Message}");
            }

            Assert.NotNull(await ScalarAsync(raw, $"SELECT last_analyzed FROM all_tables WHERE owner = '{owner}' AND table_name = '{table}'"));
        }
        finally
        {
            await ExecAsync(raw, $"DROP TABLE {table} PURGE");
        }
    }

    public static GatherStatsOptions[] AllCases =>
    [
        GatherStatsOptions.Default,
        new() { SamplePercent = 25, Histogram = GatherStatsHistogram.None, Cascade = false, Degree = 2 },
        GatherStatsOptions.Default with { Histogram = GatherStatsHistogram.SkewOnly },
    ];
}

/// <summary>26ai: 옵션 조합, 운영 접속에서도 확인 창에서 받은 확인으로 다시 묻지 않음, 권한 없는 계정의 ORA-20000.</summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class GatherStatsIntegrationTests(OracleFreeFixture db) : IAsyncLifetime
{
    private readonly ExecBus _bus = new();
    private readonly ExecInteraction _ui = new() { ConfirmAnswer = false }; // 다시 물으면 거절 → 실패로 드러난다
    private OracleSessionManager _manager = null!;
    private readonly List<OracleConnection> _conns = [];

    private OracleConnection Raw => _conns[0];

    public async Task InitializeAsync()
    {
        _manager = new OracleSessionManager(_bus);
        var raw = new OracleConnection(new ConnectionProfile("free-system", "system", db.DataSource).BuildConnectionString(db.SystemPassword));
        _conns.Add(raw);
        await raw.OpenAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _manager.Sessions)
        {
            await s.DisposeAsync();
        }

        foreach (var c in _conns)
        {
            await c.DisposeAsync();
        }
    }

    private OracleExecutionService Exec() => new(new ExecutionPolicy(), _ui, _bus, null, pageSize: 500);

    private async Task<ISession> OpenAsync(string user, string password, ConnectionEnvironment env)
    {
        var profile = new ConnectionProfile("free-" + user, user, db.DataSource, Environment: env);
        using var cred = new SecureCredential(password);
        return await _manager.OpenAsync(profile, cred, SessionPurpose.Dedicated, default);
    }

    private static string NewTable() => "ORT_AE_GS_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    public async Task Every_option_combination_gathers_on_a_production_connection_without_asking_again()
    {
        var session = await OpenAsync("system", db.SystemPassword, ConnectionEnvironment.Production);

        await GatherStatsIt.RunCasesAsync(Raw, session, Exec(), "SYSTEM", NewTable(), GatherStatsIt.AllCases);

        Assert.Empty(_ui.Confirms);
    }

    [Fact]
    public async Task Account_without_privilege_gets_ora_20000()
    {
        var table = NewTable();
        await GatherStatsIt.ExecAsync(Raw, $"CREATE TABLE {table} (id NUMBER)");
        try
        {
            var app = await OpenAsync(OracleFreeFixture.AppUser, db.AppPassword, ConnectionEnvironment.Test);
            var sql = GatherStatsDialect.For(Dbms.Oracle).Build("SYSTEM", table, GatherStatsOptions.Default);

            var result = await Exec().ExecuteAsync(app, GatherStatsIt.Statement(sql), new Dictionary<string, BindValue>(), GatherStatsIt.Options(sql), default);

            Assert.Equal(ExecutionStatus.Failed, result.Status);
            Assert.True(result.Error!.Number == 20000 || result.Error.Kind.IsNoAccess(), result.Error.Code + " " + result.Error.Message);
        }
        finally
        {
            await GatherStatsIt.ExecAsync(Raw, $"DROP TABLE {table} PURGE");
        }
    }
}

/// <summary>11g XE: 같은 옵션 조합(SKEWONLY 포함)이 옛 서버에서도 돈다.</summary>
[Trait("Category", "Integration")]
[Collection(Oracle11gShared.Name)]
public sealed class GatherStats11gTests(Oracle11gFixture db)
{
    [Fact]
    public async Task Every_option_combination_gathers_on_11g()
    {
        var bus = new ExecBus();
        var manager = new OracleSessionManager(bus);
        var profile = new ConnectionProfile("xe-system", "system", db.DataSource);
        await using var raw = new OracleConnection(profile.BuildConnectionString(db.SystemPassword));
        await raw.OpenAsync();
        using var cred = new SecureCredential(db.SystemPassword);
        await using var session = await manager.OpenAsync(profile, cred, SessionPurpose.Dedicated, default);

        await GatherStatsIt.RunCasesAsync(raw, session, new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), bus, null, pageSize: 500),
            "SYSTEM", "ORT_AE_GS_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), GatherStatsIt.AllCases);
    }
}
