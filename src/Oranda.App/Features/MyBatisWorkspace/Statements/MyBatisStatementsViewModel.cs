using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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

    [ObservableProperty]
    private object? _modeContent;

    /// <summary>목록·도구 막대를 보인다: 세션이 있거나 쓸 수 있는 보기 모드(예 매퍼 폴더 트리)가 있다. 아니면 안내 한 줄만 보인다.</summary>
    [ObservableProperty]
    private bool _showsBody;

    /// <summary>다루는 호스트. 기본은 <see cref="SqlEditorHost.Current"/>.</summary>
    public SqlEditorHost Host
    {
        get => _host ?? SqlEditorHost.Current;
        init => _host = value;
    }

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

    // ---- 활성 작업 공간 · 세션 따라가기 ------------------------------------------------------

    private void OnActiveWorkspaceChanged(object? sender, EventArgs e) => FollowWorkspace();

    /// <summary>
    /// 활성 작업 공간의 세션을 따라간다. 열기 훅은 작업 공간을 활성으로 만든 <b>뒤에</b> 세션을 붙이므로
    /// 작업 공간의 <c>MyBatis</c> 속성 변경도 함께 받아야 한다.
    /// </summary>
    private void FollowWorkspace()
    {
        var ws = Host.ActiveWorkspace;
        if (!ReferenceEquals(_workspace, ws))
        {
            if (_workspace is not null)
            {
                _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
            }

            _workspace = ws;
            if (ws is not null)
            {
                ws.PropertyChanged += OnWorkspacePropertyChanged;
            }
        }

        SetSession(ws?.MyBatis as MyBatisWorkspaceSession);
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SqlWorkspaceViewModel.MyBatis))
        {
            SetSession(_workspace?.MyBatis as MyBatisWorkspaceSession);
        }
    }

    private void SetSession(MyBatisWorkspaceSession? session)
    {
        if (ReferenceEquals(_session, session))
        {
            return;
        }

        if (_session is not null)
        {
            _session.StateChanged -= OnSessionStateChanged;
            UnhookProviders();
        }

        _session = session;
        MutateRows(() =>
        {
            SelectedNode = null;
            _groups.Clear();
            _rows.Clear();
        });
        _shown = [];
        if (session is null)
        {
            UpdateHeader();
            HasSession = false;
            UpdateCommandStates();
            return;
        }

        session.StateChanged += OnSessionStateChanged;
        HookProviders();
        EnsureModes();
        UpdateHeader();
        Rebuild();
        HasSession = true;
        SyncSelection();
    }

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        UpdateHeader();
        if (_session.VisibleEntries.SequenceEqual(_shown))
        {
            RefreshNodes();
            ScheduleUsage();
        }
        else
        {
            Rebuild();
        }

        SyncSelection();
    }

    private void UpdateHeader()
    {
        var path = _session?.MapperPath ?? string.Empty;
        MapperPath = path;
        MapperFileName = Path.GetFileName(path);
        var directory = Path.GetDirectoryName(path);
        MapperDirectory = string.IsNullOrEmpty(directory) ? string.Empty : directory + Path.DirectorySeparatorChar;
        NamespaceText = _session is null ? string.Empty : _session.MapperNamespace ?? T("namespaceNone");
    }

    // ---- 목록 짓기 · 표시 고치기 ----------------------------------------------------------------

    private void Rebuild()
    {
        if (_session is null)
        {
            return;
        }

        var entries = _session.VisibleEntries.ToList();
        _shown = entries;
        _groups.Clear();
        var rows = new List<MyBatisStatementNode>(entries.Count + GroupOrder.Length);
        foreach (var kind in GroupOrder)
        {
            var members = entries.Where(e => e.Kind == kind).ToList();
            if (members.Count == 0)
            {
                continue;
            }

            var group = new MyBatisStatementNode(GroupTitle(kind), kind, entry: null)
            {
                IsExpanded = !_collapsed.Contains(kind),
                ExpandedChangedCallback = OnGroupExpandedChanged,
            };
            foreach (var entry in members)
            {
                var node = new MyBatisStatementNode(string.Empty, kind, entry);
                RefreshNode(node);
                RefreshBadges(node);
                group.Children.Add(node);
            }

            _groups.Add(group);
            rows.Add(group);
            if (group.IsExpanded)
            {
                rows.AddRange(group.Children);
            }
        }

        MutateRows(() => _rows.ReplaceAll(rows));
        RefreshUsage();
    }

    private void RefreshNodes()
    {
        foreach (var group in _groups)
        {
            foreach (var node in group.Children)
            {
                RefreshNode(node);
                if (!string.Equals(node.BadgedId, node.Entry!.Id, StringComparison.Ordinal))
                {
                    RefreshBadges(node);
                }
            }
        }
    }

    /// <summary>요소 노드의 표시 글(수정·새 문장·편집 불가·그대로 둠)을 맞춘다. 값이 같으면 알림이 나가지 않는다.</summary>
    private void RefreshNode(MyBatisStatementNode node)
    {
        var entry = node.Entry!;
        node.Title = entry.Id ?? $"<{entry.Name}>";
        node.ModifiedText = entry.IsModified && !entry.IsNew ? T("mark.modified") : string.Empty;
        node.NewText = entry.IsNew ? T("mark.new") : string.Empty;
        node.AmbiguousText = entry.IsAmbiguous ? T("mark.ambiguous") : string.Empty;
        node.KeepText = entry.CanOpen ? string.Empty : T("mark.keep");
        node.RowOpacity = entry.CanOpen ? 1 : 0.6;
        node.ToolTipText = entry.IsAmbiguous ? T("tip.ambiguous") : entry.CanOpen ? null : T("tip.keep");
    }

    private void RefreshBadges(MyBatisStatementNode node)
    {
        var entry = node.Entry!;
        node.BadgedId = entry.Id;
        var badges = new List<MyBatisStatementBadgeView>();
        if (_session is not null && entry.Id is { } id && entry.CanOpen)
        {
            foreach (var provider in _providers)
            {
                foreach (var badge in provider.BadgesFor(_session, id))
                {
                    badges.Add(new MyBatisStatementBadgeView(badge.Text, badge.ToolTip, badge.Kind));
                }
            }
        }

        if (!node.Badges.SequenceEqual(badges))
        {
            node.Badges = badges;
        }
    }

    /// <summary>sql 조각 노드의 「n곳에서 씀」. 매퍼 전체 글을 훑으므로 글자를 칠 때는 잠깐 기다렸다가 한 번만 한다.</summary>
    private void RefreshUsage()
    {
        _usageTimer?.Stop();
        if (_session is null)
        {
            return;
        }

        var counts = _session.UsageCounts(); // 매퍼를 한 번만 훑는다(조각 수 × 글 길이가 되지 않게)
        foreach (var group in _groups.Where(g => g.Kind == MyBatisStatementKind.Sql))
        {
            foreach (var node in group.Children)
            {
                node.UsageText = node.Entry!.Id is { } id ? UsageText(counts.GetValueOrDefault(id)) : string.Empty;
            }
        }
    }

    private string UsageText(int count) =>
        count == 0 ? T("mark.unused") : string.Format(CultureInfo.CurrentCulture, T("mark.usage"), count);

    private void ScheduleUsage()
    {
        if (!_groups.Any(g => g.Kind == MyBatisStatementKind.Sql))
        {
            return;
        }

        if (_usageTimer is null)
        {
            _usageTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _usageTimer.Tick += (_, _) => RefreshUsage();
        }

        _usageTimer.Stop();
        _usageTimer.Start();
    }

    private void OnGroupExpandedChanged(MyBatisStatementNode group)
    {
        if (group.IsExpanded)
        {
            _collapsed.Remove(group.Kind);
        }
        else
        {
            _collapsed.Add(group.Kind);
        }

        int header = _rows.IndexOf(group);
        if (header < 0)
        {
            return;
        }

        if (group.IsExpanded)
        {
            MutateRows(() => _rows.InsertRange(header + 1, group.Children));
            SyncSelection(); // 접혀 있는 동안 목록에서 빠졌던 고른 문장을 다시 보인다
        }
        else
        {
            MutateRows(() =>
            {
                _rows.RemoveRange(header + 1, group.Children.Count);
                if (SelectedNode is { IsGroup: false } picked && picked.Kind == group.Kind)
                {
                    SelectedNode = null; // 보이지 않는 줄은 목록 선택에서 빠진다(세션의 고른 문장은 그대로)
                }
            });
        }
    }

    /// <summary>
    /// 줄 목록을 바꾼다. 화면이 줄을 빼면서 목록 선택을 비우는 것(양방향 연결)은 사용자가 고른 것이 아니므로 세션에 알리지 않는다.
    /// </summary>
    private void MutateRows(Action change)
    {
        bool was = _syncing;
        _syncing = true;
        try
        {
            change();
        }
        finally
        {
            _syncing = was;
        }
    }

    /// <summary>세션의 고른 문장을 목록에 맞춘다(활성 탭을 따라 바뀐 경우 포함). 세션에 다시 알리지 않는다.</summary>
    private void SyncSelection()
    {
        if (_session is null)
        {
            return;
        }

        var picked = _session.SelectedEntry;
        MyBatisStatementNode? target = null;
        if (picked is not null)
        {
            target = _groups.SelectMany(g => g.Children).FirstOrDefault(n => ReferenceEquals(n.Entry, picked));
            if (target is null || _collapsed.Contains(target.Kind))
            {
                UpdateCommandStates();
                return; // 목록에 없거나 접힌 묶음 안인 문장은 목록 선택을 건드리지 않는다
            }
        }
        else if (SelectedNode is not { IsGroup: false })
        {
            UpdateCommandStates();
            return;
        }

        if (!ReferenceEquals(SelectedNode, target))
        {
            _syncing = true;
            try
            {
                SelectedNode = target;
            }
            finally
            {
                _syncing = false;
            }
        }

        UpdateCommandStates();
    }

    // ---- 배지 · 보기 모드 --------------------------------------------------------------------

    private void HookProviders()
    {
        _providers = [.. _session!.Workspace.Host.MyBatisHooks.BadgeProviders];
        foreach (var provider in _providers)
        {
            provider.BadgesChanged += OnBadgesChanged;
        }
    }

    private void UnhookProviders()
    {
        foreach (var provider in _providers)
        {
            provider.BadgesChanged -= OnBadgesChanged;
        }

        _providers = [];
    }

    private void OnBadgesChanged(object? sender, EventArgs e)
    {
        foreach (var group in _groups)
        {
            foreach (var node in group.Children)
            {
                RefreshBadges(node);
            }
        }
    }

    // 보기 모드는 세션과 상관없이 한 번 싣고 세션이 바뀌어도 둔다(매퍼 폴더 트리는 MyBatis 문서 없이도 쓴다).
    private void EnsureModes()
    {
        if (_modesLoaded || Host.MyBatisHooks.ViewModes is not { Count: > 0 } modes)
        {
            return;
        }

        _modesLoaded = true;
        foreach (var mode in modes)
        {
            mode.AvailabilityChanged += OnModeAvailabilityChanged;
            _modes.Add(new MyBatisStatementsModeButton(mode, TranslateTitle(mode.Title)) { CheckedChangedCallback = OnModeCheckedChanged });
        }

        UpdateShowsBody();
    }

    private void UpdateShowsBody() => ShowsBody = _hasSession || _modes.Any(m => m.IsAvailable);

    private void ClearModes()
    {
        foreach (var button in _modes)
        {
            button.Mode.AvailabilityChanged -= OnModeAvailabilityChanged;
        }

        _modes.Clear();
        _modesLoaded = false;
        ModeContent = null;
        IsModeActive = false;
        UpdateShowsBody();
    }

    private void OnModeAvailabilityChanged(object? sender, EventArgs e)
    {
        foreach (var button in _modes.Where(b => ReferenceEquals(b.Mode, sender)).ToList())
        {
            bool was = button.IsAvailable;
            button.IsAvailable = button.Mode.IsAvailable;
            if (!button.IsAvailable && button.IsChecked)
            {
                button.IsChecked = false;
            }
            else if (button.IsAvailable && !was)
            {
                button.IsChecked = true; // 쓸 수 있게 되면(예 매퍼 폴더를 열면) 그 모드로 간다
            }
        }

        UpdateShowsBody();
    }

    private void OnModeCheckedChanged(MyBatisStatementsModeButton button)
    {
        if (_modeSync)
        {
            return;
        }

        if (button.IsChecked)
        {
            _modeSync = true;
            try
            {
                foreach (var other in _modes.Where(m => !ReferenceEquals(m, button)))
                {
                    other.IsChecked = false;
                }
            }
            finally
            {
                _modeSync = false;
            }

            ModeContent = button.View;
            IsModeActive = true;
        }
        else if (!_modes.Any(m => m.IsChecked))
        {
            ModeContent = null;
            IsModeActive = false;
        }
    }

    // ---- 글자 -----------------------------------------------------------------------------------

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var group in _groups)
        {
            group.Title = GroupTitle(group.Kind);
            foreach (var node in group.Children)
            {
                RefreshNode(node);
            }
        }

        foreach (var button in _modes)
        {
            button.Title = TranslateTitle(button.Mode.Title);
        }

        UpdateHeader();
        RefreshUsage();
    }

    private string GroupTitle(MyBatisStatementKind kind) => kind switch
    {
        MyBatisStatementKind.Sql => T("group.sql"),
        MyBatisStatementKind.Other => T("group.other"),
        _ => MyBatisStatementEntry.KindName(kind), // select·insert·update·delete·resultMap 은 XML 요소 이름이라 번역하지 않는다
    };

    private string T(string key)
    {
        var full = KeyPrefix + key;
        return ActiveText?.Lookup(full) ?? StringCatalog.Get("ko", full) ?? full;
    }

    /// <summary>보기 모드 이름은 한국어 화면 글로 오므로 영어 화면이면 리터럴 표(literals-en.json)로 바꾼다.</summary>
    private string TranslateTitle(string title) =>
        ActiveText?.Language == "en" && LiteralTranslator.Table.TryGetValue(title, out var english) ? english : title;
}
