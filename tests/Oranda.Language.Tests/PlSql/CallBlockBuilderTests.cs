using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Language.PlSql;

namespace Oranda.Language.Tests.PlSql;

// 소유: TSK-08-02. 익명 블록 조립.
public class CallBlockBuilderTests
{
    private static ArgumentInfo Arg(string name, int pos, string type, ArgumentDirection dir = ArgumentDirection.In, bool def = false)
        => new(name, pos, type, dir) { HasDefault = def };

    private static ProcedureInfo Proc(string name, bool fn, string? ret, params ArgumentInfo[] args)
        => new(name, fn, args) { ReturnType = ret };

    private static readonly CallTarget Standalone = new("SCOTT", null, "P");

    [Fact]
    public void Procedure_without_arguments_is_a_plain_call()
    {
        var b = CallBlockBuilder.Build(Standalone, Proc("P", false, null), []);

        Assert.True(b.CanRun);
        Assert.Equal("BEGIN\n  SCOTT.P;\nEND;".ReplaceLineEndings(), b.Text.ReplaceLineEndings());
        Assert.Empty(b.Binds);
        Assert.Equal(StatementKind.PlSql, b.Statement.Kind);
    }

    [Fact]
    public void In_out_and_inout_arguments_use_named_binds_with_directions()
    {
        var proc = Proc("P", false, null,
            Arg("A", 1, "NUMBER"),
            Arg("B", 2, "VARCHAR2", ArgumentDirection.Out),
            Arg("C", 3, "VARCHAR2", ArgumentDirection.InOut));

        var b = CallBlockBuilder.Build(Standalone, proc,
        [
            new CallInput("A", "12.5"),
            new CallInput("C", "x"),
        ]);

        Assert.True(b.CanRun);
        Assert.Contains("A => :P_A", b.Text, StringComparison.Ordinal);
        Assert.Contains("B => :P_B", b.Text, StringComparison.Ordinal);
        Assert.Equal(["P_A", "P_B", "P_C"], b.Statement.BindNames);
        Assert.Equal(new BindValue(BindDataType.Number, 12.5m, BindDirection.In), b.Binds["P_A"]);
        Assert.Equal(BindDirection.Out, b.Binds["P_B"].Direction);
        Assert.Null(b.Binds["P_B"].Value);
        Assert.Equal(new BindValue(BindDataType.Varchar2, "x", BindDirection.InOut), b.Binds["P_C"]);
        Assert.Equal(["P_B", "P_C"], b.Outputs.Select(o => o.BindName));
    }

    [Fact]
    public void Function_return_value_is_assigned_to_out_bind()
    {
        var proc = Proc("F", true, "NUMBER", Arg("X", 1, "NUMBER"));
        var b = CallBlockBuilder.Build(new CallTarget("SCOTT", "PKG", "F"), proc, [new CallInput("X", "3")]);

        Assert.StartsWith("BEGIN\n  :RET := SCOTT.PKG.F(".ReplaceLineEndings(), b.Text.ReplaceLineEndings(), StringComparison.Ordinal);
        Assert.Equal(BindDirection.Out, b.Binds["RET"].Direction);
        var o = Assert.Single(b.Outputs);
        Assert.Equal("반환값", o.Label);
    }

    [Fact]
    public void Defaulted_arguments_can_be_left_out()
    {
        var proc = Proc("P", false, null, Arg("A", 1, "NUMBER"), Arg("B", 2, "NUMBER", def: true));

        var omitted = CallBlockBuilder.Build(Standalone, proc, [new CallInput("A", "1"), new CallInput("B", "", UseDefault: true)]);
        var given = CallBlockBuilder.Build(Standalone, proc, [new CallInput("A", "1"), new CallInput("B", "2")]);

        Assert.DoesNotContain("B =>", omitted.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("P_B", omitted.Statement.BindNames);
        Assert.Contains("B => :P_B", given.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Use_default_is_ignored_for_arguments_without_default()
    {
        var proc = Proc("P", false, null, Arg("A", 1, "NUMBER"));
        var b = CallBlockBuilder.Build(Standalone, proc, [new CallInput("A", "1", UseDefault: true)]);

        Assert.Contains("A => :P_A", b.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_flag_and_empty_numeric_send_null()
    {
        var proc = Proc("P", false, null, Arg("A", 1, "NUMBER"), Arg("B", 2, "VARCHAR2"), Arg("C", 3, "DATE"));
        var b = CallBlockBuilder.Build(Standalone, proc, [new CallInput("A", "", IsNull: true), new CallInput("B", "", IsNull: true)]);

        Assert.True(b.CanRun);
        Assert.Null(b.Binds["P_A"].Value);
        Assert.Null(b.Binds["P_B"].Value);
        Assert.Null(b.Binds["P_C"].Value);
    }

    [Theory]
    [InlineData("NUMBER", "abc")]
    [InlineData("DATE", "not-a-date")]
    [InlineData("RAW", "ZZ")]
    [InlineData("PL/SQL BOOLEAN", "maybe")]
    public void Unreadable_value_is_a_problem(string type, string text)
    {
        var proc = Proc("P", false, null, Arg("A", 1, type));
        var b = CallBlockBuilder.Build(Standalone, proc, [new CallInput("A", text)]);

        Assert.False(b.CanRun);
        Assert.Contains("A:", Assert.Single(b.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void Date_and_timestamp_values_are_parsed()
    {
        var proc = Proc("P", false, null, Arg("D", 1, "DATE"), Arg("T", 2, "TIMESTAMP"), Arg("Z", 3, "TIMESTAMP WITH TIME ZONE"));
        var b = CallBlockBuilder.Build(Standalone, proc,
        [
            new CallInput("D", "2026-10-20"),
            new CallInput("T", "2026-10-20 13:45:10"),
            new CallInput("Z", "2026-10-20 13:45:10 +09:00"),
        ]);

        Assert.Equal(new DateTime(2026, 10, 20), b.Binds["P_D"].Value);
        Assert.Equal(new DateTime(2026, 10, 20, 13, 45, 10), b.Binds["P_T"].Value);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 13, 45, 10, TimeSpan.FromHours(9)), b.Binds["P_Z"].Value);
        Assert.Equal(BindDataType.TimestampTz, b.Binds["P_Z"].Type);
    }

    [Fact]
    public void Boolean_goes_through_local_variable_and_number_bind()
    {
        var proc = Proc("P", false, null,
            Arg("FLAG", 1, "PL/SQL BOOLEAN"),
            Arg("DONE", 2, "PL/SQL BOOLEAN", ArgumentDirection.Out),
            Arg("BOTH", 3, "PL/SQL BOOLEAN", ArgumentDirection.InOut));
        var b = CallBlockBuilder.Build(Standalone, proc, [new CallInput("FLAG", "true"), new CallInput("BOTH", "N")]);

        Assert.True(b.CanRun);
        Assert.StartsWith("DECLARE", b.Text, StringComparison.Ordinal);
        Assert.Equal("DECLARE", b.Statement.Verb);
        Assert.Contains("V_P_FLAG BOOLEAN;", b.Text, StringComparison.Ordinal);
        Assert.Contains("FLAG => V_P_FLAG", b.Text, StringComparison.Ordinal);
        Assert.Contains(":P_DONE := CASE WHEN V_P_DONE THEN 1 WHEN NOT V_P_DONE THEN 0 END;", b.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("V_P_FLAG THEN 1", b.Text, StringComparison.Ordinal); // IN 전용은 되돌려 쓰지 않는다
        Assert.Equal(new BindValue(BindDataType.Number, 1m, BindDirection.In), b.Binds["P_FLAG"]);
        Assert.Equal(0m, b.Binds["P_BOTH"].Value);

        var done = b.Outputs.Single(o => o.BindName == "P_DONE");
        Assert.Equal("TRUE", CallBlock.FormatOut(done, 1m));
        Assert.Equal("FALSE", CallBlock.FormatOut(done, 0m));
        Assert.Equal("NULL", CallBlock.FormatOut(done, null));
    }

    [Fact]
    public void Ref_cursor_out_is_a_cursor_output()
    {
        var proc = Proc("P", false, null, Arg("RC", 1, "REF CURSOR", ArgumentDirection.Out));
        var b = CallBlockBuilder.Build(Standalone, proc, []);

        Assert.True(b.CanRun);
        Assert.Equal(BindDataType.RefCursor, b.Binds["P_RC"].Type);
        Assert.True(Assert.Single(b.Outputs).IsCursor);
    }

    [Fact]
    public void Ref_cursor_in_is_unsupported()
    {
        var proc = Proc("P", false, null, Arg("RC", 1, "REF CURSOR"));
        var b = CallBlockBuilder.Build(Standalone, proc, []);

        Assert.False(b.CanRun);
    }

    [Theory]
    [InlineData("PL/SQL RECORD")]
    [InlineData("PL/SQL TABLE")]
    [InlineData("TABLE")]
    [InlineData("OBJECT")]
    [InlineData("INTERVAL DAY TO SECOND")]
    public void Unsupported_required_argument_blocks_running(string type)
    {
        var proc = Proc("P", false, null, Arg("X", 1, type));
        var b = CallBlockBuilder.Build(Standalone, proc, []);

        Assert.False(b.CanRun);
        Assert.Contains("X:", Assert.Single(b.Problems), StringComparison.Ordinal);
        Assert.False(CallBlockBuilder.Describe(proc)[0].IsSupported);
    }

    [Fact]
    public void Unsupported_argument_with_default_is_skipped()
    {
        var proc = Proc("P", false, null, Arg("A", 1, "NUMBER"), Arg("R", 2, "PL/SQL RECORD", def: true));
        var b = CallBlockBuilder.Build(Standalone, proc, [new CallInput("A", "1")]);

        Assert.True(b.CanRun);
        Assert.DoesNotContain("R =>", b.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SCOTT", null, "P", "SCOTT.P")]
    [InlineData("scott", null, "p", "\"scott\".\"p\"")]
    [InlineData("HR", "EMP_PKG", "RAISE_SAL", "HR.EMP_PKG.RAISE_SAL")]
    [InlineData("HR", "PKG", "LEVEL", "HR.PKG.\"LEVEL\"")]
    [InlineData("HR", "PKG", "A B", "HR.PKG.\"A B\"")]
    public void Names_are_quoted_only_when_needed(string owner, string? pkg, string name, string expected)
        => Assert.Equal(expected, new CallTarget(owner, pkg, name).QualifiedName);

    [Fact]
    public void Long_argument_names_fall_back_to_positional_bind_names()
    {
        var proc = Proc("P", false, null, Arg("A_VERY_LONG_ARGUMENT_NAME_OF_THIRTY", 4, "NUMBER"));
        var p = Assert.Single(CallBlockBuilder.Describe(proc));

        Assert.Equal("B4", p.BindName);
    }

    [Fact]
    public void Quoted_argument_names_are_used_in_named_notation()
    {
        var proc = Proc("P", false, null, Arg("mixedCase", 1, "NUMBER"));
        var b = CallBlockBuilder.Build(Standalone, proc, [new CallInput("mixedCase", "1")]);

        Assert.Contains("\"mixedCase\" => :P_MIXEDCASE", b.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Values_are_never_inlined_into_the_text()
    {
        var proc = Proc("P", false, null, Arg("A", 1, "VARCHAR2"));
        var b = CallBlockBuilder.Build(Standalone, proc, [new CallInput("A", "x'); DROP TABLE t; --")]);

        Assert.DoesNotContain("DROP", b.Text, StringComparison.Ordinal);
        Assert.Equal("x'); DROP TABLE t; --", b.Binds["P_A"].Value);
    }
}
