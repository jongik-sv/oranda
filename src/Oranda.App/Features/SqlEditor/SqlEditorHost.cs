using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Storage;
using Oranda.Core.Workspaces;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// 열린 SQL 편집기 탭 모음과 그 수명(열기·저장·닫기·복제·작업 공간·자동 저장·비정상 종료 복구).
/// 명령 처리기·개요 창·<c>IEditorContext</c> 가 같은 객체를 본다. DI 가 붙기 전에는 <see cref="Current"/> 를 쓴다.
/// UI 스레드에서만 쓴다(자동 저장 타이머는 스냅샷을 UI 스레드에서 만든다).
/// </summary>
public sealed class SqlEditorHost : ObservableObject, IDisposable
{
    private const int ClosedHistoryLimit = 20;

    private static SqlEditorHost? s_current;

    private IWorkspaceStore? _store;
    private readonly Stack<WorkspaceTab> _closed = new();
    private List<(Guid Id, long Version, bool Dirty)>? _lastAutosaveStamp;
    private CancellationTokenSource? _autosaveCts;
    private SqlEditorViewModel? _active;

    public SqlEditorHost(IWorkspaceStore? store = null, IEditorDialogs? dialogs = null, string workspaceId = "default")
    {
        _store = store;
        Dialogs = dialogs ?? new NoDialogs();
        WorkspaceId = workspaceId;
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

    public ObservableCollection<SqlEditorViewModel> Tabs { get; } = [];

    /// <summary>쓰고 있는 작업 공간 ID(자동 저장 파일 이름도 같다).</summary>
    public string WorkspaceId { get; private set; }

    public string WorkspaceName { get; private set; } = "기본";

    /// <summary>시작 때 마지막 작업 공간을 다시 연다(설정 <c>workspace.restoreOnStartup</c>).</summary>
    public bool RestoreOnStartup { get; set; } = true;

    public SqlEditorViewModel? Active
    {
        get => _active;
        set
        {
            if (ReferenceEquals(_active, value))
            {
                return;
            }

            _active = value;
            OnPropertyChanged();
            ActiveChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ActiveChanged;

    /// <summary>저장된 작업 공간 ID 목록을 돌려주는 함수(셸이 <c>IAppDataPaths</c> 로 채운다). 기본은 빈 목록.</summary>
    public Func<IReadOnlyList<string>> ListWorkspaceIds { get; set; } = () => [];

    /// <summary>문서 개요 창을 보이거나 개요로 이동하라는 요청(<c>oranda.view.outline</c>, <c>oranda.nav.documentOutline</c>). 셸이 도구 창을 연다.</summary>
    public event EventHandler? OutlineRequested;

    public void RequestOutline() => OutlineRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler<SqlEditorViewModel>? TabOpened;

    public event EventHandler<SqlEditorViewModel>? TabClosed;

    // ---- 탭 -----------------------------------------------------------------------------

    public SqlEditorViewModel NewDocument(string? text = null)
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), text ?? string.Empty) { Title = NextUntitledName() };
        vm.IsDirty = false;
        AddTab(vm);
        return vm;
    }

    private string NextUntitledName()
    {
        for (int n = 1; ; n++)
        {
            var name = "SQL " + n.ToString(CultureInfo.InvariantCulture);
            if (!Tabs.Any(t => t.FilePath is null && t.Title == name))
            {
                return name;
            }
        }
    }

    private void AddTab(SqlEditorViewModel vm)
    {
        Tabs.Add(vm);
        TabOpened?.Invoke(this, vm);
        Active = vm;
    }

    public SqlEditorViewModel Duplicate(SqlEditorViewModel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var vm = new SqlEditorViewModel(Guid.NewGuid(), source.Text)
        {
            Title = NextUntitledName(),
            ConnectionProfileId = source.ConnectionProfileId,
        };
        vm.IsDirty = false;
        vm.Select(source.SelectionStart, source.SelectionLength);
        AddTab(vm);
        return vm;
    }

    /// <summary>탭을 닫는다(저장 확인은 호출한 쪽이 한다). 「닫은 탭 다시 열기」용으로 내용을 기억한다.</summary>
    public void Close(SqlEditorViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        int index = Tabs.IndexOf(tab);
        if (index < 0)
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

        Tabs.RemoveAt(index);
        if (ReferenceEquals(Active, tab))
        {
            Active = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        TabClosed?.Invoke(this, tab);
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
        AddTab(vm);
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
        var vm = new SqlEditorViewModel(Guid.NewGuid(), text)
        {
            Title = Path.GetFileName(full),
            FilePath = full,
            Encoding = enc,
        };
        vm.IsDirty = false;
        AddTab(vm);
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
        var suggested = tab.FilePath is null ? tab.Title + ".sql" : Path.GetFileName(tab.FilePath);
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

    private static async Task WriteFileAsync(SqlEditorViewModel tab, string path, CancellationToken ct)
    {
        var bytes = FileEncoding.Encode(tab.Text, tab.Encoding);
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

    // ---- 작업 공간 -----------------------------------------------------------------------

    public Workspace BuildWorkspace()
        => new()
        {
            Id = WorkspaceId,
            Name = WorkspaceName,
            SavedAt = DateTimeOffset.UtcNow,
            ActiveTabId = Active?.TabId,
            Tabs = [.. Tabs.Select(t => t.ToWorkspaceTab())],
        };

    /// <summary>작업 공간의 탭들로 지금 탭을 바꾼다(파일 탭은 파일을 다시 읽는다).</summary>
    public void Restore(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Tabs.Clear();
        Active = null;
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
            Tabs.Add(vm);
            TabOpened?.Invoke(this, vm);
        }

        Active = Tabs.FirstOrDefault(t => t.TabId == workspace.ActiveTabId) ?? Tabs.FirstOrDefault();
        WorkspaceId = workspace.Id;
        WorkspaceName = workspace.Name;
        _lastAutosaveStamp = null;
    }

    /// <summary>「작업 공간 저장」: 이름을 물어 <c>workspaces/{id}.json</c> 에 저장한다.</summary>
    public async Task<bool> SaveWorkspaceAsync(CancellationToken ct)
    {
        if (_store is null)
        {
            return false;
        }

        var name = await Dialogs.PromptAsync("작업 공간 이름", WorkspaceName, ct).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        WorkspaceName = name.Trim();
        WorkspaceId = SlugOf(WorkspaceName);
        await _store.SaveAsync(BuildWorkspace(), ct).ConfigureAwait(true);
        return true;
    }

    /// <summary>「작업 공간 열기」: 저장된 ID 를 골라 연다.</summary>
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

        var doc = await _store.LoadAsync(id, ct).ConfigureAwait(true);
        if (doc is null)
        {
            return false;
        }

        Restore(doc.Value);
        return true;
    }

    // ---- 작업 공간 파일(Orange SQL Tool 의 Workspace 파일) ------------------------------------

    /// <summary>파일에서 열었거나 파일로 저장한 작업 공간의 .md 경로. 그 뒤 「작업 공간 파일로 저장」은 이 파일에 덮어쓴다.</summary>
    public string? WorkspaceFilePath { get; private set; }

    /// <summary>열린 탭 전체를 Markdown 작업 공간 파일 하나로 저장한다. 처음이거나 <paramref name="askPath"/> 면 경로를 묻는다.</summary>
    public async Task<bool> SaveWorkspaceFileAsync(bool askPath, CancellationToken ct)
    {
        var path = askPath || WorkspaceFilePath is null
            ? await Dialogs.PickWorkspaceFileAsync(save: true, WorkspaceName + ".md", ct).ConfigureAwait(true)
            : WorkspaceFilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))
        {
            path += ".md";
        }

        var name = Path.GetFileNameWithoutExtension(path);
        var tabs = Tabs.Select(t => new WorkspaceFileTab(t.Title, t.Text, t.ConnectionProfileId?.ToString()));
        await File.WriteAllTextAsync(path, WorkspaceFiles.WriteMarkdown(name, tabs), new System.Text.UTF8Encoding(false), ct).ConfigureAwait(true);
        WorkspaceFilePath = path;
        WorkspaceName = name;
        foreach (var t in Tabs.Where(t => t.FilePath is null))
        {
            t.IsDirty = false; // 파일 없는 탭은 작업 공간 파일에 들어갔다.
        }

        return true;
    }

    /// <summary>
    /// 작업 공간 파일(.md)이나 MyBatis 매퍼(.xml)를 열어 탭마다 새 편집기로 연다. 지금 열린 탭은 그대로 둔다.
    /// 읽은 결과(알림 포함)를 돌려주고, 취소하면 null.
    /// </summary>
    public async Task<WorkspaceFile?> OpenWorkspaceFileAsync(string? path, CancellationToken ct)
    {
        path ??= await Dialogs.PickWorkspaceFileAsync(save: false, string.Empty, ct).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var file = WorkspaceFiles.Load(path);
        SqlEditorViewModel? first = null;
        foreach (var t in file.Tabs)
        {
            var vm = NewDocument(t.Text);
            vm.Title = t.Title;
            if (Guid.TryParse(t.Connection, out var profile))
            {
                vm.ConnectionProfileId = profile;
            }

            first ??= vm;
        }

        if (first is not null)
        {
            Active = first;
        }

        if (!WorkspaceFiles.IsMyBatis(path))
        {
            WorkspaceFilePath = path;
            WorkspaceName = file.Name;
        }

        return file;
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

    /// <summary>작업 공간 이름 → 파일 ID(<c>^[a-z0-9][a-z0-9-]{0,63}$</c>).</summary>
    public static string SlugOf(string name)
    {
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
    /// 시작 때 한 번. 자동 저장 파일이 남아 있으면 비정상 종료로 보고 복구 여부를 묻는다(data-model 2.6).
    /// 없으면 마지막 작업 공간을 연다. 복구했으면 true.
    /// </summary>
    public async Task<bool> InitializeAsync(CancellationToken ct)
    {
        if (_store is null)
        {
            return false;
        }

        var auto = await _store.LoadAutosaveAsync(WorkspaceId, ct).ConfigureAwait(true);
        if (auto is not null && auto.Tabs.Count > 0)
        {
            var yes = await Dialogs.ConfirmAsync("지난 실행이 정상 종료되지 않았다. 저장하지 않은 편집 내용을 복구할까요?", ct).ConfigureAwait(true);
            if (yes)
            {
                Restore(auto);
                return true;
            }

            await _store.DeleteAutosaveAsync(WorkspaceId, ct).ConfigureAwait(true);
        }

        if (RestoreOnStartup)
        {
            var saved = await _store.LoadAsync(WorkspaceId, ct).ConfigureAwait(true);
            if (saved is not null)
            {
                Restore(saved.Value);
            }
        }

        return false;
    }

    /// <summary>
    /// 지금 상태를 <c>autosave/{id}.json</c> 에 쓴다. 지난번 이후 바뀐 것이 없으면 쓰지 않는다. 쓰면 true.
    /// UI 스레드에서 부른다(스냅샷 때문).
    /// </summary>
    public async Task<bool> AutosaveAsync(CancellationToken ct)
    {
        if (_store is null)
        {
            return false;
        }

        var stamp = Tabs.Select(t => (t.TabId, t.Version, t.IsDirty)).ToList();
        if (_lastAutosaveStamp is not null && _lastAutosaveStamp.SequenceEqual(stamp))
        {
            return false;
        }

        var snapshot = BuildWorkspace();
        await _store.AutosaveAsync(snapshot, ct).ConfigureAwait(true);
        _lastAutosaveStamp = stamp;
        return true;
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
                    catch (IOException)
                    {
                        // 디스크 오류는 다음 틱에 다시 시도한다.
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

    /// <summary>정상 종료: 작업 공간을 저장하고 자동 저장 파일을 지운다. 지워야 다음 시작이 「정상」으로 보인다.</summary>
    public async Task ShutdownAsync(CancellationToken ct)
    {
        StopAutosave();
        if (_store is null)
        {
            return;
        }

        await _store.SaveAsync(BuildWorkspace(), ct).ConfigureAwait(true);
        await _store.DeleteAutosaveAsync(WorkspaceId, ct).ConfigureAwait(true);
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
        };
    }
}
