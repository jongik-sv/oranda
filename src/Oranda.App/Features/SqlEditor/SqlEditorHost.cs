using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Execution;
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
public sealed class SqlEditorHost : ObservableObject, IDisposable
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

    // ---- 작업 공간 -----------------------------------------------------------------------

    /// <summary>「새 작업 공간」: 빈 SQL 탭 하나를 가진 작업 공간을 열고 활성으로 둔다.</summary>
    public SqlWorkspaceViewModel NewWorkspace(string? name = null)
    {
        var ws = OpenWorkspace(NewWorkspaceId(), name ?? NextWorkspaceName());
        ActiveWorkspace = ws;
        NewDocument();
        return ws;
    }

    /// <summary>보고 있는 작업 공간. 없으면 기본 작업 공간을 만든다(처음 실행·작업 공간을 모두 닫은 뒤의 「새 SQL」).</summary>
    public SqlWorkspaceViewModel EnsureWorkspace()
    {
        if (_activeWorkspace is { } active)
        {
            return active;
        }

        if (Workspaces.FirstOrDefault() is { } first)
        {
            ActiveWorkspace = first;
            return first;
        }

        var id = NewWorkspaceId();
        var ws = OpenWorkspace(id, id == DefaultWorkspaceId ? DefaultWorkspaceName : NextWorkspaceName());
        ActiveWorkspace = ws;
        return ws;
    }

    private SqlWorkspaceViewModel OpenWorkspace(string id, string name)
    {
        var ws = new SqlWorkspaceViewModel(this, id, name);
        Workspaces.Add(ws);
        WorkspaceOpened?.Invoke(this, ws);
        return ws;
    }

    /// <summary>
    /// 작업 공간을 닫는다: <c>workspaces/{id}.json</c> 에 저장하고(저장 안 한 SQL 도 그 안에 남는다) 자동 저장을 지운다.
    /// 그래서 닫아도 내용을 잃지 않고 「작업 공간 열기」로 다시 연다.
    /// </summary>
    public async Task CloseWorkspaceAsync(SqlWorkspaceViewModel workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var index = Workspaces.IndexOf(workspace);
        if (index < 0)
        {
            return;
        }

        var snapshot = workspace.ToWorkspace();
        RemoveWorkspace(workspace, index);
        if (_store is not null)
        {
            await _store.SaveAsync(snapshot, ct).ConfigureAwait(true);
            await _store.DeleteAutosaveAsync(snapshot.Id, ct).ConfigureAwait(true);
        }
    }

    private void RemoveWorkspace(SqlWorkspaceViewModel workspace, int index)
    {
        // 닫히는 탭이 활성인 채로 TabClosed 를 받는 쪽이 없게, 먼저 이웃 작업 공간으로 옮긴다.
        if (ReferenceEquals(_activeWorkspace, workspace))
        {
            var others = Workspaces.Where(w => !ReferenceEquals(w, workspace)).ToList();
            ActiveWorkspace = others.Count == 0 ? null : others[Math.Min(index, others.Count - 1)];
        }

        foreach (var tab in workspace.Tabs.ToList())
        {
            workspace.Tabs.Remove(tab);
            Tabs.Remove(tab);
            TabClosed?.Invoke(this, tab);
        }

        workspace.ActiveTab = null;
        Workspaces.RemoveAt(index);
        WorkspaceClosed?.Invoke(this, workspace);
    }

    /// <summary>「작업 공간 이름 바꾸기」. 저장 파일 ID 는 「작업 공간 저장」 때만 바뀐다(닫았다 열어도 같은 파일).</summary>
    public async Task<bool> RenameWorkspaceAsync(SqlWorkspaceViewModel workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var name = await Dialogs.PromptAsync("작업 공간 이름 바꾸기", workspace.Name, ct).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        workspace.Name = name.Trim();
        return true;
    }

    /// <summary>
    /// 새 작업 공간 ID: 기본 작업 공간이 열려 있지도 저장돼 있지도 않으면 <c>default</c>, 아니면 <c>workspace-N</c>
    /// (열린 것·저장된 것과 겹치지 않게. 닫아 둔 작업 공간 파일을 덮어쓰지 않는다).
    /// </summary>
    private string NewWorkspaceId()
    {
        var taken = Workspaces.Select(w => w.Id).Concat(ListWorkspaceIds()).ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(DefaultWorkspaceId))
        {
            return DefaultWorkspaceId;
        }

        for (int n = 2; ; n++)
        {
            var id = "workspace-" + n.ToString(CultureInfo.InvariantCulture);
            if (!taken.Contains(id))
            {
                return id;
            }
        }
    }

    private string NextWorkspaceName()
    {
        for (int n = Workspaces.Count + 1; ; n++)
        {
            var name = "작업 공간 " + n.ToString(CultureInfo.InvariantCulture);
            if (!Workspaces.Any(w => w.Name == name))
            {
                return name;
            }
        }
    }

    /// <summary>파일에서 연 작업 공간의 ID: 이름 + 경로 해시. 같은 파일은 늘 같은 ID(다시 열면 그 저장본을 쓴다), 다른 파일과는 겹치지 않는다.</summary>
    internal static string FileWorkspaceId(string name, string fullPath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant())))[..8].ToLowerInvariant();
        var slug = SlugOf(name);
        if (slug.StartsWith("workspace-", StringComparison.Ordinal) && !name.Contains("workspace", StringComparison.OrdinalIgnoreCase))
        {
            slug = "file"; // 한글 이름 등 ASCII 가 없으면 무작위 꼬리 대신 고정 접두어
        }

        slug = slug.Length > 55 ? slug[..55].TrimEnd('-') : slug;
        return slug + "-" + hash;
    }

    // ---- 탭 -----------------------------------------------------------------------------

    /// <summary>보고 있는 작업 공간에 빈 SQL 탭을 더한다(없으면 기본 작업 공간을 만든다).</summary>
    public SqlEditorViewModel NewDocument(string? text = null)
    {
        var ws = EnsureWorkspace();
        var vm = new SqlEditorViewModel(Guid.NewGuid(), text ?? string.Empty) { Title = NextUntitledName(ws) };
        vm.IsDirty = false;
        AddTab(ws, vm);
        return vm;
    }

    private static string NextUntitledName(SqlWorkspaceViewModel ws)
    {
        for (int n = 1; ; n++)
        {
            var name = "SQL " + n.ToString(CultureInfo.InvariantCulture);
            if (!ws.Tabs.Any(t => t.FilePath is null && t.Title == name))
            {
                return name;
            }
        }
    }

    private void AddTab(SqlWorkspaceViewModel ws, SqlEditorViewModel vm)
    {
        ws.Tabs.Add(vm);
        InsertIntoAllTabs(ws, vm);
        TabOpened?.Invoke(this, vm);
        Active = vm;
    }

    /// <summary>전체 탭 목록에서도 작업 공간 순서를 지킨다(그 작업 공간의 마지막 탭 바로 뒤).</summary>
    private void InsertIntoAllTabs(SqlWorkspaceViewModel ws, SqlEditorViewModel vm)
    {
        var at = 0;
        foreach (var w in Workspaces)
        {
            at += w.Tabs.Count(t => !ReferenceEquals(t, vm));
            if (ReferenceEquals(w, ws))
            {
                break;
            }
        }

        Tabs.Insert(Math.Min(at, Tabs.Count), vm);
    }

    public SqlEditorViewModel Duplicate(SqlEditorViewModel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var ws = WorkspaceOf(source) ?? EnsureWorkspace();
        var vm = new SqlEditorViewModel(Guid.NewGuid(), source.Text)
        {
            Title = NextUntitledName(ws),
            ConnectionProfileId = source.ConnectionProfileId,
        };
        vm.IsDirty = false;
        vm.Select(source.SelectionStart, source.SelectionLength);
        AddTab(ws, vm);
        return vm;
    }

    /// <summary>탭을 닫는다(저장 확인은 호출한 쪽이 한다). 「닫은 탭 다시 열기」용으로 내용을 기억한다. 작업 공간은 비어도 남는다.</summary>
    public void Close(SqlEditorViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (WorkspaceOf(tab) is not { } ws)
        {
            return;
        }

        _closed.Push(SnapshotForReopen(tab));
        while (_closed.Count > ClosedHistoryLimit)
        {
            // Stack 은 아래를 못 지운다. 한도를 넘으면 다시 쌓는다(드문 경로).
            var keep = _closed.Take(ClosedHistoryLimit).Reverse().ToList();
            _closed.Clear();
            foreach (var t in keep)
            {
                _closed.Push(t);
            }
        }

        var index = ws.Tabs.IndexOf(tab);
        ws.Tabs.RemoveAt(index);
        Tabs.Remove(tab);
        if (ReferenceEquals(ws.ActiveTab, tab))
        {
            ws.ActiveTab = ws.Tabs.Count == 0 ? null : ws.Tabs[Math.Min(index, ws.Tabs.Count - 1)];
        }

        if (ReferenceEquals(_active, tab))
        {
            SetActive(ReferenceEquals(_activeWorkspace, ws) ? ws.ActiveTab : null);
        }

        TabClosed?.Invoke(this, tab);
    }

    // ---- 닫기 가드 ------------------------------------------------------------------------

    /// <summary>
    /// 닫기 가드를 등록한다. 가드는 등록한 순서대로 불리고, 하나라도 취소하면 닫기가 중단된다(나머지 가드는 부르지 않는다).
    /// 돌려받은 값을 <see cref="IDisposable.Dispose"/> 하면 이 등록만 해제한다. 같은 가드를 두 번 등록하면 두 번 불린다.
    /// 여러 delegate 를 합친 가드는 결과를 하나만 볼 수 있어 받지 않는다(따로 등록한다).
    /// </summary>
    public IDisposable RegisterCloseGuard(SqlCloseGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        if (guard.GetInvocationList().Length > 1)
        {
            throw new ArgumentException("합쳐진 delegate 는 등록할 수 없다. 가드마다 따로 등록한다.", nameof(guard));
        }

        var entry = new CloseGuardEntry(guard);
        _closeGuards.Add(entry);
        return new CloseGuardRegistration(this, entry);
    }

    /// <summary>delegate 로 해제한다. 등록돼 있었으면 true(같은 가드가 여럿이면 가장 먼저 등록한 하나). 등록 값의 Dispose 와 겹쳐 쓰지 않는다.</summary>
    public bool UnregisterCloseGuard(SqlCloseGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        var index = _closeGuards.FindIndex(e => e.Guard == guard);
        if (index < 0)
        {
            return false;
        }

        _closeGuards.RemoveAt(index);
        return true;
    }

    /// <summary>가드가 확인을 하는 중이면 true. 이때 들어온 닫기 요청은 취소로 처리된다(false).</summary>
    public bool IsCloseGuardRunning => _guardsRunning;

    /// <summary>
    /// 닫아도 되는지 가드에 차례로 묻는다. 모두 동의하면 true. 가드가 이미 도는 중이거나(두 번째 ×, <see cref="IsCloseGuardRunning"/>) 가드가 예외를 던지면 false.
    /// 닫을 탭이 없으면 가드를 부르지 않고 true. 닫기 자체는 하지 않는다: 호출한 쪽이 true 를 받은 뒤 닫는다.
    /// </summary>
    public async Task<bool> CanCloseAsync(IReadOnlyList<SqlEditorViewModel> tabs, SqlCloseReason reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        if (tabs.Count == 0 || _closeGuards.Count == 0)
        {
            return true;
        }

        if (_guardsRunning)
        {
            return false;
        }

        _guardsRunning = true;
        try
        {
            foreach (var entry in _closeGuards.ToList())
            {
                if (!await entry.Guard(tabs, reason, ct).ConfigureAwait(true))
                {
                    return false;
                }
            }

            return true;
        }
#pragma warning disable CA1031 // 가드가 실패하면 글을 잃지 않도록 닫기를 취소한다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            System.Diagnostics.Trace.TraceError("닫기 가드 실패(닫기 취소): {0}", ex);
            return false;
        }
        finally
        {
            _guardsRunning = false;
        }
    }

    /// <summary>탭을 닫는다(가드 이유 <see cref="SqlCloseReason.Tab"/>). 가드가 취소하면 닫지 않고 false.</summary>
    public async Task<bool> CloseAsync(SqlEditorViewModel tab, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (WorkspaceOf(tab) is null)
        {
            return false;
        }

        if (!await CanCloseAsync([tab], SqlCloseReason.Tab, ct).ConfigureAwait(true))
        {
            return false;
        }

        if (WorkspaceOf(tab) is null)
        {
            return false; // 대화상자가 떠 있는 동안 다른 경로로 이미 닫혔다
        }

        Close(tab);
        return true;
    }

    /// <summary>작업 공간을 닫는다(가드 이유 <see cref="SqlCloseReason.Workspace"/>). 가드가 취소하면 닫지 않고 false.</summary>
    public async Task<bool> TryCloseWorkspaceAsync(SqlWorkspaceViewModel workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!await CanCloseAsync(workspace.Tabs.ToList(), SqlCloseReason.Workspace, ct).ConfigureAwait(true))
        {
            return false;
        }

        if (!Workspaces.Contains(workspace))
        {
            return false;
        }

        await CloseWorkspaceAsync(workspace, ct).ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// 작업 공간을 복원한다(<see cref="Restore"/>). 같은 ID 의 작업 공간이 열려 있어 그 탭이 교체되면 가드
    /// (<see cref="SqlCloseReason.Replace"/>)에 먼저 묻고, 취소하면 복원하지 않고 null.
    /// </summary>
    public async Task<SqlWorkspaceViewModel?> RestoreAsync(Workspace workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (Workspaces.FirstOrDefault(w => w.Id == workspace.Id) is { } open)
        {
            var asked = open.Tabs.ToList();
            if (!await CanCloseAsync(asked, SqlCloseReason.Replace, ct).ConfigureAwait(true))
            {
                return null;
            }

            // 확인하는 동안 탭이 바뀌었으면 묻지 않은 탭을 버리게 되므로 복원하지 않는다.
            if (Workspaces.FirstOrDefault(w => w.Id == workspace.Id) is not { } now || !now.Tabs.SequenceEqual(asked))
            {
                return null;
            }
        }

        return Restore(workspace);
    }

    /// <summary>앱을 끝내도 되는지 모든 탭을 두고 가드에 묻는다(<see cref="SqlCloseReason.AppExit"/>). 끝내기 전에 셸이 부른다.</summary>
    public Task<bool> CanShutdownAsync(CancellationToken ct) => CanCloseAsync(Tabs.ToList(), SqlCloseReason.AppExit, ct);

    /// <summary>
    /// 고친 SQL 탭이 닫히기 전에 저장할지 묻는다. 탭 닫기·같은 ID 복원으로 교체될 때만 묻는다:
    /// 작업 공간 닫기와 앱 종료는 작업 공간 파일이 내용을 보존한다. 이름 없는 탭의 저장은 「다른 이름으로」이고, 파일 고르기를 취소하면 닫기도 취소다.
    /// </summary>
    private async Task<bool> ConfirmSaveAsync(IReadOnlyList<SqlEditorViewModel> tabs, SqlCloseReason reason, CancellationToken ct)
    {
        if (reason is not (SqlCloseReason.Tab or SqlCloseReason.Replace))
        {
            return true;
        }

        foreach (var tab in tabs.Where(NeedsSaveConfirm))
        {
            switch (await Dialogs.AskSaveChangesAsync(tab.Title, ct).ConfigureAwait(true))
            {
                case SaveChoice.Save:
                    try
                    {
                        if (!await SaveAsync(tab, ct).ConfigureAwait(true))
                        {
                            return false;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 저장하지 못했으니 닫지 않는다. 아무 말 없이 창만 사라진 것처럼 보이지 않게 알린다.
                        await Dialogs.ShowMessageAsync("저장", $"'{tab.Title}' 을(를) 저장하지 못했다: {ex.Message}", ct).ConfigureAwait(true);
                        return false;
                    }

                    break;
                case SaveChoice.DontSave:
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 파일에 묶인 탭은 고쳤을 때, 이름 없는 탭은 내용이 있을 때 묻는다. 읽기 전용 탭은 고칠 수 없으니 묻지 않는다.
    /// 복원 때 파일을 못 읽어 비어 있는 탭도 묻지 않는다: 저장하면 빈 내용이 원본을 덮어쓴다.
    /// </summary>
    private static bool NeedsSaveConfirm(SqlEditorViewModel tab)
    {
        if (tab.IsReadOnly)
        {
            return false;
        }

        if (tab.FilePath is null)
        {
            return !string.IsNullOrWhiteSpace(tab.Text);
        }

        return tab.IsDirty && !(tab.FileUnreadable && string.IsNullOrWhiteSpace(tab.Text));
    }

    private sealed class CloseGuardEntry(SqlCloseGuard guard)
    {
        public SqlCloseGuard Guard { get; } = guard;
    }

    private sealed class CloseGuardRegistration(SqlEditorHost host, CloseGuardEntry entry) : IDisposable
    {
        public void Dispose() => host._closeGuards.Remove(entry);
    }

    private static WorkspaceTab SnapshotForReopen(SqlEditorViewModel tab)
    {
        // 다시 열 때 파일을 새로 읽지 않고 닫을 때의 내용을 쓰도록 항상 내용을 담는다.
        return tab.ToWorkspaceTab() with { Content = tab.Text };
    }

    public bool CanReopenClosed => _closed.Count > 0;

    public SqlEditorViewModel? ReopenClosed()
    {
        if (_closed.Count == 0)
        {
            return null;
        }

        var tab = _closed.Pop();
        var vm = SqlEditorViewModel.FromWorkspaceTab(tab, null);
        AddTab(EnsureWorkspace(), vm);
        return vm;
    }

    // ---- 파일 ---------------------------------------------------------------------------

    public async Task<SqlEditorViewModel?> OpenFileAsync(string? path, CancellationToken ct)
    {
        path ??= await Dialogs.PickOpenFileAsync(ct).ConfigureAwait(true);
        if (path is null)
        {
            return null;
        }

        var full = Path.GetFullPath(path);
        var already = Tabs.FirstOrDefault(t => string.Equals(t.FilePath, full, StringComparison.OrdinalIgnoreCase));
        if (already is not null)
        {
            Active = already;
            return already;
        }

        var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(true);
        var (text, enc) = FileEncoding.Decode(bytes);
        IReadOnlyList<WorkspaceBind>? fileBinds = null;
        if (CarriesBinds(full))
        {
            // 끝의 바인드 블록은 편집기에 보이지 않고 그 값만 이 탭의 바인드 목록에 들어간다.
            (text, fileBinds) = SqlFileBinds.Split(text);
        }

        var vm = new SqlEditorViewModel(Guid.NewGuid(), text)
        {
            Title = Path.GetFileName(full),
            FilePath = full,
            Encoding = enc,
        };
        vm.Binds.Restore(fileBinds);
        vm.IsDirty = false;
        AddTab(EnsureWorkspace(), vm);
        return vm;
    }

    /// <summary>저장한다. 파일이 없는 탭은 「다른 이름으로」. 취소하면 false.</summary>
    public async Task<bool> SaveAsync(SqlEditorViewModel tab, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.FilePath is null)
        {
            return await SaveAsAsync(tab, ct).ConfigureAwait(true);
        }

        await WriteFileAsync(tab, tab.FilePath, ct).ConfigureAwait(true);
        return true;
    }

    public async Task<bool> SaveAsAsync(SqlEditorViewModel tab, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var suggested = tab.FilePath is null ? SaveFileNames.SuggestSql(tab.Title) : Path.GetFileName(tab.FilePath);
        var path = await Dialogs.PickSaveFileAsync(suggested, ct).ConfigureAwait(true);
        if (path is null)
        {
            return false;
        }

        await WriteFileAsync(tab, Path.GetFullPath(path), ct).ConfigureAwait(true);
        tab.FilePath = Path.GetFullPath(path);
        tab.Title = Path.GetFileName(path);
        return true;
    }

    public async Task<bool> SaveAllAsync(CancellationToken ct)
    {
        foreach (var tab in Tabs.Where(t => t.IsDirty).ToList())
        {
            if (!await SaveAsync(tab, ct).ConfigureAwait(true))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 바인드 블록을 싣는 파일인가(.sql 만. MyBatis·Markdown 작업 공간 파일과 다른 확장자는 예전 그대로 쓴다).
    /// </summary>
    internal static bool CarriesBinds(string? path) =>
        path is not null && string.Equals(Path.GetExtension(path), ".sql", StringComparison.OrdinalIgnoreCase);

    private static async Task WriteFileAsync(SqlEditorViewModel tab, string path, CancellationToken ct)
    {
        // .sql 이면 이 탭이 참조하는 바인드의 지금 값을 끝에 블록으로 붙인다(값이 없으면 본문만 — 예전과 같은 바이트).
        // 편집기 글은 블록 없는 본문이라 몇 번을 저장해도 블록은 하나다.
        var text = CarriesBinds(path) ? SqlFileBinds.Append(tab.Text, tab.Binds.Snapshot(tab.ReferencedBindNames)) : tab.Text;
        var bytes = FileEncoding.Encode(text, tab.Encoding);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = path + ".oranda-tmp";
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(true);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }

            throw;
        }

        tab.MarkSaved();
    }

    // ---- 작업 공간 저장·열기 --------------------------------------------------------------

    /// <summary>보고 있는 작업 공간의 저장 모습(없으면 빈 기본 작업 공간).</summary>
    public Workspace BuildWorkspace() =>
        _activeWorkspace?.ToWorkspace() ?? new Workspace { Id = DefaultWorkspaceId, Name = DefaultWorkspaceName, SavedAt = DateTimeOffset.UtcNow };

    /// <summary>
    /// 저장된 작업 공간을 연다: 같은 ID 의 작업 공간이 열려 있으면 그 탭들을 바꾸고, 아니면 새 작업 공간으로 연다.
    /// 파일 탭은 파일을 다시 읽는다. 연 작업 공간이 활성이 된다.
    /// </summary>
    public SqlWorkspaceViewModel Restore(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var ws = Workspaces.FirstOrDefault(w => w.Id == workspace.Id);
        if (ws is null)
        {
            ws = OpenWorkspace(workspace.Id, workspace.Name);
        }
        else
        {
            if (ReferenceEquals(_activeWorkspace, ws))
            {
                SetActive(null); // 바꿔 끼우는 동안 빠지는 탭이 활성으로 남지 않게
            }

            ws.ActiveTab = null;
            foreach (var old in ws.Tabs.ToList())
            {
                ws.Tabs.Remove(old);
                Tabs.Remove(old);
                TabClosed?.Invoke(this, old);
            }

            ws.Name = workspace.Name;
        }

        ws.SourcePath = workspace.SourcePath;
        foreach (var t in workspace.Tabs)
        {
            string? fileText = null;
            if (t.Content is null && t.FilePath is not null && File.Exists(t.FilePath))
            {
                try
                {
                    fileText = FileEncoding.Decode(File.ReadAllBytes(t.FilePath)).Text;
                }
                catch (IOException)
                {
                    // 못 읽으면 FromWorkspaceTab 이 빈 문서 + 변경됨으로 연다.
                }
            }

            var vm = SqlEditorViewModel.FromWorkspaceTab(t, fileText);
            ws.Tabs.Add(vm);
            InsertIntoAllTabs(ws, vm);
            TabOpened?.Invoke(this, vm);
        }

        ws.ActiveTab = ws.Tabs.FirstOrDefault(t => t.TabId == workspace.ActiveTabId) ?? ws.Tabs.FirstOrDefault();
        ws.MarkAutosaved(null);
        if (ReferenceEquals(_activeWorkspace, ws))
        {
            SetActive(ws.ActiveTab);
        }
        else
        {
            ActiveWorkspace = ws;
        }

        return ws;
    }

    /// <summary>「작업 공간 저장」: 이름을 물어 보고 있는 작업 공간을 <c>workspaces/{id}.json</c> 에 저장한다.</summary>
    public async Task<bool> SaveWorkspaceAsync(CancellationToken ct)
    {
        if (_store is null)
        {
            return false;
        }

        var ws = EnsureWorkspace();
        var name = await Dialogs.PromptAsync("작업 공간 이름", ws.Name, ct).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var oldId = ws.Id;
        var baseId = SlugOf(name.Trim());
        var id = baseId;
        if (id != oldId)
        {
            // 다른 작업 공간(열려 있든 닫아 두었든)의 파일을 덮어쓰지 않는다. 같은 작업 공간을 같은 이름으로 다시 저장하는 것만 덮어쓴다.
            var taken = Workspaces.Where(w => !ReferenceEquals(w, ws)).Select(w => w.Id).Concat(ListWorkspaceIds())
                .ToHashSet(StringComparer.Ordinal);
            for (int n = 2; taken.Contains(id); n++)
            {
                id = baseId + "-" + n.ToString(CultureInfo.InvariantCulture);
            }
        }

        // 먼저 새 ID 로 저장하고, 성공한 뒤에 메모리의 ID·이름을 바꾼다(저장이 실패하면 아무것도 바뀌지 않는다).
        await _store.SaveAsync(ws.ToWorkspace() with { Id = id, Name = name.Trim() }, ct).ConfigureAwait(true);
        ws.Name = name.Trim();
        ws.Id = id;
        OnPropertyChanged(nameof(WorkspaceId));
        OnPropertyChanged(nameof(WorkspaceName));
        if (oldId != id)
        {
            await _store.DeleteAutosaveAsync(oldId, ct).ConfigureAwait(true); // 옛 ID 의 자동 저장이 남으면 복구 때 둘로 열린다
            ws.MarkAutosaved(null);
        }

        return true;
    }

    /// <summary>「작업 공간 열기」: 저장된 ID 를 골라 연다. 이미 열려 있으면 그 작업 공간으로 옮겨 간다.</summary>
    public async Task<bool> OpenWorkspaceAsync(CancellationToken ct)
    {
        if (_store is null)
        {
            return false;
        }

        var id = await Dialogs.ChooseAsync("작업 공간 열기", ListWorkspaceIds(), ct).ConfigureAwait(true);
        if (id is null)
        {
            return false;
        }

        if (Workspaces.FirstOrDefault(w => w.Id == id) is { } open)
        {
            ActiveWorkspace = open;
            return true;
        }

        var doc = await _store.LoadAsync(id, ct).ConfigureAwait(true);
        if (doc is null)
        {
            return false;
        }

        return await RestoreAsync(doc.Value, ct).ConfigureAwait(true) is not null;
    }

    // ---- 작업 공간 파일(Orange SQL Tool 의 Workspace 파일) ------------------------------------

    /// <summary>
    /// 보고 있는 작업 공간의 탭을 Markdown 작업 공간 파일 하나로 저장한다. 처음이거나 MyBatis 에서 왔거나
    /// <paramref name="askPath"/> 면 경로를 묻는다.
    /// </summary>
    public async Task<bool> SaveWorkspaceFileAsync(bool askPath, CancellationToken ct)
    {
        var ws = EnsureWorkspace();
        var path = askPath || WorkspaceFilePath is null
            ? await Dialogs.PickWorkspaceFileAsync(save: true, SaveFileNames.SuggestWorkspace(ws.Name), ct).ConfigureAwait(true)
            : WorkspaceFilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        path = WorkspaceFiles.SavePath(path); // 새 파일은 이름.workspace.md (D-028)
        var name = WorkspaceFiles.NameOf(path);
        var tabs = ws.Tabs.Select(t => new WorkspaceFileTab(t.Title, t.Text, t.ConnectionProfileId?.ToString()));
        await File.WriteAllTextAsync(path, WorkspaceFiles.WriteMarkdown(name, tabs), new UTF8Encoding(false), ct).ConfigureAwait(true);
        ws.SourcePath = Path.GetFullPath(path);
        ws.Name = name;
        OnPropertyChanged(nameof(WorkspaceName));
        foreach (var t in ws.Tabs.Where(t => t.FilePath is null))
        {
            t.IsDirty = false; // 파일 없는 탭은 작업 공간 파일에 들어갔다.
        }

        return true;
    }

    /// <summary>
    /// 작업 공간 파일(.md)이나 MyBatis 매퍼(.xml)를 새 작업 공간 하나로 연다(파일의 탭·문장 하나가 SQL 탭 하나).
    /// 같은 파일을 연 작업 공간이 이미 있으면 그리로 옮겨 간다. 읽은 결과(알림 포함)를 돌려주고, 취소하면 null.
    /// </summary>
    public async Task<WorkspaceFile?> OpenWorkspaceFileAsync(string? path, CancellationToken ct)
    {
        path ??= await Dialogs.PickWorkspaceFileAsync(save: false, string.Empty, ct).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var full = Path.GetFullPath(path);
        if (Workspaces.FirstOrDefault(w => string.Equals(w.SourcePath, full, StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            ActiveWorkspace = open;
            return new WorkspaceFile(open.Name, [], []);
        }

        var file = WorkspaceFiles.Load(full);

        // MyBatis 는 namespace(대개 com.example.UserMapper 처럼 길다) 대신 파일 이름을 작업 공간 이름으로 쓴다.
        var name = WorkspaceFiles.IsMyBatis(full) ? Path.GetFileNameWithoutExtension(full) : file.Name;
        var ws = OpenWorkspace(UniqueId(FileWorkspaceId(name, full)), name);
        ws.SourcePath = full;
        ActiveWorkspace = ws;
        SqlEditorViewModel? first = null;
        foreach (var t in file.Tabs)
        {
            var vm = NewDocument(t.Text);
            vm.Title = t.Title;
            if (Guid.TryParse(t.Connection, out var profile))
            {
                vm.ConnectionProfileId = profile;
            }

            vm.Binds.Restore(t.Binds);
            first ??= vm;
        }

        if (first is not null)
        {
            Active = first;
        }

        return file;
    }

    private string UniqueId(string id)
    {
        var candidate = id;
        for (int n = 2; Workspaces.Any(w => w.Id == candidate); n++)
        {
            candidate = id + "-" + n.ToString(CultureInfo.InvariantCulture);
        }

        return candidate;
    }

    /// <summary>탭 이름 바꾸기(Orange 의 Change Tab Name). 파일 탭은 화면 이름만 바뀐다.</summary>
    public async Task<bool> RenameTabAsync(SqlEditorViewModel tab, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var name = await Dialogs.PromptAsync("탭 이름 바꾸기", tab.Title, ct).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        tab.Title = name.Trim();
        return true;
    }

    /// <summary>같은 작업 공간 안에서 탭을 <paramref name="newIndex"/> 자리로 옮긴다(끌어서 순서 바꾸기). 활성 탭은 그대로 유지한다.</summary>
    public bool MoveTab(SqlEditorViewModel tab, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (WorkspaceOf(tab) is not { } ws)
        {
            return false;
        }

        var oldIndex = ws.Tabs.IndexOf(tab);
        newIndex = Math.Clamp(newIndex, 0, ws.Tabs.Count - 1);
        if (oldIndex == newIndex)
        {
            return false;
        }

        var active = ws.ActiveTab;
        ws.Tabs.Move(oldIndex, newIndex);
        Tabs.Remove(tab);
        InsertIntoAllTabs(ws, tab);
        if (!ReferenceEquals(ws.ActiveTab, active))
        {
            ws.ActiveTab = active; // 목록 이동이 선택을 풀었으면 되돌린다
        }

        return true;
    }

    /// <summary>
    /// 문서 탭을 끌어 순서를 바꿨을 때 호스트의 작업 공간 순서를 그 순서에 맞춘다(종료 때 저장하는 열린 작업 공간 순서가 된다).
    /// <paramref name="order"/> 가 열린 작업 공간과 같은 집합이 아니면(닫는 중 등) 건드리지 않고 false.
    /// </summary>
    public bool ReorderWorkspaces(IReadOnlyList<SqlWorkspaceViewModel> order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.Count != Workspaces.Count || order.Any(w => !Workspaces.Contains(w)) || order.Distinct().Count() != order.Count
            || Workspaces.SequenceEqual(order))
        {
            return false;
        }

        for (var i = 0; i < order.Count; i++)
        {
            Workspaces.Move(Workspaces.IndexOf(order[i]), i);
        }

        var all = Workspaces.SelectMany(w => w.Tabs).ToList();
        for (var i = 0; i < all.Count; i++)
        {
            Tabs.Move(Tabs.IndexOf(all[i]), i);
        }

        return true;
    }

    /// <summary>탭 머리 메뉴 「다른 탭 닫기」/「오른쪽 탭 닫기」: 기준 탭을 뺀 대상 탭들을 가드 확인 뒤 닫는다.</summary>
    public async Task<bool> CloseTabsAsync(SqlEditorViewModel anchor, bool rightOnly, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (WorkspaceOf(anchor) is not { } ws)
        {
            return false;
        }

        var at = ws.Tabs.IndexOf(anchor);
        var targets = ws.Tabs.Where((t, i) => rightOnly ? i > at : i != at).ToList();
        if (targets.Count == 0 || !await CanCloseAsync(targets, SqlCloseReason.Tab, ct).ConfigureAwait(true))
        {
            return false;
        }

        foreach (var t in targets.Where(t => WorkspaceOf(t) is not null))
        {
            Close(t);
        }

        return true;
    }

    /// <summary>작업 공간 이름 → 파일 ID(<c>^[a-z0-9][a-z0-9-]{0,63}$</c>).</summary>
    public static string SlugOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var chars = name.Trim().ToLowerInvariant()
            .Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-')
            .ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        if (slug.Length == 0)
        {
            slug = "workspace-" + Guid.NewGuid().ToString("N")[..8];
        }

        return slug.Length > 64 ? slug[..64].TrimEnd('-') : slug;
    }

    // ---- 시작·자동 저장·종료 ---------------------------------------------------------------

    /// <summary>
    /// 시작 때 한 번. 자동 저장 파일이 남아 있으면 비정상 종료로 보고 한 번 묻고, 예 하면 그 작업 공간들을 모두 복구한다(data-model 2.6).
    /// 없으면 지난번 열려 있던 작업 공간들(<see cref="OpenWorkspaceIds"/>)을 다시 연다. 복구했으면 true.
    /// </summary>
    public async Task<bool> InitializeAsync(CancellationToken ct)
    {
        if (_store is null)
        {
            return false;
        }

        var autos = new List<Workspace>();
        foreach (var id in await _store.ListAutosaveIdsAsync(ct).ConfigureAwait(true))
        {
            Workspace? auto;
            try
            {
                auto = await _store.LoadAutosaveAsync(id, ct).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                System.Diagnostics.Trace.TraceError("자동 저장 '{0}' 을(를) 읽지 못했다: {1}", id, ex.Message);
                continue; // 한 파일이 나빠도 나머지 작업 공간은 복구한다
            }

            if (auto is { Tabs.Count: > 0 })
            {
                autos.Add(auto);
            }
            else
            {
                await _store.DeleteAutosaveAsync(id, ct).ConfigureAwait(true); // 빈 자동 저장은 복구할 것이 없다
            }
        }

        if (autos.Count > 0)
        {
            var yes = await Dialogs.ConfirmAsync("지난 실행이 정상 종료되지 않았다. 저장하지 않은 편집 내용을 복구할까요?", ct).ConfigureAwait(true);
            if (yes)
            {
                foreach (var auto in autos.OrderBy(a => OrderOf(a.Id)))
                {
                    Restore(auto);
                }

                ActivateLast();
                return true;
            }

            foreach (var auto in autos)
            {
                await _store.DeleteAutosaveAsync(auto.Id, ct).ConfigureAwait(true);
            }
        }

        if (RestoreOnStartup)
        {
            foreach (var id in OpenWorkspaceIds.Distinct(StringComparer.Ordinal))
            {
                if (await LoadSavedAsync(id, ct).ConfigureAwait(true) is { } saved)
                {
                    Restore(saved);
                }
            }

            ActivateLast();
        }

        return false;
    }

    private int OrderOf(string id)
    {
        var i = OpenWorkspaceIds.ToList().IndexOf(id);
        return i < 0 ? int.MaxValue : i;
    }

    private void ActivateLast()
    {
        if (LastWorkspaceId is { } last && Workspaces.FirstOrDefault(w => w.Id == last) is { } ws)
        {
            ActiveWorkspace = ws;
        }
    }

    private async Task<Workspace?> LoadSavedAsync(string id, CancellationToken ct)
    {
        try
        {
            return (await _store!.LoadAsync(id, ct).ConfigureAwait(true))?.Value;
        }
        catch (ArgumentException)
        {
            return null; // 설정에 잘못된 ID 가 들어 있어도 시작은 막지 않는다
        }
    }

    /// <summary>
    /// 열린 작업 공간마다 바뀐 것이 있으면 <c>autosave/{id}.json</c> 에 쓴다. 하나라도 썼으면 true.
    /// UI 스레드에서 부른다(스냅샷 때문).
    /// </summary>
    public async Task<bool> AutosaveAsync(CancellationToken ct)
    {
        if (_store is null)
        {
            return false;
        }

        var wrote = false;
        foreach (var ws in Workspaces.ToList())
        {
            if (!Workspaces.Contains(ws) || !ws.ChangedSinceAutosave(out var stamp))
            {
                continue; // 앞 작업 공간을 쓰는 동안 닫혔으면 건너뛴다
            }

            await _store.AutosaveAsync(ws.ToWorkspace(), ct).ConfigureAwait(true);
            ws.MarkAutosaved(stamp);
            wrote = true;
            if (!Workspaces.Contains(ws))
            {
                await _store.DeleteAutosaveAsync(ws.Id, ct).ConfigureAwait(true); // 쓰는 사이 닫혔다: 다음 시작에 복구로 다시 나타나지 않게
            }
        }

        return wrote;
    }

    /// <summary>자동 저장 타이머를 켠다(기본 10 초, 설정 <c>workspace.autosaveSeconds</c> 1~10).</summary>
    public void StartAutosave(TimeSpan interval)
    {
        StopAutosave();
        if (_store is null)
        {
            return;
        }

        var cts = _autosaveCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            try
            {
                while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => AutosaveAsync(cts.Token)).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // 디스크 오류 등은 다음 틱에 다시 시도한다(한 번 실패로 자동 저장이 멈추면 복구할 것이 없어진다).
                        System.Diagnostics.Trace.TraceError("자동 저장 실패: {0}", ex.Message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public void Dispose() => StopAutosave();

    public void StopAutosave()
    {
        _autosaveCts?.Cancel();
        _autosaveCts?.Dispose();
        _autosaveCts = null;
    }

    /// <summary>
    /// 정상 종료: 열린 작업 공간을 모두 저장하고 자동 저장 파일을 지운다. 지워야 다음 시작이 「정상」으로 보인다.
    /// 다음 시작 때 다시 열 목록은 <see cref="Workspaces"/>·<see cref="WorkspaceId"/> 로 셸이 설정에 적는다.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken ct)
    {
        StopAutosave();
        if (_store is null)
        {
            return;
        }

        foreach (var ws in Workspaces.ToList())
        {
            await _store.SaveAsync(ws.ToWorkspace(), ct).ConfigureAwait(true);
            await _store.DeleteAutosaveAsync(ws.Id, ct).ConfigureAwait(true);
        }
    }

    // ---- 문맥 키 ------------------------------------------------------------------------

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
