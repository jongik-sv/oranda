using System.Diagnostics;
using Avalonia.Controls;
using Oranda.App.Features.ResultGrid;
using Oranda.Core.Grid;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.ResultGrid;

/// <summary>TSK-06-03 시험: 그리드 상태(열 숨김·고정·정렬·선택·복사·찾기·텍스트 모드), 큰 결과, 명령 처리기.</summary>
public sealed class ResultGridTests
{
    /// <summary>행을 만들어 내는 가짜 결과. Oranda.Testing 의 FakeResultSet 은 행 목록이 필요해 큰 결과에 맞지 않아 따로 둔다.</summary>
    private sealed class GeneratedResultSet(int rows, int pageSize = 500) : IResultSet
    {
        private int _fetched;

        public IReadOnlyList<ResultColumn> Columns { get; } =
        [
            new(0, "ID", "NUMBER", ColumnDataKind.Number),
            new(1, "NAME", "VARCHAR2", ColumnDataKind.Text),
            new(2, "GRP", "VARCHAR2", ColumnDataKind.Text),
        ];

        public bool IsComplete => (long)_fetched * pageSize >= rows;

        public long? KnownRowCount => IsComplete ? rows : null;

        public int PageRequests { get; private set; }

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            PageRequests++;
            _fetched = Math.Max(_fetched, pageIndex + 1);
            var first = pageIndex * pageSize;
            var count = Math.Clamp(rows - first, 0, pageSize);
            var ids = new object?[count];
            var names = new object?[count];
            var grps = new object?[count];
            for (var i = 0; i < count; i++)
            {
                var r = first + i;
                ids[i] = (decimal)r;
                names[i] = r % 10 == 0 ? null : "n" + (rows - r);
                grps[i] = "g" + (r / 3);
            }

            return Task.FromResult(new ResultPage(pageIndex, first, count, [ids, names, grps]) { IsLast = first + count >= rows });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<ResultGridModel> LoadedAsync(int rows, int pageSize = 500)
    {
        var m = new ResultGridModel(new GeneratedResultSet(rows, pageSize), pageSize: pageSize);
        await m.LoadFirstPageAsync();
        return m;
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
    public Task Focused_grid_reports_more_rows_so_fetch_all_is_enabled() => Oranda.App.Tests.Shell.Headless.HeadlessHost.Run(async () =>
    {
        var keys = new Oranda.App.Shell.ContextKeyService();
        var previous = Oranda.App.Shell.ContextKeyService.Shared;
        Oranda.App.Shell.ContextKeyService.Shared = keys;
        try
        {
            var vm = new SqlResultPaneViewModel();
            vm.SetResult(new GeneratedResultSet(2000));
            var view = new SqlResultPaneView { DataContext = vm };
            var window = new Avalonia.Controls.Window { Content = view, Width = 800, Height = 400 };
            window.Show();
            await WaitAsync(() => vm.Model!.LoadedRowCount >= 500);
            view.FindControl<ResultGridControl>("GridControl")!.Focus();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.True(keys.Get("gridHasMoreRows")); // 「모두 가져오기」 활성 조건

            await vm.Model!.FetchAllAsync();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(keys.Get("gridHasMoreRows"));
            window.Close();
        }
        finally
        {
            Oranda.App.Shell.ContextKeyService.Shared = previous;
        }
    });

    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new SqlResultPaneViewModel());
    }

    [Fact]
    public async Task First_rows_are_readable_right_after_first_page_and_row_count_grows_lazily()
    {
        await using var m = await LoadedAsync(2000);
        Assert.Equal(500, m.LoadedRowCount);
        Assert.Null(m.TotalRowCount);
        Assert.True(m.HasMoreRows);
        Assert.Equal("0", m.GetCellText(0, 0));
        Assert.Equal("(null)", m.GetCellText(0, 1));
        Assert.True(m.IsNull(0, 1));

        m.Prefetch(400, 450); // 끝 근처면 다음 페이지를 미리 받는다
        await WaitAsync(() => m.LoadedRowCount >= 1000);
        await m.FetchAllAsync();
        Assert.Equal(2000, m.TotalRowCount);
        Assert.False(m.HasMoreRows);
    }

    [Fact]
    public async Task Unloaded_cell_is_blank_and_requested_in_background()
    {
        await using var m = await LoadedAsync(5000);
        Assert.Equal(string.Empty, m.GetCellText(1200, 0)); // 3번째 페이지, 아직 없음
        await WaitAsync(() => m.TryGetValue(1200, 0, out _));
        Assert.Equal("1200", m.GetCellText(1200, 0));
    }

    [Fact]
    public async Task Hide_and_freeze_columns()
    {
        await using var m = await LoadedAsync(10);
        m.HideColumn(1);
        Assert.Equal([0, 2], m.VisibleColumns);
        m.HideColumn(0);
        m.HideColumn(2); // 마지막 열은 남긴다
        Assert.Single(m.VisibleColumns);
        m.ShowAllColumns();
        Assert.Equal(3, m.VisibleColumns.Count);

        m.FreezeColumns(2);
        Assert.Equal(2, m.FrozenCount);
        m.FreezeColumns(2);
        Assert.Equal(0, m.FrozenCount);
        m.SelectCell(new GridCell(0, 1));
        m.FreezeUpToActive();
        Assert.Equal(2, m.FrozenCount);
        m.HideColumn(0);
        Assert.Equal(1, m.FrozenCount);
    }

    [Fact]
    public async Task Sort_by_number_column_is_numeric_not_textual()
    {
        await using var m = await LoadedAsync(30, 10);
        await m.ToggleSortAsync(0);
        Assert.Equal("0", m.GetCellText(0, 0));
        Assert.Equal("29", m.GetCellText(29, 0));
        await m.ToggleSortAsync(0);
        Assert.True(m.SortDescending);
        Assert.Equal("29", m.GetCellText(0, 0));
        await m.ToggleSortAsync(0);
        Assert.False(m.IsSorted);
        Assert.Equal("0", m.GetCellText(0, 0));
    }

    [Fact]
    public async Task Sort_text_column_puts_nulls_last()
    {
        await using var m = await LoadedAsync(30, 10);
        await m.ToggleSortAsync(1);
        Assert.Equal(1, m.SortColumn);
        Assert.Equal("(null)", m.GetCellText(29, 1));
        Assert.Equal("(null)", m.GetCellText(27, 1));
        Assert.NotEqual("(null)", m.GetCellText(26, 1));
    }

    [Fact]
    public async Task Repeat_hide_blanks_cells_equal_to_previous_row()
    {
        await using var m = await LoadedAsync(10);
        Assert.Equal("g0", m.GetCellText(1, 2));
        m.ToggleRepeatHide();
        Assert.True(m.HideRepeatedValues);
        Assert.Equal("g0", m.GetCellText(0, 2));
        Assert.Equal(string.Empty, m.GetCellText(1, 2));
        Assert.Equal(string.Empty, m.GetCellText(2, 2));
        Assert.Equal("g1", m.GetCellText(3, 2));
    }

    [Fact]
    public async Task Selection_rectangle_and_copy_formats()
    {
        await using var m = await LoadedAsync(10);
        string? clip = null;
        m.ClipboardWriter = t =>
        {
            clip = t;
            return Task.CompletedTask;
        };

        m.SelectCell(new GridCell(1, 0));
        m.ExtendTo(new GridCell(2, 1));
        Assert.True(m.IsSelected(2, 1));
        Assert.False(m.IsSelected(3, 1));

        await m.CopyAsync(CopyFormat.Tsv);
        Assert.Equal("1\tn9\n2\tn8", clip);
        await m.CopyAsync(CopyFormat.Tsv, includeHeaders: true);
        Assert.Equal("ID\tNAME\n1\tn9\n2\tn8", clip);
        await m.CopyAsync(CopyFormat.Insert); // INSERT 문은 고른 칸이 걸친 행의 모든 열(오렌지·Toad 처럼)
        Assert.Equal("INSERT INTO TABLE_NAME (ID, NAME, GRP) VALUES (1, 'n9', 'g0');\nINSERT INTO TABLE_NAME (ID, NAME, GRP) VALUES (2, 'n8', 'g0');", clip);
        await m.CopyAsync(CopyFormat.InList);
        Assert.Equal("(1, 'n9', 2, 'n8')", clip);

        m.HideColumn(0);
        m.SelectAll();
        var csv = await m.BuildCopyTextAsync(CopyFormat.Csv, true);
        Assert.StartsWith("NAME,GRP", csv, StringComparison.Ordinal);
        Assert.Contains(",g0", csv, StringComparison.Ordinal);
        Assert.Contains("n9,g0", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_without_selection_is_empty()
    {
        await using var m = await LoadedAsync(3);
        Assert.Equal(string.Empty, await m.BuildCopyTextAsync(CopyFormat.Tsv));
    }

    [Fact]
    public async Task Text_mode_pads_columns_and_aligns_numbers_right()
    {
        await using var m = await LoadedAsync(12);
        m.AutoSizeColumns(8);
        m.ToggleTextMode();
        Assert.True(m.IsTextMode);
        var header = m.GetTextHeader().Split(Environment.NewLine);
        var line = m.GetTextLine(1);
        Assert.Equal(2, header.Length);
        Assert.Equal(header[0].Length, line.Length);
        Assert.StartsWith("ID", header[0], StringComparison.Ordinal);
        Assert.Contains("|", line, StringComparison.Ordinal);
        Assert.Equal(' ', line[0]); // 숫자는 오른쪽 맞춤이라 앞에 공백
    }

    [Fact]
    public void Display_width_counts_wide_chars_twice()
    {
        Assert.Equal(4, ResultGridModel.DisplayWidth("ab한"));
    }

    [Fact]
    public async Task Find_next_selects_match_and_wraps_around()
    {
        await using var m = await LoadedAsync(1500);
        Assert.True(await m.FindNextAsync("n999"));
        Assert.Equal(new GridCell(501, 1), m.Active); // n(rows - r) = n999 → r = 501
        Assert.False(await m.FindNextAsync("없는글자"));
        Assert.True(await m.FindNextAsync("n1499")); // 한 바퀴 돌아 처음 행 근처
        Assert.Equal(new GridCell(1, 1), m.Active);
        Assert.False(await m.FindNextAsync(""));
    }

    [Fact]
    public async Task Large_result_scrolls_through_spill_without_refetching_source()
    {
        var rs = new GeneratedResultSet(100_000);
        await using var m = new ResultGridModel(rs, new ResultPageCacheOptions { MaxMemoryBytes = 1_000_000 });
        await m.LoadFirstPageAsync();
        await m.FetchAllAsync();
        Assert.Equal(100_000, m.TotalRowCount);
        Assert.True(m.Cache.SpilledPageCount > 0);
        var requests = rs.PageRequests;

        for (long r = 0; r < 100_000; r += 4999)
        {
            m.Prefetch(r, r + 40);
            await WaitAsync(() => m.TryGetValue(r, 0, out _));
            Assert.Equal(r.ToString(System.Globalization.CultureInfo.InvariantCulture), m.GetCellText(r, 0));
        }

        Assert.Equal(requests, rs.PageRequests);
    }

    [Fact]
    public async Task Command_handler_acts_on_active_grid()
    {
        await using var m = await LoadedAsync(10);
        string? clip = null;
        m.ClipboardWriter = t =>
        {
            clip = t;
            return Task.CompletedTask;
        };
        m.SelectCell(new GridCell(3, 0));
        var closed = false;
        m.CloseRequested += (_, _) => closed = true;
        ResultGridCommands.Active = m;
        try
        {
            var h = new ResultGridCommandHandler();
            var keys = new NoKeys();
            await h.ExecuteAsync(new CommandContext("oranda.grid.copy", keys), CancellationToken.None);
            Assert.Equal("3", clip);
            await h.ExecuteAsync(new CommandContext("oranda.grid.toggleTextMode", keys), CancellationToken.None);
            Assert.True(m.IsTextMode);
            await h.ExecuteAsync(new CommandContext("oranda.grid.toggleRepeatHide", keys), CancellationToken.None);
            Assert.True(m.HideRepeatedValues);
            await h.ExecuteAsync(new CommandContext("oranda.grid.hideColumn", keys), CancellationToken.None);
            Assert.Equal(2, m.VisibleColumns.Count);
            await h.ExecuteAsync(new CommandContext("oranda.grid.showAllColumns", keys), CancellationToken.None);
            Assert.Equal(3, m.VisibleColumns.Count);
            await h.ExecuteAsync(new CommandContext("oranda.grid.closeResultTab", keys), CancellationToken.None);
            Assert.True(closed);
        }
        finally
        {
            ResultGridCommands.Active = null;
        }
    }

    [Fact]
    public void Handler_declares_every_grid_command_owned_by_this_task()
    {
        var ids = typeof(ResultGridCommandHandler).GetCustomAttributes(typeof(OrandaCommandAttribute), false)
            .Cast<OrandaCommandAttribute>().Select(a => a.Id).ToHashSet();
        string[] expected =
        [
            "oranda.grid.copy", "oranda.grid.copyWithHeaders", "oranda.grid.copyRows", "oranda.grid.copyRowsWithHeaders",
            "oranda.grid.copyAsCsv", "oranda.grid.copyAsInsert", "oranda.grid.copyAsInList",
            "oranda.grid.find", "oranda.grid.fetchAll", "oranda.grid.toggleTextMode", "oranda.grid.freezeColumns", "oranda.grid.hideColumn",
            "oranda.grid.showAllColumns", "oranda.grid.toggleRepeatHide", "oranda.grid.closeResultTab", "oranda.grid.countRows",
            "oranda.grid.toggleFilterBar", "oranda.grid.toggleQuickFilter", "oranda.grid.clearLocalFilters", "oranda.grid.filterByValue", "oranda.grid.filterExcludeValue", "oranda.grid.filterNullRows",
            "oranda.grid.pinResultTab", "oranda.grid.followForeignKey", "oranda.grid.showReferencingRows",
        ];
        Assert.Equal(expected.Order(), ids.Order());
    }

    [Fact]
    public async Task Pane_view_model_swaps_results_and_reports_status()
    {
        var vm = new SqlResultPaneViewModel();
        vm.SetResult(new GeneratedResultSet(1200));
        Assert.NotNull(vm.Model);
        await vm.Model!.LoadFirstPageAsync();
        await WaitAsync(() => vm.StatusText.Contains("500", StringComparison.Ordinal)); // 모델 알림은 UI 문맥으로 올라온다
        Assert.Contains("더 있음", vm.StatusText, StringComparison.Ordinal);
        await vm.Model.FetchAllAsync();
        await WaitAsync(() => !vm.StatusText.Contains("더 있음", StringComparison.Ordinal) && vm.StatusText.Contains("200", StringComparison.Ordinal));

        var old = vm.Model;
        vm.SetResult(new GeneratedResultSet(5));
        Assert.NotSame(old, vm.Model);
        vm.Clear();
        Assert.Null(vm.Model);
    }

    private sealed class NoKeys : IContextKeys
    {
        public bool Evaluate(string? expression) => true;
    }
}
