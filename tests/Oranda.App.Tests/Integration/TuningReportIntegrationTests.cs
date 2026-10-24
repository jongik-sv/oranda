using Oracle.ManagedDataAccess.Client;
using Oranda.App.Features.Tuning.Report;
using Oranda.App.Shell.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Monitoring;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;

namespace Oranda.App.Tests.Integration;

/// <summary>
/// TSK-06-07 튜닝 보고서 통합: 실제 문장 하나를 실행한 뒤 앱 조립의 서비스(OracleTuningService·모니터)로 모든 절을 모아 HTML 을 만든다.
/// 26ai = <see cref="ItDb"/>(ORANDA_IT_ADMIN_PASSWORD). 11g = ORANDA_IT_11G_DATASOURCE·ORANDA_IT_11G_SYSTEM_PASSWORD(예 127.0.0.1:1522/XE).
/// 환경 변수가 없으면 아무것도 하지 않는다. 표 만들기·데이터 넣기·통계 수집은 시험 준비라 시험 안에서만 한다.
/// <c>ORANDA_RENDER_DIR</c> 이 있으면 만든 보고서를 남긴다.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TuningReportIntegrationTests
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    [Fact]
    public async Task Report_from_a_real_statement_on_26ai()
    {
        using var db = new ItDb();
        if (!db.Enabled)
        {
            return;
        }

        var admin = $"User Id={Env("ADMIN_USER") ?? "SYSTEM"};Password={Env("ADMIN_PASSWORD")};Data Source={db.DataSource};Pooling=false";
        await RunAsync(db.UserConnectionString, admin, db.DataSource, Env("ADMIN_USER") ?? "SYSTEM", Env("ADMIN_PASSWORD")!, db.User.ToUpperInvariant(), "it-26ai");
    }

    [Fact]
    public async Task Report_from_a_real_statement_on_11g()
    {
        var ds = Env("11G_DATASOURCE");
        var pw = Env("11G_SYSTEM_PASSWORD");
        if (string.IsNullOrEmpty(ds) || string.IsNullOrEmpty(pw))
        {
            return;
        }

        var admin = $"User Id=SYSTEM;Password={pw};Data Source={ds};Pooling=false";
        await RunAsync(admin, admin, ds, "SYSTEM", pw, "SYSTEM", "it-11g");
    }

    private static async Task RunAsync(string ownerConn, string adminConn, string dataSource, string monitorUser, string monitorPassword, string owner, string name)
    {
        var table = "TNRPT_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var raw = new OracleConnection(ownerConn);
        await raw.OpenAsync();
        await ExecAsync(raw, $"CREATE TABLE {table} (id NUMBER PRIMARY KEY, grp VARCHAR2(10), pad VARCHAR2(60))");
        var dir = Path.Combine(Path.GetTempPath(), "oranda-tnrpt-" + Guid.NewGuid().ToString("N"));
        try
        {
            await ExecAsync(raw, $"INSERT INTO {table} SELECT level, 'G' || MOD(level, 7), RPAD('x', 50, 'x') FROM dual CONNECT BY level <= 5000");
            await ExecAsync(raw, "COMMIT");
            await ExecAsync(raw, $"BEGIN DBMS_STATS.GATHER_TABLE_STATS(USER, '{table}', method_opt => 'FOR ALL COLUMNS SIZE 1 FOR COLUMNS SIZE 254 GRP'); END;");

            // 실제 문장 한 번(바인드 하나): 실제 계획 통계를 남기려고 gather_plan_statistics.
            var sql = $"SELECT /*+ gather_plan_statistics */ COUNT(*), MAX(pad) FROM {table} WHERE grp = :g AND id > 10";
            await using (var cmd = raw.CreateCommand())
            {
                cmd.BindByName = true;
                cmd.CommandText = sql;
                cmd.Parameters.Add(new OracleParameter("g", OracleDbType.Varchar2) { Value = "G3<&>" });
                await cmd.ExecuteScalarAsync();
            }

            var sqlId = SqlIdHash.Compute(sql);
            await using var adm = new OracleConnection(adminConn);
            await adm.OpenAsync();
            await using (var check = adm.CreateCommand())
            {
                check.BindByName = true;
                check.CommandText = "SELECT MAX(sql_id) FROM v$sql WHERE sql_text = :t";
                check.Parameters.Add(new OracleParameter("t", OracleDbType.Varchar2) { Value = sql });
                Assert.Equal(sqlId, await check.ExecuteScalarAsync() as string); // 글로 셈한 sql_id 가 서버 값과 같다
            }

            Directory.CreateDirectory(dir);
            var container = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir });
            container.ScanDeferred();
            var manager = container.GetRequiredService<ISessionManager>();
            var profile = new ConnectionProfile(name, monitorUser, dataSource, Environment: ConnectionEnvironment.Production);
            ISession session;
            using (var cred = new SecureCredential(monitorPassword))
            {
                session = await manager.OpenAsync(profile, cred, SessionPurpose.Monitor, default);
            }

            await using (session)
            {
                var collector = new TuningReportCollector(container.GetRequiredService<ITuningService>(), container.GetRequiredService<IMonitorService>())
                {
                    Delay = (_, ct) => Task.Delay(50, ct),
                };
                var options = new TuningReportOptions { SqlId = sqlId, WaitSampleSeconds = 2, IncludeBindValues = false };
                var data = await collector.CollectAsync(session, options, null, default);

                Assert.Equal(sql, data.SqlText.Value!.Text.Trim());
                Assert.Contains(data.SqlText.Value.Binds, b => b.Name.Equals(":G", StringComparison.OrdinalIgnoreCase) && b.Value is null);
                Assert.True(data.Cursors.HasValue, data.Cursors.Failure);
                Assert.NotEmpty(data.Cursors.Value!.Children);
                Assert.True(data.Plans.HasValue, data.Plans.Failure);
                Assert.Contains(data.Plans.Value!, p => p.Plan is { Steps.Count: > 0 });
                Assert.True(data.ExecStats.HasValue, data.ExecStats.Failure);
                Assert.True(data.ExecStats.Value!.Executions >= 1);
                Assert.True(data.Objects.HasValue, data.Objects.Failure);
                var stats = Assert.Single(data.Objects.Value!, o => o.Table == table);
                Assert.Equal(5000, stats.Stats!.NumRows);
                Assert.Contains(stats.Histograms, h => h.Column == "GRP");
                Assert.True(data.Environment.HasValue, data.Environment.Failure);
                Assert.Contains(data.Environment.Value!, e => e.Kind == TuningEnvKind.Parameter && e.Name == "optimizer_mode");
                Assert.Contains(data.Environment.Value!, e => e.Kind == TuningEnvKind.Nls && e.Name == "NLS_CHARACTERSET");
                Assert.Contains(data.Environment.Value!, e => e.Kind == TuningEnvKind.Version);
                Assert.True(data.Waits.HasValue, data.Waits.Failure);
                Assert.NotNull(data.Awr.Excluded); // 팩 선언 없음 → 보내지 않음
                Assert.True(data.Candidates.HasValue, data.Candidates.Failure);

                var html = TuningReportHtml.Build(data);
                Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("G3&lt;&amp;&gt;", html, StringComparison.Ordinal); // 바인드 값은 빼고 만들었다
                Assert.Contains(table, html, StringComparison.Ordinal);

                var withBinds = await collector.CollectAsync(session, options with { IncludeBindValues = true }, null, default);
                var bind = withBinds.SqlText.Value!.Binds.First(b => b.Name.Equals(":G", StringComparison.OrdinalIgnoreCase));
                if (bind.Value is not null)
                {
                    Assert.Contains("G3&lt;&amp;&gt;", TuningReportHtml.Build(withBinds), StringComparison.Ordinal);
                }

                // 모으는 동안 이 세션에 트랜잭션이 생기지 않았다(DB 에 쓰지 않음).
                await using (var tx = adm.CreateCommand())
                {
                    tx.BindByName = true;
                    tx.CommandText = "SELECT COUNT(*) FROM v$transaction t JOIN v$session s ON s.taddr = t.addr WHERE s.sid = :sid";
                    tx.Parameters.Add(new OracleParameter("sid", OracleDbType.Int64) { Value = session.Server.SessionId });
                    Assert.Equal(0m, Convert.ToDecimal(await tx.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
                }

                if (Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR") is { Length: > 0 } outDir)
                {
                    Directory.CreateDirectory(outDir);
                    await File.WriteAllTextAsync(Path.Combine(outDir, $"{name}-report.html"), html);
                }
            }

            container.Dispose();
        }
        finally
        {
            await ExecAsync(raw, $"DROP TABLE {table} PURGE");
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task ExecAsync(OracleConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
