using Oranda.App.Shell;

namespace Oranda.App.Tests.Shell;

public class UiThreadTests
{
    [Fact]
    public void PostIfAppRunning_runs_inline_when_no_application_is_alive()
    {
        if (Avalonia.Application.Current is not null)
        {
            return; // 다른 시험이 Headless 앱을 띄워 둔 경우는 이 조건을 만들 수 없다.
        }

        var ran = false;
        UiThread.PostIfAppRunning(() => ran = true);
        Assert.True(ran);
    }

    [Fact]
    public void Null_action_throws()
    {
        Assert.Throws<ArgumentNullException>(() => UiThread.Post(null!));
        Assert.Throws<ArgumentNullException>(() => UiThread.PostIfAppRunning(null!));
    }
}
