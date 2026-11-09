using Oranda.Core.Execution;
using Oranda.Core.Execution.SqlPlus;
using Oranda.Testing;

namespace Oranda.Core.Tests.Execution.SqlPlus;

// 소유: TSK-06-06. 통계 출처가 없는 DBMS 는 SET STATISTICS ON 을 거절하고 질의를 보내지 않는다(측정 못 하는 값은 끄고 이유를 알린다).
public class SqlPlusStatisticsSupportTests
{
    private static SqlStatement SetStatistics(string value) =>
        new($"SET STATISTICS {value}", default, StatementKind.SqlPlus) { Verb = "SET" };

    [Fact]
    public async Task Statistics_on_without_source_reports_not_supported_and_sends_no_query()
    {
        var engine = new FakeExecutionService();
        var processor = new SqlPlusCommandProcessor(() => engine, () => null!, statistics: null);

        var r = await processor.ExecuteAsync(new FakeSession(), SetStatistics("ON"), CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, r.Status);
        Assert.Equal(SqlPlusCommandProcessor.StatisticsNotSupportedMessage, Assert.Single(r.DbmsOutput));
        Assert.Empty(engine.Calls);
    }

    [Fact]
    public async Task Statistics_off_without_source_is_silent()
    {
        var engine = new FakeExecutionService();
        var processor = new SqlPlusCommandProcessor(() => engine, () => null!, statistics: null);

        var r = await processor.ExecuteAsync(new FakeSession(), SetStatistics("OFF"), CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, r.Status);
        Assert.Empty(r.DbmsOutput);
        Assert.Empty(engine.Calls);
    }

    [Fact]
    public async Task Statistics_on_with_source_is_accepted_silently()
    {
        var engine = new FakeExecutionService();
        var processor = new SqlPlusCommandProcessor(() => engine, () => null!, statistics: new SessionStatisticsSource("SELECT 1", ["x"]));

        var r = await processor.ExecuteAsync(new FakeSession(), SetStatistics("ON"), CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, r.Status);
        Assert.Empty(r.DbmsOutput);
    }

    [Fact]
    public async Task Define_store_is_shared_through_script_directives()
    {
        var directives = DirectivesOf(new SqlPlusCommandProcessor(() => new FakeExecutionService(), () => null!));
        var session = new FakeSession();

        directives.SetDefine(session, "who", "scott");

        Assert.Equal("scott", directives.GetDefine(session, "who"));
        Assert.False(directives.ExitOnError(session));
    }

    // 실행 엔진처럼 구현 형식이 아니라 계약으로만 본다.
    private static IScriptDirectives DirectivesOf(object processor) => (IScriptDirectives)processor;
}
