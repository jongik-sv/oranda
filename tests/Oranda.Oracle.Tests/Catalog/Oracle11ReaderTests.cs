using Oranda.Core.Metadata;
using Oranda.Oracle.Metadata;

namespace Oranda.Oracle.Tests.Catalog;

/// <summary>
/// 소유: TSK-03-07. 11g 차이를 읽는 쪽에서 흡수하는 곳: 제약의 NOT NULL 거르기(LONG 조건), 옛 PLAN_TABLE 열 이름 읽기(후자는 Core.Tests PlanRowsTests 로 옮겼다).
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
}
