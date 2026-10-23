using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.Localization;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.Core.Shell;

namespace Oranda.App.Features.MyBatisWorkspace.Statements;

/// <summary>
/// 「문장」 도구 창(docs/oranda1/design.md 1장·3.1). 활성 작업 공간의 MyBatis 세션 요소를 종류별 묶음으로 보이고,
/// 수정·새 문장·조각 쓰임·배지를 항목 옆에 그린다. 항목을 고르면 세션의 고른 문장이 되고(우클릭 메뉴·명령의 대상), 두 번 누르면 문장 탭이 열린다.
/// 세션의 StateChanged 는 글자를 칠 때마다 나므로, 목록 구성(요소 개수·순서)이 같으면 노드를 다시 짓지 않고 표시만 고친다.
/// </summary>
[OrandaToolPanel("oranda.panel.mybatisStatements", ToolPanelKind.Tool, DockLocation.Left, Order = 30)]
public sealed partial class MyBatisStatementsViewModel : ToolPanelViewModelBase, IDisposable
{
    private const string KeyPrefix = "MyBatis.statements.";
    private const string NewCommandId = "oranda.mybatis.newStatement";
    private const string RenameCommandId = "oranda.mybatis.renameStatement";
    private const string DeleteCommandId = "oranda.mybatis.deleteStatement";
    private const string ExpandCommandId = "oranda.mybatis.expandToSqlTab";

    private static readonly MyBatisStatementKind[] GroupOrder =
    [
        MyBatisStatementKind.Select,
        MyBatisStatementKind.Insert,
        MyBatisStatementKind.Update,
        MyBatisStatementKind.Delete,
        MyBatisStatementKind.Sql,
        MyBatisStatementKind.ResultMap,
        MyBatisStatementKind.Other,
    ];

    // 매개변수 없는 생성자만 둔다. 셸의 생성자 고르기는 등록되지 않은 참조 형식 매개변수를 만나면 지연 어셈블리를 훑는다
    // (OutlineViewModel 주석의 시작 시험 제약). 호스트에는 처음 읽을 때 붙고, 시험은 Host 를 초기화 구문으로 준다.
    private readonly ObservableCollection<MyBatisStatementNode> _groups = [];
    private readonly MyBatisStatementRows _rows = [];
    private readonly ObservableCollection<MyBatisStatementsModeButton> _modes = [];
    private readonly HashSet<MyBatisStatementKind> _collapsed = [];
    private SqlEditorHost? _host;
    private SqlWorkspaceViewModel? _workspace;
    private MyBatisWorkspaceSession? _session;
    private List<MyBatisStatementEntry> _shown = [];
    private List<IMyBatisStatementBadgeProvider> _providers = [];
    private DispatcherTimer? _usageTimer;
    private DispatcherTimer? _searchTimer;
    private string[] _terms = [];
    private string _appliedSearch = string.Empty;
    private IShellText? _languageSource;
    private bool _attached;
    private bool _syncing;
    private bool _modeSync;
    private bool _modesLoaded;
    private bool _disposed;
    private bool _hasSession;

    [ObservableProperty]
    private string _mapperPath = string.Empty;

    [ObservableProperty]
    private string _mapperDirectory = string.Empty;

    [ObservableProperty]
    private string _mapperFileName = string.Empty;

    [ObservableProperty]
    private string _namespaceText = string.Empty;

    [ObservableProperty]
    private MyBatisStatementNode? _selectedNode;

    [ObservableProperty]
    private bool _isModeActive;

    /// <summary>눌려 있는 보기 모드(없으면 null). 모드의 View 는 「문장」 창 화면마다 따로 만든다(<see cref="MyBatisStatementsView"/>) — 같은 Control 을 두 창에 붙이면 Avalonia 가 예외를 던진다.</summary>
    [ObservableProperty]
    private MyBatisStatementsModeButton? _activeMode;

    /// <summary>검색 칸의 글(칠 때마다 바뀐다). 목록은 <see cref="FilterDebounceMilliseconds"/> 뒤에 거른다.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>거른 결과가 하나도 없다(「일치하는 쿼리 없음」을 보인다).</summary>
    [ObservableProperty]
    private bool _showsNoMatch;

    /// <summary>목록·도구 막대를 보인다: 세션이 있거나 쓸 수 있는 보기 모드(예 매퍼 폴더 트리)가 있다. 아니면 안내 한 줄만 보인다.</summary>
    [ObservableProperty]
    private bool _showsBody;

    /// <summary>다루는 호스트. 기본은 <see cref="SqlEditorHost.Current"/>.</summary>
    public SqlEditorHost Host
    {
        get => _host ?? SqlEditorHost.Current;
        init => _host = value;
    }

    /// <summary>검색 글을 친 뒤 목록을 거르기까지 기다리는 시간(0 이하면 바로. 시험용).</summary>
    internal int FilterDebounceMilliseconds { get; init; } = 150;

    /// <summary>지금 목록에 적용된 검색 글(디바운스 뒤).</summary>
    public string AppliedSearch => _appliedSearch;

    /// <summary>화면 글자 조회. 기본은 셸의 <see cref="UiText.Current"/>, 없으면 한국어 표(시험).</summary>
    internal IShellText? Text { get; init; }

    /// <summary>활성 작업 공간에 MyBatis 세션이 붙어 있다(아니면 안내 한 줄만 보인다).</summary>
    public bool HasSession
    {
        get
        {
            Attach();
            return _hasSession;
        }

        private set
        {
            if (SetProperty(ref _hasSession, value))
            {
                UpdateShowsBody();
            }
        }
    }

    /// <summary>종류별 묶음(빈 묶음은 없다). 각 묶음의 <see cref="MyBatisStatementNode.Children"/> 이 요소 노드.</summary>
    public ObservableCollection<MyBatisStatementNode> Groups
    {
        get
        {
            Attach();
            return _groups;
        }
    }

    /// <summary>
    /// 화면(가상화 ListBox)이 그리는 평평한 줄: 묶음 머리 다음에 펼친 묶음의 항목들. 묶음을 접고 펼치면 그 묶음의 항목 줄만 빼고 넣는다.
    /// 1MB 매퍼처럼 요소가 수천 개여도 보이는 줄만 화면 요소가 된다.
    /// </summary>
    public MyBatisStatementRows Rows
    {
        get
        {
            Attach();
            return _rows;
        }
    }

    /// <summary>도구 막대 오른쪽 끝의 보기 전환 단추(등록된 모드마다 하나, 없으면 빈 목록).</summary>
    public ObservableCollection<MyBatisStatementsModeButton> ViewModes
    {
        get
        {
            Attach();
            return _modes;
        }
    }

    private IShellText? ActiveText => Text ?? UiText.Current;

    /// <summary>호스트 사건을 받기 시작한다(여러 번 불러도 한 번만). View 가 붙을 때와 처음 목록을 읽을 때 불린다.</summary>
    public void Attach()
    {
        if (_disposed)
        {
            return;
        }

        EnsureModes(); // 훅은 시작 작업이 늦게 넣을 수 있어 붙어 있어도 다시 본다(창이 다시 보일 때마다 불린다)
        if (_attached)
        {
            return;
        }

        _attached = true;
        Host.ActiveWorkspaceChanged += OnActiveWorkspaceChanged;
        _languageSource = ActiveText;
        if (_languageSource is not null)
        {
            _languageSource.LanguageChanged += OnLanguageChanged;
        }

        FollowWorkspace();
    }

    partial void OnSearchTextChanged(string value)
    {
        if (string.Equals(value?.Trim() ?? string.Empty, _appliedSearch, StringComparison.Ordinal))
        {
            _searchTimer?.Stop();
            return;
        }

        if (FilterDebounceMilliseconds <= 0)
        {
            ApplySearch();
            return;
        }

        _searchTimer ??= CreateSearchTimer();
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private DispatcherTimer CreateSearchTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FilterDebounceMilliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ApplySearch();
        };
        return timer;
    }

    /// <summary>검색 글을 지우고 전체 목록으로 돌아간다(바로).</summary>
    public void ClearSearch()
    {
        _searchTimer?.Stop();
        SearchText = string.Empty;
        ApplySearch();
    }

    private void ApplySearch()
    {
        _searchTimer?.Stop();
        var text = SearchText?.Trim() ?? string.Empty;
        if (string.Equals(text, _appliedSearch, StringComparison.Ordinal))
        {
            return;
        }

        _appliedSearch = text;
        _terms = MyBatisQueryFilter.Terms(text);
        if (_session is not null)
        {
            Rebuild();
            SyncSelection();
        }
    }

    /// <summary>기다리는 검색 글이 있으면 지금 적용한다(↓·Enter 가 바로 첫 결과를 집게).</summary>
    public void FlushSearch() => ApplySearch();

    /// <summary>목록 첫 항목(묶음 머리 제외), 없으면 null.</summary>
    public MyBatisStatementNode? FirstItem() => _groups.SelectMany(g => g.IsExpanded ? g.Children : []).FirstOrDefault();

    private bool IsFiltering => _terms.Length > 0;

    /// <summary>항목을 고른다: 세션의 고른 문장이 된다(우클릭 메뉴·명령의 대상). 묶음 머리나 id 없는 요소는 세션에 알리지 않는다.</summary>
    public void SelectNode(MyBatisStatementNode? node)
    {
        if (node is null or { IsGroup: true })
        {
            // 묶음 머리는 고를 수 없다: 목록 선택을 비우고 세션의 고른 문장도 비운다(명령이 전에 고른 문장을 집지 않게).
            SelectedNode = null;
            _session?.SelectEntry(null);
            return;
        }

        SelectedNode = node;
    }

    /// <summary>항목을 두 번 눌렀다: 고르고 문장 탭을 연다. 열 수 없는 요소(resultMap 등)는 고르기만 한다.</summary>
    public void OpenNode(MyBatisStatementNode? node)
    {
        if (node?.Entry is not { } entry)
        {
            return;
        }

        SelectedNode = node;
        if (entry.CanOpen)
        {
            _session?.OpenEntry(entry); // 같은 id 요소(databaseId 변형·같은 id resultMap)를 가리지 않게 요소를 넘긴다
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_attached)
        {
            Host.ActiveWorkspaceChanged -= OnActiveWorkspaceChanged;
        }

        if (_languageSource is not null)
        {
            _languageSource.LanguageChanged -= OnLanguageChanged;
            _languageSource = null;
        }

        if (_workspace is not null)
        {
            _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
            _workspace = null;
        }

        SetSession(null);
        ClearModes();
        _usageTimer?.Stop();
        _searchTimer?.Stop();
    }

    partial void OnSelectedNodeChanged(MyBatisStatementNode? value)
    {
        UpdateCommandStates();
        if (_syncing || _session is null)
        {
            return;
        }

        // 묶음 머리·id 없는 요소(cache 등)를 고르면 고른 문장을 비운다(우클릭 「지우기」가 전에 고른 문장을 집지 않게).
        _session.SelectEntry(value?.Entry is { Id: not null } entry ? entry : null);
    }

    // ---- 도구 막대 명령(레지스트리 명령 한 경로, 대상 문장은 세션이 안다) ----------------------

    [RelayCommand(CanExecute = nameof(CanNew))]
    private Task NewStatementAsync() => ShellCommandRunner.RunAsync(NewCommandId);

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private Task RenameStatementAsync() => ShellCommandRunner.RunAsync(RenameCommandId);

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private Task DeleteStatementAsync() => ShellCommandRunner.RunAsync(DeleteCommandId);

    [RelayCommand(CanExecute = nameof(CanExpandSelected))]
    private Task ExpandToSqlTabAsync() => ShellCommandRunner.RunAsync(ExpandCommandId);

    private bool CanNew() => _session is not null;

    private bool CanEditSelected() => TargetEntry() is { IsEditable: true };

    private bool CanExpandSelected() => TargetEntry() is { IsStatement: true };

    private MyBatisStatementEntry? TargetEntry() => _session?.SelectedEntry;

    private void UpdateCommandStates()
    {
        NewStatementCommand.NotifyCanExecuteChanged();
        RenameStatementCommand.NotifyCanExecuteChanged();
        DeleteStatementCommand.NotifyCanExecuteChanged();
        ExpandToSqlTabCommand.NotifyCanExecuteChanged();
    }
}
