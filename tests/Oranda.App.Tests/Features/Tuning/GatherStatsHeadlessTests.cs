using Avalonia.Controls;
using Avalonia.LogicalTree;
using Oranda.App.Features.Tuning.Stats;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
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
        var content = GatherStatsDialog.Build(vm, out var cancel, out var run, out var script);
        var window = new Window { Content = content, DataContext = vm, Width = 760, Height = 640 };
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
        }
        finally
        {
            window.Close();
        }
    });
}
