using Oranda.Core.Plans;

namespace Oranda.Core.Tests.Plans;

// 소유: TSK-07-01(PLAN-04). DBMS_XPLAN 글에서 plan_hash_value 읽기.
public class ExecutionPlanTextTests
{
    [Fact]
    public void Plan_hash_value_line_is_read_from_display_output()
    {
        const string text = """
            Plan hash value: 3956160932

            ----------------------------------------------------------------------------
            | Id  | Operation         | Name | Rows  | Bytes | Cost (%CPU)| Time     |
            """;

        Assert.Equal(3956160932L, ExecutionPlanText.ParsePlanHashValue(text));
    }

    [Fact]
    public void Display_cursor_header_with_sql_id_lines_is_read_too()
    {
        var text = "SQL_ID  4c8yp4y5m3s1u, child number 0\n-------------------------------------\nselect * from t\n\nPlan hash value: 1601196873\n";

        Assert.Equal(1601196873L, ExecutionPlanText.ParsePlanHashValue(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no plan here")]
    [InlineData("Plan hash value: ")]
    [InlineData("Plan hash value: abc")]
    public void Missing_or_malformed_hash_is_null(string? text) => Assert.Null(ExecutionPlanText.ParsePlanHashValue(text));
}
