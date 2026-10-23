using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Shell;

namespace Oranda.App.Tests.Shell.Headless;

// 문서가 없을 때 문서 영역의 시작 화면. 문서 영역 템플릿이 다시 만들어져도(메인 창 내용을 떼었다 다시 붙임) 같은 시작 화면 Control 을
// 두 호스트에 붙이지 않는다(「already has a visual parent」 — 2026-10-24 폴더 트리 크래시와 같은 종류).
public class StartPageHostTests
{
    [Fact]
    public Task Re_templating_the_document_area_builds_a_new_start_page_instead_of_sharing_one() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        h.Window.UpdateLayout();
        var before = Assert.Single(h.Window.GetVisualDescendants().OfType<StartPage>());

        var content = (Control)h.Window.Content!;
        h.Window.Content = null;
        Dispatcher.UIThread.RunJobs();
        h.Window.Content = new Border { Child = content };
        Dispatcher.UIThread.RunJobs();
        h.Window.UpdateLayout();

        var after = Assert.Single(h.Window.GetVisualDescendants().OfType<StartPage>());
        Assert.True(after.IsEffectivelyVisible);
        Assert.NotSame(before, after); // 새 호스트가 자기 시작 화면을 만들었다(옛 것은 옛 호스트와 함께 버려진다)
    });
}
