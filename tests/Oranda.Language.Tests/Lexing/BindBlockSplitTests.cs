using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Workspaces;
using Oranda.Language.Lexing;

namespace Oranda.Language.Tests.Lexing;

/// <summary>
/// .sql 끝 바인드 블록(<c>-- @oranda-binds v1</c>, EXEC-03)이 편집기에 남아 있어도(사용자가 붙여 넣는 등) 스크립트 실행·문장 분리에서
/// 빈 문장·주석만 있는 문장·가짜 바인드를 만들지 않는다. 실행 컨트롤러가 쓰는 두 분리 방식(빈 줄 끝 표지 켬·끔) 모두 본다.
/// </summary>
public class BindBlockSplitTests
{
    private static readonly WorkspaceBind[] Binds =
    [
        new() { Name = "EMPNO", Type = BindDataType.Number, Value = "1002" },
        new() { Name = "NOTE", Value = "x; :fake /\n*/ select 1 from dual;" },
    ];

    public static TheoryData<string, bool> Cases()
    {
        var data = new TheoryData<string, bool>();
        foreach (var blank in new[] { false, true })
        {
            data.Add("select * from emp where empno = :empno;\nupdate emp set note = :note where empno = :empno;\n", blank);
            data.Add("select * from emp where empno = :empno;\r\nupdate emp set note = :note where empno = :empno;\r\n", blank);
            data.Add("select * from emp where empno = :empno;\nupdate emp set note = :note where empno = :empno", blank); // 끝 문장에 ; 없음
            data.Add("select * from emp where empno = :empno\n/\nbegin\n  update emp set note = :note where empno = :empno;\nend;\n/\n", blank);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Trailing_bind_block_adds_no_statement_and_no_binds(string body, bool splitOnBlankLine)
    {
        var o = SplitOptions.Default with { SplitOnBlankLine = splitOnBlankLine };
        var plain = StatementSplitter.Split(body, o);

        var withBlock = StatementSplitter.Split(SqlFileBinds.Append(body, Binds), o);

        Assert.Equal(2, plain.Count);
        Assert.Equal(plain.Select(s => s.Text.TrimEnd()), withBlock.Select(s => s.Text.TrimEnd()));
        Assert.Equal(plain.Select(s => s.Kind), withBlock.Select(s => s.Kind));
        Assert.Equal(plain.SelectMany(s => s.BindNames), withBlock.SelectMany(s => s.BindNames));
        Assert.All(withBlock, s => Assert.DoesNotContain("@oranda-binds", s.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void Block_alone_is_not_a_statement()
    {
        var text = SqlFileBinds.Append(string.Empty, Binds);

        Assert.All(StatementSplitter.Split(text, SplitOptions.Default), s => Assert.True(string.IsNullOrWhiteSpace(s.Text), s.Text));
    }
}
