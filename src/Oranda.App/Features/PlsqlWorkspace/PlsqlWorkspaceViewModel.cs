using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.PlsqlRun;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.PlsqlWorkspace;

/// <summary>
/// <c>oranda.panel.plsqlWorkspace</c> — PL/SQL 작업 공간(결정 59 안 B). 문서 영역의 탭 하나이고, 안에 PL/SQL 편집 탭(<see cref="PlsqlEditorViewModel"/>) 여러 개를 가진다.
/// 어느 작업 공간에 탭을 넣을지는 <see cref="PlsqlWorkspaceRouter"/> 가 정한다. 저장·복원은 아직 없다(B-2).
/// 미리보기 탭은 하나뿐이고(머리글 기울임) 고치거나 고정하면 보통 탭이 된다. 마지막 탭을 닫아도 작업 공간은 빈 채로 남는다.
/// </summary>
[OrandaToolPanel("oranda.panel.plsqlWorkspace", ToolPanelKind.Document, DockLocation.Document, AllowMultiple = true)]
public sealed partial class PlsqlWorkspaceViewModel : ToolPanelViewModelBase, IInnerTabsDocument, IPlsqlRunSource
{
    private PlsqlEditorViewModel? _titleSource;

    public PlsqlWorkspaceViewModel()
    {
        Tabs.CollectionChanged += OnTabsChanged;
    }

    /// <summary>저장 파일 ID(<c>plsql-workspaces/{id}.json</c>, <c>^[a-z0-9][a-z0-9-]{0,63}$</c>).</summary>
    public string Id { get; internal set; } = Guid.NewGuid().ToString("n");

    /// <summary>문서 탭 머리글 이름(「PL/SQL 1」).</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = "PL/SQL";

    /// <summary>이 작업 공간이 온(또는 저장한) <c>.plsql-workspace.md</c> 경로. 없으면 null.</summary>
    public string? SourcePath { get; set; }

    /// <summary>파일에 적힌 원래 이름. 열린 작업 공간과 이름이 겹쳐 <see cref="Name"/> 이 바뀌었을 때만 있다(파일에 다시 쓸 때 원래 이름으로 쓴다).</summary>
    public string? SourceName { get; set; }

    /// <summary>이 작업 공간의 PL/SQL 탭(열린 순서).</summary>
    public ObservableCollection<PlsqlEditorViewModel> Tabs { get; } = [];

    /// <summary>보이는 탭.</summary>
    [ObservableProperty]
    public partial PlsqlEditorViewModel? ActiveTab { get; set; }

    /// <summary>미리보기 탭(스키마 브라우저 한 번 누르기). 없으면 null.</summary>
    public PlsqlEditorViewModel? PreviewTab { get; private set; }

    /// <summary>
    /// 미리보기만 담으려고 만든 작업 공간이다. 그 미리보기가 다른 곳으로 옮겨 가 비면 라우터가 문서도 닫는다(빈 작업 공간이 쌓이지 않게).
    /// 보통 탭이 하나라도 들어오면 false.
    /// </summary>
    public bool IsTransient { get; internal set; }

    /// <summary>탭들이 서로 다른 접속을 쓴다(탭 머리에 접속 이름을 보인다).</summary>
    [ObservableProperty]
    public partial bool MixedConnections { get; set; }

    public bool IsEmpty => Tabs.Count == 0;

    /// <summary>닫을 때 저장 여부를 물어야 할 탭이 있다.</summary>
    public bool NeedsSaveConfirm => Tabs.Any(t => t.NeedsSaveConfirm);

    /// <summary>탭이 작업 공간에서 빠졌다(닫음·미리보기 교체). 그 탭의 소스 읽기를 멈추고 활성 탭·컴파일 오류 창을 정리한다.</summary>
    public event EventHandler<PlsqlEditorViewModel>? TabClosed;

    /// <summary>시험용: 닫기 확인에 쓰는 호스트(기본 = <see cref="SqlEditorHost.Current"/>).</summary>
    internal Func<SqlEditorHost>? Host { get; set; }

    public string? ActiveInnerTitle => ActiveTab?.Title;

    /// <summary>저장 형식으로 옮긴다(결정 71). 미리보기 탭은 저장하지 않는다(보이던 탭이 미리보기면 그 자리에 남는 탭을 보이던 탭으로 적는다).</summary>
    public PlsqlWorkspaceFile Capture()
    {
        var tabs = Tabs.Where(t => !ReferenceEquals(t, PreviewTab)).ToList();
        var active = ActiveTab is { } shown && ReferenceEquals(shown, PreviewTab) && tabs.Count > 0
            ? tabs[Math.Min(Tabs.IndexOf(shown), tabs.Count - 1)]
            : ActiveTab;
        return new PlsqlWorkspaceFile
        {
            Id = Id,
            Name = Name,
            SourcePath = SourcePath,
            SourceName = SourceName,
            ActiveTab = active is null ? -1 : tabs.IndexOf(active),
            Tabs = [.. tabs.Select(t => t.Capture())],
        };
    }

    /// <summary>편집기에서 실행(<c>oranda.plsql.run</c>): 보이는 탭의 대상.</summary>
    public DbObjectRef? RunTarget => ActiveTab?.RunTarget;

    public string? RunMember => ActiveTab?.RunMember;

    /// <summary>보이는 탭의 세션(실행 대화상자가 쓴다).</summary>
    public ISession? Session => ActiveTab?.Session;

    partial void OnActiveTabChanged(PlsqlEditorViewModel? value)
    {
        if (_titleSource is not null)
        {
            _titleSource.PropertyChanged -= OnActiveTabPropertyChanged;
        }

        _titleSource = value;
        if (value is not null)
        {
            value.PropertyChanged += OnActiveTabPropertyChanged;
        }

        OnPropertyChanged(nameof(ActiveInnerTitle));
    }

    private void OnActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlsqlEditorViewModel.Title))
        {
            OnPropertyChanged(nameof(ActiveInnerTitle));
        }
    }

    /// <summary>
    /// 탭을 넣고 보이게 한다. <paramref name="preview"/> 면 미리보기 탭: 고치지 않은 미리보기가 있으면 그 자리에서 바꿔 끼우고(옛 탭은 <see cref="TabClosed"/>),
    /// 고친 미리보기는 고정해 두고 새 탭을 더한다.
    /// </summary>
    public void Add(PlsqlEditorViewModel tab, bool preview = false)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (Tabs.Contains(tab))
        {
            ActiveTab = tab;
            return;
        }

        PlsqlEditorViewModel? replaced = null;
        if (preview && PreviewTab is { } old)
        {
            if (old.Editor.IsDirty)
            {
                Pin(old);
            }
            else
            {
                replaced = old;
            }
        }

        if (replaced is not null)
        {
            Detach(replaced);
            Tabs[Tabs.IndexOf(replaced)] = tab;
        }
        else
        {
            Tabs.Add(tab);
        }

        if (preview)
        {
            PreviewTab = tab;
            tab.IsPreviewTab = true;
            tab.Editor.PropertyChanged += OnPreviewEdited;
        }
        else
        {
            IsTransient = false;
        }

        tab.PropertyChanged += OnTabPropertyChanged;
        UpdateMixedConnections();
        ActiveTab = tab;
        if (replaced is not null)
        {
            TabClosed?.Invoke(this, replaced); // 새 탭이 보인 뒤에 알린다(활성 탭이 닫힌 탭을 가리키지 않게)
        }
    }

    /// <summary>미리보기 탭을 보통 탭으로 고정한다. 이미 보통 탭이면 아무것도 안 한다.</summary>
    public void Pin(PlsqlEditorViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!ReferenceEquals(PreviewTab, tab))
        {
            return;
        }

        tab.Editor.PropertyChanged -= OnPreviewEdited;
        tab.IsPreviewTab = false;
        PreviewTab = null;
        IsTransient = false;
    }

    /// <summary>확인 없이 탭을 뺀다. 활성 탭이었으면 옆 탭(오른쪽, 없으면 왼쪽)이 보인다.</summary>
    public void Remove(PlsqlEditorViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        // 탭 줄(ListBox)의 양방향 바인딩이 고른 항목이 빠질 때 ActiveTab 을 먼저 null 로 만들 수 있어, 빼기 전에 활성 여부를 본다.
        var wasActive = ReferenceEquals(ActiveTab, tab);
        Detach(tab);
        Tabs.RemoveAt(index);
        if (wasActive || ActiveTab is null)
        {
            ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        UpdateMixedConnections();
        TabClosed?.Invoke(this, tab);
    }

    /// <summary>탭을 닫는다. 고친 탭은 저장 여부를 묻고(SQL 탭과 같은 규칙), 취소하면 남긴다. 닫았으면 true.</summary>
    public async Task<bool> CloseTabAsync(PlsqlEditorViewModel tab, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!Tabs.Contains(tab))
        {
            return false;
        }

        if (tab.NeedsSaveConfirm)
        {
            ShowForConfirm(tab); // 묻는 탭을 보인다(「다른 탭 닫기」·뒤 문서에서도)
            if (!await tab.ConfirmCloseAsync(ResolveHost(), ct).ConfigureAwait(true))
            {
                return false;
            }
        }

        Remove(tab);
        return true;
    }

    /// <summary>
    /// 작업 공간(문서)을 닫기 전 확인: 고친 탭마다 그 탭을 보이고 차례로 묻는다. 처음 취소에서 멈춘다(false = 닫지 않음).
    /// </summary>
    public async Task<bool> ConfirmCloseAllAsync(SqlEditorHost? host = null, CancellationToken ct = default)
    {
        foreach (var tab in Tabs.Where(t => t.NeedsSaveConfirm).ToList())
        {
            ShowForConfirm(tab);
            if (!await tab.ConfirmCloseAsync(host ?? ResolveHost(), ct).ConfigureAwait(true))
            {
                return false;
            }
        }

        return true;
    }

    [RelayCommand]
    private Task CloseTab(PlsqlEditorViewModel? tab) => tab is null ? Task.CompletedTask : CloseTabAsync(tab);

    /// <summary>탭 머리 문맥 메뉴 「다른 탭 닫기」: 고친 탭은 하나씩 묻고, 취소한 탭은 남긴다.</summary>
    [RelayCommand]
    private async Task CloseOtherTabs(PlsqlEditorViewModel? keep)
    {
        foreach (var tab in Tabs.Where(t => !ReferenceEquals(t, keep)).ToList())
        {
            await CloseTabAsync(tab).ConfigureAwait(true);
        }

        if (keep is not null && Tabs.Contains(keep))
        {
            ActiveTab = keep;
        }
    }

    /// <summary>탭 머리를 두 번 누르면 미리보기 탭을 고정한다.</summary>
    [RelayCommand]
    private void PinTab(PlsqlEditorViewModel? tab)
    {
        if (tab is not null)
        {
            Pin(tab);
        }
    }

    /// <summary>탭 줄의 「+」: 이 작업 공간에 새 PL/SQL 탭(<c>oranda.file.newPlsqlEditor</c> 와 같은 길).</summary>
    [RelayCommand]
    private Task NewTab() => NewTabRequested?.Invoke(this) ?? Task.CompletedTask;

    /// <summary>「+」를 눌렀을 때 실행할 일(라우터가 넣는다: 새 PL/SQL 명령을 이 작업 공간 대상으로).</summary>
    internal Func<PlsqlWorkspaceViewModel, Task>? NewTabRequested { get; set; }

    /// <summary>저장 확인 전에 이 작업 공간 문서를 앞으로 내는 방법(라우터가 넣는다).</summary>
    internal Action<PlsqlWorkspaceViewModel>? BringToFront { get; set; }

    public async Task<bool> CloseActiveInnerTabAsync(CancellationToken ct)
    {
        // 탭 줄에서 선택이 풀려 ActiveTab 이 비어도 탭이 남아 있으면 false 를 돌려주면 안 된다: 셸이 작업 공간째 닫는다.
        if ((ActiveTab ?? Tabs.LastOrDefault()) is not { } tab)
        {
            return false;
        }

        await CloseTabAsync(tab, ct).ConfigureAwait(true);
        return true;
    }

    public bool CycleInnerTab(int offset)
    {
        if (ActiveTab is null)
        {
            return false;
        }

        var next = Tabs.IndexOf(ActiveTab) + offset;
        if (next < 0 || next >= Tabs.Count)
        {
            return false;
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

    private SqlEditorHost ResolveHost() => (Host ?? (() => SqlEditorHost.Current))();

    private void ShowForConfirm(PlsqlEditorViewModel tab)
    {
        ActiveTab = tab;
        BringToFront?.Invoke(this);
    }

    private void Detach(PlsqlEditorViewModel tab)
    {
        tab.PropertyChanged -= OnTabPropertyChanged;
        if (ReferenceEquals(PreviewTab, tab))
        {
            tab.Editor.PropertyChanged -= OnPreviewEdited;
            PreviewTab = null;
        }

        tab.IsPreviewTab = false;
    }

    /// <summary>미리보기 탭에서 글을 고치기 시작하면 고정한다(소스를 읽어 채운 것은 고침이 아니다).</summary>
    private void OnPreviewEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SqlEditorViewModel.IsDirty) && PreviewTab is { Editor.IsDirty: true } tab && ReferenceEquals(sender, tab.Editor))
        {
            Pin(tab);
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlsqlEditorViewModel.Session))
        {
            UpdateMixedConnections();
        }
    }

    private void UpdateMixedConnections() =>
        MixedConnections = Tabs.Select(t => t.Session?.Profile.Id).Where(id => id is not null).Distinct().Skip(1).Any();

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnPropertyChanged(nameof(IsEmpty));
}
