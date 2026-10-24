using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.Tuning.Trace;
using Oranda.App.Shell.Controls;
using Oranda.App.Tests.Features.MyBatisWorkspace.Flow;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Tuning;
using TestSession = Oranda.Testing.FakeSession;

namespace Oranda.App.Tests.Features.Tuning;

/// <summary>TSK-06-04 트레이스 보기 화면(Headless): 요약·본문·11g·권한 없음 상태. <c>ORANDA_RENDER_DIR</c> 가 있으면 PNG(시안 h 비교용).</summary>
public class TraceViewerHeadlessTests
{
    private static (TraceViewerView View, Window Window) Show(TraceViewerViewModel vm, int width = 1500, int height = 640)
    {
        var view = new TraceViewerView { DataContext = vm };
        var window = new Window { Content = view, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, window);
    }

    [Fact]
    public Task Summary_and_body_views_show_the_trace() => HeadlessHost.Run(async () =>
    {
        var vm = new TraceViewerViewModel(new TraceViewerTests.FakeTuning()) { Parser = _ => TraceViewerTests.Summary() };
        vm.OptimizerTrace = _ => Task.FromException<OptimizerTraceOutcome>(new InvalidOperationException("x"));
        var (view, window) = Show(vm);
        try
        {
            Assert.True(view.FindControl<StackPanel>("NoSessionState")!.IsEffectivelyVisible);
            await vm.LoadAsync(new TestSession());
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<Grid>("SummaryPane")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<DockPanel>("BodyPane")!.IsEffectivelyVisible);
            var list = view.FindControl<ListBox>("StatementList")!;
            Assert.Equal(3, list.ItemCount);
            Assert.Equal(4, view.FindControl<DetailTable>("CallTable")!.Rows!.Count);
            Assert.Equal(3, view.FindControl<ListBox>("WaitList")!.ItemCount);
            Assert.Single(view.FindControl<DetailTable>("FileList")!.Rows!);
            Assert.True(view.FindControl<Button>("OptimizerTraceButton")!.IsEnabled);
            MyBatisFlowRig.Render(window, "tn-plan-h1-summary", 1500, 640);

            vm.ViewIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<DockPanel>("BodyPane")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<ListBox>("LineList")!.ItemCount > 5);
            Assert.False(view.FindControl<ComboBox>("SortBox")!.IsEnabled); // 본문에서는 정렬 없음
            MyBatisFlowRig.Render(window, "tn-plan-h2-body", 1500, 640);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Eleven_g_and_no_privilege_screens_offer_copy_buttons() => HeadlessHost.Run(async () =>
    {
        var tuning = new TraceViewerTests.FakeTuning
        {
            File = _ => new TraceFileInfo("/u01/app/oracle/diag/rdbms/mesdev/MESDEV/trace/MESDEV_ora_40112.trc", false,
                "v$diag_trace_file_contents 는 12.2 부터 있습니다(이 DB 11.2.0.4)."),
        };
        var vm = new TraceViewerViewModel(tuning) { Parser = _ => TraceViewerTests.Summary() };
        var (view, window) = Show(vm, 900, 380);
        try
        {
            await vm.LoadAsync(new TestSession());
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<StackPanel>("UnreadableState")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<Button>("CopyPathButton")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Grid>("SummaryPane")!.IsEffectivelyVisible);
            MyBatisFlowRig.Render(window, "tn-plan-h3-11g", 900, 380);

            tuning.File = _ => throw new UnauthorizedAccessException("no");
            await vm.LoadAsync(new TestSession());
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<StackPanel>("NoPrivilegeState")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<Button>("CopyGrantButton")!.IsEffectivelyVisible);
            MyBatisFlowRig.Render(window, "tn-plan-h4-noprivilege", 900, 380);

            tuning.File = _ => new TraceFileInfo("/t.trc", true, null);
            tuning.Lines = _ => [];
            await vm.LoadAsync(new TestSession());
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<StackPanel>("NoTraceState")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Button>("OptimizerTraceButton")!.IsEnabled); // 10053 실행 길이 없으면 꺼짐
        }
        finally
        {
            window.Close();
        }
    });
}
