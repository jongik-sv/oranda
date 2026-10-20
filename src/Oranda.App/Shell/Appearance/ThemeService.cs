using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Shell.Settings;
using Oranda.Core.Events;
using CoreTheme = Oranda.Core.Settings.Theme;

namespace Oranda.App.Shell.Appearance;

/// <summary>
/// 설정 → 화면 반영(ui-shell.md 6장, 5장): 밝게/어둡게/OS 따르기를 재시작 없이 바꾸고,
/// 에디터 글꼴·크기를 앱 리소스(<see cref="EditorFontFamilyKey"/>, <see cref="EditorFontSizeKey"/>)로 내보낸다.
/// 에디터·그리드·출력 View 는 이 리소스를 <c>DynamicResource</c> 로 쓴다.
/// </summary>
public sealed class ThemeService : IDisposable
{
    public const string EditorFontFamilyKey = "OrandaEditorFontFamily";

    public const string EditorFontSizeKey = "OrandaEditorFontSize";

    /// <summary>편집기 들여쓰기 칸 수(int). 편집기 View 가 옵션으로 옮긴다(AvaloniaEdit 옵션은 스타일 속성이 아니다).</summary>
    public const string EditorTabSizeKey = "OrandaEditorTabSize";

    public const string UiFontSizeBaseKey = "OrandaFontSizeBase";

    public const string UiFontSizeSmallKey = "OrandaFontSizeSmall";

    public const double BaseUiFontSize = 13;

    public const double SmallUiFontSize = 11;

    public const double MinUiScale = 0.8;

    public const double MaxUiScale = 2.5;

    /// <summary>고대비 변형: 어두운 테마를 바탕으로 앱 색을 흰색·원색 위주로 바꾼다(7.5 접근성).</summary>
    public static readonly ThemeVariant HighContrastVariant = new("HighContrast", ThemeVariant.Dark);

    /// <summary>한글 대체 글꼴을 뒤에 단 UI 글꼴 목록.</summary>
    public const string UiFontFallback = "Inter, Malgun Gothic, Apple SD Gothic Neo";

    private readonly SettingsService _settings;
    private readonly IDisposable _subscription;

    public ThemeService(SettingsService settings, IEventBus events)
    {
        _settings = settings;
        _subscription = events.Subscribe<SettingsChanged>(this, OnSettingsChanged);
    }

    /// <summary>플랫폼 기본 고정폭 글꼴 목록(한글 대체 포함).</summary>
    public static string DefaultEditorFontFamily =>
        OperatingSystem.IsMacOS() ? "SF Mono, Menlo, Apple SD Gothic Neo" : "Cascadia Mono, Consolas, Malgun Gothic";

    /// <summary>글자 배율을 허용 범위(0.8~2.5)로 맞춘다. 숫자가 아니면 1.0.</summary>
    public static double ClampScale(double scale) =>
        double.IsNaN(scale) || double.IsInfinity(scale) ? 1.0 : Math.Clamp(scale, MinUiScale, MaxUiScale);

    /// <summary>OS 고대비 설정이 켜져 있는가.</summary>
    public static bool SystemPrefersHighContrast(Application app) =>
        app.PlatformSettings?.GetColorValues().ContrastPreference == ColorContrastPreference.High;

    public static ThemeVariant ToVariant(CoreTheme theme) => theme switch
    {
        CoreTheme.Light => ThemeVariant.Light,
        CoreTheme.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    /// <summary>현재 설정을 앱에 반영한다. UI 스레드에서 부른다.</summary>
    public void Apply()
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        var s = _settings.Current;
        var highContrast = s.Ui.HighContrast || SystemPrefersHighContrast(app);
        if (highContrast && !app.Resources.ThemeDictionaries.ContainsKey(HighContrastVariant))
        {
            app.Resources.ThemeDictionaries[HighContrastVariant] = BuildHighContrastDictionary();
        }

        app.RequestedThemeVariant = highContrast ? HighContrastVariant : ToVariant(s.Ui.Theme);
        var scale = ClampScale(s.Ui.UiScale);
        app.Resources[UiFontSizeBaseKey] = BaseUiFontSize * scale;
        app.Resources[UiFontSizeSmallKey] = SmallUiFontSize * scale;
        var family = string.IsNullOrWhiteSpace(s.Editor.FontFamily)
            ? DefaultEditorFontFamily
            : s.Editor.FontFamily + ", " + DefaultEditorFontFamily;
        app.Resources[EditorFontFamilyKey] = new FontFamily(family);
        app.Resources[EditorFontSizeKey] = s.Editor.FontSize;
        app.Resources[EditorTabSizeKey] = s.Editor.TabSize;
    }

    private static ResourceDictionary BuildHighContrastDictionary()
    {
        static SolidColorBrush Brush(uint argb) => new(Color.FromUInt32(argb));
        return new ResourceDictionary
        {
            ["OrandaEnvProductionBrush"] = Brush(0xFFFF4040),
            ["OrandaEnvProductionForegroundBrush"] = Brush(0xFF000000),
            ["OrandaEnvTestBrush"] = Brush(0xFFFFD400),
            ["OrandaEnvDevelopmentBrush"] = Brush(0xFF00FF00),
            ["OrandaUncommittedBrush"] = Brush(0xFFFFD400),
            ["OrandaSeparatorBrush"] = Brush(0xFFFFFFFF),
            ["OrandaFocusBrush"] = Brush(0xFFFFFF00),
            ["OrandaGridCurrentRowBrush"] = Brush(0x40FFFFFF),
            ["OrandaGridRowMarkerBrush"] = Brush(0xFFFFFF00),
            ["OrandaGridNeedsChangeBrush"] = Brush(0xFFB35C00),
        };
    }

    public void Dispose() => _subscription.Dispose();

    private void OnSettingsChanged(SettingsChanged e)
    {
        if (e.Section is "ui" or "editor")
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                Apply();
            }
            else
            {
                Dispatcher.UIThread.Post(Apply);
            }
        }
    }
}
