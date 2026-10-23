using System.Text.Json;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.Core.Composition;
using Oranda.Core.Storage;

namespace Oranda.App.Features.MyBatisWorkspace.Folder;

/// <summary>
/// 열린 매퍼 폴더 하나의 상태(MB-10, TSK-03-07): 색인, 폴더 감시, 최근 폴더, 문맥 키 <c>mybatisFolderOpen</c>.
/// 조각 해석기·문장 찾기·트리 모드·명령이 이 하나를 같이 본다(<see cref="OrandaServiceAttribute"/> 는 서비스 형식마다 따로 만들므로 자기 형식으로 등록한다).
/// 훑기·다시 읽기는 스레드 풀에서 하고, 색인 바꾸기와 <see cref="Changed"/> 는 폴더를 연 스레드(UI)의 동기화 문맥에서 한다.
/// </summary>
[OrandaService(typeof(MapperFolderService))]
public sealed class MapperFolderService : IDisposable
{
    internal const string FolderOpenKey = "mybatisFolderOpen";
    internal const string StatementsToolId = "oranda.panel.mybatisStatements";
    private const int MaxRecent = 10;

    private readonly IServiceProvider? _services;
    private readonly object _gate = new();
    private readonly object _flushGate = new();
    private readonly HashSet<string> _pending = new(MapperFolderIndex.PathComparer);
    private readonly HashSet<string> _scanDirty = new(MapperFolderIndex.PathComparer);
    private ContextKeyService? _keys;
    private DockWorkspace? _docking;
    private string? _recentFile;
    private List<string>? _recent;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private SynchronizationContext? _context;
    private CancellationTokenSource? _scan;
    private int _generation;
    private bool _rescan;
    private volatile bool _disposed;

    /// <summary>셸이 쓰는 생성자. 다른 서비스는 처음 쓸 때 꺼낸다(등록된 형식만 받는 시작 시험 제약).</summary>
    public MapperFolderService(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>시험용: 문맥 키·도킹 없이, 최근 폴더는 <paramref name="dataDirectory"/>(null 이면 메모리만).</summary>
    internal MapperFolderService(string? dataDirectory, ContextKeyService? keys = null)
    {
        _recentFile = dataDirectory is null ? string.Empty : Path.Combine(dataDirectory, "mybatis-folders.json");
        _keys = keys;
    }

    /// <summary>열린 폴더의 색인. 열린 폴더가 없으면 null. 훑는 중에는 빈 색인.</summary>
    public MapperFolderIndex? Index { get; private set; }

    /// <summary>폴더가 열려 있다.</summary>
    public bool IsOpen => Index is not null;

    /// <summary>폴더를 훑는 중이다.</summary>
    public bool IsScanning { get; private set; }

    /// <summary>파일이 바뀐 것을 다시 읽어 색인에 넣는 감시 지연(ms). 시험이 줄인다.</summary>
    internal int DebounceMilliseconds { get; set; } = 300;

    /// <summary>색인·열림·훑는 중이 바뀌었다(폴더를 연 스레드에서).</summary>
    public event EventHandler? Changed;

    /// <summary>최근 폴더(최근 것 먼저, 최대 10개).</summary>
    public IReadOnlyList<string> RecentFolders => Recent;

    private List<string> Recent => _recent ??= LoadRecent();

    /// <summary>
    /// 폴더를 연다: 감시를 걸고 백그라운드에서 훑은 뒤 색인을 바꾼다. 다른 폴더가 열려 있으면 닫고, 같은 폴더면 다시 훑기만 한다.
    /// 없는 폴더면 false. 열면 최근 폴더 맨 앞에 넣고 「문장」 창을 문서 종류와 상관없이 보이게 한다. 호출한 쪽이 취소하면 폴더를 닫는다.
    /// </summary>
    public async Task<bool> OpenAsync(string folder, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var root = Path.GetFullPath(folder);
        if (!Directory.Exists(root))
        {
            return false;
        }

        if (Index is { } open && MapperFolderIndex.PathComparer.Equals(open.Root, root))
        {
            return await ScanAsync(root, ct).ConfigureAwait(true); // 다시 훑기: 닫았다 열면 보기 모드가 꺼졌다 켜진다
        }

        Close();

        // 「문장」 창을 먼저 띄운다: 창(과 그 보기 모드 단추)이 「열림」으로 바뀌는 것을 받아야 폴더 트리 모드로 간다.
        if (Docking is { } docking)
        {
            docking.SetToolAlwaysShown(StatementsToolId, true);
            docking.ActivateTool(StatementsToolId);
        }

        _context = SynchronizationContext.Current;
        Index = MapperFolderIndex.Empty(root);
        Watch(root);
        Remember(root);
        Keys?.Set(FolderOpenKey, true);
        return await ScanAsync(root, ct).ConfigureAwait(true);
    }

    /// <summary>최근 폴더 목록에서 뺀다(없어진 폴더).</summary>
    public void Forget(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        if (Recent.RemoveAll(p => MapperFolderIndex.PathComparer.Equals(p, folder)) > 0)
        {
            SaveRecent();
        }
    }

    /// <summary>폴더를 닫는다(감시를 풀고 색인을 버린다). 열린 폴더가 없으면 아무것도 하지 않는다.</summary>
    public void Close()
    {
        if (Index is null && _watcher is null)
        {
            return;
        }

        _generation++;
        _scan?.Cancel();
        _scan?.Dispose();
        _scan = null;
        StopWatching();
        Index = null;
        IsScanning = false;
        _scanDirty.Clear();
        Keys?.Set(FolderOpenKey, false);
        Docking?.SetToolAlwaysShown(StatementsToolId, false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 파일 하나를 다시 읽어 색인에 넣는다(감시가 부르는 것과 같은 길). 폴더 밖·거르는 폴더 안 경로·xml 아닌 파일은 무시한다. 지금 스레드에서 읽고 바꾼다.
    /// </summary>
    internal void Reload(string path) => Apply([Read(path)]);

    public void Dispose()
    {
        _disposed = true;
        Close();
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;
        }
    }

    private ContextKeyService? Keys => _keys ??= _services?.GetService(typeof(ContextKeyService)) as ContextKeyService;

    private DockWorkspace? Docking => _docking ??= _services?.GetService(typeof(DockWorkspace)) as DockWorkspace;

    private static bool IsXml(string path) => path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    // 훑기 하나. 새 훑기를 시작하면 앞 훑기 결과는 버린다. 훑는 동안 감시가 알린 파일은 훑은 결과가 옛것일 수 있어 끝난 뒤 다시 읽는다.
    private async Task<bool> ScanAsync(string root, CancellationToken ct)
    {
        _scan?.Cancel();
        _scan?.Dispose();
        int generation = ++_generation;
        var scan = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _scan = scan;
        IsScanning = true;
        _scanDirty.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            var index = await MapperFolderIndex.ScanAsync(root, scan.Token).ConfigureAwait(true);
            if (generation != _generation)
            {
                return false; // 훑는 동안 다른 폴더를 열었거나 닫았거나 다시 훑었다
            }

            var dirty = _scanDirty.Select(Read).ToList();
            Index = index.WithFiles(dirty);
            return true;
        }
        catch (OperationCanceledException) when (generation != _generation || scan.IsCancellationRequested)
        {
            if (generation == _generation)
            {
                Close(); // 호출한 쪽이 취소했다: 빈 색인으로 열린 채 두지 않는다
            }

            return false;
        }
        finally
        {
            if (generation == _generation)
            {
                _scan = null;
                scan.Dispose();
                IsScanning = false;
                _scanDirty.Clear();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private static MapperFolderChange Read(string path)
    {
        bool exists = File.Exists(path);
        return new MapperFolderChange(path, exists ? MapperFolderIndex.ReadFile(path) : null, exists);
    }

    // 읽은 바뀜을 색인에 넣는다(폴더를 연 스레드). 훑는 중이면 끝난 뒤 다시 읽도록 경로만 남긴다. 실제로 바뀐 것이 없으면 알리지 않는다.
    private void Apply(IReadOnlyList<MapperFolderChange> changes)
    {
        if (Index is not { } index)
        {
            return;
        }

        var mine = changes.Where(c => IsXml(c.Path) && index.Contains(c.Path) && !index.IsExcluded(c.Path)).ToList();
        if (IsScanning)
        {
            _scanDirty.UnionWith(mine.Select(c => c.Path));
            return;
        }

        var next = index.WithFiles(mine);
        if (!ReferenceEquals(next, index))
        {
            Index = next;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---- 감시 -----------------------------------------------------------------------------------

    private void Watch(string root)
    {
        try
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
            };
            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Deleted += OnFileEvent;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnWatchError;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            _watcher = null; // 감시를 못 걸어도 훑은 결과는 쓴다
        }
    }

    private void StopWatching()
    {
        if (_watcher is { } watcher)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnFileEvent;
            watcher.Created -= OnFileEvent;
            watcher.Deleted -= OnFileEvent;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnWatchError;
            watcher.Dispose();
            _watcher = null;
        }

        lock (_gate)
        {
            _pending.Clear();
            _rescan = false;
            _debounce?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => Queue(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        Queue(e.OldFullPath);
        Queue(e.FullPath);
    }

    // 사건이 넘쳐 잃었다(InternalBufferOverflow 등): 다시 훑는다.
    private void OnWatchError(object sender, ErrorEventArgs e) => Queue(null);

    // 감시 사건은 스레드 풀에서 온다. 저장 한 번에 사건이 여러 번 오므로 모았다가 한 번에 읽는다.
    // 폴더가 옮겨지거나 지워지면 그 안 파일 사건이 따로 오지 않을 수 있어 다시 훑는다(path = null 도 다시 훑기).
    private void Queue(string? path)
    {
        var index = Index;
        if (_disposed || index is null)
        {
            return;
        }

        bool rescan = path is null || (!IsXml(path) && (Directory.Exists(path) || index.HasFilesUnder(path)));
        if (!rescan && (!IsXml(path!) || index.IsExcluded(path!)))
        {
            return;
        }

        lock (_gate)
        {
            if (rescan)
            {
                _rescan = true;
            }
            else
            {
                _pending.Add(path!);
            }

            _debounce ??= new Timer(_ => Flush());
            _debounce.Change(DebounceMilliseconds, Timeout.Infinite);
        }
    }

    private void Flush()
    {
        // 타이머 콜백이 겹쳐도 읽고 보내는 순서가 섞이지 않게 하나씩 한다(나중에 읽은 것이 나중에 들어간다).
        lock (_flushGate)
        {
            string[] paths;
            bool rescan;
            lock (_gate)
            {
                paths = [.. _pending];
                _pending.Clear();
                rescan = _rescan;
                _rescan = false;
            }

            if (paths.Length == 0 && !rescan)
            {
                return;
            }

            // 읽기는 여기(스레드 풀)에서, 색인 바꾸기는 폴더를 연 스레드에서.
            var read = rescan ? [] : paths.Select(Read).ToArray();
            void Run()
            {
                if (rescan && Index is { } index)
                {
                    _ = ScanAsync(index.Root, CancellationToken.None);
                }
                else
                {
                    Apply(read);
                }
            }

            if (_context is { } context)
            {
                context.Post(_ => Run(), null);
            }
            else
            {
                Run();
            }
        }
    }

    // ---- 최근 폴더 ------------------------------------------------------------------------------

    private void Remember(string root)
    {
        Recent.RemoveAll(p => MapperFolderIndex.PathComparer.Equals(p, root));
        Recent.Insert(0, root);
        if (Recent.Count > MaxRecent)
        {
            Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
        }

        SaveRecent();
    }

    private string? RecentFile
    {
        get
        {
            if (_recentFile is null && _services?.GetService(typeof(IAppDataPaths)) is IAppDataPaths paths)
            {
                _recentFile = Path.Combine(paths.DataDirectory, "mybatis-folders.json");
            }

            return string.IsNullOrEmpty(_recentFile) ? null : _recentFile;
        }
    }

    private List<string> LoadRecent()
    {
        try
        {
            if (RecentFile is { } file && File.Exists(file))
            {
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file));
                return [.. (list ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Take(MaxRecent)];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 목록 파일이 손상되면 비어 있는 것으로 시작한다(ScriptFolderList 와 같다).
        }

        return [];
    }

    private void SaveRecent()
    {
        if (RecentFile is not { } file)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(Recent));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 목록을 못 저장해도 이번 실행에서는 계속 쓸 수 있다.
        }
    }
}
