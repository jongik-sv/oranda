using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Tests.Features.ExecutionPlan;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Metadata;
using Oranda.Core.Plans;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Tests.Shell.Controls;

/// <summary>열 폭 끌기를 붙인 화면 표(Headless): 계획 비교·컴파일 오류. 머리글 경계를 끌면 줄의 같은 열도 바뀐다.</summary>
[Collection(Oranda.App.Tests.Features.PlsqlWorkspace.PlsqlStatics.Name)] // 컴파일 오류 창은 정적 Current 를 쓴다
public sealed class TableColumnsScreensTests
{
    [Fact]
    public Task Plan_compare_columns_follow_the_header_drag() => HeadlessHost.Run(() =>
    {
        var vm = new PlanViewModel();
        vm.Apply(new CorePlan(PlanMetricsTests.HashJoin(), "Plan hash value: 1"), "예상");
        vm.PinCurrent();
        PlanStep[] next =
        [
            new(0, null, 0, "SELECT STATEMENT") { Cost = 40 },
            new(1, 0, 1, "HASH JOIN") { Cost = 40 },
            new(2, 1, 2, "TABLE ACCESS") { Options = "FULL", ObjectName = "A", Cost = 30 },
        ];
        vm.Apply(new CorePlan(next, "Plan hash value: 2"), "예상");
        var view = new PlanView { DataContext = vm };
        var window = new Window { Content = view, Width = 1600, Height = 600 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.CompareWithPinned());
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var header = view.FindControl<Grid>("CompareHeader")!;
            Assert.Equal(420, header.ColumnDefinitions[3].ActualWidth, 1); // 연산 열: XAML 기본 폭(옛 k-op 폭), 남는 폭은 끝 * 열이 받는다
            TableColumnsCheck.DragMovesHeaderAndRows(window, header, view.FindControl<ListBox>("CompareList")!, column: 3);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Compile_error_columns_follow_the_header_drag() => HeadlessHost.Run(() =>
    {
        var vm = new CompileErrorsViewModel();
        vm.Items.Add(new CompileErrorItem(new CompileError(3, 7, "PLS-00103: Encountered the symbol", false)));
        vm.Items.Add(new CompileErrorItem(new CompileError(9, 1, "PLW-06002: Unreachable code", true)));
        var view = new CompileErrorsView { DataContext = vm };
        var window = new Window { Content = view, Width = 900, Height = 300 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            TableColumnsCheck.DragMovesHeaderAndRows(window, view.FindControl<Grid>("ErrorsHeader")!, view.FindControl<ListBox>("ErrorList")!, column: 1);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });
}
