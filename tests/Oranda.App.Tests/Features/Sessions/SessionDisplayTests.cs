using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.DedicatedSession;
using Oranda.App.Features.Execution;
using Oranda.App.Features.Sessions;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.ConnectionManager;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.Sessions;

/// <summary>
/// 결함(TSK-14-10, D-042): 세션 표시가 실제 실행 세션과 갈리던 것들(review 7·17·21~26).
/// 보이는 세션 = 실행할 세션이고, 세션 창은 쓰는 세션·곧 쓸 세션을 밝히며, 순번은 닫아도 밀리지 않는다.
/// </summary>
public sealed class SessionDisplayTests
{
    private static void Pump() => SessionsViewModelTests.Pump();

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            Pump();
            await Task.Delay(10);
        }
    }

    /// <summary>A(기본 작업 공간)가 접속의 세션 P 를 쓰고, B(활성)는 「자기 세션」 인데 아직 실행하지 않아 첫 실행 때 새 세션을 연다.</summary>
    private static async Task<(ISession P, SqlWorkspaceViewModel A, SqlWorkspaceViewModel B, SqlEditorViewModel TabB)> PendingAsync(SessionsViewModelTests.Rig rig)
    {
        var p = await rig.ConnectAsync();
        var a = rig.Host.EnsureWorkspace();
        var tabA = rig.Host.NewDocument();
        Assert.True(await rig.Router.PrepareAsync(tabA));
        var b = rig.Host.NewWorkspace();
        var tabB = b.Tabs[0];
        rig.Host.Active = tabB;
        Pump();
        Assert.True(rig.Router.IsPendingOwn(tabB));
        Assert.Same(p, rig.Router.SessionFor(tabB)); // 읽기용으로는 접속의 세션
        return (p, a, b, tabB);
    }

    // ---------- 7: 첫 실행 때 새 세션 ----------

    [Fact]
    public Task A_workspace_waiting_for_its_own_session_shows_new_session_on_first_run_not_the_connection_session() => Ui.Run(async () =>
    {
        using var rig = new SessionsViewModelTests.Rig();
        var (p, _, _, _) = await PendingAsync(rig);
        var combo = new ConnectionComboViewModel(rig.Fx.Events, router: () => rig.Router);
        var status = new ConnectionStatusViewModel(rig.Fx.Events, router: () => rig.Router);

        Assert.True(rig.Router.ShownPendingNew);
        Assert.Null(combo.Selected); // P 줄을 고른 채로 보이지 않는다: 실행은 P 가 아니다
        Assert.Contains("첫 실행", combo.PlaceholderText, StringComparison.Ordinal);
        Assert.Contains("scott@a", combo.PlaceholderText, StringComparison.Ordinal);
        Assert.Contains("첫 실행", status.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("SID " + p.Server.SessionId, status.ToolTip, StringComparison.Ordinal);
        Assert.Equal(p.Id, status.Current?.SessionId); // 환경 칩은 같은 DB 라 그 접속을 따른다
    });

    [Fact]
    public Task Picking_the_connection_session_in_the_combo_while_waiting_binds_the_workspace_to_it_as_shared() => Ui.Run(async () =>
    {
        using var rig = new SessionsViewModelTests.Rig();
        var (p, _, b, tabB) = await PendingAsync(rig);
        var combo = new ConnectionComboViewModel(rig.Fx.Events, router: () => rig.Router);

        Assert.Equal(SessionSelect.Selected, await combo.ChooseAsync(combo.Items.Single(i => ReferenceEquals(i.Session, p))));
        Pump();

        Assert.False(rig.Router.IsPendingOwn(tabB));
        Assert.Equal(WorkspaceSessionMode.Shared, b.SessionMode);
        Assert.Same(p, rig.Router.SessionFor(tabB));
        Assert.Same(p, combo.Selected?.Session);
        Assert.True(await rig.Router.PrepareAsync(tabB));
        Assert.Same(p, rig.Router.SessionFor(tabB)); // 실행해도 새 세션을 열지 않는다
        Assert.Equal([p], rig.Service.SessionsOf(p.Profile.Id));
    });

    [Fact]
    public Task Using_the_connection_session_from_the_sessions_window_while_waiting_binds_it_as_shared() => Ui.Run(async () =>
    {
        using var rig = new SessionsViewModelTests.Rig();
        var (p, _, b, tabB) = await PendingAsync(rig);
        rig.Vm.Refresh();
        var row = rig.Vm.Rows.Single(r => ReferenceEquals(r.Session, p));

        Assert.True(rig.Vm.CanUse(row)); // 보이는 세션이어도 실제로 쓰는 세션이 아니다
        Assert.False(row.IsShown);
        await rig.Vm.ActivateAsync(row);

        Assert.Equal(WorkspaceSessionMode.Shared, b.SessionMode);
        Assert.False(rig.Router.IsPendingOwn(tabB));
        Assert.True(await rig.Router.PrepareAsync(tabB));
        Assert.Equal([p], rig.Service.SessionsOf(p.Profile.Id));
        Assert.True(rig.Vm.Rows.Single(r => ReferenceEquals(r.Session, p)).IsShown);
    });

    // ---------- 17: 곧 쓸 세션 ----------

    [Fact]
    public Task A_connection_session_an_unrun_workspace_will_use_is_marked_planned_not_unused() => Ui.Run(async () =>
    {
        using var rig = new SessionsViewModelTests.Rig();
        var p = await rig.ConnectAsync();
        var extra = await rig.OpenExtraAsync(p);
        var ws = rig.Host.EnsureWorkspace();
        rig.Host.NewDocument(); // 아직 실행하지 않았다: 첫 실행 때 P 를 쓴다
        Pump();
        rig.Vm.Refresh();

        var rowP = rig.Vm.Rows.Single(r => ReferenceEquals(r.Session, p));
        Assert.Equal(0, rowP.WorkspaceCount);
        Assert.Equal(1, rowP.PlannedCount);
        Assert.Contains("사용 예정", rowP.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("미사용", rowP.Detail, StringComparison.Ordinal);
        Assert.Contains(ws.Name, rowP.ToolTip, StringComparison.Ordinal);
        var rowExtra = rig.Vm.Rows.Single(r => ReferenceEquals(r.Session, extra));
        Assert.Contains("미사용", rowExtra.Detail, StringComparison.Ordinal);
        Assert.Equal(0, rowExtra.PlannedCount);
    });

    [Fact]
    public Task A_waiting_own_workspace_is_not_counted_as_planned_for_the_connection_session() => Ui.Run(async () =>
    {
        using var rig = new SessionsViewModelTests.Rig();
        var (p, _, _, _) = await PendingAsync(rig);
        rig.Vm.Refresh();

        var rowP = rig.Vm.Rows.Single(r => ReferenceEquals(r.Session, p));
        Assert.Equal(1, rowP.WorkspaceCount); // A
        Assert.Equal(0, rowP.PlannedCount); // B 는 새 세션을 연다
    });

    // ---------- 23: 지금 쓰는 세션 표지 ----------

    [Fact]
    public Task The_sessions_window_marks_the_session_the_active_workspace_uses_apart_from_the_selection() => Ui.Run(async () =>
    {
        using var rig = new SessionsViewModelTests.Rig();
        var p = await rig.ConnectAsync();
        rig.Host.EnsureWorkspace();
        var tabA = rig.Host.NewDocument();
        Assert.True(await rig.Router.PrepareAsync(tabA));
        rig.Host.NewWorkspace();
        var own = (await rig.Router.NewSessionAsync())!;
        Pump();
        rig.Vm.Refresh();

        Assert.True(rig.Vm.Rows.Single(r => ReferenceEquals(r.Session, own)).IsShown);
        Assert.False(rig.Vm.Rows.Single(r => ReferenceEquals(r.Session, p)).IsShown);
        rig.Vm.Selected = rig.Vm.Rows.Single(r => ReferenceEquals(r.Session, p)); // 고른 줄과 따로다
        Assert.True(rig.Vm.Rows.Single(r => ReferenceEquals(r.Session, own)).IsShown);

        rig.Host.Active = tabA;
        await SettleAsync();
        Assert.True(rig.Vm.Rows.Single(r => ReferenceEquals(r.Session, p)).IsShown);
        Assert.False(rig.Vm.Rows.Single(r => ReferenceEquals(r.Session, own)).IsShown);
    });

    // ---------- 24: 효과 없는 「이 세션 쓰기」 ----------

    [Fact]
    public Task Use_this_session_is_off_for_a_dedicated_session_tab_and_a_workspace_without_tabs() => Ui.Run(async () =>
    {
        using var rig = new SessionsViewModelTests.Rig();
        var p = await rig.ConnectAsync();
        var extra = await rig.OpenExtraAsync(p);
        rig.Host.EnsureWorkspace();
        var tab = rig.Host.NewDocument();
        Pump();
        rig.Vm.Refresh();
        var rowExtra = rig.Vm.Rows.Single(r => ReferenceEquals(r.Session, extra));
        Assert.True(rig.Vm.CanUse(rowExtra));

        var dedicated = await rig.OpenExtraAsync(p);
        rig.Registry.Attach(tab, dedicated); // 전용 세션 탭: 콤보·세션 창으로 세션을 바꾸지 않는다
        Pump();
        Assert.False(rig.Vm.CanUse(rowExtra));
        var changed = 0;
        rig.Router.Changed += (_, _) => changed++;
        await rig.Vm.ActivateAsync(rowExtra);
        Assert.Equal(0, changed);

        var empty = rig.Host.NewWorkspace();
        rig.Host.Close(empty.Tabs[0]); // 탭 없는 작업 공간
        Pump();
        Assert.Null(rig.Host.Active);
        Assert.False(rig.Vm.CanUse(rowExtra));
        changed = 0; // 작업 공간을 열고 탭을 닫은 바뀜은 세지 않는다
        await rig.Vm.ActivateAsync(rowExtra);
        Assert.Equal(0, changed);
    });

    // ---------- 25: 고정 순번 ----------

    [Fact]
    public Task Session_numbers_stay_fixed_when_an_earlier_extra_session_closes() => Ui.Run(async () =>
    {
        using var rig = new SessionsViewModelTests.Rig();
        var p = await rig.ConnectAsync();
        var s2 = await rig.OpenExtraAsync(p);
        var s3 = await rig.OpenExtraAsync(p);
        Assert.Equal("1 - scott@a (2)", rig.Router.LabelOf(s2));
        Assert.Equal("1 - scott@a (3)", rig.Router.LabelOf(s3));

        await rig.Service.DisconnectAsync(s2, default);
        Pump();
        Assert.Equal("1 - scott@a (3)", rig.Router.LabelOf(s3)); // 위치로 (2) 로 밀리지 않는다
        Assert.Equal("1 - scott@a", rig.Router.LabelOf(p));

        rig.Host.EnsureWorkspace();
        rig.Host.NewDocument();
        Pump();
        var combo = new ConnectionComboViewModel(rig.Fx.Events, router: () => rig.Router);
        Assert.Equal("새 세션 — scott@a (4)", combo.Entries[^1].Label); // 다음에 열 세션의 번호
        var s4 = await rig.OpenExtraAsync(p);
        Assert.Equal("1 - scott@a (4)", rig.Router.LabelOf(s4));

        foreach (var s in new[] { s4, s3, p })
        {
            await rig.Service.DisconnectAsync(s, default);
        }

        Pump();
        Assert.True((await rig.Service.ConnectAsync(p.Profile, "pw-a", default)).IsConnected); // 모두 닫은 뒤 같은 DB 에 다시 접속
        Pump();
        var again = rig.Service.SessionOf(p.Profile.Id)!;
        var next = await rig.OpenExtraAsync(again);
        Assert.Equal("1 - scott@a", rig.Router.LabelOf(again));
        Assert.Equal("1 - scott@a (2)", rig.Router.LabelOf(next)); // 다시 1 부터
    });

    // ---------- 22: 설정 바꿈 ----------

    [Fact]
    public Task Changing_the_session_per_workspace_setting_resyncs_the_display() => Ui.Run(async () =>
    {
        using var fx = new ServiceFixture();
        fx.Sessions.PublishOpened = true;
        var host = new SqlEditorHost();
        var state = new ExecutionState(fx.Events);
        var perWorkspace = true;
        var router = new SessionRouter(fx.Service, host, state, new DedicatedSessionRegistry(host, state, fx.Events), fx.Events)
        {
            SessionPerWorkspace = () => perWorkspace,
        };
        var profile = await fx.Service.SaveProfileAsync(new ConnectionProfile("a", "scott", "localhost:1521/FREEPDB1"), null, false, default);
        Assert.True((await fx.Service.ConnectAsync(profile, "pw", default)).IsConnected);
        host.EnsureWorkspace();
        var tabA = host.NewDocument();
        Assert.True(await router.PrepareAsync(tabA));
        host.NewWorkspace();
        Pump();
        Assert.True(router.ShownPendingNew);
        Assert.True(state.FocusedPending);
        var changed = 0;
        router.Changed += (_, _) => changed++;

        perWorkspace = false; // 끄면 B 는 접속의 세션을 함께 쓴다
        fx.Events.Publish(new SettingsChanged("workspace"));
        await SettleAsync();

        Assert.True(changed > 0);
        Assert.False(router.ShownPendingNew);
        Assert.False(state.FocusedPending);
    });

    // ---------- 21: 닫힌 콤보의 화살표·휠 ----------

    [Fact]
    public Task Arrow_keys_and_the_wheel_on_the_closed_combo_do_not_bind_each_session_passed() => Ui.Run(async () =>
    {
        using var rig = new SessionsViewModelTests.Rig();
        var p = await rig.ConnectAsync();
        var s2 = await rig.OpenExtraAsync(p);
        await rig.OpenExtraAsync(p);
        rig.Host.EnsureWorkspace();
        var tab = rig.Host.NewDocument();
        Assert.True(await rig.Router.PrepareAsync(tab));
        Assert.Same(p, rig.Router.SessionFor(tab));
        var combo = new ConnectionComboViewModel(rig.Fx.Events, router: () => rig.Router);
        var view = new ConnectionComboView { DataContext = combo };
        var window = new Window { Width = 400, Height = 300, Content = view };
        window.Show();
        try
        {
            await SettleAsync();
            view.Combo.Focus();
            Pump();

            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            await SettleAsync();
            Assert.Same(p, rig.Router.SessionFor(tab)); // 지나는 줄을 묶지 않는다
            Assert.True(view.Combo.IsDropDownOpen); // 대신 목록을 펼친다

            view.Combo.IsDropDownOpen = false;
            await SettleAsync();
            var center = view.Combo.TranslatePoint(new Point(20, view.Combo.Bounds.Height / 2), window)!.Value;
            window.MouseWheel(center, new Vector(0, -1));
            window.MouseWheel(center, new Vector(0, -1));
            await SettleAsync();
            Assert.Same(p, rig.Router.SessionFor(tab));
            Assert.Same(p, combo.Selected?.Session);

            // 펼친 목록에서 고르면 묶는다.
            view.Combo.IsDropDownOpen = true;
            await SettleAsync();
            view.Combo.SelectedItem = view.Combo.Items.OfType<ConnectionComboItem>().Single(i => ReferenceEquals(i.Session, s2));
            view.Combo.IsDropDownOpen = false;
            await SettleAsync();
            Assert.Same(s2, rig.Router.SessionFor(tab));
        }
        finally
        {
            window.Close();
        }
    });

    // ---------- 26: 공유 세션 커밋 경고 ----------

    [Fact]
    public Task Committing_a_session_shared_by_several_workspaces_names_them_and_asks_once() => Ui.Run(async () =>
    {
        using var rig = new SessionsViewModelTests.Rig();
        var p = await rig.ConnectAsync();
        var a = rig.Host.EnsureWorkspace();
        var tabA = rig.Host.NewDocument();
        var b = rig.Host.NewWorkspace();
        b.SessionMode = WorkspaceSessionMode.Shared;
        Assert.True(await rig.Router.PrepareAsync(tabA));
        Assert.True(await rig.Router.PrepareAsync(b.Tabs[0]));
        Assert.Equal([a, b], rig.Router.WorkspacesOn(p));
        var dialogs = new FakeDialogs { SharedAnswer = false };
        var controller = new ExecutionController(new ExecutionDeps
        {
            Host = () => rig.Host,
            State = rig.State,
            Log = new ExecutionLog(null, rig.State),
            Dialogs = dialogs,
            Session = () => rig.Router.Current,
            ConnectionLabel = rig.Router.LabelOf,
            SharedWith = s => [.. rig.Router.WorkspacesOn(s).Select(w => w.Name)],
            Execution = () => throw new NotSupportedException(),
            Language = () => throw new NotSupportedException(),
        });

        rig.State.ApplyTransaction(p.Id, true);
        await controller.ConfirmedCommitAsync(p);
        Assert.True(rig.State.IsTxnPending(p.Id)); // 취소: 커밋하지 않는다
        var asked = Assert.Single(dialogs.SharedAsked);
        Assert.Equal([a.Name, b.Name], asked.Workspaces);
        Assert.True(asked.Commit);

        dialogs.SharedAnswer = true;
        await controller.ConfirmedRollbackAsync(p);
        Assert.False(rig.State.IsTxnPending(p.Id));
        Assert.False(dialogs.SharedAsked[^1].Commit);

        // 한 작업 공간만 쓰는 세션은 묻지 않는다.
        var own = (await rig.Router.NewSessionAsync())!;
        rig.State.ApplyTransaction(own.Id, true);
        await controller.ConfirmedCommitAsync(own);
        Assert.Equal(2, dialogs.SharedAsked.Count);
        Assert.False(rig.State.IsTxnPending(own.Id));
    });
}
