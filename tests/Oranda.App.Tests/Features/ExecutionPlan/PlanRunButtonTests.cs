using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Tests.Features.MyBatisWorkspace.Flow;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.ExecutionPlan;

/// <summary>계획 창 도구 줄의 「예상 계획」·「실제 계획」 단추(P2): 단축키와 같은 명령을 부르고, 활성 SQL 탭이 없으면 꺼진다.</summary>
public class PlanRunButtonTests
{
    [Fact]
    public Task Run_buttons_are_disabled_without_an_active_sql_tab() => HeadlessHost.Run(() =>
    {
        var vm = new PlanViewModel { PostToUi = a => a(), CanRunCommand = _ => false };
        var view = new PlanView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.False(view.FindControl<Button>("ExplainButton")!.IsEnabled);
            Assert.False(view.FindControl<Button>("ActualButton")!.IsEnabled);
            MyBatisFlowRig.Render(window, "plan-run-buttons-disabled", 700, 500); // 좁은 폭에서도 도구 줄이 깨지지 않는지

            // 연결을 안 한 시험용 뷰모델(판단 방법 없음)도 꺼진 채로 둔다.
            vm.CanRunCommand = null;
            vm.RefreshRunButtons();
            Dispatcher.UIThread.RunJobs();
            Assert.False(view.FindControl<Button>("ExplainButton")!.IsEnabled);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Run_buttons_call_the_shortcut_commands_once_a_tab_is_active() => HeadlessHost.Run(() =>
    {
        var called = new List<string>();
        var active = false;
        var vm = new PlanViewModel
        {
            PostToUi = a => a(),
            CanRunCommand = _ => active,
            RunCommand = id =>
            {
                called.Add(id);
                return Task.CompletedTask;
            },
        };
        var view = new PlanView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var explain = view.FindControl<Button>("ExplainButton")!;
            var actual = view.FindControl<Button>("ActualButton")!;
            Assert.False(explain.IsEnabled);

            active = true; // 문맥 키가 바뀌면(탭이 열리면) 단추를 다시 본다
            vm.RefreshRunButtons();
            Dispatcher.UIThread.RunJobs();
            Assert.True(explain.IsEnabled);
            Assert.True(actual.IsEnabled);

            explain.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            actual.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(["oranda.plan.explain", "oranda.plan.actual"], called);
            Assert.Contains("Ctrl+E", ToolTip.GetTip(explain) as string, StringComparison.Ordinal);
            Assert.Contains("Ctrl+Shift+E", ToolTip.GetTip(actual) as string, StringComparison.Ordinal);
            MyBatisFlowRig.Render(window, "plan-run-buttons", 1200, 500);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });
}
