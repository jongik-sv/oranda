using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Oranda.App.Shell.Appearance;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Styles;

/// <summary>기본 화면 글자: 메뉴 막대·메뉴 항목·탭 머리글이 새 기본값(14/12/13.5)과 화면 글자 배율을 따른다.</summary>
// 화면 글자 배율·글꼴은 앱 리소스(프로세스에 하나)를 바꾼다. await 사이에 병렬 시험이 커진 글자로 행 높이·보이는 줄 수를 재지 않게 따로 돈다.
[Collection("EditorAppearance")]
public class UiFontSizeTests
{
    [Fact]
    public void Base_defaults_are_14_and_12()
    {
        Assert.Equal(14, ThemeService.BaseUiFontSize);
        Assert.Equal(12, ThemeService.SmallUiFontSize);
    }

    [Fact]
    public Task Menu_and_tab_header_font_sizes_follow_the_ui_scale() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        h.Get<DockWorkspace>().OpenDocument("oranda.panel.sqlEditor");
        var settings = h.Get<SettingsService>();
        var original = settings.Current;
        try
        {
            foreach (var (scale, expectBase, expectTab) in new[] { (1.0, 14.0, 13.5), (1.5, 21.0, 20.25) })
            {
                await settings.UpdateAsync(s => s with { Ui = s.Ui with { UiScale = scale } });
                Dispatcher.UIThread.RunJobs();
                h.Window.UpdateLayout();

                var menu = h.Window.GetVisualDescendants().OfType<Menu>().First();
                Assert.Equal(expectBase, menu.FontSize);
                var item = h.Window.GetVisualDescendants().OfType<MenuItem>().First();
                item.Open();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(expectBase, item.FontSize);
                Assert.All(item.GetVisualDescendants().OfType<MenuItem>(), sub => Assert.Equal(expectBase, sub.FontSize));
                item.Close();

                var tabs = h.Window.GetVisualDescendants().OfType<Control>()
                    .Where(c => c is DocumentTabStripItem or ToolTabStripItem).Cast<TemplatedControl>().ToList();
                Assert.NotEmpty(tabs);
                Assert.All(tabs, t => Assert.Equal(expectTab, t.FontSize));
            }
        }
        finally
        {
            // 앱 리소스는 시험 사이에 남는다: 1.5 배로 끝내면 뒤 시험의 행 높이·글자 크기가 1.5 배가 된다(목록 행이 창 밖으로 밀림).
            await settings.UpdateAsync(_ => original);
            Dispatcher.UIThread.RunJobs();
        }
    });
}
