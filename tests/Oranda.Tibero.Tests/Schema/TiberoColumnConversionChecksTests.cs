using Oranda.Core.Metadata;
using Oranda.Tibero.Schema;

namespace Oranda.Tibero.Tests.Schema;

/// <summary>열 형식 변환 검사(Tibero, T17b)의 서버 없는 부분: 블록 틀 채우기·값 질의·「그 행 보기」.</summary>
public sealed class TiberoColumnConversionChecksTests
{
    private static readonly DbObjectRef Table = new("SAMPLE", "ORDERS", "TABLE");

    [Fact]
    public void Blocks_fill_table_type_and_cancel_codes_and_keep_the_user_expression_out()
    {
        var scan = TiberoColumnConversionChecks.Fill(TiberoColumnConversionChecks.ScanBlock, Table, "NUMBER(1)");
        var rows = TiberoColumnConversionChecks.Fill(TiberoColumnConversionChecks.RowsBlock, Table, "VARCHAR2(5 CHAR)");

        Assert.Contains("TYPE value_list IS TABLE OF NUMBER(1);", scan, StringComparison.Ordinal);
        Assert.Contains("IF SQLCODE IN (-12040, -1013) THEN", scan, StringComparison.Ordinal); // 취소는 삼키지 않는다
        Assert.Contains("q VARCHAR2(32767) := :query;", scan, StringComparison.Ordinal); // 사용자 식은 바인드로만
        Assert.Contains("FROM \"SAMPLE\".\"ORDERS\")", rows, StringComparison.Ordinal);
        Assert.Contains("v VARCHAR2(5 CHAR);", rows, StringComparison.Ordinal);
        Assert.DoesNotContain("@", scan + rows, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NUMBER(12,2)", true)]
    [InlineData("VARCHAR2(20 CHAR)", true)]
    [InlineData("TIMESTAMP(6) WITH LOCAL TIME ZONE", true)]
    [InlineData("NUMBER(*,0)", true)]
    [InlineData("NUMBER; EXECUTE IMMEDIATE 'DROP TABLE X'", false)]
    [InlineData("NUMBER -- x", false)]
    [InlineData("VARCHAR2(10)\n", false)]
    public void Type_text_is_limited_to_type_spelling(string type, bool ok)
    {
        if (ok)
        {
            Assert.Equal(type, TiberoColumnConversionChecks.CheckedType(type));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => TiberoColumnConversionChecks.CheckedType(type));
        }
    }

    [Theory]
    [InlineData("ODP-12040:Statement canceled.", true)]
    [InlineData("ORA-01013: user requested cancel of current operation", true)]
    [InlineData("TBR-15115: Invalid value.", false)]
    [InlineData(null, false)]
    public void Swallowed_cancel_errors_are_recognised(string? error, bool expected) =>
        Assert.Equal(expected, TiberoColumnConversionChecks.IsCancelError(error));

    [Fact]
    public void Value_query_wraps_the_expression_on_its_own_lines()
    {
        Assert.Equal("SELECT (\nTO_NUMBER(v) -- 끝 주석\n) FROM \"SAMPLE\".\"ORDERS\" t",
            TiberoColumnConversionChecks.ValueQuery(Table, "  TO_NUMBER(v) -- 끝 주석 "));
    }

    [Fact]
    public void View_rows_sql_lists_row_ids_or_is_null_without_rows()
    {
        Assert.Null(TiberoColumnConversionChecks.RowIdSql(Table, []));
        Assert.Equal("SELECT * FROM SAMPLE.ORDERS t WHERE t.ROWID IN (CHARTOROWID('AAA'), CHARTOROWID('B''B'))",
            TiberoColumnConversionChecks.RowIdSql(Table, ["AAA", "B'B"]));
    }
}
