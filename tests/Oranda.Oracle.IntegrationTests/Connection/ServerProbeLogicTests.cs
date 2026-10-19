using Oranda.Oracle.Connection;

namespace Oranda.Oracle.IntegrationTests.Connection;

// DB 없이 도는 단위 시험이다(Category 표시 없음).

public class ServerProbeLogicTests
{
    [Theory]
    [InlineData(false, new string[0], false)]
    [InlineData(true, new string[0], true)]
    [InlineData(false, new[] { "EXECUTE ANY PROCEDURE" }, true)]
    [InlineData(false, new[] { "CREATE SESSION" }, false)]
    public void Dbms_monitor_needs_grant_row_or_execute_any_procedure(bool hasRow, string[] privs, bool expected)
    {
        var set = new HashSet<string>(privs, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(expected, ServerProbe.CanExecuteDbmsMonitor(hasRow, set));
    }
}
