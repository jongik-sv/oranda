using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.DedicatedSession;
using Oranda.App.Features.Execution;
using Oranda.App.Features.Sessions;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.ConnectionManager;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.Sessions;

// 소유: TSK-14-04. 「세션」 도구 창 ViewModel: 목록·행 값·우클릭 네 명령.
public sealed class SessionsViewModelTests
{
    /// <summary>접속 서비스·라우터·실행 상태 위에 도구 창 ViewModel 을 얹는다. 커밋·롤백·확인·끊기는 기록만 하는 가짜(<see cref="Ops"/>).</summary>
    internal sealed class Rig : IDisposable
    {
        public Rig()
        {
            Fx.Sessions.PublishOpened = true;
            State = new ExecutionState(Fx.Events);
            Registry = new DedicatedSessionRegistry(Host, State, Fx.Events);
            Router = new SessionRouter(Fx.Service, Host, State, Registry);
            Ops.State = State;
            Vm = new SessionsViewModel(Router, State, Ops, Fx.Events);
        }

        public ServiceFixture Fx { get; } = new();

        public SqlEditorHost Host { get; } = new();

        public ExecutionState State { get; }

        public DedicatedSessionRegistry Registry { get; }

        public SessionRouter Router { get; }

        public Ops Ops { get; } = new();

        public IConnectionService Service => Fx.Service;

        public SessionsViewModel Vm { get; }

        public async Task<ISession> ConnectAsync(string name = "a")
        {
            var profile = await Fx.Service.SaveProfileAsync(new ConnectionProfile(name, "scott", "localhost:1521/FREEPDB1"), null, false, default);
            var outcome = await Fx.Service.ConnectAsync(profile, "pw-" + name, default);
            Assert.True(outcome.IsConnected, outcome.Message);
            Pump();
            return outcome.Session!;
        }

        public async Task<ISession> OpenExtraAsync(ISession of)
        {
            var outcome = await Fx.Service.OpenSessionAsync(of.Profile.Id, default);
            Assert.True(outcome.IsConnected, outcome.Message);
            Pump();
            return outcome.Session!;
        }

        public void Dispose()
        {
            Vm.Dispose();
            Fx.Dispose();
        }
    }

    internal sealed class Ops : ISessionOps
    {
        public List<string> Calls { get; } = [];

        /// <summary>미커밋 확인의 답(false = 사용자가 취소).</summary>
        public bool Confirm { get; set; } = true;

        /// <summary>넣으면 끊기가 접속 서비스까지 간다(기록은 그대로).</summary>
        public IConnectionService? Service { get; set; }

        /// <summary>커밋·롤백하면 그 세션의 미커밋 표시를 지운다(진짜 컨트롤러와 같다).</summary>
        public ExecutionState? State { get; set; }

        public Task CommitAsync(ISession session, CancellationToken ct)
        {
            Calls.Add("commit");
            State?.ApplyTransaction(session.Id, false);
            return Task.CompletedTask;
        }

        public Task RollbackAsync(ISession session, CancellationToken ct)
        {
            Calls.Add("rollback");
            State?.ApplyTransaction(session.Id, false);
            return Task.CompletedTask;
        }

        public Task<bool> ConfirmUncommittedAsync(ISession session, CancellationToken ct)
        {
            Calls.Add("confirm");
            return Task.FromResult(Confirm);
        }

        public Task DisconnectAsync(ISession session, CancellationToken ct)
        {
            Calls.Add("disconnect");
            return Service?.DisconnectAsync(session, ct) ?? Task.CompletedTask;
        }
    }

    internal static void Pump()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    [Fact]
    public Task The_list_has_a_row_per_open_session_in_connection_order_with_label_sid_and_workspace_count() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync("a");
        var b = await rig.ConnectAsync("b");
        var extra = await rig.OpenExtraAsync(a);
        var ws = rig.Host.EnsureWorkspace();
        var tab = rig.Host.NewDocument();
        Assert.True(await rig.Router.PrepareAsync(tab));

        rig.Vm.Refresh();

        Assert.Equal(["1 - scott@a", "1 - scott@a (2)", "2 - scott@b"], rig.Vm.Rows.Select(r => r.Label));
        Assert.Equal([a, extra, b], rig.Vm.Rows.Select(r => r.Session));
        Assert.All(rig.Vm.Rows, r => Assert.Equal("SID " + r.Session.Server.SessionId, r.SidText));
        var first = rig.Vm.Rows[0];
        var last = rig.Vm.Rows[2];
        Assert.Equal(1, last.WorkspaceCount); // 탭은 마지막에 연 접속(b)을 물려받아 그 세션에 묶였다
        Assert.Equal(0, first.WorkspaceCount);
        Assert.True(first.IsPrimary);
        Assert.False(rig.Vm.Rows[1].IsPrimary);
        Assert.Contains(ws.Name, last.ToolTip, StringComparison.Ordinal);
        Assert.Equal(0, rig.Vm.Rows[1].WorkspaceCount);
        Assert.Contains("미사용", rig.Vm.Rows[1].Detail, StringComparison.Ordinal);
        Assert.Equal("열린 세션 3", rig.Vm.StatusMessage);
        Assert.False(rig.Vm.IsEmpty);
    });

    [Fact]
    public Task No_open_session_shows_the_empty_state() => Ui.Run(() =>
    {
        using var rig = new Rig();
        rig.Vm.Refresh();

        Assert.Empty(rig.Vm.Rows);
        Assert.True(rig.Vm.IsEmpty);
        Assert.Equal(string.Empty, rig.Vm.StatusMessage);
        Assert.False(string.IsNullOrWhiteSpace(rig.Vm.EmptyHint));
    });

    [Fact]
    public Task Opening_and_closing_sessions_updates_the_list_keeping_row_objects_and_selection() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync("a");
        Assert.Single(rig.Vm.Rows); // 처음 읽을 때 이벤트를 듣기 시작한다
        var rowA = rig.Vm.Rows[0];
        rig.Vm.Selected = rowA;

        var extra = await rig.OpenExtraAsync(a);
        Pump();
        Assert.Equal(2, rig.Vm.Rows.Count);
        Assert.Same(rowA, rig.Vm.Rows[0]); // 같은 세션의 행은 그대로(선택·열린 메뉴 유지)
        Assert.Same(rowA, rig.Vm.Selected);

        await rig.Fx.Service.DisconnectAsync(extra, default);
        Pump();
        Assert.Single(rig.Vm.Rows);
        Assert.Same(rowA, rig.Vm.Rows[0]);
        Assert.Same(rowA, rig.Vm.Selected);
    });

    [Fact]
    public Task A_row_shows_running_and_uncommitted_state() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var extra = await rig.OpenExtraAsync(a);
        rig.Vm.Refresh();
        var row = rig.Vm.Rows[1];
        Assert.False(row.IsRunning);
        Assert.False(row.IsPending);
        Assert.Contains("대기", row.Detail, StringComparison.Ordinal);

        rig.State.BeginRun(extra.Id);
        Pump();
        Assert.True(row.IsRunning);
        Assert.Contains("실행 중", row.Detail, StringComparison.Ordinal);
        Assert.False(rig.Vm.CanCommit(row)); // 실행 중에는 커밋·롤백하지 않는다
        rig.State.EndRun(extra.Id);

        rig.State.ApplyTransaction(extra.Id, true);
        Pump();
        Assert.True(row.IsPending);
        Assert.Contains("미커밋", row.Detail, StringComparison.Ordinal);
        Assert.True(rig.Vm.CanCommit(row));
        Assert.True(rig.Vm.CanRollback(row));
        Assert.False(rig.Vm.CanCommit(rig.Vm.Rows[0])); // 다른 세션의 미커밋은 아니다
    });

    [Fact]
    public Task Use_this_session_connects_the_active_workspace_to_the_session() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var extra = await rig.OpenExtraAsync(a);
        var ws = rig.Host.EnsureWorkspace();
        var tab = rig.Host.NewDocument();
        Assert.True(await rig.Router.PrepareAsync(tab));
        rig.Host.Active = tab;
        Pump();
        rig.Vm.Refresh();
        var rowA = rig.Vm.Rows[0];
        var rowExtra = rig.Vm.Rows[1];
        Assert.False(rig.Vm.CanUse(rowA)); // 이미 쓰는 세션
        Assert.True(rig.Vm.CanUse(rowExtra));

        await rig.Vm.UseAsync(rowExtra);

        Assert.Same(extra, rig.Router.SessionFor(tab));
        Assert.Equal([ws], rig.Router.WorkspacesOn(extra));
        Assert.Equal(1, rowExtra.WorkspaceCount);
        Assert.Equal(0, rowA.WorkspaceCount);
        Assert.True(rig.Vm.CanUse(rowA));
        Assert.False(rig.Vm.CanUse(rowExtra));
    });

    [Fact]
    public Task Use_this_session_is_unavailable_while_a_non_sql_document_is_active() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        await rig.OpenExtraAsync(a);
        rig.Host.EnsureWorkspace();
        rig.Host.NewDocument(); // 쓸 탭이 있어야 켜진다(탭 없는 작업 공간이면 효과가 없어 꺼진다, TSK-14-10)
        rig.Vm.Refresh();
        Assert.True(rig.Vm.CanUse(rig.Vm.Rows[1]));

        Assert.False(rig.Vm.CanUse(null));
        rig.Registry.SqlDocumentActive = () => false; // PL/SQL 문서처럼 SQL 작업 공간이 아닌 문서가 활성
        Assert.False(rig.Vm.CanUse(rig.Vm.Rows[1]));
    });

    [Fact]
    public Task Commit_and_rollback_go_to_the_selected_session_only_when_it_has_uncommitted_changes() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var extra = await rig.OpenExtraAsync(a);
        rig.Vm.Refresh();

        await rig.Vm.CommitAsync(rig.Vm.Rows[1]);
        await rig.Vm.RollbackAsync(rig.Vm.Rows[1]);
        Assert.Empty(rig.Ops.Calls); // 미커밋이 없으면 아무것도 하지 않는다

        rig.State.ApplyTransaction(extra.Id, true);
        Pump();
        await rig.Vm.CommitAsync(rig.Vm.Rows[1]);
        Assert.False(rig.Vm.Rows[1].IsPending);
        rig.State.ApplyTransaction(extra.Id, true);
        Pump();
        await rig.Vm.RollbackAsync(rig.Vm.Rows[1]);
        Assert.Equal(["commit", "rollback"], rig.Ops.Calls);
    });

    [Fact]
    public Task Closing_a_session_asks_once_about_uncommitted_changes_then_disconnects() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        await rig.OpenExtraAsync(a);
        rig.Vm.Refresh();

        await rig.Vm.CloseAsync(rig.Vm.Rows[1]);

        Assert.Equal(["confirm", "disconnect"], rig.Ops.Calls);
    });

    [Fact]
    public Task Closing_a_session_stops_when_the_user_cancels_the_uncommitted_question() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        await rig.OpenExtraAsync(a);
        rig.Vm.Refresh();
        rig.Ops.Confirm = false;

        await rig.Vm.CloseAsync(rig.Vm.Rows[1]);

        Assert.Equal(["confirm"], rig.Ops.Calls);
    });

    [Fact]
    public Task Closing_a_running_session_is_refused_without_asking() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var extra = await rig.OpenExtraAsync(a);
        rig.Vm.Refresh();
        rig.State.BeginRun(extra.Id);

        await rig.Vm.CloseAsync(rig.Vm.Rows[1]);

        Assert.Empty(rig.Ops.Calls);
        Assert.Contains(ExecutionLog.For(rig.Fx.Events).Messages, m => m.Kind == MessageKind.Warning && m.Text.Contains("실행 중입니다", StringComparison.Ordinal));
        rig.State.EndRun(extra.Id);
    });

    [Fact]
    public Task Closing_a_session_makes_the_workspaces_using_it_not_connected() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var extra = await rig.OpenExtraAsync(a);
        rig.Host.EnsureWorkspace();
        var tab = rig.Host.NewDocument();
        Assert.True(await rig.Router.PrepareAsync(tab));
        rig.Host.Active = tab;
        Pump();
        rig.Vm.Refresh();
        await rig.Vm.UseAsync(rig.Vm.Rows[1]);
        Assert.Same(extra, rig.Router.SessionFor(tab));

        await rig.Fx.Service.DisconnectAsync(extra, default); // 실제 끊기(가짜 동작 대신)
        Pump();

        Assert.Single(rig.Vm.Rows);
        Assert.Null(rig.Router.SessionFor(tab)); // 쓰던 워크스페이스는 접속 안 됨
        Assert.Equal(a.Profile.Id, rig.Router.MissingProfileOf(tab));
    });

    [Fact]
    public Task An_extra_session_nobody_uses_can_be_closed_even_with_uncommitted_changes_after_the_question() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Ops.Service = rig.Service;
        var a = await rig.ConnectAsync();
        var extra = await rig.OpenExtraAsync(a);
        rig.State.ApplyTransaction(extra.Id, true); // 쓰는 작업 공간 없이 미커밋으로 남은 세션
        rig.Vm.Refresh();
        Assert.Equal(0, rig.Vm.Rows[1].WorkspaceCount);

        await rig.Vm.CloseAsync(rig.Vm.Rows[1]);

        Assert.Equal(["confirm", "disconnect"], rig.Ops.Calls);
        Assert.Single(rig.Vm.Rows);
        Assert.Single(rig.Service.SessionsOf(a.Profile.Id));
    });

    [Fact]
    public Task A_broken_extra_session_closes_by_itself_and_frees_a_slot_under_the_limit() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Ops.Service = rig.Service;
        var a = await rig.ConnectAsync();
        for (var i = 1; i < Oranda.App.Features.ConnectionManager.ConnectionService.MaxSessionsPerProfile; i++)
        {
            await rig.OpenExtraAsync(a);
        }

        Assert.False((await rig.Service.OpenSessionAsync(a.Profile.Id, default)).IsConnected); // 상한 8
        var broken = rig.Service.SessionsOf(a.Profile.Id)[3];
        rig.Fx.Events.Publish(new Oranda.Core.Events.SessionBroken(broken.Id, false, null));
        Pump();

        // 끊긴 추가 세션은 다시 열 자리가 없어 접속 서비스가 닫는다(TSK-14-07): 쓰던 워크스페이스는 「접속 안 됨」 이 되어 다시 고른다.
        Assert.False(rig.Service.IsOpen(broken));
        rig.Vm.Refresh();
        Assert.Equal(8 - 1, rig.Service.SessionsOf(a.Profile.Id).Count);
        Assert.DoesNotContain(rig.Vm.Rows, r => ReferenceEquals(r.Session, broken));
        Assert.True((await rig.Service.OpenSessionAsync(a.Profile.Id, default)).IsConnected); // 자리가 났다
    });
}
