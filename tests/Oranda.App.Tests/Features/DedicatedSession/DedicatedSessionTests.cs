using System.Diagnostics;
using Avalonia.Threading;
using Oranda.App.Features.Binds;
using Oranda.App.Features.DedicatedSession;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.DedicatedSession;

/// <summary>
/// 전용 세션 SQL 탭(D-017): 탭마다 자기 세션, 탭이 닫히기 전 미커밋 확인(저장 확인 다음), 닫히면 세션도 닫힘, 앱 종료 확인.
/// </summary>
public sealed class DedicatedSessionTests
{
    private static readonly ConnectionProfile Profile = new("dev", "scott", "localhost:1521/FREEPDB1");

    [Fact]
    public Task Each_dedicated_tab_has_its_own_session_and_a_commit_stays_in_its_tab() => Ui.Run(async () =>
    {
        var rig = new DedicatedRig();
        var (a, sa) = rig.NewDedicatedTab();
        var (b, sb) = rig.NewDedicatedTab();
        var plain = rig.Host.NewDocument();
        rig.MarkPending(sa);
        rig.MarkPending(sb);

        Assert.NotEqual(sa.Id, sb.Id);
        Assert.Same(sa, rig.Controller.SessionFor(a));
        Assert.Same(sb, rig.Controller.SessionFor(b));
        Assert.Same(rig.Shared, rig.Controller.SessionFor(plain)); // 공유 세션 탭

        rig.Host.Active = a;
        await rig.Controller.CommitAsync();

        Assert.Equal(1, sa.Commits);
        Assert.Equal(0, sb.Commits);
        Assert.Equal(0, rig.Shared.Commits);
        Assert.False(rig.State.IsTxnPending(sa.Id));
        Assert.True(rig.State.IsTxnPending(sb.Id));
        Assert.True(rig.State.ShownTxnPending is false); // 활성 탭 A 기준
        rig.Host.Active = b;
        Assert.True(rig.State.ShownTxnPending);
    });

    [Theory]
    [InlineData(UncommittedChoice.Commit, true)]
    [InlineData(UncommittedChoice.Rollback, true)]
    [InlineData(UncommittedChoice.Cancel, false)]
    public Task Closing_an_uncommitted_dedicated_tab_asks_and_follows_the_answer(UncommittedChoice answer, bool closed) => Ui.Run(async () =>
    {
        var rig = new DedicatedRig();
        var (tab, session) = rig.NewDedicatedTab();
        rig.MarkPending(session);
        rig.Dialogs.Answers.Enqueue(answer);

        Assert.Equal(closed, await rig.Host.CloseAsync(tab, default));

        Assert.Equal([tab.Title], rig.Dialogs.Asked);
        Assert.Equal(answer == UncommittedChoice.Commit ? 1 : 0, session.Commits);
        Assert.Equal(answer == UncommittedChoice.Rollback ? 1 : 0, session.Rollbacks);
        Assert.Equal(closed, !rig.Host.Tabs.Contains(tab));
        await rig.Registry.WhenReleasedAsync();
        Assert.Equal(closed, session.Disposed); // 닫힌 탭의 세션은 닫힌다(누수 없음)
        Assert.Equal(closed ? 0 : 1, rig.Registry.Count);
    });

    [Fact]
    public Task Clean_dedicated_tab_closes_without_asking_and_closes_its_session() => Ui.Run(async () =>
    {
        var rig = new DedicatedRig();
        var (tab, session) = rig.NewDedicatedTab();

        Assert.True(await rig.Host.CloseAsync(tab, default));

        Assert.Empty(rig.Dialogs.Asked);
        await rig.Registry.WhenReleasedAsync();
        Assert.True(session.Disposed);
        Assert.Equal(0, rig.Registry.Count);
        Assert.False(rig.State.IsDedicated(session.Id));
    });

    [Fact]
    public Task Failed_commit_keeps_the_tab_open() => Ui.Run(async () =>
    {
        var rig = new DedicatedRig();
        var (tab, session) = rig.NewDedicatedTab();
        rig.MarkPending(session);
        session.FailCommit = true;
        rig.Dialogs.Answers.Enqueue(UncommittedChoice.Commit);

        Assert.False(await rig.Host.CloseAsync(tab, default));

        Assert.Contains(tab, rig.Host.Tabs);
        Assert.False(session.Disposed);
    });

    [Theory]
    [InlineData(SaveChoice.Cancel, false, 0)]
    [InlineData(SaveChoice.DontSave, true, 1)]
    public Task Save_confirm_comes_before_the_uncommitted_confirm(SaveChoice save, bool closed, int uncommittedAsked) => Ui.Run(async () =>
    {
        var rig = new DedicatedRig(save);
        var (tab, session) = rig.NewDedicatedTab("update emp set sal = sal");
        rig.MarkPending(session);
        rig.Dialogs.Answers.Enqueue(UncommittedChoice.Rollback);

        Assert.Equal(closed, await rig.Host.CloseAsync(tab, default));

        Assert.Equal([tab.Title], rig.Editor.SaveAsked);
        Assert.Equal(uncommittedAsked, rig.Dialogs.Asked.Count); // 저장 확인을 취소하면 커밋/롤백은 묻지 않는다
        await rig.Registry.WhenReleasedAsync();
        Assert.Equal(closed, session.Disposed);
    });

    [Fact]
    public Task Broken_dedicated_session_is_cleaned_up_without_asking() => Ui.Run(async () =>
    {
        var rig = new DedicatedRig();
        var (tab, session) = rig.NewDedicatedTab();
        rig.MarkPending(session);
        session.State = SessionState.Broken;

        Assert.True(await rig.Host.CloseAsync(tab, default));

        Assert.Empty(rig.Dialogs.Asked);
        await rig.Registry.WhenReleasedAsync();
        Assert.True(session.Disposed);
        Assert.Contains(rig.Notices, n => n.Contains(tab.Title, StringComparison.Ordinal) && n.Contains("롤백", StringComparison.Ordinal));
    });

    [Fact]
    public Task Running_dedicated_tab_is_not_closed() => Ui.Run(async () =>
    {
        var rig = new DedicatedRig();
        var (tab, session) = rig.NewDedicatedTab();
        rig.State.BeginRun(session.Id);

        Assert.False(await rig.Host.CloseAsync(tab, default));

        Assert.Single(rig.Editor.Messages);
        Assert.Contains(tab, rig.Host.Tabs);
        Assert.False(session.Disposed);
        rig.State.EndRun(session.Id);
    });

    [Fact]
    public Task Commit_in_one_tab_then_cancel_in_another_keeps_the_commit_and_the_workspace() => Ui.Run(async () =>
    {
        var rig = new DedicatedRig();
        var (a, sa) = rig.NewDedicatedTab();
        var (b, sb) = rig.NewDedicatedTab();
        rig.MarkPending(sa);
        rig.MarkPending(sb);
        rig.Dialogs.Answers.Enqueue(UncommittedChoice.Commit);
        rig.Dialogs.Answers.Enqueue(UncommittedChoice.Cancel);
        var ws = rig.Host.WorkspaceOf(a)!;

        Assert.False(await rig.Host.TryCloseWorkspaceAsync(ws, default));

        Assert.Equal([a.Title, b.Title], rig.Dialogs.Asked);
        Assert.Equal(1, sa.Commits);
        Assert.Contains(ws, rig.Host.Workspaces);
        Assert.Equal(2, rig.Registry.Count);
    });

    [Theory]
    [InlineData(UncommittedChoice.Commit, true)]
    [InlineData(UncommittedChoice.Cancel, false)]
    public Task App_exit_asks_only_for_uncommitted_dedicated_tabs_then_closes_every_session(UncommittedChoice answer, bool exit) => Ui.Run(async () =>
    {
        var rig = new DedicatedRig(SaveChoice.Cancel);
        var (_, dirty) = rig.NewDedicatedTab("select 1 from dual"); // 고친 글: 앱 종료 때 저장은 묻지 않는다(D-016)
        var (_, pending) = rig.NewDedicatedTab();
        rig.MarkPending(pending);
        rig.Dialogs.Answers.Enqueue(answer);

        Assert.Equal(exit, await rig.Host.CanShutdownAsync(default));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(exit, rig.State.IsBusy(pending.Id)); // 확인을 마친 세션은 종료 정리까지 실행을 받지 않는다
        if (exit)
        {
            await rig.Registry.ReleaseAllAsync();
        }

        Assert.Empty(rig.Editor.SaveAsked);
        Assert.Single(rig.Dialogs.Asked);
        await rig.Registry.WhenReleasedAsync();
        Assert.Equal(exit, dirty.Disposed);
        Assert.Equal(exit, pending.Disposed);
        Assert.Equal(exit ? 0 : 2, rig.Registry.Count);
    });

    [Fact]
    public Task Document_close_button_waits_for_the_dedicated_confirm() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var wiring = h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
        await wiring.RunAsync(default);
        Dispatcher.UIThread.RunJobs();
        var dock = h.Get<DockWorkspace>();
        var registry = DedicatedSessionCommands.Resolve(h.Services);
        var host = registry.Host;
        var plain = host.NewDocument();
        host.NewWorkspace();
        var tab = host.NewDocument();
        var session = new DedicatedFakeSession(Profile);
        registry.Attach(tab, session);
        Dispatcher.UIThread.RunJobs();
        var answers = new Queue<bool>([false, true]);
        var asked = 0;
        registry.ConfirmUncommitted = (_, _, _) =>
        {
            asked++;
            return Task.FromResult(answers.Dequeue());
        };
        ExecutionState.For(h.Get<IEventBus>()).ApplyTransaction(session.Id, true);
        var plainDoc = dock.Documents.Single(d => ReferenceEquals(d.Panel, host.WorkspaceOf(plain)));
        var dedicatedDoc = dock.Documents.Single(d => ReferenceEquals(d.Panel, host.WorkspaceOf(tab)));

        Assert.Null(DedicatedSessionCommands.CloseConfirmFor(registry, plainDoc)); // 전용 세션 탭이 없는 작업 공간은 묻지 않는다

        dock.CloseDocument(dedicatedDoc); // 취소
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(dedicatedDoc, dock.Documents);
        Assert.False(session.Disposed);

        dock.CloseDocument(dedicatedDoc); // 확인
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, asked);
        Assert.DoesNotContain(dedicatedDoc, dock.Documents);
        Assert.DoesNotContain(tab, host.Tabs);
        await registry.WhenReleasedAsync();
        Assert.True(session.Disposed);
        Assert.Equal(0, registry.Count);
    });

    [Fact]
    public Task Closing_the_main_window_confirms_before_saving_and_closes_dedicated_sessions_before_shutdown_work() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var registry = DedicatedSessionCommands.Resolve(h.Services);
        var tab = registry.Host.NewDocument();
        var session = new DedicatedFakeSession(Profile);
        registry.Attach(tab, session);
        ExecutionState.For(h.Get<IEventBus>()).ApplyTransaction(session.Id, true);
        var order = new List<string>();
        var answers = new Queue<bool>([false, true]);
        registry.ConfirmUncommitted = (_, _, _) =>
        {
            order.Add("ask");
            return Task.FromResult(answers.Dequeue());
        };
        h.Get<Oranda.App.Shell.ShellController>().RegisterShutdown(_ =>
        {
            order.Add(session.Disposed ? "shutdown(after release)" : "shutdown(before release)");
            return Task.CompletedTask;
        });

        h.Window.Close(); // 취소: 종료 작업을 하지 않고 창이 남는다
        await HeadlessHost.WaitUntil(() => order.Count == 1);
        Dispatcher.UIThread.RunJobs();
        Assert.True(h.Window.IsVisible);
        Assert.False(session.Disposed);

        h.Window.Close();
        await HeadlessHost.WaitUntil(() => !h.Window.IsVisible);
        Assert.Equal(["ask", "ask", "shutdown(after release)"], order);
        Assert.Equal(0, registry.Count);
    });

    [Fact]
    public Task Late_state_updates_for_a_closed_dedicated_session_do_not_touch_the_shared_session() => Ui.Run(async () =>
    {
        var rig = new DedicatedRig();
        rig.State.ApplyTransaction(rig.Shared.Id, true);
        var (tab, session) = rig.NewDedicatedTab();
        rig.State.BeginRun(session.Id);
        await rig.Registry.ReleaseAsync(tab); // 끊겨서 정리된 뒤 실행의 finally 가 늦게 온다

        rig.State.ApplyTransaction(session.Id, false);
        rig.State.EndRun(session.Id);

        Assert.True(rig.State.TxnPending);
        Assert.False(rig.State.IsTxnPending(session.Id));
        Assert.False(rig.State.IsBusy(session.Id));
    });

    [Fact]
    public Task Sessions_being_closed_take_no_new_run_until_the_close_is_cancelled() => Ui.Run(async () =>
    {
        var rig = new DedicatedRig();
        var (a, sa) = rig.NewDedicatedTab("select 1 from dual");
        var (b, sb) = rig.NewDedicatedTab();
        rig.MarkPending(sb);
        var busyWhileAsking = new List<bool>();
        var ranWhileAsking = new List<bool>();
        rig.Dialogs.Asking = async _ =>
        {
            busyWhileAsking.Add(rig.State.IsBusy(sa.Id));
            ranWhileAsking.Add(await rig.Controller.RunAsync(RunMode.Statement, tab: a));
        };
        rig.Dialogs.Answers.Enqueue(UncommittedChoice.Cancel);

        Assert.False(await rig.Host.TryCloseWorkspaceAsync(rig.Host.WorkspaceOf(a)!, default));

        Assert.Equal([true], busyWhileAsking); // 깨끗하다고 본 앞 탭도 닫힐 때까지 실행을 받지 않는다
        Assert.Equal([false], ranWhileAsking);
        Assert.False(rig.State.IsBusy(sa.Id)); // 취소하면 풀린다
        Assert.False(rig.State.IsBusy(sb.Id));
    });

    [Fact]
    public Task Commit_command_uses_the_shared_session_when_a_non_sql_document_is_active() => Ui.Run(async () =>
    {
        var rig = new DedicatedRig();
        var (tab, session) = rig.NewDedicatedTab();
        rig.MarkPending(session);
        rig.Host.Active = tab;
        var sqlActive = false;
        rig.Registry.SqlDocumentActive = () => sqlActive;
        rig.Registry.Refocus();

        Assert.Null(rig.State.FocusedDedicated);
        await rig.Controller.CommitAsync();
        Assert.Equal(1, rig.Shared.Commits);
        Assert.Equal(0, session.Commits);

        sqlActive = true;
        rig.Registry.Refocus();
        Assert.Equal(session.Id, rig.State.FocusedDedicated);
        await rig.Controller.CommitAsync();
        Assert.Equal(1, session.Commits);
    });

    [Fact]
    public Task Session_opened_after_exit_started_is_closed_instead_of_attached() => Ui.Run(async () =>
    {
        var rig = new DedicatedRig();
        await rig.Registry.ReleaseAllAsync();
        var session = new DedicatedFakeSession(Profile);
        var before = rig.Host.Tabs.Count;

        var tab = await DedicatedSessionCommands.NewTabAsync(rig.Registry, _ => Task.FromResult<ISession?>(session), rig.Log, default);

        Assert.Null(tab);
        await rig.Registry.WhenReleasedAsync();
        Assert.True(session.Disposed);
        Assert.Equal(before, rig.Host.Tabs.Count);
        Assert.Equal(0, rig.Registry.Count);
    });

    [Fact]
    public Task Failed_exit_confirm_cancels_once_with_a_notice_and_the_next_close_exits_without_asking() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var registry = DedicatedSessionCommands.Resolve(h.Services);
        var editor = new EditorDialogs(SaveChoice.DontSave);
        registry.Host.Dialogs = editor;
        var tab = registry.Host.NewDocument();
        var session = new DedicatedFakeSession(Profile);
        registry.Attach(tab, session);
        ExecutionState.For(h.Get<IEventBus>()).ApplyTransaction(session.Id, true);
        var asked = 0;
        registry.ConfirmUncommitted = (_, _, _) =>
        {
            asked++;
            throw new InvalidOperationException("broken registry");
        };

        h.Window.Close(); // 확인이 예외로 실패: 이번 닫기는 취소, 알림
        await HeadlessHost.WaitUntil(() => editor.Messages.Count == 1);
        Dispatcher.UIThread.RunJobs();
        Assert.True(h.Window.IsVisible);
        Assert.False(session.Disposed);
        Assert.Contains("확인 없이", editor.Messages[0], StringComparison.Ordinal);

        h.Window.Close(); // 다음 닫기는 확인 없이 끝낸다
        await HeadlessHost.WaitUntil(() => !h.Window.IsVisible);
        Assert.Equal(1, asked);
        await registry.WhenReleasedAsync();
        Assert.True(session.Disposed);
    });

    [Fact]
    public Task Release_does_not_block_ui_thread_on_a_stuck_dispose() => Ui.Run(async () =>
    {
        using var gate = new ManualResetEventSlim(false);
        var rig = new DedicatedRig();
        var (tab, session) = rig.NewDedicatedTab();
        session.DisposeGate = gate;

        var clock = Stopwatch.StartNew();
        var release = rig.Registry.ReleaseAsync(tab);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"UI 스레드가 {clock.Elapsed} 동안 막혔다");
        Assert.Null(rig.Registry.SessionOf(tab)); // 탭에서는 이미 떨어졌다
        Assert.False(release.IsCompleted);

        gate.Set(); // 막힌 Dispose 가 백그라운드에서 나중에 끝난다
        await release.WaitAsync(TimeSpan.FromSeconds(3));
        await rig.Registry.WhenReleasedAsync();
        Assert.True(session.Disposed);
    });

    [Fact]
    public Task Exit_cleanup_runs_all_stuck_disposes_in_parallel_without_blocking_ui() => Ui.Run(async () =>
    {
        using var gate = new ManualResetEventSlim(false);
        var rig = new DedicatedRig();
        var (_, sa) = rig.NewDedicatedTab();
        var (_, sb) = rig.NewDedicatedTab();
        sa.DisposeGate = gate;
        sb.DisposeGate = gate;

        var clock = Stopwatch.StartNew();
        var all = rig.Registry.ReleaseAllAsync();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"UI 스레드가 {clock.Elapsed} 동안 막혔다");
        Assert.False(all.IsCompleted);

        gate.Set();
        await all.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(sa.Disposed && sb.Disposed);
    });

    [Fact]
    public void New_dedicated_tab_command_has_no_default_key_in_any_preset()
    {
        foreach (var id in KeymapLoader.PresetIds)
        {
            Assert.DoesNotContain(KeymapLoader.LoadPreset(id).Bindings, b => b.Command == DedicatedSessionCommands.NewTabCommandId);
        }
    }

    /// <summary>호스트·실행 상태·컨트롤러·등록부를 하나로 묶는다. 공유 작업 세션 하나가 열려 있다.</summary>
    private sealed class DedicatedRig
    {
        public DedicatedRig(SaveChoice save = SaveChoice.DontSave)
        {
            Editor = new EditorDialogs(save);
            Host = new SqlEditorHost(dialogs: Editor);
            State = new ExecutionState(Bus);
            Log = new ExecutionLog(Bus, State);
            Shared = new FakeSession(Profile);
            Registry = new DedicatedSessionRegistry(Host, State, Bus) { Notify = Notices.Add };
            Controller = new ExecutionController(new ExecutionDeps
            {
                State = State,
                Log = Log,
                Dialogs = Dialogs,
                Session = () => Shared,
                TabSession = Registry.SessionOf,
                Execution = () => new FakeExecution(),
                Language = Rig.LanguageForTests,
                Host = () => Host,
                Binds = BindVariableStore.For(Bus),
            });
            Registry.ConfirmUncommitted = (session, title, ct) => Controller.ConfirmUncommittedAsync(session, title, ct);
            Bus.Publish(new SessionOpened(Shared.Id, Profile, Shared.Server, SessionPurpose.Work));
            Dispatcher.UIThread.RunJobs();
        }

        public EventBus Bus { get; } = new();

        public EditorDialogs Editor { get; }

        public UncommittedDialogs Dialogs { get; } = new();

        public SqlEditorHost Host { get; }

        public ExecutionState State { get; }

        public ExecutionLog Log { get; }

        public FakeSession Shared { get; }

        public DedicatedSessionRegistry Registry { get; }

        public ExecutionController Controller { get; }

        public List<string> Notices { get; } = [];

        public (SqlEditorViewModel Tab, DedicatedFakeSession Session) NewDedicatedTab(string? text = null)
        {
            var tab = Host.NewDocument(text);
            var session = new DedicatedFakeSession(Profile);
            Registry.Attach(tab, session);
            return (tab, session);
        }

        public void MarkPending(DedicatedFakeSession session)
        {
            session.HasPendingTransaction = true;
            State.ApplyTransaction(session.Id, true);
        }
    }
}

/// <summary>전용 세션 가짜: 닫혔는지·커밋 실패를 본다.</summary>
internal sealed class DedicatedFakeSession(ConnectionProfile profile) : ISession
{
    public Guid Id { get; } = Guid.NewGuid();

    public ConnectionProfile Profile { get; } = profile;

    public ServerInfo Server { get; } = new("23.26.0.0.0", "FREE", "localhost", "FREEPDB1", "FREE", "FREEPDB1", true, 1,
        new ServerCapabilities(false, true, true, false, false), DbUniqueName: "FREE", SessionUser: "SCOTT");

    public SessionPurpose Purpose => SessionPurpose.Dedicated;

    public string DbKey => "FREE/FREEPDB1/SCOTT";

    public SessionState State { get; set; } = SessionState.Idle;

    public bool HasPendingTransaction { get; set; }

    public bool FailCommit { get; set; }

    public bool Disposed { get; private set; }

    public int Commits { get; private set; }

    public int Rollbacks { get; private set; }

    public Task CommitAsync(CancellationToken ct)
    {
        if (FailCommit)
        {
            throw new InvalidOperationException("ORA-02091: transaction rolled back");
        }

        Commits++;
        HasPendingTransaction = false;
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken ct)
    {
        Rollbacks++;
        HasPendingTransaction = false;
        return Task.CompletedTask;
    }

    /// <summary>설정하면 DisposeAsync 가 이 신호를 받을 때까지 호출 스레드를 막는다(응답 없는 서버의 ODP Dispose 흉내). 안전장치로 10초 뒤엔 풀린다.</summary>
    public ManualResetEventSlim? DisposeGate { get; set; }

    public ValueTask DisposeAsync()
    {
        DisposeGate?.Wait(TimeSpan.FromSeconds(10));
        Disposed = true;
        State = SessionState.Closed;
        return ValueTask.CompletedTask;
    }
}

/// <summary>미커밋 확인에 차례로 답하고, 물은 탭 이름을 남긴다. 나머지 대화상자는 쓰지 않는다.</summary>
internal sealed class UncommittedDialogs : IExecutionDialogs
{
    public Queue<UncommittedChoice> Answers { get; } = new();

    public List<string> Asked { get; } = [];

    public Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct) => Task.FromResult(true);

    public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, IReadOnlyDictionary<string, string> last, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

    public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) => Task.FromResult(ScriptErrorAction.Stop);

    public Task<UncommittedChoice> AskUncommittedAsync(CancellationToken ct) => AskUncommittedAsync("(공유)", ct);

    /// <summary>묻는 순간 할 일(시험이 그때의 상태를 본다).</summary>
    public Func<string, Task>? Asking { get; set; }

    public async Task<UncommittedChoice> AskUncommittedAsync(string subject, CancellationToken ct)
    {
        Asked.Add(subject);
        if (Asking is { } asking)
        {
            await asking(subject);
        }

        return Answers.Count > 0 ? Answers.Dequeue() : UncommittedChoice.Cancel;
    }

    public Task<bool> PreviewDmlAsync(string text, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>저장 확인은 정해진 답, 알림은 남긴다. 파일 고르기는 취소.</summary>
internal sealed class EditorDialogs(SaveChoice save) : IEditorDialogs
{
    public List<string> SaveAsked { get; } = [];

    public List<string> Messages { get; } = [];

    public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(false);

    public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(false);

    public Task<SaveChoice> AskSaveChangesAsync(string tabName, CancellationToken ct)
    {
        SaveAsked.Add(tabName);
        return Task.FromResult(save);
    }

    public Task ShowMessageAsync(string title, string message, CancellationToken ct)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }
}
