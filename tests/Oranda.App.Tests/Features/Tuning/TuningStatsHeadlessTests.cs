using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Tuning.Cursors;
using Oranda.App.Features.Tuning.Stats;
using Oranda.App.Shell.Controls;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.Tuning;

/// <summary>통계 보기·자식 커서 화면(Headless): 상태마다 보이는 것, 표 칸, 막대, 체크. <c>ORANDA_RENDER_DIR</c> 가 있으면 라이트·다크 PNG.</summary>
public class TuningStatsHeadlessTests
{
    [Fact]
    public Task Stats_view_shows_summary_tables_bars_and_histogram() => HeadlessHost.Run(async () =>
    {
        var vm = new TableStatsViewModel(TuningSamples.LotHist()) { Clock = () => TuningSamples.Now };
        vm.Attach(new TuningFakeSession(), new TableStatsTarget("MES", "LOT_HIST"));
        var view = new TableStatsView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 720 };
        window.Show();
        try
        {
            await vm.LoadAsync();
            vm.SelectedColumn = vm.Columns.Single(c => c.Name == "EVT_CD");
            await HeadlessHost.WaitUntil(() => vm.HistogramBars.Count == 6);
            Pump();
            Assert.True(view.FindControl<Control>("ContentGrid")!.IsVisible);
            Assert.False(view.FindControl<Control>("Hint")!.IsVisible);
            var columns = view.FindControl<DetailTable>("ColumnTable")!;
            Assert.Equal(9, columns.Rows!.Count);
            Assert.Equal("EVT_CD", columns.CurrentRow![0]); // 고른 열이 ▶
            var indexes = view.FindControl<DetailTable>("IndexTable")!;
            Assert.Equal(3, indexes.Rows!.Count);
            var bars = indexes.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("bar")).ToList();
            Assert.Contains(bars, b => b.Classes.Contains("good"));
            Assert.Contains(bars, b => b.Classes.Contains("poor"));
            var histogram = view.FindControl<ItemsControl>("HistogramList")!;
            Assert.Equal(6, histogram.GetVisualDescendants().OfType<ProgressBar>().Count());
            Assert.True(view.FindControl<Control>("HistogramWarningBox")!.IsVisible);
            Assert.Contains(view.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("chip") && b.Classes.Contains("danger") && b.IsEffectivelyVisible);
            var script = view.FindControl<Button>("ScriptButton")!;
            Assert.Contains("emphasis", script.Classes);
            var label = script.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "통계 수집 스크립트 만들기");
            Assert.Equal(Avalonia.Media.FontWeight.SemiBold, label.FontWeight); // 통계 없음·stale 이면 경고 글자로 강조
            Render(window, "stats-ready", 1280, 720);

            // 히스토그램 없는 열
            await vm.LoadHistogramAsync(vm.Columns.Single(c => c.Name == "HIST_SEQ"));
            Pump();
            Assert.True(view.FindControl<Control>("HistogramHintText")!.IsVisible);
            Render(window, "stats-no-histogram", 1280, 720);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Stats_view_states_have_their_own_screens() => HeadlessHost.Run(async () =>
    {
        var vm = new TableStatsViewModel(TuningSamples.LotHist()) { Clock = () => TuningSamples.Now };
        var view = new TableStatsView { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 520 };
        window.Show();
        try
        {
            Pump();
            Assert.True(view.FindControl<Control>("Hint")!.IsVisible);
            Assert.Equal("접속 없음", view.FindControl<TextBlock>("HintTitleText")!.Text);
            Render(window, "stats-no-session", 1000, 520);

            vm.Attach(new TuningFakeSession(), null);
            Pump();
            Assert.Equal("통계를 볼 표를 고르세요", view.FindControl<TextBlock>("HintTitleText")!.Text);
            Render(window, "stats-no-target", 1000, 520);

            var service = TuningSamples.LotHist();
            service.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var loading = new TableStatsViewModel(service) { Clock = () => TuningSamples.Now };
            loading.Attach(new TuningFakeSession(), new TableStatsTarget("MES", "LOT_HIST"));
            view.DataContext = loading;
            var pending = loading.LoadAsync();
            Pump();
            Assert.Equal("통계를 읽는 중…", view.FindControl<TextBlock>("HintTitleText")!.Text);
            Render(window, "stats-loading", 1000, 520);
            service.Gate.SetResult();
            await pending;

            var denied = new TableStatsViewModel(new FakeTuningService());
            denied.Attach(new TuningFakeSession(), new TableStatsTarget("HR", "SALARY"));
            view.DataContext = denied;
            await denied.LoadAsync();
            Pump();
            Assert.Equal("이 계정으로 볼 수 없는 표", view.FindControl<TextBlock>("HintTitleText")!.Text);
            Render(window, "stats-denied", 1000, 520);

            var failing = new FakeTuningService { Failure = new InvalidOperationException("ORA-03113: end-of-file on communication channel") };
            var error = new TableStatsViewModel(failing);
            error.Attach(new TuningFakeSession(), new TableStatsTarget("MES", "LOT_HIST"));
            view.DataContext = error;
            await error.LoadAsync();
            Pump();
            Assert.Equal("통계를 읽지 못했습니다", view.FindControl<TextBlock>("HintTitleText")!.Text);
            Render(window, "stats-error", 1000, 520);

            var none = TuningSamples.LotHist(stale: false);
            none.Table = new Oranda.Core.Tuning.TableStatsRow("MES", "LOT_NEW", null, null, null, null, false);
            none.Columns.Clear();
            none.Indexes.Clear();
            var noStats = new TableStatsViewModel(none) { Clock = () => TuningSamples.Now };
            noStats.Attach(new TuningFakeSession(dba: false), new TableStatsTarget("MES", "LOT_NEW"));
            view.DataContext = noStats;
            await noStats.LoadAsync();
            Pump();
            Assert.True(view.FindControl<Control>("NoIndexText")!.IsVisible);
            Assert.Contains(view.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("chip") && b.Classes.Contains("warning") && b.IsEffectivelyVisible);
            Render(window, "stats-no-stats", 1000, 520);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Child_cursor_view_shows_children_reasons_and_the_compare() => HeadlessHost.Run(async () =>
    {
        var vm = new ChildCursorsViewModel(TuningSamples.Cursors())
        {
            SqlTextLoader = (_, _, _) => Task.FromResult<string?>("SELECT lh.lot_id, lh.evt_time, lh.evt_cd, lm.prod_id, lm.qty FROM mes.lot_hist lh JOIN mes.lot_mst lm ON lm.lot_id = lh.lot_id WHERE lh.eqp_id = :eqp AND lh.evt_time >= :since ORDER BY lh.evt_time"),
        };
        vm.Attach(new TuningFakeSession(), "7h35uxf5uhmm1");
        var view = new ChildCursorsView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 720 };
        window.Show();
        try
        {
            await vm.LoadAsync();
            Pump();
            var table = view.FindControl<DetailTable>("ChildTable")!;
            Assert.Equal(4, table.Rows!.Count);
            var boxes = table.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible).ToList();
            Assert.Equal(4, boxes.Select(b => b.DataContext).OfType<ChildCursorItem>().Distinct().Count());
            Assert.Equal(2, boxes.Where(b => b.IsChecked == true).Select(b => b.DataContext).Distinct().Count());
            Assert.True(view.FindControl<Control>("CompareBanner")!.IsVisible);
            Assert.True(view.FindControl<ListBox>("CompareList")!.ItemCount > 0);
            Assert.Equal(6, view.FindControl<DetailTable>("ReasonTable")!.Rows!.Count);
            Render(window, "cursors-ready", 1280, 720);

            // 체크 상자로 셋째를 고르면 가장 오래된 체크가 풀린다.
            var third = boxes.First(b => b.DataContext is ChildCursorItem { Row.ChildNumber: 3 });
            third.IsChecked = true;
            await HeadlessHost.WaitUntil(() => vm.CompareSummary.Contains("child 3", StringComparison.Ordinal));
            Pump();
            Assert.False(vm.Items[0].IsCompared);
            Render(window, "cursors-same-plan", 1280, 720);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Child_cursor_view_states() => HeadlessHost.Run(async () =>
    {
        var vm = new ChildCursorsViewModel(TuningSamples.Cursors());
        vm.Attach(new TuningFakeSession(), null);
        var view = new ChildCursorsView { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 520 };
        window.Show();
        try
        {
            Pump();
            Assert.Equal("sql_id 를 넣으세요", view.FindControl<TextBlock>("HintTitleText")!.Text);
            Render(window, "cursors-no-target", 1000, 520);

            var aged = new ChildCursorsViewModel(new FakeTuningService());
            aged.Attach(new TuningFakeSession(), "7h35uxf5uhmm1");
            view.DataContext = aged;
            await aged.LoadAsync();
            Pump();
            Assert.Equal("공유 풀에서 사라졌습니다", view.FindControl<TextBlock>("HintTitleText")!.Text);
            Render(window, "cursors-aged-out", 1000, 520);

            var denied = new ChildCursorsViewModel(TuningSamples.Cursors());
            denied.Attach(new TuningFakeSession(dba: false, perfViews: false, user: "app"), "7h35uxf5uhmm1");
            view.DataContext = denied;
            await denied.LoadAsync();
            Pump();
            Assert.True(view.FindControl<Control>("AccessBanner")!.IsVisible);
            Render(window, "cursors-denied", 1000, 520);

            var single = TuningSamples.Cursors();
            single.Children.RemoveRange(1, 3);
            single.Reasons.Clear();
            var one = new ChildCursorsViewModel(single);
            one.Attach(new TuningFakeSession(), "7h35uxf5uhmm1");
            view.DataContext = one;
            await one.LoadAsync();
            Pump();
            Assert.True(view.FindControl<Control>("ReasonsHintText")!.IsVisible);
            Assert.True(view.FindControl<Control>("CompareHintBox")!.IsVisible);
            Assert.False(view.FindControl<Button>("CompareButton")!.IsEnabled);
            Render(window, "cursors-single", 1000, 520);
        }
        finally
        {
            window.Close();
        }
    });

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>라이트·다크 PNG 를 <c>ORANDA_RENDER_DIR</c> 아래에 쓴다(없으면 아무것도 안 한다).</summary>
    private static void Render(Window window, string name, int width, int height)
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (variant, tone) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Pump();
                window.UpdateLayout();
                using var bmp = new RenderTargetBitmap(new PixelSize(width, height));
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"{name}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Pump();
        }
    }
}
