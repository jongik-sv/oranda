using Oranda.App.Features.Execution;
using Oranda.Core.Connection;
using Oranda.Core.Execution;

namespace Oranda.App.Tests.Features.Execution;

/// <summary>
/// 특성 시험(A0): 오류를 한 줄 글로 만드는 규칙을 고정한다. DBMS 추상화(T1) 리팩터링 전후로 Oracle 문구가 바뀌지 않는지 본다.
/// </summary>
public sealed class DbErrorTextTests
{
    [Fact]
    public void Format_null_returns_fallback()
    {
        Assert.Equal("실패", DbErrorText.Format(null));
        Assert.Equal("끝", DbErrorText.Format(null, "끝"));
    }

    [Fact]
    public void Format_prefixes_ora_code_padded_to_five_digits()
    {
        var error = new DbError("ORA-00942", "table or view does not exist") { Number = 942 };

        Assert.Equal("ORA-00942: table or view does not exist", DbErrorText.Format(error));
    }

    [Theory]
    [InlineData(1, "ORA-00001: unique constraint violated")]
    [InlineData(12541, "ORA-12541: no listener")]
    [InlineData(600, "ORA-00600: internal error")]
    public void Format_pads_number(int number, string expected)
    {
        var text = expected[(expected.IndexOf(": ", StringComparison.Ordinal) + 2)..];

        Assert.Equal(expected, DbErrorText.Format(new DbError("x", text) { Number = number }));
    }

    [Fact]
    public void Format_does_not_repeat_code_when_message_already_starts_with_it()
    {
        var error = new DbError("ORA-00942", "ORA-00942: table or view does not exist") { Number = 942 };

        Assert.Equal("ORA-00942: table or view does not exist", DbErrorText.Format(error));
    }

    [Fact]
    public void Format_without_server_number_returns_message_as_is()
    {
        var error = new DbError("E", "연결이 끊겼다") { Number = 0 };

        Assert.Equal("연결이 끊겼다", DbErrorText.Format(error));
    }

    [Fact]
    public void Format_uses_the_tibero_notation_for_tibero_errors()
    {
        var error = new DbError("TBR-8033", "object does not exist") { Number = 8033, Dbms = Dbms.Tibero };

        Assert.Equal("TBR-8033: object does not exist", DbErrorText.Format(error));
    }

    [Fact]
    public void Format_does_not_repeat_a_tibero_code_already_in_the_message()
    {
        var error = new DbError("TBR-8033", "TBR-8033: object does not exist") { Number = 8033, Dbms = Dbms.Tibero };

        Assert.Equal("TBR-8033: object does not exist", DbErrorText.Format(error));
    }
}
