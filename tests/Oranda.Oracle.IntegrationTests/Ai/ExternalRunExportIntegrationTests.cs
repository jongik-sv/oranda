using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Language;
using Oranda.Language.Ai.Handlers;
using Oranda.Language.Ai.Handlers.External;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.IntegrationTests.Metadata;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Plans;
using Oranda.Oracle.Sessions;
using Oranda.Oracle.Tuning;
using Xunit.Abstractions;

namespace Oranda.Oracle.IntegrationTests.Ai;

// 소유: agent-cli-1122 레인 w-it-p2(R5). run_tab·export_result 가 함께 쓰는 문장 가드(ExternalSelectGuard)를 실제 26ai 로 본다.
// run_tab·export_result 본체는 App 처리자라 이 프로젝트(App 참조 없음)에서 부르지 못한다 → 본체 왕복은 App.Tests/Integration/ExternalRunExportFlowTests.

/// <summary>
/// 시험 계정 둘(<c>ORT_AC_RA_</c>·<c>ORT_AC_RB_</c> + 실행마다 고유 접미사, 끝에 계정째 지운다).
/// A = 접속 계정: 표 <c>T_ORD</c>(30행)·시퀀스 <c>SQ</c>·안전 함수 <c>FN_PLAIN</c>·자치 트랜잭션 함수 <c>FN_AUTO</c>.
/// B = 다른 스키마: 같은 이름의 <c>FN_PLAIN</c> 이지만 자치 트랜잭션으로 <c>B.T_LOG</c> 에 한 줄 쓴다(A 에 EXECUTE 허가). 같은 이름 표 <c>T_SAME</c>(A 값 'A', B 값 'B'). CURRENT_SCHEMA 확인용.
/// 사전 뷰 확인용(w-it-dict): 비 DBA 계정 <c>ORT_AC_RC_</c>(SELECT_CATALOG_ROLE)·<c>ORT_AC_RD_</c>(SELECT ANY DICTIONARY), A 의 개인 시노님 <c>S_SAME</c>·PUBLIC 시노님 <c>ORT_AC_PS_</c> → B.T_SAME.
/// 환경 변수: ORANDA_IT_HOST, ORANDA_IT_PORT, ORANDA_IT_SERVICE, ORANDA_IT_ADMIN_USER, ORANDA_IT_ADMIN_PASSWORD(없으면 건너뜀, ORANDA_IT_REQUIRE=1 이면 실패).
/// 비밀번호는 출력하지 않는다.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "xUnit 이 IAsyncLifetime.DisposeAsync 로 정리한다.")]
public sealed class ExternalRunGuardDbFixture : IAsyncLifetime
{
    private readonly ExecBus _bus = new();
    private OracleSessionManager? _manager;
    private OracleConnection? _admin;
    private string _password = string.Empty;
    private string _adminPassword = string.Empty;

    public bool Configured { get; private set; }

    public string UserA { get; private set; } = string.Empty;

    public string UserB { get; private set; } = string.Empty;

    /// <summary>SELECT_CATALOG_ROLE 만 있는 계정.</summary>
    public string UserCatalog { get; private set; } = string.Empty;

    /// <summary>SELECT ANY DICTIONARY 만 있는 계정.</summary>
    public string UserDictionary { get; private set; } = string.Empty;

    /// <summary>B.T_SAME 을 가리키는 PUBLIC 시노님.</summary>
    public string PublicSynonym { get; private set; } = string.Empty;

    private string AdminUser { get; set; } = string.Empty;

    private string DataSource { get; set; } = string.Empty;

    public ConnectionProfile Profile { get; private set; } = null!;

    internal OracleExecutionService Exec { get; private set; } = null!;

    internal LanguageService Language { get; } = new();

    internal IExternalSelectGuard Guard { get; private set; } = null!;

    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    public async Task InitializeAsync()
    {
        if (Env("HOST") is null || Env("SERVICE") is null || Env("ADMIN_USER") is not { } admin || Env("ADMIN_PASSWORD") is not { } adminPassword)
        {
            Assert.False(Env("REQUIRE") == "1", "ORANDA_IT_HOST/SERVICE/ADMIN_USER/ADMIN_PASSWORD 가 없다.");
            return;
        }

        Configured = true;
        var sfx = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        (UserA, UserB) = ("ORT_AC_RA_" + sfx, "ORT_AC_RB_" + sfx);
        (UserCatalog, UserDictionary, PublicSynonym) = ("ORT_AC_RC_" + sfx, "ORT_AC_RD_" + sfx, "ORT_AC_PS_" + sfx);
        _password = "It#" + Guid.NewGuid().ToString("N")[..12] + "a1";
        _adminPassword = adminPassword;
        AdminUser = admin;
        var dataSource = $"{Env("HOST")}:{Env("PORT") ?? "1521"}/{Env("SERVICE")}";
        DataSource = dataSource;
        _admin = new OracleConnection(new ConnectionProfile("it-admin", admin, dataSource).BuildConnectionString(adminPassword));
        await _admin.OpenAsync();
        foreach (var u in new[] { UserA, UserB })
        {
            await ExecAsync(_admin, $"CREATE USER {u} IDENTIFIED BY \"{_password}\" QUOTA 10M ON USERS");
            await ExecAsync(_admin, $"GRANT CREATE SESSION, CREATE TABLE, CREATE PROCEDURE, CREATE SEQUENCE, CREATE SYNONYM TO {u}");
        }

        foreach (var u in new[] { UserCatalog, UserDictionary })
        {
            await ExecAsync(_admin, $"CREATE USER {u} IDENTIFIED BY \"{_password}\"");
            await ExecAsync(_admin, $"GRANT CREATE SESSION TO {u}");
        }

        await ExecAsync(_admin, $"GRANT SELECT_CATALOG_ROLE TO {UserCatalog}");
        await ExecAsync(_admin, $"GRANT SELECT ANY DICTIONARY TO {UserDictionary}");

        await ExecAsync(_admin, $"CREATE TABLE {UserB}.T_LOG (n NUMBER)");
        await ExecAsync(_admin, $"CREATE FUNCTION {UserB}.FN_PLAIN(x NUMBER) RETURN NUMBER IS PRAGMA AUTONOMOUS_TRANSACTION; BEGIN INSERT INTO {UserB}.T_LOG VALUES (x); COMMIT; RETURN x; END;");
        await ExecAsync(_admin, $"GRANT EXECUTE ON {UserB}.FN_PLAIN TO {UserA}");
        await ExecAsync(_admin, $"CREATE TABLE {UserB}.T_SAME (src VARCHAR2(1))"); // A 에도 같은 이름 표(값이 다르다)
        await ExecAsync(_admin, $"INSERT INTO {UserB}.T_SAME VALUES ('B')");
        await ExecAsync(_admin, "COMMIT");
        await ExecAsync(_admin, $"GRANT SELECT ON {UserB}.T_SAME TO {UserA}");
        await ExecAsync(_admin, $"CREATE PUBLIC SYNONYM {PublicSynonym} FOR {UserB}.T_SAME");

        Profile = new ConnectionProfile("it-run-guard", UserA, dataSource);
        await using (var own = new OracleConnection(Profile.BuildConnectionString(_password)))
        {
            await own.OpenAsync();
            await ExecAsync(own, "CREATE TABLE T_ORD (ord_id NUMBER PRIMARY KEY, ord_nm VARCHAR2(30))");
            await ExecAsync(own, "INSERT INTO T_ORD SELECT level, 'N' || level FROM dual CONNECT BY level <= 30");
            await ExecAsync(own, "COMMIT");
            await ExecAsync(own, "CREATE SEQUENCE SQ");
            await ExecAsync(own, "CREATE TABLE T_SAME (src VARCHAR2(1))");
            await ExecAsync(own, "INSERT INTO T_SAME VALUES ('A')");
            await ExecAsync(own, "COMMIT");
            await ExecAsync(own, "CREATE TABLE T_LOG (n NUMBER)");
            await ExecAsync(own, "CREATE FUNCTION FN_PLAIN(x NUMBER) RETURN NUMBER IS BEGIN RETURN x * 2; END;");
            await ExecAsync(own, $"CREATE SYNONYM S_SAME FOR {UserB}.T_SAME");
            await ExecAsync(own, "CREATE FUNCTION FN_AUTO(x NUMBER) RETURN NUMBER IS PRAGMA AUTONOMOUS_TRANSACTION; BEGIN INSERT INTO T_LOG VALUES (x); COMMIT; RETURN x; END;");
        }

        _manager = new OracleSessionManager(_bus);
        Exec = new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), _bus, null, pageSize: 500);
        var metadata = new OracleMetadataProvider(new MemoryMetadataCache(), _bus, Timeout.InfiniteTimeSpan);
        var extractor = new AgentStatementReferenceExtractor(Language, metadata);
        Guard = new ExternalSelectGuard(Language, extractor, new UserFunctionSafetyChecker(Exec, Oranda.Oracle.Catalog.EmbeddedCatalog.Load()), Exec);
        var plans = new OracleExecutionPlanService(Exec);
        var boxes = new AgentToolboxProvider(new OracleTuningService(Exec, plans, Language), metadata, new OracleTableKeyDetailProvider(), plans, Language);
        Query = new QueryHandler(Language, Exec, extractor, boxes);
    }

    internal QueryHandler Query { get; private set; } = null!;

    /// <summary><c>query</c> 호출(게이트를 거치지 않는다). 실행 범위의 현재 스키마 = <paramref name="contextSchema"/>.</summary>
    public AgentToolInvocation Invoke(ISession session, string sql, string? contextSchema)
    {
        var run = new AgentRunScope("g1:it-run", Profile, new AiContext("it-run", ConnectionEnvironment.Development, AiAccessLevel.QueryRows, CurrentSchema: contextSchema));
        return new AgentToolInvocation(new AgentSession(run), session, AiAccessLevel.QueryRows, new System.Text.Json.Nodes.JsonObject { ["sql"] = sql },
            new Dictionary<string, AgentObjectRef>(), MayExecuteSql: true);
    }

    /// <summary>한 문장을 실행해 첫 행 첫 칸을 글로 읽는다(출처 Agent — 실행이 이름을 어디서 푸는지 본다).</summary>
    public async Task<string?> ExecAndReadAsync(ISession session, string sql)
    {
        var stmt = Assert.Single(Language.Split(sql, Core.Language.SplitOptions.Default));
        var result = await Exec.ExecuteAsync(session, stmt, new Dictionary<string, BindValue>(), new ExecutionOptions { Source = HistorySource.Agent, SkipHistory = true }, default);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        await using var rs = result.ResultSet!;
        var page = await rs.GetPageAsync(0, default);
        return Convert.ToString(page.Columns[0][0], CultureInfo.InvariantCulture);
    }

    /// <summary>A 계정으로 새 작업 세션(탭 세션처럼). 시험이 끝나면 픽스처가 닫는다.</summary>
    public async Task<ISession> OpenAsync()
    {
        using var credential = new SecureCredential(_password);
        return await _manager!.OpenAsync(Profile, credential, SessionPurpose.Work, default);
    }

    /// <summary>다른 시험 계정(<paramref name="user"/>) 또는 관리자(<c>null</c>)로 새 작업 세션.</summary>
    public async Task<ISession> OpenAsAsync(string? user)
    {
        using var credential = new SecureCredential(user is null ? _adminPassword : _password);
        return await _manager!.OpenAsync(new ConnectionProfile("it-run-guard-" + (user ?? "admin"), user ?? AdminUser, DataSource), credential, SessionPurpose.Work, default);
    }

    /// <summary>관리자로 값 하나(앱 세션과 따로).</summary>
    public async Task<long> CountAsync(string sql)
    {
        await using var cmd = _admin!.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    /// <summary>사람이 편집기에서 실행하듯 한 문장(출처 Editor).</summary>
    public async Task<ExecutionResult> RunAsync(ISession session, string sql, HistorySource source = HistorySource.Editor)
    {
        var stmt = Assert.Single(Language.Split(sql, Core.Language.SplitOptions.Default));
        var result = await Exec.ExecuteAsync(session, stmt, new Dictionary<string, BindValue>(), new ExecutionOptions { Source = source, SkipHistory = true }, default);
        if (result.ResultSet is { } rs)
        {
            await using (rs)
            {
                await rs.GetPageAsync(0, default); // 행을 실제로 읽어야 SELECT 안 함수가 돈다
            }
        }

        return result;
    }

    public async Task DisposeAsync()
    {
        if (_manager is not null)
        {
            foreach (var s in _manager.Sessions)
            {
                await s.DisposeAsync();
            }
        }

        if (_admin is null)
        {
            return;
        }

        try
        {
            try
            {
                await ExecAsync(_admin, $"DROP PUBLIC SYNONYM {PublicSynonym}");
            }
            catch (OracleException)
            {
                // 만들기 전에 실패했을 수 있다.
            }

            foreach (var u in new[] { UserA, UserB, UserCatalog, UserDictionary })
            {
                try
                {
                    await ExecAsync(_admin, $"DROP USER {u} CASCADE");
                }
                catch (OracleException)
                {
                    // 만들다 만 계정은 없을 수 있다.
                }
            }
        }
        finally
        {
            await _admin.DisposeAsync();
        }
    }

    private static async Task ExecAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// R5: <c>run_tab</c>·<c>export_result</c> 의 문장 가드(<see cref="IExternalSelectGuard"/>, 탭 실행 조건 = 사용자 함수 확인 + 이름 모두 풀림)를 실제 DB 로.
/// 거부 코드(계획 §3.1): DML = <c>sql_not_single_query</c>, 잠금 절·DB 링크·시퀀스 = <c>sql_unsafe</c>, 자치 트랜잭션 함수 = <c>sql_unsafe</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ExternalRunExportIntegrationTests(ExternalRunGuardDbFixture db, ITestOutputHelper output) : IClassFixture<ExternalRunGuardDbFixture>
{
    // RunTab 처리자의 GuardOptions 와 같은 값(App 형식이라 여기서 다시 적는다).
    private static readonly ExternalSelectOptions TabOptions = new() { VerifyUserFunctions = true, RequireResolvedNames = true };

    [Fact]
    public async Task A_plain_select_and_a_safe_user_function_pass_the_tab_guard()
    {
        if (!db.Configured)
        {
            return;
        }

        var session = await db.OpenAsync();

        var plain = await db.Guard.CheckAsync(session, "SELECT ord_id, ord_nm FROM t_ord ORDER BY ord_id", TabOptions, default);
        var safe = await db.Guard.CheckAsync(session, "SELECT fn_plain(ord_id) FROM t_ord", TabOptions, default);

        Assert.True(plain.Allowed, plain.Code + " " + plain.Message);
        Assert.True(safe.Allowed, safe.Code + " " + safe.Message);
        Assert.Equal(StatementKind.Query, safe.Statement!.Kind);
    }

    [Theory]
    [InlineData("UPDATE t_ord SET ord_nm = 'X' WHERE ord_id = 1", AgentErrorCodes.SqlNotSingleQuery, ExternalSelectRefusal.NotSingleQuery)]
    [InlineData("DELETE FROM t_ord", AgentErrorCodes.SqlNotSingleQuery, ExternalSelectRefusal.NotSingleQuery)]
    [InlineData("SELECT ord_id FROM t_ord WHERE ord_id = 1 FOR UPDATE", AgentErrorCodes.SqlUnsafe, ExternalSelectRefusal.Unsafe)]
    [InlineData("SELECT * FROM t_ord@ort_ac_nolink", AgentErrorCodes.SqlUnsafe, ExternalSelectRefusal.Unsafe)]
    [InlineData("SELECT sq.NEXTVAL FROM dual", AgentErrorCodes.SqlUnsafe, ExternalSelectRefusal.Unsafe)]
    [InlineData("SELECT fn_auto(1) FROM dual", AgentErrorCodes.SqlUnsafe, ExternalSelectRefusal.UserFunction)]
    public async Task Unsafe_statements_are_refused_with_the_contract_code(string sql, string code, ExternalSelectRefusal refusal)
    {
        if (!db.Configured)
        {
            return;
        }

        var session = await db.OpenAsync();

        var check = await db.Guard.CheckAsync(session, sql, TabOptions, default);

        Assert.False(check.Allowed, sql);
        Assert.Equal(code, check.Code);
        Assert.Equal(refusal, check.Refusal);
    }

    [Fact]
    public async Task Checking_does_not_run_the_statement_the_log_and_sequence_stay_untouched()
    {
        if (!db.Configured)
        {
            return;
        }

        var session = await db.OpenAsync();
        var before = await db.CountAsync($"SELECT COUNT(*) FROM {db.UserA}.T_LOG");

        Assert.False((await db.Guard.CheckAsync(session, "SELECT fn_auto(1) FROM dual", TabOptions, default)).Allowed);
        Assert.False((await db.Guard.CheckAsync(session, "SELECT sq.NEXTVAL FROM dual", TabOptions, default)).Allowed);

        Assert.Equal(before, await db.CountAsync($"SELECT COUNT(*) FROM {db.UserA}.T_LOG"));
        Assert.Equal(0, await db.CountAsync($"SELECT COUNT(*) FROM all_sequences WHERE sequence_owner = '{db.UserA}' AND sequence_name = 'SQ' AND last_number > 1"));
    }

    /// <summary>
    /// CURRENT_SCHEMA(m-schema 42c4e5aa 뒤 = 「닫혔는지 확인」): 탭 세션에서 사람이 <c>ALTER SESSION SET CURRENT_SCHEMA = B</c> 를 하면
    /// 가드가 검사 직전에 세션의 현재 스키마(B)를 읽어 이름 없는 <c>FN_PLAIN</c> 을 B 의 자치 트랜잭션 함수로 풀고 <c>sql_unsafe</c> 로 거부한다.
    /// (고치기 전 실측 2026-11-23: 가드는 A 의 안전 함수로 풀어 통과시켰고, 같은 세션 실행은 B 함수를 불러 B.T_LOG 에 커밋했다.)
    /// </summary>
    [Fact]
    public async Task Current_schema_altered_session_resolves_the_function_in_the_session_schema_and_refuses_the_autonomous_one()
    {
        if (!db.Configured)
        {
            return;
        }

        var session = await db.OpenAsync();
        Assert.Equal(ExecutionStatus.Succeeded, (await db.RunAsync(session, $"ALTER SESSION SET CURRENT_SCHEMA = {db.UserB}")).Status);
        var before = await db.CountAsync($"SELECT COUNT(*) FROM {db.UserB}.T_LOG");

        var check = await db.Guard.CheckAsync(session, "SELECT fn_plain(7) FROM dual", TabOptions, default);

        Assert.Equal(ExternalSelectRefusal.UserFunction, check.Refusal);
        Assert.Equal(AgentErrorCodes.SqlUnsafe, check.Code);
        Assert.Equal(before, await db.CountAsync($"SELECT COUNT(*) FROM {db.UserB}.T_LOG"));
    }

    /// <summary>
    /// RunTab_checks_object_in_altered_current_schema / RunTab_follows_schema_changed_inside_plsql: A·B 에 같은 이름 표 <c>T_SAME</c>.
    /// 세션 현재 스키마를 B 로 바꾸면(직접 또는 PL/SQL 블록의 EXECUTE IMMEDIATE) 가드는 B.T_SAME 으로 검사하고 실행도 B 를 읽는다. B 에 없는 이름은 실행이 ORA-00942 로 실패(가드 통과는 아래 주석).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunTab_guard_checks_objects_in_the_altered_current_schema_also_when_changed_inside_plsql(bool insidePlsql)
    {
        if (!db.Configured)
        {
            return;
        }

        var session = await db.OpenAsync();
        var alter = insidePlsql
            ? $"BEGIN EXECUTE IMMEDIATE 'ALTER SESSION SET CURRENT_SCHEMA = {db.UserB}'; END;"
            : $"ALTER SESSION SET CURRENT_SCHEMA = {db.UserB}";
        Assert.Equal(ExecutionStatus.Succeeded, (await db.RunAsync(session, alter)).Status);

        var same = await db.Guard.CheckAsync(session, "SELECT * FROM t_same", TabOptions, default);
        var onlyInA = await db.Guard.CheckAsync(session, "SELECT * FROM t_ord", TabOptions, default);
        var qualified = await db.Guard.CheckAsync(session, $"SELECT * FROM {db.UserA}.t_ord", TabOptions, default);
        var read = await db.ExecAndReadAsync(session, "SELECT src FROM t_same");

        Assert.True(same.Allowed, same.Code);
        Assert.Equal("B", read); // 검사한 B.T_SAME 을 실행도 읽는다
        Assert.True(qualified.Allowed, qualified.Code);

        // B 에 없는 이름(w-it-p2 보고 뒤 m-unknown-name): 가드가 사전에서 B.T_ORD 를 찾지 못해 실행 전에 sql_schema_unresolved 로 거부한다.
        // 가드 없이 실행하면 B 에서 풀어 ORA-00942 로 실패한다 — 다른 스키마 객체로 새지는 않는다.
        Assert.Equal(ExternalSelectRefusal.NotFound, onlyInA.Refusal);
        Assert.Equal(AgentErrorCodes.SqlSchemaUnresolved, onlyInA.Code);
        var missing = await db.RunAsync(session, "SELECT * FROM t_ord", HistorySource.Agent);
        Assert.Equal(ExecutionStatus.Failed, missing.Status);
        Assert.Contains("942", missing.Error?.Code ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>query: 게이트가 이름을 푼 스키마(실행 범위 현재 스키마)와 에이전트 세션의 현재 스키마가 다르면 같은 이름이 다른 표라 <c>sql_schema_unresolved</c>.</summary>
    [Fact]
    public async Task Query_refuses_when_context_schema_differs_from_session_schema()
    {
        if (!db.Configured)
        {
            return;
        }

        var session = await db.OpenAsync();
        Assert.Equal(ExecutionStatus.Succeeded, (await db.RunAsync(session, $"ALTER SESSION SET CURRENT_SCHEMA = {db.UserB}")).Status);

        var differs = await db.Query.ExecuteAsync(db.Invoke(session, "SELECT src FROM t_same", db.UserA), default);
        var agrees = await db.Query.ExecuteAsync(db.Invoke(session, "SELECT src FROM t_same", db.UserB), default);

        Assert.Equal(AgentErrorCodes.SqlSchemaUnresolved, differs.ErrorCode);
        Assert.False(agrees.IsError, agrees.ErrorCode + " " + agrees.ErrorMessage);
        Assert.Equal("B", agrees.Result!["row_values"]![0]![0]!.GetValue<string>());
    }

    /// <summary>
    /// w-it-dict: 사전 뷰·동적 뷰는 사전 확인(m-unknown-name)에 거부되지 않는다 — PUBLIC 시노님이 SYS 뷰로 풀리고 SYS 객체는 사전 확인을 건너뛴다.
    /// 계정: none = A(사전 권한 없음), catalog = SELECT_CATALOG_ROLE, dictionary = SELECT ANY DICTIONARY, admin = 관리자(DBA).
    /// 권한이 있는 계정은 가드 통과 + 실행 성공이어야 한다. 권한이 없으면 실행이 ORA-00942 여도 되고, 가드가 미리 거부해도(시노님이 안 보임) 된다.
    /// </summary>
    [Theory]
    [InlineData("none")]
    [InlineData("catalog")]
    [InlineData("dictionary")]
    [InlineData("admin")]
    public async Task Dictionary_and_dynamic_views_pass_the_guard_for_every_account_that_may_read_them(string account)
    {
        if (!db.Configured)
        {
            return;
        }

        var session = account switch
        {
            "none" => await db.OpenAsync(),
            "catalog" => await db.OpenAsAsync(db.UserCatalog),
            "dictionary" => await db.OpenAsAsync(db.UserDictionary),
            _ => await db.OpenAsAsync(null),
        };
        string[] everyone = ["SELECT table_name FROM user_tables", "SELECT owner, object_name FROM all_objects WHERE ROWNUM <= 5"];
        string[] privileged =
        [
            "SELECT owner, table_name FROM dba_tables WHERE ROWNUM <= 5",
            "SELECT sid, serial# FROM v$session WHERE ROWNUM <= 5",
            "SELECT s.sid, g.inst_id FROM v$session s JOIN gv$session g ON g.sid = s.sid AND g.serial# = s.serial# WHERE ROWNUM <= 5",
            "SELECT sql_id FROM sys.v_$sql WHERE ROWNUM <= 5",
        ];

        foreach (var sql in everyone.Concat(privileged))
        {
            var check = await db.Guard.CheckAsync(session, sql, TabOptions, default);
            var run = check.Allowed ? await db.RunAsync(session, check.Statement!.Text, HistorySource.Agent) : null;
            output.WriteLine($"[dict] {account} | {sql} | guard={check.Allowed}/{check.Refusal}/{check.Code} | run={run?.Status}/{run?.Error?.Code}");
            if (account != "none" || everyone.Contains(sql))
            {
                Assert.True(check.Allowed, account + " " + sql + " " + check.Code + " " + check.Message);
                Assert.Equal(ExecutionStatus.Succeeded, run!.Status);
            }
            else if (check.Allowed)
            {
                Assert.Contains("942", run!.Error?.Code ?? string.Empty, StringComparison.Ordinal); // 권한 없음 = 실행이 실패할 뿐
            }
            else
            {
                Assert.Equal(AgentErrorCodes.SqlSchemaUnresolved, check.Code);
            }
        }
    }

    /// <summary>
    /// w-it-dict: 없는 표·권한 없는 다른 스키마 표는 실행 전에 <c>sql_schema_unresolved</c>(NotFound). WITH 이름(없는 표와 같은 이름 포함)·재귀 WITH·인라인 뷰·DUAL·
    /// 개인/PUBLIC 시노님·권한 받은 다른 스키마 표는 통과하고 실행도 된다.
    /// </summary>
    [Fact]
    public async Task Missing_tables_are_refused_before_execution_while_cte_inline_view_dual_synonyms_and_granted_tables_pass()
    {
        if (!db.Configured)
        {
            return;
        }

        var session = await db.OpenAsync();
        string[] refused =
        [
            "SELECT * FROM ort_ac_no_such_table",
            $"SELECT * FROM {db.UserB}.t_log", // 있지만 A 에 권한이 없다
            "SELECT o.ord_id FROM t_ord o JOIN ort_ac_no_such_table n ON n.id = o.ord_id",
        ];
        string[] passed =
        [
            "WITH c AS (SELECT ord_id FROM t_ord) SELECT ord_id FROM c",
            "WITH ort_ac_no_such_table AS (SELECT 1 AS x FROM dual) SELECT x FROM ort_ac_no_such_table",
            "WITH r (n) AS (SELECT 1 FROM dual UNION ALL SELECT n + 1 FROM r WHERE n < 3) SELECT n FROM r",
            "SELECT v.ord_id FROM (SELECT ord_id FROM t_ord) v",
            "SELECT 1 FROM DUAL",
            "SELECT src FROM s_same",
            $"SELECT src FROM {db.PublicSynonym}",
            $"SELECT src FROM {db.UserB}.t_same",
        ];

        foreach (var sql in refused)
        {
            var check = await db.Guard.CheckAsync(session, sql, TabOptions, default);
            output.WriteLine($"[missing] {sql} | {check.Refusal}/{check.Code} | {check.Message}");
            Assert.Equal(ExternalSelectRefusal.NotFound, check.Refusal);
            Assert.Equal(AgentErrorCodes.SqlSchemaUnresolved, check.Code);
            Assert.Null(check.Statement);
        }

        foreach (var sql in passed)
        {
            var check = await db.Guard.CheckAsync(session, sql, TabOptions, default);
            Assert.True(check.Allowed, sql + " " + check.Code + " " + check.Message);
            Assert.Equal(ExecutionStatus.Succeeded, (await db.RunAsync(session, check.Statement!.Text, HistorySource.Agent)).Status);
        }
    }
}
