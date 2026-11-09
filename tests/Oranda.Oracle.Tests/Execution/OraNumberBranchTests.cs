using System.Reflection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.Sessions;
using Oranda.Oracle.Transfer.Copy;

namespace Oranda.Oracle.Tests.Execution;

// 소유: Tibero T1 O0. Oracle 안의 ORA 번호 분기가 지금 어떤 번호를 어떤 뜻으로 보는지 고정한다.
// O1(OracleErrorClassifier)·O2(숫자 비교 → DbErrorKind)가 동작을 바꾸지 않았음을 이 시험으로 확인한다. DB 없음.
public class OraNumberBranchTests
{
    private static readonly int[] ConnectionLostNumbers = [28, 3113, 3114, 3135, 12537, 12570, 12571, 12583];

    // OracleSession.BrokenCodes — 연결이 끊겼음을 뜻하는 번호(CONN-11).
    [Theory]
    [InlineData(3113)]
    [InlineData(3114)]
    [InlineData(3135)]
    [InlineData(1012)]
    [InlineData(12170)]
    [InlineData(12537)]
    [InlineData(12541)]
    [InlineData(12543)]
    [InlineData(12547)]
    [InlineData(12571)]
    [InlineData(28547)]
    [InlineData(25402)]
    [InlineData(25408)]
    [InlineData(17002)]
    [InlineData(17008)]
    [InlineData(17410)]
    public void Session_treats_these_numbers_as_broken_connection(int number)
    {
        Assert.True(OracleSession.IsBrokenError(number));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(28)]        // 세션 KILL — 세션 모니터는 끊김, 세션 관리자는 끊김으로 보지 않는다.
    [InlineData(942)]
    [InlineData(1013)]      // 사용자 취소
    [InlineData(1031)]
    [InlineData(1555)]
    [InlineData(12583)]     // 복사 실행기만 끊김으로 본다.
    [InlineData(28000)]
    [InlineData(28001)]
    public void Session_does_not_treat_these_numbers_as_broken_connection(int number)
    {
        Assert.False(OracleSession.IsBrokenError(number));
    }

    [Fact]
    public void Cancelled_number_is_ora_01013()
    {
        Assert.Equal(1013, OracleExecutionService.CancelledNumber);
    }

    [Fact]
    public void Change_capture_object_not_found_is_ora_31603()
    {
        Assert.Equal(31603, OracleExecutionService.ChangeCapture.ObjectNotFound);
    }

    // CopyRunner 의 private 번호들. 이름과 값 모두 지금 그대로여야 O2 가 같은 뜻의 Kind 로 옮길 수 있다.
    [Theory]
    [InlineData("SnapshotTooOld", 1555)]
    [InlineData("CreatedWithCompilationErrors", 24344)]
    [InlineData("ParentKeysNotFound", 2298)]
    [InlineData("ReferencedByForeignKeys", 2449)]
    public void Copy_runner_numbers(string field, int expected)
    {
        var f = typeof(CopyRunner).GetField(field, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(f);
        Assert.Equal(expected, (int)f.GetRawConstantValue()!);
    }

    [Fact]
    public void Copy_runner_connection_lost_set()
    {
        var f = typeof(CopyRunner).GetField("ConnectionLost", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(f);
        var set = (HashSet<int>)f.GetValue(null)!;
        Assert.Equal(ConnectionLostNumbers, set.OrderBy(n => n).ToArray());
    }
}
