using Oranda.App.Features.SqlEditor;
using Oranda.Core.Workspaces;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>탭·작업 공간을 닫기 전에 묻는 비동기 닫기 가드(SqlEditorHost.RegisterCloseGuard).</summary>
public sealed partial class SqlEditorTests
{
    [Fact]
    public Task Guards_run_in_registration_order_and_a_cancel_stops_the_close() => Ui.Run(async () =>
    {
        var host = new SqlEditorHost(dialogs: new FakeDialogs());
        var tab = host.NewDocument("a");
        var calls = new List<string>();
        host.RegisterCloseGuard((_, _, _) => { calls.Add("first"); return Task.FromResult(true); });
        host.RegisterCloseGuard((_, _, _) => { calls.Add("second"); return Task.FromResult(false); });
        host.RegisterCloseGuard((_, _, _) => { calls.Add("third"); return Task.FromResult(true); });

        Assert.False(await host.CloseAsync(tab, default));

        Assert.Equal(["first", "second"], calls); // 취소한 가드 뒤의 가드는 부르지 않는다
        Assert.Contains(tab, host.Tabs);
    });

    [Fact]
    public Task Disposed_guard_is_not_called_and_close_goes_through() => Ui.Run(async () =>
    {
        var host = new SqlEditorHost(dialogs: new FakeDialogs());
        var tab = host.NewDocument("a");
        var called = 0;
        var registration = host.RegisterCloseGuard((_, _, _) => { called++; return Task.FromResult(false); });
        registration.Dispose();

        Assert.True(await host.CloseAsync(tab, default));

        Assert.Equal(0, called);
        Assert.DoesNotContain(tab, host.Tabs);
    });

    [Fact]
    public Task Guard_receives_the_closing_tabs_with_the_reason_for_each_path() => Ui.Run(async () =>
    {
        var host = new SqlEditorHost(dialogs: new FakeDialogs());
        var a = host.NewDocument("a");
        var b = host.NewDocument("b");
        var seen = new List<(SqlCloseReason Reason, int Count)>();
        host.RegisterCloseGuard((tabs, reason, _) => { seen.Add((reason, tabs.Count)); return Task.FromResult(true); });

        await host.CloseAsync(b, default);
        var workspace = host.ActiveWorkspace!;
        await host.RestoreAsync(new Workspace { Id = workspace.Id, Name = workspace.Name, SavedAt = DateTimeOffset.UtcNow }, default);
        host.NewDocument("c");
        Assert.True(await host.CanShutdownAsync(default));
        Assert.True(await host.TryCloseWorkspaceAsync(workspace, default));

        Assert.Equal(
            [(SqlCloseReason.Tab, 1), (SqlCloseReason.Replace, 1), (SqlCloseReason.AppExit, 1), (SqlCloseReason.Workspace, 1)],
            seen);
        Assert.DoesNotContain(a, host.Tabs);
    });

    [Fact]
    public Task Cancelled_guard_keeps_the_workspace_open_and_the_tabs_in_it() => Ui.Run(async () =>
    {
        var host = new SqlEditorHost(dialogs: new FakeDialogs());
        var tab = host.NewDocument("a");
        var workspace = host.ActiveWorkspace!;
        host.RegisterCloseGuard((_, _, _) => Task.FromResult(false));

        Assert.False(await host.TryCloseWorkspaceAsync(workspace, default));
        Assert.Null(await host.RestoreAsync(new Workspace { Id = workspace.Id, Name = "x", SavedAt = DateTimeOffset.UtcNow }, default));

        Assert.Contains(workspace, host.Workspaces);
        Assert.Same(tab, Assert.Single(workspace.Tabs));
    });

    [Fact]
    public Task Throwing_guard_cancels_and_second_close_while_guard_runs_is_refused() => Ui.Run(async () =>
    {
        var host = new SqlEditorHost(dialogs: new FakeDialogs());
        var tab = host.NewDocument("a");
        var gate = new TaskCompletionSource<bool>();
        var failing = host.RegisterCloseGuard((_, _, _) => throw new InvalidOperationException("boom"));
        Assert.False(await host.CloseAsync(tab, default));
        Assert.Contains(tab, host.Tabs);
        failing.Dispose();

        host.RegisterCloseGuard((_, _, _) => gate.Task);
        var first = host.CloseAsync(tab, default);
        Assert.False(await host.CloseAsync(tab, default)); // 대화상자가 떠 있는 동안의 두 번째 ×
        gate.SetResult(true);
        Assert.True(await first);
        Assert.DoesNotContain(tab, host.Tabs);
    });

    [Fact]
    public Task Dispose_removes_only_its_own_registration_so_order_holds() => Ui.Run(async () =>
    {
        var host = new SqlEditorHost(dialogs: new FakeDialogs());
        var tab = host.NewDocument("a");
        var calls = new List<string>();
        Task<bool> Same(IReadOnlyList<SqlEditorViewModel> t, SqlCloseReason r, CancellationToken c)
        {
            calls.Add("same");
            return Task.FromResult(true);
        }

        host.RegisterCloseGuard(Same);
        host.RegisterCloseGuard((_, _, _) => { calls.Add("other"); return Task.FromResult(true); });
        var second = host.RegisterCloseGuard(Same);
        second.Dispose();
        second.Dispose(); // 두 번 해제해도 다른 등록을 건드리지 않는다

        Assert.True(await host.CloseAsync(tab, default));

        Assert.Equal(["same", "other"], calls);
    });

    [Fact]
    public Task Combined_delegate_is_rejected() => Ui.Run(() =>
    {
        var host = new SqlEditorHost(dialogs: new FakeDialogs());
        SqlCloseGuard a = (_, _, _) => Task.FromResult(true);
        SqlCloseGuard b = (_, _, _) => Task.FromResult(false);

        Assert.Throws<ArgumentException>(() => host.RegisterCloseGuard(a + b));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Shell_close_returns_true_when_the_guard_cancels_so_the_workspace_stays() => Ui.Run(async () =>
    {
        var host = new SqlEditorHost(dialogs: new FakeDialogs());
        var tab = host.NewDocument("a");
        var workspace = host.ActiveWorkspace!;
        host.RegisterCloseGuard((_, _, _) => Task.FromResult(false));

        Assert.True(await workspace.CloseActiveInnerTabAsync(default)); // 취소여도 true: 셸이 문서까지 닫지 않는다
        workspace.ActiveTab = null; // 선택이 잠깐 비는 경우에도 탭이 남아 있으면 가드를 거친다
        Assert.True(await workspace.CloseActiveInnerTabAsync(default));
        Assert.Contains(tab, host.Tabs);
    });

    [Fact]
    public Task Restore_is_refused_when_tabs_changed_while_the_guard_was_asking() => Ui.Run(async () =>
    {
        var host = new SqlEditorHost(dialogs: new FakeDialogs());
        host.NewDocument("a");
        var workspace = host.ActiveWorkspace!;
        host.RegisterCloseGuard((_, _, _) => { host.NewDocument("added meanwhile"); return Task.FromResult(true); });

        var restored = await host.RestoreAsync(new Workspace { Id = workspace.Id, Name = workspace.Name, SavedAt = DateTimeOffset.UtcNow }, default);

        Assert.Null(restored);
        Assert.Equal(2, workspace.Tabs.Count);
    });
}
