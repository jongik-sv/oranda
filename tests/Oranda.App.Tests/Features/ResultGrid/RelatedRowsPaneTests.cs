using System.Diagnostics;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ResultGrid;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.ResultGrid;

/// <summary>
/// GRID-11 외래키 따라가기의 결과 영역 쪽: 명령 → 활성 행 값 요청 → 새 결과 탭(제목·문장), 새 탭의 거르기·전체 행 수 세기가 그 행 값 바인드를 쓰는지,
/// 실패는 상태 줄에, 늦게 온 결과 버리기, 편집 안내, 명령 선언.
/// </summary>
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class RelatedRowsPaneTests : IDisposable
{
    private static readonly SqlStatement EmpQuery = new("SELECT empno, ename, deptno FROM emp", default, StatementKind.Query) { Verb = "SELECT" };

    private static readonly ResultColumn[] EmpCols =
    [
        new(0, "EMPNO", "NUMBER", ColumnDataKind.Number),
        new(1, "ENAME", "VARCHAR2", ColumnDataKind.Text),
        new(2, "DEPTNO", "NUMBER", ColumnDataKind.Number),
    ];

    private static readonly ResultColumn[] DeptCols =
    [
        new(0, "DEPTNO", "NUMBER", ColumnDataKind.Number),
        new(1, "DNAME", "VARCHAR2", ColumnDataKind.Text),
    ];

    private static readonly SqlStatement DeptQuery = new("SELECT * FROM \"SCOTT\".\"DEPT\" WHERE \"DEPTNO\" = :v1", default, StatementKind.Query)
    {
        Verb = "SELECT",
        BindNames = ["v1"],
    };

    private static readonly Dictionary<string, BindValue> DeptBinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["v1"] = new BindValue(BindDataType.Number, 20m),
    };

    public RelatedRowsPaneTests() => Reset();

    public void Dispose() => Reset();

    private static void Reset()
    {
        // 다른 시험의 ExecutionCommands.Install 이 걸어 둔 실제 연결 지점을 쓰지 않게 비운다.
        ResultGridCommands.OpenRelatedRows = null;
        ResultGridCommands.Filter = null;
        ResultGridCommands.CountFiltered = null;
        ResultGridCommands.CountRows = null;
        ResultGridCommands.Active = null;
    }

    /// <summary>열과 행을 그대로 돌려주는 메모리 결과.</summary>
    private sealed class TableResult(ResultColumn[] columns, object?[][] rows) : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns => columns;

        public bool IsComplete => true;

        public long? KnownRowCount => rows.Length;

        public bool Disposed { get; private set; }

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            var cols = new List<IReadOnlyList<object?>>();
            for (var c = 0; c < columns.Length; c++)
            {
                cols.Add(pageIndex == 0 ? [.. rows.Select(r => r[c])] : []);
            }

            return Task.FromResult(new ResultPage(pageIndex, 0, pageIndex == 0 ? rows.Length : 0, cols) { IsLast = true });
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static TableResult Emp() => new(EmpCols, [[7369m, "SMITH", 20m], [7499m, "ALLEN", 30m]]);

    private static TableResult DeptRows() => new(DeptCols, [[20m, "RESEARCH"]]);

    /// <summary>결과 탭 묶음에 결과 1개를 열고 첫 페이지를 읽은 뒤 (행, 보이는 열) 칸을 고른다.</summary>
    private static async Task<(ResultTabsViewModel Tabs, SqlResultPaneViewModel Pane)> TabsAsync(int row = 0, int col = 2)
    {
        var tabs = new ResultTabsViewModel();
        var pane = tabs.AddQuery(Emp(), "결과 1", EmpQuery).Pane!;
        await pane.Model!.LoadFirstPageAsync();
        await WaitAsync(() => pane.StatusText.Length > 0);
        pane.Model.SelectCell(new GridCell(row, col));
        return (tabs, pane);
    }

    private static async Task WaitAsync(Func<bool> cond)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "시간 초과");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Follow_command_opens_the_parent_rows_in_a_new_tab_with_the_active_row_values()
    {
        var asked = new List<RelatedRowsRequest>();
        ResultGridCommands.OpenRelatedRows = (request, _) =>
        {
            asked.Add(request);
            return Task.FromResult<RelatedRowsResult?>(new RelatedRowsResult(DeptRows(), "부모: DEPT", DeptQuery, DeptBinds));
        };
        var (tabs, pane) = await TabsAsync(row: 1, col: 2); // ALLEN 의 DEPTNO
        ResultGridCommands.Active = pane.Model;

        await new ResultGridCommandHandler().ExecuteAsync(new CommandContext("oranda.grid.followForeignKey", new AllKeys()), CancellationToken.None);
        await WaitAsync(() => tabs.Tabs.Count == 2);

        var request = Assert.Single(asked);
        Assert.Equal(RelatedRowsKind.Parent, request.Kind);
        Assert.Same(EmpQuery, request.Query);
        Assert.Equal(2, request.Ordinal);
        Assert.Equal(new object?[] { 7499m, "ALLEN", 30m }, request.Row);
        Assert.Equal(EmpCols, request.Columns);

        var opened = tabs.Tabs[1];
        Assert.Same(opened, tabs.Selected);
        Assert.Equal("부모: DEPT", opened.Title);
        Assert.Same(DeptQuery, opened.Statement);
        Assert.Same(DeptQuery, opened.Pane!.Statement); // 거르기 줄·전체 행 수 세기가 이 문장 위에서 돈다
        Assert.True(opened.Pane.HasStatementBinds);
        Assert.Same(EmpQuery, tabs.Tabs[0].Pane!.Statement); // 원래 결과는 그대로
        Assert.DoesNotContain("여는 중", pane.StatusText, StringComparison.Ordinal);
        tabs.Clear();
    }

    [Fact]
    public async Task Referencing_rows_command_asks_for_children()
    {
        var asked = new List<RelatedRowsRequest>();
        ResultGridCommands.OpenRelatedRows = (request, _) =>
        {
            asked.Add(request);
            return Task.FromResult<RelatedRowsResult?>(null); // 외래키 고르기를 취소했다
        };
        var (tabs, pane) = await TabsAsync(col: 0);
        ResultGridCommands.Active = pane.Model;

        await new ResultGridCommandHandler().ExecuteAsync(new CommandContext("oranda.grid.showReferencingRows", new AllKeys()), CancellationToken.None);
        await WaitAsync(() => asked.Count == 1 && !pane.IsOpeningRelatedRows);

        Assert.Equal(RelatedRowsKind.Children, asked[0].Kind);
        Assert.Equal(0, asked[0].Ordinal);
        Assert.Single(tabs.Tabs);
        Assert.DoesNotContain("여는 중", pane.StatusText, StringComparison.Ordinal);
        tabs.Clear();
    }

    [Fact]
    public async Task Opened_tab_counts_and_filters_with_its_own_bind_values()
    {
        var (tabs, _) = await TabsAsync();
        var opened = tabs.AddRelated(new RelatedRowsResult(DeptRows(), "부모: DEPT", DeptQuery, DeptBinds));
        var pane = opened.Pane!;
        await pane.Model!.LoadFirstPageAsync();

        // 전체 행 수 세기: 「바인드 변수」 목록(CountRows)이 아니라 이 결과 영역이 가진 값으로 센다.
        ResultGridCommands.CountRows = (_, _) => throw new InvalidOperationException("목록에는 v1 이 없다");
        var counted = new List<ResultFilterRequest>();
        ResultGridCommands.CountFiltered = (request, _) =>
        {
            counted.Add(request);
            return Task.FromResult<long?>(1);
        };
        await pane.CountAllRowsAsync();

        var count = Assert.Single(counted);
        Assert.Same(DeptQuery, count.Query);
        Assert.True(count.IsEmpty);
        Assert.Equal(DeptBinds["v1"], count.Binds["v1"]);
        Assert.Equal(1, pane.CountedRowCount);

        // 거르기와 거르기 지우기도 같은 값을 함께 넘긴다.
        var filtered = new List<ResultFilterRequest>();
        ResultGridCommands.Filter = (request, _) =>
        {
            filtered.Add(request);
            return Task.FromResult<IResultSet>(DeptRows());
        };
        pane.WhereText = "DNAME LIKE 'R%'";
        await pane.ApplyFilterAsync();
        await pane.ClearFilterAsync();

        Assert.Equal(2, filtered.Count);
        Assert.All(filtered, r => Assert.Equal(DeptBinds["v1"], r.Binds["v1"]));
        Assert.All(filtered, r => Assert.Same(DeptQuery, r.Query));
        Assert.True(filtered[1].IsEmpty);
        tabs.Clear();
    }

    [Fact]
    public async Task Opened_tab_does_not_offer_rerun_for_edit()
    {
        var (tabs, _) = await TabsAsync();
        var pane = tabs.AddRelated(new RelatedRowsResult(DeptRows(), "부모: DEPT", DeptQuery, DeptBinds)).Pane!;

        pane.ShowEditHint();

        Assert.Contains("외래키로 연 결과", pane.EditHint, StringComparison.Ordinal);
        Assert.False(pane.OffersRerunForEdit); // 실행 화면은 「바인드 변수」 목록에서 :v1 을 찾다 결과를 지운다
        Assert.False(pane.HasEditHintAction);
        tabs.Clear();
    }

    [Fact]
    public async Task Failure_is_shown_in_the_status_line_and_no_tab_opens()
    {
        ResultGridCommands.OpenRelatedRows = (_, _) => throw new InvalidOperationException("외래키 FK_EMP_DEPT 값이 NULL 이라 따라갈 부모 행이 없습니다.");
        var (tabs, pane) = await TabsAsync();

        await pane.OpenRelatedRowsAsync(RelatedRowsKind.Parent);

        Assert.Contains("부모 행을 열지 못했습니다: 외래키 FK_EMP_DEPT 값이 NULL", pane.StatusText, StringComparison.Ordinal);
        Assert.Single(tabs.Tabs);

        ResultGridCommands.OpenRelatedRows = (_, _) => throw new InvalidOperationException("SCOTT.EMP 을(를) 참조하는 외래키가 없습니다.");
        await pane.OpenRelatedRowsAsync(RelatedRowsKind.Children);

        Assert.Contains("참조하는 행을 열지 못했습니다: SCOTT.EMP", pane.StatusText, StringComparison.Ordinal);
        Assert.Single(tabs.Tabs);
        tabs.Clear();
    }

    [Fact]
    public async Task Pane_without_a_tab_host_or_an_engine_says_so()
    {
        var alone = new SqlResultPaneViewModel { Statement = EmpQuery };
        alone.SetResult(Emp());
        await alone.Model!.LoadFirstPageAsync();
        alone.Model.SelectCell(new GridCell(0, 2));
        ResultGridCommands.OpenRelatedRows = (_, _) => throw new InvalidOperationException("불리면 안 된다");

        await alone.OpenRelatedRowsAsync(RelatedRowsKind.Parent);
        Assert.Contains("새 결과 탭을 열 수 없습니다", alone.StatusText, StringComparison.Ordinal);
        alone.Clear();

        ResultGridCommands.OpenRelatedRows = null;
        var (tabs, pane) = await TabsAsync();
        await pane.OpenRelatedRowsAsync(RelatedRowsKind.Parent);
        Assert.Contains("실행 엔진이 연결되지 않았습니다", pane.StatusText, StringComparison.Ordinal);
        tabs.Clear();
    }

    [Fact]
    public async Task Late_result_after_the_source_tab_closed_is_disposed()
    {
        var gate = new TaskCompletionSource<RelatedRowsResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ResultGridCommands.OpenRelatedRows = (_, _) => gate.Task;
        var (tabs, pane) = await TabsAsync();

        var running = pane.OpenRelatedRowsAsync(RelatedRowsKind.Parent);
        Assert.Contains("부모 행 여는 중", pane.StatusText, StringComparison.Ordinal);
        tabs.Close(tabs.Tabs[0]); // 다음 실행이나 사용자가 원래 탭을 닫았다
        var late = DeptRows();
        gate.SetResult(new RelatedRowsResult(late, "부모: DEPT", DeptQuery, DeptBinds));
        await running;

        Assert.True(late.Disposed);
        Assert.Empty(tabs.Tabs);
    }

    [Fact]
    public void Commands_are_declared_for_the_grid_navigate_group()
    {
        var attrs = typeof(ResultGridCommandHandler).GetCustomAttributes(typeof(OrandaCommandAttribute), false).Cast<OrandaCommandAttribute>().ToList();
        var follow = attrs.Single(x => x.Id == "oranda.grid.followForeignKey");
        var children = attrs.Single(x => x.Id == "oranda.grid.showReferencingRows");

        foreach (var a in new[] { follow, children })
        {
            Assert.Equal(CommandCategory.Result, a.Category);
            Assert.Equal("gridFocus", a.When);
            Assert.Equal("gridQueryResult && gridHasSelection && hasSession && !sessionBusy", a.Enablement);
        }

        Assert.Equal("context:grid/navigate@10", Assert.Single(follow.Placements!));
        Assert.Equal("context:grid/navigate@20", Assert.Single(children.Placements!));
    }

    private sealed class AllKeys : IContextKeys
    {
        public bool Evaluate(string? expression) => true;
    }
}
