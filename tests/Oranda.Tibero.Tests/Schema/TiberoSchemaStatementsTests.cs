using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Schema;
using Oranda.Tibero.Schema;

namespace Oranda.Tibero.Tests.Schema;

public class TiberoSchemaStatementsTests
{
    private readonly TiberoSchemaStatements _sut = new();

    private static RecycleBinItem Item(string objectName = "BIN$abc==$0", string original = "EMP", string type = "TABLE") =>
        new(objectName, original, type, "USERS", null, false, false, null);

    [Fact]
    public void Dbms_is_tibero() => Assert.Equal(Dbms.Tibero, _sut.Dbms);

    [Theory]
    [InlineData("PROCEDURE", "ALTER PROCEDURE \"HR\".\"P1\" COMPILE")]
    [InlineData("FUNCTION", "ALTER FUNCTION \"HR\".\"P1\" COMPILE")]
    [InlineData("PACKAGE", "ALTER PACKAGE \"HR\".\"P1\" COMPILE")]
    [InlineData("PACKAGE BODY", "ALTER PACKAGE \"HR\".\"P1\" COMPILE BODY")]
    [InlineData("TRIGGER", "ALTER TRIGGER \"HR\".\"P1\" COMPILE")]
    [InlineData("VIEW", "ALTER VIEW \"HR\".\"P1\" COMPILE")]
    [InlineData("TYPE", "ALTER TYPE \"HR\".\"P1\" COMPILE")]
    [InlineData("TYPE BODY", "ALTER TYPE \"HR\".\"P1\" COMPILE BODY")]
    public void Compile_builds_the_alter_compile_text_per_type(string type, string expected)
    {
        var statement = _sut.Compile(new DbObjectRef("HR", "P1", type));

        Assert.NotNull(statement);
        Assert.Equal(expected, statement.Text);
        Assert.Equal("ALTER", statement.Verb);
        Assert.Equal(StatementKind.Ddl, statement.Kind);
    }

    [Theory]
    [InlineData("JAVA SOURCE")]
    [InlineData("JAVA CLASS")]
    [InlineData("TABLE")]
    public void Compile_returns_null_for_types_that_cannot_be_compiled(string type) =>
        Assert.Null(_sut.Compile(new DbObjectRef("HR", "X", type)));

    [Fact]
    public void Compile_quotes_identifiers_and_doubles_embedded_quotes()
    {
        var statement = _sut.Compile(new DbObjectRef("H\"R", "P\"1", "PROCEDURE"));

        Assert.Equal("ALTER PROCEDURE \"H\"\"R\".\"P\"\"1\" COMPILE", statement!.Text);
    }

    [Fact]
    public void Compile_of_a_public_synonym_has_no_owner_part()
    {
        var statement = _sut.Compile(new DbObjectRef("PUBLIC", "S1", "SYNONYM"));

        Assert.Equal("ALTER PUBLIC SYNONYM \"S1\" COMPILE", statement!.Text);
    }

    [Fact]
    public void PurgeItem_purges_a_table_by_its_recycle_bin_name_and_asks_for_the_original_name()
    {
        var statement = _sut.PurgeItem(Item());

        Assert.Equal("PURGE TABLE \"BIN$abc==$0\"", statement.Text);
        Assert.Equal("PURGE", statement.Verb);
        Assert.Equal("EMP", statement.DangerousTarget);
    }

    [Fact]
    public void PurgeItem_of_an_index_purges_an_index()
    {
        var statement = _sut.PurgeItem(Item("BIN$i==$0", "EMP_IX", "INDEX"));

        Assert.Equal("PURGE INDEX \"BIN$i==$0\"", statement.Text);
    }

    [Fact]
    public void PurgeAll_empties_the_recycle_bin()
    {
        var statement = _sut.PurgeAll();

        Assert.Equal("PURGE RECYCLEBIN", statement.Text);
        Assert.Equal("RECYCLEBIN", statement.DangerousTarget);
    }

    [Fact]
    public void Flashback_restores_the_dropped_table()
    {
        var statement = _sut.Flashback(Item());

        Assert.Equal("FLASHBACK TABLE \"BIN$abc==$0\" TO BEFORE DROP", statement.Text);
        Assert.Equal("FLASHBACK", statement.Verb);
    }

    [Fact]
    public void Flashback_with_a_new_name_adds_a_quoted_rename_and_blank_names_are_ignored()
    {
        Assert.Equal("FLASHBACK TABLE \"BIN$abc==$0\" TO BEFORE DROP RENAME TO \"EMP\"\"2\"", _sut.Flashback(Item(), " EMP\"2 ").Text);
        Assert.Equal("FLASHBACK TABLE \"BIN$abc==$0\" TO BEFORE DROP", _sut.Flashback(Item(), "  ").Text);
    }

    [Fact]
    public void ParameterChangeDdl_builds_a_memory_scope_statement_without_a_scope_clause() =>
        Assert.Equal("ALTER SYSTEM SET OPEN_CURSORS = 300;", _sut.ParameterChangeDdl("OPEN_CURSORS", "300", ParameterScope.Memory));

    [Fact]
    public void ParameterChangeDdl_quotes_text_values_and_doubles_embedded_quotes()
    {
        Assert.Equal("ALTER SYSTEM SET X = 'it''s';", _sut.ParameterChangeDdl("X", "it's", ParameterScope.Memory));
        Assert.Equal("ALTER SYSTEM SET X = '';", _sut.ParameterChangeDdl("X", "  ", ParameterScope.Memory));
    }

    [Theory]
    [InlineData(ParameterScope.Spfile)]
    [InlineData(ParameterScope.Both)]
    [InlineData((ParameterScope)99)]
    public void ParameterChangeDdl_refuses_unmeasured_parameter_file_scopes(ParameterScope scope) =>
        Assert.Throws<NotSupportedException>(() => _sut.ParameterChangeDdl("X", "1", scope));

    [Fact]
    public void ParameterChangeDdl_rejects_bad_names() =>
        Assert.Throws<ArgumentException>(() => _sut.ParameterChangeDdl("X; DROP", "1", ParameterScope.Memory));
}
