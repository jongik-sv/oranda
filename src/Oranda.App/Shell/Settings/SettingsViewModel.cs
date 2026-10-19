using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.ViewModels;
using Oranda.Core.Settings;

namespace Oranda.App.Shell.Settings;

/// <summary>테마 선택 한 줄.</summary>
public sealed record ThemeChoice(Theme Theme, string Label);

/// <summary>
/// 설정 창(<c>Ctrl+,</c>)의 모델: 테마, 에디터 글꼴·크기·탭 너비. 「저장」을 누르면 한 번에 저장하고
/// 저장소가 바뀐 섹션마다 <c>SettingsChanged</c> 를 낸다(테마·글꼴은 재시작 없이 바뀐다).
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly IShellText _text;

    [ObservableProperty]
    private ThemeChoice _selectedTheme;

    [ObservableProperty]
    private string? _fontFamily;

    [ObservableProperty]
    private decimal _fontSize;

    [ObservableProperty]
    private decimal _tabSize;

    public SettingsViewModel(SettingsService settings, IShellText text, IEnumerable<string>? installedFonts = null)
    {
        _settings = settings;
        _text = text;
        Themes =
        [
            new ThemeChoice(Theme.System, text.Lookup("Shell.settings.theme.system")),
            new ThemeChoice(Theme.Light, text.Lookup("Shell.settings.theme.light")),
            new ThemeChoice(Theme.Dark, text.Lookup("Shell.settings.theme.dark")),
        ];
        FontChoices = [text.Lookup("Shell.settings.editorFontDefault"), .. (installedFonts ?? []).Distinct(StringComparer.CurrentCulture).Order(StringComparer.CurrentCulture)];

        var s = settings.Current;
        _selectedTheme = Themes.First(t => t.Theme == s.Ui.Theme);
        _fontFamily = string.IsNullOrWhiteSpace(s.Editor.FontFamily) ? FontChoices[0] : s.Editor.FontFamily;
        _fontSize = (decimal)s.Editor.FontSize;
        _tabSize = s.Editor.TabSize;
    }

    public IReadOnlyList<ThemeChoice> Themes { get; }

    /// <summary>첫 항목은 「기본 고정폭 글꼴」(= <c>null</c> 로 저장).</summary>
    public IReadOnlyList<string> FontChoices { get; }

    public bool IsReadOnly => _settings.IsReadOnly;

    public string? Notice => _settings.IsReadOnly ? _text.Lookup("Shell.settings.readOnlyNotice") : _settings.Notice;

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    public string TitleText => _text.Lookup("Shell.settings.title");

    public string ThemeLabel => _text.Lookup("Shell.settings.theme");

    public string FontLabel => _text.Lookup("Shell.settings.editorFont");

    public string FontSizeLabel => _text.Lookup("Shell.settings.editorFontSize");

    public string TabSizeLabel => _text.Lookup("Shell.settings.tabSize");

    public string SaveLabel => _text.Lookup("Shell.settings.save");

    public string CloseLabel => _text.Lookup("Shell.settings.close");

    /// <summary>저장하거나 닫기를 눌러 창을 닫아 달라는 요청.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>화면에서 고른 값으로 설정을 저장한다.</summary>
    [RelayCommand]
    public async Task SaveAsync()
    {
        var family = string.IsNullOrWhiteSpace(FontFamily) || FontFamily == FontChoices[0] ? null : FontFamily.Trim();
        await _settings.UpdateAsync(s => s with
        {
            Ui = s.Ui with { Theme = SelectedTheme.Theme },
            Editor = s.Editor with
            {
                FontFamily = family,
                FontSize = (double)FontSize,
                TabSize = (int)TabSize,
            },
        }).ConfigureAwait(true);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
