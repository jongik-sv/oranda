using Avalonia.Controls;
using Avalonia.LogicalTree;
using Oranda.App.Features.Tuning.Stats;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.Tuning;

/// <summary>SG-1 「통계 수집…」 확인 창(Headless): 운영 경고, 옵션 칸, 읽기 전용 막기, 실행 뒤 결과 줄.</summary>
public class GatherStatsHeadlessTests
{
    private static GatherStatsDialogViewModel Vm(ConnectionProfile profile, FakeExecutionService? exec = null) =>
        new(profile, [new TableStatsTarget("SCOTT", "EMP")], new GatherStatsRunner(exec ?? new FakeExecutionService()),
            _ => Task.FromResult<ISession?>(new FakeSession(profile, purpose: SessionPurpose.Dedicated)));

    private static (Window Window, Control Content, Button Cancel, Button Run, Button Script) Show(GatherStatsDialogViewModel vm)
    {
        var content = GatherStatsDialog.Build(vm, out var cancel, out var run, out var script, out var background);
        var window = new Window { Content = content, DataContext = vm, Width = 760, Height = 640 };
        GatherStatsDialog.Wire(window, vm, run, background);
        window.Show();
        HeadlessHost.Pump(4);
        return (window, content, cancel, run, script);
    }

    [Fact]
    public Task Production_dialog_shows_the_big_warning_and_cancel_is_default() => HeadlessHost.Run(() =>
    {
        var vm = Vm(new ConnectionProfile("SAMPLE", "SCOTT", "h/svc", Environment: ConnectionEnvironment.Production));
        var (window, content, cancel, run, script) = Show(vm);
        try
        {
            Assert.True(cancel.IsDefault);
            Assert.True(cancel.IsCancel);
            Assert.False(run.IsDefault);
            Assert.True(run.IsEnabled);
            Assert.True(script.IsEnabled);
            Assert.Contains(content.GetLogicalDescendants().OfType<Border>(), b => b.Name == "ProductionWarning");
            Assert.Contains(content.GetLogicalDescendants().OfType<ComboBox>(), c => c.Name == "GatherHistogram" && c.ItemCount == 3);
            Assert.Contains(content.GetLogicalDescendants().OfType<TextBox>(), t => t.Name == "GatherDegree");
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Read_only_tibero_dialog_blocks_running() => HeadlessHost.Run(() =>
    {
        var vm = Vm(new ConnectionProfile("SAMPLE", "SCOTT", "h/svc", ReadOnly: true) { Dbms = Dbms.Tibero });
        var (window, content, _, run, _) = Show(vm);
        try
        {
            Assert.False(run.IsEnabled);
            Assert.Contains(content.GetLogicalDescendants().OfType<TextBlock>(), t => t.Name == "GatherBlocked");
            Assert.DoesNotContain(content.GetLogicalDescendants().OfType<Border>(), b => b.Name == "ProductionWarning");
            Assert.Contains(content.GetLogicalDescendants().OfType<ComboBox>(), c => c.Name == "GatherHistogram" && c.ItemCount == 3);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Running_fills_the_result_rows_and_turns_cancel_into_close() => HeadlessHost.Run(async () =>
    {
        var vm = Vm(new ConnectionProfile("SAMPLE", "SCOTT", "h/svc"));
        var (window, _, cancel, _, _) = Show(vm);
        try
        {
            Assert.Equal("취소", cancel.Content);
            await vm.RunAsync();
            HeadlessHost.Pump(4);
            Assert.Equal("닫기", cancel.Content);
            Assert.Equal("성공", vm.Rows[0].StatusText);
            Assert.Contains("성공 1", vm.SummaryText, StringComparison.Ordinal);
            Assert.False(window.IsVisible); // S4-1: 다 성공하면 창이 닫힌다
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Failed_run_keeps_the_dialog_open_with_the_error() => HeadlessHost.Run(async () =>
    {
        var exec = new FakeExecutionService
        {
            Responder = call => new ExecutionResult(call.Statement, ExecutionStatus.Failed) { Error = new DbError("ORA-00054", "ORA-00054: resource busy") { Number = 54, Kind = DbErrorKind.ResourceBusy } },
        };
        var vm = Vm(new ConnectionProfile("SAMPLE", "SCOTT", "h/svc"), exec);
        var (window, _, cancel, run, _) = Show(vm);
        try
        {
            run.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => vm.IsDone);
            HeadlessHost.Pump(4);
            Assert.True(window.IsVisible);
            Assert.Equal("실패", vm.Rows[0].StatusText);
            Assert.Contains("ORA-00054", vm.Rows[0].Message, StringComparison.Ordinal);
            Assert.Equal("닫기", cancel.Content);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Background_run_closes_at_once_and_still_refreshes_when_done() => HeadlessHost.Run(async () =>
    {
        var profile = new ConnectionProfile("SAMPLE", "SCOTT", "h/svc");
        var gate = new TaskCompletionSource<ISession?>();
        var exec = new FakeExecutionService();
        var vm = new GatherStatsDialogViewModel(profile, [new TableStatsTarget("SCOTT", "EMP")], new GatherStatsRunner(exec), _ => gate.Task);
        IReadOnlyList<TableStatsTarget>? refreshed = null;
        string? finished = null;
        vm.Completed += (_, t) => refreshed = t;
        vm.BackgroundFinished += (_, s) => finished = s;
        var (window, content, _, _, _) = Show(vm);
        try
        {
            var background = content.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "GatherInBackground");
            Assert.True(background.IsEnabled);
            background.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            HeadlessHost.Pump(4);
            Assert.False(window.IsVisible); // 누르는 즉시 닫힌다
            Assert.True(vm.IsRunning); // 실행은 이어 간다(닫기가 멈추지 않는다)
            Assert.Null(refreshed);

            gate.SetResult(new FakeSession(profile, purpose: SessionPurpose.Dedicated));
            await WaitAsync(() => finished is not null);
            Assert.Single(exec.Calls);
            Assert.Equal([new TableStatsTarget("SCOTT", "EMP")], refreshed); // 끝나면 새로 읽기(S2)는 그대로
            Assert.Contains("성공 1", finished, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    });

    private static async Task WaitAsync(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            HeadlessHost.Pump(1);
            await Task.Delay(10);
        }

        Assert.True(done());
    }
}
