using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Features.Execution;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Execution;

namespace Oranda.App.Tests.Features.Execution;

/// <summary>
/// TSK-12-06: 새 쿼리를 실행하기 시작하면 그 탭의 이전 쿼리 오류(결과 탭의 오류·오류 상태)를 바로 지운다.
/// 실행이 끝나기 전에도 이전 오류가 남아 마치 계속 오류가 나는 것처럼 보이던 문제.
/// </summary>
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class ErrorClearOnRunTests
{
    private static ExecutionResult Fail(SqlStatement s) => new(s, ExecutionStatus.Failed)
    {
        Error = new DbError("ORA-00942", "table or view does not exist") { Number = 942, Offset = 14 },
    };

    private static async Task<(TaskCompletionSource<ExecutionResult> Gate, Task<bool> Run)> StartGated(Rig rig, RunMode mode, bool newResultTab = false)
    {
        var gate = new TaskCompletionSource<ExecutionResult>();
        rig.Exec.Handler = (_, _) => gate.Task;
        var before = rig.Exec.Statements.Count;
        var run = rig.Controller.RunAsync(mode, newResultTab);
        await HeadlessHost.WaitUntil(() => rig.State.Busy && rig.Exec.Statements.Count > before);
        return (gate, run);
    }

    [Fact]
    public Task A_new_statement_run_removes_the_previous_error_tab_and_error_state_before_it_finishes() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select * from nope");
        rig.Exec.Handler = (s, _) => Task.FromResult(Fail(s));
        await rig.Controller.RunAsync(RunMode.Statement);
        var results = (ResultTabsViewModel)tab.ResultPane!;
        Assert.True(Assert.Single(results.Tabs).IsError);
        Assert.True(rig.State.HasExecError);

        var (gate, run) = await StartGated(rig, RunMode.Statement);

        // 실행 중: 이전 오류 탭도, 오류 상태도 없다. 기록(메시지 창)의 과거 오류 줄은 남는다.
        Assert.True(results.IsRunning);
        Assert.DoesNotContain(results.Tabs, t => t.IsError);
        Assert.False(rig.State.HasExecError);
        Assert.Null(rig.State.LastError);
        Assert.Contains(rig.Log.Messages, m => m.Kind == MessageKind.Error && m.Text.Contains("ORA-00942", StringComparison.Ordinal));

        gate.SetResult(Rig.Query(rig.Exec.Statements[^1]));
        await run;
        var only = Assert.Single(results.Tabs);
        Assert.True(only.HasPane);
        Assert.False(rig.State.HasExecError);
    });

    [Fact]
    public Task A_new_script_run_removes_the_previous_error_while_running_and_keeps_only_its_own_results() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select * from nope;\nselect * from nope2;");
        rig.Exec.Handler = (s, _) => Task.FromResult(Fail(s));
        await rig.Controller.RunAsync(RunMode.Script);
        var results = (ResultTabsViewModel)tab.ResultPane!;
        Assert.Contains(results.Tabs, t => t.IsError);

        var (gate, run) = await StartGated(rig, RunMode.Script);

        Assert.DoesNotContain(results.Tabs, t => t.IsError);
        Assert.False(rig.State.HasExecError);

        gate.SetResult(Rig.Query(rig.Exec.Statements[^1]));
        await run;
        Assert.False(rig.State.HasExecError);
        Assert.DoesNotContain(results.Tabs, t => t.IsError && t.Title.StartsWith("오류", StringComparison.Ordinal));
        Assert.Contains(results.Tabs, t => t.HasPane);
    });

    [Fact]
    public Task A_run_in_a_new_result_tab_also_removes_the_previous_error_but_keeps_the_good_result() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select 1 from dual");
        rig.Exec.Handler = (s, _) => Task.FromResult(Rig.Query(s));
        await rig.Controller.RunAsync(RunMode.Statement);
        var results = (ResultTabsViewModel)tab.ResultPane!;
        var good = Assert.Single(results.Tabs);
        rig.Exec.Handler = (s, _) => Task.FromResult(Fail(s));
        await rig.Controller.RunAsync(RunMode.Statement, newResultTab: true);
        Assert.Equal(2, results.Tabs.Count);
        Assert.True(results.Tabs[1].IsError);

        var (gate, run) = await StartGated(rig, RunMode.Statement, newResultTab: true);

        Assert.Same(good, Assert.Single(results.Tabs)); // 오류만 지우고 정상 결과(기존 규칙)는 그대로
        Assert.False(rig.State.HasExecError);

        gate.SetResult(Rig.Query(rig.Exec.Statements[^1]));
        await run;
        Assert.Equal(2, results.Tabs.Count);
    });

    [Fact]
    public Task A_pinned_tab_is_not_touched_by_the_clearing() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select * from nope");
        rig.Exec.Handler = (s, _) => Task.FromResult(Fail(s));
        await rig.Controller.RunAsync(RunMode.Statement);
        var results = (ResultTabsViewModel)tab.ResultPane!;
        var pinned = Assert.Single(results.Tabs);
        pinned.TogglePin();
        await rig.Controller.RunAsync(RunMode.Statement); // 고정하지 않은 새 오류 탭이 하나 더
        Assert.Equal(2, results.Tabs.Count);

        var (gate, run) = await StartGated(rig, RunMode.Statement);

        Assert.Same(pinned, Assert.Single(results.Tabs)); // 고정한 것만 남는다
        Assert.True(pinned.IsPinned);
        Assert.True(pinned.IsError);

        gate.SetResult(Rig.Query(rig.Exec.Statements[^1]));
        await run;
        Assert.Equal(2, results.Tabs.Count);
        Assert.Same(pinned, results.Tabs[0]);
    });

    [Fact]
    public Task The_error_of_another_editor_tab_stays() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = rig.Host.NewDocument("select * from nope");
        rig.Exec.Handler = (s, _) => Task.FromResult(Fail(s));
        await rig.Controller.RunAsync(RunMode.Statement, tab: a);
        var resultsA = (ResultTabsViewModel)a.ResultPane!;
        var b = rig.Host.NewDocument("select 1 from dual");
        rig.Host.Active = b;

        var (gate, run) = await StartGated(rig, RunMode.Statement);

        Assert.True(Assert.Single(resultsA.Tabs).IsError); // 다른 탭의 결과 영역은 그대로

        gate.SetResult(Rig.Query(rig.Exec.Statements[^1]));
        await run;
        Assert.True(Assert.Single(resultsA.Tabs).IsError);
    });

    [Fact]
    public Task A_failed_new_run_shows_only_the_new_error_and_a_cancelled_one_does_not_bring_the_old_one_back() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select * from nope");
        rig.Exec.Handler = (s, _) => Task.FromResult(Fail(s));
        await rig.Controller.RunAsync(RunMode.Statement);
        var results = (ResultTabsViewModel)tab.ResultPane!;

        // 새 오류: 오류 탭은 하나뿐이고 새 문구다
        var (gate, run) = await StartGated(rig, RunMode.Statement);
        Assert.Empty(results.Tabs);
        gate.SetResult(new ExecutionResult(rig.Exec.Statements[^1], ExecutionStatus.Failed) { Error = new DbError("ORA-01722", "invalid number") { Number = 1722 } });
        await run;
        var err = Assert.Single(results.Tabs);
        Assert.StartsWith("ORA-01722", err.Message, StringComparison.Ordinal);
        Assert.True(rig.State.HasExecError);

        // 취소: 이전 오류는 돌아오지 않고 취소 안내만 보인다
        (gate, run) = await StartGated(rig, RunMode.Statement);
        Assert.Empty(results.Tabs);
        gate.SetResult(new ExecutionResult(rig.Exec.Statements[^1], ExecutionStatus.Cancelled));
        await run;
        var msg = Assert.Single(results.Tabs);
        Assert.False(msg.IsError);
        Assert.False(rig.State.HasExecError);
    });

    [Fact]
    public Task A_prepared_run_from_mybatis_clears_the_previous_error_too() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select * from nope");
        rig.Exec.Handler = (s, _) => Task.FromResult(Fail(s));
        await rig.Controller.RunAsync(RunMode.Statement);
        var results = (ResultTabsViewModel)tab.ResultPane!;
        Assert.True(Assert.Single(results.Tabs).IsError);

        var gate = new TaskCompletionSource<ExecutionResult>();
        rig.Exec.Handler = (_, _) => gate.Task;
        var prepared = new SqlStatement("select 1 from dual", default, StatementKind.Query) { Verb = "SELECT" };
        var before = rig.Exec.Statements.Count;
        var run = rig.Controller.RunPreparedAsync(tab, prepared, new Dictionary<string, BindValue>());
        await HeadlessHost.WaitUntil(() => rig.State.Busy && rig.Exec.Statements.Count > before);

        Assert.DoesNotContain(results.Tabs, t => t.IsError);
        Assert.False(rig.State.HasExecError);

        gate.SetResult(Rig.Query(prepared));
        await run;
        Assert.True(Assert.Single(results.Tabs).HasPane);
    });

    [Fact]
    public void ClearErrors_removes_only_unpinned_error_tabs_and_fixes_the_selection()
    {
        var vm = new ResultTabsViewModel();
        var ok = vm.AddMessage("메시지", "1행", isError: false);
        var bad = vm.AddMessage("오류", "ORA-1", isError: true);
        vm.Selected = bad;

        vm.ClearErrors();

        Assert.Same(ok, Assert.Single(vm.Tabs));
        Assert.Same(ok, vm.Selected);

        vm.ClearErrors(); // 지울 것이 없어도 그대로
        Assert.Same(ok, Assert.Single(vm.Tabs));

        var onlyBad = new ResultTabsViewModel();
        onlyBad.Selected = onlyBad.AddMessage("오류", "x", isError: true);
        onlyBad.ClearErrors();
        Assert.Empty(onlyBad.Tabs);
        Assert.Null(onlyBad.Selected);
    }

    /// <summary>실행 중 화면 PNG(라이트·다크). ORANDA_RENDER_DIR 이 있을 때만 쓴다.</summary>
    [Fact]
    public Task The_running_screen_shows_no_old_error_in_light_and_dark() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select * from nope");
        rig.Exec.Handler = (s, _) => Task.FromResult(Fail(s));
        await rig.Controller.RunAsync(RunMode.Statement);
        var results = (ResultTabsViewModel)tab.ResultPane!;
        results.RunBarDelay = TimeSpan.Zero;

        var view = new ResultTabsView { DataContext = results };
        var window = new Window { Width = 640, Height = 200, Content = view };
        window.Show();
        Pump();
        Render(window, "before-error", 640, 200);

        var (gate, run) = await StartGated(rig, RunMode.Statement);
        Pump();
        Render(window, "running", 640, 200);
        Assert.Empty(results.Tabs);

        gate.SetResult(Rig.Query(rig.Exec.Statements[^1]));
        await run;
        Pump();
        Render(window, "done", 640, 200);
        window.Close();
    });

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Render(Window window, string name, int width, int height)
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (variant, tone) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Pump();
                window.UpdateLayout();
                using var bmp = new RenderTargetBitmap(new PixelSize(width, height));
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"errclear-{name}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Pump();
        }
    }
}
