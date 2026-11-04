using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Localization;
using Oranda.App.Features.Update;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.Update;

/// <summary>
/// 업데이트 안내의 대비(Headless). 상태 표시줄 점은 글자가 아닌 표시라 3:1 이상이어야 한다.
/// 렌더 측정(Skia 프레임의 실제 픽셀)에서 다크 상태 표시줄 위 점이 2.47:1 로 묻혔다. 여기서는 그 점·바탕이 실제 컨트롤 트리에서
/// 풀린 색(테마 리소스 적용 후)으로 두 테마 모두 3:1 을 넘는지 고정한다.
/// </summary>
[Collection("UiLanguage")]
public sealed class UpdateContrastTests
{
    private const double NonTextMinimum = 3.0;

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    private static double Ratio(Color a, Color b)
    {
        var x = Luminance(a);
        var y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static Color Solid(IBrush? brush)
    {
        var solid = Assert.IsAssignableFrom<ISolidColorBrush>(brush);
        Assert.Equal(1.0, solid.Opacity * solid.Color.A / 255.0, 3); // 반투명이면 합성이 필요하다 — 이 점·바탕은 불투명이어야 한다
        return solid.Color;
    }

    private const double TextMinimum = 4.5;

    private static Color Over(Color top, double opacity, Color bottom)
    {
        var a = top.A / 255.0 * opacity;
        byte Mix(byte t, byte b) => (byte)Math.Round((t * a) + (b * (1 - a)));
        return Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }

    private static double EffectiveOpacity(Visual v) => v.GetSelfAndVisualAncestors().Aggregate(1.0, (o, a) => o * a.Opacity);

    /// <summary>설정 창 업데이트 탭을 열어 글자 요소의 실제 글자색(불투명도 합성)과 바탕색의 대비를 잰다.</summary>
    private static async Task<(double Ratio, string Detail)> SettingsContrast(string tone, string feed, Func<Window, Control> pick)
    {
        var variant = tone == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        Application.Current!.RequestedThemeVariant = variant;
        try
        {
            var settings = new Oranda.App.Shell.Settings.SettingsService(new MemorySettingsStore());
            var vm = new Oranda.App.Shell.Settings.SettingsViewModel(settings, new Oranda.App.Shell.ShellText(language: "ko"))
            {
                UpdateFeedUrl = feed,
                SelectedTab = Oranda.App.Shell.Settings.SettingsViewModel.UpdateTabIndex,
            };
            var window = new Oranda.App.Shell.Settings.SettingsWindow { DataContext = vm, RequestedThemeVariant = variant };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            await Task.CompletedTask;

            var control = pick(window);
            var windowBg = Solid(window.Background);
            var brush = Assert.IsAssignableFrom<ISolidColorBrush>(((TextBlock)control).Foreground);
            var fg = brush.Color;
            var bg = windowBg;
            if (control.GetVisualAncestors().OfType<TextBox>().FirstOrDefault() is { Background: ISolidColorBrush box })
            {
                bg = Over(box.Color, box.Opacity, windowBg);
            }

            var text = Over(fg, EffectiveOpacity(control) * brush.Opacity, bg);
            window.Close();
            var chain = string.Join(">", control.GetSelfAndVisualAncestors().TakeWhile(a => a is not Window).Select(a => $"{a.GetType().Name}:{a.Opacity}:{brush.Opacity}"));
            return (Ratio(text, bg), $"{tone}: 글자 {text} / 바탕 {bg} 불투명도 {chain}");
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static T Find<T>(Window w, Func<T, bool> predicate)
        where T : Control => w.GetVisualDescendants().OfType<T>().First(predicate);

    /// <summary>설정 창의 오류 문구(.hint.error)는 .hint 의 Opacity 0.6 에 가려지지 않아야 한다(스타일 순서).</summary>
    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public Task Settings_feed_error_message_is_readable(string tone) => HeadlessHost.Run(async () =>
    {
        var (ratio, detail) = await SettingsContrast(tone, "ftp://x.example.com/f", w => Find<TextBlock>(w, t => t.Name == "UpdateFeedMessage"));
        Assert.True(ratio >= TextMinimum, $"설정 창 피드 오류 문구 대비 {ratio:F2}:1 (4.5:1 미만) {detail}");
    });

    /// <summary>설정 창의 입력 칸 안내 글자(PART_Placeholder)는 Fluent 기본보다 진해야 한다.</summary>
    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public Task Settings_feed_placeholder_is_readable(string tone) => HeadlessHost.Run(async () =>
    {
        var (ratio, detail) = await SettingsContrast(tone, string.Empty, w => Find<TextBlock>(w, t => t.Name == "PART_Placeholder" && t.GetVisualAncestors().OfType<TextBox>().Any(b => b.Name == "UpdateFeedBox")));
        Assert.True(ratio >= TextMinimum, $"설정 창 입력 칸 안내 글자 대비 {ratio:F2}:1 (4.5:1 미만) {detail}");
    });

    /// <summary>운영 연결: 상태 표시줄 바탕이 빨강으로 바뀐다(MainWindow.ApplyEnvironment 가 env-production 을 단다). 점은 그 위에서도 3:1 이상이어야 한다.</summary>
    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public Task Status_dot_stands_out_on_the_production_status_bar_of_the_real_main_window(string tone) => HeadlessHost.Run(async () =>
    {
        UiText.Current = new Oranda.App.Shell.ShellText(language: "ko");
        var variant = tone == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        Application.Current!.RequestedThemeVariant = variant;
        try
        {
            var h = new UpdateHarness();
            await h.Service.RunStartupAsync(TimeSpan.Zero);
            using var shell = new ShellHarness();
            shell.Window.RequestedThemeVariant = variant;
            var bar = shell.Window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "StatusBarBorder");
            var view = new UpdateStatusView { DataContext = new UpdateStatusViewModel(h.Service) };
            bar.Child = new Border { Classes = { "status-cell" }, Child = view };
            Dispatcher.UIThread.RunJobs();
            var dot = view.GetVisualDescendants().OfType<Border>().First(b => b.Width == 8);

            var normal = Ratio(Solid(dot.Background), Solid(bar.Background));
            bar.Classes.Set("env-production", true); // 연결이 운영으로 바뀐 뒤에도 따라간다
            Dispatcher.UIThread.RunJobs();
            var production = Ratio(Solid(dot.Background), Solid(bar.Background));

            Assert.True(normal >= NonTextMinimum, $"{tone}: 평소 점 대비 {normal:F2}:1");
            Assert.True(production >= NonTextMinimum, $"{tone}: 운영 상태 표시줄 점 대비 {production:F2}:1 (3:1 미만)");
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Theory]
    [InlineData("light", "available")]
    [InlineData("light", "ready")]
    [InlineData("dark", "available")]
    [InlineData("dark", "ready")]
    public Task Status_dot_stands_out_from_the_status_bar_in_both_themes(string tone, string kind) => HeadlessHost.Run(async () =>
    {
        UiText.Current = new Oranda.App.Shell.ShellText(language: "ko");
        var variant = tone == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        Application.Current!.RequestedThemeVariant = variant;
        try
        {
            var h = new UpdateHarness();
            if (kind == "available")
            {
                await h.Configure(u => u with { AutoDownload = false });
            }

            await h.Service.RunStartupAsync(TimeSpan.Zero);
            Dispatcher.UIThread.RunJobs();

            // 실제 상태 표시줄과 같은 구성: Border.statusbar > Border.status-cell > 항목.
            var view = new UpdateStatusView { DataContext = new UpdateStatusViewModel(h.Service) };
            var bar = new Border { Classes = { "statusbar" }, Padding = new Thickness(4, 2), MinHeight = 24, Child = new Border { Classes = { "status-cell" }, Child = view } };
            var window = new Window { Width = 400, Height = 30, Content = bar, RequestedThemeVariant = variant };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var dot = view.GetVisualDescendants().OfType<Border>().First(b => b.Width == 8);
            var ratio = Ratio(Solid(dot.Background), Solid(bar.Background));
            window.Close();

            Assert.True(ratio >= NonTextMinimum, $"{tone}/{kind}: 상태 표시줄 점 대비 {ratio:F2}:1 (3:1 미만)");
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Dispatcher.UIThread.RunJobs();
        }
    });
}
