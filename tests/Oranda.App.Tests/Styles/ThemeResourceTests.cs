using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Oranda.App.Shell.Appearance;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Styles;

/// <summary>설정 → 앱 리소스 반영: 같은 값은 다시 넣지 않고, 값이 바뀌면 새 값으로 바뀐다.</summary>
// 화면 글자 배율·글꼴은 앱 리소스(프로세스에 하나)를 바꾼다. await 사이에 병렬 시험이 커진 글자로 행 높이·보이는 줄 수를 재지 않게 따로 돈다.
[Collection("EditorAppearance")]
public class ThemeResourceTests
{
    private static readonly string[] Keys =
    [
        ThemeService.UiFontSizeBaseKey,
        ThemeService.UiFontSizeSmallKey,
        ThemeService.TabHeaderFontSizeKey,
        ThemeService.EditorFontFamilyKey,
        ThemeService.EditorFontSizeKey,
        ThemeService.EditorTabSizeKey,
        ThemeService.GridFontFamilyKey,
        ThemeService.GridFontSizeKey,
    ];

    [Fact]
    public Task Applying_the_same_settings_twice_keeps_resources_and_raises_no_change() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var app = Application.Current!;
        var before = Keys.Select(k => app.Resources[k]).ToList();
        var notices = 0;
        void OnChanged(object? sender, ResourcesChangedEventArgs e) => notices++;
        ((IResourceHost)app).ResourcesChanged += OnChanged;
        try
        {
            h.Get<ThemeService>().Apply();
            h.Get<ThemeService>().Apply();
        }
        finally
        {
            ((IResourceHost)app).ResourcesChanged -= OnChanged;
        }

        Assert.Equal(0, notices);
        Assert.Equal(before, Keys.Select(k => app.Resources[k]).ToList());
    });

    [Fact]
    public Task Changing_font_size_editor_font_and_ui_scale_updates_the_resources() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var app = Application.Current!;
        var settings = h.Get<SettingsService>();
        var original = settings.Current;
        try
        {
            await settings.UpdateAsync(s => s with
            {
                Ui = s.Ui with { UiScale = 1.5 },
                Editor = s.Editor with { FontFamily = "Zz Test Mono", FontSize = 19 },
            });
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(ThemeService.BaseUiFontSize * 1.5, app.Resources[ThemeService.UiFontSizeBaseKey]);
            Assert.Equal(ThemeService.SmallUiFontSize * 1.5, app.Resources[ThemeService.UiFontSizeSmallKey]);
            Assert.Equal((ThemeService.BaseUiFontSize - 0.5) * 1.5, app.Resources[ThemeService.TabHeaderFontSizeKey]);
            Assert.Equal(19.0, app.Resources[ThemeService.EditorFontSizeKey]);
            Assert.StartsWith("Zz Test Mono", ((FontFamily)app.Resources[ThemeService.EditorFontFamilyKey]!).Name, StringComparison.Ordinal);

            await settings.UpdateAsync(s => s with { Ui = s.Ui with { UiScale = 1.0 }, Editor = s.Editor with { FontSize = 12 } });
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(ThemeService.BaseUiFontSize, app.Resources[ThemeService.UiFontSizeBaseKey]);
            Assert.Equal(12.0, app.Resources[ThemeService.EditorFontSizeKey]);
        }
        finally
        {
            // 앱은 시험 사이에 남으므로 바꾼 설정을 되돌려 앱 리소스를 원래 값으로 둔다(다음 시험이 이 값을 그대로 본다).
            await settings.UpdateAsync(_ => original);
            Dispatcher.UIThread.RunJobs();
        }
    });
}
