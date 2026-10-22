using Oranda.App.Features.Execution;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Execution;
using Oranda.Core.Text;

namespace Oranda.App.Tests.Features.Execution;

/// <summary>TSK-13-30 참조 행(부모/자식)·플래시백 결과 탭의 닫기(×).</summary>
[Collection("GridEditStatics")]
public sealed class ResultTabCloseTests
{
    private static RelatedRowsResult Related(string title)
    {
        var stmt = new SqlStatement("select 1 from dual", new TextRange(0, 18), StatementKind.Query);
        return new RelatedRowsResult(new OneColumnResult("1"), title, stmt, new Dictionary<string, BindValue>());
    }

    [Fact]
    public Task Related_tab_is_closable_and_closing_it_selects_the_left_neighbour() => Ui.Run(() =>
    {
        var tabs = new ResultTabsViewModel();
        var main = tabs.AddQuery(new OneColumnResult("1"), "결과 1", null);
        var parent = tabs.AddRelated(Related("부모: DEPT"));
        var child = tabs.AddRelated(Related("자식: EMP(FK_EMP_DEPT)"));

        Assert.False(main.IsClosable); // 원래 질의 결과 탭은 그대로
        Assert.True(parent.IsClosable);
        Assert.True(child.IsClosable);

        tabs.Selected = parent;
        parent.CloseTabCommand.Execute(null);

        Assert.Equal([main, child], tabs.Tabs);
        Assert.Same(main, tabs.Selected); // 왼쪽 이웃

        child.CloseTabCommand.Execute(null); // 선택 중인 탭
        Assert.Same(main, tabs.Selected);

        main.CloseTabCommand.Execute(null); // 닫을 수 없는 탭은 아무 일도 없다
        Assert.Single(tabs.Tabs);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Closing_a_background_related_tab_keeps_the_selection() => Ui.Run(() =>
    {
        var tabs = new ResultTabsViewModel();
        var main = tabs.AddQuery(new OneColumnResult("1"), "결과 1", null);
        var parent = tabs.AddRelated(Related("부모: DEPT"));
        tabs.Selected = main;

        parent.CloseTabCommand.Execute(null);

        Assert.Same(main, tabs.Selected);
        Assert.Null(parent.Pane!.Model); // 결과 집합은 정리된다
        return Task.CompletedTask;
    });
}
