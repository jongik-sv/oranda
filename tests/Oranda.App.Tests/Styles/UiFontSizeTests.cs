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
    });
}
