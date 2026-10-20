using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Shell;
using Oranda.Core.Workspaces;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// SQL 작업 공간(오렌지 SQL Tool 창 하나). 문서 영역의 탭 하나이고, 안에 SQL 탭(편집기) 여러 개를 가진다.
/// 작업 공간마다 <c>workspaces/{id}.json</c>(비정상 종료 대비 <c>autosave/{id}.json</c>) 하나로 저장된다.
/// Markdown 작업 공간 파일이나 MyBatis 매퍼를 열면 작업 공간 하나가 된다(<see cref="SourcePath"/>).
/// 탭을 더하고 빼는 일과 활성 탭은 <see cref="SqlEditorHost"/> 가 맡는다(호스트의 <c>Active</c> 가 앱 전체의 「활성 SQL 탭」 신호다).
/// </summary>
public sealed partial class SqlWorkspaceViewModel : ToolPanelViewModelBase, IInnerTabsDocument, Oranda.App.Shell.Docking.IDockTabActions
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

    /// <summary>이 작업 공간의 SQL 탭(열린 순서).</summary>
    public ObservableCollection<SqlEditorViewModel> Tabs { get; } = [];

    /// <summary>이 작업 공간에서 보이는 SQL 탭. 작업 공간이 활성이면 호스트의 활성 탭과 같다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveInnerTitle))]
    private SqlEditorViewModel? _activeTab;

    public string? ActiveInnerTitle => ActiveTab?.Title;

    public bool IsEmpty => Tabs.Count == 0;

    public bool IsDirty => Tabs.Any(t => t.IsDirty);

    /// <summary>문서 탭 머리글: 고친 SQL 탭이 있으면 「* 이름」, 파일에서 연 작업 공간이면 뒤에 종류(「이름 (sqd)」).</summary>
    public string DisplayTitle => (IsDirty ? "* " : string.Empty) + Name + (Kind is { } kind ? $" ({kind})" : string.Empty);

    partial void OnActiveTabChanged(SqlEditorViewModel? value) => Host.OnWorkspaceTabSelected(this, value);

    /// <summary>탭 줄의 「+」: 이 작업 공간에 빈 SQL 탭을 더한다.</summary>
    [RelayCommand]
    private void NewTab()
    {
        Host.ActiveWorkspace = this;
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
        if (tab is not null)
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
        Tabs = [.. Tabs.Select(t => t.ToWorkspaceTab(Host.Binds))],
        SourcePath = SourcePath,
    };

    /// <summary>
    /// 지난 자동 저장 뒤로 바뀐 것이 있으면 true 와 새 표지를 돌려준다. 내용(판·변경 표시)뿐 아니라 작업 공간 이름·원본 파일·
    /// 활성 탭·탭 이름, 탭이 참조하는 바인드 값도 넣는다(이름이나 바인드 값만 바꾸고 죽어도 복구가 옛 값으로 돌아가지 않게).
    /// </summary>
    internal bool ChangedSinceAutosave(out string stamp)
    {
        var sb = new System.Text.StringBuilder()
            .Append(Name).Append('\u0001').Append(SourcePath).Append('\u0001').Append(ActiveTab?.TabId);
        foreach (var t in Tabs)
        {
            sb.Append('\u0002').Append(t.TabId).Append(':').Append(t.Version).Append(':').Append(t.IsDirty).Append(':').Append(t.Title)
                .Append('\u0001').Append(Host.Binds.Fingerprint(t.ReferencedBindNames));
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
