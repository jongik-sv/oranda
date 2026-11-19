using Oranda.Core.Sessions;

namespace Oranda.Core.Tests.Sessions;

public class RowProgressWatchTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task No_row_within_the_limit_after_the_cursor_opens_throws_a_stall()
    {
        var watch = new RowProgressWatch();
        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        watch.Start();
        watch.Row();

        var stall = await Assert.ThrowsAsync<ServerStallException>(() => watch.WaitAsync(never.Task, Limit).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(Limit, stall.Limit);
        Assert.IsAssignableFrom<TimeoutException>(stall);
        Assert.Equal(1, watch.Rows);
        Assert.False(never.Task.IsCompleted); // 일은 그대로 둔다(기다림만 그만둔다)
    }

    [Fact]
    public async Task Rows_that_keep_coming_are_not_cut_even_when_the_whole_read_is_longer_than_the_limit()
    {
        var watch = new RowProgressWatch();
        var work = Task.Run(async () =>
        {
            watch.Start();
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(40); // 행 사이 40ms, 전체 800ms = 한도의 네 배
                watch.Row();
            }

            return 20;
        });

        Assert.Equal(20, await watch.WaitAsync(work, Limit).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(20, watch.Rows);
    }

    [Fact]
    public async Task Time_before_the_cursor_opens_is_not_measured()
    {
        var watch = new RowProgressWatch();
        var work = Task.Run(async () =>
        {
            await Task.Delay(600); // 문장 실행(서버가 일하는 시간)은 한도의 세 배
            watch.Start();
            watch.Row();
            return "done";
        });

        Assert.Equal("done", await watch.WaitAsync(work, Limit).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_failing_work_rethrows_its_own_exception_and_infinite_disables_the_watch()
    {
        var watch = new RowProgressWatch();
        watch.Start();
        await Assert.ThrowsAsync<InvalidOperationException>(() => watch.WaitAsync(Task.FromException<int>(new InvalidOperationException("x")), Limit));

        var slow = Task.Run(async () =>
        {
            await Task.Delay(400);
            return 1;
        });
        Assert.Equal(1, await watch.WaitAsync(slow, Timeout.InfiniteTimeSpan));
    }
}
