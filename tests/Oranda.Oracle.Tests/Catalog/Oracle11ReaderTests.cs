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
    [InlineData("ID IS NOT NULL", false)] // 서버가 만든 NOT NULL 은 늘 따옴표 — 따옴표 없는 것은 사용자 CHECK
    [InlineData("ID > 0", false)]
    [InlineData("ID IS NOT NULL AND ID > 0", false)]
    [InlineData("ID > 0 OR CODE IS NOT NULL", false)] // 끝만 같은 사용자 CHECK 는 남긴다(형식 변환이 되살림)
    [InlineData("\"A B\" IS NOT NULL", true)]
    [InlineData(" \"A B\" IS NOT NULL ", false)] // 서버 꼴은 앞뒤 공백이 없다 — 사용자가 CHECK ( "A B" IS NOT NULL ) 로 쓴 것(조건을 쓴 그대로 둠, Tibero 실측 P07)
    [InlineData("\"A\"  IS NOT NULL", false)]
    [InlineData("id is not null", false)] // SQL REGEXP_LIKE 처럼 대소문자를 가린다
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Not_null_check_matches_the_sql_filter(string? condition, bool expected)
        => Assert.Equal(expected, MetadataRows.IsNotNullCheck(condition));

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

        var result = MetadataRows.BuildConstraints(rows);

        Assert.Equal(["CK_POS", "PK_T", "CK_한글"], result.Select(c => c.Name));
        Assert.Equal("QTY > 0", result[0].Condition);
        Assert.Equal(ConstraintKind.PrimaryKey, result[1].Kind);
        Assert.Equal(["ID"], result[1].Columns);
    }

    [Fact]
    public void Empty_constraint_rows_give_empty_list()
        => Assert.Empty(MetadataRows.BuildConstraints([]));
}
