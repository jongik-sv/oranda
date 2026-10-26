using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.DataTransfer;
using Oranda.App.Features.Execution;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.HistoryScripts;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.ConnectionManager;

// 소유: TSK-14-08(D-040). 쓰기 경로(이력 다시 실행·가져오기·도구의 DDL·세션 닫기)가 지금 작업 공간의 세션을 쓰고, 실행 중 표시를 세션별로 거친다.
[Collection("GridEditStatics")]
public sealed class WritePathSessionTests
{
    /// <summary>컨테이너에 접속 서비스(가짜 세션)·실행 엔진·언어 서비스를 넣고 라우터를 만든 한 벌. 명령·도우미가 컨테이너에서 찾는 경로를 그대로 탄다.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly SqlEditorHost _previousHost = SqlEditorHost.Current;
        private readonly IExecutionDialogs _previousDialogs = ExecutionDialogs.Current;

        public Rig()
        {
            Fx.Sessions.PublishOpened = true;
            SqlEditorHost.Current = Host;
            ExecutionDialogs.Current = Dialogs;
            Fx.Container.AddInstance<IConnectionService>(Fx.Service);
            Fx.Container.AddInstance<IExecutionService>(Exec);
            Fx.Container.AddInstance<ILanguageService>(new OneStatementLanguage());
            Router = SessionRouter.Resolve(Fx.Container);
            State = ExecutionState.For(Fx.Events);
        }

        public ServiceFixture Fx { get; } = new();

        public SqlEditorHost Host { get; } = new();

        public FakeDialogs Dialogs { get; } = new();

        public SessionExecution Exec { get; } = new();

        public SessionRouter Router { get; }

        public ExecutionState State { get; }

        public async Task<ISession> ConnectAsync(string name = "a")
        {
            var profile = await Fx.Service.SaveProfileAsync(new ConnectionProfile(name, "scott", "localhost:1521/FREEPDB1"), null, false, default);
            var outcome = await Fx.Service.ConnectAsync(profile, "pw-" + name, default);
            Assert.True(outcome.IsConnected, outcome.Message);
            Pump();
            return outcome.Session!;
        }

        /// <summary>같은 접속에 작업 공간 둘: A 는 접속의 세션, B 는 첫 실행 때 연 자기 세션. B 가 활성.</summary>
        public async Task<(ISession A, SqlEditorViewModel TabA, ISession B, SqlEditorViewModel TabB, SqlWorkspaceViewModel WsB)> TwoWorkspacesAsync(ISession a)
        {
            Host.EnsureWorkspace();
            var tabA = Host.NewDocument();
            var wsB = Host.NewWorkspace();
            var tabB = wsB.Tabs[0];
            Assert.True(await Router.PrepareAsync(tabA));
            Assert.True(await Router.PrepareAsync(tabB));
            var b = Router.SessionFor(tabB)!;
            Assert.NotSame(a, b);
            Host.Active = tabB;
            Pump();
            return (a, tabA, b, tabB, wsB);
        }

        public void Dispose()
        {
            SqlEditorHost.Current = _previousHost;
            ExecutionDialogs.Current = _previousDialogs;
            GridEditCommands.Active = null;
            GridEditCommands.ConfirmPreview = null;
            GridEditCommands.Rerun = null;
            GridEditCommands.Report = null;
            GridEditCommands.State = null;
            HistoryScriptsCommands.Notify = null;
            Fx.Dispose();
        }
    }

    private static void Pump()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    // ---------- 실행 표시: 세션별 실행 수(발견 10) ----------

    [Fact]
    public Task Overlapping_runs_on_one_session_keep_it_busy_until_the_last_one_ends() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var first = new object();

        rig.State.BeginRun(a.Id, first); // 편집기 실행
        rig.State.BeginRun(a.Id, null); // 같은 세션의 격자 반영이 겹쳤다
        rig.State.EndRun(a.Id); // 하나가 끝났다

        Assert.True(rig.State.IsRunning(a.Id)); // 남은 실행이 있어 바쁨 유지(세션 창 커밋·닫기 꺼짐)
        Assert.Same(first, rig.State.RunOwnerOf(a.Id));
        rig.State.EndRun(a.Id);
        Assert.False(rig.State.IsRunning(a.Id));
        rig.State.EndRun(a.Id); // 짝 없는 끝은 아무것도 바꾸지 않는다
        Assert.False(rig.State.IsRunning(a.Id));
        rig.State.BeginRun(a.Id, null);
        Assert.True(rig.State.IsRunning(a.Id));
        rig.State.EndRun(a.Id);
    });

    // ---------- 이력 「다시 실행」(발견 5·8) ----------

    [Fact]
    public Task History_rerun_on_the_active_tab_profile_runs_on_that_workspace_session_and_marks_it_running() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var connection = await rig.ConnectAsync();
        var (a, _, b, _, wsB) = await rig.TwoWorkspacesAsync(connection);
        bool? running = null;
        object? owner = null;
        rig.Exec.OnExecute = s =>
        {
            running = rig.State.IsRunning(s.Id);
            owner = rig.State.RunOwnerOf(s.Id);
        };

        Assert.True(await HistoryScriptsCommands.RerunAsync(rig.Fx.Container, "update emp set sal = sal", a.Profile.Id, default));

        Assert.Same(b, Assert.Single(rig.Exec.Sessions)); // 접속의 기본 세션(A 작업 공간의 트랜잭션)이 아니라 B 작업 공간의 세션
        Assert.True(running);
        Assert.Same(wsB, owner);
        Assert.False(rig.State.IsRunning(b.Id));
        Assert.False(rig.State.IsRunning(a.Id));
    });

    [Fact]
    public Task History_rerun_for_another_connection_uses_that_connection_and_waits_for_a_busy_session() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync("a");
        var other = await rig.ConnectAsync("c");
        rig.Fx.Service.Switch(a);
        var (_, _, b, _, _) = await rig.TwoWorkspacesAsync(a);
        var notes = new List<string>();
        HistoryScriptsCommands.Notify = notes.Add;

        Assert.True(await HistoryScriptsCommands.RerunAsync(rig.Fx.Container, "select 1 from dual", other.Profile.Id, default));
        Assert.Same(other, Assert.Single(rig.Exec.Sessions)); // 이력 항목의 접속(사용자가 고른 접속)의 세션

        rig.State.BeginRun(b.Id, new object()); // B 작업 공간의 세션이 다른 실행으로 바쁘다
        Assert.False(await HistoryScriptsCommands.RerunAsync(rig.Fx.Container, "select 1 from dual", a.Profile.Id, default));
        Assert.Single(rig.Exec.Sessions); // 같은 세션에 섞어 보내지 않았다
        Assert.Contains(notes, n => n.Contains("실행 중", StringComparison.Ordinal));
        rig.State.EndRun(b.Id);
    });

    // ---------- 가져오기 시작 전 미커밋 확인(발견 4) ----------

    [Fact]
    public Task Import_asks_about_uncommitted_changes_of_its_session_before_it_starts() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();

        Assert.True(await DataTransferCommands.ReadyForImportAsync(rig.Fx.Container, a, default)); // 미커밋이 없으면 묻지 않는다
        Assert.Equal(0, rig.Dialogs.UncommittedAsked);

        rig.State.ApplyTransaction(a.Id, true);
        rig.Dialogs.UncommittedAnswer = UncommittedChoice.Cancel;
        Assert.False(await DataTransferCommands.ReadyForImportAsync(rig.Fx.Container, a, default)); // 취소: 가져오지 않는다
        Assert.Equal(1, rig.Dialogs.UncommittedAsked);
        Assert.True(rig.State.IsTxnPending(a.Id));

        rig.Dialogs.UncommittedAnswer = UncommittedChoice.Commit;
        Assert.True(await DataTransferCommands.ReadyForImportAsync(rig.Fx.Container, a, default)); // 커밋하고 진행
        Assert.Equal(2, rig.Dialogs.UncommittedAsked);
        Assert.False(rig.State.IsTxnPending(a.Id));

        rig.State.BeginRun(a.Id, null);
        Assert.False(await DataTransferCommands.ReadyForImportAsync(rig.Fx.Container, a, default)); // 실행 중인 세션에는 시작하지 않는다
        rig.State.EndRun(a.Id);
    });

    // ---------- 세션 닫기 전 ROLLBACK(발견 18) ----------

    [Fact]
    public Task Closing_a_session_sends_a_rollback_before_it_disposes_the_connection() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = (StubSession)await rig.ConnectAsync();
        var extra = (StubSession)(await rig.Fx.Service.OpenSessionAsync(a.Profile.Id, default)).Session!;

        await rig.Fx.Service.DisconnectAsync(extra, default);
        await rig.Fx.Service.DisconnectAsync(a, default);

        Assert.Equal(1, extra.RollbacksBeforeDispose); // 정상 로그오프의 암묵 커밋 대신 닫기 전에 명시적으로 되돌린다
        Assert.Equal(1, a.RollbacksBeforeDispose);
        Assert.Equal(SessionState.Closed, a.State);
    });

    [Fact]
    public Task A_broken_session_is_closed_without_a_rollback() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = (StubSession)await rig.ConnectAsync();
        a.MarkBroken();

        await ConnectionService.CloseAsync(a);

        Assert.Equal(0, a.RollbacksBeforeDispose); // 끊긴 연결에는 보내지 않는다(서버가 이미 되돌렸다)
    });

    [Fact]
    public Task A_rollback_that_does_not_answer_does_not_keep_the_session_open() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = (StubSession)await rig.ConnectAsync();
        a.RollbackGate = new TaskCompletionSource(); // 서버가 응답하지 않는다

        await ConnectionService.CloseAsync(a, timeout: TimeSpan.FromSeconds(10));

        Assert.Equal(SessionState.Closed, a.State); // 롤백은 시간 제한 뒤 버리고 닫는다
        a.RollbackGate.SetResult();
    });

    // ---------- 도구가 쓰는 세션(발견 20) ----------

    [Fact]
    public Task Tools_use_the_active_workspace_session_of_the_same_connection() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var connection = await rig.ConnectAsync("a");
        var other = await rig.ConnectAsync("c");
        rig.Fx.Service.Switch(connection);
        var (a, _, b, tabB, _) = await rig.TwoWorkspacesAsync(connection);
        Assert.Same(a, SessionRouter.CurrentOf(rig.Fx.Container)); // 지금 접속은 프로필의 첫 세션(Switch 가 더 연 세션을 접속으로 바꾼다)

        Assert.Same(b, SessionRouter.ForTool(rig.Fx.Container)); // 스키마 브라우저·DDL 도구도 B 작업 공간의 세션으로
        Assert.Same(b, SessionRouter.ForTool(rig.Fx.Container, a)); // 고른 세션(스키마 브라우저)이 같은 접속이면 작업 공간의 세션
        Assert.Same(other, SessionRouter.ForTool(rig.Fx.Container, other)); // 다른 접속을 고른 도구는 그 접속

        Oranda.App.Features.DedicatedSession.DedicatedSessionCommands.Resolve(rig.Fx.Container).SqlDocumentActive = () => false; // SQL 작업 공간이 아닌 문서가 활성
        Assert.Same(a, SessionRouter.ForTool(rig.Fx.Container, a));
        _ = tabB;
    });

    // ---------- 시험 도우미 ----------

    /// <summary>받은 세션을 기록하는 실행 엔진. 문장마다 <see cref="OnExecute"/> 를 부른다.</summary>
    private sealed class SessionExecution : IExecutionService
    {
        public List<ISession> Sessions { get; } = [];

        public Action<ISession>? OnExecute { get; set; }

        public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds, ExecutionOptions? options, CancellationToken ct)
        {
            Sessions.Add(s);
            OnExecute?.Invoke(s);
            return Task.FromResult(new ExecutionResult(stmt, ExecutionStatus.Succeeded) { RowsAffected = 1 });
        }

        public async IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode,
            ExecutionOptions? options, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var stmt in stmts)
            {
                yield return await ExecuteAsync(s, stmt, new Dictionary<string, BindValue>(), options, ct);
            }
        }
    }

    /// <summary>글 전체를 한 문장으로 보는 언어 서비스(이력 다시 실행이 문장을 나눈다).</summary>
    private sealed class OneStatementLanguage : ILanguageService
    {
        public IReadOnlyList<SqlStatement> Split(string text, SplitOptions o) =>
            [new SqlStatement(text, new Oranda.Core.Text.TextRange(0, text.Length), StatementKind.Dml) { Verb = "UPDATE" }];

        public SqlStatement? GetStatementAt(string text, int offset, SplitOptions o) => Split(text, o)[0];

        public Task<IReadOnlyList<Diagnostic>> AnalyzeAsync(string text, CancellationToken ct) => Task.FromResult<IReadOnlyList<Diagnostic>>([]);

        public CompletionContext GetCompletionContext(string text, int offset) => throw new NotSupportedException();
    }
}
