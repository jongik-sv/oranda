using Oranda.App.Features.Ai.Chat;
using Oranda.Core.Ai.Schemas;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>도구 카드 사람 말 표(chat-detail c1): 도구별 한 줄, 거부·실패 사유, 펼친 인자, 원래 값이 나오지 않는 것.</summary>
public class ToolCardTextTests
{
    [Theory]
    [InlineData(AgentToolNames.DescribeObject, "{\"object\":\"APS.TB_A\"}", "객체 구조 조회", "APS.TB_A")]
    [InlineData(AgentToolNames.GetIndexInfo, "{\"table\":\"APS.TB_A\"}", "인덱스 정보 조회", "APS.TB_A")]
    [InlineData(AgentToolNames.GetTableStats, "{\"table\":\"APS.TB_A\",\"columns\":[\"A\",\"B\",\"C\"]}", "테이블 통계", "APS.TB_A (열 3)")]
    [InlineData(AgentToolNames.GetTableStats, "{\"table\":\"APS.TB_A\"}", "테이블 통계", "APS.TB_A")]
    [InlineData(AgentToolNames.GetHistogram, "{\"table\":\"APS.TB_A\",\"column\":\"STAT_CD\"}", "컬럼 히스토그램", "APS.TB_A.STAT_CD")]
    [InlineData(AgentToolNames.GetCursorPlan, "{\"sql_id\":\"7h35uxf5uhmm1\",\"child\":2}", "실제 실행 계획(커서)", "7h35uxf5uhmm1 (자식 2)")]
    [InlineData(AgentToolNames.GetSqlStats, "{\"sql_id\":\"7h35uxf5uhmm1\"}", "SQL 수행 통계", "7h35uxf5uhmm1")]
    [InlineData(AgentToolNames.GetTuningContext, "{\"literals\":\"masked\"}", "튜닝 정보 묶음", "요청의 대상 문장")]
    [InlineData(AgentToolNames.BenchmarkQueries, "{\"variants\":[{\"label\":\"a\",\"sql\":\"SELECT 1\"},{\"label\":\"b\",\"sql\":\"SELECT 2\"}],\"repeat\":3}", "변형 성능 비교", "변형 2개 · 반복 3회")]
    public void Each_tool_gets_a_human_title_and_its_key_argument(string tool, string args, string title, string detail)
    {
        var line = ToolCardText.Describe(tool, args);

        Assert.Equal((title, detail), (line.Title, line.Detail));
    }

    [Fact]
    public void Explain_plan_shows_sql_length_and_distinct_placeholder_count_but_never_the_sql()
    {
        var sql = "SELECT * FROM t WHERE a = :v1 AND b = :v2 AND c = :v1";
        var line = ToolCardText.Describe(AgentToolNames.ExplainPlan, $"{{\"sql\":\"{sql}\"}}");

        Assert.Equal("실행 계획(EXPLAIN)", line.Title);
        Assert.Equal($"SQL {sql.Length}자 · 자리표시 2", line.Detail);
        Assert.DoesNotContain(":v", line.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_summary_still_gives_a_title_and_the_readable_leading_arguments()
    {
        var truncated = "{\"sql\":\"SELECT a FROM t WHERE x = :v1 AND y = :v2 AND very_long_tail_that_got_cut…";

        var line = ToolCardText.Describe(AgentToolNames.ExplainPlan, truncated);
        var table = ToolCardText.Describe(AgentToolNames.GetIndexInfo, "{\"table\":\"APS.TB_A\",\"conn…");

        Assert.StartsWith("SQL ", line.Detail, StringComparison.Ordinal);
        Assert.Contains("이상", line.Detail, StringComparison.Ordinal);
        Assert.Contains("자리표시 2", line.Detail, StringComparison.Ordinal);
        Assert.Equal("APS.TB_A", table.Detail);
    }

    [Theory]
    [InlineData("some_new_tool", "{\"x\":1}")]
    [InlineData("some_new_tool", "")]
    public void Unknown_tools_keep_their_own_name(string tool, string args)
    {
        var line = ToolCardText.Describe(tool, args);

        Assert.Equal((tool, (string?)null), (line.Title, line.Detail));
    }

    [Theory]
    [InlineData(AgentToolNames.GetIndexInfo, "")]
    [InlineData(AgentToolNames.GetIndexInfo, "not json")]
    [InlineData(AgentToolNames.ExplainPlan, "{}")]
    public void Unreadable_arguments_do_not_throw_and_leave_the_detail_empty(string tool, string args)
    {
        var line = ToolCardText.Describe(tool, args);

        Assert.Null(line.Detail);
    }

    [Theory]
    [InlineData("sql_unsafe", "안전하다고 판정할 수 없는 SQL 이라 보내지 않음")]
    [InlineData("scope", "이 요청의 범위 밖 객체라서 거부")]
    [InlineData("level", "접속의 AI 허용 수준이 모자라 거부")]
    [InlineData("sql_unsafe, audit_failed", "안전하다고 판정할 수 없는 SQL 이라 보내지 않음")]
    [InlineData("tool_failed: ORA-00942", "도구 실행 중 오류 (ORA-00942)")]
    [InlineData("brand_new_code", "사유 코드 brand_new_code")]
    public void Gate_reason_codes_become_plain_sentences(string note, string expected)
    {
        Assert.Equal(expected, ToolCardText.ReasonText(note));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("audit_failed")]
    public void No_reason_is_shown_when_there_is_none(string? note)
    {
        Assert.Null(ToolCardText.ReasonText(note));
    }

    [Fact]
    public void Every_gate_reason_code_in_the_gate_has_a_sentence()
    {
        var codes = new[]
        {
            "sql_unsafe", "sql_not_single_query", "scope", "sql_id_scope", "no_statement", "sql_schema_unresolved", "level", "environment",
            "explain_plan_disabled", "literal_mask_unavailable", "session_mismatch", "connection", "invalid_arguments", "unknown_tool",
            "run_revoked", "unsafe_result", "canceled", "internal_error", "not_implemented", "tool_failed", "tool_exception",
        };

        Assert.All(codes, code => Assert.DoesNotContain("사유 코드", ToolCardText.ReasonText(code), StringComparison.Ordinal));
    }

    [Fact]
    public void Expanded_arguments_keep_the_masked_text_as_is_one_argument_per_line()
    {
        var text = ToolCardText.Expand("{\"sql\":\"SELECT 1\\nFROM t WHERE a = :v1\",\"budget_tokens\":4000}");

        Assert.Equal("sql: SELECT 1\nFROM t WHERE a = :v1\nbudget_tokens: 4000".ReplaceLineEndings(), text.ReplaceLineEndings());
        Assert.Equal("not json", ToolCardText.Expand("not json"));
    }
}
