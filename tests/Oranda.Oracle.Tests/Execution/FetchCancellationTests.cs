using Oranda.Oracle.Execution;

namespace Oranda.Oracle.Tests.Execution;

// 소유: cancel-fetch. 페이지 가져오기 취소 = 협조적 취소 + 왕복이 늦을 때만 드라이버 하드 취소. DB 없이 토큰만 본다.
// 실제 시간을 쓰므로 「아직 안 됨」 단정에는 지연을 넉넉히(1 s) 둔다(바쁜 CI 에서 멈칫해도 넘지 않게).
public class FetchCancellationTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task User_cancel_cancels_driver_token_only_after_delay()
    {
        using var user = new CancellationTokenSource();
        using var cancel = new FetchCancellation(Delay, user.Token);
        var driverCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = cancel.DriverToken.Register(() => driverCancelled.TrySetResult());

        await user.CancelAsync();

        // 왕복이 곧 끝나면 드라이버를 건드리지 않는다(리더를 이어 쓸 수 있게).
        Assert.False(cancel.DriverToken.IsCancellationRequested);
        Assert.False(cancel.HardCancelled);

        // 왕복이 끝나지 않으면 하드 취소한다.
        await driverCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(cancel.HardCancelled);
    }

    [Fact]
    public async Task Fetch_ending_before_delay_never_hard_cancels()
    {
        using var user = new CancellationTokenSource();
        var cancel = new FetchCancellation(Delay, user.Token);
        var driver = cancel.DriverToken;

        await user.CancelAsync();
        cancel.Dispose(); // 행 사이에서 취소를 받아 가져오기가 끝났다

        await Task.Delay(Delay + TimeSpan.FromMilliseconds(500));
        Assert.False(driver.IsCancellationRequested);
        Assert.False(cancel.HardCancelled);
    }

    [Fact]
    public async Task Without_user_cancel_driver_token_stays_live()
    {
        using var user = new CancellationTokenSource();
        using var cancel = new FetchCancellation(TimeSpan.FromMilliseconds(50), user.Token);

        await Task.Delay(200);

        Assert.False(cancel.DriverToken.IsCancellationRequested);
        Assert.False(cancel.HardCancelled);
    }
}
