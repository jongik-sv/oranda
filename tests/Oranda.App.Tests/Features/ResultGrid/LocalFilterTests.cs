using System.Collections.Concurrent;
using System.Diagnostics;
using Oranda.App.Features.DataTransfer;
using Oranda.App.Features.LobRowView;
using Oranda.App.Features.ResultGrid;
using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Tests.Features.ResultGrid;

/// <summary>
/// TSK-13-26 조회 결과 안에서 거르기: 빠른 거르기(모든 열·한 열·대소문자·지우기), 보이는 행 모델(정렬·읽기·찾기와의 조합),
/// 다 안 가져온 결과, 편집 대기 중 거부, 큰 결과 응답성.
/// </summary>
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class LocalFilterTests
{
    private static readonly ResultColumn[] Cols =
    [
        new(0, "ID", "NUMBER", ColumnDataKind.Number),
        new(1, "NAME", "VARCHAR2", ColumnDataKind.Text),
        new(2, "GRP", "VARCHAR2", ColumnDataKind.Text),
    ];

    /// <summary>ID = 행 번호, NAME = 「Name행번호」(10 의 배수 행은 NULL), GRP = 「g(행번호%7)」.</summary>
    private sealed class GenSet(int rows, int pageSize = 500) : IResultSet
    {
        private int _fetched;

        public IReadOnlyList<ResultColumn> Columns => Cols;

        public bool IsComplete => (long)_fetched * pageSize >= rows;

        public long? KnownRowCount => IsComplete ? rows : null;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
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
                names[i] = r % 10 == 0 ? null : "Name" + r;
                grps[i] = "g" + (r % 7);
            }

            return Task.FromResult(new ResultPage(pageIndex, first, count, [ids, names, grps]) { IsLast = first + count >= rows });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<ResultGridModel> OpenAsync(int rows, int pageSize = 500)
    {
        var m = new ResultGridModel(new GenSet(rows, pageSize), pageSize: pageSize);
        await m.LoadFirstPageAsync();
        return m;
    }

    private static async Task<string[]> ColumnAsync(ResultGridModel m, int ordinal)
    {
        var rows = await m.ReadRowsAsync(0, m.RowCount - 1, [ordinal]);
        return [.. rows.Select(r => Convert.ToString(r[0], System.Globalization.CultureInfo.InvariantCulture) ?? "<null>")];
    }

    [Fact]
    public async Task Quick_filter_over_all_columns_is_case_insensitive_substring_on_displayed_text()
    {
        await using var m = await OpenAsync(30);

        Assert.True(await m.SetLocalFilterAsync(new LocalFilterSpec { QuickText = "NAME2" })); // 대소문자 무시, 일부 일치

        Assert.True(m.IsLocalFiltered);
        Assert.Equal(10, m.RowCount); // Name2 와 Name21~Name29 (20 번 행의 NAME 은 NULL)
        Assert.All(await ColumnAsync(m, 1), v => Assert.Contains("Name2", v, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Quick_filter_matches_any_visible_column_and_skips_hidden_ones()
    {
        await using var m = await OpenAsync(30);
        await m.SetLocalFilterAsync(new LocalFilterSpec { QuickText = "g3" });
        var all = m.RowCount;
        Assert.Equal(4, all); // 3, 10, 17, 24

        m.HideColumn(2); // GRP 을 숨기면 「모든 보이는 열」 대상에서 빠진다
        await WaitAsync(() => m.RowCount == 0);
        Assert.Equal(0, m.RowCount);
    }

    [Fact]
    public async Task Quick_filter_on_one_column_ignores_the_others()
    {
        await using var m = await OpenAsync(30);

        await m.SetLocalFilterAsync(new LocalFilterSpec { QuickText = "g1", QuickColumns = [1] }); // NAME 만 본다: g1 은 NAME 에 없다
        Assert.Equal(0, m.RowCount);

        await m.SetLocalFilterAsync(new LocalFilterSpec { QuickText = "g1", QuickColumns = [2] });
        Assert.Equal(5, m.RowCount); // 1, 8, 15, 22, 29
    }

    [Fact]
    public async Task Clearing_restores_every_row_and_the_original_order()
    {
        await using var m = await OpenAsync(30);
        await m.SetLocalFilterAsync(new LocalFilterSpec { QuickText = "g2" });
        Assert.True(m.IsLocalFiltered);

        await m.ClearLocalFilterAsync();

        Assert.False(m.IsLocalFiltered);
        Assert.Equal(30, m.RowCount);
        Assert.Equal("0", (await ColumnAsync(m, 0))[0]);
    }

    [Fact]
    public async Task Sorting_applies_to_the_filtered_rows_and_clearing_keeps_the_sort()
    {
        await using var m = await OpenAsync(30);
        await m.SetLocalFilterAsync(new LocalFilterSpec { QuickText = "g2" }); // 2, 9, 16, 23

        await m.ToggleSortAsync(0);
        await m.ToggleSortAsync(0); // 내림차순
        Assert.Equal(["23", "16", "9", "2"], await ColumnAsync(m, 0));

        await m.ClearLocalFilterAsync();
        Assert.Equal(30, m.RowCount);
        Assert.Equal("29", (await ColumnAsync(m, 0))[0]); // 정렬은 그대로 내림차순
    }

    [Fact]
    public async Task Filtering_after_sorting_keeps_the_sorted_order()
    {
        await using var m = await OpenAsync(30);
        await m.ToggleSortAsync(0);
        await m.ToggleSortAsync(0);

        await m.SetLocalFilterAsync(new LocalFilterSpec { QuickText = "g2" });

        Assert.Equal(["23", "16", "9", "2"], await ColumnAsync(m, 0));
    }

    [Fact]
    public async Task Cells_copy_and_find_use_visible_rows_only()
    {
        await using var m = await OpenAsync(30);
        await m.SetLocalFilterAsync(new LocalFilterSpec { QuickText = "g2" });
        m.SelectAll();

        var text = await m.BuildCopyTextAsync(CopyFormat.Tsv);
        Assert.Equal(4, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("Name3", text, StringComparison.Ordinal);

        Assert.False(await m.FindNextAsync("Name3")); // 숨긴 행(Name3 의 GRP 는 g3)은 찾지 않는다
        Assert.True(await m.FindNextAsync("Name16"));
        Assert.Equal(2, m.Active.Row); // 보이는 행 번호: 2, 9, 16, 23 → 16 은 셋째 줄(0 부터 2)
    }

    [Fact]
    public async Task A_partly_fetched_result_filters_only_what_was_fetched_then_includes_the_rest_after_fetch_all()
    {
        await using var m = await OpenAsync(1200); // 첫 페이지 500행만 가져왔다
        Assert.True(m.HasMoreRows);

        await m.SetLocalFilterAsync(new LocalFilterSpec { QuickText = "g2" });
        var first = m.BaseRowCount;
        Assert.InRange(first, 70, 72); // 500/7 = 71.4
        Assert.True(m.HasMoreRows); // 걸러도 나머지를 자동으로 가져오지 않는다
        Assert.Equal(500, m.LoadedRowCount);

        await m.FetchAllAsync();

        Assert.False(m.HasMoreRows);
        Assert.Equal(1200, m.LoadedRowCount);
        Assert.InRange(m.BaseRowCount, 171, 172); // 1200/7 = 171.4
        Assert.True(m.BaseRowCount > first);
    }

    [Fact]
    public async Task A_new_filter_cancels_the_running_one_and_the_last_one_wins()
    {
        await using var m = await OpenAsync(20_000);
        await m.FetchAllAsync();

        var slow = m.SetLocalFilterAsync(new LocalFilterSpec { QuickText = "Name1" });
        var last = m.SetLocalFilterAsync(new LocalFilterSpec { QuickText = "Name19999" });
        await Task.WhenAll(slow, last);

        Assert.False(await slow);
        Assert.Equal(1, m.RowCount);
        Assert.Equal("Name19999", m.LocalFilter.QuickText);
    }

    [Fact]
    public async Task Pending_edits_refuse_a_filter_change_and_say_why()
    {
        await using var m = await OpenAsync(30);
        await m.SetLocalFilterAsync(new LocalFilterSpec { QuickText = "g2" });
        m.EditOverlay = new PendingOverlay();
        string? notice = null;
        m.NoticeRequested += (_, text) => notice = text;

        Assert.False(await m.SetLocalFilterAsync(new LocalFilterSpec { QuickText = "g3" }));
        Assert.False(await m.ClearLocalFilterAsync());

        Assert.Equal(ResultGridModel.FilterBlockedNotice, notice);
        Assert.Equal("g2", m.LocalFilter.QuickText);
        Assert.Equal(4, m.RowCount);
    }

    [Fact]
    public async Task Filtering_100k_rows_keeps_the_ui_thread_responsive()
    {
        var gap = await Pump.RunAsync(async () =>
        {
            await using var m = new ResultGridModel(new GenSet(100_000));
            await m.LoadFirstPageAsync();
            await m.FetchAllAsync();
            var sw = Stopwatch.StartNew();
            using var ticks = Pump.StartTicker();
            var filtering = m.SetLocalFilterAsync(new LocalFilterSpec { QuickText = "name9999" });
            await filtering;
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));
            Assert.Equal(10, m.RowCount); // Name9999, Name99990..Name99999 중 NULL 인 99990 을 뺀 9개 + Name9999 = 10
            return ticks.MaxGap();
        });

        Assert.True(gap < TimeSpan.FromMilliseconds(500), $"UI 스레드가 {gap.TotalMilliseconds:N0} ms 멈췄다");
    }

    [Fact]
    public void Remapping_carries_column_filters_by_name_and_drops_missing_columns()
    {
        var from = new[] { new ResultColumn(0, "A", "NUMBER", ColumnDataKind.Number), new ResultColumn(1, "B", "VARCHAR2", ColumnDataKind.Text) };
        var to = new[] { new ResultColumn(0, "B", "VARCHAR2", ColumnDataKind.Text), new ResultColumn(1, "C", "VARCHAR2", ColumnDataKind.Text) };
        var spec = new LocalFilterSpec { QuickText = "x", QuickColumns = [0] }
            .WithColumn(0, new ColumnFilter { Condition = new ColumnCondition(ConditionKind.Contains, "1") })
            .WithColumn(1, new ColumnFilter { Condition = new ColumnCondition(ConditionKind.Contains, "2") });

        var remapped = SqlResultPaneViewModel.RemapFilter(spec, from, to);

        Assert.Equal("x", remapped.QuickText);
        Assert.Null(remapped.QuickColumns); // A 가 없어져 「모든 열」로 돌아간다
        Assert.Equal([0], remapped.Columns.Keys.ToArray()); // B 의 조건만 새 위치(0)로 남는다
    }

    [Fact]
    public async Task Pane_applies_the_quick_text_after_the_debounce_and_clears_in_two_escapes()
    {
        var vm = new SqlResultPaneViewModel { QuickFilterDebounce = TimeSpan.FromMilliseconds(20) };
        vm.SetResult(new GenSet(30));
        await vm.Model!.LoadFirstPageAsync();

        vm.QuickFilterText = "g";
        vm.QuickFilterText = "g2"; // 입력이 이어지면 앞 거르기는 버려진다
        // 상태 줄은 모델이 거르기를 끝낸 뒤 Changed 이벤트로 다시 쓴다. 이 시험은 UI 스레드 없이 돌아 그 사이를 볼 수 있으므로 상태 줄까지 기다린다.
        await WaitAsync(() => vm.Model.IsLocalFiltered && vm.Model.RowCount == 4 && vm.StatusText.Contains("거름: 보이는 4 / 가져온 30행", StringComparison.Ordinal));

        vm.IsQuickFilterVisible = true;
        Assert.True(vm.ClearOrCloseQuickFilter()); // 첫 Esc: 글만 지운다
        await WaitAsync(() => !vm.Model.IsLocalFiltered);
        Assert.True(vm.IsQuickFilterVisible);
        Assert.False(vm.ClearOrCloseQuickFilter()); // 다음 Esc: 칸을 닫는다
        Assert.False(vm.IsQuickFilterVisible);
    }

    [Fact]
    public async Task Pane_reports_a_partial_result_and_offers_fetch_all()
    {
        var vm = new SqlResultPaneViewModel { QuickFilterDebounce = TimeSpan.Zero };
        vm.SetResult(new GenSet(1200));
        await vm.Model!.LoadFirstPageAsync();

        vm.QuickFilterText = "g2";
        await WaitAsync(() => vm.Model.IsLocalFiltered);

        Assert.Contains("(전체 아님)", vm.StatusText, StringComparison.Ordinal);
        Assert.True(vm.CanFetchAllForFilter);
    }

    [Fact]
    public async Task A_new_result_clears_the_quick_text_but_a_server_requery_keeps_it()
    {
        var statement = new SqlStatement("SELECT id, name, grp FROM t", default, StatementKind.Query) { Verb = "SELECT" };
        var vm = new SqlResultPaneViewModel { QuickFilterDebounce = TimeSpan.Zero, Statement = statement };
        ResultGridCommands.Filter = (_, _) => Task.FromResult<IResultSet>(new GenSet(30));
        try
        {
            vm.SetResult(new GenSet(30));
            await vm.Model!.LoadFirstPageAsync();
            vm.QuickFilterText = "g2";
            await WaitAsync(() => vm.Model.IsLocalFiltered);

            vm.WhereText = "id > 0"; // 서버 거르기 줄로 다시 조회: 앱 안 거르기를 새 결과에 다시 적용한다
            await vm.ApplyFilterAsync();
            await WaitAsync(() => vm.Model!.IsLocalFiltered);
            Assert.Equal("g2", vm.QuickFilterText);

            vm.SetResult(new GenSet(30)); // 새 문장의 결과
            Assert.Equal(string.Empty, vm.QuickFilterText);
            Assert.False(vm.Model!.IsLocalFiltered);
        }
        finally
        {
            ResultGridCommands.Filter = null;
            ResultGridCommands.Active = null;
        }
    }

    // ---- 복사·내보내기·행 보기·편집 반영 조합 (L4) ----

    [Fact]
    public async Task Export_scopes_split_visible_rows_from_everything_fetched_in_sorted_order()
    {
        await using var m = await OpenAsync(30);
        await m.ToggleSortAsync(0); // ID 오름차순
        await m.ToggleSortAsync(0); // 내림차순
        await m.SetLocalFilterAsync(new LocalFilterSpec { QuickText = "g2" });
        m.HideColumn(1); // 숨긴 NAME 은 내보내지 않는다(GRP 는 보이므로 거르기는 그대로)

        Assert.Equal(4, ExcelExportPlan.LoadedRows(m)); // 「보이는 행만」
        var visible = ExcelExportPlan.FromGrid(m, ExcelExportScope.LoadedRows);
        Assert.Equal(["ID", "GRP"], visible.Columns.Select(c => c.Name));
        Assert.Equal(["23", "16", "9", "2"], (await Drain(visible.Rows)).Select(r => Convert.ToString(r[0], System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Equal(30, ExcelExportPlan.FetchedRows(m)); // 「가져온 전체」: 거르기 무시, 정렬은 유지
        var all = ExcelExportPlan.FromGrid(m, ExcelExportScope.FetchedAll);
        Assert.Equal(30, all.RowCount);
        var ids = (await Drain(all.Rows)).Select(r => Convert.ToString(r[0], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(Enumerable.Range(0, 30).Reverse().Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), ids);
    }

    [Fact]
    public async Task Export_dialog_offers_fetched_all_only_while_a_filter_is_active()
    {
        // 합친 내보내기 대화상자(TSK-09-07): 거르기가 없으면 「가져온 전체」 가 없고, 걸리면 기본이 「보이는 행만」 이다.
        await using var m = await OpenAsync(30);
        var plain = new ExportDialogViewModel(new NoTransfer(), null, m, Oranda.Core.Transfer.TransferFormat.Xlsx, openAfter: false);
        Assert.False(plain.HasFetchedAll);

        await m.SetLocalFilterAsync(new LocalFilterSpec { QuickText = "g2" });
        var filtered = new ExportDialogViewModel(new NoTransfer(), null, m, Oranda.Core.Transfer.TransferFormat.Xlsx, openAfter: false);
        Assert.True(filtered.HasFetchedAll);
        Assert.True(filtered.IsLoadedRows); // 기본 = 보이는 행만
        Assert.Contains("보이는 행만(4행", filtered.LoadedLabel, StringComparison.Ordinal);
        Assert.Contains("가져온 전체(30행", filtered.FetchedLabel, StringComparison.Ordinal);
        filtered.IsFetchedAll = true;
        Assert.Equal(ExcelExportScope.FetchedAll, filtered.Scope);
        Assert.False(filtered.IsLoadedRows);
    }

    private sealed class NoTransfer : Oranda.Core.Transfer.IDataTransferService
    {
        public Task<Oranda.Core.Transfer.TransferSummary> ExportAsync(Oranda.Core.Sessions.ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds, Oranda.Core.Transfer.ExportOptions options, Stream destination, IProgress<long>? progress, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Oranda.Core.Transfer.TransferSummary> ExportRowsAsync(IReadOnlyList<ResultColumn> columns, IAsyncEnumerable<IReadOnlyList<object?>> rows, Oranda.Core.Transfer.ExportOptions options, Stream destination, IProgress<long>? progress, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Oranda.Core.Transfer.TransferSummary> ImportAsync(Oranda.Core.Sessions.ISession s, Oranda.Core.Transfer.ImportOptions options, Stream source, IProgress<long>? progress, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Fetched_all_export_uses_edited_values_of_visible_rows_and_leaves_out_new_rows()
    {
        await using var m = await OpenAsync(30);
        await m.SetLocalFilterAsync(new LocalFilterSpec { QuickText = "g2" });
        m.EditOverlay = new EditedOverlay(extraRows: 1, viewRow: 1, ordinal: 1, value: "EDITED");

        Assert.Equal(5, m.RowCount); // 보이는 4행 + 거르기와 무관한 새 행
        Assert.Equal(4, m.BaseRowCount);
        Assert.Equal(-1, m.BaseRowOf(4));

        var rows = await Drain(ExcelExportPlan.FromGrid(m, ExcelExportScope.FetchedAll).Rows);
        Assert.Equal(30, rows.Count);
        Assert.Equal("EDITED", rows.Single(r => Equals(r[0], 9m))[1]);
        Assert.DoesNotContain(rows, r => Equals(r[1], "EDITED") && !Equals(r[0], 9m));
    }

    [Fact]
    public async Task Row_viewer_moves_over_visible_rows_only()
    {
        await using var m = await OpenAsync(30);
        await m.SetLocalFilterAsync(new LocalFilterSpec { QuickText = "g2" }); // ID 2, 9, 16, 23
        using var vm = new RowViewerViewModel();

        vm.Attach(m);
        Assert.Equal(0, vm.Row);
        Assert.Equal("2", vm.Fields.Single(f => f.Name == "ID").Text);

        vm.MoveRow(1);
        await WaitAsync(() => vm.Row == 1 && vm.Fields.Single(f => f.Name == "ID").Text == "9");
        vm.MoveLast();
        await WaitAsync(() => vm.Row == 3 && vm.Fields.Single(f => f.Name == "ID").Text == "23");
        Assert.False(vm.CanGoForward);
        Assert.Contains("/ 4", vm.HeaderText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flash_by_key_marks_the_visible_row_and_skips_rows_the_filter_hides()
    {
        await using var m = await OpenAsync(30);
        await m.SetLocalFilterAsync(new LocalFilterSpec { QuickText = "g2" }); // ID 2, 9, 16, 23

        m.FlashRowsByKey([new Dictionary<string, object?> { ["ID"] = 16m }, new Dictionary<string, object?> { ["ID"] = 5m }]);

        Assert.True(m.IsFlashing(2)); // 16 은 보이는 셋째 줄
        Assert.False(m.IsFlashing(0));
        Assert.False(m.IsFlashing(1));
        Assert.False(m.IsFlashing(3));
    }

    private static async Task<List<IReadOnlyList<object?>>> Drain(IAsyncEnumerable<IReadOnlyList<object?>> rows)
    {
        var list = new List<IReadOnlyList<object?>>();
        await foreach (var r in rows)
        {
            list.Add(r);
        }

        return list;
    }

    /// <summary>화면 행 하나의 칸을 바꾼 것으로 보이게 하고 새 행을 더하는 편집 대역.</summary>
    private sealed class EditedOverlay(int extraRows, long viewRow, int ordinal, object? value) : IGridEditOverlay
    {
        public int ExtraRows => extraRows;

        public bool HasPendingChanges => true;

        public bool TryGetValue(long row, int col, out object? v)
        {
            v = value;
            return row == viewRow && col == ordinal;
        }

        public bool IsEdited(long row, int col) => row == viewRow && col == ordinal;

        public GridRowMark RowMark(long row) => GridRowMark.None;

        public long BaseRowOf(long row) => row < 4 ? row : -1;

        public bool NeedsChange(long row, int col) => false;
    }

    private sealed class PendingOverlay : IGridEditOverlay
    {
        public int ExtraRows => 0;

        public bool HasPendingChanges => true;

        public bool TryGetValue(long viewRow, int ordinal, out object? value)
        {
            value = null;
            return false;
        }

        public bool IsEdited(long viewRow, int ordinal) => false;

        public GridRowMark RowMark(long viewRow) => GridRowMark.None;

        public long BaseRowOf(long viewRow) => viewRow;

        public bool NeedsChange(long viewRow, int ordinal) => false;
    }

    /// <summary>조건 안에서 보는 목록(예: 행 보기 필드)이 다른 스레드에서 다시 채워지는 중이면 아직 아닌 것으로 본다.</summary>
    private static bool Holds(Func<bool> cond)
    {
        try
        {
            return cond();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task WaitAsync(Func<bool> cond)
    {
        var sw = Stopwatch.StartNew();
        while (!Holds(cond))
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "시간 초과");
            await Task.Delay(5);
        }
    }

    /// <summary>UI 스레드 대역: 한 스레드에서 올린 일을 차례로 돌리는 동기화 문맥. 틱이 얼마나 늦게 처리되는지로 멈춤을 잰다.</summary>
    private sealed class Pump : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

        public static async Task<TimeSpan> RunAsync(Func<Task<TimeSpan>> body)
        {
            var pump = new Pump();
            var result = new TaskCompletionSource<TimeSpan>();
            var thread = new Thread(() =>
            {
                SetSynchronizationContext(pump);
                pump.Post(async _ =>
                {
                    try
                    {
                        result.SetResult(await body());
                    }
                    catch (Exception e)
                    {
                        result.SetException(e);
                    }
                    finally
                    {
                        pump._queue.CompleteAdding();
                    }
                }, null);
                foreach (var (callback, state) in pump._queue.GetConsumingEnumerable())
                {
                    callback(state);
                }
            })
            { IsBackground = true };
            thread.Start();
            return await result.Task;
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            if (!_queue.IsAddingCompleted)
            {
                _queue.Add((d, state));
            }
        }

        public static Ticker StartTicker() => new(Current!);

        public sealed class Ticker : IDisposable
        {
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly List<TimeSpan> _handled = [];
            private readonly Timer _timer;

            public Ticker(SynchronizationContext ui) =>
                _timer = new Timer(_ => ui.Post(__ => { lock (_handled) { _handled.Add(_clock.Elapsed); } }, null), null, 0, 10);

            public void Dispose() => _timer.Dispose();

            public TimeSpan MaxGap()
            {
                _timer.Dispose();
                lock (_handled)
                {
                    var gap = TimeSpan.Zero;
                    for (var i = 1; i < _handled.Count; i++)
                    {
                        gap = TimeSpan.FromTicks(Math.Max(gap.Ticks, (_handled[i] - _handled[i - 1]).Ticks));
                    }

                    return gap;
                }
            }
        }
    }
}
