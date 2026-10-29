using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.Ai.Settings;
using Oranda.App.Features.FormatterProfiles;
using Oranda.App.Features.Localization;
using Oranda.App.Features.Update;
using Oranda.App.Shell.Appearance;
using Oranda.App.ViewModels;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Features;
using Oranda.Core.Formatting;
using Oranda.Core.Settings;
using Oranda.Core.Shell;
using Oranda.Core.Storage;

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
/// 설정 창(<c>Ctrl+,</c>)의 모델: 일반(테마·스키마 브라우저)·편집기(글꼴·크기·탭)·SQL 포맷(<see cref="SqlFormatPage"/>)·실행·결과 그리드·이력·모니터·작업 공간. 「저장」을 누르면 한 번에 저장하고
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
    [NotifyPropertyChangedFor(nameof(PanelFontSizeWatermark))]
    private decimal _uiScalePercent;

    /// <summary>패널·표·보조 글자 크기(pt, 8~32). null(비움) = 기본 크기에 화면 글자 배율을 곱한 값.</summary>
    [ObservableProperty]
    private decimal? _panelFontSize;

    [ObservableProperty]
    private bool _highContrast;

    [ObservableProperty]
    private string? _fontFamily;

    /// <summary>감사 컬럼 이름 목록(쉼표·줄로 나눔, 와일드카드 *·? 허용, 대소문자 무시). 테이블 레이아웃 창의 「감사 컬럼 빼기」가 쓴다(TSK-13-40).</summary>
    [ObservableProperty]
    private string _auditColumns;

    [ObservableProperty]
    private decimal _fontSize;

    [ObservableProperty]
    private decimal _tabSize;

    [ObservableProperty]
    private bool _lintEnabled;

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
    private bool _gatherPlanStatisticsHint;

    [ObservableProperty]
    private decimal _pageSize;

    [ObservableProperty]
    private decimal _memoryLimitMb;

    [ObservableProperty]
    private decimal _lobFetchSize;

    /// <summary>「변경 추적하기」(그리드 「플래시백 ▸ 이 행 변경 이력」, TSK-13-44) 쓰기. 끄면 기간 옵션이 흐려진다.</summary>
    [ObservableProperty]
    private bool _flashbackTrackEnabled;

    /// <summary>변경 추적 기간이 「최근 N일」이면 true, 「가능한 전체 기간」이면 false.</summary>
    [NotifyPropertyChangedFor(nameof(FlashbackTrackAll))]
    [ObservableProperty]
    private bool _flashbackTrackRecent;

    /// <summary>「최근 N일」의 N(0.01~365, 소수 허용).</summary>
    [ObservableProperty]
    private decimal _flashbackTrackDays;

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

    /// <summary>「워크스페이스마다 새 세션」(TSK-14-03, <c>workspace.sessionPerWorkspace</c>): 새 워크스페이스가 처음 실행할 때 자기 세션을 연다.</summary>
    [ObservableProperty]
    private bool _sessionPerWorkspace;

    [ObservableProperty]
    private decimal _cacheRetentionDays;

    /// <summary>스키마 브라우저: 객체를 한 번 누르면 미리보기 문서로 연다.</summary>
    [ObservableProperty]
    private bool _schemaSingleClickPreview;

    // ---- 업데이트(TSK-10-01) ----

    /// <summary>설정 창의 선택된 탭(0 시작). 저장을 막는 값이 있으면 그 탭으로 옮겨 보인다.</summary>
    [ObservableProperty]
    private int _selectedTab;

    /// <summary>「기능」 탭의 순서(일반 다음, TSK-07-04). <c>oranda.tools.roles</c> 가 이 탭으로 연다.</summary>
    public const int FeaturesTabIndex = 1;

    /// <summary>「편집기」 탭의 순서(기능 다음).</summary>
    public const int EditorTabIndex = 2;

    /// <summary>「SQL 포맷」 탭의 순서(편집기 다음).</summary>
    public const int FormatTabIndex = 3;

    /// <summary>「그리드」 탭의 순서(실행 다음).</summary>
    public const int GridTabIndex = 5;

    /// <summary>「AI 모델」 쪽(시안 1). aiTuning 기능이 꺼져 있으면 탭을 보이지 않는다(<see cref="AiTabVisible"/>).</summary>
    private AiSettings _aiSaved;

    public AiModelSettingsPage AiModel { get; }

    public bool AiTabVisible { get; }

    /// <summary>업데이트 탭의 순서(<c>SettingsWindow.axaml</c> 의 업데이트 탭, 그 뒤에 AI 모델 탭).</summary>
    public const int UpdateTabIndex = 8;

    [ObservableProperty]
    private bool _updateCheckOnStartup;

    [ObservableProperty]
    private bool _updateAutoDownload;

    [NotifyPropertyChangedFor(nameof(FeedMessage), nameof(FeedMessageIsError), nameof(HasFeedMessage))]
    [ObservableProperty]
    private string _updateFeedUrl;

    [NotifyPropertyChangedFor(nameof(SkippedVersionText), nameof(HasSkippedVersion))]
    [ObservableProperty]
    private string? _skippedVersion;

    [ObservableProperty]
    private string _updateCheckText = string.Empty;

    /// <summary>「지금 확인」 결과가 오류면 원문(예외 형식·메시지)을 툴팁으로 보인다.</summary>
    [ObservableProperty]
    private string? _updateCheckToolTip;

    private bool _skippedCleared;

    // 프로필 복사본을 만들었지만 설정에 아직 쓰지 못한 그 ID(다시 저장할 때 이어 쓴다).
    private string? _pendingProfileId;

    /// <param name="sqlFormat">「SQL 포맷」 쪽. null 이면 앱 서비스(프로필 저장소·포매터)로 만든다(앱 밖이면 저장하지 않는 기본값 쪽).</param>
    /// <param name="featureGate">「기능」 탭의 재시작 안내를 읽을 게이트. null 이면 앱 서비스에서 찾는다(앱 밖이면 안내 없음).</param>
    public SettingsViewModel(SettingsService settings, IShellText text, IEnumerable<string>? installedFonts = null, SqlFormatPage? sqlFormat = null, IFeatureGate? featureGate = null)
    {
        _settings = settings;
        _text = text;
        SqlFormat = sqlFormat ?? CreateSqlFormatPage(settings, text);
        Features = new FeatureSettingsPage(
            settings.Current.Features,
            text,
            featureGate ?? (Avalonia.Application.Current as App)?.Services?.GetService<IFeatureGate>());
        var gate = featureGate ?? (Avalonia.Application.Current as App)?.Services?.GetService<IFeatureGate>();
        _aiSaved = settings.Current.Ai;
        AiModel = new AiModelSettingsPage(settings.Current.Ai);
        AiTabVisible = gate?.IsEnabled(FeatureIds.AiTuning) ?? false;
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
        _panelFontSize = s.Ui.PanelFontSize is { } panel ? (decimal)ThemeService.ClampPanelFontSize(panel) : null;
        _highContrast = s.Ui.HighContrast;
        _fontFamily = string.IsNullOrWhiteSpace(s.Editor.FontFamily) ? FontChoices[0] : s.Editor.FontFamily;
        _fontSize = (decimal)s.Editor.FontSize;
        _tabSize = s.Editor.TabSize;
        _lintEnabled = s.Editor.LintEnabled;
        _auditColumns = s.TableLayout.AuditColumns ?? string.Empty;

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
        _gatherPlanStatisticsHint = s.Execution.GatherPlanStatisticsHint;
        _pageSize = s.Grid.PageSize;
        _memoryLimitMb = s.Grid.MemoryLimitMb;
        _lobFetchSize = s.Grid.InitialLobFetchSize;
        _flashbackTrackEnabled = s.Grid.FlashbackTrackEnabled;
        _flashbackTrackRecent = s.Grid.FlashbackTrackRange != FlashbackTrackRange.All;
        _flashbackTrackDays = (decimal)s.Grid.FlashbackTrackDays;
        _gridFontFamily = string.IsNullOrWhiteSpace(s.Grid.FontFamily) ? FontChoices[0] : s.Grid.FontFamily;
        _gridFontSize = (decimal)s.Grid.FontSize;
        _historyEnabled = s.History.Enabled;
        _saveBindValues = s.History.SaveBindValues;
        _historyMaxEntries = s.History.MaxEntries;
        _historyMaxAgeDays = s.History.MaxAgeDays;
        _monitorRefreshSeconds = s.Monitor.RefreshSeconds;
        _autosaveSeconds = s.Workspace.AutosaveSeconds;
        _restoreOnStartup = s.Workspace.RestoreOnStartup;
        _sessionPerWorkspace = s.Workspace.SessionPerWorkspace;
        _cacheRetentionDays = s.MetadataCache.RetentionDays;
        _schemaSingleClickPreview = s.SchemaBrowser.SingleClickPreview;
        _updateCheckOnStartup = s.Update.CheckOnStartup;
        _updateAutoDownload = s.Update.AutoDownload;
        _updateFeedUrl = s.Update.FeedUrl ?? string.Empty;
        _skippedVersion = s.Update.SkippedVersion;
    }

    /// <summary>「SQL 포맷」 쪽(Orange SQL Formatter 대응, TSK-13-38).</summary>
    public SqlFormatPage SqlFormat { get; }

    /// <summary>「가능한 전체 기간」 라디오(<see cref="FlashbackTrackRecent"/> 의 반대).</summary>
    public bool FlashbackTrackAll
    {
        get => !FlashbackTrackRecent;
        set => FlashbackTrackRecent = !value;
    }

    /// <summary>「기능」 탭(Role 선택·기능 세부·재시작 안내, TSK-07-04).</summary>
    public FeatureSettingsPage Features { get; }

    /// <summary>시험용: 「자세히…」가 포매터 프로필 화면을 여는 길(기본 = <c>oranda.format.profiles</c> 명령).</summary>
    internal Func<Task>? OpenProfiles { get; init; }

    private static SqlFormatPage CreateSqlFormatPage(SettingsService settings, IShellText text)
    {
        var services = (Avalonia.Application.Current as App)?.Services;
        return new SqlFormatPage(
            services?.GetService<IFormatterProfileStore>(),
            services?.GetService<ISqlFormatter>(),
            settings.Current.FormatterProfileId,
            text);
    }

    /// <summary>「자세히…」: 설정을 저장하고 창을 닫은 뒤 포매터 프로필 화면을 연다. 저장을 막는 값이 있으면 열지 않는다.</summary>
    [RelayCommand]
    private async Task OpenFormatterProfilesAsync()
    {
        if (!await SaveCoreAsync().ConfigureAwait(true))
        {
            return;
        }

        if (OpenProfiles is not null)
        {
            await OpenProfiles().ConfigureAwait(true);
            return;
        }

        var services = (Avalonia.Application.Current as App)?.Services;
        if (services?.GetService<ICommandRegistry>() is { } registry && services.GetService<IContextKeys>() is { } keys)
        {
            await registry.ExecuteAsync(FormatterProfilesCommands.ShowProfiles, keys).ConfigureAwait(true);
        }
    }

    // ---- 업데이트 탭 ----

    /// <summary>지금 실행 중인 버전(설치본이면 설치 버전, 아니면 개발 실행본 표시).</summary>
    public string UpdateVersionText { get; } = AppVersion.Display;

    /// <summary>피드 칸이 비었을 때 회색으로 보일 안내: 빌드 기본값을 알려 주거나 「기본 주소 없음」.</summary>
    public string UpdateFeedPlaceholder => AppVersion.BuildDefaultFeed ?? _text.Lookup("Shell.settings.update.noDefaultFeed");

    /// <summary>피드 칸 아래 도움말: 비우면 기본 주소, GitHub 릴리스 주소(<c>…/releases/latest/download</c>)도 넣을 수 있다는 예시.</summary>
    public string UpdateFeedHint => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        _text.Lookup("Shell.settings.update.feedHint"),
        FeedExample(AppVersion.BuildDefaultFeed));

    /// <summary>예시 주소: 빌드 기본값이 GitHub 저장소면 그 릴리스 주소, 아니면 일반 형식.</summary>
    internal static string FeedExample(string? defaultFeed) =>
        UpdateFeedParser.Parse(defaultFeed).Feed is { Kind: UpdateFeedKind.GitHub } github
            ? github.Location + "/releases/latest/download"
            : "https://github.com/<owner>/<repo>/releases/latest/download";

    /// <summary>피드 칸 아래 한 줄: 해석할 수 없는 값이면 이유, http 면 암호화 경고. 아니면 비어 있다.</summary>
    public string FeedMessage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(UpdateFeedUrl))
            {
                return string.Empty;
            }

            var parsed = UpdateFeedParser.Parse(UpdateFeedUrl);
            if (!parsed.IsValid)
            {
                return _text.Lookup(UpdateFeedParser.ErrorKey(parsed.Error));
            }

            return parsed.Feed!.IsPlainHttp ? _text.Lookup("Shell.settings.update.httpWarning") : string.Empty;
        }
    }

    public bool FeedMessageIsError => !string.IsNullOrWhiteSpace(UpdateFeedUrl) && !UpdateFeedParser.Parse(UpdateFeedUrl).IsValid;

    public bool HasFeedMessage => FeedMessage.Length > 0;

    public string SkippedVersionText => string.IsNullOrEmpty(SkippedVersion) ? _text.Lookup("Shell.settings.update.skippedNone") : SkippedVersion;

    public bool HasSkippedVersion => !string.IsNullOrEmpty(SkippedVersion);

    public string LastCheckedText =>
        _settings.Current.Update.LastCheckedUtc is { } at
            ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : _text.Lookup("Shell.settings.update.never");

    [RelayCommand]
    private void ClearSkippedVersion()
    {
        SkippedVersion = null;
        _skippedCleared = true;
    }

    /// <summary>저장한 설정으로 지금 확인하고 결과 한 줄을 보인다. 새 버전을 받는 일은 업데이트 창이 맡는다.</summary>
    [RelayCommand]
    private async Task CheckUpdateNowAsync()
    {
        if (UpdateService.Current is not { } service)
        {
            return;
        }

        UpdateCheckText = _text.Lookup("Update.state.checking");
        UpdateCheckToolTip = null;
        await service.CheckAsync(manual: true).ConfigureAwait(true);
        UpdateCheckText = UpdateTexts.Status(service, _text);
        UpdateCheckToolTip = UpdateTexts.ErrorToolTip(service);
        OnPropertyChanged(nameof(LastCheckedText));
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

    public string PanelFontSizeLabel => _text.Lookup("Shell.settings.panelFontSize");

    /// <summary>패널·표 글자 크기를 비웠을 때 쓰이는 값(화면 글자 배율에 맞춘 pt)을 칸 안에 흐리게 보인다.</summary>
    public string PanelFontSizeWatermark =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, _text.Lookup("Shell.settings.panelFontSizeAuto"),
            (ThemeService.SmallUiFontSize * ThemeService.ClampScale((double)UiScalePercent / 100)).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture));

    public string FontSizeHint => _text.Lookup("Shell.settings.fontSizeHint");

    public string EditorTabLinkLabel => _text.Lookup("Shell.settings.fontHintEditorTab");

    public string GridTabLinkLabel => _text.Lookup("Shell.settings.fontHintGridTab");

    /// <summary>일반 탭 「글자 크기」 안내의 「편집기 탭」 링크: 편집기 글꼴을 바꾸는 탭으로 옮긴다.</summary>
    [RelayCommand]
    private void ShowEditorTab() => SelectedTab = EditorTabIndex;

    /// <summary>일반 탭 「글자 크기」 안내의 「그리드 탭」 링크: 결과 그리드 글꼴을 바꾸는 탭으로 옮긴다.</summary>
    [RelayCommand]
    private void ShowGridTab() => SelectedTab = GridTabIndex;

    public string HighContrastLabel => _text.Lookup("Shell.settings.highContrast");

    public string FontLabel => _text.Lookup("Shell.settings.editorFont");

    public string FontSizeLabel => _text.Lookup("Shell.settings.editorFontSize");

    public string GridFontLabel => _text.Lookup("Shell.settings.gridFont");

    public string GridFontSizeLabel => _text.Lookup("Shell.settings.gridFontSize");

    public string TabSizeLabel => _text.Lookup("Shell.settings.tabSize");

    public string LintLabel => _text.Lookup("Shell.settings.lintEnabled");

    public string SaveLabel => _text.Lookup("Shell.settings.save");

    public string CloseLabel => _text.Lookup("Shell.settings.close");

    /// <summary>저장하거나 닫기를 눌러 창을 닫아 달라는 요청.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>화면에서 고른 값으로 설정을 저장한다.</summary>
    [RelayCommand]
    public Task SaveAsync() => SaveCoreAsync(allowStayOpen: true);

    /// <summary>
    /// 저장하고 창을 닫는다. 저장을 막는 값이 있으면 그 탭으로 옮기고 false.
    /// <paramref name="allowStayOpen"/> 이고 기능을 바꿔 재시작해야 적용되는 기능이 생겼으면 창을 닫지 않고 「기능」 탭의 안내 띠를 보인다.
    /// </summary>
    private async Task<bool> SaveCoreAsync(bool allowStayOpen = false)
    {
        if (FeedMessageIsError)
        {
            SelectedTab = UpdateTabIndex; // 잘못된 피드 주소는 저장하지 않는다: 이유가 보이는 탭으로 옮긴다
            return false;
        }

        // 포맷 프로필을 먼저 쓴다: 실패하면 설정도 그대로 두고 이유를 그 탭에 보인다.
        string? profileId;
        try
        {
            profileId = await SqlFormat.SaveAsync().ConfigureAwait(true) ?? _pendingProfileId;
            _pendingProfileId = profileId;
            SqlFormat.ErrorText = string.Empty;
        }
        catch (Exception ex) when (IsSaveError(ex))
        {
            SqlFormat.ErrorText = ex.Message;
            SelectedTab = FormatTabIndex;
            return false;
        }

        var family = string.IsNullOrWhiteSpace(FontFamily) || FontFamily == FontChoices[0] ? null : FontFamily.Trim();
        var gridFamily = string.IsNullOrWhiteSpace(GridFontFamily) || GridFontFamily == FontChoices[0] ? null : GridFontFamily.Trim();
        var featuresChanged = Features.IsDirty;
        var features = Features.ToSettings();
        try
        {
            await _settings.UpdateAsync(s => s with
            {
                // 「기능」 탭을 건드리지 않았으면 설정을 그대로 둔다(알 수 없는 id·다른 곳에서 바뀐 값을 덮지 않는다).
                Features = featuresChanged ? features : s.Features,
                FormatterProfileId = profileId ?? s.FormatterProfileId,
                Ui = s.Ui with
                {
                    Theme = SelectedTheme.Theme,
                    Language = SelectedLanguage.Code,
                    UiScale = ThemeService.ClampScale((double)UiScalePercent / 100),
                    PanelFontSize = PanelFontSize is { } panelSize ? ThemeService.ClampPanelFontSize((double)panelSize) : null,
                    HighContrast = HighContrast,
                },
                Editor = s.Editor with
                {
                    FontFamily = family,
                    FontSize = (double)FontSize,
                    TabSize = (int)TabSize,
                    LintEnabled = LintEnabled,
                },
                TableLayout = s.TableLayout with { AuditColumns = (AuditColumns ?? string.Empty).Trim() },
                Execution = s.Execution with
                {
                    AutoCommit = AutoCommit,
                    DbmsOutput = DbmsOutput,
                    ScriptErrorMode = SelectedErrorMode.Mode,
                    SplitOnBlankLine = SplitOnBlankLine,
                    GatherPlanStatisticsHint = GatherPlanStatisticsHint,
                },
                Grid = s.Grid with
                {
                    PageSize = (int)PageSize,
                    MemoryLimitMb = (int)MemoryLimitMb,
                    InitialLobFetchSize = (int)LobFetchSize,
                    FlashbackTrackEnabled = FlashbackTrackEnabled,
                    FlashbackTrackRange = FlashbackTrackRecent ? FlashbackTrackRange.RecentDays : FlashbackTrackRange.All,
                    FlashbackTrackDays = (double)Math.Clamp(FlashbackTrackDays, (decimal)GridSettings.MinFlashbackTrackDays, (decimal)GridSettings.MaxFlashbackTrackDays),
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
                Ai = AiModel.ToSettings(),
                Workspace = s.Workspace with
                {
                    AutosaveSeconds = Math.Clamp((int)AutosaveSeconds, 1, 10),
                    RestoreOnStartup = RestoreOnStartup,
                    SessionPerWorkspace = SessionPerWorkspace,
                },
                MetadataCache = s.MetadataCache with { RetentionDays = (int)CacheRetentionDays },
                SchemaBrowser = s.SchemaBrowser with { SingleClickPreview = SchemaSingleClickPreview },
                // 건너뛴 버전·마지막 확인 시각은 백그라운드 확인이 쓸 수 있어 지운 경우에만 덮어쓴다.
                Update = s.Update with
                {
                    CheckOnStartup = UpdateCheckOnStartup,
                    AutoDownload = UpdateAutoDownload,
                    FeedUrl = string.IsNullOrWhiteSpace(UpdateFeedUrl) ? null : UpdateFeedUrl.Trim(),
                    SkippedVersion = _skippedCleared ? null : s.Update.SkippedVersion,
                },
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (IsSaveError(ex) && profileId is not null)
        {
            // 프로필 복사본은 만들었는데 설정을 못 썼다: 이유를 보이고 창을 둔다(다시 저장하면 그 복사본을 이어 쓴다).
            SqlFormat.ErrorText = ex.Message;
            SelectedTab = FormatTabIndex;
            return false;
        }

        _pendingProfileId = null;
        PublishAiChanged();
        UpdateService.Current?.RefreshSettings(); // 건너뛴 버전을 지웠으면 상태 표시줄 표시도 바로 맞춘다
        Features.AcceptSaved();
        if (allowStayOpen && featuresChanged && Features.HasPendingRestart)
        {
            // 재시작해야 적용되는 기능이 있으면 안내 띠가 보이게 창을 둔다(닫기를 누르면 닫힌다).
            SelectedTab = FeaturesTabIndex;
            return true;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// 설정 저장소는 <c>ai</c> 섹션이 바뀌어도 <see cref="SettingsChanged"/> 를 내지 않는다. AI 튜닝 명령의 문맥 키가 따라오도록 AI 모델이 바뀌었으면 여기서 알린다.
    /// </summary>
    private void PublishAiChanged()
    {
        if (_settings.Current.Ai == _aiSaved)
        {
            return;
        }

        _aiSaved = _settings.Current.Ai;
        (Avalonia.Application.Current as App)?.Services?.GetService<IEventBus>()?.Publish(new SettingsChanged("ai"));
    }

    private static bool IsSaveError(Exception ex) =>
        ex is InvalidOperationException or ArgumentException or System.IO.IOException or UnauthorizedAccessException;

    [RelayCommand]
    public void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
