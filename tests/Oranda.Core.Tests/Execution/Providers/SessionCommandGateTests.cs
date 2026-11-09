using Oranda.Core.Execution.Providers;
using Oranda.Core.Sessions;

namespace Oranda.Core.Tests.Execution.Providers;

/// <summary>
/// D-041: 세션 명령 잠금 자체의 규칙. 차례가 올 때까지 기다리고, 즉시성 범위(<see cref="SessionWait.Brief"/>)에서는 한도까지만 기다린 뒤
/// <see cref="SessionBusyException"/>. 공급자 세션에 묶인 규칙(모든 기능이 같은 잠금 뒤에 줄을 섬)은 각 공급자 시험이 본다.
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
}
