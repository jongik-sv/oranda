using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Grid;

namespace Oranda.App.Tests.Features.ResultGrid;

/// <summary>TSK-13-26 열 머리글 자동 필터: 값 목록(개수·NULL·잘림), 값 체크·조건·여러 열 AND, 패널 상태, ▾ 누르기는 정렬하지 않는다.</summary>
public sealed class ColumnFilterTests
{
    private static readonly ResultColumn[] Cols =
    [
        new(0, "ID", "NUMBER", ColumnDataKind.Number),
        new(1, "DEPT", "VARCHAR2", ColumnDataKind.Text),
        new(2, "HIRED", "DATE", ColumnDataKind.Date),
    ];

    /// <summary>ID = 행 번호(0~), DEPT = 「D(r%3)」(5 의 배수 행은 NULL), HIRED = 2024-01-01 + r 일.</summary>
    private sealed class Rows(int rows) : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns => Cols;

        public bool IsComplete => true;

        public long? KnownRowCount => rows;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            var first = pageIndex * 500;
            var count = Math.Clamp(rows - first, 0, 500);
            var ids = new object?[count];
            var depts = new object?[count];
            var hired = new object?[count];
            for (var i = 0; i < count; i++)
            {
                var r = first + i;
                ids[i] = (decimal)r;
                depts[i] = r % 5 == 0 ? null : "D" + (r % 3);
                hired[i] = new DateTime(2024, 1, 1).AddDays(r);
            }

            return Task.FromResult(new ResultPage(pageIndex, first, count, [ids, depts, hired]) { IsLast = first + count >= rows });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<ResultGridModel> OpenAsync(int rows)
    {
        var m = new ResultGridModel(new Rows(rows));
        await m.LoadFirstPageAsync();
        return m;
    }

    private static async Task<string[]> IdsAsync(ResultGridModel m)
    {
        var rows = await m.ReadRowsAsync(0, m.RowCount - 1, [0]);
        return [.. rows.Select(r => Convert.ToString(r[0], System.Globalization.CultureInfo.InvariantCulture)!)];
    }

    private static LocalFilterSpec On(int ordinal, ColumnFilter f) => LocalFilterSpec.Empty.WithColumn(ordinal, f);

    [Fact]
    public async Task Value_list_counts_distinct_displayed_values_and_nulls()
    {
        await using var m = await OpenAsync(20);

        var list = await m.CollectColumnValuesAsync(1);

        Assert.Equal(["D0", "D1", "D2"], list.Values.Select(v => v.Text));
        Assert.Equal(4, list.NullCount); // 0, 5, 10, 15
        Assert.Equal(16, list.Values.Sum(v => v.Count));
        Assert.False(list.Truncated);
    }

    [Fact]
    public async Task Value_list_is_capped_and_says_so()
    {
        await using var m = await OpenAsync(100);

        var list = await m.CollectColumnValuesAsync(0, maxValues: 10); // ID 는 100개 모두 다르다

        Assert.Equal(10, list.Values.Count);
        Assert.True(list.Truncated);
        Assert.Equal(["0", "1", "2"], list.Values.Take(3).Select(v => v.Text)); // 수 열은 수 순서
    }

    [Fact]
    public async Task Checked_values_keep_only_those_rows_and_null_is_its_own_choice()
    {
        await using var m = await OpenAsync(20);

        await m.SetLocalFilterAsync(On(1, new ColumnFilter { Values = new HashSet<string> { "D1" }, IncludeNull = false }));
        Assert.Equal(["1", "4", "7", "13", "16", "19"], await IdsAsync(m)); // r%3==1 이고 5 의 배수가 아닌 행

        await m.SetLocalFilterAsync(On(1, new ColumnFilter { Values = new HashSet<string> { "D1" }, IncludeNull = true }));
        Assert.Contains("0", await IdsAsync(m)); // NULL 행(0)도 남는다

        await m.SetLocalFilterAsync(On(1, new ColumnFilter { IncludeNull = false })); // NULL 만 뺀다
        Assert.Equal(16, m.RowCount);
    }

    [Fact]
    public async Task Excluded_values_drop_only_the_unchecked_ones()
    {
        await using var m = await OpenAsync(20);

        await m.SetLocalFilterAsync(On(1, new ColumnFilter { Excluded = new HashSet<string> { "D1", "D2" } }));

        Assert.Equal(["0", "3", "5", "6", "9", "10", "12", "15", "18"], await IdsAsync(m)); // D0 이거나 NULL
    }

    [Fact]
    public async Task Conditions_contains_equals_starts_and_null_tests()
    {
        await using var m = await OpenAsync(20);

        await m.SetLocalFilterAsync(On(1, new ColumnFilter { Condition = new ColumnCondition(ConditionKind.Equals, "d2") }));
        Assert.Equal(["2", "8", "11", "14", "17"], await IdsAsync(m)); // 대소문자 무시

        await m.SetLocalFilterAsync(On(1, new ColumnFilter { Condition = new ColumnCondition(ConditionKind.StartsWith, "D") }));
        Assert.Equal(16, m.RowCount);

        await m.SetLocalFilterAsync(On(1, new ColumnFilter { Condition = new ColumnCondition(ConditionKind.Contains, "1") }));
        Assert.Equal(["1", "4", "7", "13", "16", "19"], await IdsAsync(m));

        await m.SetLocalFilterAsync(On(1, new ColumnFilter { Condition = new ColumnCondition(ConditionKind.IsNull) }));
        Assert.Equal(["0", "5", "10", "15"], await IdsAsync(m));

        await m.SetLocalFilterAsync(On(1, new ColumnFilter { Condition = new ColumnCondition(ConditionKind.IsNotNull) }));
        Assert.Equal(16, m.RowCount);
    }

    [Fact]
    public async Task Ranges_compare_numbers_and_dates_by_value_not_by_text()
    {
        await using var m = await OpenAsync(30);

        await m.SetLocalFilterAsync(On(0, new ColumnFilter { Condition = new ColumnCondition(ConditionKind.Between, "9", "11") })); // 글자 순이면 9 가 11 밖
        Assert.Equal(["9", "10", "11"], await IdsAsync(m));

        await m.SetLocalFilterAsync(On(0, new ColumnFilter { Condition = new ColumnCondition(ConditionKind.Between, "27", string.Empty) })); // 끝을 비우면 제한 없음
        Assert.Equal(["27", "28", "29"], await IdsAsync(m));

        await m.SetLocalFilterAsync(On(2, new ColumnFilter { Condition = new ColumnCondition(ConditionKind.Between, "2024-01-03", "2024-01-05") }));
        Assert.Equal(["2", "3", "4"], await IdsAsync(m));
    }

    [Fact]
    public async Task Several_column_filters_and_the_quick_text_combine_with_and()
    {
        await using var m = await OpenAsync(30);
        var spec = new LocalFilterSpec { QuickText = "d2" }
            .WithColumn(0, new ColumnFilter { Condition = new ColumnCondition(ConditionKind.Between, "10", "25") })
            .WithColumn(1, new ColumnFilter { IncludeNull = false });

        await m.SetLocalFilterAsync(spec);

        Assert.Equal(["11", "14", "17", "23"], await IdsAsync(m)); // D2 이고 10~25 이고 NULL 아님 (20 은 NULL, 26 은 범위 밖)
        await m.SetLocalFilterAsync(spec.WithColumn(0, null)); // 한 열만 지우면 나머지는 그대로
        Assert.Contains("26", await IdsAsync(m));
        Assert.DoesNotContain("20", await IdsAsync(m));
    }

    [Fact]
    public async Task Panel_builds_the_filter_from_checks_and_condition()
    {
        await using var m = await OpenAsync(20);
        var list = await m.CollectColumnValuesAsync(1);
        var panel = new ColumnFilterPanel(Cols[1], list, null);

        Assert.Null(panel.BuildFilter()); // 모두 체크 + 조건 없음 = 필터 없음

        panel.SetChecked("D1", false);
        panel.SetChecked(null, false); // NULL 항목
        var filter = panel.BuildFilter()!;
        Assert.Equal(new HashSet<string> { "D0", "D2" }, filter.Values);
        Assert.False(filter.IncludeNull);

        panel.SetAllChecked(true);
        panel.SetCondition(ConditionKind.Between, "a", "b");
        Assert.Equal(new ColumnCondition(ConditionKind.Between, "a", "b"), panel.BuildFilter()!.Condition);

        // 다시 열면 지금 필터가 체크 상태로 돌아온다
        var again = new ColumnFilterPanel(Cols[1], list, filter);
        Assert.Equal(filter.Values, again.BuildFilter()!.Values);
    }

    [Fact]
    public async Task A_truncated_list_filters_by_exclusion()
    {
        await using var m = await OpenAsync(100);
        var list = await m.CollectColumnValuesAsync(0, maxValues: 10);
        var panel = new ColumnFilterPanel(Cols[0], list, null);

        panel.SetChecked("3", false);
        var filter = panel.BuildFilter()!;

        Assert.Null(filter.Values); // 잘린 목록에서는 체크한 값만 남기지 않고 푼 값만 뺀다
        Assert.Equal(new HashSet<string> { "3" }, filter.Excluded);
        await m.SetLocalFilterAsync(On(0, filter));
        Assert.Equal(99, m.RowCount); // 목록 밖의 값(10~99)은 그대로 보인다
    }

    [Fact]
    public async Task Pane_applies_a_column_filter_and_a_second_column_filter_narrows_further()
    {
        var vm = new SqlResultPaneViewModel();
        vm.SetResult(new Rows(30));
        await vm.Model!.LoadFirstPageAsync();

        await vm.SetColumnFilterAsync(1, new ColumnFilter { Values = new HashSet<string> { "D2" }, IncludeNull = false });
        await vm.SetColumnFilterAsync(0, new ColumnFilter { Condition = new ColumnCondition(ConditionKind.Between, "10", "20") });

        Assert.Equal(["11", "14", "17"], await IdsAsync(vm.Model));
        Assert.Contains("거름: 보이는 3 / 가져온 30행", vm.StatusText, StringComparison.Ordinal);
        await vm.ClearLocalFiltersAsync();
        Assert.Equal(30, vm.Model.RowCount);
    }

    [Fact]
    public Task Pressing_the_header_arrow_opens_the_filter_and_never_sorts() => HeadlessHost.Run(async () =>
    {
        await using var m = new ResultGridModel(new Rows(10));
        var grid = new ResultGridControl { Model = m };
        var window = new Window { Content = grid, Width = 700, Height = 300 };
        window.Show();
        try
        {
            await HeadlessHost.WaitUntil(() => m.LoadedRowCount == 10 && grid.Bounds.Height > 0);
            ColumnFilterRequestedEventArgs? asked = null;
            grid.ColumnFilterRequested += (_, e) => asked = e;
            // 첫 열 오른쪽 끝에서 크기 조절 손잡이(4) 안쪽 ▾ 영역(16)의 가운데
            var arrow = grid.TranslatePoint(new Point(grid.RowHeaderWidth + m.GetColumnWidth(0) - 4 - 8, grid.HeaderHeight / 2), window)!.Value;

            window.MouseDown(arrow, MouseButton.Left);
            window.MouseUp(arrow, MouseButton.Left);
            await Task.Delay(100);

            Assert.NotNull(asked);
            Assert.Equal(0, asked!.Ordinal);
            Assert.Equal(-1, m.SortColumn); // 놓아도 정렬하지 않는다
            Assert.Equal([0, 1, 2], m.VisibleColumns);

            // 제목 가운데를 누르면 여전히 정렬한다
            var title = grid.TranslatePoint(new Point(grid.RowHeaderWidth + 20, grid.HeaderHeight / 2), window)!.Value;
            window.MouseDown(title, MouseButton.Left);
            window.MouseUp(title, MouseButton.Left);
            await HeadlessHost.WaitUntil(() => m.SortColumn == 0);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Scrolling_a_long_value_list_recycles_rows_without_throwing_and_keeps_checks() => HeadlessHost.Run(async () =>
    {
        await using var m = await OpenAsync(900);
        var list = await m.CollectColumnValuesAsync(0); // ID 는 900개 모두 다르다
        var panel = new ColumnFilterPanel(Cols[0], list, null);
        panel.SetChecked("5", false);
        var window = new Window { Content = panel, Width = 400, Height = 600 };
        window.Show();
        try
        {
            var listBox = panel.GetVisualDescendants().OfType<ListBox>().Single();
            await HeadlessHost.WaitUntil(() => listBox.Bounds.Height > 0);
            for (var round = 0; round < 3; round++)
            {
                listBox.ScrollIntoView(899); // 끝으로 갔다가 처음으로 돌아오며 컨테이너를 재활용시킨다
                Dispatcher.UIThread.RunJobs();
                listBox.ScrollIntoView(0);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Equal(new HashSet<string>(list.Values.Select(v => v.Text).Where(t => t != "5")), panel.BuildFilter()!.Values);
        }
        finally
        {
            window.Close();
        }
    });
}
