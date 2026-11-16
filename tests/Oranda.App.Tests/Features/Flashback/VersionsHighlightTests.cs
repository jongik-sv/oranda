using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Features.Flashback;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Tests.Features.Flashback;

/// <summary>변경 이력(VERSIONS) 결과 그리드: 앞 버전과 값이 다른 데이터 칸만 바탕색. 의사열·키의 첫 버전·삭제 행은 칠하지 않고 일반 결과는 칠하지 않는다.</summary>
public sealed class VersionsHighlightTests
{
    private const string VersionsSql =
        "SELECT VERSIONS_STARTTIME, VERSIONS_ENDTIME, VERSIONS_OPERATION, RAWTOHEX(VERSIONS_XID) AS VERSIONS_XID, \"ID\", \"NAME\", \"SAL\", \"MEMO\"\n"
        + "FROM \"SCOTT\".\"EMP_T\" VERSIONS BETWEEN TIMESTAMP SYSDATE - 1.5 AND MAXVALUE\nWHERE \"ID\" = 7\nORDER BY VERSIONS_STARTSCN DESC NULLS LAST";

    private static readonly DateTime T0 = new(2026, 11, 16, 9, 0, 0);

    /// <summary>「이 행 변경 이력」 결과 모양(최신순). 0 = 삭제, 1·2 = 바뀜, 3 = 기간 앞부터 있던 첫 버전.</summary>
    private sealed class History : IResultSet
    {
        private static readonly object?[][] Data =
        [
            [T0.AddMinutes(30), null, "D", "0B00", 7m, "KIM", 3100m, "x"],
            [T0.AddMinutes(20), T0.AddMinutes(30), "U", "0A00", 7m, "KIM", 3100m, "x"],
            [T0.AddMinutes(10), T0.AddMinutes(20), "U", "0900", 7m, "KIM", 3000m, null],
            [null, T0.AddMinutes(10), null, null, 7m, "LEE", 3000m, null],
        ];

        public IReadOnlyList<ResultColumn> Columns { get; } =
        [
            new(0, "VERSIONS_STARTTIME", "TIMESTAMP", ColumnDataKind.Timestamp),
            new(1, "VERSIONS_ENDTIME", "TIMESTAMP", ColumnDataKind.Timestamp),
            new(2, "VERSIONS_OPERATION", "VARCHAR2", ColumnDataKind.Text),
            new(3, "VERSIONS_XID", "VARCHAR2", ColumnDataKind.Text),
            new(4, "ID", "NUMBER", ColumnDataKind.Number),
            new(5, "NAME", "VARCHAR2", ColumnDataKind.Text),
            new(6, "SAL", "NUMBER", ColumnDataKind.Number),
            new(7, "MEMO", "VARCHAR2", ColumnDataKind.Text),
        ];

        public bool IsComplete => true;

        public long? KnownRowCount => Data.Length;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            var n = pageIndex == 0 ? Data.Length : 0;
            IReadOnlyList<IReadOnlyList<object?>> cols = [.. Enumerable.Range(0, Columns.Count).Select(c => (IReadOnlyList<object?>)[.. Data.Take(n).Select(r => r[c])])];
            return Task.FromResult(new ResultPage(pageIndex, 0, n, cols) { IsLast = true });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static SqlStatement Query(string text) => new(text, default, StatementKind.Query) { Verb = "SELECT" };

    private static (SqlResultPaneViewModel Vm, ResultGridModel Model) Open(string text)
    {
        var vm = new SqlResultPaneViewModel { Statement = Query(text) };
        vm.SetResult(new History());
        return (vm, vm.Model!);
    }

    [Fact]
    public void Only_versions_queries_are_targets()
    {
        Assert.NotNull(VersionsHighlight.VersionsSources(VersionsSql, 8));
        Assert.Null(VersionsHighlight.VersionsSources("SELECT * FROM EMP_T AS OF TIMESTAMP SYSDATE - 1/24 WHERE ID = 7", 8));
        Assert.Null(VersionsHighlight.VersionsSources("SELECT * FROM EMP_T WHERE ID = 7", 8));
    }

    [Fact]
    public Task Changed_data_cells_get_the_background_and_others_do_not() => HeadlessHost.Run(async () =>
    {
        var (vm, m) = Open(VersionsSql);
        await m.LoadFirstPageAsync();
        VersionsHighlight.Attach(m, [4], VersionsHighlight.VersionsSources(VersionsSql, m.Columns.Count));

        bool[] Row(int r) => [.. Enumerable.Range(0, 8).Select(c => m.IsChangedCell(r, c))];
        Assert.All(Row(0), Assert.False);                                                 // 삭제 행
        Assert.Equal([false, false, false, false, false, false, true, true], Row(1)); // SAL 3000→3100, MEMO NULL→x
        Assert.Equal([false, false, false, false, false, true, false, false], Row(2)); // NAME LEE→KIM (의사열은 달라도 제외)
        Assert.All(Row(3), Assert.False);                                                 // 첫 버전
        Assert.Contains(VersionsHighlight.Notice, vm.StatusText, StringComparison.Ordinal); // 결과 요약 줄 안내

        // 화면 정렬을 바꿔도(오래된 순) 같은 행의 같은 칸이 칠해진다
        await m.SortAsync(0, descending: false);
        var oldest = Enumerable.Range(0, 4).Single(r => m.SourceRowOf(r) == 2);
        Assert.True(m.IsChangedCell(oldest, 5));
        Assert.False(m.IsChangedCell(oldest, 6));
    });

    [Fact]
    public Task Plain_result_has_no_background() => HeadlessHost.Run(async () =>
    {
        var (_, m) = Open("SELECT * FROM EMP_T WHERE ID = 7");
        await m.LoadFirstPageAsync();
        Assert.Null(m.CellChanged);
        Assert.False(m.IsChangedCell(1, 6));
    });

    [Fact]
    public Task Theme_brush_exists_in_light_and_dark() => HeadlessHost.Run(() =>
    {
        var app = Application.Current!;
        Assert.True(app.TryFindResource("OrandaGridVersionChangedBrush", ThemeVariant.Light, out var light) && light is ISolidColorBrush);
        Assert.True(app.TryFindResource("OrandaGridVersionChangedBrush", ThemeVariant.Dark, out var dark) && dark is ISolidColorBrush);
        var edited = app.TryFindResource("OrandaGridNeedsChangeBrush", ThemeVariant.Light, out var e) ? ((ISolidColorBrush)e!).Color : default;
        Assert.NotEqual(edited, ((ISolidColorBrush)light!).Color);
        Assert.NotEqual(((ISolidColorBrush)light).Color, ((ISolidColorBrush)dark!).Color);
        return Task.CompletedTask;
    });

    /// <summary>ORANDA_RENDER_DIR 를 줄 때만 실제 픽셀로 그려 변경 이력 결과의 바뀐 칸 바탕을 눈으로 확인한다(밝게·어둡게).</summary>
    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public Task Versions_grid_renders_for_a_visual_check(string tone) => HeadlessHost.Run(async () =>
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        var variant = tone == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        Application.Current!.RequestedThemeVariant = variant;
        var (vm, m) = Open(VersionsSql);
        var view = new SqlResultPaneView { DataContext = vm };
        var window = new Window { Content = view, Width = 1250, Height = 220, RequestedThemeVariant = variant };
        window.Show();
        try
        {
            await HeadlessHost.WaitUntil(() => m.LoadedRowCount == 4);
            VersionsHighlight.Attach(m, [4], VersionsHighlight.VersionsSources(VersionsSql, m.Columns.Count));
            var grid = view.FindControl<ResultGridControl>("GridControl")!;
            await HeadlessHost.WaitUntil(() => grid.Bounds.Height > 0);
            m.SelectCell(new GridCell(3, 4));
            Dispatcher.UIThread.RunJobs();

            Directory.CreateDirectory(dir);
            using var frame = HeadlessWindowExtensions.CaptureRenderedFrame(window)!;
#pragma warning disable CS0618 // 렌더 확인용
            frame.Save(Path.Combine(dir, $"history-diff-{tone}.png"));
#pragma warning restore CS0618
        }
        finally
        {
            window.Close();
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Dispatcher.UIThread.RunJobs();
        }
    });
}
