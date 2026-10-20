using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Tests.Features.SchemaBrowser;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.ConnectionManager;

// 접속 관리자 줄 끌기: Windows 에서 DoDragDrop 이 COMException 으로 실패해도 앱이 죽지 않고 상태줄에 안내한다(TSK-07-05).
public sealed class ConnectionManagerDragTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Drag_start_failing_with_COMException_is_reported_and_never_unhandled(bool throwsSynchronously)
    {
        using var fx = new ServiceFixture();
        await fx.Service.SaveProfileAsync(new ConnectionProfile("ft-dev", "ft", "localhost:1522/FREEPDB1"), "pw", true, CancellationToken.None);
        var vm = new ConnectionManagerViewModel(fx.Service);
        await vm.LoadAsync();

        await HeadlessHost.Run(async () =>
        {
            using var spy = new DragTestSupport.UnhandledSpy();
            var started = 0;
            var window = new ConnectionManagerWindow
            {
                DataContext = vm,
                Width = 900,
                Height = 640,
                DragStarter = (_, _, _) =>
                {
                    started++;
                    return throwsSynchronously ? throw DragTestSupport.OleFailure() : Task.FromException<DragDropEffects>(DragTestSupport.OleFailure());
                },
            };
            window.Show();
            var tree = window.FindControl<TreeView>("ConnectionTree")!;
            var node = vm.AllNodes().Single(n => n.Title == "ft-dev");
            Dispatcher.UIThread.RunJobs();
            tree.UpdateLayout();
            var item = (TreeViewItem)tree.TreeContainerFromItem(node)!;
            var p = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;

            window.MouseDown(p, MouseButton.Left);
            window.MouseMove(new Point(p.X + 20, p.Y), RawInputModifiers.LeftMouseButton);
            Assert.Equal(1, started); // 이동 이벤트 안에서 곧바로 시작
            await DragTestSupport.PumpAsync();

            Assert.Null(spy.Caught);
            Assert.StartsWith("끌어 놓기를 시작하지 못했습니다: ", vm.Status);
            Assert.Contains("E_FAIL", vm.Status);
            Assert.Equal(StatusKind.Error, vm.StatusKind);
            window.Close();
        });
    }
}
