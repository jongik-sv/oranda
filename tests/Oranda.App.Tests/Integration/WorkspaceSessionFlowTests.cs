using System.Diagnostics;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Xunit.Abstractions;

namespace Oranda.App.Tests.Integration;

/// <summary>
/// TSK-14-01 같은 DB 여러 세션·워크스페이스별 실행: 같은 프로필로 연 두 작업 공간이 실제 서버에서 어느 세션(SID)을 쓰는지,
/// 한쪽이 오래 도는 동안 다른 쪽이 바로 실행되는지, 트랜잭션이 세션마다 갈리는지 본다.
/// </summary>
[Trait("Category", "Integration")]
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class WorkspaceSessionFlowTests(ItDb db, ITestOutputHelper output) : IClassFixture<ItDb>
{
    /// <summary>
    /// 오래 걸리는 문장. 11g 는 dbms_session.sleep 이 없고 dbms_lock 은 SYS 만 권한을 줄 수 있어 dbms_utility.get_time(PUBLIC)으로 그만큼 돈다.
    /// </summary>
    private string SleepSql(int seconds) => db.Service.Equals("XE", StringComparison.OrdinalIgnoreCase)
        ? $"declare t0 number := dbms_utility.get_time; begin while dbms_utility.get_time - t0 < {seconds * 100} loop null; end loop; end;"
        : $"begin dbms_session.sleep({seconds}); end;";

    private static async Task<string> RunAsync(FlowRig rig, SqlEditorViewModel tab, string sql)
    {
        rig.Host.Active = tab;
        tab.LoadText(sql, false);
        tab.Select(0, 0);
        Assert.True(await rig.Controller.RunAsync(RunMode.Statement, tab: tab), "실행하지 않았다: " + sql);
        var results = (ResultTabsViewModel)tab.ResultPane!;
        return results.Selected?.Pane?.Model is null ? string.Empty : await FlowRig.CellAsync(results.Selected);
    }

    private const string SidSql = "select sys_context('userenv','sid') from dual";

    /// <summary>앱이 연 세션을 모두 닫는다(FlowRig 는 지금 접속 하나만 닫는다).</summary>
    private static async Task CloseAllAsync(FlowRig rig)
    {
        foreach (var session in rig.Service.AllSessions.Reverse().ToList())
        {
            await rig.Service.DisconnectAsync(session, default);
        }
    }

    [Fact]
    public Task Two_workspaces_on_one_profile_run_side_by_side_on_their_own_sessions() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        var t = "WS_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.Exec($"create table {t} (id number)");
        try
        {
            await rig.ConnectAsync();
            var sleep = SleepSql(5);
            var a = rig.Host.NewDocument();
            var wsB = rig.Host.NewWorkspace();
            var b = wsB.Tabs[0];
            var router = SessionRouter.Resolve(rig.Container);

            // 같은 프로필, 작업 공간 둘 = 서버 세션 둘(SID 가 다르다). 첫 작업 공간은 접속의 세션을 그대로 쓴다.
            var sidA = await RunAsync(rig, a, SidSql);
            var sidB = await RunAsync(rig, b, SidSql);
            output.WriteLine($"SID A={sidA} B={sidB}");
            Assert.NotEqual(sidA, sidB);
            Assert.Same(rig.Service.ActiveSession, router.SessionFor(a));
            Assert.Equal(2, rig.Service.SessionsOf(rig.Session.Profile.Id).Count);
            Assert.Single(rig.Service.Sessions);

            // A 가 5초 도는 동안 B 는 바로 실행된다.
            rig.Host.Active = a;
            a.LoadText(sleep, false);
            a.Select(0, 0);
            var watch = Stopwatch.StartNew();
            var running = rig.Controller.RunAsync(RunMode.Statement, tab: a);
            await HeadlessHost.WaitUntil(() => rig.State.IsRunning(router.SessionFor(a)!.Id));
            rig.Host.Active = b;
            Assert.False(rig.State.ShownBusy); // B 의 실행 명령은 켜져 있다
            var started = watch.ElapsedMilliseconds;
            Assert.Equal("1", await RunAsync(rig, b, "select 1 from dual"));
            var bMs = watch.ElapsedMilliseconds - started;
            output.WriteLine($"A 실행 중 B 실행: {bMs} ms");
            Assert.True(bMs < 1000, $"B 가 {bMs} ms 걸렸다");
            Assert.True(rig.State.IsRunning(router.SessionFor(a)!.Id));
            Assert.True(await running);
            output.WriteLine($"A 끝: {watch.ElapsedMilliseconds} ms");

            // 트랜잭션은 세션마다: A 의 미커밋 INSERT 는 B 에 보이지 않는다.
            await RunAsync(rig, a, $"insert into {t} values (1)");
            Assert.Equal("1", await RunAsync(rig, a, $"select count(*) from {t}"));
            Assert.Equal("0", await RunAsync(rig, b, $"select count(*) from {t}"));
            Assert.True(rig.State.IsTxnPending(router.SessionFor(a)!.Id));
            Assert.False(rig.State.IsTxnPending(router.SessionFor(b)!.Id));
            await rig.Controller.RollbackAsync(router.SessionFor(a));
        }
        finally
        {
            await CloseAllAsync(rig);
            db.Exec($"drop table {t} purge");
        }
    });

    [Fact]
    public Task Workspaces_can_share_one_session_and_move_to_a_new_one() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        var t = "WS_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.Exec($"create table {t} (id number)");
        try
        {
            await rig.ConnectAsync();
            var a = rig.Host.NewDocument();
            var wsB = rig.Host.NewWorkspace();
            var b = wsB.Tabs[0];
            var router = SessionRouter.Resolve(rig.Container);
            await RunAsync(rig, a, SidSql);
            await RunAsync(rig, b, SidSql);
            var sessionB = router.SessionFor(b)!;

            // A 가 B 의 세션을 고르면 둘이 같은 세션(SID)·같은 트랜잭션을 쓴다.
            rig.Host.Active = a;
            Assert.Equal(SessionSelect.Selected, await router.SelectAsync(sessionB));
            var sidA = await RunAsync(rig, a, SidSql);
            var sidB = await RunAsync(rig, b, SidSql);
            output.WriteLine($"함께 쓰기 SID A={sidA} B={sidB}");
            Assert.Equal(sidA, sidB);
            await RunAsync(rig, a, $"insert into {t} values (1)");
            Assert.Equal("1", await RunAsync(rig, b, $"select count(*) from {t}")); // 같은 트랜잭션이라 보인다
            Assert.Equal("0", db.Text($"select count(*) from {t}"));

            // 함께 쓰는 세션이 실행 중이면 다른 쪽도 막힌다(sessionBusy 는 세션 기준).
            rig.State.BeginRun(sessionB.Id);
            rig.Host.Active = a;
            Assert.True(rig.State.ShownBusy);
            rig.State.EndRun(sessionB.Id);
            await rig.Controller.RollbackAsync(sessionB);

            // 「새 세션」 으로 옮기면 SID 가 갈린다.
            rig.Host.Active = a;
            Assert.NotNull(await router.NewSessionAsync());
            var moved = await RunAsync(rig, a, SidSql);
            output.WriteLine($"새 세션 SID A={moved} B={sidB}");
            Assert.NotEqual(sidB, moved);
        }
        finally
        {
            await CloseAllAsync(rig);
            db.Exec($"drop table {t} purge");
        }
    });
}
