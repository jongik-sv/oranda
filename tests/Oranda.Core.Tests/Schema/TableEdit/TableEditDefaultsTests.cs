using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Xunit;

namespace Oranda.Core.Tests.Schema.TableEdit;

/// <summary>거짓 차이 정규화(설계 8.4) 각 항목.</summary>
public sealed class TableEditDefaultsTests
{
    private static string Type(string dataType, int? length = null, int? precision = null, int? scale = null, CharSemantics? semantics = null) =>
        TableEditDefaults.NormalizeType(new ColumnDraft("k", "C")
        {
            DataType = dataType, Length = length, Precision = precision, Scale = scale, CharSemantics = semantics,
        });

    [Theory]
    [InlineData("number", null, null, "NUMBER")]
    [InlineData("NUMBER", null, 0, "NUMBER(*,0)")]
    [InlineData("INTEGER", null, null, "NUMBER(*,0)")]
    [InlineData("int", null, null, "NUMBER(*,0)")]
    [InlineData("NUMBER", 10, 0, "NUMBER(10)")]
    [InlineData("NUMBER", 10, null, "NUMBER(10)")]
    [InlineData("number", 12, 2, "NUMBER(12,2)")]
    [InlineData("DECIMAL", 12, 2, "NUMBER(12,2)")]
    [InlineData("FLOAT", null, null, "FLOAT(126)")]
    public void Numbers(string type, int? precision, int? scale, string expected) =>
        Assert.Equal(expected, Type(type, precision: precision, scale: scale));

    [Fact]
    public void Characters_carry_length_and_semantics()
    {
        Assert.Equal("VARCHAR2(20 BYTE)", Type("varchar2", 20, semantics: CharSemantics.Bytes));
        Assert.Equal("VARCHAR2(20 CHAR)", Type("VARCHAR2", 20, semantics: CharSemantics.Characters));
        Assert.Equal("VARCHAR2(20)", Type("VARCHAR2", 20));
        Assert.Equal("VARCHAR2(20 BYTE)", Type("VARCHAR", 20, semantics: CharSemantics.Bytes));
        Assert.Equal("CHAR(1)", Type("CHAR"));
        Assert.Equal("NVARCHAR2(20)", Type("NVARCHAR", 20, semantics: CharSemantics.Characters));
        Assert.Equal("RAW(16)", Type("raw", 16));
    }

    [Fact]
    public void Types_without_length_ignore_the_length_value()
    {
        // 사전은 DATE 에도 길이(7)를 준다.
        Assert.Equal("DATE", Type("date", 7));
        Assert.Equal("CLOB", Type("CLOB", 4000));
    }

    [Fact]
    public void Timestamp_default_fraction_and_spacing()
    {
        Assert.Equal("TIMESTAMP(6)", Type("timestamp"));
        Assert.Equal("TIMESTAMP(6)", Type("TIMESTAMP(6)"));
        Assert.Equal("TIMESTAMP(3) WITH TIME ZONE", Type("timestamp(3)  with time zone"));
        Assert.Equal("TIMESTAMP(6) WITH LOCAL TIME ZONE", Type("TIMESTAMP WITH LOCAL TIME ZONE"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  \n", null)]
    [InlineData("NULL", null)]
    [InlineData("null ", null)]
    [InlineData("'N'\n", "'N'")]
    [InlineData("sysdate", "SYSDATE")]
    [InlineData("'a b'", "'a b'")]
    public void Defaults(string? text, string? expected) => Assert.Equal(expected, TableEditDefaults.NormalizeDefault(text));

    [Theory]
    [InlineData("\"AMOUNT\"*\"QTY\"", "AMOUNT*QTY")]
    [InlineData("amount * qty", "AMOUNT*QTY")]
    [InlineData("\"Mixed\" + 1", "\"Mixed\"+1")]
    [InlineData("UPPER(\"NOTE\")", "UPPER(NOTE)")]
    [InlineData("case when x = 'a' then 1 end", "CASE WHEN X='a' THEN 1 END")]
    [InlineData("'it''s' || name", "'it''s'||NAME")]
    [InlineData("(\"AMOUNT\"*\"QTY\")", "AMOUNT*QTY")]
    [InlineData("((a + 1))", "A+1")]
    [InlineData("(a) + (b)", "(A)+(B)")]
    [InlineData("  ", null)]
    public void Expressions(string text, string? expected) => Assert.Equal(expected, TableEditDefaults.NormalizeExpression(text));

    [Fact]
    public void Conditions_follow_the_expression_rule()
    {
        Assert.Equal(TableEditDefaults.NormalizeCondition("status in ('N','Y')"), TableEditDefaults.NormalizeCondition("\"STATUS\" IN ('N', 'Y')"));
        Assert.NotEqual(TableEditDefaults.NormalizeCondition("status in ('n')"), TableEditDefaults.NormalizeCondition("status in ('N')"));
    }

    [Fact]
    public void Server_names_and_reserved_words()
    {
        Assert.True(TableEditDefaults.IsServerName("SYS_C0012"));
        Assert.True(TableEditDefaults.IsServerName("BIN$abc=="));
        Assert.False(TableEditDefaults.IsServerName("PK_ORDERS"));
        Assert.True(TableEditDefaults.IsReservedWord("LEVEL"));
        Assert.False(TableEditDefaults.IsReservedWord("ORDER_ID"));
    }
}
