using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using Oranda.Language;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Plans;
using Oranda.Oracle.Sessions;
using Oranda.Oracle.Tuning;

namespace Oranda.Oracle.IntegrationTests.Tuning;

// 소유: TSK-06-06. 팩 없는 대기 분해: 다른 세션이 실행 중인 sql_id 를 gv$session 표본으로 모아 비율로 묶는다.

[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class WaitSampleIntegrationTests : IAsyncLifetime
{
    private readonly OracleFreeFixture _db;
    private readonly ExecBus _bus = new();
    private readonly List<OracleConnection> _raw = [];
    private OracleSessionManager _manager = null!;

    public WaitSampleIntegrationTests(OracleFreeFixture db) => _db = db;

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

        foreach (var c in _raw)
        {
            await c.DisposeAsync();
        }
    }

    private OracleTuningService Service()
    {
        var exec = new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), _bus, null, pageSize: 500);
        return new OracleTuningService(exec, new OracleExecutionPlanService(exec), new LanguageService());
    }

    private async Task<OracleConnection> RawAsync()
    {
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource);
        var conn = new OracleConnection(profile.BuildConnectionString(_db.SystemPassword));
        await conn.OpenAsync();
        _raw.Add(conn);
        return conn;
    }

    [Fact]
    public async Task Samples_of_a_running_sql_count_its_wait_and_none_after_it_ends()
    {
        // 다른 세션이 5 초 동안 잔다(DBMS_SESSION.SLEEP = 대기 이벤트 「PL/SQL lock timer」). 시험 준비만 이 세션에서 한다.
        var tag = "TNAWR_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var sleeper = await RawAsync();
        var block = $"BEGIN /* {tag} */ DBMS_SESSION.SLEEP(5); END;";
        var running = Task.Run(async () =>
        {
            await using var cmd = sleeper.CreateCommand();
            cmd.CommandText = block;
            await cmd.ExecuteNonQueryAsync();
        });

        var watcher = await RawAsync();
        string? sqlId = null;
        for (var i = 0; i < 40 && sqlId is null; i++)
        {
            await using var cmd = watcher.CreateCommand();
            cmd.CommandText = "SELECT s.sql_id FROM v$session s JOIN v$sql q ON q.sql_id = s.sql_id AND q.child_number = s.sql_child_number WHERE q.sql_text LIKE :t AND s.status = 'ACTIVE'";
            cmd.Parameters.Add(new OracleParameter("t", OracleDbType.Varchar2) { Value = "BEGIN /* " + tag + " */%" });
            sqlId = await cmd.ExecuteScalarAsync() as string;
            if (sqlId is null)
            {
                await Task.Delay(100);
            }
        }

        Assert.NotNull(sqlId);
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource);
        using var cred = new SecureCredential(_db.SystemPassword);
        var monitor = await _manager.OpenAsync(profile, cred, SessionPurpose.Monitor, default);
        var service = Service();

        var samples = new List<WaitSample>();
        for (var i = 0; i < 3; i++)
        {
            samples.AddRange(await service.SampleWaitsAsync(monitor, sqlId, default));
            await Task.Delay(300);
        }

        Assert.NotEmpty(samples);
        Assert.All(samples, s => Assert.Equal(sqlId, s.SqlId));
        var events = WaitBreakdown.FromSamples(samples);
        var top = events[0];
        Assert.Equal("PL/SQL lock timer", top.Event);
        Assert.Equal("Idle", top.WaitClass);
        Assert.Equal(1, top.Sessions);
        Assert.Equal(1.0, events.Sum(e => e.Share), 6);
        Assert.Equal(WaitTone.Other, WaitBreakdown.Classes(events).Single().Tone);

        await running;
        Assert.Empty(await service.SampleWaitsAsync(monitor, sqlId, default));
    }

    [Fact]
    public async Task Awr_plan_steps_are_not_sent_without_a_declared_pack()
    {
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource);
        using var cred = new SecureCredential(_db.SystemPassword);
        var none = await _manager.OpenAsync(profile, cred, SessionPurpose.Monitor, default);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().GetAwrPlanAsync(none, "0000000000000", 1, default));
    }
}
