using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Tests.Sessions;

/// <summary>
/// Oracle 질의 읽기의 행 진행 감시(<see cref="OracleSession.ReadWatchedAsync{T}"/>·<see cref="OracleMetadataProvider.ReadRowsAsync"/>):
/// 결과를 받는 도중 ReadAsync 가 돌아오지 않는 경우(망이 받다 끊겨 수신이 멈춤)를 가짜 읽기 일로 흉내 낸다. 연결은 열지 않는다. 한도는 시험에서 짧게 준다.
/// </summary>
public sealed class OracleRowStallTests
{
    private static readonly TimeSpan Stall = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_read_that_never_returns_times_out_breaks_the_session_and_releases_the_gate_only_after_the_stuck_work_ends()
    {
        await using var rig = new Rig();
        var stuck = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.OnClose = () => stuck.TrySetException(new ObjectDisposedException("connection")); // 연결을 닫으면 멈춘 수신이 깬다

        var stall = await Assert.ThrowsAsync<ServerStallException>(() => rig.Session.ReadWatchedAsync(async progress =>
        {
            progress.Start();
            for (var i = 0; i < 3; i++)
            {
                progress.Row();
            }

            return await stuck.Task; // 4번째 행이 오지 않는다
        }, Stall, default).WaitAsync(Quick));

        Assert.Equal(Stall, stall.Limit);
        Assert.Equal(SessionState.Broken, rig.Session.State);
        Assert.True(rig.Session.IsStalled);
        var broken = Assert.Single(rig.Events.OfType<SessionBroken>());
        Assert.Equal(DbErrorKind.Disconnected, broken.Error?.Kind);
        Assert.Equal("ORANDA-STALL", broken.Error?.Code);

        // 연결 닫기는 백그라운드에서 시작했고, 멈춘 일이 끝난 뒤에야 명령 잠금을 놓는다(D-041).
        await rig.Closed.Task.WaitAsync(Quick);
        await WaitUntil(() => !rig.Session.CommandGate.IsHeld);

        // 끊긴 것으로 본 세션의 새 질의는 잠금을 기다리지 않고 바로 실패한다.
        var again = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Session.ReadWatchedAsync(_ => Task.FromResult(1), Stall, default).WaitAsync(Quick));
        Assert.Equal(OracleSession.StalledSessionMessage, again.Message);
        var listAgain = await Assert.ThrowsAsync<InvalidOperationException>(
            () => OracleMetadataProvider.QueryAsync(rig.Session, "meta.schemas", _ => { }, default).WaitAsync(Quick));
        Assert.Equal(OracleSession.StalledSessionMessage, listAgain.Message);

        // 세션을 닫아도 연결은 한 번만 닫는다.
        await rig.Session.DisposeAsync();
        Assert.Equal(1, rig.CloseCount);
    }

    [Fact]
    public async Task While_the_stuck_work_runs_the_gate_stays_held_so_no_other_command_can_slip_onto_the_connection()
    {
        await using var rig = new Rig(); // 닫기가 멈춘 일을 깨우지 않는다(늦게 답이 오는 경우)
        var stuck = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAsync<ServerStallException>(() => rig.Session.ReadWatchedAsync(progress =>
        {
            progress.Start();
            return stuck.Task;
        }, Stall, default).WaitAsync(Quick));

        await rig.Closed.Task.WaitAsync(Quick);
        await Task.Delay(Stall);
        Assert.True(rig.Session.CommandGate.IsHeld);
        Assert.False(rig.Session.CommandGate.TryEnter());

        stuck.SetResult(0); // 늦게라도 일이 끝나면 놓는다
        await WaitUntil(() => !rig.Session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task A_slow_read_that_keeps_receiving_rows_is_not_cut()
    {
        await using var rig = new Rig();

        // 행마다 60ms(한도의 1/5), 15행 = 900ms: 전체로는 한도의 세 배지만 행이 계속 온다.
        var rows = await rig.Session.ReadWatchedAsync(async progress =>
        {
            progress.Start();
            for (var i = 0; i < 15; i++)
            {
                await Task.Delay(60);
                progress.Row();
            }

            return progress.Rows;
        }, Stall, default).WaitAsync(Quick);

        Assert.Equal(15, rows);
        Assert.Equal(SessionState.Idle, rig.Session.State);
        Assert.False(rig.Session.CommandGate.IsHeld);
        Assert.Equal(0, rig.CloseCount);
    }

    [Fact]
    public async Task A_slow_execution_before_the_cursor_opens_is_not_measured()
    {
        await using var rig = new Rig();

        var rows = await rig.Session.ReadWatchedAsync(async progress =>
        {
            await Task.Delay(Stall * 3); // 문장 실행(서버가 일하는 시간): 커서를 열기 전
            progress.Start();
            progress.Row();
            return progress.Rows;
        }, Stall, default).WaitAsync(Quick);

        Assert.Equal(1, rows);
        Assert.Equal(SessionState.Idle, rig.Session.State);
        Assert.False(rig.Session.IsStalled);
    }

    [Fact]
    public async Task A_failing_read_is_thrown_as_is_and_does_not_break_the_session()
    {
        await using var rig = new Rig();

        await Assert.ThrowsAsync<FormatException>(() => rig.Session.ReadWatchedAsync<int>(progress =>
        {
            progress.Start();
            throw new FormatException("x");
        }, Stall, default).WaitAsync(Quick));

        Assert.Equal(SessionState.Idle, rig.Session.State);
        Assert.False(rig.Session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task A_row_by_row_read_that_stops_closes_the_cursor_and_releases_the_gate_after_the_stuck_read_ends()
    {
        await using var rig = new Rig();
        var stuck = new TaskCompletionSource<object?[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.OnClose = () => stuck.TrySetException(new ObjectDisposedException("connection"));
        var cursorClosed = 0;
        var calls = 0;

        await Assert.ThrowsAsync<ServerStallException>(() => OracleMetadataProvider.ReadRowsAsync(rig.Session,
            _ => ++calls <= 3 ? Task.FromResult<object?[]?>([calls]) : stuck.Task,
            () =>
            {
                Assert.True(rig.Session.CommandGate.IsHeld); // 커서 닫기도 잠금 안에서
                Interlocked.Increment(ref cursorClosed);
                return ValueTask.CompletedTask;
            },
            Stall, default).WaitAsync(Quick));

        Assert.Equal(SessionState.Broken, rig.Session.State);
        await rig.Closed.Task.WaitAsync(Quick);
        await WaitUntil(() => !rig.Session.CommandGate.IsHeld);
        Assert.Equal(1, Volatile.Read(ref cursorClosed));
    }

    [Fact]
    public async Task A_row_by_row_read_does_not_count_the_time_spent_waiting_for_the_gate()
    {
        await using var rig = new Rig();
        var gate = rig.Session.CommandGate;
        var calls = 0;
        Task? userRun = null;

        // 2번째 행 뒤에 사용자 실행이 잠금을 한도의 세 배 동안 쥔다(행마다 다시 잡는 이어 읽기는 그 뒤에 줄을 선다).
        var rows = await OracleMetadataProvider.ReadRowsAsync(rig.Session,
            _ =>
            {
                calls++;
                if (calls == 3)
                {
                    return Task.FromResult<object?[]?>(null);
                }

                if (calls == 2)
                {
                    userRun = Task.Run(async () =>
                    {
                        await gate.EnterAsync(default);
                        try
                        {
                            await Task.Delay(Stall * 3);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }, CancellationToken.None);
                }

                return Task.FromResult<object?[]?>([calls]);
            },
            () => ValueTask.CompletedTask,
            Stall, default).WaitAsync(Quick);

        Assert.Equal(2, rows.Count);
        await userRun!.WaitAsync(Quick);
        Assert.Equal(SessionState.Idle, rig.Session.State);
        Assert.False(gate.IsHeld);
    }

    [Fact]
    public async Task A_row_by_row_read_with_slow_but_steady_rows_finishes()
    {
        await using var rig = new Rig();
        var calls = 0;
        var closed = 0;

        var rows = await OracleMetadataProvider.ReadRowsAsync(rig.Session,
            async _ =>
            {
                await Task.Delay(60, CancellationToken.None);
                return ++calls > 15 ? null : [calls];
            },
            () =>
            {
                closed++;
                return ValueTask.CompletedTask;
            },
            Stall, default).WaitAsync(Quick);

        Assert.Equal(15, rows.Count);
        Assert.Equal(1, closed);
        Assert.False(rig.Session.CommandGate.IsHeld);
        Assert.Equal(SessionState.Idle, rig.Session.State);
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        var until = DateTime.UtcNow + Quick;
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "제한 시간 안에 끝나지 않았다.");
            await Task.Delay(10);
        }
    }

    /// <summary>열지 않은 연결을 가진 세션. 연결 닫기는 횟수를 세고 <see cref="OnClose"/> 를 부른다.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly OracleConnection _connection = new("User Id=u;Password=p;Data Source=localhost:1/x");
        private readonly List<OrandaEvent> _events = [];
        private int _closeCount;

        public Rig()
        {
            var info = new ServerInfo("23.26.0.0.0", "FREE", "localhost", "FREEPDB1", "FREE", "FREEPDB1", true, 1,
                new ServerCapabilities(false, true, true, false, false));
            var bus = new EventBus();
            bus.Subscribe<OrandaEvent>(this, e =>
            {
                lock (_events)
                {
                    _events.Add(e);
                }
            });
            Session = new OracleSession(Guid.NewGuid(), new ConnectionProfile("dev", "scott", "localhost:1521/FREEPDB1"), info,
                SessionPurpose.Work, "K", _connection, bus, null, () =>
                {
                    Interlocked.Increment(ref _closeCount);
                    OnClose?.Invoke();
                    Closed.TrySetResult();
                    return ValueTask.CompletedTask;
                });
        }

        public OracleSession Session { get; }

        public Action? OnClose { get; set; }

        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CloseCount => Volatile.Read(ref _closeCount);

        public IReadOnlyList<OrandaEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
