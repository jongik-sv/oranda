using Oranda.App.Features.ResultGrid;
using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Tests.Features.ResultGrid;

/// <summary>TSK-13-17 서버 ORDER BY 다시 조회: 감싸기 문장, 막는 이유, 묻는 창에서 고른 뒤의 흐름과 해제.</summary>
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class ServerSortTests : IDisposable
{
    private static readonly ResultColumn[] Cols =
    [
        new(0, "EMPNO", "NUMBER", ColumnDataKind.Number),
        new(1, "ENAME", "VARCHAR2", ColumnDataKind.Text),
        new(2, "NOTE", "CLOB", ColumnDataKind.Clob),
    ];

    public ServerSortTests() => ResultGridCommands.Filter = null;

    public void Dispose() => ResultGridCommands.Filter = null;

    private sealed class TableResult(ResultColumn[] columns, object?[][] rows) : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns => columns;

        public bool IsComplete => true;

        public long? KnownRowCount => rows.Length;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            var cols = new List<IReadOnlyList<object?>>();
            for (var c = 0; c < columns.Length; c++)
            {
                cols.Add(pageIndex == 0 ? [.. rows.Select(r => r[c])] : []);
            }

            return Task.FromResult(new ResultPage(pageIndex, 0, pageIndex == 0 ? rows.Length : 0, cols) { IsLast = true });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static TableResult Emp(ResultColumn[]? cols = null) =>
        new(cols ?? Cols, [[3m, "C", null], [1m, "A", null], [2m, "B", null]]);

    private static SqlStatement Query(string text) => new(text, default, StatementKind.Query) { Verb = "SELECT" };

    private static async Task<SqlResultPaneViewModel> PaneAsync(string sql = "SELECT empno, ename, note FROM emp", ResultColumn[]? cols = null)
    {
        var pane = new SqlResultPaneViewModel { Statement = Query(sql) };
        pane.SetResult(Emp(cols));
        await pane.Model!.LoadFirstPageAsync();
        return pane;
    }

    private static List<ResultFilterRequest> RecordServerQueries()
    {
        var asked = new List<ResultFilterRequest>();
        ResultGridCommands.Filter = (request, _) =>
        {
            asked.Add(request);
            return Task.FromResult<IResultSet>(Emp());
        };
        return asked;
    }

    [Fact]
    public void Wrapper_builds_the_inline_view_with_data_position_and_nulls_last_in_both_directions()
    {
        Assert.Equal("SELECT * FROM (\nSELECT a FROM t\n)\nORDER BY 1 NULLS LAST", ResultQueryWrapper.OrderBy("SELECT a FROM t;  ", 1, false));
        Assert.Equal("SELECT * FROM (\nWITH x AS (SELECT 1 a FROM dual) SELECT a FROM x\n)\nORDER BY 3 DESC NULLS LAST",
                     ResultQueryWrapper.OrderBy("WITH x AS (SELECT 1 a FROM dual) SELECT a FROM x", 3, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => ResultQueryWrapper.OrderBy("SELECT 1 FROM dual", 0, false));
    }

    [Theory]
    [InlineData("SELECT empno, ename, note FROM emp FOR UPDATE", "FOR UPDATE")]
    [InlineData("SELECT empno, ename, note FROM emp FOR  UPDATE OF ename NOWAIT", "FOR UPDATE")]
    public async Task For_update_statements_cannot_be_requeried(string sql, string expected)
    {
        RecordServerQueries();
        var pane = await PaneAsync(sql);

        Assert.Contains(expected, pane.Model!.ServerSortBlockedReason!(0, false), StringComparison.Ordinal);
    }

    [Fact]
    public async Task For_update_inside_a_string_or_comment_does_not_block()
    {
        RecordServerQueries();
        var pane = await PaneAsync("SELECT 'for update' AS empno, 'x' AS ename, 'y' AS note FROM emp /* FOR UPDATE */");

        Assert.Null(pane.Model!.ServerSortBlockedReason!(0, false));
    }

    [Fact]
    public async Task Duplicate_inner_column_names_block_the_requery_for_ora_00918()
    {
        RecordServerQueries();
        var dup = new ResultColumn[] { new(0, "ID", "NUMBER", ColumnDataKind.Number), new(1, "ID", "NUMBER", ColumnDataKind.Number) };
        var pane = new SqlResultPaneViewModel { Statement = Query("SELECT a.id, b.id FROM a, b") };
        pane.SetResult(new TableResult(dup, [[1m, 2m]]));
        await pane.Model!.LoadFirstPageAsync();

        Assert.Contains("ORA-00918", pane.Model.ServerSortBlockedReason!(0, false), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsortable_column_kinds_and_non_query_results_are_blocked()
    {
        var pane = await PaneAsync();
        Assert.NotNull(pane.Model!.ServerSortBlockedReason!(0, false)); // 실행 연결 지점이 없으면 막힌다

        RecordServerQueries();
        Assert.Null(pane.Model.ServerSortBlockedReason!(0, false));
        Assert.Contains("정렬할 수 없습니다", pane.Model.ServerSortBlockedReason!(2, false), StringComparison.Ordinal); // CLOB

        var plsql = new SqlResultPaneViewModel { Statement = new SqlStatement("BEGIN OPEN :c FOR SELECT 1 FROM dual; END;", default, StatementKind.PlSql) };
        plsql.SetResult(Emp());
        Assert.NotNull(plsql.Model!.ServerSortBlockedReason!(0, false));
    }

    [Fact]
    public async Task Choosing_the_server_requery_in_the_dialog_replaces_the_result_with_a_sort_marker_and_never_nests()
    {
        var asked = RecordServerQueries();
        var pane = await PaneAsync();
        var first = pane.Model!;
        first.LocalSortMaxRowsOverride = 2; // 3행이면 문턱에 닿는다
        first.ConfirmLargeSort = (_, _) => Task.FromResult(LargeSortChoice.ServerReQuery);

        await first.ToggleSortAsync(0);

        Assert.Single(asked);
        Assert.Equal("1 NULLS LAST", asked[0].OrderBy);
        Assert.Equal("SELECT empno, ename, note FROM emp", asked[0].Query.Text);
        var second = pane.Model!;
        Assert.NotSame(first, second);
        Assert.Equal(0, second.SortColumn);
        Assert.False(second.SortDescending);
        Assert.False(second.IsSorting);

        // 같은 열을 다시 누르면 내림차순으로 다시 조회한다: 이번에도 원래 문장 기준.
        await second.LoadFirstPageAsync(); // 화면이 새 모델의 첫 페이지를 가져온다
        second.LocalSortMaxRowsOverride = 2;
        second.ConfirmLargeSort = (_, _) => Task.FromResult(LargeSortChoice.ServerReQuery);
        await second.ToggleSortAsync(0);
        Assert.Equal("1 DESC NULLS LAST", asked[1].OrderBy);
        Assert.Equal("SELECT empno, ename, note FROM emp", asked[1].Query.Text);

        // 내림차순에서 한 번 더 누르면 정렬 없이 다시 조회한다(서버가 정렬한 결과는 순서표를 지워도 원래 순서가 돌아오지 않는다).
        await pane.Model!.LoadFirstPageAsync();
        await pane.Model!.ToggleSortAsync(0);
        Assert.Equal(3, asked.Count);
        Assert.True(asked[2].IsEmpty);
        Assert.Equal(-1, pane.Model!.SortColumn);
    }

    [Fact]
    public async Task Cancelling_the_dialog_keeps_the_result_and_asks_the_server_nothing()
    {
        var asked = RecordServerQueries();
        var pane = await PaneAsync();
        var model = pane.Model!;
        model.LocalSortMaxRowsOverride = 2;
        model.ConfirmLargeSort = (_, _) => Task.FromResult(LargeSortChoice.Cancel);

        await model.ToggleSortAsync(0);

        Assert.Empty(asked);
        Assert.Same(model, pane.Model);
        Assert.False(model.IsSorted);
    }

    [Fact]
    public async Task A_failed_requery_leaves_the_current_result_and_reports_the_reason()
    {
        ResultGridCommands.Filter = (_, _) => throw new InvalidOperationException("ORA-00942: table or view does not exist");
        var pane = await PaneAsync();
        var model = pane.Model!;

        await model.ServerSort!(0, false, default);

        Assert.Same(model, pane.Model);
        Assert.Contains("ORA-00942", pane.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_filtered_result_keeps_its_condition_and_only_swaps_the_order()
    {
        var asked = RecordServerQueries();
        var pane = await PaneAsync();
        pane.WhereText = "empno > 1";
        await pane.ApplyFilterAsync();
        Assert.True(pane.IsFiltered);

        await pane.Model!.ServerSort!(1, true, default);

        Assert.Equal("empno > 1", asked[^1].Where);
        Assert.Equal("2 DESC NULLS LAST", asked[^1].OrderBy);
        Assert.Equal("SELECT empno, ename, note FROM emp", asked[^1].Query.Text);
        Assert.True(pane.IsFiltered);
    }
}
