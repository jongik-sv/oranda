using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Language;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Plans;
using Oranda.Oracle.Sessions;
using Oranda.Oracle.Tuning;

namespace Oranda.Oracle.IntegrationTests.Tuning;

/// <summary>
/// 히스토그램 끝점 글(tuning.histogram + ColumnStatValue.HistogramEndpoint): 짧은 문자 열은 endpoint_actual_value 가 비어 endpoint_value(큰 수)만 오고,
/// DATE·TIMESTAMP 는 율리우스 일수, INTERVAL 은 초·달 수다 — 모두 글로 풀려야 한다. 11g(raw 열 없음)와 26ai(endpoint_actual_value_raw) 두 길.
/// </summary>
internal static class HistogramEndpointScenario
{
    public static async Task RunAsync(string dataSource, string systemPassword)
    {
        var table = "ORT_CS_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var profile = new ConnectionProfile("hist-system", "system", dataSource);
        await using var raw = profile.CreateConnection(systemPassword);
        await raw.OpenAsync();
        await ExecAsync(raw, $"CREATE TABLE {table} (c2 VARCHAR2(2), nv NVARCHAR2(10), d DATE, tz TIMESTAMP(3) WITH TIME ZONE, ids INTERVAL DAY(3) TO SECOND(6), iym INTERVAL YEAR(3) TO MONTH)");
        try
        {
            await ExecAsync(raw, $"""
                INSERT INTO {table}
                SELECT DECODE(MOD(level, 3), 0, '**', 1, 'SY', 2, 'AB'),
                       DECODE(MOD(level, 2), 0, N'Zz', 1, N'한글N'),
                       DATE '2024-01-01' + MOD(level, 2) + 0.5,
                       TIMESTAMP '2024-03-04 05:06:07.123 +09:00' + NUMTODSINTERVAL(MOD(level, 2), 'DAY'),
                       NUMTODSINTERVAL(MOD(level, 2) * 1.25, 'DAY') - INTERVAL '0 01:00:00' DAY TO SECOND,
                       NUMTOYMINTERVAL(MOD(level, 2) * 26 - 5, 'MONTH')
                  FROM dual CONNECT BY level <= 60
                """);
            await ExecAsync(raw, "COMMIT");
            await ExecAsync(raw, $"BEGIN DBMS_STATS.GATHER_TABLE_STATS(USER, '{table}', estimate_percent => 100, method_opt => 'FOR ALL COLUMNS SIZE 254'); END;");

            var bus = new ExecBus();
            var manager = new OracleSessionManager(bus);
            using var cred = new SecureCredential(systemPassword);
            await using var session = await manager.OpenAsync(profile, cred, SessionPurpose.Monitor, default);
            var exec = new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), bus, null, pageSize: 500);
            var service = new OracleTuningService(exec, new OracleExecutionPlanService(exec), new LanguageService());

            async Task<string?[]> Labels(string column) =>
                [.. (await service.GetHistogramAsync(session, "SYSTEM", table, column, default)).Select(b => b.EndpointValue)];

            Assert.Equal(["**", "AB", "SY"], await Labels("C2"));
            // N'한글N': actual(_raw)가 있으면 「한글N」, 없으면 endpoint_value 앞 바이트(「한글…」). 26ai 도 값 가짓수에 따라 actual 을 비운다.
            var nv = await Labels("NV");
            Assert.Equal("Zz", nv[0]);
            Assert.True(nv[1] is "한글N" or "한글…", nv[1]);
            Assert.Equal(["2024-01-01 12:00:00", "2024-01-02 12:00:00"], await Labels("D"));
            Assert.Equal(["2024-03-03 20:06:07 UTC", "2024-03-04 20:06:07 UTC"], await Labels("TZ"));
            Assert.Equal(["-0 01:00:00", "+1 05:00:00"], await Labels("IDS"));
            Assert.Equal(["-0-05", "+1-09"], await Labels("IYM"));
        }
        finally
        {
            await ExecAsync(raw, $"DROP TABLE {table} PURGE");
        }
    }

    private static async Task ExecAsync(OracleConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}

[Trait("Category", "Integration")]
[Collection(Oracle11gShared.Name)]
public sealed class HistogramEndpoint11gTests(Oracle11gFixture db)
{
    [Fact]
    public Task Endpoint_values_are_shown_as_text_on_11g() => HistogramEndpointScenario.RunAsync(db.DataSource, db.SystemPassword);
}

[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class HistogramEndpointFreeTests(OracleFreeFixture db)
{
    [Fact]
    public Task Endpoint_values_are_shown_as_text_on_26ai() => HistogramEndpointScenario.RunAsync(db.DataSource, db.SystemPassword);
}
