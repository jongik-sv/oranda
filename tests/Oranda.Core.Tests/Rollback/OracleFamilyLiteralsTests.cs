using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Literals;

namespace Oranda.Core.Tests.Rollback;

/// <summary>Oracle 계열 값 읽기 식과 리터럴(되돌리기 지점 설계 6.6, 16.1 「리터럴(글 수준)」).</summary>
public class OracleFamilyLiteralsTests
{
    private static RollbackColumn Col(RollbackValueKind kind, string type = "X") => new("C", type, kind);

    private static string? Lit(RollbackValueKind kind, object? value) => OracleFamilyLiterals.Literal(Col(kind), value, out _);

    [Theory]
    [InlineData(RollbackValueKind.Text, "it's", "'it''s'")]
    [InlineData(RollbackValueKind.Number, "12345678901234567890123456789012345678", "12345678901234567890123456789012345678")]
    [InlineData(RollbackValueKind.Number, "-.5", "-.5")]
    [InlineData(RollbackValueKind.Number, "1.0E+130", "1.0E+130")]
    [InlineData(RollbackValueKind.Date, " 2026-11-12 14:05:10", "TO_DATE(' 2026-11-12 14:05:10', 'SYYYY-MM-DD HH24:MI:SS')")]
    [InlineData(RollbackValueKind.Timestamp, " 2026-11-12 14:05:10.123456789", "TO_TIMESTAMP(' 2026-11-12 14:05:10.123456789', 'SYYYY-MM-DD HH24:MI:SS.FF9')")]
    [InlineData(RollbackValueKind.TimestampTz, " 2026-11-12 14:05:10.000000000 ASIA/SEOUL",
                "TO_TIMESTAMP_TZ(' 2026-11-12 14:05:10.000000000 ASIA/SEOUL', 'SYYYY-MM-DD HH24:MI:SS.FF9 TZR')")]
    [InlineData(RollbackValueKind.TimestampLtz, " 2026-11-12 05:05:10.000000000",
                "CAST(FROM_TZ(TO_TIMESTAMP(' 2026-11-12 05:05:10.000000000', 'SYYYY-MM-DD HH24:MI:SS.FF9'), 'UTC') AS TIMESTAMP WITH LOCAL TIME ZONE)")]
    [InlineData(RollbackValueKind.IntervalDaySecond, "+01 02:03:04.500000", "TO_DSINTERVAL('+01 02:03:04.500000')")]
    [InlineData(RollbackValueKind.IntervalYearMonth, "+01-02", "TO_YMINTERVAL('+01-02')")]
    [InlineData(RollbackValueKind.Raw, "0AFF", "HEXTORAW('0AFF')")]
    [InlineData(RollbackValueKind.RowId, "AAAR3sAAEAAAACXAAA", "CHARTOROWID('AAAR3sAAEAAAACXAAA')")]
    public void Literal_wraps_fixed_format_text(RollbackValueKind kind, string value, string expected)
    {
        Assert.Equal(expected, Lit(kind, value));
    }

    [Fact]
    public void National_text_uses_unistr_escapes()
    {
        Assert.Equal(@"UNISTR('\C720\C790 \005C n''')", Lit(RollbackValueKind.NationalText, "유자 \\ n'"));
        Assert.Equal(@"UNISTR('\D83D\DE00')", Lit(RollbackValueKind.NationalText, "😀"));
    }

    [Theory]
    [InlineData(double.NaN, "BINARY_DOUBLE_NAN")]
    [InlineData(double.PositiveInfinity, "BINARY_DOUBLE_INFINITY")]
    [InlineData(double.NegativeInfinity, "-BINARY_DOUBLE_INFINITY")]
    [InlineData(0.1, "TO_BINARY_DOUBLE('0.1')")]
    public void Binary_double_literals(double value, string expected)
    {
        Assert.Equal(expected, Lit(RollbackValueKind.BinaryDouble, value));
    }

    [Fact]
    public void Binary_float_uses_float_functions()
    {
        Assert.Equal("TO_BINARY_FLOAT('0.1')", Lit(RollbackValueKind.BinaryFloat, 0.1f));
        Assert.Equal("BINARY_FLOAT_NAN", Lit(RollbackValueKind.BinaryFloat, float.NaN));
        Assert.Equal("-BINARY_FLOAT_INFINITY", Lit(RollbackValueKind.BinaryFloat, float.NegativeInfinity));
    }

    [Fact]
    public void Unwritable_values_return_null_with_reason()
    {
        Assert.Null(OracleFamilyLiterals.Literal(Col(RollbackValueKind.Text), new string('가', 1400), out var tooLong));
        Assert.Contains("4000", tooLong, StringComparison.Ordinal);
        Assert.Null(OracleFamilyLiterals.Literal(Col(RollbackValueKind.Large), "x", out var large));
        Assert.NotNull(large);
        Assert.Null(OracleFamilyLiterals.Literal(Col(RollbackValueKind.Number), "1,5", out var notNumber));
        Assert.NotNull(notNumber);
        Assert.Null(OracleFamilyLiterals.Literal(Col(RollbackValueKind.Number), 1.5d, out _));
        Assert.Null(OracleFamilyLiterals.Literal(Col(RollbackValueKind.Timestamp), new DateTime(2026, 11, 12), out var notText));
        Assert.NotNull(notText);
        Assert.Null(OracleFamilyLiterals.Literal(Col(RollbackValueKind.Raw), "0G", out _));
        Assert.Null(OracleFamilyLiterals.Literal(Col(RollbackValueKind.BinaryDouble), "abc", out _));
        Assert.Equal("NULL", Lit(RollbackValueKind.Large, DBNull.Value));
        Assert.Equal("NULL", Lit(RollbackValueKind.Text, null));
    }

    [Theory]
    [InlineData("VARCHAR2(20)", RollbackValueKind.Text)]
    [InlineData("NCHAR(2)", RollbackValueKind.NationalText)]
    [InlineData("NUMBER(10,2)", RollbackValueKind.Number)]
    [InlineData("FLOAT(126)", RollbackValueKind.Number)]
    [InlineData("DOUBLE PRECISION", RollbackValueKind.Number)]
    [InlineData("BINARY_FLOAT", RollbackValueKind.BinaryFloat)]
    [InlineData("TIMESTAMP(9)", RollbackValueKind.Timestamp)]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", RollbackValueKind.TimestampTz)]
    [InlineData("TIMESTAMP(6) WITH LOCAL TIME ZONE", RollbackValueKind.TimestampLtz)]
    [InlineData("INTERVAL DAY(2) TO SECOND(6)", RollbackValueKind.IntervalDaySecond)]
    [InlineData("INTERVAL YEAR(2) TO MONTH", RollbackValueKind.IntervalYearMonth)]
    [InlineData("RAW(16)", RollbackValueKind.Raw)]
    [InlineData("CLOB", RollbackValueKind.Large)]
    [InlineData("UROWID(4000)", RollbackValueKind.Large)]
    [InlineData("SDO_GEOMETRY", RollbackValueKind.Large)]
    public void Kind_of_dictionary_type(string type, RollbackValueKind expected)
    {
        Assert.Equal(expected, OracleFamilyLiterals.KindOf(new ColumnInfo("C", 1, type)));
    }

    [Fact]
    public void Read_expressions_follow_table()
    {
        Assert.Equal("TO_CHAR(t.\"P\", 'TM9', 'NLS_NUMERIC_CHARACTERS=''.,''')", OracleFamilyLiterals.ReadExpression(Col(RollbackValueKind.Number), "t.\"P\""));
        Assert.Equal("TO_CHAR(SYS_EXTRACT_UTC(CAST(c AS TIMESTAMP WITH TIME ZONE)), 'SYYYY-MM-DD HH24:MI:SS.FF9')",
                     OracleFamilyLiterals.ReadExpression(Col(RollbackValueKind.TimestampLtz), "c"));
        Assert.Equal("CASE WHEN c IS NULL THEN 1 ELSE 0 END", OracleFamilyLiterals.ReadExpression(Col(RollbackValueKind.Large), "c"));
        Assert.Equal("c", OracleFamilyLiterals.ReadExpression(Col(RollbackValueKind.BinaryDouble), "c"));
        Assert.Equal("ROWIDTOCHAR(t.ROWID)", OracleFamilyLiterals.RowIdReadExpression("t"));
    }

    [Fact]
    public void Predicate_uses_is_null()
    {
        Assert.Equal("c IS NULL", OracleFamilyLiterals.Predicate("c", "NULL"));
        Assert.Equal("c = 'N'", OracleFamilyLiterals.Predicate("c", "'N'"));
    }
}
