using Oranda.App.Shell.Composition;
using Oranda.Core.Composition;
using Oranda.Core.Composition.Dispatch;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Testing;

namespace Oranda.App.Tests.Shell.Composition;

// 소유: tb-base K0(D-046). 앱 조립에서 실행 엔진이 받는 SQL*Plus 처리기는 Core 디스패처다.
// 디스패처를 거쳐도 스크립트 지시(WHENEVER SQLERROR EXIT·DEFINE 값)가 실행 엔진까지 닿아야 한다(T1 이전 Oracle 동작).
public class SqlPlusDirectivesCompositionTests
{
    private static SqlStatement SqlPlus(string text, string verb, params string[] substitutions) =>
        new(text, default, StatementKind.SqlPlus) { Verb = verb, SubstitutionNames = substitutions };

    private static (ServiceContainer Container, FakeExecutionInteraction Interaction) Compose()
    {
        var c = AppComposition.Create(new AppCompositionOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "oranda-k0-" + Guid.NewGuid().ToString("N")) });
        var interaction = new FakeExecutionInteraction();
        c.AddInstance<IExecutionInteraction>(interaction);
        return (c, interaction);
    }

    private static async Task<List<ExecutionResult>> RunScriptAsync(IExecutionService exec, FakeSession session, ScriptErrorMode mode, params SqlStatement[] stmts)
    {
        var results = new List<ExecutionResult>();
        await foreach (var r in exec.ExecuteScriptAsync(session, stmts, mode, new ExecutionOptions { Source = HistorySource.Script }, CancellationToken.None))
        {
            results.Add(r);
        }

        return results;
    }

    [Fact]
    public async Task Whenever_sqlerror_exit_stops_the_script_through_the_dispatcher()
    {
        var (c, _) = Compose();
        using var _c = c;
        Assert.IsType<SqlPlusCommandProcessorDispatcher>(c.GetRequiredService<ISqlPlusCommandProcessor>());
        var exec = c.GetRequiredService<IExecutionService>();
        var session = new FakeSession();

        var results = await RunScriptAsync(exec, session, ScriptErrorMode.Continue,
            SqlPlus("WHENEVER SQLERROR EXIT ROLLBACK", "WHENEVER"),
            SqlPlus("WHENEVER NONSENSE", "WHENEVER"), // SP2-0158 로 실패
            SqlPlus("PROMPT after", "PROMPT"));

        Assert.Equal(2, results.Count); // 설정(계속)보다 WHENEVER SQLERROR EXIT 가 앞선다
        Assert.Equal(ExecutionStatus.Succeeded, results[0].Status);
        Assert.Equal(ExecutionStatus.Failed, results[1].Status);
        Assert.Equal(1, session.RollbackCount); // EXIT ROLLBACK 의 트랜잭션 처리
    }

    [Fact]
    public async Task Define_value_is_seen_by_the_engine_substitution_through_the_dispatcher()
    {
        var (c, interaction) = Compose();
        using var _c = c;
        interaction.SubstitutionAnswer = null; // 묻게 되면 취소 → 실패로 드러난다
        var exec = c.GetRequiredService<IExecutionService>();
        var session = new FakeSession();

        var results = await RunScriptAsync(exec, session, ScriptErrorMode.Stop,
            SqlPlus("DEFINE d = 20", "DEFINE"),
            SqlPlus("PROMPT &d", "PROMPT", "d"));

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(ExecutionStatus.Succeeded, r.Status));
        Assert.Equal("PROMPT 20", results[1].Statement.Text);
    }
}
