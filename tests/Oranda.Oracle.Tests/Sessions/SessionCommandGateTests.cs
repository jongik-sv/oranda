using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Monitoring;
using Oranda.Core.Sessions;
using Oranda.Oracle.Execution;
using Oranda.Oracle.Grid;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Monitoring;
using Oranda.Oracle.Plans;
using Oranda.Oracle.Schema;
using Oranda.Oracle.Sessions;
using Oranda.Oracle.Tuning;

namespace Oranda.Oracle.Tests.Sessions;

/// <summary>
/// D-041: 한 세션 연결을 지키는 명령 잠금은 <see cref="OracleSession.CommandGate"/> 하나다. 연결을 쓰는 모든 기능이 그 잠금 뒤에 줄을 서고,
/// 즉시성 범위(<see cref="SessionWait.Brief"/>)에서는 기다리지 않고 <see cref="SessionBusyException"/>, 같은 흐름 안에서 다시 잡는 길은 없다.
/// 연결은 열지 않는다: 잠금 뒤에 줄을 서지 않는 기능은 곧바로 닫힌 연결을 만나 다른 예외(InvalidOperationException)로 끝난다.
/// </summary>
public sealed class SessionCommandGateTests
{
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Enter_waits_for_release_and_cancelling_the_wait_does_not_take_the_gate()
    {
        var gate = new SessionCommandGate();
        await gate.EnterAsync(default);
        Assert.True(gate.IsHeld);

        var second = gate.EnterAsync(default);
        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        using var cts = new CancellationTokenSource();
        var third = gate.EnterAsync(cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third.WaitAsync(Quick));

        gate.Release();
        await second.WaitAsync(Quick); // 차례가 넘어갔다
        Assert.True(gate.IsHeld);
        Assert.False(gate.TryEnter());
        gate.Release();
        Assert.False(gate.IsHeld);
        Assert.True(gate.TryEnter());
        gate.Release();
    }

    [Fact]
    public async Task Brief_scope_gives_up_with_busy_and_flows_only_into_work_started_inside_it()
    {
        var gate = new SessionCommandGate();
        await gate.EnterAsync(default);

        Assert.Null(SessionWait.CurrentLimit);
        Task inside;
        using (SessionWait.Brief(TimeSpan.FromMilliseconds(30)))
        {
            Assert.Equal(TimeSpan.FromMilliseconds(30), SessionWait.CurrentLimit);
            inside = RunLaterAsync(gate); // 범위 안에서 시작한 비동기 작업은 범위가 끝난 뒤에도 그 한도를 본다
        }

        Assert.Null(SessionWait.CurrentLimit);
        await Assert.ThrowsAsync<SessionBusyException>(() => inside.WaitAsync(Quick));
        Assert.Throws<SessionBusyException>(() =>
        {
            using var _ = SessionWait.Brief(TimeSpan.Zero);
            gate.Enter(default);
        });

        // 범위 밖은 기다린다.
        var outside = gate.EnterAsync(default);
        await Task.Delay(80);
        Assert.False(outside.IsCompleted);
        gate.Release();
        await outside.WaitAsync(Quick);
        gate.Release();

        static async Task RunLaterAsync(SessionCommandGate g)
        {
            await Task.Yield();
            await g.EnterAsync(default);
        }
    }

    [Fact]
    public async Task Every_feature_that_talks_to_the_connection_waits_on_the_session_gate()
    {
        await using var rig = new Rig();
        await rig.Session.CommandGate.EnterAsync(default); // 긴 실행이 연결을 쥔 상태

        var exec = new OracleExecutionService(new ExecutionPolicy(), new AllowInteraction(), rig.Bus, null, pageSize: 50);
        var calls = new (string Name, Func<CancellationToken, Task> Call)[]
        {
            ("commit", ct => rig.Session.CommitAsync(ct)),
            ("rollback", ct => rig.Session.RollbackAsync(ct)),
            ("metadata", ct => new OracleMetadataProvider(null!, rig.Bus, Timeout.InfiniteTimeSpan).ListSchemasAsync(rig.Session, ct)),
            ("ddl", ct => new OracleMetadataProvider(null!, rig.Bus, Timeout.InfiniteTimeSpan)
                .GetDdlAsync(rig.Session, new Core.Metadata.DbObjectRef("SCOTT", "EMP", "TABLE"), new Core.Metadata.DdlOptions(), ct)),
            ("schema", ct => SchemaQuery.RunAsync(rig.Session, "dbinfo.column_reorder_traits", null, _ => { }, ct)),
            ("plan", ct => new OracleExecutionPlanService(exec).GetLastCursorAsync(rig.Session, ct)),
            ("tuning", ct => new OracleTuningService(exec, null!, null!).GetTableStatsAsync(rig.Session, "SCOTT", "EMP", ct)),
            ("monitor", ct => new OracleMonitorService(exec).GetSessionsAsync(rig.Session, new SessionFilter(), ct)),
            ("lob", ct => new OracleLobAccessor().OpenReadAsync(rig.Session, new LobValue(LobKind.Clob, 10) { IsTruncated = true }, ct)),
        };

        foreach (var (name, call) in calls)
        {
            // 잠금 뒤에서 기다리다 취소된다(닫힌 연결에 닿지 않는다).
            using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            {
                var ex = await Record.ExceptionAsync(() => call(cts.Token).WaitAsync(Quick));
                Assert.True(ex is OperationCanceledException, $"{name}: {ex?.GetType().Name ?? "끝남"} {ex?.Message}");
            }

            // 즉시성 범위에서는 기다리지 않고 바쁨으로 끝난다.
            using (SessionWait.Brief(TimeSpan.FromMilliseconds(10)))
            {
                var ex = await Record.ExceptionAsync(() => call(default).WaitAsync(Quick));
                Assert.True(ex is SessionBusyException, $"{name}(brief): {ex?.GetType().Name ?? "끝남"} {ex?.Message}");
            }
        }

        // 실행은 취소된 결과로 돌아온다(닫힌 연결 오류 ORANDA-EXEC 가 아니다).
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            var r = await exec.ExecuteAsync(rig.Session, Select1(), new Dictionary<string, BindValue>(), null, cts.Token).WaitAsync(Quick);
            Assert.Equal(ExecutionStatus.Cancelled, r.Status);
        }

        rig.Session.CommandGate.Release();
        Assert.False(rig.Session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task Paths_that_already_hold_the_gate_do_not_take_it_again()
    {
        await using var rig = new Rig();
        var exec = new OracleExecutionService(new ExecutionPolicy(), new AllowInteraction(), rig.Bus, null, pageSize: 50);

        // 모니터의 세션 끊기는 실행 엔진이 잠금을 잡는다(예전에는 모니터 잠금을 쥔 채 실행을 불렀다: 잠금이 하나면 교착).
        var kill = new OracleMonitorService(exec).KillAsync(rig.Session, new SessionKey(1, 10, 20), immediate: true, default);
        var ex = await Record.ExceptionAsync(() => kill.WaitAsync(Quick));
        Assert.IsType<InvalidOperationException>(ex); // 닫힌 연결까지 갔다(교착이면 TimeoutException)
        Assert.False(rig.Session.CommandGate.IsHeld);

        // 실행·커밋도 끝나면 잠금을 놓는다.
        var r = await exec.ExecuteAsync(rig.Session, Select1(), new Dictionary<string, BindValue>(), null, default).WaitAsync(Quick);
        Assert.Equal(ExecutionStatus.Failed, r.Status);
        Assert.False(rig.Session.CommandGate.IsHeld);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Session.CommitAsync(default).WaitAsync(Quick));
        Assert.False(rig.Session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task Closing_the_session_keeps_the_gate_usable_for_late_cleanup()
    {
        var rig = new Rig();
        await rig.Session.CommandGate.EnterAsync(default);
        await rig.DisposeAsync().AsTask().WaitAsync(Quick);

        rig.Session.CommandGate.Release(); // 닫힌 뒤 늦게 끝난 작업이 놓는다(ObjectDisposedException 아님)
        await rig.Session.CommandGate.EnterForCleanupAsync().WaitAsync(Quick);
        rig.Session.CommandGate.Release();
    }

    [Fact]
    public void Oracle_layer_has_no_other_per_session_connection_lock()
    {
        var src = Path.Combine(FindRepositoryRoot(), "src", "Oranda.Oracle");
        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("ConditionalWeakTable<OracleSession, SemaphoreSlim>", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }

    private static SqlStatement Select1() => new("SELECT 1 FROM dual", default, StatementKind.Query) { Verb = "SELECT" };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Oranda.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Oranda.sln 을 찾지 못했다.");
    }

    /// <summary>열지 않은 연결을 가진 세션. 닫기는 즉시 끝난다.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly OracleConnection _connection = new("User Id=u;Password=p;Data Source=localhost:1/x");

        public Rig()
        {
            var info = new ServerInfo("23.26.0.0.0", "FREE", "localhost", "FREEPDB1", "FREE", "FREEPDB1", true, 1,
                new ServerCapabilities(false, true, true, false, false));
            Session = new OracleSession(Guid.NewGuid(), new ConnectionProfile("dev", "scott", "localhost:1521/FREEPDB1"), info,
                SessionPurpose.Work, "K", _connection, Bus, null, () => ValueTask.CompletedTask);
        }

        public NullBus Bus { get; } = new();

        public OracleSession Session { get; }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class NullBus : IEventBus
    {
        public IDisposable Subscribe<TEvent>(object owner, Action<TEvent> handler)
            where TEvent : OrandaEvent
            => throw new NotSupportedException();

        public void Publish<TEvent>(TEvent e)
            where TEvent : OrandaEvent
        {
        }
    }

    private sealed class AllowInteraction : IExecutionInteraction
    {
        public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) =>
            Task.FromResult(ScriptErrorAction.Stop);
    }
}
