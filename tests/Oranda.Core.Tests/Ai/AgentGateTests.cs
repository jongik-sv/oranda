using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Testing;

namespace Oranda.Core.Tests.Ai;

public partial class AgentGateTests
{
    private static readonly string[] LeakedFromErrors = ["db01", "1521", "10.1.2.3", "C:", "fs01", "\r", "\n"];

    private static readonly string[] LeakableFields = ["endpoint", "low_value", "high_value"];

    private const string Statement = "SELECT * FROM TB_ORDER o WHERE o.stat_cd = 'A'";

    private static ConnectionProfile Dev(AiAccessLevel? level = null, bool? explainPlan = null) =>
        new("dev", "scott", "h/s", AiAccess: level, AllowExplainPlan: explainPlan);

    private static ConnectionProfile Prod(AiAccessLevel? level = AiAccessLevel.Plan) =>
        new("prod", "scott", "h/s", Environment: ConnectionEnvironment.Production, AiAccess: level);

    private static AgentSession Session(
        ConnectionProfile? profile = null,
        AiAccessLevel context = AiAccessLevel.QueryRows,
        string? statement = Statement,
        string? sqlId = null,
        AgentSendScope? send = null,
        string? currentSchema = null,
        bool allowRiskyFunctions = false)
    {
        profile ??= Dev(AiAccessLevel.QueryRows);
        var ctx = new AiContext(profile.Name, profile.Environment, context, StatementText: statement, SqlId: sqlId, CurrentSchema: currentSchema, AllowRiskyFunctions: allowRiskyFunctions);
        // 운영 접속은 리터럴 가림이 필수라 사전이 있어야 SQL 도구를 쓸 수 있다(없을 때의 닫힘은 AgentGateTests.Masking 이 시험한다).
        var literals = profile.Environment == ConnectionEnvironment.Production ? new LiteralVault() : null;
        return new AgentSession(new AgentRunScope("run-1", profile, ctx, send ?? AgentSendScope.StatementOnly) { Literals = literals }) { Client = "test-client" };
    }

    private static Task<AgentGateResponse> Call(GateFixture fx, AgentSession session, string tool, string? json = null) =>
        fx.Gate.CallToolAsync(session, tool, json is null ? null : JsonNode.Parse(json), CancellationToken.None);

    private static string? Code(AgentGateResponse response) =>
        response.Result.IsError ? (string?)JsonNode.Parse(response.Result.Text)!["code"] : null;

    private static void AssertDenied(AgentGateResponse response, string code)
    {
        Assert.True(response.Result.IsError);
        Assert.Equal(AiToolJudgement.Denied, response.Judgement);
        Assert.Equal(code, Code(response));
        Assert.False(string.IsNullOrWhiteSpace((string?)JsonNode.Parse(response.Result.Text)!["error"]));
    }

    // ---------------------------------------------------------------- 허용 수준

    [Fact]
    public async Task Metadata_level_denies_get_index_info_but_allows_describe_object()
    {
        var fx = new GateFixture();
        var session = Session(Dev(AiAccessLevel.Metadata));

        AssertDenied(await Call(fx, session, AgentToolNames.GetIndexInfo, """{"table":"TB_ORDER"}"""), "level");
        var ok = await Call(fx, session, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        Assert.False(ok.Result.IsError);
        Assert.Equal(AiToolJudgement.Allowed, ok.Judgement);
    }

    [Fact]
    public async Task Plan_level_allows_get_index_info()
    {
        var fx = new GateFixture();
        var response = await Call(fx, Session(Dev(AiAccessLevel.Plan)), AgentToolNames.GetIndexInfo, """{"table":"TB_ORDER"}""");
        Assert.False(response.Result.IsError);
    }

    [Fact]
    public async Task Production_query_level_runs_benchmark_but_a_stored_query_rows_shrinks_to_plan_and_is_denied()
    {
        var fx = new GateFixture();
        const string benchmark = """{"variants":[{"label":"a","sql":"SELECT * FROM TB_ORDER"}]}""";

        var allowed = Session(Prod(AiAccessLevel.Query), context: AiAccessLevel.QueryRows);
        Assert.Equal(AiAccessLevel.Query, allowed.Level);
        var ok = await Call(fx, allowed, AgentToolNames.BenchmarkQueries, benchmark);
        Assert.False(ok.Result.IsError);
        Assert.True(Assert.Single(fx.Handlers[AgentToolNames.BenchmarkQueries].Calls).MayExecuteSql);

        var narrowed = Session(Prod(AiAccessLevel.QueryRows), context: AiAccessLevel.QueryRows);
        Assert.Equal(AiAccessLevel.Plan, narrowed.Level);
        var response = await Call(fx, narrowed, AgentToolNames.BenchmarkQueries, benchmark);
        AssertDenied(response, "level");
        Assert.Single(fx.Handlers[AgentToolNames.BenchmarkQueries].Calls);
    }

    [Fact]
    public async Task Level_comes_from_the_session_not_from_a_wider_profile_value()
    {
        // 문맥이 좁으면 프로필이 넓어도 문맥 값으로 줄어든다(Level 은 둘 중 작은 값). 같은 세션에서 프로필을 바꾸는 시험은
        // AgentRunScope.Profile 이 변하지 않는 record 라 만들 수 없어, 이 계약은 AgentContractTests 가 지킨다.
        var fx = new GateFixture();
        var session = Session(Dev(AiAccessLevel.QueryRows), context: AiAccessLevel.Metadata);
        AssertDenied(await Call(fx, session, AgentToolNames.GetIndexInfo, """{"table":"TB_ORDER"}"""), "level");
    }

    [Fact]
    public async Task Off_level_denies_every_tool()
    {
        var fx = new GateFixture();
        var session = Session(Dev(AiAccessLevel.Off));
        AssertDenied(await Call(fx, session, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}"""), "level");
    }

    // ---------------------------------------------------------------- 도구·접속·인자

    [Fact]
    public async Task Connection_argument_must_match_the_request_connection_ignoring_case()
    {
        var fx = new GateFixture();
        var session = Session();
        AssertDenied(await Call(fx, session, AgentToolNames.DescribeObject, """{"connection":"prod","object":"TB_ORDER"}"""), "connection");
        Assert.False((await Call(fx, session, AgentToolNames.DescribeObject, """{"connection":"DEV","object":"TB_ORDER"}""")).Result.IsError);
    }

    [Fact]
    public async Task Unknown_tool_is_denied_and_audited_under_the_requested_name()
    {
        var fx = new GateFixture();
        var response = await Call(fx, Session(), "DROP_EVERYTHING", """{"sql":"select 1 from dual"}""");
        AssertDenied(response, "unknown_tool");

        var entry = Assert.Single(fx.Audit.Entries);
        Assert.Equal("DROP_EVERYTHING", entry.Tool);
        Assert.Null(entry.Arguments);
    }

    [Fact]
    public async Task Revoked_session_is_denied_and_lists_no_tools()
    {
        var fx = new GateFixture();
        var session = Session();
        Assert.NotEmpty(fx.Gate.ListTools(session));
        session.Revoke();

        AssertDenied(await Call(fx, session, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}"""), "run_revoked");
        Assert.Empty(fx.Gate.ListTools(session));
        Assert.Empty(fx.Handlers[AgentToolNames.DescribeObject].Calls);
    }

    [Theory]
    [InlineData("""{"object":"TB_ORDER","extra":1}""")]
    [InlineData("""{"object":"TB_ORDER","Connection":"dev"}""")]
    [InlineData("""{"Object":"TB_ORDER"}""")]
    [InlineData("""{}""")]
    [InlineData("""{"object":5}""")]
    [InlineData("""{"object":null}""")]
    public async Task Undeclared_misspelled_missing_or_mistyped_arguments_are_rejected(string json)
    {
        var fx = new GateFixture();
        AssertDenied(await Call(fx, Session(), AgentToolNames.DescribeObject, json), "invalid_arguments");
        Assert.Empty(fx.Handlers[AgentToolNames.DescribeObject].Calls);
    }

    [Fact]
    public async Task Arguments_that_are_not_an_object_or_have_bad_nested_types_are_rejected()
    {
        var fx = new GateFixture();
        var session = Session();
        AssertDenied(await fx.Gate.CallToolAsync(session, AgentToolNames.DescribeObject, JsonNode.Parse("[1]"), CancellationToken.None), "invalid_arguments");
        AssertDenied(await Call(fx, session, AgentToolNames.GetTableStats, """{"table":"TB_ORDER","columns":[1]}"""), "invalid_arguments");
        AssertDenied(await Call(fx, session, AgentToolNames.GetCursorPlan, """{"sql_id":"abcdefghij123","child":"1"}"""), "invalid_arguments");
        AssertDenied(await Call(fx, session, AgentToolNames.BenchmarkQueries, """{"variants":[{"sql":"select 1"}]}"""), "invalid_arguments");
        AssertDenied(await Call(fx, session, AgentToolNames.BenchmarkQueries, """{"variants":[{"label":"a","sql":"select 1","extra":"x"}]}"""), "invalid_arguments");
    }

    [Fact]
    public async Task Handler_receives_a_fresh_object_with_only_declared_keys()
    {
        var fx = new GateFixture();
        var input = JsonNode.Parse("""{"table":"TB_ORDER","columns":["STAT_CD"],"connection":null}""")!;
        await fx.Gate.CallToolAsync(Session(), AgentToolNames.GetTableStats, input, CancellationToken.None);

        var invocation = Assert.Single(fx.Handlers[AgentToolNames.GetTableStats].Calls);
        Assert.NotSame(input, invocation.Arguments);
        Assert.Equal(["table", "columns"], invocation.Arguments.Select(p => p.Key).ToArray());
        Assert.Equal("STAT_CD", (string?)invocation.Arguments["columns"]![0]);
    }

    // ---------------------------------------------------------------- 범위

    [Fact]
    public async Task Statement_reference_is_in_scope_and_the_handler_gets_the_resolved_name()
    {
        var fx = new GateFixture();
        var response = await Call(fx, Session(), AgentToolNames.GetTableStats, """{"table":"tb_order"}""");
        Assert.False(response.Result.IsError);

        var invocation = Assert.Single(fx.Handlers[AgentToolNames.GetTableStats].Calls);
        Assert.Equal("APP.TB_ORDER", (string?)invocation.Arguments["table"]);
        Assert.Equal("APP.TB_ORDER", invocation.Resolved["table"].Key);
        Assert.Same(fx.Source.Db, invocation.Db);
        Assert.Equal(AiAccessLevel.QueryRows, invocation.Level);
    }

    [Fact]
    public async Task Related_objects_and_send_scope_patterns_are_in_scope()
    {
        var fx = new GateFixture();
        var session = Session(send: new AgentSendScope(false, ["REF.CODE_*"]));

        Assert.False((await Call(fx, session, AgentToolNames.GetIndexInfo, """{"table":"APP.TB_CUST"}""")).Result.IsError);
        Assert.False((await Call(fx, session, AgentToolNames.DescribeObject, """{"object":"REF.CODE_A"}""")).Result.IsError);
        AssertDenied(await Call(fx, session, AgentToolNames.DescribeObject, """{"object":"HR.SALARY"}"""), "scope");
    }

    [Fact]
    public async Task Missing_and_out_of_scope_objects_get_the_same_answer()
    {
        var fx = new GateFixture();
        var session = Session();
        var outOfScope = await Call(fx, session, AgentToolNames.DescribeObject, """{"object":"HR.SALARY"}""");
        var missing = await Call(fx, session, AgentToolNames.DescribeObject, """{"object":"HR.NO_SUCH_TABLE"}""");

        AssertDenied(outOfScope, "scope");
        AssertDenied(missing, "scope");
        Assert.Equal(outOfScope.Result.Text, missing.Result.Text);
    }

    [Fact]
    public async Task Statement_scope_is_computed_once_per_run()
    {
        var fx = new GateFixture();
        var session = Session();
        for (var i = 0; i < 3; i++)
        {
            await Call(fx, session, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        }

        Assert.Single(fx.Extractor.AnalyzedTexts, Statement);
        Assert.Equal(1, fx.Extractor.RelatedCalls);
    }

    [Fact]
    public async Task Empty_statement_gives_an_empty_scope_but_the_send_scope_still_applies()
    {
        var fx = new GateFixture();
        var session = Session(statement: null, send: new AgentSendScope(false, ["APP.TB_C*"]));
        AssertDenied(await Call(fx, session, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}"""), "scope");
        Assert.False((await Call(fx, session, AgentToolNames.DescribeObject, """{"object":"TB_CUST"}""")).Result.IsError);
    }

    // ---------------------------------------------------------------- SQL 인자

    [Theory]
    [InlineData("SELECT * FROM TB_ORDER FOR UPDATE", "sql_unsafe")]
    [InlineData("SELECT * FROM TB_ORDER@REMOTE_DB", "sql_unsafe")]
    [InlineData("SELECT SEQ_ORDER.NEXTVAL FROM TB_ORDER", "sql_unsafe")]
    [InlineData("SELECT DBMS_LOCK.SLEEP(1) FROM TB_ORDER", "sql_unsafe")]
    [InlineData("DELETE FROM TB_ORDER", "sql_not_single_query")]
    [InlineData("SELECT 1 FROM TB_ORDER; SELECT 2 FROM TB_ORDER", "sql_not_single_query")]
    [InlineData("   ", "sql_not_single_query")]
    [InlineData("SELECT * FROM HR.SALARY", "scope")]
    public async Task Unsafe_or_out_of_scope_sql_is_denied(string sql, string code)
    {
        var fx = new GateFixture();
        var args = new JsonObject { ["sql"] = sql }.ToJsonString();
        AssertDenied(await Call(fx, Session(), AgentToolNames.ExplainPlan, args), code);
        Assert.Empty(fx.Handlers[AgentToolNames.ExplainPlan].Calls);
    }

    [Fact]
    public async Task A_plain_select_over_in_scope_objects_is_allowed()
    {
        var fx = new GateFixture();
        var args = new JsonObject { ["sql"] = "SELECT o.id FROM TB_ORDER o JOIN APP.TB_CUST c ON c.id = o.cust_id" }.ToJsonString();
        Assert.False((await Call(fx, Session(), AgentToolNames.ExplainPlan, args)).Result.IsError);
    }

    [Fact]
    public async Task One_bad_benchmark_variant_denies_the_whole_call()
    {
        var fx = new GateFixture();
        var session = Session();
        var good = """{"label":"base","sql":"SELECT * FROM TB_ORDER"}""";
        var bad = """{"label":"rewrite","sql":"SELECT * FROM TB_ORDER FOR UPDATE"}""";
        var outside = """{"label":"other","sql":"SELECT * FROM HR.SALARY"}""";

        Assert.False((await Call(fx, session, AgentToolNames.BenchmarkQueries, $$"""{"variants":[{{good}}]}""")).Result.IsError);
        AssertDenied(await Call(fx, session, AgentToolNames.BenchmarkQueries, $$"""{"variants":[{{good}},{{bad}}]}"""), "sql_unsafe");
        AssertDenied(await Call(fx, session, AgentToolNames.BenchmarkQueries, $$"""{"variants":[{{good}},{{outside}}]}"""), "scope");
        Assert.Single(fx.Handlers[AgentToolNames.BenchmarkQueries].Calls);
    }

    [Fact]
    public async Task Tuning_context_needs_a_statement_and_checks_sql_and_sql_id_separately()
    {
        var fx = new GateFixture();
        AssertDenied(await Call(fx, Session(statement: null), AgentToolNames.GetTuningContext, "{}"), "no_statement");
        Assert.False((await Call(fx, Session(), AgentToolNames.GetTuningContext, "{}")).Result.IsError);

        var session = Session(sqlId: "known00000001");
        var both = new JsonObject { ["sql"] = "SELECT * FROM HR.SALARY", ["sql_id"] = "known00000001" }.ToJsonString();
        AssertDenied(await Call(fx, session, AgentToolNames.GetTuningContext, both), "scope");
        var unknownId = new JsonObject { ["sql"] = "SELECT * FROM TB_ORDER", ["sql_id"] = "nope000000001" }.ToJsonString();
        AssertDenied(await Call(fx, session, AgentToolNames.GetTuningContext, unknownId), "sql_id_scope");
    }

    // ---------------------------------------------------------------- sql_id

    [Fact]
    public async Task Context_sql_id_is_allowed_unknown_is_denied_and_handler_sql_ids_widen_later_calls()
    {
        var fx = new GateFixture();
        var session = Session(sqlId: "ctx0000000001");
        fx.Handlers[AgentToolNames.BenchmarkQueries].Respond = _ => new AgentToolOutput { Result = new JsonObject(), SqlIds = ["bench00000001"] };

        Assert.False((await Call(fx, session, AgentToolNames.GetCursorPlan, """{"sql_id":"ctx0000000001"}""")).Result.IsError);
        AssertDenied(await Call(fx, session, AgentToolNames.GetCursorPlan, """{"sql_id":"bench00000001"}"""), "sql_id_scope");

        var variants = """{"variants":[{"label":"a","sql":"SELECT * FROM TB_ORDER"}]}""";
        Assert.False((await Call(fx, session, AgentToolNames.BenchmarkQueries, variants)).Result.IsError);
        Assert.False((await Call(fx, session, AgentToolNames.GetSqlStats, """{"sql_id":"bench00000001"}""")).Result.IsError);
    }

    [Fact]
    public async Task Failed_handler_calls_do_not_remember_sql_ids()
    {
        var fx = new GateFixture();
        var session = Session();
        fx.Handlers[AgentToolNames.BenchmarkQueries].Respond = _ => new AgentToolOutput { ErrorMessage = "ORA-00942", SqlIds = ["bench00000001"] };

        await Call(fx, session, AgentToolNames.BenchmarkQueries, """{"variants":[{"label":"a","sql":"SELECT * FROM TB_ORDER"}]}""");
        Assert.False(session.IsKnownSqlId("bench00000001"));
    }

    // ---------------------------------------------------------------- EXPLAIN PLAN 설정

    [Fact]
    public async Task Explain_plan_is_denied_and_unlisted_when_the_connection_does_not_allow_it()
    {
        var fx = new GateFixture();
        var session = Session(Dev(AiAccessLevel.QueryRows, explainPlan: false));
        AssertDenied(await Call(fx, session, AgentToolNames.ExplainPlan, """{"sql":"SELECT * FROM TB_ORDER"}"""), "explain_plan_disabled");
        Assert.DoesNotContain(fx.Gate.ListTools(session), t => t.Name == AgentToolNames.ExplainPlan);

        Assert.Contains(fx.Gate.ListTools(Session()), t => t.Name == AgentToolNames.ExplainPlan);
    }

    // ---------------------------------------------------------------- ListTools

    [Fact]
    public void List_tools_follows_level_environment_and_settings()
    {
        var fx = new GateFixture();
        string[] Names(AgentSession s) => [.. fx.Gate.ListTools(s).Select(t => t.Name)];

        Assert.Equal([AgentToolNames.DescribeObject], Names(Session(Dev(AiAccessLevel.Metadata))));
        Assert.Empty(Names(Session(Dev(AiAccessLevel.Off))));
        Assert.Equal(9, Names(Session(Dev(AiAccessLevel.QueryRows))).Length);

        var plan = Names(Session(Dev(AiAccessLevel.Plan)));
        Assert.Equal(8, plan.Length);
        Assert.DoesNotContain(AgentToolNames.BenchmarkQueries, plan);

        Assert.DoesNotContain(AgentToolNames.BenchmarkQueries, Names(Session(Prod(AiAccessLevel.Plan))));
        Assert.Contains(AgentToolNames.BenchmarkQueries, Names(Session(Prod(AiAccessLevel.Query))));
        Assert.Contains(AgentToolNames.GetHistogram, Names(Session(Prod(AiAccessLevel.Plan))));
        Assert.Contains(AgentToolNames.ExplainPlan, Names(Session(Prod(AiAccessLevel.Plan)))); // 운영도 EXPLAIN PLAN 기본 켬
        Assert.DoesNotContain(AgentToolNames.ExplainPlan, Names(Session(new ConnectionProfile("p", "u", "h/s", Environment: ConnectionEnvironment.Production, AiAccess: AiAccessLevel.Plan, AllowExplainPlan: false))));
    }

    // ---------------------------------------------------------------- 결과 가림

    private const string HistogramResult =
        """{"table":"APP.TB_ORDER","column":"STAT_CD","kind":"FREQUENCY","buckets":[{"endpoint":"A","count":10},{"endpoint":"B","count":5}]}""";

    [Fact]
    public async Task Plan_level_removes_histogram_endpoints_and_reports_masked()
    {
        var fx = new GateFixture();
        fx.Handlers[AgentToolNames.GetHistogram].Respond = _ => AgentToolOutput.Ok(JsonNode.Parse(HistogramResult)!);

        var response = await Call(fx, Session(Dev(AiAccessLevel.Plan)), AgentToolNames.GetHistogram, """{"table":"TB_ORDER","column":"STAT_CD"}""");

        Assert.False(response.Result.IsError);
        Assert.Equal(AiToolJudgement.Masked, response.Judgement);
        Assert.Equal("masked", response.Note);
        var body = JsonNode.Parse(response.Result.Text)!;
        Assert.Equal(2, body["buckets"]!.AsArray().Count);
        Assert.Equal(10, (int)body["buckets"]![0]!["count"]!);
        Assert.DoesNotContain("endpoint", response.Result.Text, StringComparison.Ordinal);
        Assert.Equal(AiToolJudgement.Masked, Assert.Single(fx.Audit.Entries).Verdict);
        Assert.Equal("masked", fx.Audit.Entries[0].Reason);
    }

    [Fact]
    public async Task Query_rows_keeps_ordinary_columns_but_removes_sensitive_ones()
    {
        var fx = new GateFixture();
        var session = Session(Dev(AiAccessLevel.QueryRows));
        fx.Handlers[AgentToolNames.GetHistogram].Respond = _ => AgentToolOutput.Ok(JsonNode.Parse(HistogramResult)!);

        var plain = await Call(fx, session, AgentToolNames.GetHistogram, """{"table":"TB_ORDER","column":"STAT_CD"}""");
        Assert.Equal(AiToolJudgement.Allowed, plain.Judgement);
        Assert.Contains("\"endpoint\":\"A\"", plain.Result.Text, StringComparison.Ordinal);

        var sensitive = await Call(fx, session, AgentToolNames.GetHistogram, """{"table":"TB_ORDER","column":"JUMIN_NO"}""");
        Assert.Equal(AiToolJudgement.Masked, sensitive.Judgement);
        Assert.DoesNotContain("endpoint", sensitive.Result.Text, StringComparison.Ordinal);
        Assert.Contains("\"count\":10", sensitive.Result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_rows_removes_low_and_high_values_only_for_sensitive_named_columns()
    {
        var fx = new GateFixture();
        fx.Handlers[AgentToolNames.GetTableStats].Respond = _ => AgentToolOutput.Ok(JsonNode.Parse(
            """{"table":"APP.TB_ORDER","columns":[{"name":"STAT_CD","low_value":"A","high_value":"Z"},{"name":"CUST_EMAIL","low_value":"a@x","stats":{"high_value":"z@x"}}]}""")!);

        var response = await Call(fx, Session(), AgentToolNames.GetTableStats, """{"table":"TB_ORDER"}""");

        Assert.Equal(AiToolJudgement.Masked, response.Judgement);
        var columns = JsonNode.Parse(response.Result.Text)!["columns"]!.AsArray();
        Assert.Equal("A", (string?)columns[0]!["low_value"]);
        Assert.Equal("Z", (string?)columns[0]!["high_value"]);
        Assert.Null(columns[1]!["low_value"]);
        Assert.Null(columns[1]!["stats"]!["high_value"]);
        Assert.Equal("CUST_EMAIL", (string?)columns[1]!["name"]);
    }

    [Fact]
    public async Task Below_query_rows_every_row_value_field_is_removed_at_any_depth_ignoring_case()
    {
        var fx = new GateFixture();
        fx.Handlers[AgentToolNames.GetTuningContext].Respond = _ => AgentToolOutput.Ok(JsonNode.Parse(
            """{"objects":[{"columns":[{"hist":{"buckets":[{"Endpoint_Value":"x","ENDPOINT_ACTUAL_VALUE":"y","keep":1}]},"low_value":"l","High_Value":"h"}]}],"endpoint":"top"}""")!);

        var response = await Call(fx, Session(Dev(AiAccessLevel.Plan)), AgentToolNames.GetTuningContext, "{}");

        Assert.Equal(AiToolJudgement.Masked, response.Judgement);
        foreach (var leaked in LeakableFields)
        {
            Assert.DoesNotContain(leaked, response.Result.Text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("\"keep\":1", response.Result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Masking_does_not_touch_the_node_the_handler_returned()
    {
        var fx = new GateFixture();
        var shared = JsonNode.Parse(HistogramResult)!;
        fx.Handlers[AgentToolNames.GetHistogram].Respond = _ => AgentToolOutput.Ok(shared);

        await Call(fx, Session(Dev(AiAccessLevel.Plan)), AgentToolNames.GetHistogram, """{"table":"TB_ORDER","column":"STAT_CD"}""");
        Assert.Equal("A", (string?)shared["buckets"]![0]!["endpoint"]);
    }

    [Theory]
    [InlineData("JUMIN_NO", true)]
    [InlineData("rrn", true)]
    [InlineData("CUST_SSN", true)]
    [InlineData("RESIDENT_ID", true)]
    [InlineData("USER_PASSWD", true)]
    [InlineData("password_hash", true)]
    [InlineData("PWD", true)]
    [InlineData("API_SECRET", true)]
    [InlineData("ACCESS_TOKEN", true)]
    [InlineData("PHONE_NO", true)]
    [InlineData("TEL_NO", true)]
    [InlineData("MOBILE", true)]
    [InlineData("HP_NO", true)]
    [InlineData("EMAIL_ADDR", true)]
    [InlineData("MAIL_ADDR", true)]
    [InlineData("ACCT_NO", true)]
    [InlineData("BANK_ACCOUNT", true)]
    [InlineData("CARD_NO", true)]
    [InlineData("IBAN", true)]
    [InlineData("BIRTH_DT", true)]
    [InlineData("ACNT_NO", true)]
    [InlineData("ACNO", true)]
    [InlineData("HPNO", true)]
    [InlineData("MBL_NO", true)]
    [InlineData("E_MAIL", true)]
    [InlineData("CUST_NM", true)]
    [InlineData("CUSTNM", true)]
    [InlineData("ADDR1", true)]
    [InlineData("RES_NO", true)]
    [InlineData("PSWD", true)]
    [InlineData("CRD_NO", true)]
    [InlineData("SYS_NC00012$", true)]
    [InlineData("sys_nc00012$", true)]
    [InlineData("SYS_STU6Q2", true)]
    [InlineData("SYS_STSX1", true)]
    [InlineData("SYS_NC00012", false)]
    [InlineData("HOTEL_NM", false)]
    [InlineData("TELLER_ID", false)]
    [InlineData("DISCARD_YN", false)]
    [InlineData("CURRENT_DT", false)]
    [InlineData("HOTEL_TEL_NO", true)]
    [InlineData("CURRENT_ADDR", true)]
    [InlineData("STAT_CD", false)]
    [InlineData("ORDER_DT", false)]
    [InlineData("AMOUNT", false)]
    [InlineData("HPX", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Sensitive_column_names(string? name, bool expected) =>
        Assert.Equal(expected, SensitiveColumns.IsSensitive(name));

    // ---------------------------------------------------------------- 감사 기록·사건

    [Fact]
    public async Task Every_call_is_audited_once_and_sql_text_never_reaches_the_summary()
    {
        var fx = new GateFixture(new FixedTime());
        var session = Session();
        var secret = "SELECT * FROM TB_ORDER WHERE note = 'secret-literal-123'";

        var ok = await Call(fx, session, AgentToolNames.ExplainPlan, new JsonObject { ["sql"] = secret }.ToJsonString());
        var denied = await Call(fx, session, AgentToolNames.DescribeObject, """{"object":"HR.SALARY"}""");
        var variants = """{"variants":[{"label":"a","sql":"SELECT * FROM TB_ORDER WHERE x = 'v1'"},{"label":"b","sql":"SELECT * FROM TB_ORDER WHERE x = 'v2'"}],"repeat":2}""";
        await Call(fx, session, AgentToolNames.BenchmarkQueries, variants);

        Assert.False(ok.Result.IsError);
        Assert.Equal(AiToolJudgement.Denied, denied.Judgement);
        Assert.Equal(3, fx.Audit.Entries.Count);

        var first = fx.Audit.Entries[0];
        Assert.Equal($"sql=<{secret.Length} chars>", first.Arguments);
        Assert.DoesNotContain("secret", first.Arguments, StringComparison.Ordinal);
        Assert.Equal(AgentToolNames.ExplainPlan, first.Tool);
        Assert.Equal(AiToolJudgement.Allowed, first.Verdict);
        Assert.Equal("dev", first.ProfileName);
        Assert.Equal("test-client", first.Client);
        Assert.Equal("run-1", first.RunId);
        Assert.Equal(AiAccessLevel.QueryRows, first.AccessLevel);
        Assert.Equal(FixedTime.Now, first.OccurredAt);
        Assert.Equal(ok.Result.Text.Length, first.ResultChars);
        Assert.NotNull(first.ElapsedMs);

        var second = fx.Audit.Entries[1];
        Assert.Equal("scope", second.Reason);
        Assert.Equal("object=HR.SALARY", second.Arguments);
        Assert.Equal(denied.Result.Text.Length, second.ResultChars);

        var third = fx.Audit.Entries[2];
        Assert.Equal("variants=2, repeat=2", third.Arguments);
    }

    [Fact]
    public async Task Audit_summary_uses_the_resolved_name_and_is_capped()
    {
        var fx = new GateFixture();
        var session = Session();
        await Call(fx, session, AgentToolNames.GetHistogram, """{"table":"tb_order","column":"STAT_CD"}""");
        Assert.Equal("table=APP.TB_ORDER, column=STAT_CD", fx.Audit.Entries[0].Arguments);

        var columns = new JsonArray([.. Enumerable.Range(0, 200).Select(i => (JsonNode?)JsonValue.Create("COLUMN_" + i))]);
        await Call(fx, session, AgentToolNames.GetTableStats, new JsonObject { ["table"] = "TB_ORDER", ["columns"] = columns }.ToJsonString());
        var arguments = fx.Audit.Entries[1].Arguments!;
        Assert.True(arguments.Length <= 400);
        Assert.StartsWith("table=APP.TB_ORDER, columns=COLUMN_0|COLUMN_1", arguments, StringComparison.Ordinal);
        Assert.True(arguments.Length < 400, "columns is clipped to 100 characters");
    }

    [Fact]
    public async Task Audit_failure_does_not_block_the_result_and_is_noted()
    {
        var fx = new GateFixture();
        fx.Audit.Fail = true;

        var response = await Call(fx, Session(), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");

        Assert.False(response.Result.IsError);
        Assert.Contains("audit_failed", response.Note, StringComparison.Ordinal);
        var activity = Assert.Single(fx.Events);
        Assert.Contains("audit_failed", activity.Note, StringComparison.Ordinal);

        var denied = await Call(fx, Session(), AgentToolNames.DescribeObject, """{"object":"HR.SALARY"}""");
        Assert.Equal("scope, audit_failed", denied.Note);
    }

    [Fact]
    public async Task Handler_exception_becomes_failed_with_a_short_message_and_cancellation_propagates()
    {
        var fx = new GateFixture();
        var session = Session();
        var handler = fx.Handlers[AgentToolNames.DescribeObject];
        handler.Respond = _ => throw new InvalidOperationException(new string('m', 500));

        var failed = await Call(fx, session, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        Assert.Equal(AiToolJudgement.Failed, failed.Judgement);
        Assert.True(failed.Result.IsError);
        Assert.Contains("InvalidOperationException", failed.Result.Text, StringComparison.Ordinal);
        Assert.Contains(new string('m', 200), failed.Result.Text, StringComparison.Ordinal);
        Assert.True(((string)JsonNode.Parse(failed.Result.Text)!["error"]!).Length <= 300);
        Assert.Equal(AiToolJudgement.Failed, fx.Audit.Entries[0].Verdict);

        // 호출자가 취소한 경우만 취소로 전파한다.
        using var cts = new CancellationTokenSource();
        handler.Respond = _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => fx.Gate.CallToolAsync(session, AgentToolNames.DescribeObject, JsonNode.Parse("""{"object":"TB_ORDER"}"""), cts.Token));

        // 취소는 전파하지만 감사 한 줄과 사건은 남는다(DB 에 이미 나간 호출을 기록에서 놓치지 않는다).
        Assert.Equal(2, fx.Audit.Entries.Count);
        Assert.Equal(AiToolJudgement.Failed, fx.Audit.Entries[1].Verdict);
        Assert.Equal("canceled", fx.Audit.Entries[1].Reason);
        Assert.Equal([1L, 2L], fx.Events.Select(e => e.Sequence).ToArray());
        Assert.Equal("canceled", fx.Events[1].Note);
    }

    [Fact]
    public async Task Handler_error_output_and_missing_handler_are_failed()
    {
        var fx = new GateFixture();
        var session = Session();
        fx.Handlers[AgentToolNames.DescribeObject].Respond = _ => AgentToolOutput.Error("ORA-00942: table or view does not exist");

        var failed = await Call(fx, session, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        Assert.Equal(AiToolJudgement.Failed, failed.Judgement);
        Assert.Contains("ORA-00942", failed.Result.Text, StringComparison.Ordinal);

        var bare = new GateFixture(handlers: []);
        var missing = await Call(bare, session, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        Assert.Equal(AiToolJudgement.Failed, missing.Judgement);
        Assert.Equal("not_implemented", Code(missing));
    }

    [Fact]
    public async Task Failed_call_records_only_the_ora_number_as_the_failure_kind()
    {
        var fx = new GateFixture();
        var session = Session();
        var handler = fx.Handlers[AgentToolNames.DescribeObject];

        handler.Respond = _ => AgentToolOutput.Error("조회에 실패했습니다 (ORA-00942). password=hunter2");
        var failed = await Call(fx, session, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        handler.Respond = _ => throw new InvalidOperationException("ORA-01031: insufficient privileges at db01:1521");
        var thrown = await Call(fx, session, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        handler.Respond = _ => AgentToolOutput.Error("조회에 실패했습니다.");
        var plain = await Call(fx, session, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");

        Assert.Equal("tool_failed: ORA-00942", fx.Audit.Entries[0].Reason);
        Assert.Equal("tool_failed: ORA-00942", failed.Note);
        Assert.Equal("tool_exception: ORA-01031", fx.Audit.Entries[1].Reason);
        Assert.Contains("ORA-01031", thrown.Result.Text, StringComparison.Ordinal);
        Assert.Equal("tool_failed", fx.Audit.Entries[2].Reason);
        Assert.Equal("tool_failed", plain.Note);
        Assert.All(fx.Audit.Entries, e => Assert.DoesNotContain("hunter2", e.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Oversized_result_is_replaced_by_a_narrowing_hint()
    {
        var fx = new GateFixture();
        fx.Handlers[AgentToolNames.DescribeObject].Respond = _ => AgentToolOutput.Ok(new JsonObject { ["blob"] = new string('x', 90_000) });

        var response = await Call(fx, Session(), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");

        Assert.True(response.Result.IsError);
        Assert.Equal(AiToolJudgement.Failed, response.Judgement);
        Assert.Equal("result_too_large", Code(response));
        Assert.Contains("narrow the request", response.Result.Text, StringComparison.Ordinal);
        Assert.True(response.Result.Text.Length < 1_000);
    }

    [Fact]
    public async Task Completed_events_carry_increasing_sequence_numbers_in_call_order()
    {
        var fx = new GateFixture();
        var session = Session();

        await Call(fx, session, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        await Call(fx, session, "nope");
        await Call(fx, session, AgentToolNames.DescribeObject, """{"object":"HR.SALARY"}""");

        Assert.Equal([1L, 2L, 3L], fx.Events.Select(e => e.Sequence).ToArray());
        Assert.All(fx.Events, e => Assert.Equal("run-1", e.RunId));
        Assert.Equal(["describe_object", "nope", "describe_object"], fx.Events.Select(e => e.ToolName).ToArray());
        Assert.Equal(
            [AiToolJudgement.Allowed, AiToolJudgement.Denied, AiToolJudgement.Denied],
            fx.Events.Select(e => e.Judgement).ToArray());
        Assert.Equal("""{"ok":true}""".Length, fx.Events[0].Size);
    }

    [Fact]
    public void Two_handlers_for_one_tool_are_a_wiring_error()
    {
        var one = new FakeHandler(AgentToolNames.DescribeObject);
        var two = new FakeHandler(AgentToolNames.DescribeObject);
        Assert.Throws<ArgumentException>(() => new AgentGate(new FakeSource(), new FakeExtractor(), [one, two], new FakeAudit()));
    }


    // ---------------------------------------------------------------- 풀어 쓴 SQL·현재 스키마

    [Fact]
    public async Task Normalized_sql_replaces_the_original_for_the_handler_including_variants()
    {
        var fx = new GateFixture();
        fx.Extractor.Normalize = sql => sql.Replace("TB_ORDER", "\"APP\".\"TB_ORDER\"", StringComparison.Ordinal);

        await Call(fx, Session(), AgentToolNames.ExplainPlan, """{"sql":"SELECT * FROM TB_ORDER"}""");
        Assert.Equal("SELECT * FROM \"APP\".\"TB_ORDER\"", (string?)fx.Handlers[AgentToolNames.ExplainPlan].Calls[0].Arguments["sql"]);

        await Call(fx, Session(), AgentToolNames.BenchmarkQueries, """{"variants":[{"label":"a","sql":"SELECT * FROM TB_ORDER"}]}""");
        var variant = fx.Handlers[AgentToolNames.BenchmarkQueries].Calls[0].Arguments["variants"]![0]!;
        Assert.Equal("SELECT * FROM \"APP\".\"TB_ORDER\"", (string?)variant["sql"]);
        Assert.Equal("a", (string?)variant["label"]);
    }

    [Fact]
    public async Task Without_normalized_sql_a_foreign_current_schema_is_refused_and_the_default_schema_passes_the_original()
    {
        var fx = new GateFixture();
        var args = """{"sql":"SELECT * FROM TB_ORDER"}""";

        AssertDenied(await Call(fx, Session(currentSchema: "OTHER"), AgentToolNames.ExplainPlan, args), "sql_schema_unresolved");
        Assert.Empty(fx.Handlers[AgentToolNames.ExplainPlan].Calls);

        Assert.False((await Call(fx, Session(currentSchema: "Scott"), AgentToolNames.ExplainPlan, args)).Result.IsError);
        Assert.Equal("SELECT * FROM TB_ORDER", (string?)fx.Handlers[AgentToolNames.ExplainPlan].Calls[0].Arguments["sql"]);
    }

    // ---------------------------------------------------------------- 행 값 가림 강화

    [Fact]
    public async Task Row_value_field_names_match_in_camel_pascal_and_dashed_forms()
    {
        var fx = new GateFixture();
        fx.Handlers[AgentToolNames.GetHistogram].Respond = _ => AgentToolOutput.Ok(JsonNode.Parse(
            """{"buckets":[{"endpointValue":"x","EndpointActualValue":"y","low-value":"l","HighValue":"h","Endpoint":"e","count":1}]}""")!);

        var response = await Call(fx, Session(Dev(AiAccessLevel.Plan)), AgentToolNames.GetHistogram, """{"table":"TB_ORDER","column":"STAT_CD"}""");

        Assert.Equal(AiToolJudgement.Masked, response.Judgement);
        Assert.Equal("""{"buckets":[{"count":1}]}""", response.Result.Text);
    }

    [Fact]
    public async Task Results_that_cannot_be_masked_are_withheld_below_query_rows()
    {
        var fx = new GateFixture();
        var handler = fx.Handlers[AgentToolNames.DescribeObject];
        var plan = Session(Dev(AiAccessLevel.Plan));

        handler.Respond = _ => AgentToolOutput.Ok(JsonValue.Create("plain text")!);
        AssertDenied(await Call(fx, plan, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}"""), "unsafe_result");

        handler.Respond = _ => AgentToolOutput.Ok(JsonValue.Create(42)!);
        AssertDenied(await Call(fx, plan, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}"""), "unsafe_result");

        // JSON 으로 읽히지 않는 글과 두 글자 미만의 글은 통과한다.
        handler.Respond = _ => AgentToolOutput.Ok(new JsonObject { ["comment"] = "[deprecated] use X", ["one"] = "{" });
        Assert.False((await Call(fx, plan, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""")).Result.IsError);

        // QueryRows 에서는 루트 모양 검사를 하지 않는다.
        handler.Respond = _ => AgentToolOutput.Ok(JsonValue.Create("plain text")!);
        Assert.False((await Call(fx, Session(), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""")).Result.IsError);
    }

    [Fact]
    public async Task Sensitive_columns_are_found_by_column_name_keys_and_nested_column_objects()
    {
        var fx = new GateFixture();
        fx.Handlers[AgentToolNames.GetTableStats].Respond = _ => AgentToolOutput.Ok(JsonNode.Parse(
            """{"columns":[{"column_name":"JUMIN_NO","low_value":"1"},{"column":{"name":"CARD_NO"},"high_value":"2","stats":{"low_value":"3"}},{"ColumnName":"STAT_CD","low_value":"A"},{"Name":"SYS_NC00012$","high_value":"z"}]}""")!);

        var response = await Call(fx, Session(), AgentToolNames.GetTableStats, """{"table":"TB_ORDER"}""");

        Assert.Equal(AiToolJudgement.Masked, response.Judgement);
        var columns = JsonNode.Parse(response.Result.Text)!["columns"]!.AsArray();
        Assert.Null(columns[0]!["low_value"]);
        Assert.Null(columns[1]!["high_value"]);
        Assert.Null(columns[1]!["stats"]!["low_value"]);
        Assert.Equal("A", (string?)columns[2]!["low_value"]);
        Assert.Null(columns[3]!["high_value"]);
    }

    // ---------------------------------------------------------------- 오류 글

    [Fact]
    public async Task Below_query_rows_handler_errors_keep_only_the_ora_number()
    {
        var fx = new GateFixture();
        fx.Handlers[AgentToolNames.DescribeObject].Respond = _ => AgentToolOutput.Error("ORA-12541: no listener at db01.corp:1521 (10.1.2.3) value 'secret-123'");

        var response = await Call(fx, Session(Dev(AiAccessLevel.Plan)), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");

        Assert.Equal(AiToolJudgement.Failed, response.Judgement);
        var error = (string?)JsonNode.Parse(response.Result.Text)!["error"];
        Assert.Equal("ORA-12541: The tool failed; details are available in the Oranda log.", error);
        Assert.DoesNotContain("secret", response.Result.Text, StringComparison.Ordinal);

        fx.Handlers[AgentToolNames.DescribeObject].Respond = _ => throw new InvalidOperationException("password=hunter2 at db01:1521");
        var thrown = await Call(fx, Session(Dev(AiAccessLevel.Plan)), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        Assert.Contains("InvalidOperationException", thrown.Result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", thrown.Result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_rows_error_text_drops_hosts_ips_paths_and_newlines_and_is_capped()
    {
        var fx = new GateFixture();
        var handler = fx.Handlers[AgentToolNames.DescribeObject];
        handler.Respond = _ => AgentToolOutput.Error("ORA-12541: cannot reach db01.corp:1521 at 10.1.2.3\r\nlog C:\\app\\oracle\\x.log and \\\\fs01\\share\\y.trc end");

        var response = await Call(fx, Session(), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        var error = (string)JsonNode.Parse(response.Result.Text)!["error"]!;

        Assert.StartsWith("ORA-12541", error, StringComparison.Ordinal);
        foreach (var leaked in LeakedFromErrors)
        {
            Assert.DoesNotContain(leaked, error, StringComparison.Ordinal);
        }

        handler.Respond = _ => AgentToolOutput.Error(new string('x', 1_000));
        var capped = await Call(fx, Session(), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        Assert.Equal(300, ((string)JsonNode.Parse(capped.Result.Text)!["error"]!).Length);
    }

    // ---------------------------------------------------------------- 하위 동작 허용 값

    [Theory]
    [InlineData(AiAccessLevel.Plan, false, null, true, false)]
    [InlineData(AiAccessLevel.Plan, false, false, false, false)]
    [InlineData(AiAccessLevel.QueryRows, false, null, true, true)]
    [InlineData(AiAccessLevel.Query, false, false, false, true)]
    [InlineData(AiAccessLevel.Plan, true, null, true, false)]
    [InlineData(AiAccessLevel.Plan, true, false, false, false)]
    [InlineData(AiAccessLevel.Query, true, null, true, true)]
    [InlineData(AiAccessLevel.Plan, true, true, true, false)]
    public async Task Handler_gets_the_explain_plan_and_execute_permissions(
        AiAccessLevel level, bool production, bool? allowExplain, bool expectExplain, bool expectExecute)
    {
        var fx = new GateFixture();
        var profile = new ConnectionProfile(
            "p", "u", "h/s",
            Environment: production ? ConnectionEnvironment.Production : ConnectionEnvironment.Development,
            AiAccess: level,
            AllowExplainPlan: allowExplain);

        var response = await Call(fx, Session(profile), AgentToolNames.GetTuningContext, "{}");

        Assert.False(response.Result.IsError);
        var invocation = Assert.Single(fx.Handlers[AgentToolNames.GetTuningContext].Calls);
        Assert.Equal(expectExplain, invocation.ExplainPlanAllowed);
        Assert.Equal(expectExecute, invocation.MayExecuteSql);
    }

    // ---------------------------------------------------------------- 폐기·취소·감사 시간

    [Fact]
    public async Task Revocation_during_the_handler_discards_the_result_and_does_not_remember_sql_ids()
    {
        var fx = new GateFixture();
        var session = Session();
        fx.Handlers[AgentToolNames.BenchmarkQueries].Respond = invocation =>
        {
            invocation.Session.Revoke();
            return new AgentToolOutput { Result = new JsonObject { ["rows"] = 1 }, SqlIds = ["bench00000001"] };
        };

        var response = await Call(fx, session, AgentToolNames.BenchmarkQueries, """{"variants":[{"label":"a","sql":"SELECT * FROM TB_ORDER"}]}""");

        AssertDenied(response, "run_revoked");
        Assert.DoesNotContain("rows", response.Result.Text, StringComparison.Ordinal);
        Assert.False(session.IsKnownSqlId("bench00000001"));
        Assert.Equal("run_revoked", Assert.Single(fx.Audit.Entries).Reason);
    }

    [Fact]
    public async Task Revoking_a_session_stops_a_handler_that_is_still_running()
    {
        var fx = new GateFixture();
        var session = Session();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.Handlers[AgentToolNames.DescribeObject].RespondAsync = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return AgentToolOutput.Ok(new JsonObject());
        };

        var pending = Call(fx, session, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Revoke();

        AssertDenied(await pending.WaitAsync(TimeSpan.FromSeconds(5)), "run_revoked");
    }

    [Fact]
    public async Task A_cancellation_that_arrives_after_the_handler_finished_still_leaves_the_audit_line_and_event()
    {
        var fx = new GateFixture();
        using var cts = new CancellationTokenSource();
        fx.Handlers[AgentToolNames.DescribeObject].Respond = _ =>
        {
            cts.Cancel();
            return AgentToolOutput.Ok(new JsonObject { ["ok"] = true });
        };

        var response = await fx.Gate.CallToolAsync(
            Session(), AgentToolNames.DescribeObject, JsonNode.Parse("""{"object":"TB_ORDER"}"""), cts.Token);

        Assert.False(response.Result.IsError);
        Assert.Single(fx.Audit.Entries);
        Assert.Single(fx.Events);
    }

    [Fact]
    public async Task A_handler_timeout_that_is_not_the_callers_cancellation_is_a_failure_not_a_cancellation()
    {
        var fx = new GateFixture();
        fx.Handlers[AgentToolNames.DescribeObject].Respond = _ => throw new TaskCanceledException("db timeout");

        var response = await Call(fx, Session(), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");

        Assert.Equal(AiToolJudgement.Failed, response.Judgement);
        Assert.Equal("tool_exception", Code(response));
    }

    [Fact]
    public async Task A_hung_audit_store_cannot_hold_the_result_back()
    {
        var fx = new GateFixture(auditTimeout: TimeSpan.FromMilliseconds(200));
        fx.Audit.Hang = true;

        var response = await Call(fx, Session(), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(response.Result.IsError);
        Assert.Contains("audit_failed", response.Note, StringComparison.Ordinal);
        Assert.Single(fx.Events);
    }

    [Fact]
    public async Task Tool_names_are_cleaned_of_control_and_line_separator_characters_in_audit_and_event()
    {
        var fx = new GateFixture();
        var name = "bad\nname" + (char)0x2028 + "x" + (char)0x85 + "y";

        await Call(fx, Session(), name);

        const string Expected = "bad name x y";
        Assert.Equal(Expected, Assert.Single(fx.Audit.Entries).Tool);
        Assert.Equal(Expected, Assert.Single(fx.Events).ToolName);
    }

    // ---------------------------------------------------------------- 직렬화·크기

    [Fact]
    public async Task Calls_of_one_session_never_overlap()
    {
        var fx = new GateFixture();
        var session = Session();
        var counter = new OverlapCounter();
        fx.Handlers[AgentToolNames.DescribeObject].RespondAsync = async (_, _) =>
        {
            counter.Enter();
            await Task.Delay(40, CancellationToken.None);
            counter.Leave();
            return AgentToolOutput.Ok(new JsonObject { ["ok"] = true });
        };

        var calls = Enumerable.Range(0, 4).Select(_ => Call(fx, session, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}"""));
        var responses = await Task.WhenAll(calls);

        Assert.All(responses, r => Assert.False(r.Result.IsError));
        Assert.Equal(1, counter.Max);
        Assert.Equal([1L, 2L, 3L, 4L], fx.Events.Select(e => e.Sequence).Order().ToArray());
    }

    [Fact]
    public async Task A_call_waiting_for_its_turn_is_denied_when_the_run_is_revoked()
    {
        var fx = new GateFixture();
        var session = Session();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.Handlers[AgentToolNames.DescribeObject].RespondAsync = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return AgentToolOutput.Ok(new JsonObject { ["ok"] = true });
        };

        var first = Call(fx, session, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Call(fx, session, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        session.Revoke();

        AssertDenied(await second.WaitAsync(TimeSpan.FromSeconds(5)), "run_revoked");
        release.SetResult();
        AssertDenied(await first.WaitAsync(TimeSpan.FromSeconds(5)), "run_revoked");
    }

    [Fact]
    public async Task Results_keep_korean_text_unescaped_and_long_arguments_are_refused()
    {
        var fx = new GateFixture();
        fx.Handlers[AgentToolNames.DescribeObject].Respond = _ => AgentToolOutput.Ok(new JsonObject { ["comment"] = "주문 상태" });

        var response = await Call(fx, Session(), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        Assert.Equal("""{"comment":"주문 상태"}""", response.Result.Text);

        var tooLong = new JsonObject { ["sql"] = "SELECT " + new string('x', 20_001) }.ToJsonString();
        AssertDenied(await Call(fx, Session(), AgentToolNames.ExplainPlan, tooLong), "invalid_arguments");
    }

    // ---------------------------------------------------------------- 검사 우회 경로

    [Theory]
    [InlineData("ABCDEFGHIJKL1")]
    [InlineData("short")]
    [InlineData("abcdefghijklmn")]
    [InlineData("abc defghijkl1")]
    public async Task Sql_id_arguments_must_have_the_oracle_format(string sqlId)
    {
        var fx = new GateFixture();
        var args = new JsonObject { ["sql_id"] = sqlId }.ToJsonString();
        AssertDenied(await Call(fx, Session(), AgentToolNames.GetCursorPlan, args), "invalid_arguments");
    }

    [Fact]
    public async Task A_db_session_of_another_connection_is_refused()
    {
        var fx = new GateFixture();
        fx.Source.Db = new FakeSession();
        var profile = new ConnectionProfile("dev", "scott", "h/s", AiAccess: AiAccessLevel.QueryRows, Id: Guid.NewGuid());

        AssertDenied(await Call(fx, Session(profile), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}"""), "session_mismatch");
        Assert.Empty(fx.Handlers[AgentToolNames.DescribeObject].Calls);

        // 같은 접속이거나 id 가 없으면 통과한다.
        var same = new ConnectionProfile("dev", "scott", "h/s", AiAccess: AiAccessLevel.QueryRows, Id: fx.Source.Db.Profile.Id);
        Assert.False((await Call(fx, Session(same), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""")).Result.IsError);
        Assert.False((await Call(fx, Session(), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""")).Result.IsError);
    }


    // ---------------------------------------------------------------- 재리뷰 반영: 대상 문장 검사·가림 우회·범위 계산·정화

    [Fact]
    public async Task Tuning_context_without_sql_checks_the_request_statement_and_hands_it_to_the_handler()
    {
        var fx = new GateFixture();

        // DML 문맥 문장은 sql 인자와 똑같이 거부된다.
        AssertDenied(await Call(fx, Session(statement: "DELETE FROM TB_ORDER"), AgentToolNames.GetTuningContext, "{}"), "sql_not_single_query");
        AssertDenied(await Call(fx, Session(statement: "SELECT * FROM TB_ORDER FOR UPDATE"), AgentToolNames.GetTuningContext, "{}"), "sql_unsafe");
        Assert.Empty(fx.Handlers[AgentToolNames.GetTuningContext].Calls);

        // SELECT 문맥 문장은 sql 로 채워지고 실행 허용이 참이다.
        fx.Extractor.Normalize = sql => sql.StartsWith("SELECT * FROM TB_ORDER", StringComparison.Ordinal) ? sql.Replace("TB_ORDER", "\"APP\".\"TB_ORDER\"", StringComparison.Ordinal) : null;
        Assert.False((await Call(fx, Session(), AgentToolNames.GetTuningContext, "{}")).Result.IsError);
        var invocation = Assert.Single(fx.Handlers[AgentToolNames.GetTuningContext].Calls);
        Assert.Contains("\"APP\".\"TB_ORDER\"", (string?)invocation.Arguments["sql"], StringComparison.Ordinal);
        Assert.True(invocation.MayExecuteSql);
    }

    [Fact]
    public async Task Tuning_context_with_only_a_sql_id_never_gets_the_execute_permission()
    {
        var fx = new GateFixture();
        var session = Session(sqlId: "known00000001");

        Assert.False((await Call(fx, session, AgentToolNames.GetTuningContext, """{"sql_id":"known00000001"}""")).Result.IsError);

        var invocation = Assert.Single(fx.Handlers[AgentToolNames.GetTuningContext].Calls);
        Assert.False(invocation.MayExecuteSql);
        Assert.Null(invocation.Arguments["sql"]);
    }

    [Fact]
    public async Task Normalized_sql_that_points_at_other_objects_is_refused()
    {
        var fx = new GateFixture();
        fx.Extractor.Normalize = sql => sql.Contains("TB_ORDER", StringComparison.Ordinal) ? "SELECT * FROM HR.SALARY" : null;

        AssertDenied(await Call(fx, Session(), AgentToolNames.ExplainPlan, """{"sql":"SELECT * FROM TB_ORDER"}"""), "sql_unsafe");
        Assert.Empty(fx.Handlers[AgentToolNames.ExplainPlan].Calls);
    }

    [Fact]
    public async Task Json_inside_strings_is_parsed_and_masked_at_every_level()
    {
        var fx = new GateFixture();
        var handler = fx.Handlers[AgentToolNames.DescribeObject];

        // Plan: 문자열 안의 끝값이 지워지고, 바뀐 문자열만 바뀐다.
        handler.Respond = _ => AgentToolOutput.Ok(new JsonObject { ["note"] = """{"endpoint":"A","n":1}""", ["other"] = "[1, 2]", ["plain"] = "{x" });
        var plan = await Call(fx, Session(Dev(AiAccessLevel.Plan)), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        Assert.Equal(AiToolJudgement.Masked, plan.Judgement);
        var body = JsonNode.Parse(plan.Result.Text)!;
        Assert.Equal("""{"n":1}""", (string?)body["note"]);
        Assert.Equal("[1, 2]", (string?)body["other"]);
        Assert.Equal("{x", (string?)body["plain"]);

        // QueryRows: 민감 열 객체 안의 JSON 글(깊이 둘)도 가려진다.
        handler.Respond = _ => AgentToolOutput.Ok(new JsonObject
        {
            ["columns"] = new JsonArray(new JsonObject
            {
                ["name"] = "JUMIN_NO",
                ["detail"] = new JsonObject { ["inner"] = """{"stats":"{\"low_value\":\"1\",\"k\":2}"}""" }.ToJsonString(),
            }),
        });
        var rows = await Call(fx, Session(), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        Assert.Equal(AiToolJudgement.Masked, rows.Judgement);
        Assert.DoesNotContain("low_value", rows.Result.Text, StringComparison.Ordinal);
        Assert.Contains("k", rows.Result.Text, StringComparison.Ordinal);

        // 정상 결과(열 주석이 JSON)는 거부되지 않고 그대로다.
        handler.Respond = _ => AgentToolOutput.Ok(new JsonObject { ["comment"] = """{ "kind": "code" }""" });
        var plain = await Call(fx, Session(Dev(AiAccessLevel.Plan)), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        Assert.Equal(AiToolJudgement.Allowed, plain.Judgement);
        Assert.Contains("""{ \"kind\": \"code\" }""", plain.Result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dictionary_shaped_results_and_all_column_identity_keys_are_masked_for_sensitive_columns()
    {
        var fx = new GateFixture();
        fx.Handlers[AgentToolNames.GetTableStats].Respond = _ => AgentToolOutput.Ok(JsonNode.Parse(
            """{"columns":{"JUMIN_NO":{"low_value":"1","n":2},"STAT_CD":{"low_value":"A"}},"list":[{"col_name":"CARD_NO","low_value":"1"},{"col":"주민번호","low_value":"2"},{"colname":"STAT_CD","low_value":"A"},{"column_nm":"EMAIL","high_value":"z"}]}""")!);

        var response = await Call(fx, Session(), AgentToolNames.GetTableStats, """{"table":"TB_ORDER"}""");

        Assert.Equal(AiToolJudgement.Masked, response.Judgement);
        var body = JsonNode.Parse(response.Result.Text)!;
        Assert.Null(body["columns"]!["JUMIN_NO"]!["low_value"]);
        Assert.Equal(2, (int)body["columns"]!["JUMIN_NO"]!["n"]!);
        Assert.Equal("A", (string?)body["columns"]!["STAT_CD"]!["low_value"]);
        var list = body["list"]!.AsArray();
        Assert.Null(list[0]!["low_value"]);
        Assert.Null(list[1]!["low_value"]);
        Assert.Equal("A", (string?)list[2]!["low_value"]);
        Assert.Null(list[3]!["high_value"]);
        Assert.Contains("column_nm", AgentFields.ColumnIdentityFields);
    }

    [Theory]
    [InlineData("주민번호", true)]
    [InlineData("전화번호", true)]
    [InlineData("휴대폰", true)]
    [InlineData("카드번호", true)]
    [InlineData("계좌", true)]
    [InlineData("비밀번호", true)]
    [InlineData("이메일", true)]
    [InlineData("생년월일", true)]
    [InlineData("여권번호", true)]
    [InlineData("주문번호", false)]
    public void Korean_column_names_are_sensitive(string name, bool expected) =>
        Assert.Equal(expected, SensitiveColumns.IsSensitive(name));

    [Fact]
    public async Task An_extractor_that_cancels_itself_ends_the_call_as_failed_after_one_attempt()
    {
        var fx = new GateFixture();
        fx.Extractor.ThrowOnAnalyze = text => text.Contains("boom", StringComparison.Ordinal);
        var session = Session(statement: "SELECT * FROM TB_ORDER -- boom");

        var response = await Call(fx, session, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AiToolJudgement.Failed, response.Judgement);
        Assert.Single(fx.Extractor.AnalyzedTexts, "SELECT * FROM TB_ORDER -- boom");
    }

    [Fact]
    public async Task Disposing_a_session_while_calls_run_or_wait_does_not_deadlock()
    {
        var fx = new GateFixture();
        var session = Session();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.Handlers[AgentToolNames.DescribeObject].RespondAsync = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return AgentToolOutput.Ok(new JsonObject { ["ok"] = true });
        };

        var running = Call(fx, session, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiting = Call(fx, session, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");

        session.Dispose();
        session.Dispose();

        AssertDenied(await waiting.WaitAsync(TimeSpan.FromSeconds(5)), "run_revoked");
        release.SetResult();
        AssertDenied(await running.WaitAsync(TimeSpan.FromSeconds(5)), "run_revoked");
        Assert.True(session.IsRevoked);
    }

    [Fact]
    public void Revoke_survives_a_throwing_cancellation_callback_and_the_store_drops_sibling_tokens_first()
    {
        var session = Session();
        session.RevocationToken.Register(() => throw new InvalidOperationException("callback failed"));
        session.Revoke();
        Assert.True(session.IsRevoked);

        var store = new AgentRunTokenStore();
        var other = Session();
        var first = store.Issue(other);
        store.Issue(other);
        var seenInCallback = -1;
        other.RevocationToken.Register(() => seenInCallback = store.Count);

        store.Revoke(first);

        Assert.Equal(0, seenInCallback);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Error_text_drops_listener_service_descriptor_and_unix_path_details_but_keeps_times()
    {
        var fx = new GateFixture();
        var handler = fx.Handlers[AgentToolNames.DescribeObject];
        handler.Respond = _ => AgentToolOutput.Error(
            "ORA-12541: No listener at host db01 port 1521; Service ORCLPDB is not registered; (DESCRIPTION=(HOST=db01)(PORT=1521)(SERVICE_NAME=ORCLPDB)); trace /u01/app/oracle/trace/x.trc at 09:30 and db01.corp:1521 and 10.1.2.3:1521");

        var response = await Call(fx, Session(), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        var error = (string)JsonNode.Parse(response.Result.Text)!["error"]!;

        foreach (var leaked in new[] { "db01", "1521", "ORCLPDB", "/u01", "10.1.2.3" })
        {
            Assert.DoesNotContain(leaked, error, StringComparison.Ordinal);
        }

        Assert.Contains("09:30", error, StringComparison.Ordinal);
        Assert.Contains("is not registered", error, StringComparison.Ordinal);

        // Plan 미만: Oracle 예외 계열은 오류 번호를 형 이름과 함께 남긴다.
        handler.Respond = _ => throw new OracleException("ORA-00942: table or view does not exist at db01:1521");
        var thrown = await Call(fx, Session(Dev(AiAccessLevel.Plan)), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        Assert.Contains("OracleException: ORA-00942", thrown.Result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("db01", thrown.Result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Results_do_not_escape_sql_punctuation_and_a_trailing_newline_fails_the_sql_id_format()
    {
        var fx = new GateFixture();
        fx.Handlers[AgentToolNames.DescribeObject].Respond = _ => AgentToolOutput.Ok(new JsonObject { ["text"] = "select * where a<b and c='x'" });
        var response = await Call(fx, Session(), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        Assert.Equal("""{"text":"select * where a<b and c='x'"}""", response.Result.Text);

        var args = new JsonObject { ["sql_id"] = "abcdefghijkl1\n" }.ToJsonString();
        AssertDenied(await Call(fx, Session(), AgentToolNames.GetCursorPlan, args), "invalid_arguments");
    }

    private sealed class OracleException(string message) : Exception(message);

    // ---------------------------------------------------------------- 가짜 객체

    private sealed class GateFixture
    {
        public GateFixture(TimeProvider? time = null, IEnumerable<IAgentToolHandler>? handlers = null, TimeSpan? auditTimeout = null, ISqlLiteralMasker? masker = null, bool noMasker = false,
            IUserFunctionSafetyChecker? userFunctions = null)
        {
            Handlers = AgentTools.All.ToDictionary(t => t.Name, t => new FakeHandler(t.Name), StringComparer.Ordinal);
            Gate = new AgentGate(Source, Extractor, handlers ?? Handlers.Values, Audit, time, noMasker ? null : masker ?? new FakeMasker(), userFunctions) { AuditTimeout = auditTimeout ?? TimeSpan.FromSeconds(5) };
            Gate.ToolCompleted += (_, e) => Events.Add(e);
        }

        public FakeSource Source { get; } = new();

        public FakeExtractor Extractor { get; } = new();

        public FakeAudit Audit { get; } = new();

        public Dictionary<string, FakeHandler> Handlers { get; }

        public List<AgentToolActivityEventArgs> Events { get; } = [];

        public AgentGate Gate { get; }
    }

    private sealed class OverlapCounter
    {
        private readonly object _gate = new();
        private int _running;

        public int Max { get; private set; }

        public void Enter()
        {
            lock (_gate)
            {
                _running++;
                Max = Math.Max(Max, _running);
            }
        }

        public void Leave()
        {
            lock (_gate)
            {
                _running--;
            }
        }
    }

    private sealed class FixedTime : TimeProvider
    {
        public static readonly DateTimeOffset Now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeSource : IAgentSessionSource
    {
        public FakeSession Db { get; set; } = new();

        public AgentSession? Find(string runId) => null;

        public ValueTask<ISession> AcquireDbSessionAsync(AgentSession session, CancellationToken ct) => ValueTask.FromResult<ISession>(Db);
    }

    private sealed class FakeHandler(string toolName) : IAgentToolHandler
    {
        public string ToolName { get; } = toolName;

        public List<AgentToolInvocation> Calls { get; } = [];

        public Func<AgentToolInvocation, AgentToolOutput> Respond { get; set; } = _ => AgentToolOutput.Ok(new JsonObject { ["ok"] = true });

        public Func<AgentToolInvocation, CancellationToken, Task<AgentToolOutput>>? RespondAsync { get; set; }

        public async ValueTask<AgentToolOutput> ExecuteAsync(AgentToolInvocation invocation, CancellationToken ct)
        {
            Calls.Add(invocation);
            return RespondAsync is null ? Respond(invocation) : await RespondAsync(invocation, ct);
        }
    }

    private sealed class FakeAudit : IAgentAuditStore
    {
        public List<AgentAuditEntry> Entries { get; } = [];

        public bool Fail { get; set; }

        /// <summary>토큰을 무시하고 영영 끝나지 않는 저장소.</summary>
        public bool Hang { get; set; }

        public ValueTask AppendAsync(AgentAuditEntry entry, CancellationToken ct)
        {
            if (Hang)
            {
                return new ValueTask(new TaskCompletionSource().Task);
            }

            if (Fail)
            {
                throw new IOException("disk full");
            }

            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<AgentAuditEntry>> ListRecentAsync(int limit, Guid? profileId, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<AgentAuditEntry>>(Entries);
    }

    /// <summary>
    /// 문장 글을 정규식으로 훑는 추출기. 아는 객체는 APP.TB_ORDER·APP.TB_CUST·REF.CODE_A·HR.SALARY 이고, TB_ORDER 의 상대는 TB_CUST 다.
    /// </summary>
    private sealed partial class FakeExtractor : IStatementReferenceExtractor
    {
        private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
        {
            "APP.TB_ORDER", "APP.TB_CUST", "REF.CODE_A", "HR.SALARY",
        };

        public List<string> AnalyzedTexts { get; } = [];

        /// <summary>문장 글을 풀어 쓴 글로 바꾸는 규칙(없으면 NormalizedSql 은 null).</summary>
        public Func<string, string?>? Normalize { get; set; }

        /// <summary>참이면 분석 중 스스로 시간 초과(TaskCanceledException)를 내는 문장 글 조건.</summary>
        public Func<string, bool>? ThrowOnAnalyze { get; set; }

        public int RelatedCalls { get; private set; }

        public ValueTask<AgentStatementInfo> AnalyzeAsync(ISession db, string sql, string? defaultSchema, CancellationToken ct)
        {
            AnalyzedTexts.Add(sql);
            if (ThrowOnAnalyze?.Invoke(sql) == true)
            {
                throw new TaskCanceledException("extractor timeout");
            }

            var upper = sql.ToUpperInvariant().Replace("\"", string.Empty, StringComparison.Ordinal);
            var objects = FromPattern().Matches(upper)
                .Select(m => ResolveName(m.Groups[1].Value, defaultSchema) ?? new AgentObjectRef(defaultSchema ?? "APP", m.Groups[1].Value))
                .ToList();
            var kind = StartsWithQuery().IsMatch(upper) ? StatementKind.Query : StatementKind.Dml;
            var count = sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
            // 분류: NEXTVAL·DBMS_/UTL_ 호출은 금지, 그 밖의 OWNER.NAME( 꼴은 사용자 함수 후보. UNCLASSIFIED_CALL 은 분류하지 못한 호출.
            var calls = QualifiedCall().Matches(upper).Select(m => m.Value.TrimEnd('(', ' ')).ToList();
            var forbidden = upper.Contains(".NEXTVAL", StringComparison.Ordinal)
                || calls.Any(c => c.StartsWith("DBMS_", StringComparison.Ordinal) || c.StartsWith("UTL_", StringComparison.Ordinal));
            var functions = calls.Where(c => !c.StartsWith("DBMS_", StringComparison.Ordinal) && !c.StartsWith("UTL_", StringComparison.Ordinal)).Distinct().ToList();
            var unclassified = upper.Contains("UNCLASSIFIED_CALL", StringComparison.Ordinal);
            return ValueTask.FromResult(new AgentStatementInfo(
                kind,
                false,
                objects,
                count,
                HasLockingClause: upper.Contains("FOR UPDATE", StringComparison.Ordinal),
                HasRemoteReference: upper.Contains('@', StringComparison.Ordinal),
                CallsUserFunction: forbidden || unclassified || functions.Count > 0,
                NormalizedSql: Normalize?.Invoke(sql),
                HasForbiddenCall: forbidden,
                UserFunctions: unclassified ? null : functions));
        }

        public ValueTask<AgentObjectRef?> ResolveAsync(ISession db, string name, string? defaultSchema, CancellationToken ct) =>
            ValueTask.FromResult(ResolveName(name, defaultSchema));

        public ValueTask<IReadOnlyList<AgentObjectRef>> GetRelatedAsync(ISession db, IReadOnlyList<AgentObjectRef> objects, CancellationToken ct)
        {
            RelatedCalls++;
            IReadOnlyList<AgentObjectRef> related = objects.Any(o => o.Key == "APP.TB_ORDER") ? [new AgentObjectRef("APP", "TB_CUST", "TABLE")] : [];
            return ValueTask.FromResult(related);
        }

        private static AgentObjectRef? ResolveName(string name, string? defaultSchema)
        {
            var parts = name.Trim().ToUpperInvariant().Split('.');
            var owner = parts.Length == 2 ? parts[0] : (defaultSchema ?? "APP").ToUpperInvariant();
            var table = parts[^1];
            return Known.Contains(owner + "." + table) ? new AgentObjectRef(owner, table, "TABLE") : null;
        }

        [GeneratedRegex(@"\b(?:FROM|JOIN)\s+([A-Z0-9_$.]+)")]
        private static partial Regex FromPattern();

        [GeneratedRegex(@"^\s*(?:SELECT|WITH)\b")]
        private static partial Regex StartsWithQuery();

        [GeneratedRegex(@"\b[A-Z_]+\.[A-Z_]+\s*\(")]
        private static partial Regex QualifiedCall();
    }
}
