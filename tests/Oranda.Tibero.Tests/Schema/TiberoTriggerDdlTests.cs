using Oranda.Tibero.Schema;

namespace Oranda.Tibero.Tests.Schema;

public class TiberoTriggerDdlTests
{
    [Fact]
    public void Source_lines_become_one_create_statement_with_the_qualified_name()
    {
        string?[] lines = ["TRIGGER trg_emp_bi\n", "BEFORE INSERT ON emp\n", "FOR EACH ROW\n", "BEGIN\n", "  :new.id := 1;\n", "END;"];

        var ddl = TiberoTriggerDdl.FromSource("SCOTT", "TRG_EMP_BI", lines);

        Assert.Equal("CREATE OR REPLACE TRIGGER \"SCOTT\".\"TRG_EMP_BI\"\nBEFORE INSERT ON emp\nFOR EACH ROW\nBEGIN\n  :new.id := 1;\nEND;", ddl);
    }

    [Theory]
    [InlineData("CREATE OR REPLACE TRIGGER \"HR\".\"T1\" AFTER DELETE ON t\nBEGIN NULL; END;\n/\n")]
    [InlineData("create trigger hr.t1 AFTER DELETE ON t\nBEGIN NULL; END;")]
    [InlineData("  EDITIONABLE TRIGGER t1 AFTER DELETE ON t\nBEGIN NULL; END;")]
    public void Leading_create_words_an_owner_and_a_trailing_slash_are_normalized(string source)
    {
        var ddl = TiberoTriggerDdl.FromSource("SCOTT", "T1", [source]);

        Assert.Equal("CREATE OR REPLACE TRIGGER \"SCOTT\".\"T1\" AFTER DELETE ON t\nBEGIN NULL; END;", ddl);
    }

    [Fact]
    public void Lines_without_line_feeds_are_joined_line_by_line()
    {
        var ddl = TiberoTriggerDdl.FromSource("S", "T", ["TRIGGER t", "AFTER UPDATE ON x", "BEGIN NULL; END;"]);

        Assert.Equal("CREATE OR REPLACE TRIGGER \"S\".\"T\"\nAFTER UPDATE ON x\nBEGIN NULL; END;", ddl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("PROCEDURE p IS BEGIN NULL; END;")]
    [InlineData("TRIGGERX t AFTER UPDATE ON x")]
    [InlineData("TRIGGER ")]
    public void Text_that_is_not_a_trigger_gives_null(string source)
    {
        string?[] lines = source.Length == 0 ? [] : [source];

        Assert.Null(TiberoTriggerDdl.FromSource("S", "T", lines));
    }

    [Fact]
    public void Names_with_quotes_are_doubled_and_disable_is_a_separate_statement()
    {
        Assert.Equal("\"A\"\"B\".\"T\"", TiberoTriggerDdl.Qualified("A\"B", "T"));
        Assert.Equal("ALTER TRIGGER \"SCOTT\".\"T1\" DISABLE", TiberoTriggerDdl.DisableStatement("SCOTT", "T1"));
    }
}
