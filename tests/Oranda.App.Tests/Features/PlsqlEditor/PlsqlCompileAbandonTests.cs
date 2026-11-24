using Oranda.App.Features.Execution;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using static Oranda.App.Tests.Features.PlsqlEditor.PlsqlEditorTests;

namespace Oranda.App.Tests.Features.PlsqlEditor;

/// <summary>
/// PHC-7: 취소(break)가 서버에 닿지 않아 드라이버가 돌아오지 않으면(실측: 긴급 데이터가 사라지는 경로) 정해진 시간 뒤 세션을 버린다.
/// PHC-8: CREATE 가 오래 끝나지 않으면 다른 연결에서 막는 세션을 찾아 경고 띠에 보인다.
/// </summary>
[Collection(Oranda.App.Tests.Features.PlsqlWorkspace.PlsqlStatics.Name)]
public sealed class PlsqlCompileAbandonTests : IDisposable
{
    private static readonly DbObjectRef Proc = new("SCOTT", "P", "PROCEDURE");

    private const string ProcSource = "PROCEDURE p IS BEGIN\n  NULL;\nEND;\n";

    public PlsqlCompileAbandonTests() => PlsqlEditorViewModel.Active = null;

    public void Dispose() => PlsqlEditorViewModel.Active = null;

    /// <summary>버릴 수 있는 가짜 세션(실제 Oracle·Tibero 세션처럼 버리면 끊김이 된다).</summary>
    private sealed class AbandonableSession : ISession, IAbandonableSession
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = new("t", "scott", "localhost/FREEPDB1", Id: Guid.NewGuid());

        public ServerInfo Server => throw new NotSupportedException();

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "FREE/FREEPDB1/SCOTT";

        public SessionState State { get; private set; } = SessionState.Idle;

        public bool HasPendingTransaction => false;

        public string? AbandonedFor { get; private set; }

        public void Abandon(string reason)
        {
            AbandonedFor = reason;
            State = SessionState.Broken;
        }

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeProbe(CompileBlockerResult answer) : ICompileBlockerProbe
    {
        public int Calls { get; private set; }

        public int? AskedSid { get; private set; }

        public Task<CompileBlockerResult> FindAsync(ISession waiting, int sid, CancellationToken ct)
        {
            Calls++;
            AskedSid = sid;
            return Task.FromResult(answer);
        }
    }

    private static async Task<(PlsqlEditorViewModel Vm, FakeExec Exec, AbandonableSession Session)> OpenAsync(FakeExec exec)
    {
        var session = new AbandonableSession();
        var vm = new PlsqlEditorViewModel(new FakeMeta { Source = ProcSource }, exec) { CancelGrace = TimeSpan.FromMilliseconds(200) };
        Assert.True(await vm.OpenAsync(session, Proc));
        return (vm, exec, session);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition());
    }

    // ---- PHC-7 ----

    [Fact]
    public Task Timeout_whose_cancel_never_returns_abandons_the_session_and_says_so() => Ui.Run(async () =>
    {
        var (vm, exec, session) = await OpenAsync(new FakeExec { IgnoreCancel = true });
        var state = new ExecutionState();
        state.AddDedicated(session.Id, session.Profile);
        vm.State = state;
        vm.CompileWaitOverride = TimeSpan.FromMilliseconds(100);

        var outcome = await vm.CompileAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PlsqlCompileStatus.Timeout, outcome.Status);
        Assert.Contains("연결을 끊었습니다", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("서버 소스를 확인하세요", vm.WarningText, StringComparison.Ordinal);
        Assert.NotNull(session.AbandonedFor);
        Assert.Equal(SessionState.Broken, session.State);
        Assert.False(vm.IsBusy);
        Assert.False(state.IsBusy(session.Id)); // 버린 세션의 실행 표시는 내린다
        exec.Release.TrySetResult(); // 닫힌 연결이 뒤늦게 깨어나도 아무 일 없다
    });

    [Fact]
    public Task User_cancel_that_never_returns_abandons_the_session() => Ui.Run(async () =>
    {
        var (vm, exec, session) = await OpenAsync(new FakeExec { IgnoreCancel = true });

        var compile = vm.CompileAsync();
        await exec.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        vm.CancelCompile();
        var outcome = await compile.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PlsqlCompileStatus.Cancelled, outcome.Status);
        Assert.Contains("연결을 끊었습니다", outcome.Message, StringComparison.Ordinal);
        Assert.NotNull(session.AbandonedFor);
        exec.Release.TrySetResult();
    });

    [Fact]
    public Task A_cancel_that_returns_in_time_keeps_the_session() => Ui.Run(async () =>
    {
        var (vm, exec, session) = await OpenAsync(new FakeExec { BlockCreate = true });

        var compile = vm.CompileAsync();
        await exec.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        vm.CancelCompile();

        Assert.Equal(PlsqlCompileStatus.Cancelled, (await compile.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.Null(session.AbandonedFor);
        Assert.Equal(SessionState.Idle, session.State);
    });

    [Fact]
    public Task Cancel_before_the_statement_reaches_the_driver_does_not_abandon() => Ui.Run(async () =>
    {
        // 실행 확인 창이 열려 있는 동안(드라이버로 가기 전) 취소: 엔진이 곧 돌려주므로 버리지 않고 기다린다.
        var (vm, exec, session) = await OpenAsync(new FakeExec { ConfirmDelay = TimeSpan.FromMilliseconds(600) });

        var compile = vm.CompileAsync();
        await exec.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        vm.CancelCompile();

        Assert.Equal(PlsqlCompileStatus.Cancelled, (await compile.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.Null(session.AbandonedFor);
    });

    [Fact]
    public Task Next_compile_on_an_abandoned_session_asks_to_reconnect() => Ui.Run(async () =>
    {
        var (vm, exec, session) = await OpenAsync(new FakeExec { IgnoreCancel = true });
        vm.CompileWaitOverride = TimeSpan.FromMilliseconds(100);
        await vm.CompileAsync().WaitAsync(TimeSpan.FromSeconds(10));
        exec.Release.TrySetResult();
        var sent = exec.Statements.Count;

        var again = await vm.CompileAsync(force: true);

        Assert.Equal(PlsqlCompileStatus.NoSession, again.Status);
        Assert.Contains("다시 접속", again.Message, StringComparison.Ordinal);
        Assert.Equal(sent, exec.Statements.Count);
    });

    [Fact]
    public void Only_the_session_reopened_in_the_same_place_replaces_an_abandoned_one()
    {
        var dead = new AbandonableSession();
        dead.Abandon("x");
        var reopened = new AbandonableSession();
        var other = new AbandonableSession(); // 같은 프로필의 다른 살아 있는 세션(미커밋이 있을 수 있다)

        Assert.Same(reopened, PlsqlEditorViewModel.ReplacementFor(new Reopening(dead, reopened, open: [reopened, other]), dead));
        Assert.Null(PlsqlEditorViewModel.ReplacementFor(new Reopening(dead, null, open: [other]), dead)); // 다시 열지 않았으면 다른 세션으로 옮기지 않는다
        Assert.Null(PlsqlEditorViewModel.ReplacementFor(new Reopening(dead, reopened, open: [other]), dead)); // 다시 연 세션도 이미 닫혔다
        Assert.Null(PlsqlEditorViewModel.ReplacementFor(null, dead));
    }

    /// <summary><see cref="Oranda.App.Features.ConnectionManager.IConnectionService.ReopenedAs"/> 만 답하는 가짜 접속 서비스.</summary>
    private sealed class Reopening(ISession dead, ISession? reopened, IReadOnlyList<ISession> open) : Oranda.App.Features.ConnectionManager.IConnectionService
    {
        public ISession? ActiveSession => open.Count > 0 ? open[0] : null;

        public IReadOnlyList<ISession> Sessions => open;

        public ConnectionProfile? LastProfile => null;

        public bool IsBroken => false;

#pragma warning disable CS0067 // 시험에서 쓰지 않는 이벤트
        public event EventHandler? ActiveSessionChanged;

        public event EventHandler<ISession>? Connected;
#pragma warning restore CS0067

        public ISession? ReopenedAs(ISession closed) => ReferenceEquals(closed, dead) ? reopened : null;

        public bool IsOpen(ISession session) => open.Contains(session);

        public ISession? SessionOf(Guid profileId) => open.Count > 0 ? open[0] : null;

        public bool IsSessionBroken(Guid sessionId) => sessionId == dead.Id;

        public bool Switch(ISession session) => false;

        public Task DisconnectAsync(ISession session, CancellationToken ct) => throw new NotSupportedException();

        public Task<Oranda.Core.Storage.StoredDocument<ConnectionsDocument>> LoadAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<ConnectionProfile> SaveProfileAsync(ConnectionProfile profile, string? password, bool savePassword, CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteProfileAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public Task<Oranda.App.Features.ConnectionManager.ConnectOutcome> ConnectAsync(ConnectionProfile profile, string? password, CancellationToken ct) => throw new NotSupportedException();

        public Task DisconnectAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<Oranda.App.Features.ConnectionManager.ConnectOutcome> ReconnectAsync(string? password, CancellationToken ct) => throw new NotSupportedException();

        public Task<Oranda.App.Features.ConnectionManager.PasswordChangeOutcome> ChangePasswordAsync(ConnectionProfile profile, string? oldPassword, string newPassword, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> HasStoredPasswordAsync(ConnectionProfile profile, CancellationToken ct) => throw new NotSupportedException();

        public Task<ConnectionProfile> ImportWalletAsync(string zipPath, string? walletAlias, CancellationToken ct) => throw new NotSupportedException();

        public Task<Oranda.App.Features.ConnectionManager.ConnectionTestOutcome> TestConnectionAsync(ConnectionProfile profile, string? password, CancellationToken ct) => throw new NotSupportedException();

        public Task<ConnectionProfile> DuplicateProfileAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public Task UpdateFoldersAsync(Func<ConnectionsDocument, ConnectionsDocument> edit, CancellationToken ct) => throw new NotSupportedException();
    }

    // ---- PHC-8 ----

    [Fact]
    public Task A_long_compile_shows_who_is_blocking_it() => Ui.Run(async () =>
    {
        var (vm, exec, _) = await OpenAsync(new FakeExec { BlockCreate = true, SessionSid = 345 });
        var probe = new FakeProbe(new CompileBlockerResult(new CompileBlocker(77, 9, "APP_USER", "batch.exe", "host-a", "SAMPLE.P_SLEEP"), null));
        vm.BlockerProbe = probe;
        vm.BlockerDelay = TimeSpan.FromMilliseconds(50);

        var compile = vm.CompileAsync();
        await exec.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Eventually(() => vm.WarningText.Contains("SID 77", StringComparison.Ordinal));

        Assert.Equal(345, probe.AskedSid); // 다른 연결에서 우리 SID 로 찾는다
        Assert.Contains("APP_USER·batch.exe·host-a", vm.WarningText, StringComparison.Ordinal);
        Assert.Contains("SAMPLE.P_SLEEP", vm.WarningText, StringComparison.Ordinal);
        vm.CancelCompile();
        await compile.WaitAsync(TimeSpan.FromSeconds(10));
    });

    [Fact]
    public Task Without_privilege_the_band_gives_the_general_lock_guidance() => Ui.Run(async () =>
    {
        var (vm, exec, _) = await OpenAsync(new FakeExec { BlockCreate = true });
        vm.BlockerProbe = new FakeProbe(new CompileBlockerResult(null, "V$SESSION·DBA_OBJECTS 를 읽을 권한이 없음: ORA-00942"));
        vm.BlockerDelay = TimeSpan.FromMilliseconds(50);

        var compile = vm.CompileAsync();
        await exec.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Eventually(() => vm.WarningText.Contains("잠금을 기다립니다", StringComparison.Ordinal));

        Assert.Contains("ORA-00942", vm.WarningText, StringComparison.Ordinal);
        Assert.Contains("DBA", vm.WarningText, StringComparison.Ordinal);
        vm.CancelCompile();
        await compile.WaitAsync(TimeSpan.FromSeconds(10));
    });

    [Fact]
    public Task A_quick_compile_does_not_look_for_a_blocker_and_reads_the_sid_once() => Ui.Run(async () =>
    {
        var (vm, exec, _) = await OpenAsync(new FakeExec());
        var probe = new FakeProbe(new CompileBlockerResult(null, "x"));
        vm.BlockerProbe = probe;
        vm.BlockerDelay = TimeSpan.FromMilliseconds(300);

        Assert.Equal(PlsqlCompileStatus.Compiled, (await vm.CompileAsync()).Status);
        Assert.Equal(PlsqlCompileStatus.Compiled, (await vm.CompileAsync(force: true)).Status);
        await Task.Delay(500);

        Assert.Equal(0, probe.Calls);
        Assert.Equal(string.Empty, vm.WarningText);
        Assert.Single(exec.Queries, q => q.Contains("AS sid FROM DUAL", StringComparison.Ordinal)); // 세션마다 한 번
    });

    [Fact]
    public void Blocker_row_maps_by_column_name()
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["blocking_sid"] = 12m, ["blocking_serial"] = 3456m, ["blocking_username"] = "U", ["blocking_program"] = "P", ["blocking_machine"] = null, ["running_object"] = "O.X",
        };

        var blocker = SessionCompileBlockerProbe.ToBlocker(n => row.TryGetValue(n, out var v) ? v : null);

        Assert.Equal(new CompileBlocker(12, 3456, "U", "P", null, "O.X"), blocker);
        Assert.Contains("SID 12(U·P)", PlsqlEditorViewModel.BlockerText(new CompileBlockerResult(blocker, null)), StringComparison.Ordinal);
        Assert.Contains("서버가 막는 세션을 알려 주지 않음",
            PlsqlEditorViewModel.BlockerText(new CompileBlockerResult(new CompileBlocker(null, null, null, null, null, null), null)), StringComparison.Ordinal);
    }
}
