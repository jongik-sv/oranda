using System.Text.Json.Nodes;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
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

// 소유: agent-cli-1122 레인 d-lang. 외부 연결 메타 도구 6개·query 를 실제 26ai 로 부른다(게이트는 거치지 않는다).

/// <summary>
/// 시험 객체(<c>ORT_AC_</c> 접두 + 실행마다 고유 접미사): 표 1개(민감 열 포함)·동의어·시퀀스·패키지(겹지정 함수)·깨진 뷰. 끝에 모두 지운다.
/// 환경 변수: ORANDA_IT_HOST, ORANDA_IT_PORT, ORANDA_IT_SERVICE, ORANDA_IT_ADMIN_USER, ORANDA_IT_ADMIN_PASSWORD(없으면 건너뜀, ORANDA_IT_REQUIRE=1 이면 실패).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "xUnit 이 IAsyncLifetime.DisposeAsync 로 정리한다.")]
public sealed class ExternalToolsDbFixture : IAsyncLifetime
{
    private readonly ExecBus _bus = new();
    private OracleSessionManager? _manager;
    private OracleConnection? _raw;

    public bool Configured { get; private set; }

    public string Owner { get; private set; } = string.Empty;

    public string Suffix { get; private set; } = string.Empty;

    public string Table => "ORT_AC_ORD_" + Suffix;

    public string Synonym => "ORT_AC_SYN_" + Suffix;

    public string Sequence => "ORT_AC_SQ_" + Suffix;

    public string Package => "ORT_AC_PK_" + Suffix;

    public string BrokenView => "ORT_AC_BAD_" + Suffix;

    public ConnectionProfile Profile { get; private set; } = null!;

    public ISession Db { get; private set; } = null!;

    internal OracleMetadataProvider Metadata { get; private set; } = null!;

    internal QueryHandler Query { get; private set; } = null!;

    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    public async Task InitializeAsync()
    {
        if (Env("HOST") is null || Env("SERVICE") is null || Env("ADMIN_USER") is not { } user || Env("ADMIN_PASSWORD") is not { } password)
        {
            Assert.False(Env("REQUIRE") == "1", "ORANDA_IT_HOST/SERVICE/ADMIN_USER/ADMIN_PASSWORD 가 없다.");
            return;
        }

        Configured = true;
        Suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        Owner = user.ToUpperInvariant();
        Profile = new ConnectionProfile("it-admin", user, $"{Env("HOST")}:{Env("PORT") ?? "1521"}/{Env("SERVICE")}");

        _raw = new OracleConnection(Profile.BuildConnectionString(password));
        await _raw.OpenAsync();
        await ExecAsync($"CREATE TABLE {Table} (ord_id NUMBER PRIMARY KEY, ord_nm VARCHAR2(30), phone_no VARCHAR2(20), ord_dt DATE)");
        await ExecAsync($"COMMENT ON TABLE {Table} IS '외부 시험 주문'");
        await ExecAsync($"INSERT INTO {Table} SELECT level, 'N' || level, '010-' || level, DATE '2026-11-01' + level FROM dual CONNECT BY level <= 30");
        await ExecAsync("COMMIT");
        await ExecAsync($"CREATE SYNONYM {Synonym} FOR {Table}");
        await ExecAsync($"CREATE SEQUENCE {Sequence}");
        await ExecAsync($"""
            CREATE OR REPLACE PACKAGE {Package} AS
              PROCEDURE add_ord(p_id IN NUMBER, p_nm IN VARCHAR2 DEFAULT NULL);
              FUNCTION total RETURN NUMBER;
              FUNCTION total(p_from IN DATE) RETURN NUMBER;
            END {Package};
            """);
        await ExecAsync($"""
            CREATE OR REPLACE PACKAGE BODY {Package} AS
              PROCEDURE add_ord(p_id IN NUMBER, p_nm IN VARCHAR2 DEFAULT NULL) IS
              BEGIN
                NULL;
              END add_ord;
              FUNCTION total RETURN NUMBER IS
              BEGIN
                RETURN 0;
              END total;
              FUNCTION total(p_from IN DATE) RETURN NUMBER IS
              BEGIN
                RETURN 1;
              END total;
            END {Package};
            """);
        await ExecAsync($"CREATE FORCE VIEW {BrokenView} AS SELECT x FROM ort_ac_nothing_{Suffix}");

        _manager = new OracleSessionManager(_bus);
        using var credential = new SecureCredential(password);
        Db = await _manager.OpenAsync(Profile, credential, SessionPurpose.Monitor, default);

        var exec = new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), _bus, null, pageSize: 20);
        var language = new LanguageService();
        var plans = new OracleExecutionPlanService(exec);
        Metadata = new OracleMetadataProvider(new MemoryMetadataCache(), _bus, Timeout.InfiniteTimeSpan);
        var boxes = new AgentToolboxProvider(new OracleTuningService(exec, plans, language), Metadata, new OracleTableKeyDetailProvider(), plans, language);
        Query = new QueryHandler(language, exec, new AgentStatementReferenceExtractor(language, Metadata), boxes);
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

        if (_raw is null)
        {
            return;
        }

        try
        {
            foreach (var sql in new[]
            {
                $"DROP VIEW {BrokenView}", $"DROP PACKAGE {Package}", $"DROP SEQUENCE {Sequence}", $"DROP SYNONYM {Synonym}", $"DROP TABLE {Table} PURGE",
            })
            {
                try
                {
                    await ExecAsync(sql);
                }
                catch (OracleException)
                {
                    // 만들다 만 객체는 없을 수 있다. 나머지를 계속 지운다.
                }
            }
        }
        finally
        {
            await _raw.DisposeAsync();
        }
    }

    public AgentToolInvocation Invoke(JsonObject args, AiAccessLevel level = AiAccessLevel.QueryRows)
    {
        var run = new AgentRunScope("g1:it-admin", Profile, new AiContext("it-admin", ConnectionEnvironment.Development, level));
        return new AgentToolInvocation(new AgentSession(run), Db, level, args, new Dictionary<string, AgentObjectRef>(), MayExecuteSql: level >= AiAccessLevel.Query);
    }

    private async Task ExecAsync(string sql)
    {
        await using var cmd = _raw!.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}

[Trait("Category", "Integration")]
public sealed class ExternalToolsIntegrationTests(ExternalToolsDbFixture db) : IClassFixture<ExternalToolsDbFixture>
{
    private static JsonNode Ok(AgentToolOutput o)
    {
        Assert.False(o.IsError, o.ErrorCode + ": " + o.ErrorMessage + " / " + o.LogDetail);
        return o.Result!;
    }

    [Fact]
    public async Task Find_objects_lists_test_objects_with_status_and_comment()
    {
        if (!db.Configured)
        {
            return;
        }

        var result = Ok(await new FindObjectsHandler(db.Metadata).ExecuteAsync(
            db.Invoke(new JsonObject { ["name_like"] = "ort_ac_%_" + db.Suffix, ["owner"] = db.Owner, ["limit"] = 200 }), default));

        var objects = result["objects"]!.AsArray();
        var table = Assert.Single(objects, o => o!["name"]!.GetValue<string>() == db.Table && o["type"]!.GetValue<string>() == "TABLE")!;
        Assert.Equal("VALID", table["status"]!.GetValue<string>());
        Assert.Equal("외부 시험 주문", table["comment"]!.GetValue<string>());
        Assert.Equal("INVALID", Assert.Single(objects, o => o!["name"]!.GetValue<string>() == db.BrokenView)!["status"]!.GetValue<string>());
        Assert.Contains(objects, o => o!["name"]!.GetValue<string>() == db.Package && o["type"]!.GetValue<string>() == "PACKAGE BODY");
        Assert.False(result["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task List_schemas_includes_the_connection_user()
    {
        if (!db.Configured)
        {
            return;
        }

        var result = Ok(await new ListSchemasHandler(db.Metadata).ExecuteAsync(db.Invoke([], AiAccessLevel.Metadata), default));

        Assert.Contains(db.Owner, result["schemas"]!.AsArray().Select(s => s!.GetValue<string>()));
    }

    [Fact]
    public async Task Get_ddl_returns_table_and_sequence_ddl_in_sql_text()
    {
        if (!db.Configured)
        {
            return;
        }

        var table = Ok(await new GetDdlHandler(db.Metadata).ExecuteAsync(db.Invoke(new JsonObject { ["object"] = db.Synonym }), default));
        var sequence = Ok(await new GetDdlHandler(db.Metadata).ExecuteAsync(db.Invoke(new JsonObject { ["object"] = db.Owner + "." + db.Sequence }), default));

        Assert.Equal(db.Table, table["name"]!.GetValue<string>());
        Assert.Contains("CREATE TABLE", table["sql_text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("SEQUENCE", sequence["type"]!.GetValue<string>());
        Assert.Contains("CREATE SEQUENCE", sequence["sql_text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_source_and_procedure_args_read_the_package()
    {
        if (!db.Configured)
        {
            return;
        }

        var source = Ok(await new GetSourceHandler(db.Metadata).ExecuteAsync(db.Invoke(new JsonObject { ["object"] = db.Package, ["member"] = "total" }), default));
        var args = Ok(await new GetProcedureArgsHandler(db.Metadata).ExecuteAsync(db.Invoke(new JsonObject { ["object"] = db.Package }), default));

        var text = source["sql_text"]!.GetValue<string>();
        Assert.Contains("RETURN 1;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("add_ord", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["ADD_ORD", "TOTAL"], source["members"]!.AsArray().Select(m => m!.GetValue<string>()).Order());

        var members = args["members"]!.AsArray();
        var add = Assert.Single(members, m => m!["member"]!.GetValue<string>() == "ADD_ORD")!;
        Assert.Equal(["P_ID", "P_NM"], add["args"]!.AsArray().Select(a => a!["name"]!.GetValue<string>()));
        Assert.True(add["args"]![1]!["default"]!.GetValue<bool>());
        Assert.Equal(2, members.Count(m => m!["member"]!.GetValue<string>() == "TOTAL"));
        Assert.All(members.Where(m => m!["member"]!.GetValue<string>() == "TOTAL"), m => Assert.Equal("NUMBER", m!["returns"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Resolve_names_follows_the_synonym_and_hides_missing_names()
    {
        if (!db.Configured)
        {
            return;
        }

        var result = Ok(await new ResolveNamesHandler(db.Metadata).ExecuteAsync(
            db.Invoke(new JsonObject { ["names"] = new JsonArray(db.Synonym.ToLowerInvariant(), db.Package, "ORT_AC_NONE_" + db.Suffix) }), default));

        var names = result["names"]!.AsArray();
        Assert.Equal(db.Table, names[0]!["name"]!.GetValue<string>());
        Assert.True(names[0]!["via_synonym"]!.GetValue<bool>());
        Assert.Equal("PACKAGE", names[1]!["type"]!.GetValue<string>());
        Assert.False(names[2]!["found"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Query_returns_rows_masks_phone_and_stops_at_max_rows()
    {
        if (!db.Configured)
        {
            return;
        }

        var sql = $"SELECT ord_id, ord_nm, phone_no, ord_dt FROM {db.Table} ORDER BY ord_id";
        var output = await db.Query.ExecuteAsync(db.Invoke(new JsonObject { ["sql"] = sql, ["max_rows"] = 25 }), default);
        var result = Ok(output);
        var counted = Ok(await db.Query.ExecuteAsync(db.Invoke(new JsonObject { ["sql"] = sql }, AiAccessLevel.Query), default));

        var rows = result["row_values"]!.AsArray();
        Assert.Equal(25, rows.Count);
        Assert.True(result["truncated"]!.GetValue<bool>());
        Assert.Equal(1m, rows[0]![0]!.GetValue<decimal>());
        Assert.Equal("***", rows[0]![2]!.GetValue<string>());
        Assert.Equal("2026-11-02T00:00:00", rows[0]![3]!.GetValue<string>());
        Assert.Equal(["ORD_ID", "ORD_NM", "PHONE_NO", "ORD_DT"], result["columns"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
        Assert.Null(counted["row_values"]);
        Assert.Equal(30, counted["row_count"]!.GetValue<int>());
        Assert.False(counted["truncated"]!.GetValue<bool>());
        if (result["sql_id"] is { } id)
        {
            Assert.Equal([id.GetValue<string>()], output.SqlIds);
        }
    }

    [Fact]
    public async Task Query_with_bind_and_errors_show_codes_only()
    {
        if (!db.Configured)
        {
            return;
        }

        var bound = Ok(await db.Query.ExecuteAsync(
            db.Invoke(new JsonObject { ["sql"] = $"SELECT ord_nm FROM {db.Table} WHERE ord_id = :id", ["binds"] = new JsonObject { ["id"] = 7 } }), default));
        var failed = await db.Query.ExecuteAsync(db.Invoke(new JsonObject { ["sql"] = $"SELECT TO_NUMBER(ord_nm) FROM {db.Table}" }), default);

        Assert.Equal("N7", bound["row_values"]![0]![0]!.GetValue<string>());
        Assert.Equal("tool_failed", failed.ErrorCode);
        Assert.Contains("ORA-01722", failed.ErrorMessage, StringComparison.Ordinal);
    }
}
