using Oranda.App.Features.Execution;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Execution;

namespace Oranda.App.Tests.Features.Execution;

/// <summary>결과 탭 줄의 편집 모드 단추로 읽기 결과에 돌아갈 때의 다시 실행: 결과를 만든 문장을 그대로 일반 실행하고 편집 세션을 붙이지 않는다.</summary>
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class ResultTabsRerunTests
{
    [Fact]
    public Task Plain_rerun_runs_the_given_statement_not_the_caret_one_and_attaches_no_edit_session() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select 1 from dual;\nselect 2 from dual;");
        tab.Select(25, 0); // 커서는 둘째 문장
        rig.Exec.Handler = (s, _) => Task.FromResult(Rig.Query(s));
        var first = new SqlStatement("select 1 from dual", default, StatementKind.Query) { Verb = "SELECT" };

        await rig.Controller.RerunAsync(first, default);

        Assert.Equal("select 1 from dual", Assert.Single(rig.Exec.Statements).Text);
        var results = Assert.IsType<ResultTabsViewModel>(tab.ResultPane);
        var item = Assert.Single(results.Tabs);
        Assert.NotNull(item.Pane?.Model);
        Assert.Null(item.EditSession);
        Assert.False(item.Title.EndsWith("(편집)", StringComparison.Ordinal));
    });

    [Fact]
    public Task Plain_rerun_replaces_the_result_in_place_instead_of_adding_a_tab() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select 1 from dual");
        rig.Exec.Handler = (s, _) => Task.FromResult(Rig.Query(s));
        await rig.Controller.RunAsync(RunMode.Statement);
        var results = Assert.IsType<ResultTabsViewModel>(tab.ResultPane);
        var stmt = rig.Exec.Statements[0];

        await rig.Controller.RerunAsync(stmt, default);

        Assert.Single(results.Tabs);
        Assert.Equal(2, rig.Exec.Statements.Count);
    });
}
