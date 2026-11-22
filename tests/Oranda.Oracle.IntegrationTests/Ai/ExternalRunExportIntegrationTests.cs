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

namespace Oranda.Oracle.IntegrationTests.Ai;

// 소유: agent-cli-1122 레인 w-it-p2(R5). run_tab·export_result 가 함께 쓰는 문장 가드(ExternalSelectGuard)를 실제 26ai 로 본다.
// run_tab·export_result 본체는 App 처리자라 이 프로젝트(App 참조 없음)에서 부르지 못한다 → 본체 왕복은 App.Tests/Integration/ExternalRunExportFlowTests.

/// <summary>
/// 시험 계정 둘(<c>ORT_AC_RA_</c>·<c>ORT_AC_RB_</c> + 실행마다 고유 접미사, 끝에 계정째 지운다).
/// A = 접속 계정: 표 <c>T_ORD</c>(30행)·시퀀스 <c>SQ</c>·안전 함수 <c>FN_PLAIN</c>·자치 트랜잭션 함수 <c>FN_AUTO</c>.
/// B = 다른 스키마: 같은 이름의 <c>FN_PLAIN</c> 이지만 자치 트랜잭션으로 <c>B.T_LOG</c> 에 한 줄 쓴다(A 에 EXECUTE 허가). 같은 이름 표 <c>T_SAME</c>(A 값 'A', B 값 'B'). CURRENT_SCHEMA 확인용.
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

    public bool Configured { get; private set; }

    public string UserA { get; private set; } = string.Empty;

    public string UserB { get; private set; } = string.Empty;

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
        _password = "It#" + Guid.NewGuid().ToString("N")[..12] + "a1";
        var dataSource = $"{Env("HOST")}:{Env("PORT") ?? "1521"}/{Env("SERVICE")}";
        _admin = new OracleConnection(new ConnectionProfile("it-admin", admin, dataSource).BuildConnectionString(adminPassword));
        await _admin.OpenAsync();
        foreach (var u in new[] { UserA, UserB })
        {
            await ExecAsync(_admin, $"CREATE USER {u} IDENTIFIED BY \"{_password}\" QUOTA 10M ON USERS");
            await ExecAsync(_admin, $"GRANT CREATE SESSION, CREATE TABLE, CREATE PROCEDURE, CREATE SEQUENCE TO {u}");
        }

        await ExecAsync(_admin, $"CREATE TABLE {UserB}.T_LOG (n NUMBER)");
        await ExecAsync(_admin, $"CREATE FUNCTION {UserB}.FN_PLAIN(x NUMBER) RETURN NUMBER IS PRAGMA AUTONOMOUS_TRANSACTION; BEGIN INSERT INTO {UserB}.T_LOG VALUES (x); COMMIT; RETURN x; END;");
        await ExecAsync(_admin, $"GRANT EXECUTE ON {UserB}.FN_PLAIN TO {UserA}");
        await ExecAsync(_admin, $"CREATE TABLE {UserB}.T_SAME (src VARCHAR2(1))"); // A 에도 같은 이름 표(값이 다르다)
        await ExecAsync(_admin, $"INSERT INTO {UserB}.T_SAME VALUES ('B')");
        await ExecAsync(_admin, "COMMIT");
        await ExecAsync(_admin, $"GRANT SELECT ON {UserB}.T_SAME TO {UserA}");

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
            await ExecAsync(own, "CREATE FUNCTION FN_AUTO(x NUMBER) RETURN NUMBER IS PRAGMA AUTONOMOUS_TRANSACTION; BEGIN INSERT INTO T_LOG VALUES (x); COMMIT; RETURN x; END;");
        }

        _manager = new OracleSessionManager(_bus);
        Exec = new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), _bus, null, pageSize: 500);
        var metadata = new OracleMetadataProvider(new MemoryMetadataCache(), _bus, Timeout.InfiniteTimeSpan);
        var extractor = new AgentStatementReferenceExtractor(Language, metadata);
        Guard = new ExternalSelectGuard(Language, extractor, new UserFunctionSafetyChecker(Exec), Exec);
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
            foreach (var u in new[] { UserA, UserB })
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
public sealed class ExternalRunExportIntegrationTests(ExternalRunGuardDbFixture db) : IClassFixture<ExternalRunGuardDbFixture>
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

        // B 에 없는 이름(현재 동작 고정, w-it-p2 보고): 가드는 사전에 없는 이름을 「풀림」으로 본다(SynonymResolver 가 그대로 돌려준다) → 통과.
        // 실행은 B 에서 풀어 ORA-00942 로 실패한다 — 다른 스키마 객체로 새지는 않는다. 가드가 거부하게 고치면 Unresolved 로 바꾼다.
        Assert.True(onlyInA.Allowed, onlyInA.Code);
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
}
