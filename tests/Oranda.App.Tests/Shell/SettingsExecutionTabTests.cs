using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.Update;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Shell;

/// <summary>
/// TSK-09-01(D-016): 설정 「실행」 탭은 기본값이라는 것을 밝힌다 — 탭 위 안내 한 줄, 네 칸 이름 끝 「(기본값)」.
/// <c>ORANDA_RENDER_DIR</c> 가 있으면 ko/en·라이트/다크 PNG(<c>settings-exec-*.png</c>)를 그 폴더에 쓴다.
/// </summary>
[Collection("UiLanguage")]
public sealed class SettingsExecutionTabTests
{
    [Theory]
    [InlineData("ko", "새 탭·다음 실행의 기본값입니다. 지금 탭은 SQL 메뉴에서 바꿉니다.", "(기본값)")]
    [InlineData("en", "These are defaults for new tabs and later runs. Change the current tab from the SQL menu.", "(default)")]
    public Task Execution_tab_shows_the_default_notice_and_marks_each_field_as_default(string lang, string notice, string mark) => HeadlessHost.Run(() =>
    {
        var text = new ShellText(language: lang);
        var vm = new SettingsViewModel(new SettingsService(new MemorySettingsStore()), text);
        var window = new SettingsWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
            var exec = tabs.Items.OfType<TabItem>().Single(t => Equals(t.Header, text.Lookup("Shell.settings.tab.execution")));
            tabs.SelectedItem = exec;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var content = (Control)exec.Content!;
            var hint = content.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ExecNotice");
            Assert.Equal(notice, hint.Text);
            Assert.True(hint.IsEffectivelyVisible);

            // 안내 줄은 칸들보다 위에 있다.
            var boxes = content.GetVisualDescendants().OfType<CheckBox>().ToList();
            Assert.Equal(4, boxes.Count); // 자동 커밋·DBMS_OUTPUT·빈 줄·A-Rows 자동 재실행
            Assert.All(boxes, b => Assert.EndsWith(mark, (string)b.Content!, StringComparison.Ordinal));
            Assert.All(boxes, b => Assert.True(hint.TranslatePoint(default, window)!.Value.Y < b.TranslatePoint(default, window)!.Value.Y));
            var labels = content.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains(text.Lookup("Shell.settings.errorLabel"), labels);
            Assert.EndsWith(mark, text.Lookup("Shell.settings.errorLabel"), StringComparison.Ordinal);

            Render(window, lang);
        }
        finally
        {
            window.Close();
        }
    });

    private static void Render(Window window, string lang)
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (variant, tone) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var size = new PixelSize(Math.Max(1, (int)window.Bounds.Width), Math.Max(1, (int)window.Bounds.Height));
                using var bmp = new RenderTargetBitmap(size);
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"settings-exec-{lang}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Dispatcher.UIThread.RunJobs();
        }
    }
}
