using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Storage;
using Oranda.Core.Workspaces;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// 열린 SQL 작업 공간과 그 안의 SQL 탭 모음, 그리고 그 수명(열기·저장·닫기·복제·작업 공간·자동 저장·비정상 종료 복구).
/// 오렌지처럼 작업 공간(SQL Tool 창) 여러 개를 열고, 작업 공간마다 SQL 탭 여러 개를 둔다(<see cref="Workspaces"/>).
/// <see cref="Tabs"/> 는 모든 작업 공간의 탭을 한데 모은 목록이고, <see cref="Active"/> 는 활성 작업 공간의 활성 탭이다:
/// 명령 처리기·결과 창·개요 창·<c>IEditorContext</c> 는 작업 공간을 몰라도 이 둘만 보면 된다.
/// DI 가 붙기 전에는 <see cref="Current"/> 를 쓴다.
/// UI 스레드에서만 쓴다(자동 저장 타이머는 스냅샷을 UI 스레드에서 만든다).
/// </summary>
public sealed partial class SqlEditorHost : ObservableObject, IDisposable
{
    private const int ClosedHistoryLimit = 20;

    /// <summary>처음 만드는 작업 공간(예전 단일 작업 공간과 같은 파일).</summary>
    public const string DefaultWorkspaceId = "default";

    public const string DefaultWorkspaceName = "기본";

    private static SqlEditorHost? s_current;

    private IWorkspaceStore? _store;
    private readonly Stack<WorkspaceTab> _closed = new();
    private readonly List<CloseGuardEntry> _closeGuards = [];
    private bool _guardsRunning;
    private CancellationTokenSource? _autosaveCts;
    private SqlEditorViewModel? _active;
    private SqlWorkspaceViewModel? _activeWorkspace;

    public SqlEditorHost(IWorkspaceStore? store = null, IEditorDialogs? dialogs = null)
    {
        _store = store;
        Dialogs = dialogs ?? new NoDialogs();
        // 저장 확인은 언제나 첫 번째 가드다(외부 가드는 이 뒤에 등록되므로 저장 확인이 먼저 끝난다).
        RegisterCloseGuard(ConfirmSaveAsync);
    }

    /// <summary>프로세스 안의 기본 호스트. 셸의 DI 가 붙으면 그쪽이 <c>Current</c> 를 설정한다.</summary>
    public static SqlEditorHost Current
    {
        get => s_current ??= new SqlEditorHost();
        set => s_current = value;
    }

    public IEditorDialogs Dialogs { get; set; }

    /// <summary>작업 공간 저장소. 셸이 시작 작업에서 넣는다(<see cref="Current"/> 는 DI 전에 만들어지므로).</summary>
    public IWorkspaceStore? Store
    {
        get => _store;
        set => _store = value;
    }

    /// <summary>열린 작업 공간(문서 탭 순서).</summary>
    public ObservableCollection<SqlWorkspaceViewModel> Workspaces { get; } = [];

    /// <summary>모든 작업 공간의 SQL 탭(작업 공간 순서, 그 안에서는 열린 순서).</summary>
    public ObservableCollection<SqlEditorViewModel> Tabs { get; } = [];

    /// <summary>보고 있는 작업 공간의 ID(없으면 기본 작업 공간 ID). 자동 저장 파일 이름도 같다.</summary>
    public string WorkspaceId => _activeWorkspace?.Id ?? DefaultWorkspaceId;

    public string WorkspaceName => _activeWorkspace?.Name ?? DefaultWorkspaceName;

    /// <summary>보고 있는 작업 공간이 Markdown 작업 공간 파일에서 왔거나 그리로 저장됐으면 그 경로. 「작업 공간 파일로 저장」은 여기에 덮어쓴다.</summary>
    public string? WorkspaceFilePath => _activeWorkspace?.SourcePath is { } p && !WorkspaceFiles.IsReadOnlySource(p) ? p : null;

    /// <summary>시작 때 마지막에 열려 있던 작업 공간들을 다시 연다(설정 <c>workspace.restoreOnStartup</c>).</summary>
    public bool RestoreOnStartup { get; set; } = true;

    /// <summary>시작 때 다시 열 작업 공간 ID 들(설정 <c>workspace.openWorkspaceIds</c>, 셸이 넣는다).</summary>
    public IReadOnlyList<string> OpenWorkspaceIds { get; set; } = [DefaultWorkspaceId];

    /// <summary>시작 때 활성으로 둘 작업 공간(설정 <c>workspace.lastWorkspaceId</c>).</summary>
    public string? LastWorkspaceId { get; set; }

    /// <summary>보고 있는 작업 공간. 바꾸면 <see cref="Active"/> 도 그 작업 공간의 활성 탭으로 바뀐다.</summary>
    public SqlWorkspaceViewModel? ActiveWorkspace
    {
        get => _activeWorkspace;
        set
        {
            if (ReferenceEquals(_activeWorkspace, value))
            {
                return;
            }

            _activeWorkspace = value;
            SetActive(value?.ActiveTab); // 알림 받는 쪽이 Active 를 읽으면 이미 새 작업 공간의 탭이다
            RaiseActiveWorkspaceChanged();
        }
    }

    /// <summary>활성 SQL 탭(활성 작업 공간의 활성 탭). 다른 작업 공간의 탭을 넣으면 그 작업 공간이 활성이 된다.</summary>
    public SqlEditorViewModel? Active
    {
        get => _active;
        set
        {
            var switched = false;
            if (value is not null && WorkspaceOf(value) is { } ws)
            {
                ws.ActiveTab = value;
                if (!ReferenceEquals(_activeWorkspace, ws))
                {
                    _activeWorkspace = ws;
                    switched = true;
                }
            }

            SetActive(value);
            if (switched)
            {
                RaiseActiveWorkspaceChanged();
            }
        }
    }

    public event EventHandler? ActiveChanged;

    public event EventHandler? ActiveWorkspaceChanged;

    public event EventHandler<SqlWorkspaceViewModel>? WorkspaceOpened;

    public event EventHandler<SqlWorkspaceViewModel>? WorkspaceClosed;

    /// <summary>저장된 작업 공간 ID 목록을 돌려주는 함수(셸이 <c>IAppDataPaths</c> 로 채운다). 기본은 빈 목록.</summary>
    public Func<IReadOnlyList<string>> ListWorkspaceIds { get; set; } = () => [];

    /// <summary>개요 창을 보여 달라는 요청(<c>oranda.nav.documentOutline</c>). 셸이 받아 도구 창을 연다.</summary>
    public event EventHandler? OutlineRequested;

    public void RequestOutline() => OutlineRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler<SqlEditorViewModel>? TabOpened;

    public event EventHandler<SqlEditorViewModel>? TabClosed;

    private void RaiseActiveWorkspaceChanged()
    {
        OnPropertyChanged(nameof(ActiveWorkspace));
        OnPropertyChanged(nameof(WorkspaceId));
        OnPropertyChanged(nameof(WorkspaceName));
        ActiveWorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>탭이 들어 있는 작업 공간.</summary>
    public SqlWorkspaceViewModel? WorkspaceOf(SqlEditorViewModel tab) =>
        Workspaces.FirstOrDefault(w => w.Tabs.Contains(tab));

    private void SetActive(SqlEditorViewModel? value)
    {
        if (ReferenceEquals(_active, value))
        {
            return;
        }

        _active = value;
        OnPropertyChanged(nameof(Active));
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>작업 공간의 탭 줄에서 탭을 골랐다. 그 작업 공간이 활성이면 앱의 활성 탭도 바꾼다.</summary>
    internal void OnWorkspaceTabSelected(SqlWorkspaceViewModel workspace, SqlEditorViewModel? tab)
    {
        if (ReferenceEquals(_activeWorkspace, workspace))
        {
            SetActive(tab);
        }
    }

    /// <summary>셸의 <c>IContextKeys</c> 가 읽을 편집기 쪽 상태 키(commands.md 2.2).</summary>
    public IReadOnlyDictionary<string, bool> GetContextKeys()
    {
        var a = Active;
        return new Dictionary<string, bool>
        {
            ["hasDocument"] = a is not null,
            ["sqlDocActive"] = a is not null,
            ["sqlDocOpen"] = Tabs.Count > 0,
            ["hasSelection"] = a?.HasSelection ?? false,
            ["canUndo"] = a?.CanUndo ?? false,
            ["canRedo"] = a?.CanRedo ?? false,
            ["hasFindText"] = a?.HasFindText ?? false,
            ["hasBookmark"] = a?.HasBookmark ?? false,
            ["readOnlyDoc"] = a?.IsReadOnly ?? false,
            ["sqlWorkspaceActive"] = _activeWorkspace is not null,
        };
    }
}
