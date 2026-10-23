using Oranda.App.Features.MyBatisWorkspace.Workspace;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>문장 id 형식(<see cref="MyBatisStatementText.IsValidId"/>): 영문·숫자·밑줄, 첫 글자는 숫자가 아님.</summary>
public sealed class MyBatisStatementIdTests
{
    [Theory]
    [InlineData("selectEmp", true)]
    [InlineData("_x1", true)]
    [InlineData("1abc", false)]
    [InlineData("a.b", false)]
    [InlineData("", false)]
    [InlineData("selectEmp\n", false)] // 정규식 끝의 $ 는 끝 줄바꿈을 통과시킨다(\z 로 막는다)
    public void IsValidId_follows_the_contract(string id, bool expected) => Assert.Equal(expected, MyBatisStatementText.IsValidId(id));
}
