using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Tests.Features.MyBatisWorkspace.Flow;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Plans;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;
using TestData = Oranda.Testing.TestData;
using TestSession = Oranda.Testing.FakeSession;

namespace Oranda.App.Tests.Features.ExecutionPlan;

/// <summary>
/// TSK-06-04 계획 창 보강 화면(Headless): A-Rows 없음 띠·그래프 탭·보고서 탭 상태·통계/자식 커서 단추.
/// <c>ORANDA_RENDER_DIR</c> 가 있으면 라이트·다크 PNG 를 남긴다(시안 g 비교용).
/// </summary>
public class PlanBoostHeadlessTests
{
    private static (PlanView View, Window Window) Show(PlanViewModel vm, int width = 1500, int height = 620)
    {
        var view = new PlanView { DataContext = vm };
        var window = new Window { Content = view, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, window);
    }

    private static PlanViewModel Vm(PlanBoostTests.FakePlans? plans = null, params string[] commands)
    {
        var set = new HashSet<string>(commands);
        return new PlanViewModel(plans ?? new PlanBoostTests.FakePlans()) { PostToUi = a => a(), HasCommand = set.Contains };
    }

    [Fact]
    public Task Band_appears_above_the_tree_for_an_actual_plan_without_a_rows() => HeadlessHost.Run(() =>
    {
        var vm = Vm(null, PlanViewModel.StatsCommandId, PlanViewModel.ChildCursorsCommandId);
        vm.SqlIdText = "7h35uxf5uhmm1";
        vm.Apply(PlanBoostTests.NoActuals(), "실제(ALLSTATS LAST)");
        var (view, window) = Show(vm);
        try
        {
            var band = view.FindControl<PlanStatisticsBand>("StatisticsBand")!;
            Assert.True(band.IsVisible);
            var hint = band.FindControl<Button>("HintCopyButton")!;
            Assert.Contains("accent", hint.Classes);
            Assert.Contains("편집기 글은 바꾸지 않습니다", ToolTip.GetTip(hint) as string, StringComparison.Ordinal);
            Assert.Contains("운영에서도 묻지 않습니다", ToolTip.GetTip(band.FindControl<Button>("StatisticsLevelButton")!) as string, StringComparison.Ordinal);
            Assert.Equal("실제 행 수 없음 안내", AutomationProperties.GetName(band.FindControl<Border>("Strip")!));

            // 띠는 트리보다 위에 있다.
            var tree = view.FindControl<ListBox>("PlanList")!;
            Assert.True(band.TranslatePoint(default, view)!.Value.Y < tree.TranslatePoint(default, view)!.Value.Y);

            Assert.True(view.FindControl<Button>("StatsButton")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<Button>("ChildCursorsButton")!.IsEffectivelyVisible);
            MyBatisFlowRig.Render(window, "tn-plan-g1-band", 1500, 620);

            vm.IsRerunning = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(hint.IsEnabled); // 사본 실행 중에는 단추를 끈다
            vm.IsRerunning = false;

            vm.Apply(PlanBoostTests.WithActuals("x"), "실제");
            Dispatcher.UIThread.RunJobs();
            Assert.False(band.IsVisible);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Stats_buttons_show_now_that_tsk_06_03_registers_the_commands() => HeadlessHost.Run(() =>
    {
        var registry = ShellTestHelpers.CreateCommandRegistry();
        var vm = new PlanViewModel(new PlanBoostTests.FakePlans()) { PostToUi = a => a(), HasCommand = id => registry.Find(id) is not null };
        vm.Apply(PlanBoostTests.WithActuals("x"), "실제");
        var (view, window) = Show(vm);
        try
        {
            Assert.True(view.FindControl<Button>("StatsButton")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<Button>("ChildCursorsButton")!.IsEffectivelyVisible);
            vm.SelectedTabIndex = PlanViewModel.GraphTabIndex;
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<Button>("GraphStatsButton")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<Button>("GraphAssistantButton")!.IsEffectivelyVisible); // TSK-06-05 도우미도 등록됨
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Stats_buttons_are_hidden_while_the_commands_are_not_registered() => HeadlessHost.Run(() =>
    {
        var vm = Vm();
        vm.Apply(PlanBoostTests.NoActuals(), "실제");
        var (view, window) = Show(vm);
        try
        {
            Assert.False(view.FindControl<Button>("StatsButton")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Button>("ChildCursorsButton")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Button>("GraphAssistantButton")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Graph_tab_draws_nodes_and_clicking_a_node_selects_the_tree_row() => HeadlessHost.Run(() =>
    {
        var vm = Vm(null, PlanViewModel.StatsCommandId, PlanViewModel.ChildCursorsCommandId, PlanViewModel.AssistantCommandId);
        PlanStep[] steps =
        [
            new(0, null, 0, "SELECT STATEMENT") { Cost = 8412, ActualRows = 1208, Starts = 1, ActualTime = TimeSpan.FromSeconds(1.84) },
            new(1, 0, 1, "SORT") { Options = "ORDER BY", Cost = 8412, EstimatedRows = 31, ActualRows = 1208, Starts = 1, ActualTime = TimeSpan.FromSeconds(1.83) },
            new(2, 1, 2, "HASH JOIN") { Options = "ANTI NA", Cost = 8410, EstimatedRows = 31, ActualRows = 1208, Starts = 1, ActualTime = TimeSpan.FromSeconds(1.79) },
            new(3, 2, 3, "NESTED LOOPS") { Cost = 8402, EstimatedRows = 31, ActualRows = 1211, Starts = 1, ActualTime = TimeSpan.FromSeconds(1.71) },
            new(4, 3, 4, "TABLE ACCESS") { Options = "FULL", ObjectOwner = "MES", ObjectName = "LOT_HIST", Cost = 8380, EstimatedRows = 31, ActualRows = 1211, Starts = 1,
                ActualTime = TimeSpan.FromSeconds(1.6), Risk = PlanRisk.FullScan | PlanRisk.CardinalityMisestimate,
                FilterPredicates = "\"LH\".\"EQP_ID\"=:B1 AND \"LH\".\"EVT_TIME\">=:B2" },
            new(5, 3, 4, "TABLE ACCESS") { Options = "BY INDEX ROWID", ObjectOwner = "MES", ObjectName = "LOT_MST", Cost = 2, EstimatedRows = 1, ActualRows = 1211, Starts = 1211,
                ActualTime = TimeSpan.FromSeconds(0.09) },
            new(6, 5, 5, "INDEX") { Options = "UNIQUE SCAN", ObjectOwner = "MES", ObjectName = "LOT_MST_PK", Cost = 1, EstimatedRows = 1, ActualRows = 1211, Starts = 1211,
                ActualTime = TimeSpan.FromSeconds(0.05) },
            new(7, 2, 3, "TABLE ACCESS") { Options = "FULL", ObjectOwner = "MES", ObjectName = "EVT_SKIP", Cost = 3, EstimatedRows = 12, ActualRows = 12, Starts = 1,
                ActualTime = TimeSpan.FromMilliseconds(2) },
        ];
        vm.Apply(new CorePlan(steps, "Plan hash value: 1") { Cursor = new CursorRef("7h35uxf5uhmm1", 0) }, "실제");
        vm.SelectedTabIndex = PlanViewModel.GraphTabIndex;
        var (view, window) = Show(vm, 1500, 560);
        try
        {
            var canvas = view.FindControl<PlanGraphView>("GraphCanvas")!;
            Assert.True(canvas.IsEffectivelyVisible);
            Assert.Equal(8, canvas.Graph!.Nodes.Count);
            Assert.True(view.FindControl<StackPanel>("GraphTools")!.IsVisible);
            Assert.False(view.FindControl<StackPanel>("ReportTools")!.IsVisible);
            Assert.True(view.FindControl<Border>("GraphLegend")!.IsVisible);

            canvas.Fit();
            Dispatcher.UIThread.RunJobs();
            var node = canvas.Graph.Nodes.Single(n => n.StepId == 7);
            var screen = new Point(node.CenterX * canvas.Zoom + canvas.Offset.X, (node.Y + 10) * canvas.Zoom + canvas.Offset.Y);
            Assert.True(canvas.ClickAt(screen));
            Assert.Equal(7, vm.SelectedRow!.Id); // 그래프 → 트리
            Assert.Equal(7, canvas.SelectedStepId);
            Assert.False(canvas.ClickAt(new Point(1, 1)));

            vm.SelectedRow = vm.Rows[4];
            Dispatcher.UIThread.RunJobs();
            MyBatisFlowRig.Render(window, "tn-plan-g2-graph", 1500, 560);

            var before = canvas.Zoom;
            view.FindControl<Button>("ZoomInButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(canvas.Zoom > before);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Report_tab_shows_each_state_screen() => HeadlessHost.Run(async () =>
    {
        var plans = new PlanBoostTests.FakePlans
        {
            Report = (_, _) => "SQL Monitoring Report\n\nGlobal Information\n------------------------------\n Status              :  DONE (ALL ROWS)\n SQL ID              :  7h35uxf5uhmm1\n Duration            :  2s",
        };
        plans.Cursors["7h35uxf5uhmm1"] = PlanBoostTests.WithActuals("7h35uxf5uhmm1");
        var vm = Vm(plans, "oranda.conn.manage");
        var (view, window) = Show(vm, 1100, 420);
        try
        {
            vm.SelectedTabIndex = PlanViewModel.ReportTabIndex;
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<StackPanel>("ReportNoCursor")!.IsEffectivelyVisible);

            await vm.ShowCursorAsync(new TestSession(), new CursorRef("7h35uxf5uhmm1"));
            await vm.LoadReportAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<StackPanel>("ReportNoLicense")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<Button>("ConnectionManagerButton")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Button>("BrowserButton")!.IsEnabled);
            MyBatisFlowRig.Render(window, "tn-plan-g3-report-nolicense", 1100, 420);

            view.FindControl<Button>("ShowTreeButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(0, vm.SelectedTabIndex);

            vm.SelectedTabIndex = PlanViewModel.ReportTabIndex;
            await vm.ShowCursorAsync(new TestSession(TestData.Profile(licensePack: LicensePack.DiagnosticsAndTuning)), new CursorRef("7h35uxf5uhmm1"));
            await vm.LoadReportAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<TextBox>("ReportBox")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<Button>("BrowserButton")!.IsEnabled);
            MyBatisFlowRig.Render(window, "tn-plan-g3-report-ready", 1100, 420);

            plans.Report = (_, _) => string.Empty;
            await vm.LoadReportAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<StackPanel>("ReportEmpty")!.IsEffectivelyVisible);

            plans.Report = (_, _) => throw new InvalidOperationException("ORA-13647");
            await vm.LoadReportAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<StackPanel>("ReportError")!.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    });
}
