using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Editing;
using Dock.Avalonia.Controls;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Styles;

/// <summary>앱 전역 컨트롤 모양(App.axaml): 일반 단추의 테두리, 증감 칸의 ▲▼ 배치, 툴바 단추 불변.</summary>
public class ControlStyleTests
{
    [Fact]
    public Task Plain_button_has_a_border_while_toolbar_and_accent_buttons_keep_their_own_look() => HeadlessHost.Run(() =>
    {
        var plain = new Button { Content = "저장" };
        var toolbar = new Button { Content = "T", Classes = { "toolbar" } };
        var window = Show(new StackPanel { Children = { plain, toolbar } });

        Assert.Equal(new Thickness(1), plain.BorderThickness);
        Assert.True(plain.Bounds.Height >= 28, $"단추 높이 {plain.Bounds.Height}");
        Assert.Equal(new Thickness(0), toolbar.BorderThickness);
        window.Close();
    });

    [Fact]
    public Task Disabled_plain_button_keeps_its_box_so_it_still_looks_like_a_button() => HeadlessHost.Run(() =>
    {
        var enabled = new Button { Content = "시험" };
        var disabled = new Button { Content = "저장", IsEnabled = false };
        var window = Show(new StackPanel { Children = { enabled, disabled } });

        Assert.Equal(new Thickness(1), disabled.BorderThickness);
        Assert.Equal(enabled.Bounds.Height, disabled.Bounds.Height);
        Assert.True(disabled.Bounds.Width >= 84, $"최소 폭 {disabled.Bounds.Width}");
        var presenter = disabled.GetVisualDescendants().OfType<ContentPresenter>().First();
        Assert.True(presenter.Background is Avalonia.Media.ISolidColorBrush { Color.A: > 0 }, "비활성 단추의 바탕이 사라졌다");
        Assert.True(presenter.BorderBrush is Avalonia.Media.ISolidColorBrush { Color.A: > 0 }, "비활성 단추의 테두리가 사라졌다");
        Assert.Equal(1.0, presenter.Opacity);
        window.Close();
    });

    [Fact]
    public Task Document_tab_buttons_do_not_get_the_plain_button_box() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        h.Get<DockWorkspace>().OpenDocument("oranda.panel.sqlEditor");
        Dispatcher.UIThread.RunJobs();
        h.Window.UpdateLayout();

        var buttons = h.Window.GetVisualDescendants().OfType<DocumentTabStrip>()
            .SelectMany(strip => strip.GetVisualDescendants().OfType<Button>()).ToList();
        Assert.NotEmpty(buttons);
        var plain = Brush(h.Window, "OrandaButtonBackgroundBrush");
        foreach (var button in buttons)
        {
            Assert.Equal(new Thickness(0), button.BorderThickness);
            Assert.False(button.Background is ISolidColorBrush b && b.Color == plain.Color, "문서 탭 단추에 일반 단추 바탕이 입혀졌다");
        }
    });

    [Fact]
    public Task Editor_text_area_uses_the_light_selection_resource_and_keeps_syntax_colors() => HeadlessHost.Run(() =>
    {
        var area = new TextArea();
        var window = Show(area);

        var inactive = Brush(window, "OrandaEditorSelectionInactiveBrush");
        Assert.True(area.SelectionBrush is ISolidColorBrush i && i.Color == inactive.Color, $"포커스 없는 선택 색 {area.SelectionBrush}");

        area.Focus();
        Dispatcher.UIThread.RunJobs();
        var resource = Brush(window, "OrandaEditorSelectionBrush");
        Assert.True(area.SelectionBrush is ISolidColorBrush b && b.Color == resource.Color, $"선택 색 {area.SelectionBrush}");
        Assert.Null(area.SelectionForeground);
        Assert.Null(area.SelectionBorder);
        window.Close();
    });

    [Fact]
    public Task Numeric_up_down_stacks_its_spin_buttons_inside_the_box_height() => HeadlessHost.Run(() =>
    {
        var box = new NumericUpDown { Value = 0, Minimum = 0, Maximum = 10, Width = 140 };
        var window = Show(box);

        var spinner = box.GetVisualDescendants().OfType<ButtonSpinner>().Single();
        var up = Part(box, "PART_IncreaseButton");
        var down = Part(box, "PART_DecreaseButton");
        Assert.True(up.Bounds.Height + down.Bounds.Height <= box.Bounds.Height, "▲▼ 두 단추가 칸 높이를 넘는다");
        Assert.True(up.Bounds.Width < box.Bounds.Width / 2, "▲▼ 가 가로로 칸의 절반 넘게 차지한다");
        var upTop = up.TranslatePoint(default, spinner)!.Value.Y;
        var downTop = down.TranslatePoint(default, spinner)!.Value.Y;
        Assert.True(downTop >= upTop + up.Bounds.Height - 0.5, "▲ 아래에 ▼ 가 쌓이지 않았다");
        window.Close();
    });

    [Fact]
    public Task Numeric_up_down_disabled_limit_button_has_no_filled_background() => HeadlessHost.Run(() =>
    {
        var box = new NumericUpDown { Value = 0, Minimum = 0, Maximum = 10, Width = 140 };
        var window = Show(box);

        var down = Part(box, "PART_DecreaseButton");
        Assert.False(down.IsEnabled);
        var presenter = down.GetVisualDescendants().OfType<ContentPresenter>().First();
        Assert.True(presenter.Background is null || presenter.Background is Avalonia.Media.ISolidColorBrush { Color.A: 0 }, "꺼진 단추에 바탕이 칠해졌다");
        window.Close();
    });

    private static RepeatButton Part(NumericUpDown box, string name) =>
        box.GetVisualDescendants().OfType<RepeatButton>().Single(b => b.Name == name);

    private static ISolidColorBrush Brush(Window window, string key)
    {
        Assert.True(window.TryFindResource(key, window.ActualThemeVariant, out var value), key);
        return Assert.IsAssignableFrom<ISolidColorBrush>(value);
    }

    private static Window Show(Control content)
    {
        var window = new Window { Width = 300, Height = 200, Content = content };
        window.Show();
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        return window;
    }
}
