using Oranda.Core.Metadata;
using Oranda.Oracle.Metadata;

namespace Oranda.Oracle.Tests.Metadata;

// TSK-12-02: 인자 없는 단독 프로시저가 빈 목록이 되어 「프로시저·함수 실행」 창이 빈 화면으로 뜨던 결함.
public class BuildProceduresTests
{
    // meta.procedures 열: procedure_name, overload, subprogram_id, object_type
    // meta.arguments 열: object_name, overload, position, argument_name, in_out, data_type, defaulted, type_owner, type_name, type_subname
    private static object?[] Member(string? name, string? overload = null, string type = "PROCEDURE") => [name, overload, 1m, type];

    private static object?[] Arg(string proc, int pos, string? name, string inOut, string type, string? overload = null)
        => [proc, overload, (decimal)pos, name, inOut, type, "N", null, null, null];

    [Theory]
    [InlineData(null)] // 11g 처럼 단독 프로시저의 procedure_name 이 비어 있는 서버
    [InlineData("DAILY_BATCH")]
    public void Standalone_procedure_without_arguments_is_still_listed(string? memberName)
    {
        var list = OracleMetadataProvider.BuildProcedures(new DbObjectRef("SCOTT", "DAILY_BATCH", "PROCEDURE"), [Member(memberName)], []);

        var p = Assert.Single(list);
        Assert.Equal("DAILY_BATCH", p.Name);
        Assert.False(p.IsFunction);
        Assert.Empty(p.Arguments);
    }

    [Fact]
    public void Standalone_procedure_with_arguments_keeps_them()
    {
        var list = OracleMetadataProvider.BuildProcedures(
            new DbObjectRef("SCOTT", "RAISE_SAL", "PROCEDURE"),
            [Member(null)],
            [Arg("RAISE_SAL", 1, "P_ID", "IN", "NUMBER"), Arg("RAISE_SAL", 2, "P_MSG", "OUT", "VARCHAR2")]);

        var p = Assert.Single(list);
        Assert.Equal(["P_ID", "P_MSG"], p.Arguments.Select(a => a.Name));
        Assert.Equal(ArgumentDirection.Out, p.Arguments[1].Direction);
    }

    [Fact]
    public void Function_without_arguments_has_its_return_type()
    {
        var list = OracleMetadataProvider.BuildProcedures(
            new DbObjectRef("SCOTT", "NOW_TEXT", "FUNCTION"),
            [Member(null, type: "FUNCTION")],
            [Arg("NOW_TEXT", 0, null, "OUT", "VARCHAR2")]);

        var p = Assert.Single(list);
        Assert.True(p.IsFunction);
        Assert.Equal("VARCHAR2", p.ReturnType);
        Assert.Empty(p.Arguments);
    }

    [Fact]
    public void Package_lists_members_without_arguments_and_keeps_overloads_apart()
    {
        var list = OracleMetadataProvider.BuildProcedures(
            new DbObjectRef("SCOTT", "EMP_PKG", "PACKAGE"),
            [Member("PING"), Member("HIRE", "1"), Member("HIRE", "2")],
            [Arg("HIRE", 1, "P_NAME", "IN", "VARCHAR2", "1"), Arg("HIRE", 1, "P_ID", "IN", "NUMBER", "2")]);

        Assert.Equal(["PING", "HIRE", "HIRE"], list.Select(p => p.Name));
        Assert.Empty(list[0].Arguments);
        Assert.Equal("P_NAME", Assert.Single(list[1].Arguments).Name);
        Assert.Equal("P_ID", Assert.Single(list[2].Arguments).Name);
        Assert.Equal([null, 1, 2], list.Select(p => p.Overload));
    }

    [Fact]
    public void Standalone_without_any_row_stays_empty()
    {
        Assert.Empty(OracleMetadataProvider.BuildProcedures(new DbObjectRef("SCOTT", "GONE", "PROCEDURE"), [], []));
    }
}
