using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Features.Templates;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Layout;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell.Headless;

/// <summary>
/// D-048: 오른쪽 도구 창(템플릿·바인드 변수)은 SQL 편집기 옆이 아니라 [편집기 / 아래 결과 묶음] 열의 오른쪽에 붙어
/// 편집기 위끝부터 결과 영역 아래끝까지 세로 전체를 쓴다. 닫으면 그 열이 사라지고 편집기·결과가 넓어진다.
/// </summary>
public sealed class RightToolsFullHeightTests
{
    private const string Templates = "oranda.panel.templates";
    private const string Binds = "oranda.panel.binds";

    private static async Task OpenSqlTabAsync(ShellHarness h)
    {
        var wiring = h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
        await wiring.RunAsync(default);
        SqlEditorHost.Current.NewDocument("select 1 from dual");
        Dispatcher.UIThread.RunJobs();
    }

    private static Rect BoundsOf<T>(ShellHarness h)
        where T : Control
    {
        h.Window.UpdateLayout();
        var control = h.Window.GetVisualDescendants().OfType<T>().First(c => c.IsEffectivelyVisible);
        var topLeft = control.TranslatePoint(default, h.Window)!.Value;
        return new Rect(topLeft, control.Bounds.Size);
    }

    private static bool Shown<T>(ShellHarness h)
        where T : Control
    {
        h.Window.UpdateLayout();
        return h.Window.GetVisualDescendants().OfType<T>().Any(c => c.IsEffectivelyVisible);
    }

    [Fact]
    public Task Templates_panel_spans_from_the_editor_top_to_the_results_bottom() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        h.Window.Width = 1280;
        h.Window.Height = 800;
        var ws = h.Get<DockWorkspace>();
        await OpenSqlTabAsync(h);
        var editorBefore = BoundsOf<SqlWorkspaceView>(h);
        var resultsBefore = BoundsOf<ResultsPanelView>(h);

        ws.ShowTool(Templates);
        Dispatcher.UIThread.RunJobs();

        var editor = BoundsOf<SqlWorkspaceView>(h);
        var results = BoundsOf<ResultsPanelView>(h);
        var panel = BoundsOf<TemplatesView>(h);

        // 곁 창은 편집기·결과 둘 다의 오른쪽에 있고, 세로로는 둘을 합친 범위를 덮는다(도구 창 머리 띠 몫만큼 위가 조금 아래일 수 있다).
        Assert.True(panel.Left >= editor.Right - 1 && panel.Left >= results.Right - 1, $"panel {panel}, editor {editor}, results {results}");
        Assert.True(panel.Top <= editor.Top + 40, $"panel top {panel.Top} vs editor top {editor.Top}");
        Assert.True(panel.Bottom >= results.Bottom - 2, $"panel bottom {panel.Bottom} vs results bottom {results.Bottom}");
        Assert.True(Math.Abs(results.Right - editor.Right) < 2, $"결과 영역 폭이 편집기와 같아야 한다: {results} / {editor}");
        Assert.True(results.Width < resultsBefore.Width - 50, "결과 영역도 곁 창 몫만큼 좁아진다");

        var root = ws.Capture().Root!;
        Assert.Equal(DockOrientation.Horizontal, root.Orientation);
        Assert.Equal([Templates], root.Children[^1].Tools); // 맨 바깥 가로 분할의 마지막 칸
        Assert.Equal(DockLocation.Right, LayoutPlanner.LocationOf(root, root.Children[^1]));

        // 닫으면 열이 사라지고 편집기·결과가 처음 폭으로 돌아간다(왼쪽 창은 넓어지지 않는다).
        ws.HideTool(Templates);
        Dispatcher.UIThread.RunJobs();
        Assert.False(Shown<TemplatesView>(h));
        var editorAfter = BoundsOf<SqlWorkspaceView>(h);
        var resultsAfter = BoundsOf<ResultsPanelView>(h);
        Assert.True(Math.Abs(editorAfter.Left - editorBefore.Left) < 2, $"{editorAfter} / {editorBefore}");
        Assert.True(Math.Abs(editorAfter.Width - editorBefore.Width) < 2, $"{editorAfter} / {editorBefore}");
        Assert.True(Math.Abs(resultsAfter.Width - resultsBefore.Width) < 2, $"{resultsAfter} / {resultsBefore}");
        Assert.DoesNotContain(Templates, LayoutPlanner.ToolIds(ws.Capture().Root));
    });

    [Fact]
    public Task Binds_join_the_same_full_height_column_and_switching_keeps_it() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        h.Window.Width = 1280;
        h.Window.Height = 800;
        var ws = h.Get<DockWorkspace>();
        await OpenSqlTabAsync(h);

        ws.ShowTool(Templates);
        ws.ShowTool(Binds);
        Dispatcher.UIThread.RunJobs();

        var root = ws.Capture().Root!;
        var right = root.Children[^1];
        Assert.Equal([Templates, Binds], right.Tools); // 같은 오른쪽 묶음의 탭(전환)
        Assert.Equal(Binds, right.ActiveTool);
        var editor = BoundsOf<SqlWorkspaceView>(h);
        var results = BoundsOf<ResultsPanelView>(h);
        var binds = BoundsOf<BindVariablesView>(h);
        Assert.True(binds.Left >= editor.Right - 1 && binds.Bottom >= results.Bottom - 2, $"binds {binds}, editor {editor}, results {results}");

        ws.ShowTool(Templates);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Templates, ws.Capture().Root!.Children[^1].ActiveTool);
        Assert.True(BoundsOf<TemplatesView>(h).Bottom >= BoundsOf<ResultsPanelView>(h).Bottom - 2);
    });
}
