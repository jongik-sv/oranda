using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;

namespace Oranda.Core.Tests.Ai;

// 리터럴 가림 통합(설계 ai-tuning-2): 인자 되돌림·literal_unknown·필수인데 없으면 닫기·술어 가림·감사 표시.
public partial class AgentGateTests
{
    private const string TargetSql = "SELECT * FROM TB_ORDER o WHERE o.stat_cd = 'A' AND o.cust_nm = 'KIM'";

    private static (AgentSession Session, LiteralVault Vault) MaskedSession(ConnectionProfile? profile = null)
    {
        var vault = new LiteralVault();
        var p = profile ?? Dev(AiAccessLevel.QueryRows);
        var ctx = new AiContext(p.Name, p.Environment, AiAccessLevel.QueryRows, StatementText: TargetSql);
        var session = new AgentSession(new AgentRunScope("run-m", p, ctx) { Literals = vault }) { Client = "test-client" };
        return (session, vault);
    }

    [Fact]
    public async Task Placeholders_in_sql_arguments_are_restored_before_the_scope_check_and_the_handler()
    {
        var fx = new GateFixture(masker: new FakeMasker());
        var (session, vault) = MaskedSession();
        vault.Add("'A'");
        vault.Add("'KIM'");

        var response = await Call(fx, session, AgentToolNames.ExplainPlan, """{"sql":"SELECT * FROM TB_ORDER o WHERE o.stat_cd = :v1 AND o.cust_nm = :v2"}""");

        Assert.False(response.Result.IsError, response.Result.Text);
        var call = Assert.Single(fx.Handlers[AgentToolNames.ExplainPlan].Calls);
        Assert.Equal("SELECT * FROM TB_ORDER o WHERE o.stat_cd = 'A' AND o.cust_nm = 'KIM'", (string?)call.Arguments["sql"]);
        Assert.Contains("'A'", fx.Extractor.AnalyzedTexts[0], StringComparison.Ordinal); // 범위 검사는 원문으로
    }

    [Fact]
    public async Task Benchmark_variant_placeholders_are_restored_too()
    {
        var fx = new GateFixture(masker: new FakeMasker());
        var (session, vault) = MaskedSession();
        vault.Add("'A'");

        var response = await Call(fx, session, AgentToolNames.BenchmarkQueries, """{"variants":[{"label":"a","sql":"SELECT * FROM TB_ORDER WHERE x = :v1"}]}""");

        Assert.False(response.Result.IsError, response.Result.Text);
        var call = Assert.Single(fx.Handlers[AgentToolNames.BenchmarkQueries].Calls);
        Assert.Equal("SELECT * FROM TB_ORDER WHERE x = 'A'", (string?)call.Arguments["variants"]![0]!["sql"]);
    }

    [Fact]
    public async Task A_placeholder_that_is_not_in_the_vault_is_refused_as_literal_unknown()
    {
        var fx = new GateFixture(masker: new FakeMasker());
        var (session, vault) = MaskedSession();
        vault.Add("'A'");

        var response = await Call(fx, session, AgentToolNames.ExplainPlan, """{"sql":"SELECT * FROM TB_ORDER WHERE x = :v9"}""");

        AssertDenied(response, "literal_unknown");
        Assert.Empty(fx.Handlers[AgentToolNames.ExplainPlan].Calls);
        Assert.Empty(fx.Extractor.AnalyzedTexts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sql_tools_are_closed_when_masking_is_required_but_the_vault_or_masker_is_missing(bool vaultPresent)
    {
        // 운영은 항상 필요. 사전이 없거나(운영) 마스커가 없으면(사전 있음) 닫는다.
        var fx = new GateFixture(noMasker: vaultPresent);
        var prod = Prod(AiAccessLevel.Plan);
        var session = vaultPresent
            ? MaskedSession().Session
            : new AgentSession(new AgentRunScope("run-p", prod, new AiContext(prod.Name, prod.Environment, AiAccessLevel.Plan)));

        var response = await Call(fx, session, AgentToolNames.GetCursorPlan, """{"sql_id":"abcdefghijklm"}""");

        AssertDenied(response, "literal_mask_unavailable");
        Assert.Empty(fx.Handlers[AgentToolNames.GetCursorPlan].Calls);
    }

    [Fact]
    public async Task Non_sql_tools_and_unmasked_dev_runs_are_unaffected()
    {
        var fx = new GateFixture();

        var describe = await Call(fx, Session(Prod(AiAccessLevel.Plan), context: AiAccessLevel.Plan), AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""");
        var dev = await Call(fx, Session(Dev(AiAccessLevel.Plan)), AgentToolNames.ExplainPlan, """{"sql":"SELECT * FROM TB_ORDER WHERE x = 'A'"}""");

        Assert.False(describe.Result.IsError);
        Assert.False(dev.Result.IsError);
        Assert.Equal("SELECT * FROM TB_ORDER WHERE x = 'A'", (string?)Assert.Single(fx.Handlers[AgentToolNames.ExplainPlan].Calls).Arguments["sql"]);
    }

    [Fact]
    public async Task Result_sql_text_fields_are_masked_and_other_strings_are_left_alone()
    {
        var fx = new GateFixture(masker: new FakeMasker());
        var (session, vault) = MaskedSession();
        fx.Handlers[AgentToolNames.GetCursorPlan].Respond = _ => AgentToolOutput.Ok(JsonNode.Parse(
            """{"steps":[{"id":1,"access":"\"STAT_CD\"='A'","filter":"\"NM\"='KIM'","operation":"TABLE ACCESS 'FULL'"}],"access_predicates":["X='A'"],"note":"saw 'KIM'","sql_text":"select 'Z' from dual"}""")!);

        session.AddSqlId("abcdefghijklm");

        var response = await Call(fx, session, AgentToolNames.GetCursorPlan, """{"sql_id":"abcdefghijklm"}""");

        Assert.False(response.Result.IsError, response.Result.Text);
        var body = JsonNode.Parse(response.Result.Text)!;
        Assert.Equal("\"STAT_CD\"=:v1", (string?)body["steps"]![0]!["access"]);
        Assert.Equal("\"NM\"=:v2", (string?)body["steps"]![0]!["filter"]);
        Assert.Equal("X=:v1", (string?)body["access_predicates"]![0]);
        Assert.Equal("select :v3 from dual", (string?)body["sql_text"]);
        Assert.Equal("TABLE ACCESS 'FULL'", (string?)body["steps"]![0]!["operation"]);
        Assert.Equal("saw 'KIM'", (string?)body["note"]);
        Assert.True(vault.TryGetRaw(":v3", out var raw));
        Assert.Equal("'Z'", raw);
    }

    [Fact]
    public async Task A_result_that_cannot_be_masked_is_withheld_when_masking_is_on()
    {
        var fx = new GateFixture(masker: new FakeMasker());
        var (session, _) = MaskedSession();
        session.AddSqlId("abcdefghijklm");
        fx.Handlers[AgentToolNames.GetCursorPlan].Respond = _ => AgentToolOutput.Ok(JsonValue.Create("select 'secret' from dual")!);

        var response = await Call(fx, session, AgentToolNames.GetCursorPlan, """{"sql_id":"abcdefghijklm"}""");

        AssertDenied(response, "unsafe_result");
        Assert.DoesNotContain("secret", response.Result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Error_text_that_contains_a_vault_value_is_masked_again()
    {
        var fx = new GateFixture(masker: new FakeMasker());
        var (session, vault) = MaskedSession();
        vault.Add("'S3CR3T-NAME'");
        fx.Handlers[AgentToolNames.ExplainPlan].Respond = _ => AgentToolOutput.Error("ORA-01722: invalid number 'S3CR3T-NAME' near S3CR3T-NAME");

        var response = await Call(fx, session, AgentToolNames.ExplainPlan, """{"sql":"SELECT * FROM TB_ORDER WHERE x = :v1"}""");

        Assert.True(response.Result.IsError);
        Assert.DoesNotContain("S3CR3T-NAME", response.Result.Text, StringComparison.Ordinal);
        Assert.Contains("ORA-01722", response.Result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_summary_marks_masked_runs_and_never_holds_sql_text()
    {
        var fx = new GateFixture(masker: new FakeMasker());
        var (session, vault) = MaskedSession();
        vault.Add("'KIM'");

        await Call(fx, session, AgentToolNames.ExplainPlan, """{"sql":"SELECT * FROM TB_ORDER WHERE x = :v1"}""");

        var entry = Assert.Single(fx.Audit.Entries);
        Assert.StartsWith("literals=masked", entry.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("KIM", entry.Arguments, StringComparison.Ordinal);
        Assert.Contains("sql=<", entry.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Required_flag_without_a_vault_closes_the_sql_tools_even_on_a_dev_connection()
    {
        var fx = new GateFixture();
        var dev = Dev(AiAccessLevel.Plan);
        var ctx = new AiContext(dev.Name, dev.Environment, AiAccessLevel.Plan, StatementText: TargetSql);
        var session = new AgentSession(new AgentRunScope("run-r", dev, ctx) { MaskLiteralsRequired = true });

        AssertDenied(await Call(fx, session, AgentToolNames.ExplainPlan, """{"sql":"SELECT * FROM TB_ORDER"}"""), "literal_mask_unavailable");
        Assert.False((await Call(fx, session, AgentToolNames.DescribeObject, """{"object":"TB_ORDER"}""")).Result.IsError);
    }

    [Fact]
    public async Task Audit_marks_masking_even_when_the_call_is_refused_early()
    {
        var fx = new GateFixture();
        var (session, _) = MaskedSession();

        AssertDenied(await Call(fx, session, AgentToolNames.ExplainPlan, """{"connection":"somewhere-else","sql":"SELECT 1 FROM dual"}"""), "connection");

        Assert.StartsWith("literals=masked", Assert.Single(fx.Audit.Entries).Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Error_text_is_masked_before_it_is_sanitized_so_multi_line_values_do_not_slip_through()
    {
        var fx = new GateFixture();
        var (session, vault) = MaskedSession();
        vault.Add("'first\nsecond-value'");
        fx.Handlers[AgentToolNames.ExplainPlan].Respond = _ => AgentToolOutput.Error("ORA-01722: bad 'first\nsecond-value' here");
        var failing = await Call(fx, session, AgentToolNames.ExplainPlan, """{"sql":"SELECT * FROM TB_ORDER"}""");
        Assert.DoesNotContain("second-value", failing.Result.Text, StringComparison.Ordinal);

        fx.Handlers[AgentToolNames.ExplainPlan].Respond = _ => throw new InvalidOperationException("bad 'first\nsecond-value' near here");
        var thrown = await Call(fx, session, AgentToolNames.ExplainPlan, """{"sql":"SELECT * FROM TB_ORDER"}""");
        Assert.True(thrown.Result.IsError);
        Assert.DoesNotContain("second-value", thrown.Result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sql_text_fields_holding_arrays_with_non_string_items_are_masked_without_failing()
    {
        var fx = new GateFixture(masker: new FakeMasker());
        var (session, _) = MaskedSession();
        session.AddSqlId("abcdefghijklm");
        fx.Handlers[AgentToolNames.GetCursorPlan].Respond = _ => AgentToolOutput.Ok(JsonNode.Parse(
            """{"access_predicates":[1,["x='A'"],{"k":"y='B'"},null],"filter":[["z='C'"]]}""")!);

        var response = await Call(fx, session, AgentToolNames.GetCursorPlan, """{"sql_id":"abcdefghijklm"}""");

        Assert.False(response.Result.IsError, response.Result.Text);
        Assert.DoesNotContain("'", response.Result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_value_the_model_wrote_itself_gets_the_same_number_back_in_result_predicates()
    {
        // 알려진 수용 위험(설계 위험 목록): 같은 원문은 같은 번호라 추측값이 맞으면 술어에서 원문의 번호가 다시 보인다. 현재 동작을 고정한다.
        var fx = new GateFixture(masker: new FakeMasker());
        var (session, vault) = MaskedSession();
        vault.Add("'KIM'");
        session.AddSqlId("abcdefghijklm");
        fx.Handlers[AgentToolNames.GetCursorPlan].Respond = _ => AgentToolOutput.Ok(JsonNode.Parse("""{"filter":"\"A\"='KIM' OR \"A\"='LEE'"}""")!);

        var response = await Call(fx, session, AgentToolNames.GetCursorPlan, """{"sql_id":"abcdefghijklm"}""");

        Assert.Equal("\"A\"=:v1 OR \"A\"=:v2", (string?)JsonNode.Parse(response.Result.Text)!["filter"]);
    }

    /// <summary>작은따옴표 문자열만 가리는 시험용 마스커(Language 의 실제 구현은 Language.Tests 가 시험한다).</summary>
    private sealed partial class FakeMasker : ISqlLiteralMasker
    {
        public LiteralVault CreateVault(string statementText) => new();

        public SqlMaskResult Mask(string sql, LiteralVault vault) =>
            SqlMaskResult.Ok(Quoted().Replace(sql, m => vault.Add(m.Value)));

        public SqlRestoreResult Restore(string sql, LiteralVault vault)
        {
            foreach (Match m in Placeholder().Matches(sql))
            {
                if (vault.IsOwnPlaceholder(m.Value) && !vault.TryGetRaw(m.Value, out _))
                {
                    return SqlRestoreResult.Unknown(m.Value);
                }
            }

            return SqlRestoreResult.Ok(Placeholder().Replace(sql, m => vault.TryGetRaw(m.Value, out var raw) ? raw : m.Value));
        }

        public string MaskPredicate(string predicate, LiteralVault vault) =>
            Quoted().Replace(predicate, m => m.Value == "'FULL'" ? m.Value : vault.Add(m.Value));

        [GeneratedRegex("'[^']*'")]
        private static partial Regex Quoted();

        [GeneratedRegex(@":v\d+", RegexOptions.IgnoreCase)]
        private static partial Regex Placeholder();
    }
}
