using Oranda.App.Features.Execution;
using Oranda.App.Tests.Features.SqlEditor;

namespace Oranda.App.Tests.Features.Execution;

/// <summary>GRID-06 결과 탭 고정: 고정한 탭은 다음 실행이 바꾸지 않고 결과는 새 탭(겹치지 않는 번호)에 열린다.</summary>
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class ResultTabPinTests
{
    [Fact]
    public Task A_pinned_result_tab_survives_the_next_run_which_opens_a_new_tab() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select 1 from dual");
        rig.Exec.Handler = (s, _) => Task.FromResult(Rig.Query(s));

        await rig.Controller.RunAsync(RunMode.Statement);
        var results = (ResultTabsViewModel)tab.ResultPane!;
        var pinned = Assert.Single(results.Tabs);
        pinned.TogglePin();

        await rig.Controller.RunAsync(RunMode.Statement);

        Assert.Equal(2, results.Tabs.Count);
        Assert.Same(pinned, results.Tabs[0]);
        Assert.NotNull(pinned.Pane!.Model); // 고정한 결과는 닫지 않았다
        Assert.Equal("결과 2", results.Tabs[1].Title);
        Assert.Same(results.Tabs[1], results.Selected);

        await rig.Controller.RunAsync(RunMode.Statement); // 고정하지 않은 탭만 바뀐다
        Assert.Equal(2, results.Tabs.Count);
        Assert.Same(pinned, results.Tabs[0]);
    });

    [Fact]
    public Task A_script_run_keeps_pinned_tabs_and_selects_its_own_first_result() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select 1 from dual;\nselect 2 from dual;");
        rig.Exec.Handler = (s, _) => Task.FromResult(Rig.Query(s));

        await rig.Controller.RunAsync(RunMode.Script);
        var results = (ResultTabsViewModel)tab.ResultPane!;
        var pinned = results.Tabs[1]; // 「결과 2」
        pinned.TogglePin();

        await rig.Controller.RunAsync(RunMode.Script);

        Assert.Contains(pinned, results.Tabs);
        Assert.Equal(results.Tabs.Count, results.Tabs.Select(t => t.Title).Distinct().Count()); // 번호가 겹치지 않는다
        Assert.NotSame(pinned, results.Selected);
        Assert.True(results.Selected!.HasPane);
    });
}
