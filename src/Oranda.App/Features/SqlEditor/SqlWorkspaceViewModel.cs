using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.Core.Workspaces;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// SQL 작업 공간(오렌지 SQL Tool 창 하나). 문서 영역의 탭 하나이고, 안에 SQL 탭(편집기) 여러 개를 가진다.
/// 작업 공간마다 <c>workspaces/{id}.json</c>(비정상 종료 대비 <c>autosave/{id}.json</c>) 하나로 저장된다.
/// Markdown 작업 공간 파일이나 MyBatis 매퍼를 열면 작업 공간 하나가 된다(<see cref="SourcePath"/>).
/// 탭을 더하고 빼는 일과 활성 탭은 <see cref="SqlEditorHost"/> 가 맡는다(호스트의 <c>Active</c> 가 앱 전체의 「활성 SQL 탭」 신호다).
/// </summary>
public sealed partial class SqlWorkspaceViewModel : ToolPanelViewModelBase, IInnerTabsDocument, IDockTabActions, IToolScopeDocument
{
    private string? _autosaveStamp;

    internal SqlWorkspaceViewModel(SqlEditorHost host, string id, string name)
    {
        Host = host;
        Id = id;
        _name = name;
        Tabs.CollectionChanged += OnTabsChanged;
    }

    public SqlEditorHost Host { get; }

    /// <summary>파일 ID(<c>^[a-z0-9][a-z0-9-]{0,63}$</c>). 「작업 공간 저장」으로 이름을 바꾸면 함께 바뀐다.</summary>
    public string Id { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    private string _name;

    private string? _sourcePath;

    /// <summary>이 작업 공간을 연 파일(Markdown 작업 공간 .md, MyBatis 매퍼 .xml, Orange .sqd). 파일에서 열지 않았으면 null.</summary>
    public string? SourcePath
    {
        get => _sourcePath;
        internal set
        {
            if (SetProperty(ref _sourcePath, value))
            {
                OnPropertyChanged(nameof(Kind));
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    /// <summary>문서 탭에 붙는 원본 종류: md·sqd·MyBatis. 파일 없이 앱 안에만 저장되는 작업 공간이면 null.</summary>
    public string? Kind => WorkspaceFiles.KindOf(SourcePath);

    private IMyBatisWorkspaceSession? _myBatis;

    /// <summary>
    /// 이 작업 공간에 붙은 MyBatis 세션(design.md 2.1). 열기 훅(03-01)만 붙이고 떼며, 지금은 아무도 붙이지 않는다.
    /// 붙이거나 떼면 <see cref="DocumentKind"/>·<see cref="ToolScope"/> 변경을 알리고 <see cref="ToolScopeChanged"/> 를 낸다 —
    /// 같은 Dock 문서 안의 변화라 활성 문서 이벤트가 오지 않으므로, 이 신호가 도구 창 보임 규칙의 전환 신호다.
    /// </summary>
    public IMyBatisWorkspaceSession? MyBatis
    {
        get => _myBatis;
        internal set
        {
            var old = _myBatis;
            if (SetProperty(ref _myBatis, value))
            {
                // 세션이 바뀌면 옛 세션 구독을 풀고 새 세션의 StateChanged 를 받는다(저장 안 한 상태·문장 목록이 문서 탭 머리·dirty 에 반영된다).
                if (old is not null)
                {
                    old.StateChanged -= OnSessionStateChanged;
                }

                if (value is not null)
                {
                    value.StateChanged += OnSessionStateChanged;
                }

                OnPropertyChanged(nameof(DocumentKind));
                OnPropertyChanged(nameof(ToolScope));
                OnPropertyChanged(nameof(IsDirty));
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(IsTabRenameAllowed));
                ToolScopeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(DisplayTitle));
    }

    /// <summary>탭 이름을 바꿀 수 있는가: MyBatis 문서의 문장 탭 이름은 문장 id 라 바꾸지 않는다(문장 이름은 「이름 바꾸기」 명령).</summary>
    public bool IsTabRenameAllowed => MyBatis is null;

    /// <summary>문서 종류: MyBatis 세션이 붙어 있으면 <see cref="SqlWorkspaceKind.MyBatis"/>, 아니면 <see cref="SqlWorkspaceKind.Sql"/>.</summary>
    public SqlWorkspaceKind DocumentKind => MyBatis is null ? SqlWorkspaceKind.Sql : SqlWorkspaceKind.MyBatis;

    /// <summary>Shell 도구 창 보임 규칙이 보는 범위 글자(<see cref="IToolScopeDocument"/>). MyBatis 문서면 <c>"mybatis"</c>, 아니면 null.</summary>
    public string? ToolScope => DocumentKind == SqlWorkspaceKind.MyBatis ? "mybatis" : null;

    /// <summary><see cref="ToolScope"/> 가 바뀌었다(<see cref="MyBatis"/> 를 붙이거나 뗐다).</summary>
    public event EventHandler? ToolScopeChanged;

    /// <summary>
    /// 이 작업 공간이 쓸 세션(TSK-14-01, <c>SessionRouter</c> 가 고른다): 자기 세션 / 접속의 세션을 함께 씀. null = 설정을 따른다.
    /// 작업 공간 파일에 남는다(세션 번호가 아니라 이 선택만).
    /// </summary>
    [ObservableProperty]
    private WorkspaceSessionMode? _sessionMode;

    /// <summary>함께 쓰는 세션이 다른 작업 공간에서 실행 중이라 보이는 안내 막대(TSK-14-03, <c>SessionRouter</c> 가 넣고 지운다). 없으면 null.</summary>
    [ObservableProperty]
    private SessionBusyNotice? _busyNotice;

    /// <summary>이 작업 공간의 SQL 탭(열린 순서).</summary>
    public ObservableCollection<SqlEditorViewModel> Tabs { get; } = [];

    /// <summary>이 작업 공간에서 보이는 SQL 탭. 작업 공간이 활성이면 호스트의 활성 탭과 같다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveInnerTitle))]
    private SqlEditorViewModel? _activeTab;

    public string? ActiveInnerTitle => ActiveTab?.Title;

    public bool IsEmpty => Tabs.Count == 0;

    public bool IsDirty => Tabs.Any(t => t.IsDirty) || MyBatis?.IsDirty == true;

    /// <summary>문서 탭 머리글: 고친 SQL 탭이 있으면 「* 이름」, 파일에서 연 작업 공간이면 뒤에 종류(「이름 (sqd)」).</summary>
    public string DisplayTitle => (IsDirty ? "* " : string.Empty) + Name + (Kind is { } kind ? $" ({kind})" : string.Empty);

    partial void OnActiveTabChanged(SqlEditorViewModel? value) => Host.OnWorkspaceTabSelected(this, value);

    /// <summary>
    /// 탭 줄의 「+」: 이 작업 공간에 빈 SQL 탭을 더한다. MyBatis 문서면 빈 탭 대신 「+ 새 문장」(종류·id 묻기, design.md 2.2)으로 간다 —
    /// 문장 탭은 문장 id 로 만들어지므로 일반 새 문서로 가지 않는다.
    /// </summary>
    [RelayCommand]
    private async Task NewTabAsync()
    {
        Host.ActiveWorkspace = this;
        if (MyBatis is { } session)
        {
            await session.BeginNewStatementAsync(CancellationToken.None).ConfigureAwait(true);
            return;
        }

        Host.NewDocument();
    }

    /// <summary>탭 머리글의 ×.</summary>
    [RelayCommand]
    private async Task CloseTabAsync(SqlEditorViewModel? tab)
    {
        if (tab is not null)
        {
            await Host.CloseAsync(tab, CancellationToken.None).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void BeginRenameTab(SqlEditorViewModel? tab)
    {
        if (tab is not null && MyBatis is null) // design.md 2.2: MyBatis 문장 탭 이름은 문장 id 다
        {
            ActiveTab = tab;
            tab.BeginRename();
        }
    }

    [RelayCommand]
    private async Task CloseOtherTabsAsync(SqlEditorViewModel? tab)
    {
        if (tab is not null)
        {
            await Host.CloseTabsAsync(tab, rightOnly: false, CancellationToken.None).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task CloseRightTabsAsync(SqlEditorViewModel? tab)
    {
        if (tab is not null)
        {
            await Host.CloseTabsAsync(tab, rightOnly: true, CancellationToken.None).ConfigureAwait(true);
        }
    }

    public Task RenameFromTabAsync(CancellationToken ct) => Host.RenameWorkspaceAsync(this, ct);

    public Task CloseFromTabAsync(CancellationToken ct) => Host.TryCloseWorkspaceAsync(this, ct);

    public async Task<bool> CloseActiveInnerTabAsync(CancellationToken ct)
    {
        // 활성 탭이 잠깐 비어도 탭이 남아 있으면 true: false 는 셸이 작업 공간을 가드 없이 닫는 신호다.
        if ((ActiveTab ?? Tabs.LastOrDefault()) is not { } tab)
        {
            return false;
        }

        await Host.CloseAsync(tab, ct).ConfigureAwait(true);
        return true;
    }

    public bool CycleInnerTab(int offset)
    {
        if (ActiveTab is not { } tab)
        {
            return false;
        }

        var next = Tabs.IndexOf(tab) + offset;
        if (next < 0 || next >= Tabs.Count)
        {
            return false; // 끝을 넘으면 셸이 다음 문서(작업 공간)로 넘어간다
        }

        ActiveTab = Tabs[next];
        return true;
    }

    public void SelectEdgeInnerTab(bool first)
    {
        if (Tabs.Count > 0)
        {
            ActiveTab = first ? Tabs[0] : Tabs[^1];
        }
    }

    /// <summary>저장할 모습(<c>workspaces/{id}.json</c>·자동 저장 같은 형식).</summary>
    public Workspace ToWorkspace() => new()
    {
        Id = Id,
        Name = Name,
        SavedAt = DateTimeOffset.UtcNow,
        ActiveTabId = ActiveTab?.TabId,
        Tabs = [.. Tabs.Select(t => t.ToWorkspaceTab())],
        SourcePath = SourcePath,
        Session = WorkspaceSessionModes.Format(SessionMode),
    };

    /// <summary>
    /// 지난 자동 저장 뒤로 바뀐 것이 있으면 true 와 새 표지를 돌려준다. 내용(판·변경 표시)뿐 아니라 작업 공간 이름·원본 파일·
    /// 활성 탭·탭 이름, 탭이 참조하는 바인드 값, 세션 방식(<see cref="SessionMode"/>, TSK-14-02)도 넣는다(이름이나 바인드 값만 바꾸고 죽어도 복구가 옛 값으로 돌아가지 않게).
    /// </summary>
    internal bool ChangedSinceAutosave(out string stamp)
    {
        var sb = new System.Text.StringBuilder()
            .Append(Name).Append('\u0001').Append(SourcePath).Append('\u0001').Append(ActiveTab?.TabId)
            .Append('\u0001').Append(WorkspaceSessionModes.Format(SessionMode));
        foreach (var t in Tabs)
        {
            sb.Append('\u0002').Append(t.TabId).Append(':').Append(t.Version).Append(':').Append(t.IsDirty).Append(':').Append(t.Title)
                .Append('\u0001').Append(t.Binds.Fingerprint(t.ReferencedBindNames));
        }

        if (MyBatis is not null)
        {
            // design.md 5.6: MyBatis 쪽(고침·고른 문장·감시 기준)만 바뀌어도 자동 저장이 돌게 복구 참여자의 표지를 더한다.
            foreach (var participant in Host.MyBatisHooks.RecoveryParticipants)
            {
                sb.Append('\u0003').Append(participant.Key).Append(':').Append(participant.Fingerprint(this));
            }
        }

        stamp = sb.ToString();
        return !string.Equals(_autosaveStamp, stamp, StringComparison.Ordinal);
    }

    internal void MarkAutosaved(string? stamp) => _autosaveStamp = stamp;

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var t in e.OldItems?.OfType<SqlEditorViewModel>() ?? [])
        {
            t.PropertyChanged -= OnTabPropertyChanged;
        }

        foreach (var t in e.NewItems?.OfType<SqlEditorViewModel>() ?? [])
        {
            t.PropertyChanged += OnTabPropertyChanged;
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(DisplayTitle));
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SqlEditorViewModel.IsDirty))
        {
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(DisplayTitle));
        }
        else if (e.PropertyName == nameof(SqlEditorViewModel.Title) && ReferenceEquals(sender, ActiveTab))
        {
            OnPropertyChanged(nameof(ActiveInnerTitle));
        }
    }
}
