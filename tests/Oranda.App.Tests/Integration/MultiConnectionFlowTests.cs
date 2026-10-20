using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;
using Xunit.Abstractions;

namespace Oranda.App.Tests.Integration;

/// <summary>
/// TSK-03-08 동시 접속 여러 개(CONN-06): 상주 컨테이너 26ai 에 서로 다른 사용자 둘로 동시에 접속해 탭마다 자기 접속에서 실행하고,
/// 미커밋은 접속마다 따로 남고 앱 종료 확인이 접속마다 묻고, 닫힌 접속에 묶인 탭은 다른 접속으로 몰래 실행하지 않는지 본다.
/// </summary>
[Trait("Category", "Integration")]
[Collection("GridEditStatics")]
public sealed class MultiConnectionFlowTests(ItDb db, ITestOutputHelper output) : IClassFixture<ItDb>
{
    private static async Task<ISession> OpenAsync(FlowRig rig, ConnectionProfile profile, string password)
    {
        var outcome = await rig.Service.ConnectAsync(profile, password, default);
        Assert.True(outcome.IsConnected, outcome.Message);
        await HeadlessHost.WaitUntil(() => rig.State.SessionId == outcome.Session!.Id);
        return outcome.Session!;
    }

    private static async Task<string> RunAsync(FlowRig rig, SqlEditorViewModel tab, string sql)
    {
        rig.Host.Active = tab;
        tab.LoadText(sql, false);
        tab.Select(0, 0);
        await rig.Run("oranda.exec.runStatement");
        var results = (ResultTabsViewModel)tab.ResultPane!;
        return results.Selected?.Pane?.Model is null ? string.Empty : await FlowRig.CellAsync(results.Selected);
    }

    [Fact]
    public Task Two_tabs_with_the_same_bind_name_run_with_their_own_values_on_a_live_server() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        await rig.ConnectAsync();

        // 같은 :ID 를 쓰는 두 탭: 각자 목록의 값으로 실행한다(TSK-04-11).
        var a = rig.Host.NewDocument("select :ID from dual");
        var b = rig.Host.NewDocument("select :ID from dual");
        a.Binds.Add("ID").Text = "11";
        b.Binds.Add("ID").Text = "22";
        a.Binds.Find("ID")!.Type = Oranda.Core.Execution.BindDataType.Number;
        b.Binds.Find("ID")!.Type = Oranda.Core.Execution.BindDataType.Number;

        Assert.Equal("11", await RunAsync(rig, a, "select :ID from dual"));
        Assert.Equal("22", await RunAsync(rig, b, "select :ID from dual"));
        Assert.Equal("11", a.Binds.Find("ID")!.Text);
    });

    [Fact]
    public Task Two_connections_run_side_by_side_and_keep_their_own_transactions() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var other = new ItDb();
        using var rig = new FlowRig(db);
        var t = "MC_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.Exec($"create table {t} (id number)");
        try
        {
            var profileA = await rig.Service.SaveProfileAsync(rig.Profile(name: "mc-a"), null, false, default);
            var profileB = await rig.Service.SaveProfileAsync(new ConnectionProfile("mc-b", other.User, other.DataSource, Id: Guid.NewGuid()), null, false, default);

            var a = await OpenAsync(rig, profileA, db.Password);
            var tabA = rig.Host.NewDocument();
            Assert.Equal(profileA.Id, tabA.ConnectionProfileId);
            await RunAsync(rig, tabA, $"insert into {t} values (1)");
            await HeadlessHost.WaitUntil(() => rig.State.PendingWorkSessions.Count == 1);

            // 두 번째 접속은 첫 접속을 닫지 않는다. 활성 탭(tabB)이 새 접속을 따른다(미커밋인 tabA 는 A 에 남는다).
            var tabB = rig.Host.NewDocument();
            var b = await OpenAsync(rig, profileB, other.Password);
            Assert.Equal(profileB.Id, tabB.ConnectionProfileId);
            Assert.Equal(profileA.Id, tabA.ConnectionProfileId);
            Assert.Equal(2, rig.Service.Sessions.Count);
            Assert.NotEqual(SessionState.Closed, a.State);
            Assert.Equal(other.User, await RunAsync(rig, tabB, "select user from dual"));

            // 탭을 바꾸면 그 탭의 접속에서 실행하고, 미커밋은 A 에만 있다.
            Assert.Equal(db.User, await RunAsync(rig, tabA, "select user from dual"));
            Assert.Same(a, rig.Service.ActiveSession);
            Assert.Equal("1", await RunAsync(rig, tabA, $"select count(*) from {t}"));
            Assert.Equal("0", db.Text($"select count(*) from {t}")); // 다른 세션에서는 아직 안 보인다
            Assert.Equal([a.Id], rig.State.PendingWorkSessions);

            // 앱 종료 확인은 미커밋이 있는 접속마다 접속 이름을 밝혀 묻는다.
            rig.Dialogs.UncommittedAnswer = UncommittedChoice.Commit;
            Assert.True(await rig.Controller.ConfirmCloseAsync());
            Assert.Equal([$"1 - {db.User}@mc-a"], rig.Dialogs.ConnectionsAsked);
            Assert.Equal("1", db.Text($"select count(*) from {t}"));

            // A 를 닫으면 tabA 는 「접속 안 됨」: 취소하면 실행하지 않고, 「지금 접속으로」 를 고르면 B 에서 실행한다.
            await rig.Service.DisconnectAsync(a, default);
            await HeadlessHost.WaitUntil(() => rig.Service.Sessions.Count == 1);
            var router = SessionRouter.Resolve(rig.Container);
            Assert.Equal(profileA.Id, router.MissingProfileOf(tabA));
            rig.Dialogs.MissingAnswer = MissingConnectionChoice.Cancel;
            rig.Host.Active = tabA;
            Assert.False(await rig.Controller.RunAsync(RunMode.Statement, tab: tabA));
            Assert.Single(rig.Dialogs.MissingAsked);
            rig.Dialogs.MissingAnswer = MissingConnectionChoice.UseCurrent;
            Assert.Equal(other.User, await RunAsync(rig, tabA, "select user from dual"));
            Assert.Equal(profileB.Id, tabA.ConnectionProfileId);
            Assert.Same(b, router.SessionFor(tabA));
            output.WriteLine($"CONN-06 OK: {db.User} + {other.User} 동시 접속, 탭별 실행·미커밋·종료 확인·접속 안 됨 탭");
        }
        finally
        {
            foreach (var session in rig.Service.Sessions.ToList())
            {
                await rig.Service.DisconnectAsync(session, default); // FlowRig 는 지금 접속 하나만 닫는다
            }

            db.Exec($"drop table {t} purge");
        }
    });
}
