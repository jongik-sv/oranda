using Oranda.App.Features.Execution;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.ResultGrid;
using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Tests.Features.GridEdit;

/// <summary>TSK-13-17 서버 ORDER BY 다시 조회: 편집 모드 유지, 원래 문장이 정본(중첩 금지), 정렬 열 위치 = 데이터 열 위치.</summary>
public sealed partial class GridEditTests
{
    private static List<ResultFilterRequest> RecordServerQueries()
    {
        var asked = new List<ResultFilterRequest>();
        ResultGridCommands.Filter = (request, _) =>
        {
            asked.Add(request);
            return Task.FromResult<IResultSet>(new MemoryResult(EmpRowCols, [.. EmpRows().Reverse()]));
        };
        return asked;
    }

    [Fact]
    public async Task Server_sort_keeps_edit_mode_and_wraps_only_the_original_statement()
    {
        var asked = RecordServerQueries();
        var (pane, first, session, _) = await RowsPaneAsync(edit: true);
        try
        {
            await first.ServerSort!(3, false, default); // SAL

            Assert.Single(asked);
            Assert.Equal(EmpRowsSql, asked[0].Query.Text);
            Assert.Equal("4 NULLS LAST", asked[0].OrderBy);
            Assert.Equal(
                ResultQueryWrapper.OrderBy(EmpRowsSql, 4, false),
                GridFilterRunner.BuildStatement(asked[0]).Text); // 단위 감싸기와 실행 경로가 만드는 문장이 같다

            var second = pane.Model!;
            Assert.NotSame(first, second);
            Assert.True(pane.IsEditMode); // 서버 정렬 뒤에도 편집 모드가 꺼지지 않는다
            Assert.Same(session, GridEditBinding.Of(second));
            Assert.False(pane.IsFiltered);
            Assert.Equal(3, second.SortColumn);
            Assert.False(second.SortDescending);

            await second.ServerSort!(3, true, default); // 두 번째 누르기도 원래 문장 기준
            Assert.Equal(2, asked.Count);
            Assert.Equal(EmpRowsSql, asked[1].Query.Text);
            Assert.DoesNotContain("SELECT * FROM", asked[1].Query.Text, StringComparison.Ordinal);
            Assert.Equal("4 DESC NULLS LAST", asked[1].OrderBy);
            Assert.True(pane.IsEditMode);
        }
        finally
        {
            GridEditCommands.Active = null;
            ResultGridCommands.Filter = null;
        }
    }

    [Fact]
    public async Task Server_sort_position_is_the_data_column_position_not_the_screen_order()
    {
        var asked = RecordServerQueries();
        var (_, m, _, _) = await RowsPaneAsync(edit: true);
        try
        {
            Assert.True(m.MoveColumn(3, 0)); // SAL 을 화면 맨 앞으로
            await m.ServerSort!(5, false, default); // 데이터 열 ROWID(6번째)

            Assert.Equal("6 NULLS LAST", asked[0].OrderBy);
        }
        finally
        {
            GridEditCommands.Active = null;
            ResultGridCommands.Filter = null;
        }
    }

    [Fact]
    public async Task Server_sort_is_blocked_while_the_edit_session_has_unapplied_changes()
    {
        var asked = RecordServerQueries();
        var (pane, m, session, _) = await RowsPaneAsync(edit: true);
        try
        {
            Assert.True(session!.SetCell(0, 1, "ZED"));

            Assert.Equal("반영하지 않은 변경이 있어 다시 조회할 수 없습니다.", m.ServerSortBlockedReason!(1, false));
            await m.ServerSort!(1, false, default);
            Assert.Empty(asked);
            Assert.Same(m, pane.Model);
        }
        finally
        {
            GridEditCommands.Active = null;
            ResultGridCommands.Filter = null;
        }
    }
}
