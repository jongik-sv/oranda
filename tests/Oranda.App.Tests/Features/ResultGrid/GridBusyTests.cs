using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Shell;
using Oranda.App.Tests.Features.MyBatisWorkspace.Flow;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Tests.Features.ResultGrid;

/// <summary>
/// 결과 위쪽의 얇은 진행 막대(실행 중 표시)와 상태 줄 「모두 가져오기」·「가져오기 취소」 단추:
/// 막대는 가져오는 동안 보이고 끝·오류·취소에 사라지며, 단추는 더 가져올 행이 있을 때만 보인다.
/// </summary>
[Collection("GridEditStatics")]
public sealed class GridBusyTests : IDisposable
{
    private static readonly ResultColumn[] Cols =
    [
        new(0, "ID", "NUMBER", ColumnDataKind.Number),
        new(1, "NAME", "VARCHAR2", ColumnDataKind.Text),
    ];

    private readonly Func<string, Task>? _oldRunner = ShellCommandRunner.Runner;
    private readonly ResultGridModel? _oldActive = ResultGridCommands.Active;

    public void Dispose()
    {
        ShellCommandRunner.Runner = _oldRunner;
        ResultGridCommands.Active = _oldActive;
        ResultGridCommands.Filter = null;
    }

    /// <summary>0쪽은 바로, 그 뒤 쪽은 문(Gate)을 열 때까지 기다린다. Fail 이면 문이 열릴 때 예외.</summary>
    private sealed class GatedSet(int rows, int pageSize = 500) : IResultSet
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<ResultColumn> Columns => Cols;

        public bool IsComplete => false;

        public long? KnownRowCount => null;

        public async Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            if (pageIndex > 0)
            {
                await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            var first = pageIndex * pageSize;
            var count = Math.Clamp(rows - first, 0, pageSize);
            var ids = new object?[count];
            var names = new object?[count];
            for (var i = 0; i < count; i++)
            {
                ids[i] = (decimal)(first + i);
                names[i] = "N" + (first + i);
            }

            return new ResultPage(pageIndex, first, count, [ids, names]) { IsLast = first + count >= rows };
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<(SqlResultPaneViewModel Pane, GatedSet Set)> OpenAsync(int rows = 1200)
    {
        var set = new GatedSet(rows);
        var pane = new SqlResultPaneViewModel { BusyBarDelay = TimeSpan.Zero };
        pane.SetResult(set);
        await pane.Model!.LoadFirstPageAsync();
        return (pane, set);
    }

    [Fact]
    public Task Bar_shows_while_fetching_all_and_hides_when_done() => HeadlessHost.Run(async () =>
    {
        var (pane, set) = await OpenAsync();
        Assert.False(pane.IsBusy);
        Assert.False(pane.IsBusyBarVisible);
        Assert.True(pane.CanFetchAll);

        var fetch = pane.Model!.FetchAllAsync();
        Assert.True(pane.IsBusy);
        Assert.True(pane.IsBusyBarVisible);
        Assert.True(pane.IsFetchingAll);
        Assert.False(pane.CanFetchAll); // 같은 자리에 「취소」가 보인다

        set.Gate.SetResult();
        await fetch;

        Assert.False(pane.IsBusy);
        Assert.False(pane.IsBusyBarVisible);
        Assert.False(pane.CanFetchAll); // 다 가져오면 단추가 사라진다
        Assert.Equal(1200, pane.Model.LoadedRowCount);
        Assert.Contains("1,200", pane.StatusText, StringComparison.Ordinal);
    });

    [Fact]
    public Task Bar_shows_for_a_background_page_fetch_and_hides_after() => HeadlessHost.Run(async () =>
    {
        var (pane, set) = await OpenAsync();
        var model = pane.Model!;
        // 화면 끝 근처를 읽으면 다음 쪽을 미리 받는다.
        model.Prefetch(400, 460);
        Assert.True(pane.IsBusyBarVisible);

        set.Gate.SetResult();
        await HeadlessHost.WaitUntil(() => !pane.IsBusyBarVisible && model.LoadedRowCount > 500);
        Assert.False(pane.IsBusy);
    });

    [Fact]
    public Task Bar_hides_when_the_fetch_fails() => HeadlessHost.Run(async () =>
    {
        var (pane, set) = await OpenAsync();
        var fetch = pane.Model!.FetchAllAsync();
        Assert.True(pane.IsBusyBarVisible);

        set.Gate.SetException(new InvalidOperationException("ORA-03113"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fetch);

        Assert.False(pane.IsBusy);
        Assert.False(pane.IsBusyBarVisible);
        Assert.False(pane.IsFetchingAll);
        Assert.True(pane.CanFetchAll); // 실패 뒤 다시 누를 수 있다
    });

    [Fact]
    public Task Cancel_stops_the_fetch_keeps_the_rows_and_hides_the_bar() => HeadlessHost.Run(async () =>
    {
        var (pane, _) = await OpenAsync();
        var fetch = pane.Model!.FetchAllAsync();
        Assert.True(pane.IsFetchingAll);

        pane.CancelFetchAll();
        await fetch; // 단추로 멈춘 것은 예외 없이 끝난다

        Assert.False(pane.IsBusyBarVisible);
        Assert.False(pane.IsFetchingAll);
        Assert.Equal(500, pane.Model.LoadedRowCount);
        Assert.True(pane.CanFetchAll);
    });

    [Fact]
    public Task An_outer_cancel_still_surfaces_as_cancellation() => HeadlessHost.Run(async () =>
    {
        var (pane, _) = await OpenAsync();
        using var cts = new CancellationTokenSource();
        var fetch = pane.Model!.FetchAllAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        Assert.False(pane.IsBusyBarVisible);
    });

    [Fact]
    public Task Bar_waits_for_the_delay_so_short_queries_do_not_flash() => HeadlessHost.Run(async () =>
    {
        var (pane, set) = await OpenAsync();
        pane.BusyBarDelay = TimeSpan.FromMilliseconds(150);

        var quick = pane.Model!.FetchAllAsync();
        Assert.True(pane.IsBusy);
        Assert.False(pane.IsBusyBarVisible); // 지연 안에서는 아직 안 보인다
        pane.CancelFetchAll();
        await quick;
        await Task.Delay(250);
        Assert.False(pane.IsBusyBarVisible); // 지연 안에 끝났으면 끝내 안 보인다

        var slow = pane.Model.FetchAllAsync();
        await HeadlessHost.WaitUntil(() => pane.IsBusyBarVisible, 30000);
        set.Gate.SetResult();
        await slow;
        Assert.False(pane.IsBusyBarVisible);
    });

    [Fact]
    public Task Fetch_all_button_shows_only_while_more_rows_remain() => HeadlessHost.Run(async () =>
    {
        var (pane, set) = await OpenAsync(300);
        Assert.False(pane.CanFetchAll); // 첫 쪽이 마지막이라 더 없다
        Assert.False(pane.Model!.HasMoreRows);

        var (partial, _) = await OpenAsync(1200);
        Assert.True(partial.CanFetchAll);
        Assert.Contains("더 있음", partial.StatusText, StringComparison.Ordinal);
        set.Gate.TrySetResult();
    });

    [Fact]
    public Task Server_requery_keeps_the_bar_on_until_it_returns_or_fails() => HeadlessHost.Run(async () =>
    {
        var (pane, _) = await OpenAsync();
        pane.Statement = new SqlStatement("SELECT id, name FROM t", default, StatementKind.Query) { Verb = "SELECT" };
        var gate = new TaskCompletionSource<IResultSet>(TaskCreationOptions.RunContinuationsAsynchronously);
        ResultGridCommands.Filter = (_, _) => gate.Task;

        var sort = pane.ServerSortAsync(1, false, CancellationToken.None);
        Assert.True(pane.IsBusyBarVisible);

        gate.SetException(new InvalidOperationException("boom"));
        await sort;
        Assert.False(pane.IsBusyBarVisible);
        Assert.False(pane.IsBusy);
    });

    [Fact]
    public void Result_tabs_run_bar_follows_IsRunning_and_waits_for_its_delay()
    {
        var tabs = new ResultTabsViewModel { RunBarDelay = TimeSpan.Zero };
        Assert.False(tabs.IsRunBarVisible);
        tabs.IsRunning = true;
        Assert.True(tabs.IsRunBarVisible);
        tabs.IsRunning = false; // 끝·오류·취소 모두 같은 길
        Assert.False(tabs.IsRunBarVisible);
    }

    [Fact]
    public async Task Result_tabs_run_bar_stays_hidden_for_a_run_shorter_than_the_delay()
    {
        var tabs = new ResultTabsViewModel { RunBarDelay = TimeSpan.FromMilliseconds(150) };
        tabs.IsRunning = true;
        Assert.False(tabs.IsRunBarVisible);
        tabs.IsRunning = false;
        await Task.Delay(250);
        Assert.False(tabs.IsRunBarVisible);

        tabs.IsRunning = true;
        await HeadlessHost.WaitUntil(() => tabs.IsRunBarVisible, 30000);
        tabs.IsRunning = false;
        Assert.False(tabs.IsRunBarVisible);
    }

    private static ProgressBar Bar(SqlResultPaneView view) => view.FindControl<ProgressBar>("BusyBar")!;

    [Fact]
    public Task View_shows_the_bar_and_buttons_and_the_button_runs_the_fetch_all_command() => HeadlessHost.Run(async () =>
    {
        var (pane, set) = await OpenAsync();
        var ran = new List<string>();
        ShellCommandRunner.Runner = id =>
        {
            ran.Add(id);
            return Task.CompletedTask;
        };
        var view = new SqlResultPaneView { DataContext = pane };
        var window = new Window { Width = 700, Height = 320, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var fetchAll = view.FindControl<Button>("FilterFetchAllButton")!;
            var cancel = view.FindControl<Button>("FetchCancelButton")!;
            Assert.True(fetchAll.IsVisible);
            Assert.False(cancel.IsVisible);
            Assert.False(Bar(view).IsVisible);
            Assert.Contains(fetchAll.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "모두 가져오기");
            Assert.NotNull(ToolTip.GetTip(fetchAll));
            MyBatisFlowRig.Render(window, "grid-busy-more-rows", 700, 320);

            fetchAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HeadlessHost.WaitUntil(() => ran.Count == 1);
            Assert.Equal("oranda.grid.fetchAll", ran[0]);

            // 명령이 도는 동안(모델이 모두 가져오는 중)
            var fetch = pane.Model!.FetchAllAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.True(Bar(view).IsVisible);
            Assert.Equal(2, Bar(view).Height);
            Assert.True(Bar(view).IsIndeterminate);
            Assert.False(fetchAll.IsVisible);
            Assert.True(cancel.IsVisible);
            // 정지 화면에는 흐르는 막대가 안 그려져 사진 때만 한쪽을 채워 위치·색을 본다.
            Bar(view).IsIndeterminate = false;
            Bar(view).Value = 40;
            MyBatisFlowRig.Render(window, "grid-busy-fetching", 700, 320);
            Bar(view).IsIndeterminate = true;

            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await fetch;
            Dispatcher.UIThread.RunJobs();
            Assert.False(Bar(view).IsVisible);
            Assert.False(cancel.IsVisible);
            Assert.True(fetchAll.IsVisible); // 취소하면 가져온 만큼만 남고 다시 누를 수 있다

            var second = pane.Model.FetchAllAsync();
            set.Gate.SetResult();
            await second;
            Dispatcher.UIThread.RunJobs();
            Assert.False(fetchAll.IsVisible);
            Assert.False(Bar(view).IsVisible);
            MyBatisFlowRig.Render(window, "grid-busy-complete", 700, 320);
        }
        finally
        {
            window.Close();
            pane.Clear();
        }
    });

    [Fact]
    public Task The_bar_overlaps_the_layout_and_does_not_push_the_grid_down() => HeadlessHost.Run(async () =>
    {
        var (pane, _) = await OpenAsync();
        var view = new SqlResultPaneView { DataContext = pane };
        var window = new Window { Width = 700, Height = 320, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var grid = view.FindControl<Control>("GridControl")!;
            var before = grid.TranslatePoint(default, window)!.Value.Y;
            var fetch = pane.Model!.FetchAllAsync();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(Bar(view).IsVisible);
            Assert.Equal(before, grid.TranslatePoint(default, window)!.Value.Y);
            pane.CancelFetchAll();
            await fetch;
        }
        finally
        {
            window.Close();
            pane.Clear();
        }
    });

    [Fact]
    public Task Result_tabs_view_shows_the_run_bar_while_running() => HeadlessHost.Run(() =>
    {
        var tabs = new ResultTabsViewModel { RunBarDelay = TimeSpan.Zero };
        var view = new ResultTabsView { DataContext = tabs };
        var window = new Window { Width = 700, Height = 260, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var bar = view.FindControl<ProgressBar>("RunBar")!;
            Assert.False(bar.IsVisible);
            tabs.IsRunning = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(bar.IsVisible);
            bar.IsIndeterminate = false;
            bar.Value = 40;
            MyBatisFlowRig.Render(window, "grid-busy-running", 700, 260);
            bar.IsIndeterminate = true;
            tabs.IsRunning = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(bar.IsVisible);
        }
        finally
        {
            window.Close();
        }
    });
}
