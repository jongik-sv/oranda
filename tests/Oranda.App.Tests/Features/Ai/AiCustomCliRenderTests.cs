using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Ai.Settings;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.Update;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// 설정 「AI 모델」 의 기타 CLI 등록 화면 헤드리스 확인: 고정 경고·줄 편집칸·줄 경고가 보이고, 라이트·다크에서 그려진다.
/// <c>ORANDA_RENDER_DIR</c> 가 있을 때만 PNG(<c>custom-cli-*.png</c>)를 쓴다. 저장소에 넣지 않는다.
/// </summary>
public class AiCustomCliRenderTests
{
    private static void Render(Control control, string name)
    {
        if (Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR") is not { Length: > 0 } dir)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        Dispatcher.UIThread.RunJobs();
        if (control is Window window)
        {
            window.UpdateLayout();
        }

        using var bmp = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Math.Ceiling(control.Bounds.Width)), Math.Max(1, (int)Math.Ceiling(control.Bounds.Height))));
        bmp.Render(control);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
        bmp.Save(Path.Combine(dir, name + ".png"));
#pragma warning restore CS0618
    }

    private static T Find<T>(Window window, string name)
        where T : Control => window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [Fact]
    public Task The_registry_shows_rows_fixed_warnings_and_row_warnings_in_both_themes() => HeadlessHost.Run(() =>
    {
        var ai = new AiSettings
        {
            SystemCliId = AiCustomCliTarget.SelectionOf("c1"),
            MaskLiterals = true,
            CustomClis =
            [
                new AiCustomCli { Id = "c1", Name = "Example CLI", Command = "EXAMPLE_API_KEY=abc examplecli --yolo -p \"{prompt}\"" },
                new AiCustomCli { Id = "c2", Name = "Second", Command = "secondcli | tee" },
            ],
        };
        var page = new AiModelSettingsPage(ai, new AiProfileServices(new SettingsService(new MemorySettingsStore()), CustomCliInstalled: name => name == "examplecli"),
            installCheckDelay: TimeSpan.Zero);
        var window = new Window { Width = 680, Height = 1200, Content = new AiModelSettingsView { DataContext = page, Margin = new Thickness(24) } };
        window.Show();
        try
        {
            window.UpdateLayout();
            Assert.True(Find<Border>(window, "CustomCliPanel").IsEffectivelyVisible);
            Assert.True(Find<TextBlock>(window, "CustomCliSandboxWarning").IsEffectivelyVisible);
            Assert.True(Find<TextBlock>(window, "CustomCliPlainWarning").IsEffectivelyVisible);
            Assert.True(Find<StackPanel>(window, "CustomCliMaskBlock").IsEffectivelyVisible); // 선택 + 값 가림 켬
            Assert.Equal(2, window.GetVisualDescendants().OfType<TextBox>().Count(t => t.Name == "CustomCliCommandBox"));
            Assert.Equal(2, window.GetVisualDescendants().OfType<Button>().Count(b => b.Name == "DeleteCustomCliButton"));
            Assert.False(Find<TextBlock>(window, "NoCustomClisText").IsEffectivelyVisible);
            Assert.NotNull(Find<ComboBox>(window, "ExampleBox").ItemsSource);

            foreach (var (variant, tone) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Render(window, "custom-cli-settings-" + tone);
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            window.Close();
        }
    });

    [Fact]
    public Task An_empty_registry_shows_the_hint_and_still_shows_the_fixed_warnings() => HeadlessHost.Run(() =>
    {
        var page = new AiModelSettingsPage(new AiSettings());
        var window = new Window { Width = 680, Height = 700, Content = new AiModelSettingsView { DataContext = page, Margin = new Thickness(24) } };
        window.Show();
        try
        {
            window.UpdateLayout();
            Assert.True(Find<TextBlock>(window, "NoCustomClisText").IsEffectivelyVisible);
            Assert.True(Find<TextBlock>(window, "CustomCliSandboxWarning").IsEffectivelyVisible);
            Assert.False(Find<StackPanel>(window, "CustomCliMaskBlock").IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    });
}
