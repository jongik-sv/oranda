using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Tibero.Rollback;

namespace Oranda.Tibero.Tests.Rollback;

/// <summary>Tibero 되돌리기 방언(서버 없음). 서버 왕복은 <c>RollbackDialectMeasureIt.Tibero_dialect_reads_every_kind…</c>.</summary>
public sealed class TiberoRollbackDialectTests
{
    private static readonly TiberoRollbackDialect Dialect = TiberoRollbackDialect.Instance;

    [Theory]
    [InlineData("VARCHAR", RollbackValueKind.Text)] // Tibero meta.columns 이름
    [InlineData("CHAR", RollbackValueKind.Text)]
    [InlineData("NVARCHAR", RollbackValueKind.NationalText)]
    [InlineData("NCHAR", RollbackValueKind.NationalText)]
    [InlineData("NUMBER", RollbackValueKind.Number)]
    [InlineData("BINARY_FLOAT", RollbackValueKind.BinaryFloat)]
    [InlineData("TIMESTAMP(9) WITH LOCAL TIME ZONE", RollbackValueKind.TimestampLtz)]
    [InlineData("TIMESTAMP(9) WITH TIME ZONE", RollbackValueKind.TimestampTz)]
    [InlineData("TIMESTAMP(6)", RollbackValueKind.Timestamp)]
    [InlineData("INTERVAL DAY(9) TO SECOND(9)", RollbackValueKind.IntervalDaySecond)]
    [InlineData("RAW", RollbackValueKind.Raw)]
    [InlineData("CLOB", RollbackValueKind.Large)]
    [InlineData("LONG RAW", RollbackValueKind.Large)]
    public void Kind_follows_the_tibero_type_names(string type, RollbackValueKind expected) =>
        Assert.Equal(expected, Dialect.KindOf(new ColumnInfo("C", 1, type)));

    [Fact]
    public void Contract_values_are_the_measured_tibero_values()
    {
        Assert.Equal(Dbms.Tibero, Dialect.Dbms);
        Assert.Equal("TSN", Dialect.PointName);
        Assert.Equal("AS OF TSN 7", Dialect.AsOf(7));
        Assert.True(Dialect.SupportsReturningRowId);
        Assert.Equal("ORANDA$RB", Dialect.SavepointName);
        Assert.Equal(128, Dialect.MaxIdentifierBytes(null!));
        Assert.Equal("CHARTOROWID('AAAA''B')", Dialect.RowIdLiteral("AAAA'B"));
    }

    [Fact]
    public void Binary_float_and_double_are_read_as_server_text_and_written_with_their_own_function_or_constant()
    {
        var bf = new RollbackColumn("BF", "BINARY_FLOAT", RollbackValueKind.BinaryFloat);
        var bd = new RollbackColumn("BD", "BINARY_DOUBLE", RollbackValueKind.BinaryDouble);

        Assert.Equal("TO_CHAR(t.\"BF\")", Dialect.ReadExpression(bf, "t.\"BF\""));
        Assert.Equal("TO_BINARY_FLOAT('0.1')", Dialect.Literal(bf, "1.00000001E-001", out _)); // 서버 글자를 float 로 읽어 R 형식
        Assert.Equal("TO_BINARY_FLOAT('0.1')", Dialect.Literal(bf, 0.1f, out _)); // .NET float 도 FLOAT 함수(DOUBLE 로 비교하면 0행)
        Assert.Equal("BINARY_FLOAT_NAN", Dialect.Literal(bf, "Nan", out _));
        Assert.Equal("-BINARY_DOUBLE_INFINITY", Dialect.Literal(bd, "-Inf", out _));
        Assert.Equal("BINARY_DOUBLE_INFINITY", Dialect.Literal(bd, "Inf", out _));
        Assert.Equal("TO_BINARY_DOUBLE('0.03333333333333333')", Dialect.Literal(bd, "3.3333333333333333E-002", out _));
    }

    [Fact]
    public void Text_literals_quote_escape_national_text_and_refuse_large_values()
    {
        var v = new RollbackColumn("V", "VARCHAR", RollbackValueKind.Text);
        var nv = new RollbackColumn("NV", "NVARCHAR", RollbackValueKind.NationalText);
        var clob = new RollbackColumn("CL", "CLOB", RollbackValueKind.Large);

        Assert.Equal("'it''s'", Dialect.Literal(v, "it's", out _));
        Assert.Equal("NULL", Dialect.Literal(v, null, out _));
        Assert.Null(Dialect.Literal(v, new string('가', 1400), out var tooLong)); // 4200바이트
        Assert.Contains("4000바이트", tooLong, StringComparison.Ordinal);
        Assert.Equal("UNISTR('a\\005C\\AC00\\D83D\\DE00')", Dialect.Literal(nv, "a\\가😀", out _));
        Assert.Null(Dialect.Literal(clob, 1m, out var large));
        Assert.Contains("큰 열", large, StringComparison.Ordinal);
        Assert.Equal("CASE WHEN t.\"CL\" IS NULL THEN 1 ELSE 0 END", Dialect.ReadExpression(clob, "t.\"CL\""));
        Assert.Equal("TO_CHAR(SYS_EXTRACT_UTC(CAST(c AS TIMESTAMP WITH TIME ZONE)), 'SYYYY-MM-DD HH24:MI:SS.FF9')",
            Dialect.ReadExpression(new RollbackColumn("L", "TIMESTAMP(9) WITH LOCAL TIME ZONE", RollbackValueKind.TimestampLtz), "c"));
    }
}
