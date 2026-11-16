using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Shell.Controls;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Shell.Controls;

// 소유: gc-w9-narrow. 좁은 배치 도우미(폭 < 기준 → 클래스 narrow·상속 IsNarrow).
public sealed class NarrowLayoutTests
{
    [Theory]
    [InlineData(400, 560, true)]
    [InlineData(559, 560, true)]
    [InlineData(560, 560, false)]
    [InlineData(800, 560, false)]
    [InlineData(0, 560, false)]
    [InlineData(400, 0, false)]
    public void Narrow_means_width_below_the_threshold(double width, double threshold, bool narrow) =>
        Assert.Equal(narrow, NarrowLayout.IsNarrowWidth(width, threshold));

    [Fact]
    public async Task Host_gets_the_narrow_class_and_children_inherit_it_as_the_width_changes()
    {
        await HeadlessHost.Run(() =>
        {
            var child = new TextBlock { Text = "x" };
            var host = new Border { Child = new StackPanel { Children = { child } } };
            NarrowLayout.SetThreshold(host, NarrowLayout.DefaultThreshold);
            var window = new Window { Content = host, Width = 800, Height = 200 };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.False(NarrowLayout.GetIsNarrow(host));
                Assert.DoesNotContain(NarrowLayout.NarrowClass, host.Classes);

                window.Width = 400;
                Dispatcher.UIThread.RunJobs();
                Assert.True(NarrowLayout.GetIsNarrow(host));
                Assert.True(NarrowLayout.GetIsNarrow(child));
                Assert.Contains(NarrowLayout.NarrowClass, host.Classes);

                window.Width = 800;
                Dispatcher.UIThread.RunJobs();
                Assert.False(NarrowLayout.GetIsNarrow(child));
                Assert.DoesNotContain(NarrowLayout.NarrowClass, host.Classes);

                // 기준을 끄면 폭과 상관없이 넓은 배치.
                window.Width = 400;
                Dispatcher.UIThread.RunJobs();
                NarrowLayout.SetThreshold(host, 0);
                Assert.False(NarrowLayout.GetIsNarrow(host));
                Assert.DoesNotContain(NarrowLayout.NarrowClass, host.Classes);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
