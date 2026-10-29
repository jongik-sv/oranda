using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
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
        ("JAVA SOURCE", "Java 소스"),
        ("JAVA CLASS", "Java 클래스"),
        ("JAVA RESOURCE", "Java 리소스"),
    ];

    /// <summary>범주 순서에 있는 객체 유형(필터 콤보 목록).</summary>
    public static IReadOnlyList<string> KnownTypes { get; } = [.. Categories.Select(c => c.Type)];

    /// <summary>범주 순서의 (객체 유형, 트리 제목). 저장 필터 편집의 객체 탭 줄.</summary>
    internal static IReadOnlyList<(string Type, string Title)> CategoryTitles => Categories;

    private readonly IServiceProvider? _services;
    private bool _resolved;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _followingConnections;
    private IMetadataProvider? _meta;
    private ISchemaTreeProvider? _treeProvider;
    private ISchemaAdminProvider? _admin;
    private ISessionManager? _sessionManager;
    private IEditorContext? _editor;
    private ISchemaFavoriteStore _favoriteStore;
    private IReadOnlySet<string> _favorites = new HashSet<string>(StringComparer.Ordinal);
    private SchemaNode? _favoritesNode;
    private SchemaFilter _filter = new();
    private readonly Dictionary<string, HashSet<string>> _invalidByOwner = new(StringComparer.Ordinal);
    private bool _reuseInvalid;
    private int _generation;
    private int _buildGeneration;
    private int _loadsInFlight;
    private CancellationTokenSource? _debounce;
    private CancellationTokenSource? _previewDelay;
    private SchemaObjectOpener? _opener;
    private bool _suppressFilterSchedule;

    /// <summary>누름 때 불러 둔 열 목록(표·뷰). 끌기 시작에서는 DB 를 부를 수 없어(await 금지) 이 캐시로 글자를 만든다.</summary>
    private readonly Dictionary<DbObjectRef, IReadOnlyList<ColumnInfo>> _dragColumns = [];

    /// <summary>시험·디자이너용: 서비스 없이 만든다(빈 트리).</summary>
    public SchemaBrowserViewModel()
    {
        _favoriteStore = new InMemorySchemaFavoriteStore();
        Types.Changed += OnTypesChanged;
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
        Types.Changed += OnTypesChanged;
        if (services.GetService(typeof(IEventBus)) is IEventBus bus)
        {
            _subscriptions.Add(bus.Subscribe<SessionOpened>(this, e => PostToUi(() => _ = OnSessionOpenedAsync(e))));
            _subscriptions.Add(bus.Subscribe<SessionClosed>(this, e => PostToUi(() => _ = OnSessionClosedAsync(e))));
            _subscriptions.Add(bus.Subscribe<MetadataInvalidated>(this, e => PostToUi(() => _ = OnMetadataInvalidatedAsync(e))));
        }

        // 패널은 처음 보일 때 만들어진다(같은 자리의 스크립트 창 등 다른 탭 뒤에 있었으면 접속한 뒤). 그 전에 연 접속은 SessionOpened 를
        // 놓쳤으므로 지금 접속을 바로 따른다. 접속 서비스가 이미 등록돼 있을 때만 본다(GetService 는 모르는 형이면 지연 어셈블리를 훑어 Oracle 을 깨우므로 GetRegisteredOrDefault 로 본다).
        if (services.GetRegisteredOrDefault<ConnectionManager.IConnectionService>() is { ActiveSession: { } open })
        {
            ResolveServices();
            FollowConnections();
            _ = SetSessionAsync(open);
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
        _dragColumns.Clear();
        return LoadAsync(ct);
    }

    private async Task OnSessionOpenedAsync(SessionOpened e)
    {
        if (e.Purpose != SessionPurpose.Work)
        {
            return;
        }

        ResolveServices();
        FollowConnections();
        var session = _sessionManager?.Sessions.FirstOrDefault(s => s.Id == e.SessionId);
        // 지금 접속을 따라가면 새 접속이 지금 접속이 될 때(ActiveSessionChanged) 바꾼다: 동시 접속으로 곧 닫힐 세션을 먼저 읽지 않게.
        if (session is not null && (!_followingConnections || ReferenceEquals(ConnectionManager.SessionRouter.CurrentOf(_services), session)))
        {
            await SetSessionAsync(session); // 새 접속은 지금 접속이 된다
        }
    }

    /// <summary>
    /// 지금 접속이 바뀌면(툴바 콤보·탭 전환, CONN-06) 트리를 그 접속으로 바꾼다. 접속 서비스는 첫 작업 세션이 열린 뒤에 꺼낸다
    /// (그 전에는 Oracle 어셈블리를 깨우지 않게).
    /// </summary>
    private void FollowConnections()
    {
        if (_followingConnections || _services?.GetService(typeof(ConnectionManager.IConnectionService)) is not ConnectionManager.IConnectionService connections)
        {
            return;
        }

        _followingConnections = true;
        EventHandler handler = (_, _) => PostToUi(() =>
        {
            if (ConnectionManager.SessionRouter.CurrentOf(_services) is { } current && current.Id != CurrentSession?.Id)
            {
                _ = SetSessionAsync(current);
            }
        });
        connections.ActiveSessionChanged += handler;
        _subscriptions.Add(new Unsubscriber(() => connections.ActiveSessionChanged -= handler));
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private async Task OnSessionClosedAsync(SessionClosed e)
    {
        if (CurrentSession?.Id == e.SessionId)
        {
            ResolveServices();
            var next = ConnectionManager.SessionRouter.CurrentOf(_services);
            await SetSessionAsync(next?.Id == e.SessionId ? null : next);
        }
    }

    private async Task OnMetadataInvalidatedAsync(MetadataInvalidated e)
    {
        if (CurrentSession is { } s && string.Equals(s.DbKey, e.DbKey, StringComparison.Ordinal))
        {
            _dragColumns.Clear(); // 표 구조가 바뀌었을 수 있다.
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
        _treeProvider = (ISchemaTreeProvider?)_services.GetService(typeof(ISchemaTreeProvider));
        _admin = (ISchemaAdminProvider?)_services.GetService(typeof(ISchemaAdminProvider));
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

    /// <summary>
    /// 트리에 보일 객체 유형(체크 드롭다운, TSK-14-25). 전부 또는 아무것도 안 고르면 모든 유형. 바꾸면 바로 다시 거르고 접속별로 기억한다.
    /// </summary>
    public Oranda.App.Shell.Controls.TypeChoiceSet Types { get; } = new(KnownTypes);

    [ObservableProperty]
    private bool _invalidOnly;

    [ObservableProperty]
    private bool _favoritesOnly;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>트리를 짓거나 노드를 읽는 중이다(패널의 진행 막대). 버려진 읽기도 끝나면 꺼진다.</summary>
    [ObservableProperty]
    private bool _isLoading;

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
        if (value?.Kind is not (SchemaNodeKind.Column or SchemaNodeKind.Index or SchemaNodeKind.Constraint or SchemaNodeKind.Folder))
        {
            _ = Properties.ShowAsync(value?.Ref);
        }

        // 오른쪽 클릭 메뉴·단축키의 활성 조건(commands.md 2.2): 객체 노드면 schemaNodeSelected, 소스가 있는 객체면 plsqlNodeSelected.
        if (_services?.GetService(typeof(Oranda.App.Shell.ContextKeyService)) is Oranda.App.Shell.ContextKeyService keys)
        {
            keys.Set("schemaNodeSelected", value is { Ref: not null });
            keys.Set("tableNodeSelected", IsInsideTable(value));
            keys.Set("plsqlNodeSelected", value?.Ref?.ObjectType is "PROCEDURE" or "FUNCTION" or "PACKAGE" or "PACKAGE BODY"
                or "TRIGGER" or "TYPE" or "TYPE BODY");
        }
    }
}
