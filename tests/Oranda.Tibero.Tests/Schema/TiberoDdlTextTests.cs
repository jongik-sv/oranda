using Oranda.Tibero.Schema;

namespace Oranda.Tibero.Tests.Schema;

public class TiberoDdlTextTests
{
    [Fact]
    public void A_multi_word_keyword_is_matched_word_by_word_and_the_name_is_replaced()
    {
        string?[] lines = ["package   body hr.pkg_x AS\n", "  PROCEDURE p;\n", "END;\n/\n"];

        var ddl = TiberoDdlText.FromSource("PACKAGE BODY", "HR", "PKG_X", lines);

        Assert.Equal("CREATE OR REPLACE PACKAGE BODY \"HR\".\"PKG_X\" AS\n  PROCEDURE p;\nEND;", ddl);
    }

    [Theory]
    [InlineData("PACKAGE pkg AS\nEND;")]
    [InlineData("PACKAGEBODY pkg AS")]
    [InlineData("PACKAGE BODY ")]
    [InlineData("")]
    public void Source_that_does_not_start_with_the_keyword_and_a_name_gives_null(string source)
    {
        Assert.Null(TiberoDdlText.FromSource("PACKAGE BODY", "S", "N", source.Length == 0 ? [] : [source]));
    }

    [Theory]
    [InlineData("PACKAGE", "PACKAGE BODY pkg AS\nEND;")]
    [InlineData("TYPE", "TYPE BODY t AS\nEND;")]
    public void A_body_source_is_not_read_as_a_spec_named_body(string keyword, string source)
    {
        Assert.Null(TiberoDdlText.FromSource(keyword, "S", "N", [source]));
    }

    [Fact]
    public void The_terminator_is_a_slash_on_a_new_line_or_nothing()
    {
        Assert.Equal("CREATE OR REPLACE PROCEDURE p IS BEGIN NULL; END;\n/", TiberoDdlText.WithTerminator("CREATE OR REPLACE PROCEDURE p IS BEGIN NULL; END;", true));
        Assert.Equal("X", TiberoDdlText.WithTerminator("X", false));
    }
}
