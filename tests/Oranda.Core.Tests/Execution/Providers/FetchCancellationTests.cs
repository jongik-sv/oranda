using Oranda.Core.Execution.Providers;
using Oranda.Testing;

namespace Oranda.Core.Tests.Execution.Providers;

// 소유: cancel-fetch, K4. 페이지 가져오기 취소 = 협조적 취소 + 왕복이 늦을 때만 드라이버 하드 취소. DB 없이 토큰만 본다.
// 시계 = 손으로 돌리는 ManualTimeProvider: 실제 시간에 기대지 않아 바쁜 원격 PC 에서도 판정이 흔들리지 않는다.
public class FetchCancellationTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task User_cancel_cancels_driver_token_only_after_delay()
    {
        var time = new ManualTimeProvider();
        using var user = new CancellationTokenSource();
        using var cancel = new FetchCancellation(Delay, time, user.Token);

        await user.CancelAsync();

        // 왕복이 곧 끝나면 드라이버를 건드리지 않는다(리더를 이어 쓸 수 있게).
        time.Advance(Delay - TimeSpan.FromTicks(1));
        Assert.False(cancel.DriverToken.IsCancellationRequested);
        Assert.False(cancel.HardCancelled);

        // 왕복이 끝나지 않으면 하드 취소한다.
        time.Advance(TimeSpan.FromTicks(1));
        Assert.True(cancel.DriverToken.IsCancellationRequested);
        Assert.True(cancel.HardCancelled);
    }

    [Fact]
    public async Task Fetch_ending_before_delay_never_hard_cancels()
    {
        var time = new ManualTimeProvider();
        using var user = new CancellationTokenSource();
        var cancel = new FetchCancellation(Delay, time, user.Token);
        var driver = cancel.DriverToken;

        await user.CancelAsync();
        Assert.Equal(1, time.PendingTimers); // 하드 취소 타이머가 걸렸다
        cancel.Dispose(); // 행 사이에서 취소를 받아 가져오기가 끝났다

        time.Advance(Delay * 10);
        Assert.False(driver.IsCancellationRequested);
        Assert.False(cancel.HardCancelled);
        Assert.Equal(0, time.PendingTimers); // 걸린 타이머도 함께 정리됐다
    }

    [Fact]
    public void Without_user_cancel_driver_token_stays_live()
    {
        var time = new ManualTimeProvider();
        using var user = new CancellationTokenSource();
        using var cancel = new FetchCancellation(TimeSpan.FromMilliseconds(50), time, user.Token);

        time.Advance(TimeSpan.FromHours(1));

        Assert.False(cancel.DriverToken.IsCancellationRequested);
        Assert.False(cancel.HardCancelled);
    }

    [Fact]
    public async Task System_clock_constructor_still_hard_cancels()
    {
        // 제품 생성자(TimeProvider.System) 경로: 실제 시계로 「결국 된다」만 본다(「아직 안 됨」 단정은 위 시험 몫).
        using var user = new CancellationTokenSource();
        using var cancel = new FetchCancellation(TimeSpan.FromMilliseconds(20), user.Token);
        var driverCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = cancel.DriverToken.Register(() => driverCancelled.TrySetResult());

        await user.CancelAsync();

        await driverCancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(cancel.HardCancelled);
    }
}
