using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Avalonia.Threading;
using Oranda.Core.Events;
using Oranda.Core.Extensibility;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.SchemaBrowser;

/// <summary>
/// <c>oranda.panel.schemaBrowser</c> — 스키마별 객체 트리(지연 로딩) · 필터 · 접속별 즐겨찾기 · 편집기로 이름/SELECT/열 목록 넣기 (TSK-07-02).
/// 데이터는 <see cref="IMetadataProvider"/> 만 쓴다(읽기). 편집기와는 <see cref="IEditorContext"/> 로만 닿는다.
/// </summary>
[OrandaToolPanel("oranda.panel.schemaBrowser", ToolPanelKind.Tool, DockLocation.Left, Order = 10)]
public sealed partial class SchemaBrowserViewModel : ToolPanelViewModelBase, IDisposable
{
    /// <summary>한 번에 화면에 올리는 객체 수. 1만 개짜리 범주도 이 크기씩 올려 UI 가 멈추지 않게 한다.</summary>
    public const int PageSize = 500;

    private static readonly (string Type, string Title)[] Categories =
    [
        ("TABLE", "테이블"),
        ("VIEW", "뷰"),
        ("MATERIALIZED VIEW", "머티리얼라이즈드 뷰"),
        ("INDEX", "인덱스"),
        ("SEQUENCE", "시퀀스"),
        ("SYNONYM", "시노님"),
        ("PROCEDURE", "프로시저"),
        ("FUNCTION", "함수"),
        ("PACKAGE", "패키지"),
        ("PACKAGE BODY", "패키지 본문"),
        ("TRIGGER", "트리거"),
        ("TYPE", "타입"),
        ("TYPE BODY", "타입 본문"),
        ("DATABASE LINK", "DB 링크"),
        ("JOB", "스케줄러 잡"),
        ("DIRECTORY", "디렉터리"),
        ("DOMAIN", "도메인"),
        ("PROPERTY GRAPH", "속성 그래프"),
    ];

    /// <summary>범주 순서에 있는 객체 유형(필터 콤보 목록).</summary>
    public static IReadOnlyList<string> KnownTypes { get; } = [.. Categories.Select(c => c.Type)];

    private readonly IServiceProvider? _services;
    private bool _resolved;
    private readonly List<IDisposable> _subscriptions = [];
    private IMetadataProvider? _meta;
    private ISessionManager? _sessionManager;
    private IEditorContext? _editor;
    private ISchemaFavoriteStore _favoriteStore;
    private IReadOnlySet<string> _favorites = new HashSet<string>(StringComparer.Ordinal);
    private SchemaNode? _favoritesNode;
    private SchemaFilter _filter = new();
    private readonly Dictionary<string, HashSet<string>> _invalidByOwner = new(StringComparer.Ordinal);
    private int _generation;
    private CancellationTokenSource? _debounce;
    private CancellationTokenSource? _previewDelay;
    private SchemaObjectOpener? _opener;
    private bool _suppressFilterSchedule;

    /// <summary>시험·디자이너용: 서비스 없이 만든다(빈 트리).</summary>
    public SchemaBrowserViewModel()
    {
        _favoriteStore = new InMemorySchemaFavoriteStore();
    }

    /// <summary>
    /// 셸(DI)이 쓰는 생성자. 이벤트 버스만 바로 꺼내 세션 열림·닫힘을 기다리고, 나머지 서비스(메타데이터·세션 관리자·편집기)는
    /// 처음 쓸 때 꺼낸다 — 첫 프레임 전에 Oracle 어셈블리를 올리지 않기 위해서다(D-006, ui-shell.md 시작 시간).
    /// </summary>
    public SchemaBrowserViewModel(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _favoriteStore = new InMemorySchemaFavoriteStore();
        if (services.GetService(typeof(IEventBus)) is IEventBus bus)
        {
            _subscriptions.Add(bus.Subscribe<SessionOpened>(this, e => PostToUi(() => _ = OnSessionOpenedAsync(e))));
            _subscriptions.Add(bus.Subscribe<SessionClosed>(this, e => PostToUi(() => _ = OnSessionClosedAsync(e))));
            _subscriptions.Add(bus.Subscribe<MetadataInvalidated>(this, e => PostToUi(() => _ = OnMetadataInvalidatedAsync(e))));
        }
    }

    /// <summary>이벤트를 UI 스레드로 넘기는 방법. 시험은 바로 실행하도록 바꾼다.</summary>
    public Action<Action> PostToUi { get; set; } = a =>
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            a();
        }
        else
        {
            Dispatcher.UIThread.Post(a);
        }
    };

    /// <summary>지금 보여 주는 세션. 없으면 null.</summary>
    public ISession? CurrentSession { get; private set; }

    /// <summary>다른 세션으로 바꾸고 트리를 다시 만든다(편집기 탭이 바뀌었을 때 등).</summary>
    public Task SetSessionAsync(ISession? session, CancellationToken ct = default)
    {
        CurrentSession = session;
        return LoadAsync(ct);
    }

    private async Task OnSessionOpenedAsync(SessionOpened e)
    {
        if (e.Purpose != SessionPurpose.Work)
        {
            return;
        }

        ResolveServices();
        var session = _sessionManager?.Sessions.FirstOrDefault(s => s.Id == e.SessionId);
        if (session is not null)
        {
            await SetSessionAsync(session);
        }
    }

    private async Task OnSessionClosedAsync(SessionClosed e)
    {
        if (CurrentSession?.Id == e.SessionId)
        {
            ResolveServices();
            var next = _sessionManager?.Sessions.LastOrDefault(s => s.Id != e.SessionId && s.Purpose == SessionPurpose.Work);
            await SetSessionAsync(next);
        }
    }

    private async Task OnMetadataInvalidatedAsync(MetadataInvalidated e)
    {
        if (CurrentSession is { } s && string.Equals(s.DbKey, e.DbKey, StringComparison.Ordinal))
        {
            await ApplyFilterAsync();
        }
    }

    private void ResolveServices()
    {
        if (_services is null || _resolved)
        {
            return;
        }

        _resolved = true;
        _meta = (IMetadataProvider?)_services.GetService(typeof(IMetadataProvider));
        _sessionManager = (ISessionManager?)_services.GetService(typeof(ISessionManager));
        _editor = (IEditorContext?)_services.GetService(typeof(IEditorContext));
        if (_services.GetService(typeof(ISchemaFavoriteStore)) is ISchemaFavoriteStore store)
        {
            _favoriteStore = store;
        }
    }

    public void Dispose()
    {
        _properties?.Dispose();
        _debounce?.Cancel();
        _debounce?.Dispose();
        CancelPendingPreview();
        foreach (var d in _subscriptions)
        {
            d.Dispose();
        }
    }

    public ObservableCollection<SchemaNode> Roots { get; } = [];

    [ObservableProperty]
    private SchemaNode? _selectedNode;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private string? _typeFilter;

    [ObservableProperty]
    private bool _invalidOnly;

    [ObservableProperty]
    private bool _favoritesOnly;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>필터 입력 뒤 자동 적용까지 기다리는 시간(ms). 0 이면 바로.</summary>
    public int DebounceMilliseconds { get; set; } = 300;

    /// <summary>한 번 누르기·방향키로 고른 뒤 미리보기를 열기까지 기다리는 시간(ms). 0 이면 바로. 그 사이 다른 노드를 고르면 앞의 것은 열지 않는다.</summary>
    public int PreviewDelayMilliseconds { get; set; } = 250;

    /// <summary>객체를 「해당 편집기」로 여는 쪽(시험은 도킹을 바꿔 넣는다).</summary>
    internal SchemaObjectOpener Opener => _opener ??= new SchemaObjectOpener(_services);

    /// <summary>시험용: 설정 「한 번 눌러 미리보기 열기」 값(기본 = 앱 설정 <c>schemaBrowser.singleClickPreview</c>).</summary>
    internal Func<bool>? SingleClickPreviewProvider { get; set; }

    /// <summary>한 번 누르기로 미리보기를 연다(설정, 기본 켬). 끄면 한 번 누르기는 속성 창만 바꾸고 두 번 누르기·Enter 로 연다.</summary>
    public bool SingleClickPreview => SingleClickPreviewProvider?.Invoke()
        ?? (_services?.GetService(typeof(SettingsService)) as SettingsService)?.Current.SchemaBrowser.SingleClickPreview
        ?? true;

    /// <summary><c>oranda.schema.filter</c> 가 필터 입력칸으로 포커스를 옮기라고 알린다.</summary>
    public event EventHandler? FilterFocusRequested;

    /// <summary><see cref="RevealAsync(DbObjectRef, ISession?, CancellationToken)"/> 가 노드를 골랐다. View 가 그 노드를 화면에 보이고 포커스를 준다.</summary>
    public event EventHandler<SchemaNode>? RevealRequested;

    public bool HasSession => CurrentSession is not null;

    public bool IsNodeSelected => SelectedNode is { Ref: not null };

    public SchemaFilter Filter => _filter;

    public void RequestFilterFocus() => FilterFocusRequested?.Invoke(this, EventArgs.Empty);


    /// <summary>트리 아래 속성 창(오렌지 Property 창). 고른 객체의 상세를 탭으로 보인다.</summary>
    public SchemaPropertiesViewModel Properties => _properties ??= new SchemaPropertiesViewModel(() => _meta, () => CurrentSession);

    private SchemaPropertiesViewModel? _properties;

    partial void OnSelectedNodeChanged(SchemaNode? value)
    {
        OnPropertyChanged(nameof(IsNodeSelected));

        // 객체를 고르면 아래 속성 창에 보인다. 열 노드는 그 테이블을 그대로 두고, 스키마·범주는 비운다.
        if (value?.Kind != SchemaNodeKind.Column)
        {
            _ = Properties.ShowAsync(value?.Ref);
        }

        // 오른쪽 클릭 메뉴·단축키의 활성 조건(commands.md 2.2): 객체 노드면 schemaNodeSelected, 소스가 있는 객체면 plsqlNodeSelected.
        if (_services?.GetService(typeof(Oranda.App.Shell.ContextKeyService)) is Oranda.App.Shell.ContextKeyService keys)
        {
            keys.Set("schemaNodeSelected", value is { Ref: not null });
            keys.Set("plsqlNodeSelected", value?.Ref?.ObjectType is "PROCEDURE" or "FUNCTION" or "PACKAGE" or "PACKAGE BODY"
                or "TRIGGER" or "TYPE" or "TYPE BODY");
        }
    }

    partial void OnFilterTextChanged(string value) => ScheduleFilter();

    partial void OnTypeFilterChanged(string? value) => ScheduleFilter();

    partial void OnInvalidOnlyChanged(bool value) => ScheduleFilter();

    partial void OnFavoritesOnlyChanged(bool value) => ScheduleFilter();

    private void ScheduleFilter()
    {
        if (_suppressFilterSchedule)
        {
            return;
        }

        _debounce?.Cancel();
        if (DebounceMilliseconds <= 0)
        {
            _ = ApplyFilterAsync();
            return;
        }

        var cts = _debounce = new CancellationTokenSource();
        _ = DelayedApplyAsync(cts.Token);
    }

    private async Task DelayedApplyAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(DebounceMilliseconds, ct);
            await ApplyFilterAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>스키마 목록을 다시 읽어 트리를 처음부터 만든다(새로 고침 · 접속 바뀜).</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        ResolveServices();
        CurrentSession ??= _sessionManager?.Sessions.LastOrDefault(s => s.Purpose == SessionPurpose.Work);
        var gen = Interlocked.Increment(ref _generation);
        Roots.Clear();
        _favoritesNode = null;
        SelectedNode = null;
        OnPropertyChanged(nameof(HasSession));
        var session = CurrentSession;
        if (session is null || _meta is null)
        {
            StatusText = "접속하면 객체 트리가 나타난다";
            return;
        }

        IsBusy = true;
        try
        {
            var schemas = await _meta.ListSchemasAsync(session, ct);
            var favs = await _favoriteStore.LoadAsync(session.Profile.Id, ct);
            if (gen != _generation)
            {
                return;
            }

            _favorites = favs;
            _filter = BuildFilter();
            _favoritesNode = new SchemaNode(this, SchemaNodeKind.Favorites, "즐겨찾기");
            _favoritesNode.ResetChildren();
            Roots.Add(_favoritesNode);
            // 내 스키마(접속 사용자)를 맨 위에 둔다(SQL Developer·Orange 처럼 자기 객체부터 보인다). 나머지는 이름 순서 그대로.
            var ordered = schemas.OrderBy(n => string.Equals(n, session.Profile.User, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
            foreach (var name in ordered)
            {
                var n = new SchemaNode(this, SchemaNodeKind.Schema, name) { Owner = name };
                n.ResetChildren();
                Roots.Add(n);
            }

            StatusText = $"스키마 {schemas.Count}개";
            var own = Roots.FirstOrDefault(r => r.Kind == SchemaNodeKind.Schema
                && string.Equals(r.Owner, session.Profile.User, StringComparison.OrdinalIgnoreCase));
            if (own is not null)
            {
                await ExpandAsync(own, ct);
                own.IsExpandedSilently = true; // 화면에서도 펼친 채로 보인다(이미 읽었으니 다시 읽지 않는다).
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText = "스키마 목록을 읽지 못했다: " + ex.Message;
        }
        finally
        {
            if (gen == _generation)
            {
                IsBusy = false;
            }
        }
    }

    private SchemaFilter BuildFilter() => new(FilterText, TypeFilter, InvalidOnly, FavoritesOnly);

    /// <summary>필터를 적용한다: 이미 불러온 스키마 노드를 비우고 펼쳐 있던 것만 다시 읽는다.</summary>
    public async Task ApplyFilterAsync(CancellationToken ct = default)
    {
        _filter = BuildFilter();
        var gen = Interlocked.Increment(ref _generation);
        var reload = new List<SchemaNode>();

        // 다시 읽어도 펼쳐 둔 범주와 고른 객체는 그대로 둔다(컴파일·DDL 뒤 트리가 접히지 않게).
        var openCategories = new Dictionary<SchemaNode, HashSet<string>>();
        var selectedKey = SelectedNode?.Ref is { } picked ? SchemaSql.FavoriteKey(picked) : null;
        foreach (var root in Roots.Where(r => r.Kind == SchemaNodeKind.Schema))
        {
            if (root.Loaded || root.IsExpanded)
            {
                reload.Add(root);
                openCategories[root] = root.Children
                    .Where(c => c.Kind == SchemaNodeKind.Category && c.IsExpanded && c.CategoryType is not null)
                    .Select(c => c.CategoryType!)
                    .ToHashSet(StringComparer.Ordinal);
            }

            root.ResetChildren();
        }

        foreach (var n in reload)
        {
            if (gen != _generation)
            {
                return;
            }

            await ExpandAsync(n, ct);
            foreach (var cat in n.Children.Where(c => c.Kind == SchemaNodeKind.Category && c.CategoryType is { } t && openCategories[n].Contains(t)).ToList())
            {
                await ExpandAsync(cat, ct);
                cat.IsExpandedSilently = true;
                if (selectedKey is not null
                    && cat.Children.FirstOrDefault(c => c.Ref is { } r && SchemaSql.FavoriteKey(r) == selectedKey) is { } again)
                {
                    SelectedNode = again;
                }
            }
        }
    }

    /// <summary>노드가 펼쳐질 때 자식을 읽는다. 이미 읽었거나 읽는 중이면 아무것도 안 한다.</summary>
    public async Task ExpandAsync(SchemaNode node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Loaded || node.Loading || !node.HasChildrenToLoad)
        {
            return;
        }

        var session = CurrentSession;
        if (session is null || _meta is null)
        {
            return;
        }

        node.Loading = true;
        var gen = _generation;
        try
        {
            switch (node.Kind)
            {
                case SchemaNodeKind.Schema:
                    await LoadSchemaAsync(node, session, ct);
                    break;
                case SchemaNodeKind.Category:
                    node.Children.Clear();
                    node.Shown = 0;
                    AddPage(node);
                    break;
                case SchemaNodeKind.Item:
                    await LoadColumnsAsync(node, session, ct);
                    break;
                case SchemaNodeKind.Favorites:
                    LoadFavorites(node);
                    break;
            }

            if (gen == _generation)
            {
                node.Loaded = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            node.Children.Clear();
            node.Children.Add(new SchemaNode(null, SchemaNodeKind.Message, "읽지 못했다: " + ex.Message));
        }
        finally
        {
            node.Loading = false;
        }
    }

    private async Task LoadSchemaAsync(SchemaNode node, ISession session, CancellationToken ct)
    {
        var f = _filter;
        var query = new ObjectQuery
        {
            Owner = node.Owner,
            ObjectTypes = f.ObjectType is null ? [] : [f.ObjectType],
            NamePattern = SchemaFilter.ToLikePattern(f.Text),
            InvalidOnly = f.InvalidOnly,
        };
        var gen = _generation;

        // 목록 조회 결과에는 상태가 없으므로 INVALID 객체만 먼저 따로 읽어 트리에서 빨갛게 보인다(Orange 처럼 깨진 객체가 바로 보이게).
        var invalid = f.InvalidOnly
            ? null
            : (await _meta!.ListObjectsAsync(session, query with { InvalidOnly = true, ObjectTypes = [], NamePattern = null }, ct))
                .Select(SchemaSql.FavoriteKey).ToHashSet(StringComparer.Ordinal);
        if (node.Owner is { } owner && invalid is not null)
        {
            _invalidByOwner[owner] = invalid;
        }

        var objects = await _meta!.ListObjectsAsync(session, query, ct);
        var favorites = _favorites;
        var groups = await Task.Run(
            () =>
            {
                IEnumerable<DbObjectRef> src = objects;
                if (f.FavoritesOnly)
                {
                    src = src.Where(o => favorites.Contains(SchemaSql.FavoriteKey(o)));
                }

                return src.GroupBy(o => o.ObjectType, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<DbObjectRef>)[.. g.OrderBy(o => o.Name, StringComparer.Ordinal)], StringComparer.Ordinal);
            },
            ct);
        if (gen != _generation)
        {
            return;
        }

        node.Children.Clear();
        var known = new HashSet<string>(Categories.Select(c => c.Type), StringComparer.Ordinal);
        var ordered = Categories.Select(c => (c.Type, c.Title))
            .Concat(groups.Keys.Where(k => !known.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).Select(k => (Type: k, Title: k)));
        foreach (var (type, title) in ordered)
        {
            if (!groups.TryGetValue(type, out var items) || items.Count == 0)
            {
                continue;
            }

            var cat = new SchemaNode(this, SchemaNodeKind.Category, $"{title} ({items.Count})")
            {
                Owner = node.Owner,
                CategoryType = type,
                Parent = node,
                Items = items,
            };
            cat.Children.Add(new SchemaNode(null, SchemaNodeKind.Placeholder, "…"));
            node.Children.Add(cat);
        }

        if (node.Children.Count == 0)
        {
            node.Children.Add(new SchemaNode(null, SchemaNodeKind.Message, "해당하는 객체가 없다"));
        }

        StatusText = $"{node.Owner}: 객체 {objects.Count}개";
    }

    private void AddPage(SchemaNode category)
    {
        var invalid = category.Owner is { } owner && _invalidByOwner.TryGetValue(owner, out var set) ? set : null;
        var next = category.Items.Skip(category.Shown).Take(PageSize).ToList();
        foreach (var r in next)
        {
            var status = _filter.InvalidOnly || (invalid?.Contains(SchemaSql.FavoriteKey(r)) ?? false) ? "INVALID" : null;
            category.Children.Add(MakeObject(r, category, status));
        }

        category.Shown += next.Count;
        if (category.Shown < category.Items.Count)
        {
            category.Children.Add(new SchemaNode(this, SchemaNodeKind.More, $"더 보기… ({category.Items.Count - category.Shown}개 남음)") { Parent = category });
        }
    }

    private SchemaNode MakeObject(DbObjectRef r, SchemaNode? parent, string? status = null)
    {
        var n = new SchemaNode(this, SchemaNodeKind.Item, r.Name)
        {
            Ref = r,
            Owner = r.Owner,
            Parent = parent,
            Status = status,
            IsFavorite = _favorites.Contains(SchemaSql.FavoriteKey(r)),
        };
        n.ResetChildren();
        return n;
    }

    /// <summary>「더 보기」 노드를 눌렀을 때: 다음 쪽을 올린다.</summary>
    public void ShowMore(SchemaNode more)
    {
        ArgumentNullException.ThrowIfNull(more);
        if (more.Kind != SchemaNodeKind.More || more.Parent is not { } cat)
        {
            return;
        }

        cat.Children.Remove(more);
        AddPage(cat);
    }

    // ---- 객체 열기(SCH-01): 한 번 누르기 = 미리보기, 두 번 누르기·Enter = 고정 -------------------

    /// <summary>
    /// 사용자가 노드를 한 번 누르거나 방향키로 골랐을 때 View 가 부른다(코드가 고른 선택 — 트리에서 찾기·필터 다시 읽기 — 는 부르지 않는다).
    /// 설정이 켜져 있고 객체 노드면 잠깐(<see cref="PreviewDelayMilliseconds"/>) 뒤 미리보기 문서로 연다. 포커스는 트리에 남는다.
    /// 앞서 예약한 미리보기는 취소된다.
    /// </summary>
    public void SchedulePreview(SchemaNode? node)
    {
        CancelPendingPreview();
        if (node is not { Kind: SchemaNodeKind.Item, Ref: not null } || !SingleClickPreview)
        {
            return;
        }

        if (PreviewDelayMilliseconds <= 0)
        {
            _ = OpenNodeAsync(node, pin: false, CancellationToken.None);
            return;
        }

        var cts = _previewDelay = new CancellationTokenSource();
        _ = DelayedPreviewAsync(node, cts.Token);
    }

    private async Task DelayedPreviewAsync(SchemaNode node, CancellationToken ct)
    {
        try
        {
            await Task.Delay(PreviewDelayMilliseconds, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // 기다리는 사이 다른 노드로 옮겨 갔으면(코드가 고른 선택 포함) 열지 않는다. 예약 취소 토큰은 읽기에 넘기지 않는다 —
        // 이어서 두 번 누르면 이 미리보기 탭을 고정하는데, 그때 읽던 내용이 끊기면 안 된다(탭을 닫으면 읽기는 opener 가 멈춘다).
        if (ReferenceEquals(SelectedNode, node))
        {
            await OpenNodeAsync(node, pin: false, CancellationToken.None);
        }
    }

    /// <summary>
    /// 두 번 누르기·Enter: 고정 문서로 열고 그 문서로 키보드 포커스를 옮긴다. 예약된 미리보기는 취소하고, 같은 객체의 미리보기가
    /// 이미 열려 있으면 그 탭을 고정한다(탭을 하나 더 만들지 않는다). 연 문서가 있으면 true.
    /// </summary>
    public Task<bool> OpenPinnedAsync(SchemaNode? node, CancellationToken ct = default)
    {
        CancelPendingPreview();
        return OpenNodeAsync(node ?? SelectedNode, pin: true, ct);
    }

    private void CancelPendingPreview()
    {
        _previewDelay?.Cancel();
        _previewDelay?.Dispose();
        _previewDelay = null;
    }

    /// <summary>객체 노드만 연다(스키마·범주·「더 보기」·메시지·열 노드는 열지 않는다). 접속이 없거나 세션이 바쁘면 상태 줄에 알린다.</summary>
    private async Task<bool> OpenNodeAsync(SchemaNode? node, bool pin, CancellationToken ct)
    {
        if (node is not { Kind: SchemaNodeKind.Item, Ref: { } target })
        {
            return false;
        }

        if (CurrentSession is not { } session)
        {
            StatusText = UiText.Get("Shell.schemaOpen.noSession", "접속이 없어 객체를 열지 않았다");
            return false;
        }

        if (session.State != SessionState.Idle)
        {
            StatusText = session.State == SessionState.Busy
                ? UiText.Get("Shell.schemaOpen.busy", "세션이 실행 중이라 객체를 열지 않았다 — 끝난 뒤 다시 연다")
                : UiText.Get("Shell.schemaOpen.closed", "세션이 끊겨 객체를 열지 않았다");
            return false;
        }

        try
        {
            return await Opener.OpenAsync(target, session, preview: !pin, focus: pin, ct) is not null;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // ---- 트리에서 찾기(oranda.schema.reveal) -----------------------------------------------

    /// <summary>
    /// 객체를 트리에서 찾아 고른다(DataGrip 「Select in Database Tree」): 스키마 → 유형 범주를 트리처럼 지연 로딩으로 펼치고
    /// 노드를 선택한다(아래 속성 창도 선택을 따라간다). 찾으면 true.
    /// </summary>
    public Task<bool> RevealAsync(DbObjectRef target, CancellationToken ct = default) => RevealAsync(target, null, ct);

    /// <summary>
    /// <paramref name="session"/> 을 주면 그 접속 대상(DbKey)의 트리에서 찾는다(지금 보이는 대상과 다르면 트리를 바꾼다).
    /// 이름 필터·유형 필터 등에 가려 못 찾으면 필터를 풀고 한 번 더 찾는다.
    /// </summary>
    public async Task<bool> RevealAsync(DbObjectRef target, ISession? session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ResolveServices();
        target = target with { SubName = null };

        if (session is not null && (CurrentSession is null || !string.Equals(CurrentSession.DbKey, session.DbKey, StringComparison.Ordinal)))
        {
            await SetSessionAsync(session, ct);
        }
        else
        {
            // 접속 직후 등 트리를 짓는 중이면 끝나기를 기다린다. 도구 창을 방금 만들었으면 트리가 비어 있으니 지금 짓는다.
            await WaitWhileAsync(() => IsBusy, ct);
            if (CurrentSession is null || !Roots.Any(r => r.Kind == SchemaNodeKind.Schema))
            {
                await LoadAsync(ct);
            }
        }

        var node = await FindNodeAsync(target, ct);
        if (node is null && !_filter.IsEmpty)
        {
            await ClearFilterAsync(ct);
            node = await FindNodeAsync(target, ct);
        }

        if (node is null)
        {
            StatusText = $"트리에서 {target.Owner}.{target.Name} 을(를) 찾지 못했다";
            return false;
        }

        for (var p = node.Parent; p is not null; p = p.Parent)
        {
            p.IsExpandedSilently = true;
        }

        SelectedNode = node;
        StatusText = $"{target.Owner}.{target.Name} ({target.ObjectType})";
        RevealRequested?.Invoke(this, node);
        return true;
    }

    /// <summary>스키마 → 범주 → 객체 노드를 찾는다. 범주가 쪽으로 나뉘어 있으면 그 객체가 보일 때까지 「더 보기」를 누른다.</summary>
    private async Task<SchemaNode?> FindNodeAsync(DbObjectRef target, CancellationToken ct)
    {
        var schemas = Roots.Where(r => r.Kind == SchemaNodeKind.Schema).ToList();
        var schema = schemas.FirstOrDefault(r => string.Equals(r.Owner, target.Owner, StringComparison.Ordinal))
            ?? schemas.FirstOrDefault(r => string.Equals(r.Owner, target.Owner, StringComparison.OrdinalIgnoreCase));
        if (schema is null || !await EnsureLoadedAsync(schema, ct))
        {
            return null;
        }

        // 같은 유형 범주를 먼저, 본문(PACKAGE BODY 등)이면 명세 범주를, 그래도 없으면 다른 범주에서 같은 이름을 찾는다.
        var categories = schema.Children.Where(c => c.Kind == SchemaNodeKind.Category && c.CategoryType is not null).ToList();
        var spec = target.ObjectType.EndsWith(" BODY", StringComparison.Ordinal) ? target.ObjectType[..^5] : null;
        var ordered = categories
            .OrderBy(c => string.Equals(c.CategoryType, target.ObjectType, StringComparison.Ordinal) ? 0
                : string.Equals(c.CategoryType, spec, StringComparison.Ordinal) ? 1 : 2)
            .ToList();
        foreach (var cat in ordered)
        {
            var index = IndexOfName(cat.Items, target.Name);
            if (index < 0)
            {
                continue;
            }

            if (!await EnsureLoadedAsync(cat, ct))
            {
                return null;
            }

            while (cat.Shown <= index && cat.Children.LastOrDefault() is { Kind: SchemaNodeKind.More } more)
            {
                ShowMore(more);
            }

            return cat.Children.FirstOrDefault(c => c.Kind == SchemaNodeKind.Item && c.Ref is { } r
                && string.Equals(r.Name, target.Name, StringComparison.Ordinal));
        }

        return null;
    }

    private static int IndexOfName(IReadOnlyList<DbObjectRef> items, string name)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>노드의 자식을 읽어 둔다. 다른 쪽(트리 짓기·사용자 펼침)이 읽는 중이면 끝나기를 기다린다.</summary>
    private async Task<bool> EnsureLoadedAsync(SchemaNode node, CancellationToken ct)
    {
        await WaitWhileAsync(() => node.Loading, ct);
        if (!node.Loaded)
        {
            await ExpandAsync(node, ct);
            await WaitWhileAsync(() => node.Loading, ct);
        }

        return node.Loaded;
    }

    private static async Task WaitWhileAsync(Func<bool> condition, CancellationToken ct)
    {
        for (var i = 0; i < 400 && condition(); i++)
        {
            await Task.Delay(25, ct);
        }
    }

    /// <summary>필터를 모두 풀고 바로 적용한다(입력 지연 타이머는 쓰지 않는다 — 다시 읽기가 겹치지 않게).</summary>
    private async Task ClearFilterAsync(CancellationToken ct)
    {
        _suppressFilterSchedule = true;
        try
        {
            FilterText = string.Empty;
            TypeFilter = null;
            InvalidOnly = false;
            FavoritesOnly = false;
        }
        finally
        {
            _suppressFilterSchedule = false;
        }

        _debounce?.Cancel();
        await ApplyFilterAsync(ct);
    }

    private async Task LoadColumnsAsync(SchemaNode node, ISession session, CancellationToken ct)
    {
        var info = await _meta!.GetTableAsync(session, node.Ref!, ct);
        node.Children.Clear();
        foreach (var c in info.Columns.OrderBy(c => c.Position))
        {
            node.Children.Add(new SchemaNode(null, SchemaNodeKind.Column, c.Name)
            {
                Owner = node.Owner,
                Detail = DescribeColumn(c),
                Parent = node,
            });
        }

        if (node.Children.Count == 0)
        {
            node.Children.Add(new SchemaNode(null, SchemaNodeKind.Message, "열이 없다"));
        }
    }

    internal static string DescribeColumn(ColumnInfo c)
    {
        var type = c.DataType;
        if (c.Precision is { } p)
        {
            type += c.Scale is { } s and not 0 ? $"({p},{s})" : $"({p})";
        }
        else if (c.Length is { } l and > 0 && c.DataType.Contains("CHAR", StringComparison.Ordinal))
        {
            type += $"({l})";
        }

        return c.IsNullable ? type : type + " NOT NULL";
    }

    private void LoadFavorites(SchemaNode node)
    {
        node.Children.Clear();
        foreach (var key in _favorites.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (TryParseKey(key) is { } r)
            {
                node.Children.Add(MakeObject(r, node));
            }
        }

        if (node.Children.Count == 0)
        {
            node.Children.Add(new SchemaNode(null, SchemaNodeKind.Message, "즐겨찾기가 없다"));
        }
    }

    internal static DbObjectRef? TryParseKey(string key)
    {
        var bar = key.LastIndexOf('|');
        var dot = key.IndexOf('.', StringComparison.Ordinal);
        if (bar < 0 || dot < 0 || dot > bar)
        {
            return null;
        }

        return new DbObjectRef(key[..dot], key[(dot + 1)..bar], key[(bar + 1)..]);
    }

    /// <summary>즐겨찾기를 켜고 끈다(접속별).</summary>
    public async Task ToggleFavoriteAsync(SchemaNode? node, CancellationToken ct = default)
    {
        node ??= SelectedNode;
        var session = CurrentSession;
        if (node?.Ref is not { } r || session is null)
        {
            return;
        }

        var key = SchemaSql.FavoriteKey(r);
        var on = !_favorites.Contains(key);
        await _favoriteStore.SetAsync(session.Profile.Id, key, on, ct);
        _favorites = await _favoriteStore.LoadAsync(session.Profile.Id, ct);
        foreach (var n in Flatten(Roots).Where(n => n.Ref is { } rr && SchemaSql.FavoriteKey(rr) == key))
        {
            n.IsFavorite = on;
        }

        if (_favoritesNode is { } fav)
        {
            var wasExpanded = fav.IsExpanded;
            fav.ResetChildren();
            if (wasExpanded)
            {
                await ExpandAsync(fav, ct);
            }
        }

        if (_filter.FavoritesOnly)
        {
            await ApplyFilterAsync(ct);
        }
    }

    private static IEnumerable<SchemaNode> Flatten(IEnumerable<SchemaNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Flatten(n.Children))
            {
                yield return c;
            }
        }
    }

    /// <summary>노드에 대한 삽입 텍스트. 만들 수 없으면 null.</summary>
    public async Task<string?> BuildTextAsync(SchemaNode? node, SchemaInsertMode mode, CancellationToken ct = default)
    {
        node ??= SelectedNode;
        if (node is null)
        {
            return null;
        }

        if (node.Kind == SchemaNodeKind.Column)
        {
            return SchemaSql.Quote(node.Title);
        }

        if (node.Ref is not { } r)
        {
            return null;
        }

        if (mode == SchemaInsertMode.Name)
        {
            return SchemaSql.QualifiedName(r);
        }

        if (!SchemaNode.IsTableLike(r.ObjectType) || _meta is null || CurrentSession is not { } session)
        {
            return mode == SchemaInsertMode.Select ? SchemaSql.Select(r, null) : SchemaSql.QualifiedName(r);
        }

        var info = await _meta.GetTableAsync(session, r, ct);
        return mode == SchemaInsertMode.Select ? SchemaSql.Select(r, info.Columns) : SchemaSql.ColumnList(info.Columns);
    }

    /// <summary>
    /// 활성 편집기의 선택 영역(또는 캐럿)에 넣는다. 편집기 계약(<see cref="IEditorContext.ProposeEditAsync"/>)을 거치므로
    /// 편집기가 미리보기·되돌리기를 책임진다. 편집기가 없거나 거절하면 false.
    /// </summary>
    public async Task<bool> InsertAsync(SchemaNode? node, SchemaInsertMode mode, CancellationToken ct = default)
    {
        ResolveServices();
        if (_editor?.GetActiveDocument() is not { } doc)
        {
            StatusText = "열려 있는 SQL 편집기가 없다";
            return false;
        }

        var text = await BuildTextAsync(node, mode, ct);
        if (text is null)
        {
            return false;
        }

        var range = doc.Selection.Length > 0
            ? doc.Selection
            : new Oranda.Core.Text.TextRange(Math.Clamp(doc.CaretOffset, 0, doc.Text.Length), 0);
        var description = mode switch
        {
            SchemaInsertMode.Name => "이름 넣기",
            SchemaInsertMode.Select => "SELECT 문 넣기",
            _ => "열 목록 넣기",
        };
        return await _editor.ProposeEditAsync(new TextEdit(range, text) { Description = description }, ct);
    }

    /// <summary>끌기 시작 때 쓸 텍스트: 표·뷰는 SELECT 문, 그 밖은 이름. Ctrl = 이름, Shift = 열 목록.</summary>
    public Task<string?> PrepareDragTextAsync(SchemaNode node, bool ctrl, bool shift, CancellationToken ct = default) =>
        BuildTextAsync(node, ctrl ? SchemaInsertMode.Name : shift ? SchemaInsertMode.Columns : SchemaInsertMode.Select, ct);
}
