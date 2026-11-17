using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit.Conversion;
using Oranda.Oracle.Schema;

namespace Oranda.Oracle.Tests.Schema;

// 계획 T17(설계 18.9): 형식 변환 「변환 안 되는 행」의 Oracle 조건. 실제 질의는 Schema/ColumnConversionIntegrationTests(26ai·11g).
public sealed class OracleColumnConversionChecksTests
{
    private static readonly DbObjectRef Orders = new("SAMPLE", "ORDERS", "TABLE");

    [Fact]
    public void Validate_where_counts_unconvertible_and_overflowing_numbers()
    {
        var where = OracleColumnConversionChecks.ValidateWhere("ORDER_NO", new ConversionValidation("NUMBER", null, 12, 2), targetNullable: true);

        Assert.Equal(
            "t.\"ORDER_NO\" IS NOT NULL AND (VALIDATE_CONVERSION(t.\"ORDER_NO\" AS NUMBER) = 0"
            + " OR CASE WHEN VALIDATE_CONVERSION(t.\"ORDER_NO\" AS NUMBER) = 1 THEN CASE WHEN ABS(ROUND(TO_NUMBER(t.\"ORDER_NO\"), 2)) >= POWER(10, 10) THEN 1 END END = 1)",
            where);
    }

    [Fact]
    public void Validate_where_uses_the_date_format_and_counts_nulls_for_not_null_targets()
    {
        var where = OracleColumnConversionChecks.ValidateWhere("D", new ConversionValidation("DATE", "YYYY-MM-DD HH24:MI:SS"), targetNullable: false);

        Assert.Equal("(t.\"D\" IS NOT NULL AND (VALIDATE_CONVERSION(t.\"D\" AS DATE, 'YYYY-MM-DD HH24:MI:SS') = 0)) OR t.\"D\" IS NULL", where);
    }

    [Fact]
    public void Value_query_is_one_select_with_the_expression_on_its_own_lines()
    {
        Assert.Equal("SELECT (\nTO_NUMBER(V) -- 끝 주석\n) FROM \"SAMPLE\".\"ORDERS\" t", OracleColumnConversionChecks.ValueQuery(Orders, " TO_NUMBER(V) -- 끝 주석 "));
    }

    [Fact]
    public void Row_id_sql_lists_the_collected_rows_or_is_null()
    {
        Assert.Equal(
            "SELECT * FROM SAMPLE.ORDERS t WHERE t.ROWID IN (CHARTOROWID('AAAx'), CHARTOROWID('AAAy'))",
            OracleColumnConversionChecks.RowIdSql(Orders, ["AAAx", "AAAy"]));
        Assert.Null(OracleColumnConversionChecks.RowIdSql(Orders, []));
    }
}
