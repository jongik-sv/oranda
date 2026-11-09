using Oranda.App.Features.Execution;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Text;

namespace Oranda.App.Tests.Features.Execution;

/// <summary>세션 실행 순번: 서버가 낸 오류(번호가 0 이 아님)면 서버에 보낸 문장으로 센다. Tibero 드라이버 오류 번호는 음수다(TBR-8033 → -8033).</summary>
public class SessionRunLogWasSentTests
{
    private static ExecutionResult Failed(DbError? error) =>
        new(new SqlStatement("select * from nope", new TextRange(0, 18), StatementKind.Query), ExecutionStatus.Failed) { Error = error };

    [Fact]
    public void An_oracle_server_error_with_a_positive_number_counts_as_sent()
    {
        Assert.True(SessionRunLog.WasSent(Failed(new DbError("ORA-00942", "table or view does not exist") { Number = 942 })));
    }

    [Fact]
    public void A_tibero_server_error_with_a_negative_number_counts_as_sent()
    {
        Assert.True(SessionRunLog.WasSent(Failed(new DbError("TBR-8033", "Table not found") { Number = -8033, Dbms = Dbms.Tibero })));
    }

    [Fact]
    public void An_error_the_app_raised_itself_with_number_zero_does_not_count_as_sent()
    {
        Assert.False(SessionRunLog.WasSent(Failed(new DbError("ORANDA-001", "session closed"))));
        Assert.False(SessionRunLog.WasSent(Failed(null)));
    }
}
