using System.Diagnostics;
using System.Globalization;
using Oranda.App.Features.ResultGrid;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.ResultGrid;

/// <summary>GRID-09 시험: 고른 칸 집계(개수·합계·평균·최소·최대)와 「전체 행 수 세기」의 결과 영역 쪽(상태 줄·취소·명령).</summary>
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class SelectionAggregateTests : IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public SelectionAggregateTests()
    {
        // 다른 시험의 ExecutionCommands.Install 이 걸어 둔 실제 연결 지점을 쓰지 않게 비운다.
        ResultGridCommands.CountRows = null;
        ResultGridCommands.Active = null;
    }

    public void Dispose()
    {
        ResultGridCommands.CountRows = null;
        ResultGridCommands.Active = null;
    }

    /// <summary>열과 행을 그대로 돌려주는 메모리 결과(페이지 크기 단위로 나눈다). 가져온 페이지 수를 센다.</summary>
    private sealed class TableResult(ResultColumn[] columns, object?[][] rows, int pageSize = 500) : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns => columns;

        public int PageRequests { get; private set; }

        public bool IsComplete { get; private set; }

        public long? KnownRowCount => IsComplete ? rows.Length : null;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            PageRequests++;
            var first = pageIndex * pageSize;
            var count = Math.Clamp(rows.Length - first, 0, pageSize);
            var cols = new List<IReadOnlyList<object?>>();
            for (var c = 0; c < columns.Length; c++)
            {
                cols.Add([.. rows.Skip(first).Take(count).Select(r => r[c])]);
            }

            var last = first + count >= rows.Length;
            IsComplete |= last;
            return Task.FromResult(new ResultPage(pageIndex, first, count, cols) { IsLast = last });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ResultColumn[] Cols(params (string Name, ColumnDataKind Kind)[] cols) =>
        [.. cols.Select((c, i) => new ResultColumn(i, c.Name, c.Kind.ToString(), c.Kind))];

    private static readonly ResultColumn[] EmpCols =
        Cols(("EMPNO", ColumnDataKind.Number), ("ENAME", ColumnDataKind.Text), ("SAL", ColumnDataKind.Number), ("RATE", ColumnDataKind.BinaryFloat));

    private static async Task<ResultGridModel> ModelAsync(ResultColumn[] cols, object?[][] rows, int pageSize = 500)
    {
        var m = new ResultGridModel(new TableResult(cols, rows, pageSize), pageSize: pageSize);
        await m.LoadFirstPageAsync();
        return m;
    }

    private static void Select(ResultGridModel m, long r1, int c1, long r2, int c2)
    {
        m.SelectCell(new GridCell(r1, c1));
        m.ExtendTo(new GridCell(r2, c2));
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

    // ---- 집계 계산 ------------------------------------------------------------------------

    [Fact]
    public async Task Aggregate_counts_non_null_cells_and_sums_only_numeric_columns()
    {
        await using var m = await ModelAsync(EmpCols,
        [
            [7369m, "SMITH", 800m, null],
            [7499m, "ALLEN", null, null],
            [7521m, null, 1250.5m, null],
        ]);
        Select(m, 0, 1, 2, 2); // ENAME·SAL 세 줄: 글자 2칸(+NULL 1), 숫자 2칸(+NULL 1)

        var a = SelectionAggregate.Compute(m)!;

        Assert.Equal(4, a.Count);
        Assert.Equal(2, a.NumericCount);
        Assert.Equal(2050.5m, a.Sum);
        Assert.Equal(1025.25m, a.Average); // 평균은 숫자 칸 수로 나눈다
        Assert.Equal(800m, a.Min);
        Assert.Equal(1250.5m, a.Max);
        Assert.False(a.IsPartial);
        Assert.Equal("개수 4 · 합계 2,050.5 · 평균 1,025.25 · 최소 800 · 최대 1,250.5", a.ToText(Inv));
    }

    [Fact]
    public async Task Text_only_selection_shows_count_only_and_a_single_cell_shows_nothing()
    {
        await using var m = await ModelAsync(EmpCols, [[1m, "A", 1m, null], [2m, "B", 2m, null]]);
        Select(m, 0, 1, 1, 1);
        Assert.Equal("개수 2", SelectionAggregate.Compute(m)!.ToText(Inv));

        m.SelectCell(new GridCell(0, 0));
        Assert.Null(SelectionAggregate.ScopeOf(m));
        Assert.Null(SelectionAggregate.Compute(m));
    }

    [Fact]
    public async Task Raw_number_strings_and_binary_doubles_are_summed()
    {
        // NUMBER 는 decimal 이거나 드라이버 원시 문자열, BINARY_DOUBLE 은 double 로 온다. 섞이면 double 로 센다.
        await using var m = await ModelAsync(EmpCols,
        [
            [1m, "A", "2.5", 0.5d],
            [2m, "B", "abc", double.NaN],
        ]);
        Select(m, 0, 2, 1, 3);

        var a = SelectionAggregate.Compute(m)!;

        Assert.Equal(4, a.Count); // 숫자로 못 읽는 「abc」와 NaN 도 개수에는 든다
        Assert.Equal(2, a.NumericCount);
        Assert.Equal(3d, a.Sum);
        Assert.Equal(1.5d, a.Average);
        Assert.Equal(0.5d, a.Min);
        Assert.Equal(2.5d, a.Max);
    }

    [Fact]
    public async Task Decimal_overflow_falls_back_to_double()
    {
        await using var m = await ModelAsync(Cols(("N", ColumnDataKind.Number)), [[decimal.MaxValue], [decimal.MaxValue], ["1E+40"]]);
        Select(m, 0, 0, 2, 0);

        var a = SelectionAggregate.Compute(m)!;

        Assert.Equal(3, a.NumericCount);
        var sum = Assert.IsType<double>(a.Sum);
        Assert.True(Math.Abs(sum - (1e40 + 2 * (double)decimal.MaxValue)) < 1e30);
        Assert.Equal(1e40, Assert.IsType<double>(a.Max));
        Assert.Contains("E+40", a.ToText(Inv), StringComparison.Ordinal); // 아주 큰 값은 지수 표기
    }

    [Fact]
    public async Task Big_selection_is_capped_reads_only_fetched_rows_and_never_fetches()
    {
        var rows = Enumerable.Range(0, 2000).Select(i => new object?[] { (decimal)i, "n" + i, 1m, null }).ToArray();
        var source = new TableResult(EmpCols, rows, pageSize: 500);
        await using var m = new ResultGridModel(source, pageSize: 500);
        await m.LoadFirstPageAsync();
        Assert.Equal(500, m.RowCount); // 아직 첫 페이지만
        var requests = source.PageRequests;

        m.SelectAll();
        var all = SelectionAggregate.Compute(m)!;
        Assert.Equal(1500, all.Count); // 가져온 500행 × NULL 아닌 3열
        Assert.Equal(1000, all.NumericCount);
        Assert.False(all.IsPartial);

        var capped = SelectionAggregate.Compute(m, maxCells: 100)!;
        Assert.True(capped.IsPartial);
        Assert.True(capped.Count <= 100);
        Assert.EndsWith(" (일부)", capped.ToText(Inv), StringComparison.Ordinal);

        Assert.Equal(requests, source.PageRequests); // 집계는 가져오기를 부르지 않는다
    }

    [Fact]
    public async Task Sorted_and_hidden_columns_follow_the_visible_grid()
    {
        await using var m = await ModelAsync(EmpCols, [[3m, "C", 30m, null], [1m, "A", 10m, null], [2m, "B", 20m, null]]);
        m.HideColumn(1); // ENAME 을 숨기면 보이는 열 0·1 은 EMPNO·SAL
        await m.ToggleSortAsync(0);
        Select(m, 0, 1, 1, 1); // 정렬 뒤 첫 두 줄의 SAL = 10, 20

        var a = SelectionAggregate.Compute(m)!;

        Assert.Equal(30m, a.Sum);
        Assert.Equal(10m, a.Min);
        Assert.Equal(20m, a.Max);
    }

    // ---- 결과 영역: 상태 줄 집계 ---------------------------------------------------------

    [Fact]
    public async Task Pane_shows_selection_summary_and_copies_it()
    {
        var vm = new SqlResultPaneViewModel();
        vm.SetResult(new TableResult(EmpCols, [[1m, "A", 100m, null], [2m, "B", 200m, null]]));
        var m = vm.Model!;
        await m.LoadFirstPageAsync();
        string? clip = null;
        m.ClipboardWriter = t =>
        {
            clip = t;
            return Task.CompletedTask;
        };

        Select(m, 0, 2, 1, 2);
        await WaitAsync(() => vm.HasSelectionSummary);
        Assert.StartsWith("개수 2 · 합계 ", vm.SelectionSummary, StringComparison.Ordinal);

        await vm.CopySelectionSummaryAsync();
        Assert.Equal(vm.SelectionSummary, clip);

        m.SelectCell(new GridCell(0, 0));
        await WaitAsync(() => !vm.HasSelectionSummary);
        vm.Clear();
    }

    // ---- 결과 영역: 전체 행 수 세기 -------------------------------------------------------

    private static readonly SqlStatement Query = new("SELECT * FROM emp WHERE deptno = :d", default, StatementKind.Query)
    {
        Verb = "SELECT",
        BindNames = ["d"],
    };

    private static async Task<SqlResultPaneViewModel> PaneAsync(SqlStatement? statement = null, int rows = 1200)
    {
        var data = Enumerable.Range(0, rows).Select(i => new object?[] { (decimal)i, "n", 1m, null }).ToArray();
        var vm = new SqlResultPaneViewModel { Statement = statement ?? Query };
        vm.SetResult(new TableResult(EmpCols, data));
        await vm.Model!.LoadFirstPageAsync();
        await WaitAsync(() => vm.StatusText.Length > 0);
        return vm;
    }

    [Fact]
    public async Task Count_rows_shows_fetched_and_total_rows_in_the_status_line()
    {
        SqlStatement? asked = null;
        ResultGridCommands.CountRows = (stmt, _) =>
        {
            asked = stmt;
            return Task.FromResult<long?>(12345);
        };
        var vm = await PaneAsync();
        Assert.True(vm.CanCountRows);

        await vm.CountAllRowsAsync();

        Assert.Same(Query, asked);
        Assert.Equal(12345, vm.CountedRowCount);
        Assert.False(vm.IsCountingRows);
        var c = CultureInfo.CurrentCulture;
        Assert.Equal($"가져온 {500.ToString("N0", c)}행 / 전체 {12345.ToString("N0", c)}행", vm.StatusText);
        vm.Clear();
    }

    [Fact]
    public async Task Count_rows_failure_goes_to_the_status_line_and_never_throws()
    {
        ResultGridCommands.CountRows = (_, _) => throw new InvalidOperationException("ORA-00942: table or view does not exist");
        var vm = await PaneAsync();

        await vm.CountAllRowsAsync();

        Assert.Null(vm.CountedRowCount);
        Assert.Contains("전체 행 수를 세지 못했습니다: ORA-00942", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("(더 있음)", vm.StatusText, StringComparison.Ordinal);
        vm.Clear();
    }

    [Fact]
    public async Task Count_rows_can_be_cancelled_from_the_status_line()
    {
        var started = new TaskCompletionSource();
        ResultGridCommands.CountRows = async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        };
        var vm = await PaneAsync();

        var run = vm.CountAllRowsAsync();
        await started.Task;
        Assert.True(vm.IsCountingRows);
        Assert.Contains("전체 행 수 세는 중", vm.StatusText, StringComparison.Ordinal);

        vm.CancelCountRows();
        await run;

        Assert.False(vm.IsCountingRows);
        Assert.Contains("취소했습니다", vm.StatusText, StringComparison.Ordinal);
        vm.Clear();
    }

    [Fact]
    public async Task A_new_result_while_counting_drops_the_old_count()
    {
        var gate = new TaskCompletionSource<long?>();
        CancellationToken seen = default;
        ResultGridCommands.CountRows = (_, ct) =>
        {
            seen = ct;
            return gate.Task;
        };
        var vm = await PaneAsync();

        var run = vm.CountAllRowsAsync();
        vm.SetResult(new TableResult(EmpCols, [[1m, "A", 1m, null]]));
        Assert.True(seen.IsCancellationRequested); // 결과를 바꾸면 세던 것을 취소한다
        gate.SetResult(999);
        await run;

        Assert.Null(vm.CountedRowCount);
        Assert.False(vm.IsCountingRows);
        vm.Clear();
    }

    [Fact]
    public async Task Count_rows_is_offered_only_for_query_results()
    {
        var called = false;
        ResultGridCommands.CountRows = (_, _) =>
        {
            called = true;
            return Task.FromResult<long?>(1);
        };
        var plsql = await PaneAsync(new SqlStatement("BEGIN OPEN :c FOR SELECT * FROM emp; END;", default, StatementKind.PlSql));

        Assert.False(plsql.CanCountRows); // REF CURSOR 결과는 감쌀 질의가 없다
        await plsql.CountAllRowsAsync();
        Assert.False(called);
        plsql.Clear();

        var none = new SqlResultPaneViewModel();
        Assert.False(none.CanCountRows);
    }

    [Fact]
    public async Task Count_rows_without_an_engine_says_so()
    {
        var vm = await PaneAsync();

        await vm.CountAllRowsAsync();

        Assert.Contains("셀 수 없습니다", vm.StatusText, StringComparison.Ordinal);
        vm.Clear();
    }

    [Fact]
    public async Task Count_rows_command_reaches_the_pane_of_the_active_grid()
    {
        var calls = 0;
        ResultGridCommands.CountRows = (_, _) =>
        {
            calls++;
            return Task.FromResult<long?>(4321);
        };
        var vm = await PaneAsync();
        ResultGridCommands.Active = vm.Model;

        await new ResultGridCommandHandler().ExecuteAsync(new CommandContext("oranda.grid.countRows", new AllKeys()), CancellationToken.None);
        await WaitAsync(() => vm.CountedRowCount is not null);

        Assert.Equal(1, calls);
        Assert.Equal(4321, vm.CountedRowCount);
        vm.Clear();
    }

    [Fact]
    public void Count_rows_command_is_declared_for_the_grid_context_menu()
    {
        var a = typeof(ResultGridCommandHandler).GetCustomAttributes(typeof(OrandaCommandAttribute), false)
            .Cast<OrandaCommandAttribute>().Single(x => x.Id == "oranda.grid.countRows");
        Assert.Equal(CommandCategory.Result, a.Category);
        Assert.Equal("gridFocus", a.When);
        Assert.Equal("gridQueryResult && hasSession && !sessionBusy", a.Enablement);
        Assert.Equal("context:grid/rows@10", Assert.Single(a.Placements!));
    }

    private sealed class AllKeys : IContextKeys
    {
        public bool Evaluate(string? expression) => true;
    }
}
