using System.Globalization;
using System.Text.Json.Nodes;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Language.Ai.Handlers;
using Oranda.Oracle.Connection;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.IntegrationTests.Ai;

/// <summary>
/// V$SQL 조회 권한이 없는 일반 계정(CREATE SESSION + 시험 표 SELECT 뿐)으로 <c>get_tuning_context</c> 를 부른다.
/// 이전에는 ORA-00942 가 전체를 실패시켰다. 이제 객체 정보는 돌려주고 omitted 에 이유를 적으며 sql_id 는 알리지 않는다.
/// 계정은 이 시험이 만들고 끝에 지운다. 값(비밀번호)은 출력하지 않는다. 환경 변수가 없으면 아무것도 안 한다(ORANDA_IT_REQUIRE=1 이면 픽스처가 실패).
/// </summary>
[Trait("Category", "Integration")]
public sealed class AiToolsNoSqlViewIntegrationTests(AiToolsDbFixture db) : IClassFixture<AiToolsDbFixture>
{
    [Fact]
    public async Task Tuning_context_for_an_account_without_v_sql_returns_objects_and_names_the_missing_privilege()
    {
        if (!db.Configured)
        {
            return;
        }

        var user = "AIT_NP_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var password = "Np" + Guid.NewGuid().ToString("N");
        var adminPassword = Environment.GetEnvironmentVariable("ORANDA_IT_ADMIN_PASSWORD")!;

        await using var admin = new OracleConnection(db.Profile.BuildConnectionString(adminPassword));
        await admin.OpenAsync();
        var manager = new OracleSessionManager(new ExecBus());
        try
        {
            await ExecAsync(admin, $"CREATE USER {user} IDENTIFIED BY \"{password}\"");
            await ExecAsync(admin, $"GRANT CREATE SESSION TO {user}");
            await ExecAsync(admin, $"GRANT SELECT ON {db.Owner}.{db.Order} TO {user}");
            await ExecAsync(admin, $"GRANT SELECT ON {db.Owner}.{db.Dept} TO {user}");

            var profile = new ConnectionProfile("it-no-sqlview", user, db.Profile.DataSource);
            using var credential = new SecureCredential(password);
            var session = await manager.OpenAsync(profile, credential, SessionPurpose.Monitor, default);

            var sql = $"SELECT stat_cd FROM {db.Owner}.{db.Order} WHERE stat_cd = 'B'";
            var run = new AgentRunScope("it-run", profile, new AiContext("it-no-sqlview", ConnectionEnvironment.Development, AiAccessLevel.Plan, StatementText: sql, CurrentSchema: user));
            var invocation = new AgentToolInvocation(
                new AgentSession(run), session, AiAccessLevel.Plan, new JsonObject { ["budget_tokens"] = 4000 }, new Dictionary<string, AgentObjectRef>(),
                ExplainPlanAllowed: true);

            var output = await new GetTuningContextHandler(db.Boxes, db.Extractor).ExecuteAsync(invocation, default);

            Assert.False(output.IsError, output.ErrorMessage);
            var r = output.Result!;
            Assert.Null(r["statement"]?["sql_id"]);
            Assert.Empty(output.SqlIds);
            Assert.EndsWith(db.Order, Assert.Single(r["objects"]!.AsArray())!["name"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Contains(r["omitted"]!.AsArray(), o => o!.GetValue<string>() == "v$sql: 권한 없음");
        }
        finally
        {
            foreach (var s in manager.Sessions)
            {
                await s.DisposeAsync();
            }

            try
            {
                await ExecAsync(admin, $"DROP USER {user} CASCADE");
            }
            catch (OracleException)
            {
                // 만들다 만 계정은 없을 수 있다.
            }
        }
    }

    private static async Task ExecAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
