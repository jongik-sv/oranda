using System.Globalization;
using System.Text.Json.Nodes;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using Oranda.Language;
using Oranda.Language.Ai.Handlers;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.Plans;
using Oranda.Oracle.Sessions;
using Oranda.Oracle.Tuning;

namespace Oranda.Oracle.IntegrationTests.Ai;

/// <summary>
/// 사용자 함수가 든 SQL(c7): 추출기가 함수 이름을 찾고, 검사기가 딕셔너리를 걸어 자치 트랜잭션·외부 패키지를 판정한다.
/// 안전하면 benchmark_queries 가 읽기 전용 트랜잭션 안에서 실행하고, 위험하면 실행하지 않고 공유 풀 커서 실측으로 대체한다(게이트가 정한 Substitutions).
/// 「위험 함수도 실행」 을 켜면(게이트가 ReadOnlyExecution 만 준다) 위험 함수도 읽기 전용으로 실행한다 — 자치 트랜잭션의 커밋은 막지 못한다.
/// 함수·표는 이 시험이 만든 새 계정(AIT_UF_*)에 만들고 끝에 계정째 지운다. 값(비밀번호)은 출력하지 않는다.
/// 환경 변수가 없으면 아무것도 안 한다(ORANDA_IT_REQUIRE=1 이면 픽스처가 실패).
/// </summary>
[Trait("Category", "Integration")]
public sealed class AiUserFunctionIntegrationTests(AiToolsDbFixture db) : IClassFixture<AiToolsDbFixture>
{
    [Fact]
    public async Task Autonomous_chain_is_substituted_without_running_and_runs_only_when_risky_functions_are_allowed()
    {
        if (!db.Configured)
        {
            return;
        }

        await WithAccountAsync(async a =>
        {
            const string sql = "SELECT FN_OUTER(1) FROM dual";
            var info = await db.Extractor.AnalyzeAsync(a.Session, sql, a.User, default);
            Assert.Contains("FN_OUTER", info.UserFunctions ?? [], StringComparer.OrdinalIgnoreCase);
            Assert.Equal(UserFunctionVerdict.Autonomous, await a.Checker.CheckAsync(a.Session, info.UserFunctions!, a.User, default));

            var h = new BenchmarkQueriesHandler(db.Boxes, db.Extractor);
            var substituted = await h.ExecuteAsync(a.Invoke(Args(sql), substitute: "user_function_autonomous"), default);

            Assert.False(substituted.IsError, substituted.ErrorMessage);
            var item = Assert.Single(substituted.Result!.AsArray())!;
            Assert.Equal("cursor_stats", item["substituted"]!.GetValue<string>());
            Assert.Equal(0, await a.CountLogAsync()); // 검사·대체는 함수를 부르지 않는다

            // 「위험 함수도 실행」: 읽기 전용으로 실행되지만 자치 트랜잭션은 따로 커밋한다(시작 창 안내 그대로).
            var risky = await h.ExecuteAsync(a.Invoke(Args(sql)), default);
            Assert.False(risky.IsError, risky.ErrorMessage);
            Assert.True(await a.CountLogAsync() > 0);
        });
    }

    [Fact]
    public async Task External_function_is_substituted_with_the_pool_cursor_measured_by_the_application()
    {
        if (!db.Configured)
        {
            return;
        }

        await WithAccountAsync(async a =>
        {
            const string sql = "SELECT FN_NET(1) FROM dual";
            var info = await db.Extractor.AnalyzeAsync(a.Session, sql, a.User, default);
            Assert.Contains("FN_NET", info.UserFunctions ?? [], StringComparer.OrdinalIgnoreCase);
            Assert.Equal(UserFunctionVerdict.External, await a.Checker.CheckAsync(a.Session, info.UserFunctions!, a.User, default));

            // 앱이 운영에서 같은 글을 이미 돌렸다(공유 풀에 이 계정이 파싱한 커서가 있다).
            await a.RunAsOwnerAsync(sql);

            var r = await new BenchmarkQueriesHandler(db.Boxes, db.Extractor).ExecuteAsync(a.Invoke(Args(sql), substitute: "user_function_external"), default);

            Assert.False(r.IsError, r.ErrorMessage);
            var item = Assert.Single(r.Result!.AsArray())!;
            Assert.Equal("user_function_external", item["reason"]!.GetValue<string>());
            Assert.True(item["executions"]!.GetValue<long>() >= 1);
            Assert.Contains("운영 커서 실측", item["note"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Single(r.SqlIds);
        });
    }

    [Fact]
    public async Task Safe_function_benchmarks_read_only_even_after_an_explain_or_on_a_read_only_profile_and_dml_inside_fails_leaving_nothing()
    {
        if (!db.Configured)
        {
            return;
        }

        await WithAccountAsync(async a =>
        {
            Assert.Equal(UserFunctionVerdict.Safe, await a.Checker.CheckAsync(a.Session, ["FN_PLAIN"], a.User, default));
            // 비자치 DML 함수는 검사기로는 안전하다. 막는 것은 DB(ORA-14551 질의 안 DML, 또는 ORA-01456 읽기 전용 트랜잭션)다.
            Assert.Equal(UserFunctionVerdict.Safe, await a.Checker.CheckAsync(a.Session, ["FN_DML"], a.User, default));

            var h = new BenchmarkQueriesHandler(db.Boxes, db.Extractor);
            // 같은 세션에서 앞선 EXPLAIN PLAN 이 PLAN_TABLE 에 행을 써 트랜잭션이 열려 있어도 읽기 전용 시작이 실패하지 않는다(ORA-01453).
            var explain = await new ExplainPlanHandler(db.Boxes, db.Extractor).ExecuteAsync(a.Invoke(new JsonObject { ["sql"] = "SELECT FN_PLAIN(1) FROM dual" }), default);
            Assert.False(explain.IsError, explain.ErrorMessage);
            var plain = await h.ExecuteAsync(a.Invoke(Args("SELECT FN_PLAIN(level) FROM dual CONNECT BY level <= 3")), default);
            var dml = await h.ExecuteAsync(a.Invoke(Args("SELECT FN_DML(1) FROM dual")), default);
            var after = await h.ExecuteAsync(a.Invoke(Args("SELECT FN_PLAIN(1) FROM dual")), default);

            Assert.False(plain.IsError, plain.ErrorMessage);
            Assert.Equal(3, plain.Result![0]!["rows"]!.GetValue<long>());
            Assert.True(dml.IsError);
            Assert.Matches(@"ORA-(14551|01456)", dml.ErrorMessage);
            Assert.Equal(0, await a.CountLogAsync());
            Assert.False(after.IsError, after.ErrorMessage); // 실패 뒤에도 세션은 정상이다
            Assert.Equal(1, after.Result![0]!["rows"]!.GetValue<long>());

            // 읽기 전용 트랜잭션이 실제로 열린다: 트랜잭션이 없으면 오류를 내는 함수가 읽기 전용 실행에서만 통과한다.
            var tuning = a.Tuning();
            var ro = await tuning.RunBenchmarkAsync(a.Session, new BenchmarkCase("tx", "SELECT FN_TXN FROM dual") { Source = HistorySource.Agent, ReadOnly = true }, 1, default);
            Assert.Equal(1, ro.Rows);
            await Assert.ThrowsAnyAsync<Exception>(() => tuning.RunBenchmarkAsync(a.Session, new BenchmarkCase("tx", "SELECT FN_TXN FROM dual") { Source = HistorySource.Agent }, 1, default));

            // 읽기 전용 프로필: 실행 서비스가 문장마다 읽기 전용 트랜잭션을 열므로 SET TRANSACTION 을 다시 하지 않는다.
            var roSession = await a.OpenAsync(a.Profile with { ReadOnly = true });
            var onReadOnly = await h.ExecuteAsync(a.Invoke(Args("SELECT FN_PLAIN(1) FROM dual"), session: roSession), default);
            Assert.False(onReadOnly.IsError, onReadOnly.ErrorMessage);
        });
    }

    private static JsonObject Args(string sql) => new() { ["variants"] = new JsonArray(new JsonObject { ["label"] = "a", ["sql"] = sql }), ["repeat"] = 1 };

    private async Task WithAccountAsync(Func<Account, Task> body)
    {
        var user = "AIT_UF_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var password = "Uf" + Guid.NewGuid().ToString("N");
        var adminPassword = Environment.GetEnvironmentVariable("ORANDA_IT_ADMIN_PASSWORD")!;

        await using var admin = new OracleConnection(db.Profile.BuildConnectionString(adminPassword));
        await admin.OpenAsync();
        var bus = new ExecBus();
        var manager = new OracleSessionManager(bus);
        try
        {
            await ExecAsync(admin, $"CREATE USER {user} IDENTIFIED BY \"{password}\" QUOTA 10M ON USERS");
            await ExecAsync(admin, $"GRANT CREATE SESSION, CREATE TABLE, CREATE PROCEDURE TO {user}");
            await ExecAsync(admin, $"GRANT SELECT_CATALOG_ROLE TO {user}"); // 커서 실측(V$SQL) 대체를 보려면 필요하다

            var profile = new ConnectionProfile("it-user-fn", user, db.Profile.DataSource);
            await using (var own = new OracleConnection(profile.BuildConnectionString(password)))
            {
                await own.OpenAsync();
                await ExecAsync(own, "CREATE TABLE T_LOG (n NUMBER)");
                await ExecAsync(own, "CREATE PROCEDURE P_AUTO IS PRAGMA AUTONOMOUS_TRANSACTION; BEGIN INSERT INTO T_LOG VALUES (1); COMMIT; END;");
                await ExecAsync(own, "CREATE FUNCTION FN_INNER(x NUMBER) RETURN NUMBER IS BEGIN P_AUTO; RETURN x; END;");
                await ExecAsync(own, "CREATE FUNCTION FN_OUTER(x NUMBER) RETURN NUMBER IS BEGIN RETURN FN_INNER(x); END;");
                await ExecAsync(own, "CREATE FUNCTION FN_NET(x NUMBER) RETURN NUMBER IS v VARCHAR2(2000); BEGIN IF x < 0 THEN v := UTL_HTTP.REQUEST('http://127.0.0.1:9/'); END IF; RETURN x; END;");
                await ExecAsync(own, "CREATE FUNCTION FN_PLAIN(x NUMBER) RETURN NUMBER IS BEGIN RETURN x * 2; END;");
                await ExecAsync(own, "CREATE FUNCTION FN_DML(x NUMBER) RETURN NUMBER IS BEGIN INSERT INTO T_LOG VALUES (x); RETURN x; END;");
                await ExecAsync(own, "CREATE FUNCTION FN_TXN RETURN NUMBER IS BEGIN IF DBMS_TRANSACTION.LOCAL_TRANSACTION_ID IS NULL THEN RAISE_APPLICATION_ERROR(-20001, 'no transaction'); END IF; RETURN 1; END;");
                await using var check = own.CreateCommand();
                check.CommandText = "SELECT COUNT(*) FROM user_objects WHERE status <> 'VALID'";
                Assert.Equal(0, Convert.ToInt32(await check.ExecuteScalarAsync(), CultureInfo.InvariantCulture)); // UTL_HTTP 권한이 없으면 FN_NET 이 무효다
            }

            var exec = new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), bus, null, pageSize: 500);
            var account = new Account(user, password, profile, manager, exec, admin);
            account.Session = await account.OpenAsync(profile);
            await body(account);
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

    private sealed class Account(string user, string password, ConnectionProfile profile, OracleSessionManager manager, OracleExecutionService exec, OracleConnection admin)
    {
        public string User => user;

        public ConnectionProfile Profile => profile;

        public ISession Session { get; set; } = null!;

        public UserFunctionSafetyChecker Checker { get; } = new(exec);

        public OracleTuningService Tuning() => new(exec, new OracleExecutionPlanService(exec), new LanguageService());

        /// <summary>에이전트처럼 모니터 세션을 연다.</summary>
        public async Task<ISession> OpenAsync(ConnectionProfile p)
        {
            using var credential = new SecureCredential(password);
            return await manager.OpenAsync(p, credential, SessionPurpose.Monitor, default);
        }

        /// <summary>
        /// 게이트를 거치지 않는 시험이라 게이트가 채우는 값을 같은 규칙으로 채운다: 대체 사유가 있으면 그 변형은 실행하지 않고,
        /// 없으면 사용자 함수가 확인을 통과했거나 「위험 함수도 실행」 이 켜진 경우처럼 읽기 전용으로 실행한다.
        /// </summary>
        public AgentToolInvocation Invoke(JsonObject args, string? substitute = null, ISession? session = null)
        {
            var db = session ?? Session;
            var run = new AgentRunScope("it-run", db.Profile, new AiContext("it-user-fn", ConnectionEnvironment.Development, AiAccessLevel.Query, CurrentSchema: User));
            return new AgentToolInvocation(
                new AgentSession(run), db, AiAccessLevel.Query, args, new Dictionary<string, AgentObjectRef>(),
                ExplainPlanAllowed: true, MayExecuteSql: true, ReadOnlyExecution: substitute is null, Substitutions: substitute is null ? null : [substitute]);
        }

        /// <summary>앱이 운영에서 돌린 것처럼 이 계정 이름으로 문장을 한 번 실행한다(공유 풀에 이 계정이 파싱한 커서를 남긴다).</summary>
        public async Task RunAsOwnerAsync(string sql)
        {
            await using var conn = new OracleConnection(profile.BuildConnectionString(password));
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
            }
        }

        public async Task<int> CountLogAsync()
        {
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {User}.T_LOG";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }
    }
}
