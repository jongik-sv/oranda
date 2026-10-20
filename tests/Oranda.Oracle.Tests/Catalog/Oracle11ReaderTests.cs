using Oranda.Core.Metadata;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Plans;

namespace Oranda.Oracle.Tests.Catalog;

/// <summary>
/// 소유: TSK-03-07. 11g 차이를 읽는 쪽에서 흡수하는 곳: 제약의 NOT NULL 거르기(LONG 조건), 옛 PLAN_TABLE 열 이름 읽기.
/// </summary>
public class Oracle11ReaderTests
{
    private static object?[] Cons(string name, string type, string? condition, string? column = null) =>
        [name, type, "ENABLED", condition, null, null, column, column is null ? null : 1m, null];

    [Theory]
    [InlineData("\"ID\" IS NOT NULL", true)]
    [InlineData("ID IS NOT NULL", true)]
    [InlineData("ID > 0", false)]
    [InlineData("ID IS NOT NULL AND ID > 0", false)]
    [InlineData("id is not null", false)] // SQL LIKE '%IS NOT NULL' 처럼 대소문자를 가린다
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Not_null_check_matches_the_sql_filter(string? condition, bool expected)
        => Assert.Equal(expected, OracleMetadataProvider.IsNotNullCheck(condition));

    [Fact]
    public void Not_null_check_constraints_are_dropped_and_others_kept()
    {
        List<object?[]> rows =
        [
            Cons("SYS_C001", "C", "\"ID\" IS NOT NULL"),
            Cons("CK_POS", "C", "QTY > 0"),
            Cons("PK_T", "P", null, "ID"),
            Cons("CK_한글", "C", "\"이름\" <> ' '"),
        ];

        var result = OracleMetadataProvider.BuildConstraints(rows);

        Assert.Equal(["CK_POS", "PK_T", "CK_한글"], result.Select(c => c.Name));
        Assert.Equal("QTY > 0", result[0].Condition);
        Assert.Equal(ConstraintKind.PrimaryKey, result[1].Kind);
        Assert.Equal(["ID"], result[1].Columns);
    }

    [Fact]
    public void Empty_constraint_rows_give_empty_list()
        => Assert.Empty(OracleMetadataProvider.BuildConstraints([]));

    [Fact]
    public void Plan_table_rows_are_read_by_column_name_in_any_order()
    {
        string[] names = ["STATEMENT_ID", "PLAN_ID", "FILTER_PREDICATES", "ID", "PARENT_ID", "OPERATION", "OPTIONS",
            "OBJECT_OWNER", "OBJECT_NAME", "COST", "CARDINALITY", "ACCESS_PREDICATES"];
        object?[] row = ["S1", 7m, "X > 1", 1m, 0m, "TABLE ACCESS", "FULL", "HR", "EMP", 3m, 14m, null];

        var step = Assert.Single(OracleExecutionPlanService.ReadPlanTableRows(names, [row]));

        Assert.Equal(1, step.Id);
        Assert.Equal(0, step.ParentId);
        Assert.Equal("TABLE ACCESS", step.Operation);
        Assert.Equal("FULL", step.Options);
        Assert.Equal("HR", step.Owner);
        Assert.Equal("EMP", step.Name);
        Assert.Equal(3, step.Cost);
        Assert.Equal(14, step.EstimatedRows);
        Assert.Null(step.Access);
        Assert.Equal("X > 1", step.Filter);
    }

    [Fact]
    public void Old_plan_table_without_plan_id_and_predicates_still_reads()
    {
        // 옛 개인 PLAN_TABLE: plan_id·access/filter_predicates·depth 가 없다. 열 이름은 소문자로 와도 된다.
        string[] names = ["statement_id", "operation", "options", "object_owner", "object_name", "id", "parent_id", "cost", "cardinality"];
        List<object?[]> rows =
        [
            ["S1", "SELECT STATEMENT", null, null, null, 0m, null, 2m, 1m],
            ["S1", "TABLE ACCESS", "FULL", "SYS", "DUAL", 1m, 0m, 2m, 1m],
        ];

        var steps = OracleExecutionPlanService.BuildSteps(OracleExecutionPlanService.ReadPlanTableRows(names, rows));

        Assert.Equal(2, steps.Count);
        Assert.Equal("SELECT STATEMENT", steps[0].Operation);
        Assert.Equal(1, steps[1].Depth);
        Assert.Null(steps[1].AccessPredicates);
        Assert.Null(steps[1].FilterPredicates);
    }

    [Fact]
    public void Plan_table_without_operation_gives_empty_operation_not_an_error()
    {
        var step = Assert.Single(OracleExecutionPlanService.ReadPlanTableRows(["ID"], [[0m]]));

        Assert.Equal(0, step.Id);
        Assert.Equal(string.Empty, step.Operation);
        Assert.Null(step.ParentId);
    }
}
