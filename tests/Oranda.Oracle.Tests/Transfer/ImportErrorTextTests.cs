using Oranda.Oracle.Transfer;

namespace Oranda.Oracle.Tests.Transfer;

// 소유: TSK-09-12. 가져오기 오류 행 문구에 오류 코드가 한 번만 나온다.
public class ImportErrorTextTests
{
    [Theory]
    [InlineData("ORA-00001", "ORA-00001: unique constraint (SCOTT.PK_T) violated", "ORA-00001: unique constraint (SCOTT.PK_T) violated")]
    [InlineData("ORA-00001", "  ORA-00001: unique constraint (SCOTT.PK_T) violated\n", "ORA-00001: unique constraint (SCOTT.PK_T) violated")]
    [InlineData("ORA-00001", "ora-00001: lower case", "ora-00001: lower case")]
    [InlineData("ORA-12899", "value too large for column", "ORA-12899: value too large for column")]
    [InlineData("ORA-00001", "ORA-00001", "ORA-00001")]
    [InlineData("ORA-00942", "", "ORA-00942")]
    [InlineData("ORA-00942", null, "ORA-00942")]
    [InlineData("", "plain message", "plain message")]
    [InlineData(null, "plain message", "plain message")]
    [InlineData("", "", "알 수 없는 오류")]
    public void Code_is_written_once(string? code, string? message, string expected) =>
        Assert.Equal(expected, OracleDataTransferService.ErrorText(code, message));

    [Fact]
    public void A_different_code_inside_the_message_is_kept_after_the_leading_code()
    {
        // 메시지 앞이 아니라 중간에 다른 코드가 있으면 그대로 둔다.
        Assert.Equal("ORA-00001: failed, see ORA-02291", OracleDataTransferService.ErrorText("ORA-00001", "failed, see ORA-02291"));
        Assert.Equal("ORA-02291: ORA-00001 parent", OracleDataTransferService.ErrorText("ORA-02291", "ORA-00001 parent"));
    }
}
