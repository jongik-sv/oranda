using Oranda.Core.Connection;

namespace Oranda.Core.Tests.Connection;

/// <summary>사용자 지정 접속 문자열 정규화: 여러 줄 tnsnames.ora 형식을 드라이버에 넘길 한 줄로.</summary>
public sealed class ConnectStringTextTests
{
    private const string OneLine = "(DESCRIPTION=(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST=db.example.com)(PORT=1521)))(CONNECT_DATA=(SERVICE_NAME=ORCLPDB)))";

    [Fact]
    public void Multi_line_descriptor_with_indent_and_spaces_becomes_one_line()
    {
        var text = "(DESCRIPTION =\r\n    (ADDRESS_LIST =\r\n      (ADDRESS = (PROTOCOL = TCP)(HOST = db.example.com)(PORT = 1521))\r\n    )\r\n"
            + "    (CONNECT_DATA =\n\t(SERVICE_NAME   =   ORCLPDB)\n    )\n  )\n";
        var r = ConnectStringText.Parse(text);
        Assert.Equal(OneLine, r.Normalized);
        Assert.Null(r.Alias);
        Assert.True(r.ParenthesesBalanced);
    }

    [Fact]
    public void Comment_lines_are_dropped()
    {
        var text = "# 운영 복제본\n(DESCRIPTION =\n  # 주소\n  (ADDRESS_LIST = (ADDRESS = (PROTOCOL = TCP)(HOST = db.example.com)(PORT = 1521)))\n"
            + "  (CONNECT_DATA = (SERVICE_NAME = ORCLPDB)))";
        Assert.Equal(OneLine, ConnectStringText.Normalize(text));
    }

    [Fact]
    public void Whole_tnsnames_entry_drops_the_alias_and_reports_it()
    {
        var text = "ORCLPDB.EXAMPLE =\n  (DESCRIPTION =\n    (ADDRESS_LIST = (ADDRESS = (PROTOCOL = TCP)(HOST = db.example.com)(PORT = 1521)))\n"
            + "    (CONNECT_DATA = (SERVICE_NAME = ORCLPDB)))";
        var r = ConnectStringText.Parse(text);
        Assert.Equal(OneLine, r.Normalized);
        Assert.Equal("ORCLPDB.EXAMPLE", r.Alias);

        Assert.Equal("SALES", ConnectStringText.Parse("SALES, SALES2 = (DESCRIPTION=(ADDRESS=(HOST=h)))").Alias);
    }

    [Theory]
    [InlineData("(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=h)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=s))")]
    [InlineData("(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=h)(PORT=1521)))(CONNECT_DATA=(SERVICE_NAME=s)))")]
    [InlineData(")(")]
    public void Unbalanced_parentheses_are_reported(string text) => Assert.False(ConnectStringText.Parse(text).ParenthesesBalanced);

    [Theory]
    [InlineData("db.example.com:1521/ORCLPDB")]
    [InlineData("tcps://db.example.com:2484/ORCLPDB?connect_timeout=5&wallet_location=C:\\w")]
    [InlineData("h1:1521,h2:1521/svc")]
    [InlineData("SALES")]
    [InlineData(OneLine)]
    public void Already_one_line_strings_stay_the_same(string text)
    {
        var r = ConnectStringText.Parse("  " + text + " \n");
        Assert.Equal(text, r.Normalized);
        Assert.Null(r.Alias);
        Assert.True(r.ParenthesesBalanced);
    }

    [Fact]
    public void Spaces_inside_quotes_are_kept()
    {
        var text = "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=h)(PORT=2484))\n  (SECURITY=(SSL_SERVER_CERT_DN=\"CN=db, O=Example  Corp\")))";
        Assert.Equal("(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=h)(PORT=2484))(SECURITY=(SSL_SERVER_CERT_DN=\"CN=db, O=Example  Corp\")))", ConnectStringText.Normalize(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n# only a comment\n")]
    public void Blank_input_is_empty(string? text) => Assert.Equal(string.Empty, ConnectStringText.Normalize(text));
}
