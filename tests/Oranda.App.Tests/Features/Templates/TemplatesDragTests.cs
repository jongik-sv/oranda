using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Oranda.App.Features.Localization;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Features.Templates;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.SchemaBrowser;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Platform.Storage;

namespace Oranda.App.Tests.Features.Templates;

// 템플릿 창 조각 끌기: Windows 에서 DoDragDrop 이 COMException 으로 실패해도 앱이 죽지 않고 상태줄에 안내한다(TSK-07-05).
public sealed class TemplatesDragTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-tpld-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Drag_start_failing_with_COMException_is_reported_and_never_unhandled(bool throwsSynchronously) => Ui.Run(async () =>
    {
        using var spy = new DragTestSupport.UnhandledSpy();
        var started = 0;
        var vm = new TemplatesViewModel(new AppDataPaths(_root), new ShellText()) { Host = new SqlEditorHost() };
        var view = new TemplatesView
        {
            DataContext = vm,
            DragStarter = (_, _, _) =>
            {
                started++;
                return throwsSynchronously ? throw DragTestSupport.OleFailure() : Task.FromException<DragDropEffects>(DragTestSupport.OleFailure());
            },
        };
        var window = new Window { Width = 360, Height = 640, Content = view };
        window.Show();
        vm.Query = "nvl2"; // 찾으면 그 줄이 보이게 펼쳐진다.
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var list = view.FindControl<TreeView>("List")!;
        var item = (TreeViewItem)list.TreeContainerFromItem(vm.SelectedRow!)!;
        var p = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;

        window.MouseDown(p, MouseButton.Left);
        window.MouseMove(new Point(p.X + 20, p.Y), RawInputModifiers.LeftMouseButton);
        Assert.Equal(1, started); // 이동 이벤트 안에서 곧바로 시작
        await DragTestSupport.PumpAsync();

        Assert.Null(spy.Caught);
        Assert.StartsWith("끌어 놓기를 시작하지 못했습니다: ", vm.Status);
        Assert.Contains("E_FAIL", vm.Status);
        window.Close();
    });
}
