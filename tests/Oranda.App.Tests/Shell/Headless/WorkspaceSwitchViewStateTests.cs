using System.Text;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Docking;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell.Headless;

/// <summary>
/// 작업 공간을 오가도 편집기의 커서·선택·스크롤이 그대로 남는다. 도킹이 문서를 바꿀 때 편집기 View 를 새로 만들어도
/// 새 View 가 탭(ViewModel)의 위치로 되살려야 한다.
/// </summary>
public class WorkspaceSwitchViewStateTests
{
    private static string LongText()
    {
        var sb = new StringBuilder();
        for (int i = 1; i <= 300; i++)
        {
            sb.Append("select ").Append(i).Append(" as c from dual;\n");
        }

        return sb.ToString();
    }

    private static SqlEditorView VisibleEditor(ShellHarness h, SqlEditorViewModel tab)
        => h.Window.GetVisualDescendants().OfType<SqlEditorView>().Single(v => v.IsEffectivelyVisible && ReferenceEquals(v.DataContext, tab));

    [Fact]
    public Task Caret_selection_and_scroll_survive_switching_to_another_workspace_and_back() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var wiring = h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
        await wiring.RunAsync(default);
        Dispatcher.UIThread.RunJobs();
        var host = SqlEditorHost.Current;
        var dock = h.Get<DockWorkspace>();
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();

        await registry.ExecuteAsync("oranda.file.newSqlEditor", keys);
        Dispatcher.UIThread.RunJobs();
        var tab = host.Active!;
        tab.Document.Text = LongText();
        Dispatcher.UIThread.RunJobs();

        var editor = VisibleEditor(h, tab).TextEditorControl;
        var line = tab.Document.GetLineByNumber(150);
        editor.Select(line.Offset + 7, 3); // 150행의 숫자 "150"
        editor.ScrollToLine(200);
        Dispatcher.UIThread.RunJobs();
        int caret = editor.CaretOffset;
        int scrolledTo = VisibleEditor(h, tab).FirstVisibleLine;
        Assert.True(scrolledTo > 100, $"시험 준비: 스크롤이 내려가야 한다({scrolledTo})");

        await registry.ExecuteAsync("oranda.file.newWorkspace", keys);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, dock.Documents.Count);
        Assert.NotSame(tab, host.Active);

        dock.Activate(dock.Documents.Single(d => d.Panel is SqlWorkspaceViewModel w && w.Tabs.Contains(tab))); // 문서 탭 줄에서 첫 작업 공간을 고른다
        Dispatcher.UIThread.RunJobs();
        Assert.Same(tab, host.Active);

        var back = VisibleEditor(h, tab);
        Assert.Equal(caret, back.TextEditorControl.CaretOffset);
        Assert.Equal(line.Offset + 7, back.TextEditorControl.SelectionStart);
        Assert.Equal(3, back.TextEditorControl.SelectionLength);
        int scrolledBack = back.FirstVisibleLine;

        // 화면에서 떨어진 옛 편집기 View 가 문서 변경을 따라 움직인 자기 커서를 탭에 써 넣으면 안 된다.
        // (전체를 바꾸는 편집이면 옛 View 의 커서가 0 으로 가서, 다음에 돌아올 때 1행 1열이 되었다.)
        back.TextEditorControl.CaretOffset = tab.Document.GetLineByNumber(10).Offset;
        Dispatcher.UIThread.RunJobs();
        tab.Document.Replace(0, tab.Document.TextLength, LongText()); // 포매터처럼 문서 전체를 바꾼다
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(back.TextEditorControl.CaretOffset, tab.CaretOffset);
        Assert.Equal(back.TextEditorControl.SelectionLength, tab.SelectionLength);

        Assert.Equal(scrolledTo, scrolledBack);
    });
}
