using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Execution;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.PlsqlWorkspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Shell;
using Xunit.Abstractions;

namespace Oranda.App.Tests.Integration;

/// <summary>
/// TSK-12-04: 앱 전체(주 창·도킹·툴바 콤보)를 띄우고 서로 다른 두 접속에 붙은 뒤 툴바 접속 콤보로 바꾸면 활성 문서의 다음 실행이
/// 그 DB 로 가는지 잰다. SQL 탭은 <c>sys_context('userenv','db_name')</c>·<c>user</c> 로, 이전 접속의 미커밋은 묻는지,
/// PL/SQL 문서는 컴파일이 고른 접속의 스키마에 생기는지 본다.
/// 26ai = <see cref="ItDb"/>. 11g(ORANDA_IT_11G_DATASOURCE·ORANDA_IT_11G_SYSTEM_PASSWORD)가 있으면 첫 시험의 둘째 접속은 11g SYSTEM(조회만)이다.
/// </summary>
[Trait("Category", "Integration")]
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class ConnectionComboSwitchFlowTests(ItDb db, ITestOutputHelper output) : IClassFixture<ItDb>
{
    private const string WhoAmI = "select sys_context('userenv','db_name') || '/' || user from dual";

    /// <summary>앱 한 벌 + 가짜 대화상자. 끝나면 열린 접속을 모두 닫는다.</summary>
    private sealed class App : IDisposable
    {
        private readonly IExecutionDialogs _previousDialogs = ExecutionDialogs.Current;

        public App()
        {
            ExecutionDialogs.Current = Dialogs;
            Harness = new ShellHarness();
        }

        public FakeDialogs Dialogs { get; } = new();

        public ShellHarness Harness { get; }

        public IConnectionService Service => ConnectionManagerCommands.Resolve(Harness.Services);

        public ConnectionComboViewModel Combo => ConnectionComboViewModel.For(Harness.Services) ?? throw new InvalidOperationException("툴바 콤보가 없다");

        public ICommandRegistry Registry => Harness.Get<ICommandRegistry>();

        public IContextKeys Keys => Harness.Get<IContextKeys>();

        public async Task StartAsync()
        {
            var wiring = Harness.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
            await wiring.RunAsync(default);
            Dispatcher.UIThread.RunJobs();
        }

        public async Task<ConnectionProfile> ConnectAsync(string name, string user, string source, string password)
        {
            var profile = await Service.SaveProfileAsync(new ConnectionProfile(name, user, source, Id: Guid.NewGuid()), null, false, default);
            var outcome = await Service.ConnectAsync(profile, password, default);
            Assert.True(outcome.IsConnected, outcome.Message);
            Dispatcher.UIThread.RunJobs();
            return profile;
        }

        public async Task<SqlEditorViewModel> NewSqlTabAsync()
        {
            await Registry.ExecuteAsync("oranda.file.newSqlEditor", Keys);
            Dispatcher.UIThread.RunJobs();
            return SqlEditorHost.Current.Active ?? throw new InvalidOperationException("SQL 탭이 없다");
        }

        public Task<SessionSelect?> ChooseAsync(ConnectionProfile profile) =>
            Combo.ChooseAsync(Combo.Items.Single(i => i.Session.Profile.Id == profile.Id));

        public async Task<string> RunAsync(SqlEditorViewModel tab, string sql)
        {
            var before = (tab.ResultPane as ResultTabsViewModel)?.Selected;
            tab.LoadText(sql, false);
            tab.Select(0, 0);
            await Registry.ExecuteAsync("oranda.exec.runStatement", Keys);
            await HeadlessHost.WaitUntil(() => tab.ResultPane is ResultTabsViewModel { IsRunning: false } r && !ReferenceEquals(r.Selected, before));
            var results = (ResultTabsViewModel)tab.ResultPane!;
            return results.Selected?.Pane?.Model is null ? string.Empty : await FlowRig.CellAsync(results.Selected);
        }

        public void Dispose()
        {
            foreach (var session in Service.Sessions.ToList())
            {
                Service.DisconnectAsync(session, default).GetAwaiter().GetResult();
            }

            Harness.Dispose();
            ExecutionDialogs.Current = _previousDialogs;
        }
    }

    [Fact]
    public Task Choosing_another_connection_in_the_toolbar_combo_runs_the_next_query_there() => HeadlessHost.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        var eleven = Other11g();
        using var other = eleven is null ? new ItDb() : null;
        using var app = new App();
        await app.StartAsync();
        var a = await app.ConnectAsync("combo-a", db.User, db.DataSource, db.Password);
        var tab = await app.NewSqlTabAsync();
        var b = eleven is { } e
            ? await app.ConnectAsync("combo-b", "system", e.Source, e.Password)
            : await app.ConnectAsync("combo-b", other!.User, other.DataSource, other.Password);
        Assert.IsType<Oranda.App.Features.SqlEditor.SqlWorkspaceViewModel>(app.Harness.Get<DockWorkspace>().ActiveDocument?.Panel);

        var onB = await app.RunAsync(tab, WhoAmI); // 새 접속을 활성 탭이 따른다
        Assert.Equal(b.Id, tab.ConnectionProfileId);

        Assert.Equal(SessionSelect.Selected, await app.ChooseAsync(a));
        var onA = await app.RunAsync(tab, WhoAmI);
        Assert.EndsWith("/" + db.User, onA, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(onB, onA);

        // 화면의 콤보 상자에서 고른 것도 같은 길로 간다.
        var view = (ConnectionComboView)app.Harness.Window.FindControl<ContentControl>("ConnectionComboHost")!.Content!;
        Assert.Same(app.Combo, view.DataContext);
        view.Combo.SelectedItem = view.Combo.Items.OfType<ConnectionComboItem>().Single(i => i.Session.Profile.Id == b.Id);
        await HeadlessHost.WaitUntil(() => tab.ConnectionProfileId == b.Id);
        Assert.Equal(onB, await app.RunAsync(tab, WhoAmI));
        output.WriteLine($"콤보 바꾸기: B={onB}, A={onA}");
    });

    [Fact]
    public Task An_uncommitted_change_on_the_previous_connection_is_asked_before_switching() => HeadlessHost.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var other = new ItDb();
        var t = "CB_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.Exec($"create table {t} (id number)");
        try
        {
            using var app = new App();
            await app.StartAsync();
            var a = await app.ConnectAsync("combo-a", db.User, db.DataSource, db.Password);
            var tab = SqlEditorHost.Current.Active ?? await app.NewSqlTabAsync(); // 이 접속을 쓰는 탭이 이것 하나(다른 탭이 같이 쓰면 묻지 않고 옮긴다)
            Assert.Single(SqlEditorHost.Current.Tabs);
            await app.RunAsync(tab, $"insert into {t} values (1)");
            var sessionA = app.Service.SessionOf(a.Id)!;
            await HeadlessHost.WaitUntil(() => sessionA.HasPendingTransaction);
            var b = await app.ConnectAsync("combo-b", other.User, other.DataSource, other.Password);
            Assert.Equal(a.Id, tab.ConnectionProfileId); // 미커밋 탭은 새 접속을 따르지 않는다

            // 취소: 묻고, 탭은 이전 접속에 남는다.
            app.Dialogs.UncommittedAnswer = UncommittedChoice.Cancel;
            Assert.Equal(SessionSelect.Cancelled, await app.ChooseAsync(b));
            Assert.Equal([$"1 - {db.User}@combo-a"], app.Dialogs.SwitchAsked);
            Assert.Same(sessionA, app.Combo.Selected?.Session);
            Assert.Equal(db.User, await app.RunAsync(tab, "select user from dual"));

            // 롤백하고 바꾸기: 다음 실행은 새 접속, 이전 변경은 되돌려졌다.
            app.Dialogs.UncommittedAnswer = UncommittedChoice.Rollback;
            Assert.Equal(SessionSelect.Selected, await app.ChooseAsync(b));
            Assert.False(sessionA.HasPendingTransaction);
            Assert.Equal(other.User, await app.RunAsync(tab, "select user from dual"));
            Assert.Equal("0", db.Text($"select count(*) from {t}"));

            // 그대로 두고 바꾸기: 이전 접속의 트랜잭션은 남고 탭만 옮긴다.
            Assert.Equal(SessionSelect.Selected, await app.ChooseAsync(a));
            await app.RunAsync(tab, $"insert into {t} values (2)");
            await HeadlessHost.WaitUntil(() => sessionA.HasPendingTransaction);
            app.Dialogs.UncommittedAnswer = UncommittedChoice.Keep;
            Assert.Equal(SessionSelect.Selected, await app.ChooseAsync(b));
            Assert.True(sessionA.HasPendingTransaction);
            Assert.Equal(other.User, await app.RunAsync(tab, "select user from dual"));
            app.Dialogs.UncommittedAnswer = UncommittedChoice.Rollback;
            Assert.True(await ExecutionCommands.Resolve(app.Harness.Services).ConfirmCloseAsync()); // 앱 종료 때 남은 것을 다시 묻는다
            Assert.False(sessionA.HasPendingTransaction);
            output.WriteLine($"미커밋 확인: {string.Join(" / ", app.Dialogs.SwitchAsked)}");
        }
        finally
        {
            db.Exec($"drop table {t} purge");
        }
    });

    [Fact]
    public Task A_plsql_document_compiles_into_the_connection_chosen_in_the_combo() => HeadlessHost.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var other = new ItDb();
        using var app = new App();
        await app.StartAsync();
        var a = await app.ConnectAsync("combo-a", db.User, db.DataSource, db.Password);
        await app.NewSqlTabAsync();
        var b = await app.ConnectAsync("combo-b", other.User, other.DataSource, other.Password);
        Assert.Equal(SessionSelect.Selected, await app.ChooseAsync(a));

        await app.Registry.ExecuteAsync("oranda.file.newPlsqlEditor", app.Keys);
        Dispatcher.UIThread.RunJobs();
        var dock = app.Harness.Get<DockWorkspace>();
        var workspace = Assert.IsType<PlsqlWorkspaceViewModel>(dock.ActiveDocument?.Panel);
        var editor = workspace.ActiveTab!;
        Assert.Same(app.Service.SessionOf(a.Id), editor.Session);
        Assert.Equal(a.Id, app.Combo.Selected?.Session.Profile.Id); // 콤보는 문서의 접속을 보인다

        Assert.Equal(SessionSelect.Selected, await app.ChooseAsync(b));
        Assert.Same(app.Service.SessionOf(b.Id), editor.Session);
        Assert.Equal(b.Id, app.Combo.Selected?.Session.Profile.Id);
        Assert.Same(app.Service.SessionOf(b.Id), app.Service.ActiveSession);

        editor.Editor.LoadText("create or replace function f_combo return varchar2 is begin return user; end;", false);
        var outcome = await editor.CompileAsync();
        Assert.Equal(PlsqlCompileStatus.Compiled, outcome.Status);
        Assert.Equal("1", other.Text("select count(*) from user_objects where object_name = 'F_COMBO'"));
        Assert.Equal("0", db.Text("select count(*) from user_objects where object_name = 'F_COMBO'"));
        output.WriteLine($"PL/SQL 컴파일 대상: {other.User} ({outcome.Message})");
    });

    private static (string Source, string Password)? Other11g() =>
        Environment.GetEnvironmentVariable("ORANDA_IT_11G_DATASOURCE") is { Length: > 0 } ds
        && Environment.GetEnvironmentVariable("ORANDA_IT_11G_SYSTEM_PASSWORD") is { Length: > 0 } pw
            ? (ds, pw)
            : null;
}
