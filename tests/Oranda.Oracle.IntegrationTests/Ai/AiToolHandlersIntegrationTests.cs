using System.Globalization;
using System.Text.Json.Nodes;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Storage;
using Oranda.Language;
using Oranda.Language.Ai.Handlers;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.IntegrationTests.Metadata;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Plans;
using Oranda.Oracle.Sessions;
using Oranda.Oracle.Tuning;

namespace Oranda.Oracle.IntegrationTests.Ai;

// 소유: ai-tools(t6). 튜닝 도구 처리자 9개를 실제 26ai 로 부른다(AgentGate 를 거치지 않는 부분).
// 허용 수준에 따른 끝값 제거·범위 밖 거부(수락 기준 2-a)는 AgentGate 머지 뒤 별도 클래스에서 확인한다.

/// <summary>
/// 시험용 표 2개(+범위 밖 표 1개, 동의어 1개)를 한 번 만들고 클래스의 모든 시험이 공유한다. 이름에 실행마다 고유한 접미사를 붙이고 끝에 지운다.
/// 환경 변수(TuningRecommendTests 와 같다): ORANDA_IT_HOST, ORANDA_IT_PORT(1521), ORANDA_IT_SERVICE, ORANDA_IT_ADMIN_USER, ORANDA_IT_ADMIN_PASSWORD.
/// 없으면 아무것도 안 하고 끝난다(ORANDA_IT_REQUIRE=1 이면 실패). 값은 출력하지 않는다.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "xUnit 이 IAsyncLifetime.DisposeAsync 로 정리한다. 접속 _raw 도 거기서 닫는다.")]
public sealed class AiToolsDbFixture : IAsyncLifetime
{
    private readonly ExecBus _bus = new();
    private OracleSessionManager _manager = null!;
    private OracleConnection? _raw;

    public bool Configured { get; private set; }

    public string Owner { get; private set; } = string.Empty;

    public string Order { get; private set; } = string.Empty;

    public string Dept { get; private set; } = string.Empty;

    public string Outside { get; private set; } = string.Empty;

    public string Synonym { get; private set; } = string.Empty;

    public string SqlId { get; private set; } = string.Empty;

    public string StatementText { get; private set; } = string.Empty;

    public ConnectionProfile Profile { get; private set; } = null!;

    public ISession Db { get; private set; } = null!;

    internal AgentToolboxProvider Boxes { get; private set; } = null!;

    internal AgentStatementReferenceExtractor Extractor { get; private set; } = null!;

    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    public async Task InitializeAsync()
    {
        if (Env("HOST") is null || Env("SERVICE") is null || Env("ADMIN_USER") is not { } user || Env("ADMIN_PASSWORD") is not { } password)
        {
            Assert.False(Env("REQUIRE") == "1", "ORANDA_IT_HOST/SERVICE/ADMIN_USER/ADMIN_PASSWORD 가 없다.");
            return;
        }

        Configured = true;
        var sfx = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        Owner = user.ToUpperInvariant();
        (Order, Dept, Outside, Synonym) = ($"AI_T_ORDER_{sfx}", $"AI_T_DEPT_{sfx}", $"AI_T_OUT_{sfx}", $"AI_T_SYN_{sfx}");
        Profile = new ConnectionProfile("it-admin", user, $"{Env("HOST")}:{Env("PORT") ?? "1521"}/{Env("SERVICE")}");

        _raw = new OracleConnection(Profile.BuildConnectionString(password));
        await _raw.OpenAsync();
        await ExecAsync($"CREATE TABLE {Dept} (dept_id NUMBER PRIMARY KEY, dept_nm VARCHAR2(30) NOT NULL)");
        await ExecAsync($"CREATE TABLE {Order} (order_id NUMBER PRIMARY KEY, dept_id NUMBER REFERENCES {Dept} (dept_id), stat_cd VARCHAR2(2 CHAR), order_dt DATE NOT NULL)");
        await ExecAsync($"COMMENT ON COLUMN {Order}.stat_cd IS '상태코드'");
        await ExecAsync($"CREATE TABLE {Outside} (k NUMBER)");
        await ExecAsync($"INSERT INTO {Dept} SELECT level, 'D' || level FROM dual CONNECT BY level <= 20");
        await ExecAsync(
            $"INSERT INTO {Order} SELECT level, MOD(level, 20) + 1, CASE WHEN MOD(level, 20) = 0 THEN 'B' WHEN MOD(level, 50) = 0 THEN 'C' ELSE 'A' END, SYSDATE - MOD(level, 365) FROM dual CONNECT BY level <= 20000");
        await ExecAsync("COMMIT");
        await ExecAsync($"CREATE INDEX {Order}_IX1 ON {Order} (dept_id, order_dt DESC)");
        await ExecAsync($"CREATE INDEX {Order}_IX2 ON {Order} (UPPER(stat_cd))");
        await ExecAsync($"CREATE SYNONYM {Synonym} FOR {Order}");
        await ExecAsync($"BEGIN DBMS_STATS.GATHER_TABLE_STATS(USER, '{Dept}'); DBMS_STATS.GATHER_TABLE_STATS(USER, '{Order}', method_opt => 'FOR ALL COLUMNS SIZE 1 FOR COLUMNS STAT_CD SIZE 254', cascade => TRUE); END;");

        // 공유 풀에 실제 계획이 남도록 한 번 실행한다(sql_id 는 본문에 넣은 표지로 찾는다).
        var tag = "AITOOLS_" + sfx;
        StatementText = $"SELECT /*+ gather_plan_statistics {tag} */ o.order_id, d.dept_nm FROM {Order} o JOIN {Dept} d ON d.dept_id = o.dept_id WHERE o.stat_cd = 'B'";
        await using (var run = _raw.CreateCommand())
        {
            run.CommandText = StatementText;
            await using var reader = await run.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
            }
        }

        await using (var find = _raw.CreateCommand())
        {
            find.BindByName = true;
            find.CommandText = "SELECT sql_id FROM v$sql WHERE sql_text LIKE :t AND sql_text NOT LIKE '%v$sql%' AND ROWNUM = 1";
            find.Parameters.Add(new OracleParameter("t", OracleDbType.Varchar2) { Value = "%" + tag + "%" });
            SqlId = (string)(await find.ExecuteScalarAsync() ?? throw new InvalidOperationException("v$sql 에서 시험 문장을 찾지 못했다."));
        }

        _manager = new OracleSessionManager(_bus);
        using var credential = new SecureCredential(password);
        Db = await _manager.OpenAsync(Profile, credential, SessionPurpose.Monitor, default);

        var exec = new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), _bus, null, pageSize: 500);
        var language = new LanguageService();
        var plans = new OracleExecutionPlanService(exec);
        var metadata = new OracleMetadataProvider(new MemoryMetadataCache(), _bus, Timeout.InfiniteTimeSpan);
        Boxes = new AgentToolboxProvider(new OracleTuningService(exec, plans, language), metadata, new OracleTableKeyDetailProvider(), plans, language);
        Extractor = new AgentStatementReferenceExtractor(language, metadata);
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
            foreach (var sql in new[] { $"DROP SYNONYM {Synonym}", $"DROP TABLE {Order} PURGE", $"DROP TABLE {Dept} PURGE", $"DROP TABLE {Outside} PURGE" })
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

    public AgentObjectRef Table(string name) => new(Owner, name, "TABLE");

    public AgentToolInvocation Invoke(
        JsonObject args, AiAccessLevel level = AiAccessLevel.Plan, ConnectionEnvironment env = ConnectionEnvironment.Development,
        bool? allowExplainPlan = null, params (string Arg, AgentObjectRef Obj)[] resolved)
    {
        var profile = Profile with { Environment = env, AllowExplainPlan = allowExplainPlan };
        var run = new AgentRunScope("it-run", profile, new AiContext("it-admin", env, level, StatementText: StatementText, SqlId: SqlId, CurrentSchema: Owner));
        // 게이트를 거치지 않는 시험이라 게이트가 채우는 허용 값을 같은 규칙으로 채운다.
        return new AgentToolInvocation(
            new AgentSession(run), Db, level, args, resolved.ToDictionary(r => r.Arg, r => r.Obj),
            ExplainPlanAllowed: level >= AiAccessLevel.Plan && AiAccessPolicy.ExplainPlanEnabled(profile),
            MayExecuteSql: level >= AiAccessLevel.Query);
    }

    private async Task ExecAsync(string sql)
    {
        await using var cmd = _raw!.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>도구 처리자 9개 + 문장 분석기를 실제 26ai 로 부른다. DB 를 바꾸지 않는 조회(벤치마크의 SELECT 포함)만 한다.</summary>
[Trait("Category", "Integration")]
public sealed class AiToolHandlersIntegrationTests(AiToolsDbFixture db) : IClassFixture<AiToolsDbFixture>
{
    private static JsonNode Ok(AgentToolOutput o)
    {
        Assert.False(o.IsError, o.ErrorMessage);
        return o.Result!;
    }

    [Fact]
    public async Task Describe_object_lists_columns_comment_primary_key_foreign_key_and_indexes()
    {
        if (!db.Configured)
        {
            return;
        }

        var h = new DescribeObjectHandler(db.Boxes);
        var args = new JsonObject { ["object"] = db.Order };

        var r = Ok(await h.ExecuteAsync(db.Invoke(args, AiAccessLevel.Metadata, resolved: ("object", db.Table(db.Order))), default));

        Assert.Equal(["ORDER_ID", "DEPT_ID", "STAT_CD", "ORDER_DT"], r["columns"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
        Assert.Equal("상태코드", r["columns"]![2]!["comment"]!.GetValue<string>());
        var kinds = r["constraints"]!.AsArray().Select(c => c!["kind"]!.GetValue<string>()).ToList();
        Assert.Contains("PRIMARY KEY", kinds);
        Assert.Contains("FOREIGN KEY", kinds);
        Assert.Contains(r["indexes"]!.AsArray(), i => i!["name"]!.GetValue<string>().EndsWith("_IX1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Index_info_merges_order_direction_function_expression_and_statistics()
    {
        if (!db.Configured)
        {
            return;
        }

        var h = new GetIndexInfoHandler(db.Boxes);

        var r = Ok(await h.ExecuteAsync(db.Invoke(new JsonObject { ["table"] = db.Order }, resolved: ("table", db.Table(db.Order))), default));

        var ix1 = r.AsArray().Single(i => i!["name"]!.GetValue<string>().EndsWith("_IX1", StringComparison.Ordinal))!;
        Assert.Equal(["DEPT_ID", "ORDER_DT"], ix1["columns"]!.AsArray().Select(c => c!["column"]!.GetValue<string>()));
        Assert.True(ix1["columns"]![1]!["descending"]!.GetValue<bool>());
        Assert.True(ix1["clustering_factor"]!.GetValue<long>() > 0);
        Assert.NotNull(ix1["last_analyzed"]);
        var ix2 = r.AsArray().Single(i => i!["name"]!.GetValue<string>().EndsWith("_IX2", StringComparison.Ordinal))!;
        Assert.Contains("UPPER", ix2["columns"]![0]!["expression"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Table_stats_and_histogram_report_counts_and_end_values_only_at_row_values_level()
    {
        if (!db.Configured)
        {
            return;
        }

        var stats = new GetTableStatsHandler(db.Boxes);
        var hist = new GetHistogramHandler(db.Boxes);
        var args = new JsonObject { ["table"] = db.Order, ["columns"] = new JsonArray("STAT_CD") };
        var histArgs = new JsonObject { ["table"] = db.Order, ["column"] = "STAT_CD" };

        var s = Ok(await stats.ExecuteAsync(db.Invoke(args, resolved: ("table", db.Table(db.Order))), default));
        var plan = Ok(await hist.ExecuteAsync(db.Invoke(histArgs, AiAccessLevel.Plan, resolved: ("table", db.Table(db.Order))), default));
        var rows = Ok(await hist.ExecuteAsync(db.Invoke(histArgs, AiAccessLevel.QueryRows, resolved: ("table", db.Table(db.Order))), default));

        Assert.Equal(20000, s["num_rows"]!.GetValue<long>());
        var col = Assert.Single(s["columns"]!.AsArray())!;
        Assert.Equal("FREQUENCY", col["histogram"]!.GetValue<string>());
        Assert.Equal(20000, plan["total_count"]!.GetValue<long>());
        Assert.DoesNotContain("\"endpoint\"", plan.ToJsonString(), StringComparison.Ordinal);
        // 짧은 문자열 열은 endpoint_actual_value 가 비어 서버가 준 숫자 인코딩(endpoint_value)이 그대로 나온다. 해석은 후속(OracleTuningService).
        var endpoints = rows["buckets"]!.AsArray().Select(b => b!["endpoint"]!.GetValue<string>()).ToList();
        Assert.Equal(3, endpoints.Count);
        Assert.All(endpoints, e => Assert.False(string.IsNullOrEmpty(e)));
    }

    [Fact]
    public async Task Cursor_plan_and_sql_stats_read_the_cursor_that_the_fixture_ran()
    {
        if (!db.Configured)
        {
            return;
        }

        var planHandler = new GetCursorPlanHandler(db.Boxes);
        var statsHandler = new GetSqlStatsHandler(db.Boxes);
        var args = new JsonObject { ["sql_id"] = db.SqlId };

        var plan = Ok(await planHandler.ExecuteAsync(db.Invoke(args), default));
        var st = Ok(await statsHandler.ExecuteAsync(db.Invoke(args), default));

        Assert.Equal(db.SqlId, plan["sql_id"]!.GetValue<string>());
        Assert.NotEmpty(plan["steps"]!.AsArray());
        Assert.Contains(plan["steps"]!.AsArray(), s => s!["a_rows"] is not null);
        Assert.True(st["found"]!.GetValue<bool>());
        Assert.True(st["executions"]!.GetValue<long>() >= 1);
    }

    [Fact]
    public void Sql_id_computed_from_the_statement_text_matches_the_one_the_server_assigned()
    {
        if (!db.Configured)
        {
            return;
        }

        Assert.Equal(db.SqlId, Oranda.Language.Ai.Tools.SqlIdCalculator.Compute(db.StatementText));
    }

    [Fact]
    public async Task Tuning_context_and_benchmark_hand_back_sql_ids_that_the_cursor_tools_then_accept()
    {
        if (!db.Configured)
        {
            return;
        }

        var ctx = await new GetTuningContextHandler(db.Boxes, db.Extractor).ExecuteAsync(db.Invoke(new JsonObject()), default);
        Assert.Equal([db.SqlId], ctx.SqlIds);

        var bench = await new BenchmarkQueriesHandler(db.Boxes, db.Extractor).ExecuteAsync(
            db.Invoke(
                new JsonObject { ["variants"] = new JsonArray(new JsonObject { ["label"] = "a", ["sql"] = $"SELECT COUNT(*) FROM {db.Order}" }), ["repeat"] = 1 },
                AiAccessLevel.Query),
            default);
        var id = Assert.Single(bench.SqlIds);
        var plan = await new GetCursorPlanHandler(db.Boxes).ExecuteAsync(db.Invoke(new JsonObject { ["sql_id"] = id }), default);
        Assert.False(plan.IsError, plan.ErrorMessage);
    }

    [Fact]
    public async Task Explain_plan_works_where_allowed_and_is_refused_for_production_with_cursor_plan_guidance()
    {
        if (!db.Configured)
        {
            return;
        }

        var h = new ExplainPlanHandler(db.Boxes, db.Extractor);
        var args = new JsonObject { ["sql"] = $"SELECT * FROM {db.Order} WHERE stat_cd = :1" };

        var dev = Ok(await h.ExecuteAsync(db.Invoke(args), default));
        var prodDefault = Ok(await h.ExecuteAsync(db.Invoke(args, env: ConnectionEnvironment.Production), default));
        var prod = await h.ExecuteAsync(db.Invoke(args, env: ConnectionEnvironment.Production, allowExplainPlan: false), default);

        Assert.NotEmpty(dev["steps"]!.AsArray());
        Assert.NotEmpty(prodDefault["steps"]!.AsArray()); // 운영도 EXPLAIN PLAN 은 기본 켬
        Assert.True(prod.IsError);
        Assert.Contains("get_cursor_plan", prod.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tuning_context_bundles_both_tables_used_columns_indexes_and_plan_findings()
    {
        if (!db.Configured)
        {
            return;
        }

        var h = new GetTuningContextHandler(db.Boxes, db.Extractor);

        var r = Ok(await h.ExecuteAsync(db.Invoke(new JsonObject { ["budget_tokens"] = 4000 }), default));

        Assert.Equal(db.SqlId, r["statement"]!["sql_id"]!.GetValue<string>());
        var objects = r["objects"]!.AsArray();
        Assert.Equal(2, objects.Count);
        var order = objects.Single(o => o!["name"]!.GetValue<string>().EndsWith(db.Order, StringComparison.Ordinal))!;
        var used = order["used_columns"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("STAT_CD", used);
        Assert.Contains("DEPT_ID", used);
        Assert.NotEmpty(order["indexes"]!.AsArray());
        Assert.Contains(r["omitted"]!.AsArray(), o => o!.GetValue<string>().Contains("끝값", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Benchmark_runs_select_variants_and_refuses_production_and_non_select()
    {
        if (!db.Configured)
        {
            return;
        }

        var h = new BenchmarkQueriesHandler(db.Boxes, db.Extractor);
        JsonObject Args(string sql) => new() { ["variants"] = new JsonArray(new JsonObject { ["label"] = "a", ["sql"] = sql }), ["repeat"] = 2 };

        var ok = Ok(await h.ExecuteAsync(db.Invoke(Args($"SELECT COUNT(*) FROM {db.Order}"), AiAccessLevel.Query), default));
        var prod = Ok(await h.ExecuteAsync(db.Invoke(Args("SELECT 1 FROM dual"), AiAccessLevel.Query, ConnectionEnvironment.Production), default));
        var dml = await h.ExecuteAsync(db.Invoke(Args($"DELETE FROM {db.Order}"), AiAccessLevel.Query), default);

        Assert.Equal(1, ok[0]!["rows"]!.GetValue<long>());
        Assert.Equal(2, ok[0]!["repeat"]!.GetValue<int>());
        Assert.Equal(1, prod[0]!["rows"]!.GetValue<long>()); // 운영도 SELECT 는 실행한다
        Assert.True(dml.IsError);
        Assert.Equal(20000, Convert.ToInt32(await ScalarAsync($"SELECT COUNT(*) FROM {db.Order}"), CultureInfo.InvariantCulture)); // 지워지지 않았다
    }

    [Fact]
    public async Task Extractor_resolves_synonym_flags_statement_traits_and_finds_foreign_key_parent()
    {
        if (!db.Configured)
        {
            return;
        }

        var resolved = await db.Extractor.ResolveAsync(db.Db, db.Synonym, db.Owner, default);
        var missing = await db.Extractor.ResolveAsync(db.Db, "NO_SUCH_TABLE_" + db.Synonym, db.Owner, default);
        var info = await db.Extractor.AnalyzeAsync(db.Db, $"SELECT * FROM {db.Synonym} FOR UPDATE", db.Owner, default);
        var related = await db.Extractor.GetRelatedAsync(db.Db, [db.Table(db.Order)], default);

        Assert.Equal($"{db.Owner}.{db.Order}", resolved?.Key);
        Assert.Null(missing);
        Assert.Equal($"{db.Owner}.{db.Order}", Assert.Single(info.Objects).Key);
        Assert.True(info.HasLockingClause);
        Assert.Contains($"{db.Owner}.{db.Dept}", related.Select(x => x.Key));
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new OracleConnection(db.Profile.BuildConnectionString(Environment.GetEnvironmentVariable("ORANDA_IT_ADMIN_PASSWORD")!));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }
}
