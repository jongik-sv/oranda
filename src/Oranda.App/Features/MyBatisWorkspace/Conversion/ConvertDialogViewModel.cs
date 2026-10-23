using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Localization;
using Oranda.App.ViewModels;
using Oranda.Core.Formatting;
using Oranda.Core.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Conversion;

// 「MyBatis 작업 공간으로 바꾸기」 대화 상자의 ViewModel(TSK-03-05, MB-08). 엔진·기존 매퍼 읽기는 대리자로 받아 단독으로 시험한다.
// 시작 시험 제약: 필드·생성자에 Language 의 형을 두지 않는다(Core·App 형만).

/// <summary>화면 글을 strings 키로 읽고 <c>{0}</c> 자리를 채운다.</summary>
internal static class ConvertText
{
    internal static string Format(string key, string fallback, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, UiText.Get(key, fallback), args);
}

/// <summary>저장할 곳.</summary>
public enum ConvertTargetKind
{
    NewFile,
    ExistingMapper,
}

/// <summary>기존 매퍼를 읽은 결과. <see cref="Error"/> 가 있으면 읽지 못한 것이다(화면에 그대로 보인다).</summary>
public sealed record ExistingMapperRead(string? Namespace, IReadOnlyList<string> StatementIds, string? Error = null);

/// <summary>확인 결과: 저장할 곳과 바꾼 문장(포함한 줄만, 표 순서).</summary>
public sealed record ConvertDialogResult(
    ConvertTargetKind Target,
    string Path,
    string Namespace,
    IReadOnlyList<ConvertedStatement> Statements,
    ConvertOptions Options);

/// <summary>
/// 바꾸기 대화 상자 모델. 대상(새 파일·기존 매퍼)·경로·namespace·표(줄)·옵션을 들고, 포함 수와 id 오류에 따라 확인 단추를 켜고 끈다.
/// </summary>
public sealed partial class ConvertDialogViewModel : ViewModelBase
{
    /// <summary>기존 매퍼 경로를 칠 때 읽기를 미루는 시간(글자마다 파일을 읽지 않는다).</summary>
    private static readonly TimeSpan DefaultReadDelay = TimeSpan.FromMilliseconds(300);

    private readonly Func<IReadOnlyList<ConvertSourceTab>, IReadOnlyCollection<string>, IReadOnlyList<ConvertCandidate>> _analyze;
    private readonly TimeSpan _readDelay;
    private readonly IReadOnlyList<ConvertSourceTab> _tabs;
    private readonly Func<string, ExistingMapperRead> _readExisting;
    private readonly Func<string, bool> _isValidId;
    private readonly Func<ConvertCandidate, ConvertStatementKind, string, IReadOnlyList<ConvertBind>, ConvertOptions, ISqlFormatter?, ConvertedStatement> _convert;
    private readonly ISqlFormatter? _formatter;
    private readonly FormatterOptions? _formatterOptions;
    private string _newFilePath;
    private string _newFileNamespace;
    private string _existingPath = string.Empty;
    private HashSet<string> _existingIds = new(StringComparer.Ordinal);
    private HashSet<string> _suggestionBasis = new(StringComparer.Ordinal);
    private int _readVersion;
    private Task _readTask = Task.CompletedTask;
    private bool _suspend;

    [ObservableProperty]
    private ConvertTargetKind _target = ConvertTargetKind.NewFile;

    [ObservableProperty]
    private string _path;

    [ObservableProperty]
    private string _namespace;

    [ObservableProperty]
    private string? _existingError;

    [ObservableProperty]
    private bool _isReadingExisting;

    [ObservableProperty]
    private ConvertLineViewModel? _selectedLine;

    [ObservableProperty]
    private bool _addJdbcType;

    [ObservableProperty]
    private bool _wrapWhere;

    [ObservableProperty]
    private bool _useCdata = true;

    [ObservableProperty]
    private bool _saveScenario = true;

    [ObservableProperty]
    private string _previewXml = string.Empty;

    [ObservableProperty]
    private string _scenarioText = string.Empty;

    [ObservableProperty]
    private string? _convertError;

    /// <param name="workspaceFileName">원래 작업 공간 파일 이름(머리 글).</param>
    /// <param name="tabs">바꿀 SQL 탭(바인드 포함).</param>
    /// <param name="suggestedNewPath">새 파일 경로 제안.</param>
    /// <param name="suggestedNamespace">새 파일 namespace 제안.</param>
    /// <param name="readExisting">기존 매퍼 경로 → namespace·문장 id(실패하면 <see cref="ExistingMapperRead.Error"/>).</param>
    /// <param name="analyze"><c>ConvertEngine.Analyze</c>.</param>
    /// <param name="convert"><c>ConvertEngine.Convert</c>.</param>
    /// <param name="isValidId"><c>ConvertEngine.IsValidId</c>.</param>
    /// <param name="formatter">서식 적용기(없으면 원래 글).</param>
    /// <param name="formatterOptions">기본 포매터 프로필 옵션(없으면 원래 글).</param>
    /// <param name="existingReadDelay">기존 매퍼 경로를 고친 뒤 읽기까지 기다리는 시간(없으면 300 ms).</param>
    public ConvertDialogViewModel(
        string workspaceFileName,
        IReadOnlyList<ConvertSourceTab> tabs,
        string suggestedNewPath,
        string suggestedNamespace,
        Func<string, ExistingMapperRead> readExisting,
        Func<IReadOnlyList<ConvertSourceTab>, IReadOnlyCollection<string>, IReadOnlyList<ConvertCandidate>> analyze,
        Func<ConvertCandidate, ConvertStatementKind, string, IReadOnlyList<ConvertBind>, ConvertOptions, ISqlFormatter?, ConvertedStatement> convert,
        Func<string, bool> isValidId,
        ISqlFormatter? formatter = null,
        FormatterOptions? formatterOptions = null,
        TimeSpan? existingReadDelay = null)
    {
        ArgumentNullException.ThrowIfNull(workspaceFileName);
        ArgumentNullException.ThrowIfNull(tabs);
        ArgumentNullException.ThrowIfNull(readExisting);
        ArgumentNullException.ThrowIfNull(analyze);
        ArgumentNullException.ThrowIfNull(convert);
        ArgumentNullException.ThrowIfNull(isValidId);
        _tabs = tabs;
        _readExisting = readExisting;
        _analyze = analyze;
        _readDelay = existingReadDelay ?? DefaultReadDelay;
        _convert = convert;
        _isValidId = isValidId;
        _formatter = formatter;
        _formatterOptions = formatterOptions;
        _newFilePath = suggestedNewPath ?? string.Empty;
        _newFileNamespace = suggestedNamespace ?? string.Empty;
        _path = _newFilePath;
        _namespace = _newFileNamespace;
        HeaderText = ConvertText.Format("MbConvert.header", "{0} · SQL 탭 {1}개를 매퍼 xml 문장으로 바꿉니다", workspaceFileName, tabs.Count);

        Lines = [.. analyze(tabs, new HashSet<string>(StringComparer.Ordinal)).Select(c => new ConvertLineViewModel(c))];
        foreach (var line in Lines)
        {
            line.PropertyChanged += OnLineChanged;
        }

        Recompute();
        UpdateAllNotes();
        SelectedLine = Lines.FirstOrDefault(l => l.Include) ?? (Lines.Count > 0 ? Lines[0] : null);
    }

    public string HeaderText { get; }

    public IReadOnlyList<ConvertLineViewModel> Lines { get; }

    public bool IsNewFile
    {
        get => Target == ConvertTargetKind.NewFile;
        set
        {
            if (value)
            {
                Target = ConvertTargetKind.NewFile;
            }
        }
    }

    public bool IsExistingMapper
    {
        get => Target == ConvertTargetKind.ExistingMapper;
        set
        {
            if (value)
            {
                Target = ConvertTargetKind.ExistingMapper;
            }
        }
    }

    /// <summary>기존 매퍼면 namespace 는 문서 값이라 고칠 수 없다.</summary>
    public bool IsNamespaceEditable => Target == ConvertTargetKind.NewFile;

    public bool HasExistingError => ExistingError is not null;

    /// <summary>마지막 기존 매퍼 읽기. 시험이 기다릴 수 있게 내보인다(읽는 중에 경로가 또 바뀌면 새 읽기로 바뀐다).</summary>
    internal Task ExistingReadTask => _readTask;

    /// <summary>새 파일일 때 namespace 가 비었거나 점으로 이은 Java 식별자가 아니면 그 이유. 기존 매퍼는 문서 값이라 검사하지 않는다.</summary>
    public string? NamespaceError
    {
        get
        {
            if (Target != ConvertTargetKind.NewFile)
            {
                return null;
            }

            var ns = Namespace.Trim();
            if (ns.Length == 0)
            {
                return UiText.Get("MbConvert.namespace.empty", "namespace 를 입력하십시오 (MyBatis 는 빈 namespace 를 받지 않습니다)");
            }

            return NamespacePattern().IsMatch(ns)
                ? null
                : UiText.Get("MbConvert.namespace.invalid", "namespace 는 점으로 이은 Java 식별자여야 합니다 (예: com.acme.OrderMapper)");
        }
    }

    public bool HasNamespaceError => NamespaceError is not null;

    [GeneratedRegex(@"^[A-Za-z_$][A-Za-z0-9_$]*(\.[A-Za-z_$][A-Za-z0-9_$]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamespacePattern();

    /// <summary>포함한 줄 수.</summary>
    public int IncludedCount => Lines.Count(l => l.Include);

    public string ConfirmText => ConvertText.Format("MbConvert.confirm", "문장 {0}개 바꾸고 열기", IncludedCount);

    /// <summary>포함 0 · id 오류 · 경로 비어 있음 · namespace 오류 · 기존 매퍼를 읽는 중이거나 읽지 못함이면 거짓.</summary>
    public bool CanConfirm =>
        IncludedCount > 0
        && !Lines.Any(l => l.Include && l.IdError is not null)
        && !string.IsNullOrWhiteSpace(Path)
        && NamespaceError is null
        && !IsReadingExisting
        && (Target == ConvertTargetKind.NewFile || ExistingError is null);

    public string PreviewTitle => SelectedLine is { } l
        ? ConvertText.Format("MbConvert.preview.title", "미리보기 · {0}", l.Id)
        : UiText.Get("MbConvert.preview.titleNone", "미리보기");

    public string FormatCaption => _formatter is not null && _formatterOptions is not null
        ? UiText.Get("MbConvert.preview.formatDefault", "줄 바꿈은 기본 서식")
        : UiText.Get("MbConvert.preview.formatNone", "서식 없이 원래 글");

    /// <summary>바닥 안내 한 줄. 바꾸다 실패했으면 그 오류가 대신 보인다.</summary>
    public string FooterText => ConvertError ?? UiText.Get("MbConvert.footer", "원래 작업 공간 파일은 그대로 둡니다. 바꾸면 새 MyBatis 문서 탭이 열립니다.");

    /// <summary>지금 옵션 체크.</summary>
    public ConvertOptions Options => new()
    {
        AddJdbcType = AddJdbcType,
        WrapWhere = WrapWhere,
        UseCdata = UseCdata,
        SaveScenario = SaveScenario,
        Format = _formatter is null ? null : _formatterOptions,
    };

    /// <summary>
    /// 포함한 줄을 모두 바꿔 결과를 만든다. 엔진이 실패하면 오류 글을 남기고 null(창은 열어 둔다).
    /// </summary>
    public ConvertDialogResult? TryBuildResult()
    {
        if (!CanConfirm)
        {
            return null;
        }

        var options = Options;
        var statements = new List<ConvertedStatement>();
        try
        {
            foreach (var line in Lines.Where(l => l.Include))
            {
                statements.Add(ConvertLine(line, options));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            ConvertError = ex.Message;
            return null;
        }

        ConvertError = null;
        return new ConvertDialogResult(Target, Path.Trim(), Namespace.Trim(), statements, options);
    }

    partial void OnTargetChanged(ConvertTargetKind value)
    {
        // 대상마다 경로·namespace 를 따로 기억한다(새 파일로 돌아오면 입력해 둔 값이 그대로다).
        _suspend = true;
        if (value == ConvertTargetKind.ExistingMapper)
        {
            _newFilePath = Path;
            _newFileNamespace = Namespace;
            Path = _existingPath;
        }
        else
        {
            _existingPath = Path;
            Path = _newFilePath;
            Namespace = _newFileNamespace;
        }

        _suspend = false;
        OnPropertyChanged(nameof(IsNewFile));
        OnPropertyChanged(nameof(IsExistingMapper));
        OnPropertyChanged(nameof(IsNamespaceEditable));
        OnPropertyChanged(nameof(NamespaceError));
        OnPropertyChanged(nameof(HasNamespaceError));
        LoadExisting();
        Recompute();
    }

    partial void OnPathChanged(string value)
    {
        if (_suspend)
        {
            return;
        }

        if (Target == ConvertTargetKind.ExistingMapper)
        {
            LoadExisting();
        }

        Recompute();
    }

    partial void OnExistingErrorChanged(string? value) => OnPropertyChanged(nameof(HasExistingError));

    partial void OnIsReadingExistingChanged(bool value) => OnPropertyChanged(nameof(CanConfirm));

    partial void OnNamespaceChanged(string value)
    {
        OnPropertyChanged(nameof(NamespaceError));
        OnPropertyChanged(nameof(HasNamespaceError));
        OnPropertyChanged(nameof(CanConfirm));
    }

    partial void OnSelectedLineChanged(ConvertLineViewModel? value) => RefreshPreview();

    partial void OnAddJdbcTypeChanged(bool value) => OnOptionChanged();

    partial void OnWrapWhereChanged(bool value) => OnOptionChanged();

    partial void OnUseCdataChanged(bool value) => OnOptionChanged();

    partial void OnSaveScenarioChanged(bool value) => OnOptionChanged();

    // 옵션이 바뀌면 미리보기와 함께 포함한 줄의 바꾸기 알림도 다시 계산한다.
    private void OnOptionChanged()
    {
        UpdateAllNotes();
        RefreshPreview();
    }

    partial void OnConvertErrorChanged(string? value) => OnPropertyChanged(nameof(FooterText));

    private void OnLineChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConvertLineViewModel.Include) or nameof(ConvertLineViewModel.Id))
        {
            Recompute();
        }

        if (sender is ConvertLineViewModel changed
            && e.PropertyName is nameof(ConvertLineViewModel.Include) or nameof(ConvertLineViewModel.Id) or nameof(ConvertLineViewModel.KindIndex))
        {
            UpdateNotes(changed);
        }

        if (sender == SelectedLine && e.PropertyName is nameof(ConvertLineViewModel.Id) or nameof(ConvertLineViewModel.KindIndex))
        {
            RefreshPreview();
        }
    }
}
