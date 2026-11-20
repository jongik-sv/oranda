using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;

namespace Oranda.App.Tests.Features.SchemaBrowser;

/// <summary>스키마 브라우저 속성 창의 열 통계 요약(열 노드를 고를 때만, 테이블당 한 번·히스토그램은 열당 한 번 읽음).</summary>
public class SchemaColumnStatsTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");
    private static readonly DateTime Analyzed = new(2026, 11, 1, 9, 30, 0);

    private static FakeTuning NewTuning()
    {
        var t = new FakeTuning();
        t.Columns["SCOTT.EMP"] =
        [
            new ColumnStatsRow("EMPNO", 14, 0, 0.07, "NONE", 1, Analyzed) { LowValue = "7369", HighValue = "7934" },
            new ColumnStatsRow("ENAME", 14, 2, 0.07, "FREQUENCY", 3, Analyzed) { LowValue = "ADAMS", HighValue = "WARD" },
            new ColumnStatsRow("HIDDEN", null, null, null, "NONE", null, null),
        ];
        t.Histograms["ENAME"] = [new(6, "ADAMS", 6), new(10, "KING", 4), new(12, "WARD", 2)];
        return t;
    }

    private static SchemaColumnStatsViewModel NewVm(FakeTuning tuning, ISession? session = null)
    {
        var s = session ?? new SchemaBrowserTests.TestSession();
        return new SchemaColumnStatsViewModel(() => tuning, () => s) { Delay = TimeSpan.Zero };
    }

    [Fact]
    public async Task Column_shows_counts_low_high_histogram_and_gathered_time()
    {
        var tuning = NewTuning();
        using var vm = NewVm(tuning);

        await vm.ShowAsync(Emp, "ENAME");

        Assert.True(vm.IsVisible);
        Assert.False(vm.HasMessage);
        Assert.Equal(
            [("고유 값", "14"), ("NULL", "2"), ("최솟값", "ADAMS"), ("최댓값", "WARD"), ("히스토그램", "FREQUENCY"), ("버킷", "3"), ("마지막 수집", "2026-11-01 09:30")],
            vm.Lines.Select(l => (l.Label, l.Value)));
        Assert.Equal("FREQUENCY · 3 버킷", vm.HistogramCaption);
        Assert.Equal(["ADAMS", "KING", "WARD"], vm.HistogramBars.Select(b => b.Label));
        Assert.Equal(1, vm.HistogramBars[0].Fraction);
        Assert.Equal(0.5, vm.HistogramBars[0].Share);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Column_stats_are_read_once_per_table_and_histograms_once_per_column()
    {
        var tuning = NewTuning();
        using var vm = NewVm(tuning);

        await vm.ShowAsync(Emp, "EMPNO");
        await vm.ShowAsync(Emp, "ENAME");
        await vm.ShowAsync(Emp, "EMPNO");
        await vm.ShowAsync(Emp, "ENAME");

        Assert.Equal(["columns SCOTT.EMP", "histogram ENAME"], tuning.Calls); // 히스토그램이 없는 열(EMPNO)은 읽지 않는다
        Assert.Equal("히스토그램 없음", (await ShowAndReturn(vm, "EMPNO")).HistogramCaption);
        Assert.Empty(vm.HistogramBars);

        await vm.ShowAsync(new DbObjectRef("SCOTT", "DEPT", "TABLE"), "DEPTNO");
        Assert.Equal("columns SCOTT.DEPT", tuning.Calls[^1]);
    }

    [Fact]
    public async Task Column_without_statistics_says_gather_is_needed()
    {
        using var vm = NewVm(NewTuning());

        await vm.ShowAsync(Emp, "HIDDEN");
        Assert.Equal("통계 없음 — 통계 수집 필요", vm.Message);
        Assert.Empty(vm.Lines);

        await vm.ShowAsync(Emp, "NOT_THERE");
        Assert.Equal("통계 없음 — 통계 수집 필요", vm.Message);
    }

    [Fact]
    public async Task Busy_session_or_missing_view_shows_one_quiet_line_and_reads_again_next_time()
    {
        var tuning = NewTuning();
        tuning.Failure = new SessionBusyException();
        using var vm = NewVm(tuning);

        await vm.ShowAsync(Emp, "ENAME");
        Assert.Equal("통계를 읽지 못했습니다", vm.Message);
        Assert.Empty(vm.Lines);
        Assert.False(vm.IsBusy);

        tuning.Failure = new InvalidOperationException("ORA-00942: table or view does not exist");
        await vm.ShowAsync(Emp, "EMPNO");
        Assert.Equal("통계를 읽지 못했습니다", vm.Message);

        tuning.Failure = null;
        await vm.ShowAsync(Emp, "EMPNO");
        Assert.False(vm.HasMessage);
        Assert.Equal(3, tuning.Calls.Count(c => c == "columns SCOTT.EMP")); // 실패는 두지 않는다
    }

    [Fact]
    public async Task Histogram_failure_keeps_the_summary()
    {
        var tuning = NewTuning();
        tuning.HistogramFailure = new SessionBusyException();
        using var vm = NewVm(tuning);

        await vm.ShowAsync(Emp, "ENAME");

        Assert.Equal("ADAMS", vm.Lines[2].Value);
        Assert.Equal("FREQUENCY · 3 버킷 · 히스토그램을 읽지 못했습니다", vm.HistogramCaption);
        Assert.Empty(vm.HistogramBars);
    }

    [Fact]
    public async Task Picking_another_column_quickly_cancels_the_earlier_read()
    {
        var tuning = NewTuning();
        var gate = new TaskCompletionSource();
        tuning.Gate = gate;
        using var vm = NewVm(tuning);

        var first = vm.ShowAsync(Emp, "ENAME");
        Assert.True(vm.IsBusy);
        Assert.Equal("통계를 읽는 중…", vm.Message);
        Assert.True(tuning.LastToken.CanBeCanceled);
        var firstToken = tuning.LastToken;

        tuning.Gate = null;
        await vm.ShowAsync(Emp, "EMPNO");
        await first;

        Assert.True(firstToken.IsCancellationRequested);
        Assert.Equal("7369", vm.Lines[2].Value); // 늦게 끝난 앞 요청이 화면을 덮지 않는다
        Assert.Equal(2, tuning.Calls.Count(c => c == "columns SCOTT.EMP"));
    }

    [Fact]
    public async Task Hide_cancels_and_a_new_session_drops_the_cache()
    {
        var tuning = NewTuning();
        var session = new SchemaBrowserTests.TestSession();
        ISession current = session;
        using var vm = new SchemaColumnStatsViewModel(() => tuning, () => current) { Delay = TimeSpan.Zero };

        await vm.ShowAsync(Emp, "EMPNO");
        vm.Hide();
        Assert.False(vm.IsVisible);

        current = new SchemaBrowserTests.TestSession();
        await vm.ShowAsync(Emp, "EMPNO");
        Assert.Equal(2, tuning.Calls.Count(c => c == "columns SCOTT.EMP"));

        vm.ClearCache();
        await vm.ShowAsync(Emp, "EMPNO");
        Assert.Equal(3, tuning.Calls.Count(c => c == "columns SCOTT.EMP"));
    }

    [Fact]
    public async Task Without_tuning_service_or_session_it_asks_to_connect()
    {
        using var vm = new SchemaColumnStatsViewModel(() => null, () => null) { Delay = TimeSpan.Zero };
        await vm.ShowAsync(Emp, "EMPNO");
        Assert.Equal("접속하면 열 통계를 볼 수 있습니다.", vm.Message);
    }

    [Fact]
    public async Task Tree_reads_nothing_until_a_column_node_is_picked_then_the_properties_show_its_summary()
    {
        var h = new SchemaBrowserTests.Harness();
        var tuning = NewTuning();
        h.Tuning = tuning;
        h.Vm.Properties.Delay = TimeSpan.Zero;
        h.Vm.Properties.ColumnStats.Delay = TimeSpan.Zero;
        await h.Vm.SetSessionAsync(h.Session);
        var schema = h.Vm.Roots.First(r => r.Title == "SCOTT");
        await h.Vm.ExpandAsync(schema);
        var tables = schema.Children.First(c => c.Title.StartsWith("테이블", StringComparison.Ordinal));
        await h.Vm.ExpandAsync(tables);
        var emp = tables.Children.First(c => c.Title == "EMP");
        await h.Vm.ExpandAsync(emp);
        var columns = emp.Children[0];
        await h.Vm.ExpandAsync(columns);
        Assert.Empty(tuning.Calls); // 펼치기만으로는 통계를 읽지 않는다

        h.Vm.SelectedNode = columns.Children.First(c => c.Title == "ENAME");
        await WaitUntil(() => tuning.Calls.Contains("histogram ENAME") && !h.Vm.Properties.ColumnStats.IsBusy);
        Assert.True(h.Vm.Properties.ColumnStats.IsVisible);
        Assert.Equal("WARD", h.Vm.Properties.ColumnStats.Lines[3].Value);

        h.Vm.SelectedNode = columns.Children.First(c => c.Title == "EMPNO");
        await WaitUntil(() => h.Vm.Properties.ColumnStats.Lines is [_, _, { Value: "7369" }, ..]);
        Assert.Equal(1, tuning.Calls.Count(c => c.StartsWith("columns", StringComparison.Ordinal)));

        h.Vm.SelectedNode = columns; // 폴더를 고르면 요약을 감추고 탭으로 돌아간다
        Assert.False(h.Vm.Properties.ColumnStats.IsVisible);

        h.Vm.SelectedNode = columns.Children.First(c => c.Title == "EMPNO");
        h.Vm.SelectedNode = emp; // 테이블을 고르면 요약을 감춘다
        Assert.False(h.Vm.Properties.ColumnStats.IsVisible);
    }

    [Fact]
    public Task Properties_view_swaps_the_tabs_for_the_column_summary_and_draws_the_bars() => HeadlessHost.Run(async () =>
    {
        var tuning = NewTuning();
        var session = new SchemaBrowserTests.TestSession();
        using var props = new SchemaPropertiesViewModel(() => null, () => session, () => tuning) { Delay = TimeSpan.Zero };
        props.ColumnStats.Delay = TimeSpan.Zero;
        var view = new SchemaPropertiesView { DataContext = props };
        var window = new Window { Width = 420, Height = 360, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tabs = view.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "PropertyTabs");
        var panel = view.GetVisualDescendants().OfType<ScrollViewer>().Single(t => t.Name == "ColumnStatsPanel");
        Assert.True(tabs.IsVisible);
        Assert.False(panel.IsVisible);

        await props.ColumnStats.ShowAsync(Emp, "ENAME");
        Dispatcher.UIThread.RunJobs();

        Assert.False(tabs.IsVisible);
        Assert.True(panel.IsVisible);
        var texts = panel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("최솟값", texts);
        Assert.Contains("ADAMS", texts);
        Assert.Contains("FREQUENCY · 3 버킷", texts);
        Assert.Equal(3, panel.GetVisualDescendants().OfType<ProgressBar>().Count(b => b.Classes.Contains("hbar")));

        await props.ShowAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(tabs.IsVisible);
        Assert.False(panel.IsVisible);
        window.Close();
    });

    private static async Task<SchemaColumnStatsViewModel> ShowAndReturn(SchemaColumnStatsViewModel vm, string column)
    {
        await vm.ShowAsync(Emp, column);
        return vm;
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(done());
    }

    private sealed class FakeTuning : ITuningService
    {
        public Dictionary<string, IReadOnlyList<ColumnStatsRow>> Columns { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, IReadOnlyList<HistogramBucket>> Histograms { get; } = new(StringComparer.Ordinal);

        public List<string> Calls { get; } = [];

        public Exception? Failure { get; set; }

        public Exception? HistogramFailure { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public CancellationToken LastToken { get; private set; }

        public async Task<IReadOnlyList<ColumnStatsRow>> GetColumnStatsAsync(ISession s, string owner, string table, CancellationToken ct)
        {
            Calls.Add($"columns {owner}.{table}");
            LastToken = ct;
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(ct);
            }

            if (Failure is { } f)
            {
                throw f;
            }

            return Columns.TryGetValue($"{owner}.{table}", out var rows) ? rows : [];
        }

        public Task<IReadOnlyList<HistogramBucket>> GetHistogramAsync(ISession s, string owner, string table, string column, CancellationToken ct)
        {
            Calls.Add("histogram " + column);
            if (HistogramFailure is { } f)
            {
                throw f;
            }

            return Task.FromResult(Histograms.TryGetValue(column, out var b) ? b : (IReadOnlyList<HistogramBucket>)[]);
        }
    }
}
