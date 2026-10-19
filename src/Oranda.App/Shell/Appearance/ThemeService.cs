using System;
using Avalonia;
using Avalonia.Media;
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
        app.RequestedThemeVariant = ToVariant(s.Ui.Theme);
        var family = string.IsNullOrWhiteSpace(s.Editor.FontFamily)
            ? DefaultEditorFontFamily
            : s.Editor.FontFamily + ", " + DefaultEditorFontFamily;
        app.Resources[EditorFontFamilyKey] = new FontFamily(family);
        app.Resources[EditorFontSizeKey] = s.Editor.FontSize;
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
