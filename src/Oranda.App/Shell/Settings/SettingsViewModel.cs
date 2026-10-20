using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.Localization;
using Oranda.App.Shell.Appearance;
using Oranda.App.ViewModels;
using Oranda.Core.Execution;
using Oranda.Core.Settings;

namespace Oranda.App.Shell.Settings;

/// <summary>테마 선택 한 줄.</summary>
public sealed record ThemeChoice(Theme Theme, string Label);

/// <summary>언어 선택 한 줄. <see cref="Code"/> 는 <c>system</c> | <c>ko</c> | <c>en</c>.</summary>
public sealed record LanguageChoice(string Code, string Label);

/// <summary>설정 창 글자 조회(<c>{Binding Strings[키]}</c>): 키 <c>x</c> → 리소스 <c>Shell.settings.x</c>. 창을 열 때의 언어로 고정된다.</summary>
public sealed class SettingsStrings(IShellText text)
{
    public string this[string key] => text.Lookup("Shell.settings." + key);
}

/// <summary>스크립트 오류 처리 선택 한 줄.</summary>
public sealed record ErrorModeChoice(ScriptErrorMode Mode, string Label);

/// <summary>
/// 설정 창(<c>Ctrl+,</c>)의 모델: 일반(테마·스키마 브라우저)·편집기(글꼴·크기·탭)·실행·결과 그리드·이력·모니터·작업 공간. 「저장」을 누르면 한 번에 저장하고
/// 저장소가 바뀐 섹션마다 <c>SettingsChanged</c> 를 낸다(테마·글꼴은 재시작 없이 바뀐다).
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly IShellText _text;

    [ObservableProperty]
    private ThemeChoice _selectedTheme;

    [ObservableProperty]
    private LanguageChoice _selectedLanguage;

    /// <summary>화면 글자 배율(%, 80~250).</summary>
    [ObservableProperty]
    private decimal _uiScalePercent;

    [ObservableProperty]
    private bool _highContrast;

    [ObservableProperty]
    private string? _fontFamily;

    [ObservableProperty]
    private decimal _fontSize;

    [ObservableProperty]
    private decimal _tabSize;

    // ---- 실행 · 결과 그리드 · 이력 · 모니터 · 작업 공간(Orange 옵션 창의 Data·Grid·Session·Startup 자리) ----

    [ObservableProperty]
    private bool _autoCommit;

    [ObservableProperty]
    private bool _dbmsOutput;

    [ObservableProperty]
    private ErrorModeChoice _selectedErrorMode;

    [ObservableProperty]
    private bool _splitOnBlankLine;

    [ObservableProperty]
    private decimal _pageSize;

    [ObservableProperty]
    private decimal _memoryLimitMb;

    [ObservableProperty]
    private decimal _lobFetchSize;

    [ObservableProperty]
    private bool _historyEnabled;

    [ObservableProperty]
    private bool _saveBindValues;

    [ObservableProperty]
    private decimal _historyMaxEntries;

    [ObservableProperty]
    private decimal _historyMaxAgeDays;

    [ObservableProperty]
    private decimal _monitorRefreshSeconds;

    [ObservableProperty]
    private decimal _autosaveSeconds;

    [ObservableProperty]
    private string _gridFontFamily;

    [ObservableProperty]
    private decimal _gridFontSize;

    [ObservableProperty]
    private bool _restoreOnStartup;

    [ObservableProperty]
    private decimal _cacheRetentionDays;

    /// <summary>스키마 브라우저: 객체를 한 번 누르면 미리보기 문서로 연다.</summary>
    [ObservableProperty]
    private bool _schemaSingleClickPreview;

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
        Strings = new SettingsStrings(text);
        Languages =
        [
            new LanguageChoice(UiLanguage.System, text.Lookup("Shell.settings.language.system")),
            new LanguageChoice("ko", text.Lookup("Shell.settings.language.ko")),
            new LanguageChoice("en", text.Lookup("Shell.settings.language.en")),
        ];
        FontChoices = [text.Lookup("Shell.settings.editorFontDefault"), .. (installedFonts ?? []).Distinct(StringComparer.CurrentCulture).Order(StringComparer.CurrentCulture)];

        var s = settings.Current;
        _selectedTheme = Themes.First(t => t.Theme == s.Ui.Theme);
        var code = UiLanguage.Normalize(s.Ui.Language);
        _selectedLanguage = Languages.First(l => l.Code == code);
        _uiScalePercent = Math.Round((decimal)ThemeService.ClampScale(s.Ui.UiScale) * 100);
        _highContrast = s.Ui.HighContrast;
        _fontFamily = string.IsNullOrWhiteSpace(s.Editor.FontFamily) ? FontChoices[0] : s.Editor.FontFamily;
        _fontSize = (decimal)s.Editor.FontSize;
        _tabSize = s.Editor.TabSize;

        ErrorModes =
        [
            new ErrorModeChoice(ScriptErrorMode.Ask, text.Lookup("Shell.settings.mode.ask")),
            new ErrorModeChoice(ScriptErrorMode.Stop, text.Lookup("Shell.settings.mode.stop")),
            new ErrorModeChoice(ScriptErrorMode.Continue, text.Lookup("Shell.settings.mode.continue")),
        ];
        _autoCommit = s.Execution.AutoCommit;
        _dbmsOutput = s.Execution.DbmsOutput;
        _selectedErrorMode = ErrorModes.First(m => m.Mode == s.Execution.ScriptErrorMode);
        _splitOnBlankLine = s.Execution.SplitOnBlankLine;
        _pageSize = s.Grid.PageSize;
        _memoryLimitMb = s.Grid.MemoryLimitMb;
        _lobFetchSize = s.Grid.InitialLobFetchSize;
        _gridFontFamily = string.IsNullOrWhiteSpace(s.Grid.FontFamily) ? FontChoices[0] : s.Grid.FontFamily;
        _gridFontSize = (decimal)s.Grid.FontSize;
        _historyEnabled = s.History.Enabled;
        _saveBindValues = s.History.SaveBindValues;
        _historyMaxEntries = s.History.MaxEntries;
        _historyMaxAgeDays = s.History.MaxAgeDays;
        _monitorRefreshSeconds = s.Monitor.RefreshSeconds;
        _autosaveSeconds = s.Workspace.AutosaveSeconds;
        _restoreOnStartup = s.Workspace.RestoreOnStartup;
        _cacheRetentionDays = s.MetadataCache.RetentionDays;
        _schemaSingleClickPreview = s.SchemaBrowser.SingleClickPreview;
    }

    public IReadOnlyList<ErrorModeChoice> ErrorModes { get; }

    public IReadOnlyList<ThemeChoice> Themes { get; }

    public IReadOnlyList<LanguageChoice> Languages { get; }

    public SettingsStrings Strings { get; }

    /// <summary>첫 항목은 「기본 고정폭 글꼴」(= <c>null</c> 로 저장).</summary>
    public IReadOnlyList<string> FontChoices { get; }

    public bool IsReadOnly => _settings.IsReadOnly;

    public string? Notice => _settings.IsReadOnly ? _text.Lookup("Shell.settings.readOnlyNotice") : _settings.Notice;

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    public string TitleText => _text.Lookup("Shell.settings.title");

    public string ThemeLabel => _text.Lookup("Shell.settings.theme");

    public string LanguageLabel => _text.Lookup("Shell.settings.language");

    public string UiScaleLabel => _text.Lookup("Shell.settings.uiScale");

    public string HighContrastLabel => _text.Lookup("Shell.settings.highContrast");

    public string FontLabel => _text.Lookup("Shell.settings.editorFont");

    public string FontSizeLabel => _text.Lookup("Shell.settings.editorFontSize");

    public string GridFontLabel => _text.Lookup("Shell.settings.gridFont");

    public string GridFontSizeLabel => _text.Lookup("Shell.settings.gridFontSize");

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
        var gridFamily = string.IsNullOrWhiteSpace(GridFontFamily) || GridFontFamily == FontChoices[0] ? null : GridFontFamily.Trim();
        await _settings.UpdateAsync(s => s with
        {
            Ui = s.Ui with
            {
                Theme = SelectedTheme.Theme,
                Language = SelectedLanguage.Code,
                UiScale = ThemeService.ClampScale((double)UiScalePercent / 100),
                HighContrast = HighContrast,
            },
            Editor = s.Editor with
            {
                FontFamily = family,
                FontSize = (double)FontSize,
                TabSize = (int)TabSize,
            },
            Execution = s.Execution with
            {
                AutoCommit = AutoCommit,
                DbmsOutput = DbmsOutput,
                ScriptErrorMode = SelectedErrorMode.Mode,
                SplitOnBlankLine = SplitOnBlankLine,
            },
            Grid = s.Grid with
            {
                PageSize = (int)PageSize,
                MemoryLimitMb = (int)MemoryLimitMb,
                InitialLobFetchSize = (int)LobFetchSize,
                FontFamily = gridFamily,
                FontSize = Math.Clamp((double)GridFontSize, SettingsService.MinEditorFontSize, SettingsService.MaxEditorFontSize),
            },
            History = s.History with
            {
                Enabled = HistoryEnabled,
                SaveBindValues = SaveBindValues,
                MaxEntries = (int)HistoryMaxEntries,
                MaxAgeDays = (int)HistoryMaxAgeDays,
            },
            Monitor = s.Monitor with { RefreshSeconds = Math.Clamp((int)MonitorRefreshSeconds, 2, 60) },
            Workspace = s.Workspace with
            {
                AutosaveSeconds = Math.Clamp((int)AutosaveSeconds, 1, 10),
                RestoreOnStartup = RestoreOnStartup,
            },
            MetadataCache = s.MetadataCache with { RetentionDays = (int)CacheRetentionDays },
            SchemaBrowser = s.SchemaBrowser with { SingleClickPreview = SchemaSingleClickPreview },
        }).ConfigureAwait(true);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
