using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Ai.Kit;
using Oranda.App.Features.Flashback;
using Oranda.App.Features.Flashback.Ai;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Tests.Features.Ai.InPlace;
using Oranda.App.Tests.Features.Ai.Kit;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Tests.Features.Flashback.Ai;

/// <summary>플래시백 VERSIONS 결과 위 「차이 요약」(설계 §4.2): 버전 입력 만들기, 가벼운 일 요청, 결과 그리드 위 기능 자리.</summary>
public class FlashbackDiffSummaryScreenTests
{
    private const string VersionsSql =
        "SELECT VERSIONS_STARTTIME, VERSIONS_OPERATION, \"ID\", \"NAME\", \"SAL\"\n"
        + "FROM \"SCOTT\".\"EMP_T\" VERSIONS BETWEEN TIMESTAMP SYSDATE - 1 AND MAXVALUE\nWHERE \"ID\" = 7\nORDER BY VERSIONS_STARTSCN DESC NULLS LAST";

    private static readonly DateTime T0 = new(2026, 11, 18, 9, 0, 0);

    /// <summary>최신순 결과: 삭제 · SAL 바뀜 · NAME 바뀜 · 첫 버전.</summary>
    private sealed class History : IResultSet
    {
        private static readonly object?[][] Data =
        [
            [T0.AddMinutes(30), "D", 7m, "KIM", 3100m],
            [T0.AddMinutes(20), "U", 7m, "KIM", 3100m],
            [T0.AddMinutes(10), "U", 7m, "KIM", 3000m],
            [null, null, 7m, "LEE", 3000m],
        ];

        public IReadOnlyList<ResultColumn> Columns { get; } =
        [
            new(0, "VERSIONS_STARTTIME", "TIMESTAMP", ColumnDataKind.Timestamp),
            new(1, "VERSIONS_OPERATION", "VARCHAR2", ColumnDataKind.Text),
            new(2, "ID", "NUMBER", ColumnDataKind.Number),
            new(3, "NAME", "VARCHAR2", ColumnDataKind.Text),
            new(4, "SAL", "NUMBER", ColumnDataKind.Number),
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

    private static async Task<(SqlResultPaneViewModel Pane, VersionsHighlight Highlight)> OpenAsync()
    {
        var pane = new SqlResultPaneViewModel { Statement = new SqlStatement(VersionsSql, default, StatementKind.Query) { Verb = "SELECT" } };
        pane.SetResult(new History());
        await pane.Model!.LoadFirstPageAsync();
        return (pane, VersionsHighlight.Attach(pane.Model, [2], VersionsHighlight.VersionsSources(VersionsSql, pane.Model.Columns.Count), newestFirst: true));
    }

    private static (FlashbackDiffSummaryViewModel Vm, FakeDriver Driver) Summary(VersionsHighlight highlight, AiAccessLevel level, bool connected = true)
    {
        var profile = new ConnectionProfile("dev-example", "scott", "db:1521/ORCL", Id: Guid.NewGuid(), AiAccess: level);
        var driver = new FakeDriver { Answer = _ => KitFakes.Answer("## 요약\n이름과 급여가 바뀐 뒤 지워졌습니다.\n## 눈여겨볼 점\n- 삭제") };
        return (new FlashbackDiffSummaryViewModel(InPlaceFakes.Host(driver), () => highlight.SummaryInput("SCOTT.EMP_T"), () => connected ? profile : null), driver);
    }

    [Fact]
    public Task Versions_are_read_oldest_first_with_the_values_before_and_after() => HeadlessHost.Run(async () =>
    {
        var (_, highlight) = await OpenAsync();

        var input = highlight.SummaryInput("SCOTT.EMP_T");

        Assert.Equal("SCOTT.EMP_T", input.TableName);
        Assert.Equal(["ID"], input.KeyColumns);
        Assert.Equal(["", "U", "U", "D"], input.Versions.Select(v => v.Operation));
        Assert.Empty(input.Versions[0].Changes);
        Assert.Equal(new FlashbackVersionChange("NAME", "LEE", "KIM"), Assert.Single(input.Versions[1].Changes));
        Assert.Equal(new FlashbackVersionChange("SAL", "3000", "3100"), Assert.Single(input.Versions[2].Changes));
        Assert.All(input.Versions, v => Assert.Equal("ID=7", v.KeyText));
    });

    [Fact]
    public Task Summary_is_a_light_request_without_values_below_query_rows() => HeadlessHost.Run(async () =>
    {
        var (_, highlight) = await OpenAsync();
        var (vm, driver) = Summary(highlight, AiAccessLevel.Plan);

        await vm.Entry.RunCommand.ExecuteAsync(null);

        var request = Assert.Single(driver.Requests);
        Assert.Equal(AiTier.Light, request.Tier);
        Assert.Contains("NAME", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("LEE", request.Prompt, StringComparison.Ordinal);
        Assert.Equal(FlashbackDiffSummaryPrompt.NoValuesNote, vm.Note);
        Assert.Equal("이름과 급여가 바뀐 뒤 지워졌습니다.", vm.Strip.Answer!["요약"]!.Body);
    });

    [Fact]
    public Task Without_the_connection_the_entry_is_off() => HeadlessHost.Run(async () =>
    {
        var (_, highlight) = await OpenAsync();

        var entry = Summary(highlight, AiAccessLevel.Plan, connected: false).Vm.Entry;

        Assert.False(entry.IsEnabled);
        Assert.Equal(FlashbackDiffSummaryViewModel.NoConnectionText, entry.ToolTip);
    });

    [Fact]
    public Task The_result_pane_shows_the_summary_above_the_grid_and_clears_it_on_a_new_result() => HeadlessHost.Run(async () =>
    {
        var (pane, highlight) = await OpenAsync();
        var (vm, _) = Summary(highlight, AiAccessLevel.QueryRows);
        pane.FeatureStrip = vm;
        var view = new SqlResultPaneView { DataContext = pane };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var button = view.GetVisualDescendants().OfType<Button>().First(b => b.Name == "DiffSummaryButton");
        Assert.True(button.IsEffectivelyVisible);
        button.Command!.Execute(null);
        await HeadlessHost.WaitUntil(() => vm.Strip.State == AiStripState.Done);
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.GetVisualDescendants().OfType<AiInlineStrip>().Single(s => s.Name == "DiffSummaryStrip").IsEffectivelyVisible);

        pane.SetResult(new History());
        Dispatcher.UIThread.RunJobs();
        Assert.Null(pane.FeatureStrip);
        Assert.False(view.GetVisualDescendants().OfType<ContentControl>().Single(c => c.Name == "FeatureStripHost").IsVisible);
        window.Close();
    });
}
