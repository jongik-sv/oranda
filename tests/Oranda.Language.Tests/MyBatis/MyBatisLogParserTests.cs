using Oranda.Core.Execution;
using Oranda.Language.MyBatis;

namespace Oranda.Language.Tests.MyBatis;

public class MyBatisLogParserTests
{
    private const string Prefix = "2026-10-20 10:00:00 DEBUG [main] c.x.EmpMapper.select - ";

    [Fact]
    public void One_pair_becomes_named_binds()
    {
        var log = Prefix + "==>  Preparing: SELECT * FROM emp WHERE id = ? AND name = ?\n" + Prefix + "==> Parameters: 7(Integer), abc(String)\n";
        var s = Assert.Single(MyBatisLogParser.Parse(log));
        Assert.Equal("SELECT * FROM emp WHERE id = :p1 AND name = :p2", s.Sql);
        Assert.Equal(2, s.Params.Count);
        Assert.Equal(new MyBatisLogParam("p1", BindDataType.Number, "7", false), s.Params[0]);
        Assert.Equal(new MyBatisLogParam("p2", BindDataType.Varchar2, "abc", false), s.Params[1]);
    }

    [Fact]
    public void Several_pairs_are_read_in_order()
    {
        var log = Prefix + "==>  Preparing: SELECT 1 FROM a WHERE x = ?\n" + Prefix + "==> Parameters: 1(Long)\n"
            + Prefix + "<==      Total: 1\n"
            + Prefix + "==>  Preparing: UPDATE b SET y = ? WHERE z = ?\n" + Prefix + "==> Parameters: q(String), 2(Integer)\n";
        var all = MyBatisLogParser.Parse(log);
        Assert.Equal(2, all.Count);
        Assert.Equal("SELECT 1 FROM a WHERE x = :p1", all[0].Sql);
        Assert.Equal("UPDATE b SET y = :p2 WHERE z = :p3", all[1].Sql);
        Assert.Equal("2", all[1].Params[1].Value);
        Assert.Equal("p3", all[1].Params[1].Name);
    }

    [Fact]
    public void Null_value_has_no_type()
    {
        var s = Assert.Single(MyBatisLogParser.Parse("==>  Preparing: INSERT INTO t VALUES (?, ?)\n==> Parameters: null, x(String)"));
        Assert.True(s.Params[0].IsNull);
        Assert.Equal(string.Empty, s.Params[0].Value);
        Assert.Equal("x", s.Params[1].Value);
    }

    [Fact]
    public void String_with_commas_and_parens_survives()
    {
        var s = Assert.Single(MyBatisLogParser.Parse("==>  Preparing: SELECT ? , ? FROM dual\n==> Parameters: a, b (c)(String), 3(Integer)"));
        Assert.Equal("a, b (c)", s.Params[0].Value);
        Assert.Equal(BindDataType.Varchar2, s.Params[0].Type);
        Assert.Equal("3", s.Params[1].Value);
    }

    [Theory]
    [InlineData("Integer", BindDataType.Number)]
    [InlineData("java.lang.Long", BindDataType.Number)]
    [InlineData("Short", BindDataType.Number)]
    [InlineData("BigDecimal", BindDataType.Number)]
    [InlineData("Double", BindDataType.Number)]
    [InlineData("String", BindDataType.Varchar2)]
    [InlineData("Timestamp", BindDataType.Timestamp)]
    [InlineData("LocalDateTime", BindDataType.Timestamp)]
    [InlineData("Date", BindDataType.Date)]
    [InlineData("Whatever", BindDataType.Varchar2)]
    public void Java_types_map_to_bind_types(string java, BindDataType expected)
        => Assert.Equal(expected, MyBatisLogParser.MapType(java));

    [Fact]
    public void Question_mark_in_literal_or_comment_is_left_alone()
    {
        var sql = MyBatisLogParser.ReplacePlaceholders("SELECT '?' /* ? */ , x -- ?\n FROM t WHERE a = ? AND b = ?", out int n);
        Assert.Equal(2, n);
        Assert.Equal("SELECT '?' /* ? */ , x -- ?\n FROM t WHERE a = :p1 AND b = :p2", sql);
    }

    [Fact]
    public void Text_without_markers_gives_nothing() => Assert.Empty(MyBatisLogParser.Parse("select 1 from dual"));

    [Fact]
    public void Empty_parameters_line_gives_statement_without_values()
    {
        var s = Assert.Single(MyBatisLogParser.Parse("==>  Preparing: SELECT 1 FROM dual\n==> Parameters: "));
        Assert.Empty(s.Params);
    }
}
