using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Shell;
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

    /// <summary><c>oranda.schema.filter</c> 가 필터 입력칸으로 포커스를 옮기라고 알린다.</summary>
    public event EventHandler? FilterFocusRequested;

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
