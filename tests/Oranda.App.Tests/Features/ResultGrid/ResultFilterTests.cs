using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ResultGrid;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.ResultGrid;

/// <summary>
/// GRID-10 결과 거르기 줄(조건 글, 셀 값 조건은 바인드로만, 결과 바꾸기·지우기·최근 조건·세기)과
/// GRID-06 결과 탭 고정(다음 실행이 지우지 않음, 번호 피하기, 명령 경로).
/// </summary>
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class ResultFilterTests : IDisposable
{
    private static readonly SqlStatement Query = new("SELECT empno, ename, sal FROM emp WHERE deptno = :d", default, StatementKind.Query)
    {
        Verb = "SELECT",
        BindNames = ["d"],
    };

    private static readonly ResultColumn[] EmpCols =
    [
        new(0, "EMPNO", "NUMBER", ColumnDataKind.Number),
        new(1, "ENAME", "VARCHAR2", ColumnDataKind.Text),
        new(2, "SAL", "NUMBER", ColumnDataKind.Number),
    ];

    public ResultFilterTests() => Reset();

    public void Dispose() => Reset();

    private static void Reset()
    {
        // 다른 시험의 ExecutionCommands.Install 이 걸어 둔 실제 연결 지점을 쓰지 않게 비운다.
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

    private static TableResult Emp() => new(EmpCols, [[7369m, "SMITH", 800m], [7499m, "ALLEN", 1600m], [7839m, null, 5000m]]);

    private static async Task<SqlResultPaneViewModel> PaneAsync(SqlStatement? statement = null)
    {
        var vm = new SqlResultPaneViewModel { Statement = statement ?? Query };
        vm.SetResult(Emp());
        await vm.Model!.LoadFirstPageAsync();
        await WaitAsync(() => vm.StatusText.Length > 0);
        return vm;
    }

    /// <summary>받은 요청을 남기고 새 결과를 돌려주는 거르기 연결 지점.</summary>
    private static List<ResultFilterRequest> RecordFilters(Func<IResultSet>? result = null)
    {
        var asked = new List<ResultFilterRequest>();
        ResultGridCommands.Filter = (request, _) =>
        {
            asked.Add(request);
            return Task.FromResult(result?.Invoke() ?? Emp());
        };
        return asked;
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

    // ---- 셀 값 조건 ------------------------------------------------------------------------

    [Fact]
    public void Cell_condition_binds_the_value_and_never_inlines_it()
    {
        Assert.True(ResultFilter.TryCellCondition(EmpCols[1], "O'Brien", CellFilterKind.Equal, "oranda_f1", out var eq, out var bind, out _));
        Assert.Equal("ENAME = :oranda_f1", eq);
        Assert.Equal(new BindValue(BindDataType.Varchar2, "O'Brien"), bind);

        Assert.True(ResultFilter.TryCellCondition(EmpCols[2], 800m, CellFilterKind.NotEqual, "oranda_f2", out var ne, out bind, out _));
        Assert.Equal("SAL <> :oranda_f2", ne);
        Assert.Equal(new BindValue(BindDataType.Number, 800m), bind);
    }

    [Fact]
    public void Null_cell_values_become_is_null_tests_without_a_bind()
    {
        Assert.True(ResultFilter.TryCellCondition(EmpCols[1], null, CellFilterKind.Equal, "oranda_f1", out var eq, out var bind, out _));
        Assert.Equal("ENAME IS NULL", eq); // 「= NULL」은 아무 행도 돌려주지 않는다
        Assert.Null(bind);

        Assert.True(ResultFilter.TryCellCondition(EmpCols[1], null, CellFilterKind.NotEqual, "oranda_f1", out var ne, out bind, out _));
        Assert.Equal("ENAME IS NOT NULL", ne);
        Assert.Null(bind);

        Assert.True(ResultFilter.TryCellCondition(EmpCols[1], "SMITH", CellFilterKind.IsNull, "oranda_f1", out var isNull, out bind, out _));
        Assert.Equal("ENAME IS NULL", isNull);
        Assert.Null(bind);
    }

    [Fact]
    public void Column_names_are_quoted_as_oracle_identifiers_when_needed()
    {
        Assert.Equal("EMP_NO#1", ResultFilter.QuoteColumn("EMP_NO#1"));
        Assert.Equal("\"ename\"", ResultFilter.QuoteColumn("ename"));
        Assert.Equal("\"DATE\"", ResultFilter.QuoteColumn("DATE")); // 예약어 별칭
        Assert.Equal("\"SAL*2\"", ResultFilter.QuoteColumn("SAL*2")); // 식 열 이름도 인라인 뷰의 열 이름이다
        Assert.Equal("\"급여\"", ResultFilter.QuoteColumn("급여"));
        Assert.Null(ResultFilter.QuoteColumn("'A\"B'"));

        var odd = new ResultColumn(0, "'A\"B'", "CHAR", ColumnDataKind.Text);
        Assert.False(ResultFilter.TryCellCondition(odd, "x", CellFilterKind.Equal, "oranda_f1", out _, out _, out var error));
        Assert.Contains("쓸 수 없습니다", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_types_follow_the_column_kind_and_unsupported_kinds_allow_only_null_tests()
    {
        static BindValue? Bind(string type, ColumnDataKind kind, object value) => ResultFilter.ToBind(new ResultColumn(0, "C", type, kind), value);
        var day = new DateTime(2026, 10, 20, 9, 30, 0, DateTimeKind.Unspecified);

        Assert.Equal(new BindValue(BindDataType.Number, 12.5m), Bind("NUMBER", ColumnDataKind.Number, "12.5"));
        Assert.Equal(new BindValue(BindDataType.Varchar2, "1234567890123456789012345678901234567890"),
            Bind("NUMBER", ColumnDataKind.Number, "1234567890123456789012345678901234567890")); // decimal 범위 밖: 서버가 바꿔 비교
        Assert.Equal(new BindValue(BindDataType.Date, day), Bind("DATE", ColumnDataKind.Date, day));
        Assert.Equal(new BindValue(BindDataType.Timestamp, day), Bind("TIMESTAMP", ColumnDataKind.Timestamp, day));
        Assert.Equal(BindDataType.TimestampTz, Bind("TIMESTAMP WITH TIME ZONE", ColumnDataKind.TimestampTz, new DateTimeOffset(day, TimeSpan.FromHours(9)))?.Type);
        Assert.Equal(new BindValue(BindDataType.NVarchar2, "가"), Bind("NVARCHAR2", ColumnDataKind.Text, "가"));
        Assert.Equal(new BindValue(BindDataType.Varchar2, "AAAR3sAAEAAAACXAAA"), Bind("ROWID", ColumnDataKind.RowId, "AAAR3sAAEAAAACXAAA"));
        Assert.Equal(new BindValue(BindDataType.BinaryDouble, 1.5d), Bind("BINARY_FLOAT", ColumnDataKind.BinaryFloat, 1.5f));
        Assert.Equal(new BindValue(BindDataType.Boolean, true), Bind("BOOLEAN", ColumnDataKind.Boolean, true));
        Assert.Null(Bind("LONG", ColumnDataKind.Text, "long text"));
        Assert.Null(Bind("CLOB", ColumnDataKind.Clob, new LobValue(LobKind.Clob, 10)));
        Assert.Null(Bind("INTERVAL DAY TO SECOND", ColumnDataKind.IntervalDaySecond, TimeSpan.FromHours(1)));

        var clob = new ResultColumn(0, "NOTE", "CLOB", ColumnDataKind.Clob);
        Assert.False(ResultFilter.TryCellCondition(clob, new LobValue(LobKind.Clob, 10), CellFilterKind.Equal, "oranda_f1", out _, out _, out var error));
        Assert.Contains("NULL 인 행만", error, StringComparison.Ordinal);
        Assert.True(ResultFilter.TryCellCondition(clob, new LobValue(LobKind.Clob, 10), CellFilterKind.IsNull, "oranda_f1", out var isNull, out _, out _));
        Assert.Equal("NOTE IS NULL", isNull);
    }

    [Fact]
    public void Conditions_are_joined_with_and_keeping_or_precedence_and_bind_names_skip_taken_ones()
    {
        Assert.Equal("B = 2", ResultFilter.Combine("  ", "B = 2"));
        Assert.Equal("A = 1 AND B = 2", ResultFilter.Combine("A = 1", "B = 2"));
        Assert.Equal("(A = 1 or C = 3) AND B = 2", ResultFilter.Combine("A = 1 or C = 3", "B = 2"));
        Assert.Equal("ORDER_NO = 1 AND COLOR = 2", ResultFilter.Combine("ORDER_NO = 1", "COLOR = 2")); // 낱말 OR 만 본다

        var counter = 0;
        Assert.Equal("oranda_f2", ResultFilter.NextBindName(["d", "ORANDA_F1"], ref counter));
        Assert.Equal("oranda_f3", ResultFilter.NextBindName(["d"], ref counter));
    }

    // ---- 결과 영역: 적용·지우기·실패 ---------------------------------------------------------------

    [Fact]
    public async Task Applying_reruns_the_original_query_in_the_same_pane_and_keeps_the_bar_text()
    {
        var asked = RecordFilters();
        var vm = await PaneAsync();
        var old = vm.Model;
        vm.ToggleFilterBar();
        vm.WhereText = "  SAL > 1000 ";
        vm.OrderByText = "SAL DESC";

        await vm.ApplyFilterAsync();

        var request = Assert.Single(asked);
        Assert.Same(Query, request.Query); // 감싸는 것은 늘 결과를 만든 원래 질의
        Assert.Equal("SAL > 1000", request.Where);
        Assert.Equal("SAL DESC", request.OrderBy);
        Assert.NotSame(old, vm.Model);
        Assert.Same(Query, vm.Statement);
        Assert.True(vm.IsFiltered);
        Assert.True(vm.IsFilterBarVisible);
        Assert.Equal("  SAL > 1000 ", vm.WhereText);
        Assert.Equal(["SAL > 1000"], vm.FilterHistory);
        Assert.Contains("거른 결과", vm.StatusText, StringComparison.Ordinal);
        vm.Clear();
    }

    [Fact]
    public async Task A_failed_filter_keeps_the_current_result_and_says_why_in_the_status_line()
    {
        ResultGridCommands.Filter = (_, _) => throw new InvalidOperationException("ORA-00904: \"SALX\": invalid identifier");
        var vm = await PaneAsync();
        var old = vm.Model;
        vm.WhereText = "SALX > 1";

        await vm.ApplyFilterAsync();

        Assert.Same(old, vm.Model);
        Assert.False(vm.IsFiltered);
        Assert.False(vm.IsFiltering);
        Assert.Contains("거르지 못했습니다: ORA-00904", vm.StatusText, StringComparison.Ordinal);
        Assert.Empty(vm.FilterHistory);
        vm.Clear();
    }

    [Fact]
    public async Task Clearing_reruns_the_original_query_only_when_a_filter_is_active()
    {
        var asked = RecordFilters();
        var vm = await PaneAsync();

        await vm.ClearFilterAsync(); // 거른 적이 없으면 다시 실행할 까닭이 없다
        Assert.Empty(asked);

        vm.WhereText = "SAL > 1000";
        await vm.ApplyFilterAsync();
        await vm.ClearFilterAsync();

        Assert.Equal(2, asked.Count);
        Assert.True(asked[1].IsEmpty);
        Assert.False(vm.IsFiltered);
        Assert.Equal(string.Empty, vm.WhereText);
        Assert.DoesNotContain("거른 결과", vm.StatusText, StringComparison.Ordinal);
        vm.Clear();
    }

    [Fact]
    public async Task Cell_filters_append_bound_conditions_to_the_where_box_and_apply()
    {
        var asked = RecordFilters();
        var vm = await PaneAsync();
        vm.Model!.SelectCell(new GridCell(1, 1)); // ALLEN

        await vm.FilterByCellAsync(CellFilterKind.Equal);

        Assert.True(vm.IsFilterBarVisible);
        Assert.Equal("ENAME = :oranda_f1", vm.WhereText);
        Assert.Equal("ALLEN", asked[0].Binds["oranda_f1"].Value);

        vm.Model!.SelectCell(new GridCell(0, 2));
        await vm.FilterByCellAsync(CellFilterKind.NotEqual); // 새 결과의 칸 값을 아직 못 가져왔으면 거르지 않고 그렇다고 말한다
        Assert.Single(asked);
        Assert.Contains("아직 가져오지 않았습니다", vm.StatusText, StringComparison.Ordinal);

        await vm.Model!.LoadFirstPageAsync(); // 화면에서는 그리드가 새 결과의 첫 페이지를 가져온다
        vm.Model!.SelectCell(new GridCell(0, 2)); // SAL 800
        await vm.FilterByCellAsync(CellFilterKind.NotEqual);

        Assert.Equal("ENAME = :oranda_f1 AND SAL <> :oranda_f2", vm.WhereText);
        Assert.Equal(800m, asked[1].Binds["oranda_f2"].Value);
        Assert.Equal("ALLEN", asked[1].Binds["oranda_f1"].Value);

        await vm.Model!.LoadFirstPageAsync();
        vm.Model!.SelectCell(new GridCell(2, 1)); // NULL 칸에서 「이 값으로 거르기」
        await vm.FilterByCellAsync(CellFilterKind.Equal);
        Assert.Equal("ENAME = :oranda_f1 AND SAL <> :oranda_f2 AND ENAME IS NULL", vm.WhereText);
        vm.Clear();
    }

    [Fact]
    public async Task Filtering_is_offered_only_for_query_results()
    {
        var asked = RecordFilters();
        var plsql = await PaneAsync(new SqlStatement("BEGIN OPEN :c FOR SELECT * FROM emp; END;", default, StatementKind.PlSql));

        Assert.False(plsql.CanApplyFilter); // REF CURSOR 결과는 감쌀 질의가 없다
        plsql.ToggleFilterBar();
        Assert.False(plsql.IsFilterBarVisible);
        plsql.WhereText = "SAL > 1";
        await plsql.ApplyFilterAsync();
        plsql.Model!.SelectCell(new GridCell(0, 1));
        await plsql.FilterByCellAsync(CellFilterKind.Equal);
        Assert.Empty(asked);
        plsql.Clear();
    }

    [Fact]
    public async Task Without_an_engine_the_status_line_says_so()
    {
        var vm = await PaneAsync();
        vm.WhereText = "SAL > 1";

        await vm.ApplyFilterAsync();

        Assert.Contains("거를 수 없습니다", vm.StatusText, StringComparison.Ordinal);
        vm.Clear();
    }

    [Fact]
    public async Task A_filtered_result_is_read_only_and_says_editing_is_off()
    {
        RecordFilters();
        var vm = await PaneAsync();
        vm.WhereText = "SAL > 1";
        await vm.ApplyFilterAsync();

        vm.ShowEditHint();

        Assert.Contains("거른 결과는 편집할 수 없습니다", vm.EditHint, StringComparison.Ordinal);
        Assert.False(vm.OffersRerunForEdit);
        Assert.Null(vm.EditHintAction);
        vm.Clear();
    }

    [Fact]
    public async Task Counting_a_filtered_result_counts_the_filter_without_order_by()
    {
        RecordFilters();
        ResultFilterRequest? counted = null;
        ResultGridCommands.CountRows = (_, _) => Task.FromResult<long?>(999);
        ResultGridCommands.CountFiltered = (request, _) =>
        {
            counted = request;
            return Task.FromResult<long?>(2);
        };
        var vm = await PaneAsync();
        vm.WhereText = "SAL > 1";
        vm.OrderByText = "SAL";
        await vm.ApplyFilterAsync();

        await vm.CountAllRowsAsync();

        Assert.Equal("SAL > 1", counted?.Where);
        Assert.Equal(string.Empty, counted?.OrderBy);
        Assert.Equal(2, vm.CountedRowCount);
        vm.Clear();
    }

    [Fact]
    public async Task A_late_filter_result_after_the_pane_closed_is_disposed()
    {
        var gate = new TaskCompletionSource<IResultSet>();
        ResultGridCommands.Filter = (_, _) => gate.Task;
        var vm = await PaneAsync();
        vm.WhereText = "SAL > 1";

        var run = vm.ApplyFilterAsync();
        Assert.True(vm.IsFiltering);
        vm.Clear();
        var late = Emp();
        gate.SetResult(late);
        await run;

        Assert.True(late.Disposed);
        Assert.Null(vm.Model);
        Assert.False(vm.IsFiltering);
    }

    [Fact]
    public async Task Recent_conditions_keep_the_last_ten_newest_first_without_duplicates()
    {
        RecordFilters();
        var vm = await PaneAsync();
        for (var i = 1; i <= 12; i++)
        {
            vm.WhereText = "SAL > " + i;
            await vm.ApplyFilterAsync();
        }

        vm.WhereText = "SAL > 5";
        await vm.ApplyFilterAsync();

        Assert.Equal(ResultFilter.HistorySize, vm.FilterHistory.Count);
        Assert.Equal("SAL > 5", vm.FilterHistory[0]);
        Assert.Equal("SAL > 12", vm.FilterHistory[1]);
        Assert.Single(vm.FilterHistory, c => c == "SAL > 5");
        Assert.DoesNotContain("SAL > 2", vm.FilterHistory);
        vm.Clear();
    }

    [Fact]
    public Task Filter_bar_keys_apply_with_enter_and_escape_clears_then_closes() => Oranda.App.Tests.Shell.Headless.HeadlessHost.Run(async () =>
    {
        var asked = RecordFilters();
        var vm = new SqlResultPaneViewModel { Statement = Query };
        vm.SetResult(Emp());
        var view = new SqlResultPaneView { DataContext = vm };
        var window = new Window { Content = view, Width = 900, Height = 400 };
        window.Show();
        try
        {
            vm.ToggleFilterBar();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var where = view.FindControl<TextBox>("WhereBox")!;
            Assert.True(where.IsEffectivelyVisible);
            Assert.True(where.IsFocused); // 줄을 열면 WHERE 칸으로

            where.Text = "SAL > 1";
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            await WaitAsync(() => asked.Count == 1);
            Assert.Equal("SAL > 1", asked[0].Where);

            where.Focus();
            window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
            Assert.Equal(string.Empty, vm.WhereText); // 글이 있으면 비운다
            Assert.True(vm.IsFilterBarVisible);
            window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
            Assert.False(vm.IsFilterBarVisible); // 빈 칸이면 줄을 닫는다
            Assert.True(vm.IsFiltered); // 닫아도 걸어 둔 거르기는 그대로
        }
        finally
        {
            window.Close();
            vm.Clear();
        }
    });

    // ---- 명령 경로 ------------------------------------------------------------------------

    [Fact]
    public async Task Grid_commands_reach_the_pane_and_the_result_tab()
    {
        var asked = RecordFilters();
        var tabs = new ResultTabsViewModel();
        var tab = tabs.AddQuery(Emp(), "결과 1", Query);
        var vm = tab.Pane!;
        await vm.Model!.LoadFirstPageAsync();
        ResultGridCommands.Active = vm.Model;
        var h = new ResultGridCommandHandler();
        var focus = 0;
        vm.FilterFocusRequested += (_, _) => focus++;

        await h.ExecuteAsync(new CommandContext("oranda.grid.toggleFilterBar", new AllKeys()), CancellationToken.None);
        Assert.True(vm.IsFilterBarVisible);
        Assert.Equal(1, focus); // 줄을 열면 WHERE 칸으로

        vm.Model.SelectCell(new GridCell(0, 1));
        await h.ExecuteAsync(new CommandContext("oranda.grid.filterNullRows", new AllKeys()), CancellationToken.None);
        await WaitAsync(() => asked.Count == 1);
        Assert.Equal("ENAME IS NULL", asked[0].Where);
        Assert.Equal(1, focus); // 셀 값 거르기는 포커스를 그리드에 둔다

        ResultGridCommands.Active = vm.Model; // 거르면 결과(모델)가 바뀐다
        await h.ExecuteAsync(new CommandContext("oranda.grid.pinResultTab", new AllKeys()), CancellationToken.None);
        Assert.True(tab.IsPinned);
        await h.ExecuteAsync(new CommandContext("oranda.grid.pinResultTab", new AllKeys()), CancellationToken.None);
        Assert.False(tab.IsPinned);
        tabs.Clear();
    }

    [Fact]
    public void Filter_and_pin_commands_are_declared_with_query_and_selection_enablement()
    {
        var defs = typeof(ResultGridCommandHandler).GetCustomAttributes(typeof(OrandaCommandAttribute), false)
            .Cast<OrandaCommandAttribute>().ToDictionary(a => a.Id);

        Assert.Equal("gridQueryResult", defs["oranda.grid.toggleFilterBar"].Enablement);
        foreach (var id in new[] { "oranda.grid.filterByValue", "oranda.grid.filterExcludeValue", "oranda.grid.filterNullRows" })
        {
            Assert.Equal("gridQueryResult && gridHasSelection && hasSession && !sessionBusy", defs[id].Enablement);
            Assert.Equal("gridFocus", defs[id].When);
            Assert.StartsWith("context:grid/filter/filter@", Assert.Single(defs[id].Placements!), StringComparison.Ordinal); // 「거르기 ▸」 하위 메뉴(TSK-09-03)
        }

        Assert.Equal("gridHasResult", defs["oranda.grid.pinResultTab"].Enablement);
    }

    [Fact]
    public Task Pin_and_filter_icons_exist() => Oranda.App.Tests.Features.SqlEditor.Ui.Run(() =>
    {
        // 아이콘 경로는 Avalonia 렌더 인터페이스로 읽으므로 헤드리스 UI 스레드에서 확인한다.
        Assert.True(Oranda.App.Shell.Icons.ShellIcons.Contains("pin"));
        Assert.True(Oranda.App.Shell.Icons.ShellIcons.Contains("filter"));
        Assert.True(Oranda.App.Shell.Icons.ShellIcons.Contains("filterClear"));
    });

    // ---- 결과 탭 고정(GRID-06) -----------------------------------------------------------------

    [Fact]
    public void Clearing_result_tabs_keeps_pinned_tabs_and_new_numbers_avoid_them()
    {
        var tabs = new ResultTabsViewModel();
        var first = tabs.AddQuery(Emp(), "결과 1", Query);
        var second = tabs.AddQuery(Emp(), "결과 2", Query);
        second.TogglePin();

        tabs.Clear();

        Assert.Same(second, Assert.Single(tabs.Tabs));
        Assert.Same(second, tabs.Selected);
        Assert.Null(first.Pane!.Model); // 고정하지 않은 탭의 결과는 닫혔다
        Assert.NotNull(second.Pane!.Model);
        Assert.Equal(3, tabs.NextOrdinal()); // 「결과 2」는 고정한 탭이 쓴다

        second.UnpinCommand.Execute(null);
        Assert.False(second.IsPinned);
        tabs.Clear();
        Assert.Empty(tabs.Tabs);
        Assert.Null(tabs.Selected);
        Assert.Equal(1, tabs.NextOrdinal());
    }

    private sealed class AllKeys : IContextKeys
    {
        public bool Evaluate(string? expression) => true;
    }
}
