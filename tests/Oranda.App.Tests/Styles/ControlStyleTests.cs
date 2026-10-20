using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
