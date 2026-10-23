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

/// <summary>「바꿀 탭」 표의 한 줄. 포함·문장 id·종류를 고치면 부모가 겹침과 미리보기를 다시 계산한다.</summary>
public sealed partial class ConvertLineViewModel : ObservableObject
{
    /// <summary>종류 콤보에 보이는 글(<see cref="ConvertStatementKind"/> 순서 그대로).</summary>
    public static IReadOnlyList<string> KindLabels { get; } = ["select", "insert", "update", "delete", "CALLABLE"];

    private bool _include;
    private IReadOnlyList<ConvertNote> _convertNotes = [];
    private string _noteText;
    private string _suggestedId;

    [ObservableProperty]
    private string _id;

    [ObservableProperty]
    private int _kindIndex;

    [ObservableProperty]
    private string? _idError;

    internal ConvertLineViewModel(ConvertCandidate candidate)
    {
        Candidate = candidate;
        _id = candidate.SuggestedId;
        _suggestedId = candidate.SuggestedId;
        _kindIndex = candidate.Kind is { } k ? (int)k : -1;
        _include = candidate.Kind is not null && candidate.IncludeByDefault;
        _noteText = BuildNoteText(candidate, []);
    }

    public ConvertCandidate Candidate { get; }

    /// <summary>바꿀 줄에 넣는가. 종류 없는 줄은 켜지지 않는다.</summary>
    public bool Include
    {
        get => _include;
        set
        {
            if (SetProperty(ref _include, value && Kind is not null))
            {
                OnPropertyChanged(nameof(IsDimmed));
            }
        }
    }

    public string TabTitle => Candidate.TabTitle;

    /// <summary>종류가 없는 줄(바꿀 수 없는 문장)은 고를 수 없고 포함도 못 한다.</summary>
    public bool CanPickKind => Candidate.Kind is not null;

    public ConvertStatementKind? Kind => KindIndex < 0 ? null : (ConvertStatementKind)KindIndex;

    /// <summary>확인 열 글: 분석 단계 알림에 바꾸기 단계 알림(<see cref="SetConvertNotes"/>)을 합친 것.</summary>
    public string NoteText
    {
        get => _noteText;
        private set => SetProperty(ref _noteText, value);
    }

    public bool HasWarning => Candidate.Notes.Count > 0 || _convertNotes.Count > 0;

    public bool HasIdError => IdError is not null;

    /// <summary>제외한 줄은 확인 글을 흐리게 보인다.</summary>
    public bool IsDimmed => !Include;

    /// <summary>
    /// 이 줄을 바꾼 결과의 알림(<see cref="ConvertedStatement.Notes"/>)을 확인 열에 합친다. 포함하지 않은 줄·바꾸지 못한 줄은 빈 목록을 넘긴다.
    /// </summary>
    internal void SetConvertNotes(IReadOnlyList<ConvertNote> notes)
    {
        if (_convertNotes.SequenceEqual(notes))
        {
            return;
        }

        _convertNotes = notes;
        NoteText = BuildNoteText(Candidate, notes);
        OnPropertyChanged(nameof(HasWarning));
    }

    /// <summary>
    /// 새 제안 id 를 적용한다. 사용자가 손으로 고친 줄(현재 id 가 이전 제안과 다른 줄)은 id 를 그대로 둔다.
    /// </summary>
    internal void ApplySuggestedId(string suggested)
    {
        var untouched = Id == _suggestedId;
        _suggestedId = suggested;
        if (untouched)
        {
            Id = suggested;
        }
    }

    partial void OnKindIndexChanged(int value) => OnPropertyChanged(nameof(Kind));

    partial void OnIdErrorChanged(string? value) => OnPropertyChanged(nameof(HasIdError));

    private static string BuildNoteText(ConvertCandidate c, IReadOnlyList<ConvertNote> convertNotes)
    {
        var bind = ConvertText.Format("MbConvert.note.bind", "바인드 {0}", c.BindCount);
        var notes = c.Notes.Concat(convertNotes).Distinct().ToList();
        if (notes.Count == 0)
        {
            return bind + " · " + UiText.Get("MbConvert.note.ok", "문제 없음");
        }

        return bind + " · " + string.Join(" · ", notes.Select(NoteLabel));
    }

    /// <summary>알림 하나의 화면 글.</summary>
    internal static string NoteLabel(ConvertNote n)
    {
        var name = n.Name ?? string.Empty;
        return n.Kind switch
        {
            ConvertNoteKind.Substitution => ConvertText.Format("MbConvert.note.substitution", "치환 {0} 값 검증 필요", name),
            ConvertNoteKind.PositionalBind => ConvertText.Format("MbConvert.note.positional", "위치 바인드를 {0} 로 바꿈", name),
            ConvertNoteKind.RefCursorOut => ConvertText.Format("MbConvert.note.refCursor", "REF CURSOR OUT {0} 은 바꾸지 못함", name),
            ConvertNoteKind.WhereNotWrapped => UiText.Get("MbConvert.note.whereNotWrapped", "where 로 감싸지 못함"),
            ConvertNoteKind.PlSqlBlock => UiText.Get("MbConvert.note.plsql", "PL/SQL 블록이라 뺌 (종류를 CALLABLE 로 고르면 포함)"),
            ConvertNoteKind.Unsupported => UiText.Get("MbConvert.note.unsupported", "바꿀 수 없는 문장"),
            ConvertNoteKind.FormatSkipped => UiText.Get("MbConvert.note.formatSkipped", "서식을 적용하지 못함"),
            _ => string.Empty,
        };
    }
}

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

    /// <summary>
    /// 기존 매퍼 읽기를 시작한다(새 파일이거나 경로가 비면 비운다). 경로를 칠 때마다 읽지 않도록 디바운스하고, 읽기는 백그라운드에서 한다.
    /// 읽는 동안 이전 읽기는 낡은 것이 되어 결과를 버리고, 확인 단추는 꺼진다.
    /// </summary>
    private void LoadExisting()
    {
        var version = ++_readVersion; // 앞선 읽기는 낡은 것이 된다
        if (Target == ConvertTargetKind.NewFile || string.IsNullOrWhiteSpace(Path))
        {
            IsReadingExisting = false;
            ExistingError = null;
            if (Target == ConvertTargetKind.ExistingMapper)
            {
                Namespace = string.Empty;
            }

            _existingIds = new HashSet<string>(StringComparer.Ordinal);
            ResuggestIds();
            _readTask = Task.CompletedTask;
            return;
        }

        IsReadingExisting = true;
        _readTask = ReadExistingAsync(Path.Trim(), version);
    }

    private async Task ReadExistingAsync(string path, int version)
    {
        await Task.Delay(_readDelay).ConfigureAwait(true);
        if (version != _readVersion)
        {
            return; // 기다리는 동안 경로가 또 바뀌었다
        }

        var read = await Task.Run(() => SafeRead(path)).ConfigureAwait(true);

        // 읽는 동안 경로·대상이 바뀌었으면 낡은 결과라 버린다(UI 문맥으로 돌아온 뒤에 반영한다).
        if (version != _readVersion)
        {
            return;
        }

        _existingIds = new HashSet<string>(read.Error is null ? read.StatementIds : [], StringComparer.Ordinal);
        ExistingError = read.Error;
        Namespace = read.Namespace ?? string.Empty;
        IsReadingExisting = false;
        ResuggestIds();
        Recompute();
    }

    private ExistingMapperRead SafeRead(string path)
    {
        try
        {
            return _readExisting(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return new ExistingMapperRead(null, [], ex.Message);
        }
    }

    /// <summary>
    /// 기존 매퍼 id 집합이 바뀌었으면 분석을 다시 불러 제안 id 를 새로 받는다. 손으로 고친 줄은 그대로 둔다.
    /// </summary>
    private void ResuggestIds()
    {
        if (_suggestionBasis.SetEquals(_existingIds))
        {
            return;
        }

        _suggestionBasis = new HashSet<string>(_existingIds, StringComparer.Ordinal);
        var fresh = new Dictionary<(int Tab, int Statement), ConvertCandidate>();
        foreach (var c in _analyze(_tabs, _suggestionBasis))
        {
            fresh[(c.TabIndex, c.StatementIndex)] = c;
        }

        foreach (var line in Lines)
        {
            if (fresh.TryGetValue((line.Candidate.TabIndex, line.Candidate.StatementIndex), out var c))
            {
                line.ApplySuggestedId(c.SuggestedId);
            }
        }
    }

    /// <summary>
    /// 바꿀 수 있는 포함 줄을 바꿔 보고 그 알림(<see cref="ConvertedStatement.Notes"/>)을 확인 열에 합친다. 엔진 예외는 알림 없음으로 둔다.
    /// </summary>
    private void UpdateNotes(ConvertLineViewModel line)
    {
        if (!line.Include || line.Kind is null)
        {
            line.SetConvertNotes([]);
            return;
        }

        try
        {
            line.SetConvertNotes(ConvertLine(line, Options).Notes);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            line.SetConvertNotes([]);
        }
    }

    private void UpdateAllNotes()
    {
        foreach (var line in Lines)
        {
            UpdateNotes(line);
        }
    }

    /// <summary>id 오류(형식·기존 매퍼와 겹침·줄끼리 겹침)와 단추 상태를 다시 계산한다. 제외한 줄은 오류를 보이지 않는다.</summary>
    private void Recompute()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var l in Lines.Where(l => l.Include))
        {
            counts[l.Id] = counts.GetValueOrDefault(l.Id) + 1;
        }

        foreach (var l in Lines)
        {
            l.IdError = !l.Include ? null
                : !_isValidId(l.Id) ? UiText.Get("MbConvert.id.invalid", "문장 id 형식이 맞지 않습니다 (영문자·숫자·밑줄, 영문자로 시작)")
                : _existingIds.Contains(l.Id) ? UiText.Get("MbConvert.id.existing", "기존 매퍼에 같은 id 가 있습니다")
                : counts[l.Id] > 1 ? UiText.Get("MbConvert.id.duplicate", "다른 줄과 id 가 겹칩니다")
                : null;
        }

        OnPropertyChanged(nameof(IncludedCount));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(CanConfirm));
    }

    private void RefreshPreview()
    {
        OnPropertyChanged(nameof(PreviewTitle));
        OnPropertyChanged(nameof(FormatCaption));
        if (SelectedLine is not { Kind: not null } line)
        {
            PreviewXml = string.Empty;
            ScenarioText = string.Empty;
            return;
        }

        try
        {
            var statement = ConvertLine(line, Options);
            PreviewXml = statement.ElementText;
            ScenarioText = SaveScenario ? ScenarioSummary(statement.ScenarioValues) : UiText.Get("MbConvert.scenario.off", "시나리오로 저장하지 않습니다.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            PreviewXml = ex.Message;
            ScenarioText = string.Empty;
        }
    }

    private ConvertedStatement ConvertLine(ConvertLineViewModel line, ConvertOptions options)
    {
        var binds = line.Candidate.TabIndex >= 0 && line.Candidate.TabIndex < _tabs.Count ? _tabs[line.Candidate.TabIndex].Binds : [];
        return _convert(line.Candidate, line.Kind ?? line.Candidate.Kind ?? ConvertStatementKind.Select, line.Id, binds, options, _formatter);
    }

    /// <summary><c>이름 = 값 · …</c>. 값이 하나도 없으면 안내 글.</summary>
    internal static string ScenarioSummary(IReadOnlyDictionary<string, MyBatisScenarioValue> values)
    {
        if (values.Count == 0)
        {
            return UiText.Get("MbConvert.scenario.none", "바인드 값이 없습니다.");
        }

        return string.Join(" · ", values.Select(kv => kv.Key + " = " + ValueText(kv.Value)));
    }

    private static string ValueText(MyBatisScenarioValue v) => v.Kind switch
    {
        MyBatisScenarioValueKind.Null => "NULL",
        MyBatisScenarioValueKind.Text => "'" + v.Text + "'",
        MyBatisScenarioValueKind.List => "[" + string.Join(", ", (v.Items ?? []).Select(i => v.ItemKind == MyBatisScenarioValueKind.Text ? "'" + i + "'" : i)) + "]",
        _ => v.Text ?? string.Empty,
    };
}
