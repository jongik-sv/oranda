using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.ObjectDetail;

/// <summary>구역 줄을 고르면 열을 따로 읽는 키 종류(TSK-13-34).</summary>
public enum DetailKeyKind
{
    None,
    Index,
    Constraint,
}

/// <summary>상세 탭 하나(열·제약·인덱스 …). 화면은 격자 표(<see cref="Oranda.App.Shell.Controls.DetailTable"/>), <see cref="Text"/> 는 글자 맞춤 텍스트(복사·DB 정보용).</summary>
public sealed class ObjectDetailSection : ObservableObject
{
    private IReadOnlyList<IReadOnlyList<object?>> _rows;
    private IReadOnlyList<string> _columns;
    private int _editCount;
    private string _notice = string.Empty;

    public ObjectDetailSection(DetailSection source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Title = source.Title;
        _columns = source.Columns;
        _rows = source.Rows;
        IsLoaded = true;
        KeyKind = source.Title switch
        {
            "인덱스" => DetailKeyKind.Index,
            "제약" => DetailKeyKind.Constraint,
            _ => DetailKeyKind.None,
        };
    }

    public string Title { get; }

    /// <summary>테이블 편집 중 이 페이지의 바뀐 건수(설계 7.1). 0 이면 이름만 보인다.</summary>
    public int EditCount
    {
        get => _editCount;
        set
        {
            if (SetProperty(ref _editCount, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    /// <summary>구역 목록에 보일 이름(편집 중 바뀐 건수가 있으면 「열 (2)」).</summary>
    public string DisplayTitle => EditCount > 0 ? $"{Title} ({EditCount})" : Title;

    /// <summary>인덱스·제약 구역이면 그 종류(줄의 첫 칸 = 키 이름).</summary>
    public DetailKeyKind KeyKind { get; }

    /// <summary>표에 자체 「#」 칸(열 위치 등)이 첫 칸으로 있다 — 표의 행 머리에 번호를 겹쳐 쓰지 않는다.</summary>
    public bool HasNumberColumn => Columns.Count > 0 && Columns[0] == "#";

    /// <summary>「데이터」 탭(표 대신 데이터 그리드를 보인다, <see cref="ObjectDataViewModel"/>).</summary>
    public bool IsData { get; private init; }

    /// <summary>고를 수 있는 페이지. 거짓 = 흐림(새 테이블 탭의 데이터·통계·크기, 설계 7.5).</summary>
    public bool IsAvailable { get; init; } = true;

    public IReadOnlyList<string> Columns
    {
        get => _columns;
        private set
        {
            if (SetProperty(ref _columns, value))
            {
                OnPropertyChanged(nameof(HasNumberColumn));
                OnPropertyChanged(nameof(Text));
            }
        }
    }

    /// <summary>이 탭을 읽을 때 공급자에 넘길 구역 종류(DL-1). 이미 읽힌 채로 온 탭은 <see cref="DetailPart.Whole"/>·<see cref="DetailPart.Table"/>.</summary>
    public DetailPart Part { get; private init; } = DetailPart.Whole;

    /// <summary>내용을 읽었다(행이 없어 「없음」인 것 포함). 거짓이면 고를 때 <see cref="Part"/> 를 읽는다.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>표 대신 보일 안내(읽는 중·없음·읽지 못함). 빈 글이면 표를 보인다.</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value))
            {
                OnPropertyChanged(nameof(HasNotice));
            }
        }
    }

    public bool HasNotice => Notice.Length > 0;

    public IReadOnlyList<IReadOnlyList<object?>> Rows
    {
        get => _rows;
        private set
        {
            if (SetProperty(ref _rows, value))
            {
                OnPropertyChanged(nameof(Text));
            }
        }
    }

    /// <summary>열 너비를 맞춘 표 텍스트(첫 줄 = 머리).</summary>
    public string Text => Format(new DetailSection(Title, Columns, Rows));

    /// <summary>첫 칸이 <paramref name="key"/> 인 줄의 <paramref name="column"/> 칸을 바꾼다(인덱스 열을 읽은 뒤 식·DESC 로 바꿔 보일 때). 바뀌면 true.</summary>
    internal bool ReplaceCell(string key, string column, object? value)
    {
        var c = Columns.ToList().IndexOf(column);
        var r = Rows.ToList().FindIndex(row => row.Count > 0 && string.Equals(row[0] as string, key, StringComparison.Ordinal));
        if (c < 0 || r < 0 || c >= Rows[r].Count || Equals(Rows[r][c], value))
        {
            return false;
        }

        var rows = Rows.ToList();
        var row = rows[r].ToArray();
        row[c] = value;
        rows[r] = row;
        Rows = rows;
        return true;
    }

    /// <summary>아직 읽지 않은 탭 머리(DL-1): 고르면 <paramref name="part"/> 를 읽는다.</summary>
    internal static ObjectDetailSection CreatePending(string title, DetailPart part) =>
        new(new DetailSection(title, [], [])) { Part = part, IsLoaded = false };

    /// <summary>읽은 내용을 채운다. <paramref name="source"/> 가 null 이면 행이 없다(「없음」 안내).</summary>
    internal void Load(DetailSection? source)
    {
        if (source is null)
        {
            Columns = [];
            Rows = [];
            Notice = LiteralTranslator.IsEnglish ? "None" : "없음";
        }
        else
        {
            Columns = source.Columns;
            Rows = source.Rows;
            Notice = string.Empty;
        }

        IsLoaded = true;
    }

    /// <summary>표 대신 안내만 바꾼다(읽는 중·읽지 못함). 내용은 읽지 않은 채로 둔다.</summary>
    internal void ShowNotice(string notice) => Notice = notice;

    /// <summary>읽은 내용을 낡은 것으로 둔다(통계 수집 뒤): 내용은 그대로 보이고, 다음에 고를 때 다시 읽는다.</summary>
    internal void Forget()
    {
        if (Part is not (DetailPart.Whole or DetailPart.Table))
        {
            IsLoaded = false;
        }
    }

    /// <summary>「데이터」 탭 항목(표 내용 없음).</summary>
    internal static ObjectDetailSection CreateData() =>
        new(new DetailSection(ObjectDataViewModel.SectionTitle, [], [])) { IsData = true };

    internal static string Cell(object? v) => v switch
    {
        null => string.Empty,
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString()?.ReplaceLineEndings(" ") ?? string.Empty,
    };

    private static string Format(DetailSection s)
    {
        var table = s.Rows.Select(r => r.Select(Cell).ToArray()).ToList();
        var widths = s.Columns.Select((c, i) => Math.Max(c.Length, table.Count == 0 ? 0 : table.Max(r => i < r.Length ? r[i].Length : 0))).ToArray();
        var sb = new StringBuilder();

        void Line(IReadOnlyList<string> cells)
        {
            for (var i = 0; i < widths.Length; i++)
            {
                var text = i < cells.Count ? cells[i] : string.Empty;
                sb.Append(text).Append(' ', widths[i] - text.Length);
                if (i < widths.Length - 1)
                {
                    sb.Append("  ");
                }
            }

            sb.AppendLine();
        }

        Line(s.Columns);
        Line([.. widths.Select(w => new string('-', w))]);
        foreach (var r in table)
        {
            Line(r);
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// <c>oranda.panel.objectDetail</c> — 객체 상세 탭(열·제약·인덱스·파티션·권한·의존·통계·크기)과 DDL 보기(TSK-07-03),
/// 표·뷰의 「데이터」 탭(<see cref="ObjectDataViewModel"/>).
/// 상세는 <see cref="IMetadataProvider"/> 만 쓴다. 서비스는 처음 쓸 때 꺼낸다(시작 경로에서 Oracle 어셈블리를 올리지 않기 위해).
/// </summary>
[OrandaToolPanel("oranda.panel.objectDetail", ToolPanelKind.Document, DockLocation.Document, AllowMultiple = true)]
public sealed partial class ObjectDetailViewModel : ToolPanelViewModelBase
{
    private readonly IServiceProvider? _services;
    private readonly Dictionary<(DetailKeyKind Kind, string Name), (ObjectDetailSection Table, string Status)> _keyCache = [];
    private IMetadataProvider? _meta;
    private ITableKeyDetailProvider? _keys;
    private bool _keysResolved;
    private CancellationTokenSource? _keyCts;
    private int _keySerial;
    private ISession? _session;
    private ObjectDataViewModel? _data;
    private int _generation;
    private bool _opening;
    private CancellationTokenSource? _detailCts;
    private readonly Dictionary<DetailPart, Task> _partLoads = [];
    private bool _ddlLoaded;

    public ObjectDetailViewModel()
    {
    }

    public ObjectDetailViewModel(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>시험용: 제공자를 직접 넣는다.</summary>
    internal ObjectDetailViewModel(IMetadataProvider meta) => _meta = meta;

    /// <summary>시험용: 제공자 둘을 직접 넣는다(<paramref name="keys"/> 가 null 이면 인덱스·제약 열 표가 없다).</summary>
    internal ObjectDetailViewModel(IMetadataProvider meta, ITableKeyDetailProvider? keys)
    {
        KeyReadDelay = TimeSpan.Zero;
        _meta = meta;
        _keys = keys;
        _keysResolved = true;
    }

    public DbObjectRef? Target { get; private set; }

    /// <summary>이 문서가 읽는 세션(같은 객체를 다시 열 때 접속이 같은지 본다). 열기 전에는 null.</summary>
    public ISession? Session => _session;

    public ObservableCollection<ObjectDetailSection> Sections { get; } = [];

    /// <summary>
    /// 「데이터」 탭(표·뷰·머티리얼라이즈드 뷰, 구역 목록의 「열」 바로 뒤). 상세를 열 때는 읽지 않고 그 탭을 처음 고를 때 읽는다.
    /// 실행 쪽은 <see cref="ObjectDetailCommands.DataRunner"/>(상세 문서의 세션), 편집은 서비스의 <see cref="IRowEditor"/>.
    /// </summary>
    public ObjectDataViewModel Data => _data ??= new ObjectDataViewModel(
        s => DataRunner is { } run ? run(s) : ObjectDetailCommands.DataRunner?.Invoke(s),
        () => RowEditor ?? _services?.GetService(typeof(IRowEditor)) as IRowEditor,
        ResolveMeta);

    /// <summary>시험용: 「데이터」 탭의 실행 쪽을 바꾼다(기본 = <see cref="ObjectDetailCommands.DataRunner"/>).</summary>
    internal Func<ISession, ObjectDataRunner?>? DataRunner { get; set; }

    /// <summary>시험용: 「데이터」 탭의 행 편집기를 바꾼다(기본 = 서비스의 <see cref="IRowEditor"/>).</summary>
    internal IRowEditor? RowEditor { get; set; }

    /// <summary>열 순서 바꾸기 대화상자 등 이 문서에서 다른 화면을 여는 쪽이 서비스를 꺼내는 곳.</summary>
    internal IServiceProvider? Services => _services;

    /// <summary>true 면 「열」 탭이 선택돼 있고 대상이 표다(「열 순서 바꾸기…」 단추가 보인다).</summary>
    [ObservableProperty]
    private bool _canReorderColumns;

    [ObservableProperty]
    private string _title = "객체 상세";

    [ObservableProperty]
    private ObjectDetailSection? _selectedSection;

    /// <summary>true 면 인덱스·제약 구역이 선택돼 있고 열을 읽을 제공자가 있다(아래에 열 표가 보인다).</summary>
    [ObservableProperty]
    private bool _hasKeyColumns;

    /// <summary>고른 인덱스·제약의 열 표(인덱스: #·열/식·정렬, 제약: #·열·참조 표·참조 열). 줄을 고르기 전·읽는 중에는 null.</summary>
    [ObservableProperty]
    private ObjectDetailSection? _keyColumns;

    /// <summary>줄을 고른 뒤 열을 읽기 전 기다리는 시간(빠른 방향키 이동 때 줄마다 묻지 않게). 시험 생성자는 0.</summary>
    internal TimeSpan KeyReadDelay { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>열 표 위 한 줄(무엇의 열인지·읽는 중·권한 없음·오류).</summary>
    [ObservableProperty]
    private string _keyStatus = string.Empty;

    /// <summary>true 면 「데이터」 탭이 선택돼 있다(오른쪽에 표 대신 데이터 그리드).</summary>
    [ObservableProperty]
    private bool _isDataSelected;

    /// <summary>이 객체를 만든 소스 객체(JAVA CLASS → 같은 스키마의 JAVA SOURCE). 있으면 「소스 열기」 단추가 보인다(TSK-14-22).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    private DbObjectRef? _sourceTarget;

    public bool HasSource => SourceTarget is not null;

    /// <summary>시험용: 「소스 열기」가 문서를 여는 방법(기본 = 스키마 브라우저와 같은 <see cref="SchemaBrowser.SchemaObjectOpener"/>).</summary>
    internal Func<DbObjectRef, ISession, Task>? SourceOpener { get; set; }

    /// <summary>「소스 열기」: <see cref="SourceTarget"/> 을 고정 문서로 연다(JAVA SOURCE 는 DDL 탭에 소스 글). 연 것이 있으면 true.</summary>
    public async Task<bool> OpenSourceAsync(CancellationToken ct = default)
    {
        if (SourceTarget is not { } source || _session is not { } session)
        {
            return false;
        }

        if (SourceOpener is { } open)
        {
            await open(source, session);
            return true;
        }

        return await new SchemaBrowser.SchemaObjectOpener(_services).OpenAsync(source, session, preview: false, focus: true, ct) is not null;
    }

    /// <summary>true 면 DDL 탭이 선택돼 있다.</summary>
    [ObservableProperty]
    private bool _isDdlSelected;

    [ObservableProperty]
    private string _ddlText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    // DDL 옵션(SCH-04). 바뀌면 DDL 을 다시 만든다.
    [ObservableProperty]
    private bool _includeStorage;

    [ObservableProperty]
    private bool _includeSegmentAttributes;

    [ObservableProperty]
    private bool _includeTablespace;

    [ObservableProperty]
    private bool _sqlTerminator = true;

    [ObservableProperty]
    private bool _includeDependentObjects;

    public DdlOptions CurrentOptions => new()
    {
        IncludeStorage = IncludeStorage,
        IncludeSegmentAttributes = IncludeSegmentAttributes,
        IncludeTablespace = IncludeTablespace,
        SqlTerminator = SqlTerminator,
        IncludeDependentObjects = IncludeDependentObjects,
    };

    private string? DdlError { get; set; }

    partial void OnIncludeStorageChanged(bool value) => ReloadDdlOnOptionChange();

    partial void OnIncludeSegmentAttributesChanged(bool value) => ReloadDdlOnOptionChange();

    partial void OnIncludeTablespaceChanged(bool value) => ReloadDdlOnOptionChange();

    partial void OnSqlTerminatorChanged(bool value) => ReloadDdlOnOptionChange();

    partial void OnIncludeDependentObjectsChanged(bool value) => ReloadDdlOnOptionChange();

    partial void OnSelectedSectionChanged(ObjectDetailSection? value)
    {
        UpdateCanReorderColumns();
        IsDataSelected = value is { IsData: true };
        RaiseTableEditProperties();
        ResetKeyColumns(value);
        if (IsDataSelected)
        {
            _ = Data.EnsureLoadedAsync(); // 처음 고를 때만 읽는다(실패는 데이터 탭 안내에)
        }
        else if (!_opening && value is { IsLoaded: false })
        {
            SectionLoad = EnsureSectionLoadedAsync(value, _generation); // 처음 고를 때만 읽는다(DL-1)
        }
    }

    partial void OnIsDdlSelectedChanged(bool value)
    {
        if (value && !_opening && !_ddlLoaded && !IsNewTableMode && Target is not null && _session is not null)
        {
            DdlLoad = LoadDdlAsync(); // DDL 은 그 탭을 처음 고를 때 읽는다(DL-1)
        }
    }

    /// <summary>시험용: 마지막으로 시작한 탭 읽기(고른 탭이 이미 읽혔으면 바뀌지 않는다).</summary>
    internal Task? SectionLoad { get; private set; }

    /// <summary>시험용: 마지막으로 시작한 DDL 읽기.</summary>
    internal Task? DdlLoad { get; private set; }

    private void ReloadDdlOnOptionChange()
    {
        if (_opening || Target is null || _session is null)
        {
            return;
        }

        if (IsDdlSelected)
        {
            DdlLoad = LoadDdlAsync();
        }
        else
        {
            _ddlLoaded = false; // DDL 탭을 고를 때 새 옵션으로 읽는다
        }
    }

    /// <summary>
    /// 객체를 열어 탭 머리를 곧바로 보이고, 선택된 탭(기본 「열」, DDL 로 열면 DDL)만 먼저 읽는다(DL-1). 다른 탭·DDL 은 고를 때 한 번 읽는다.
    /// 어느 한쪽이 실패해도 다른 쪽은 보여 준다. 다시 열면(새로 고침 포함) 읽은 탭을 잊고 앞의 읽기는 취소한다.
    /// </summary>
    public async Task OpenAsync(ISession session, DbObjectRef target, bool showDdl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(target);

        WatchFeatureGate();
        EndTableEdit(); // 다시 열면 편집은 꺼진다(묻는 일은 부르는 쪽: 새로 고침·실행 성공 뒤, 설계 7.1)
        IsNewTableMode = false; // 있는 객체를 열면 새 테이블 탭이 아니다(설계 7.5)
        var gen = Interlocked.Increment(ref _generation);
        _detailCts?.Cancel(); // 앞 객체의 탭·DDL 읽기를 멈춘다(버리지 않는다: 읽는 쪽이 아직 토큰을 볼 수 있다)
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _detailCts = cts;
        _partLoads.Clear();
        _ddlLoaded = false;
        _session = session;
        Target = target;
        Title = $"{target.Owner}.{target.Name}";
        _opening = true;
        try
        {
            // JAVA SOURCE 는 소스 글이 본문이라 DDL 탭(소스 글)을 먼저 보인다.
            IsDdlSelected = showDdl || target.ObjectType == "JAVA SOURCE";
            SourceTarget = null;
            Sections.Clear();
            SelectedSection = null;
            DdlText = string.Empty;
            DdlError = null;
            Data.Reset(session, target);
            _keyCache.Clear();
            ResetKeyColumns(null);

            var meta = ResolveMeta();
            if (meta is null)
            {
                StatusText = "메타데이터 제공자가 없다";
                return;
            }

            IsBusy = true;
            string? error = null;
            await UpdateJavaStateAsync(meta, session, cts.Token); // Java 소스·클래스면 「다시 컴파일」을 쓸 수 있나(TSK-14-23)
            try
            {
                var outline = await meta.GetDetailOutlineAsync(session, target, cts.Token);
                if (gen != _generation)
                {
                    return;
                }

                foreach (var tab in outline.Tabs)
                {
                    Sections.Add(tab.Section is { } loaded ? new ObjectDetailSection(loaded) : ObjectDetailSection.CreatePending(tab.Title, tab.Part));
                }

                SourceTarget = outline.SourceObject;

                // 「데이터」 탭은 「열」 바로 뒤(SQL Developer 테이블 편집기의 Columns → Data 순서). 고르기 전에는 읽지 않는다.
                if (ObjectDataViewModel.Supports(target))
                {
                    var columns = Sections.FirstOrDefault(s => s.Title == "열");
                    Sections.Insert(columns is null ? Sections.Count : Sections.IndexOf(columns) + 1, ObjectDetailSection.CreateData());
                }

                SelectedSection = Sections.FirstOrDefault(s => s.Title == "열") ?? Sections.FirstOrDefault(s => !s.IsData);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = "상세를 읽지 못했다: " + ex.Message;
            }

            _opening = false;
            if (IsDdlSelected)
            {
                await LoadDdlCoreAsync(meta, gen, cts.Token);
            }
            else if (SelectedSection is { IsLoaded: false } first)
            {
                SectionLoad = EnsureSectionLoadedAsync(first, gen);
                await SectionLoad;
            }

            if (gen == _generation)
            {
                StatusText = error ?? (IsDdlSelected ? DdlError : null) ?? $"{target.ObjectType} {Title}";
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (gen == _generation)
            {
                _opening = false;
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// 탭 하나를 읽어 채운다(DL-1). 같은 구역 종류(<see cref="ObjectDetailSection.Part"/>)의 탭은 한 번에 채우고, 읽는 동안 그 탭들에 「읽는 중」을 보인다.
    /// 다른 객체를 열면 결과를 버린다. 실패하면 이유를 보이고 다시 고를 때 다시 읽는다.
    /// </summary>
    private Task EnsureSectionLoadedAsync(ObjectDetailSection section, int gen)
    {
        if (section.IsLoaded || _session is not { } session || Target is not { } target || ResolveMeta() is not { } meta || _detailCts is not { } cts)
        {
            return Task.CompletedTask;
        }

        if (_partLoads.TryGetValue(section.Part, out var running))
        {
            return running;
        }

        var task = LoadPartAsync(meta, session, target, section.Part, gen, cts.Token);
        if (!task.IsCompleted)
        {
            _partLoads[section.Part] = task; // 읽는 동안 같은 구역을 또 고르면 같은 읽기를 기다린다(끝난 것은 탭의 IsLoaded 로 안다)
        }

        return task;
    }

    private async Task LoadPartAsync(IMetadataProvider meta, ISession session, DbObjectRef target, DetailPart part, int gen, CancellationToken ct)
    {
        var tabs = Sections.Where(s => s.Part == part && !s.IsLoaded && !s.IsData).ToList();
        foreach (var tab in tabs)
        {
            tab.ShowNotice(LiteralTranslator.IsEnglish ? "Reading…" : "읽는 중…");
        }

        try
        {
            var sections = await meta.GetDetailPartAsync(session, target, part, ct);
            if (gen != _generation)
            {
                return;
            }

            foreach (var tab in tabs)
            {
                tab.Load(sections.FirstOrDefault(x => string.Equals(x.Title, tab.Title, StringComparison.Ordinal)));
            }
        }
        catch (OperationCanceledException)
        {
            if (gen == _generation)
            {
                _partLoads.Remove(part);
                foreach (var tab in tabs)
                {
                    tab.ShowNotice(string.Empty);
                }
            }
        }
        catch (Exception ex)
        {
            if (gen == _generation)
            {
                _partLoads.Remove(part); // 다시 고르면 다시 읽는다
                foreach (var tab in tabs)
                {
                    tab.ShowNotice((LiteralTranslator.IsEnglish ? "Could not read: " : "읽지 못했다: ") + ex.Message);
                }
            }
        }
    }

    /// <summary>통계를 보이는 구역(통계 수집이 바꾸는 값: 행 수·마지막 분석·파티션 행 수·크기).</summary>
    internal static readonly DetailPart[] StatsParts = [DetailPart.Stats, DetailPart.Partitions, DetailPart.Size];

    /// <summary>
    /// 이 객체의 통계가 바뀌었다(통계 수집 뒤): 통계·파티션·크기 탭을 낡은 것으로 두고, 그중 지금 보는 탭이면 바로 다시 읽는다.
    /// 다른 탭은 고를 때 읽는다. 열·편집 상태·데이터 탭은 건드리지 않는다. 새 테이블 탭이면 할 일이 없다.
    /// </summary>
    public Task ReloadStatsAsync()
    {
        if (IsNewTableMode || _session is null || Target is null)
        {
            return Task.CompletedTask;
        }

        foreach (var part in StatsParts)
        {
            _partLoads.Remove(part);
        }

        foreach (var section in Sections.Where(s => StatsParts.Contains(s.Part)))
        {
            section.Forget();
        }

        if (!IsDdlSelected && SelectedSection is { IsLoaded: false } selected && StatsParts.Contains(selected.Part))
        {
            SectionLoad = EnsureSectionLoadedAsync(selected, _generation);
            return SectionLoad;
        }

        return Task.CompletedTask;
    }

    /// <summary>옵션이 바뀐 뒤 DDL 만 다시 읽는다.</summary>
    public async Task LoadDdlAsync(CancellationToken ct = default)
    {
        if (ResolveMeta() is not { } meta)
        {
            return;
        }

        var gen = _generation;
        IsBusy = true;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _detailCts?.Token ?? CancellationToken.None);
            await LoadDdlCoreAsync(meta, gen, linked.Token);
            if (gen == _generation && DdlError is not null)
            {
                StatusText = DdlError;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (gen == _generation)
            {
                IsBusy = false;
            }
        }
    }

    private async Task LoadDdlCoreAsync(IMetadataProvider meta, int gen, CancellationToken ct)
    {
        if (_session is null || Target is null)
        {
            return;
        }

        try
        {
            var ddl = await meta.GetDdlAsync(_session, Target, CurrentOptions, ct);
            if (gen == _generation)
            {
                DdlText = ddl;
                DdlError = null;
                _ddlLoaded = true;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (gen == _generation)
            {
                DdlText = string.Empty;
                DdlError = "DDL 을 읽지 못했다: " + ex.Message;
                _ddlLoaded = true; // 옵션을 바꾸거나 새로 고치면 다시 읽는다
            }
        }
    }

    private ITableKeyDetailProvider? ResolveKeys()
    {
        if (!_keysResolved)
        {
            _keys = _services?.GetService(typeof(ITableKeyDetailProvider)) as ITableKeyDetailProvider;
            _keysResolved = true;
        }

        return _keys;
    }

    private void ResetKeyColumns(ObjectDetailSection? section)
    {
        _keyCts?.Cancel();
        _keyCts = null;
        _keySerial++;
        KeyColumns = null;
        HasKeyColumns = section is { KeyKind: not DetailKeyKind.None } && ResolveKeys() is not null;
        KeyStatus = !HasKeyColumns ? string.Empty
            : section!.KeyKind == DetailKeyKind.Index
                ? (LiteralTranslator.IsEnglish ? "Select an index to see its columns" : "인덱스를 고르면 그 열이 보인다")
                : (LiteralTranslator.IsEnglish ? "Select a constraint to see its columns" : "제약을 고르면 그 열이 보인다");
    }

    /// <summary>
    /// 인덱스·제약 구역에서 현재 줄이 바뀌었다(표의 ▶). 그 키의 열을 읽어 <see cref="KeyColumns"/> 에 둔다.
    /// 고를 때마다 앞의 읽기를 취소하고, 같은 (종류, 이름)은 다시 읽지 않는다. 함수 기반·내림차순 인덱스는 위 표의 「열」 칸도 식으로 바꾼다.
    /// </summary>
    public async Task SelectKeyRowAsync(IReadOnlyList<object?>? row)
    {
        if (SelectedSection is not { KeyKind: not DetailKeyKind.None } section || ResolveKeys() is not { } keys
            || _session is not { } session || Target is not { } target)
        {
            return;
        }

        if (row is not null && !section.Rows.Any(r => ReferenceEquals(r, row)))
        {
            return; // 다른 구역의 줄(구역을 바꾸는 사이에 늦게 온 알림)
        }

        if (row is not { Count: > 0 } || row[0] is not string { Length: > 0 } name)
        {
            ResetKeyColumns(section);
            return;
        }

        _keyCts?.Cancel();
        var serial = ++_keySerial;
        var gen = _generation;
        if (_keyCache.TryGetValue((section.KeyKind, name), out var hit))
        {
            _keyCts = null;
            KeyColumns = hit.Table;
            KeyStatus = hit.Status;
            return;
        }

        using var cts = new CancellationTokenSource();
        _keyCts = cts;
        KeyColumns = null;
        KeyStatus = LiteralTranslator.IsEnglish ? $"Reading columns of {name}…" : $"{name} 의 열을 읽는 중…";
        try
        {
            if (KeyReadDelay > TimeSpan.Zero)
            {
                await Task.Delay(KeyReadDelay, cts.Token); // 방향키로 빠르게 지나가는 줄마다 서버에 묻지 않는다
            }

            ObjectDetailSection table;
            bool denied;
            if (section.KeyKind == DetailKeyKind.Index)
            {
                var result = await keys.GetIndexColumnsAsync(session, target, name, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                denied = result.Denied;
                table = new ObjectDetailSection(new DetailSection(
                    "인덱스 열",
                    ["#", "열/식", "정렬"],
                    [.. result.Items.Select(c => (IReadOnlyList<object?>)[c.Position, c.Expression ?? c.Column, c.Descending ? "DESC" : "ASC"])]));
                if (serial == _keySerial && gen == _generation && result.Items.Count > 0)
                {
                    // 위 표의 「열」 칸은 숨은 열 이름(SYS_NC…$)이 보이므로 같은 결과로 식·DESC 를 채운다.
                    section.ReplaceCell(name, "열", string.Join(", ", result.Items.Select(c => (c.Expression ?? c.Column) + (c.Descending ? " DESC" : string.Empty))));
                }
            }
            else
            {
                var result = await keys.GetConstraintColumnsAsync(session, target, name, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                denied = result.Denied;
                table = new ObjectDetailSection(new DetailSection(
                    "제약 열",
                    ["#", "열", "참조 테이블", "참조 열"],
                    [.. result.Items.Select(c => (IReadOnlyList<object?>)
                    [
                        c.Position, c.Column,
                        c.ReferencedTable is { } t ? (c.ReferencedOwner is { } o ? $"{o}.{t}" : t) : null,
                        c.ReferencedColumn,
                    ])]));
            }

            if (serial != _keySerial || gen != _generation)
            {
                return; // 그새 다른 줄·다른 객체를 골랐다
            }

            var status = denied
                ? (LiteralTranslator.IsEnglish ? $"No privilege to read the columns of {name}" : $"권한이 없어 {name} 의 열을 읽지 못했다")
                : (LiteralTranslator.IsEnglish ? $"{name} · {table.Rows.Count} columns" : $"{name} · 열 {table.Rows.Count}개");
            if (!denied)
            {
                _keyCache[(section.KeyKind, name)] = (table, status); // 권한 없음은 권한을 받은 뒤 다시 고르면 다시 읽는다
            }

            KeyColumns = table;
            KeyStatus = status;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (serial == _keySerial && gen == _generation)
            {
                KeyStatus = (LiteralTranslator.IsEnglish ? "Could not read the columns: " : "열을 읽지 못했다: ") + ex.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_keyCts, cts))
            {
                _keyCts = null;
            }
        }
    }

    /// <summary>
    /// 「열 순서 바꾸기…」 단추: 테이블의 「열」 구역이고 기능 <see cref="Oranda.Core.Features.FeatureIds.ColumnReorder"/> 가 켜졌을 때(스키마 브라우저 메뉴와 같게).
    /// 게이트가 없으면 켬(지금 동작).
    /// </summary>
    private void UpdateCanReorderColumns()
        => CanReorderColumns = SelectedSection is { Title: "열" } && Target is { ObjectType: "TABLE" }
            && (_services?.GetService(typeof(Oranda.Core.Features.IFeatureGate)) is not Oranda.Core.Features.IFeatureGate gate
                || gate.IsEnabled(Oranda.Core.Features.FeatureIds.ColumnReorder));

    private IMetadataProvider? ResolveMeta()
        => _meta ??= (IMetadataProvider?)_services?.GetService(typeof(IMetadataProvider));
}
