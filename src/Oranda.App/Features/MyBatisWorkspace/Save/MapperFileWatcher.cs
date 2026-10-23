namespace Oranda.App.Features.MyBatisWorkspace.Save;

/// <summary>매퍼 파일 하나의 날 사건(바뀜·생김·지움·이름 바꿈). 스레드 풀에서 난다. 디바운스는 <see cref="MapperWatchService"/> 가 한다.</summary>
internal interface IMapperFileWatcher : IDisposable
{
    event EventHandler? Changed;

    /// <summary>지금 사건을 받고 있다(폴더가 없어 감시를 못 켰으면 거짓).</summary>
    bool IsActive { get; }
}

/// <summary>
/// <see cref="FileSystemWatcher"/> 로 매퍼 파일 하나를 본다(design.md 5.3). 폴더를 보고 파일 이름으로 거른다:
/// 원자적 쓰기(<c>File.Replace</c>)와 IDE 의 safe-write 는 이름 바꾸기라 Changed 가 아니라 Created·Deleted·Renamed 로 온다.
/// 감시가 넘치거나 끊기면(Error) 다시 만든다.
/// </summary>
internal sealed class MapperFileWatcher : IMapperFileWatcher
{
    private readonly string _directory;
    private readonly string _fileName;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public MapperFileWatcher(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.GetFullPath(path);
        _directory = Path.GetDirectoryName(full) ?? throw new ArgumentException("폴더가 없는 경로입니다.", nameof(path));
        _fileName = Path.GetFileName(full);
        Start();
    }

    public event EventHandler? Changed;

    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _watcher is not null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Stop();
        }
    }

    private void Start()
    {
        lock (_gate)
        {
            if (_disposed || !Directory.Exists(_directory))
            {
                return;
            }

            var w = new FileSystemWatcher(_directory, _fileName)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            w.Changed += OnEvent;
            w.Created += OnEvent;
            w.Deleted += OnEvent;
            w.Renamed += OnEvent;
            w.Error += OnError;
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
    }

    private void Stop()
    {
        if (_watcher is { } w)
        {
            w.EnableRaisingEvents = false;
            w.Changed -= OnEvent;
            w.Created -= OnEvent;
            w.Deleted -= OnEvent;
            w.Renamed -= OnEvent;
            w.Error -= OnError;
            w.Dispose();
            _watcher = null;
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnError(object sender, ErrorEventArgs e)
    {
        lock (_gate)
        {
            Stop();
        }

        Start();
        Changed?.Invoke(this, EventArgs.Empty); // 놓친 사건이 있을 수 있으니 한 번 다시 본다
    }
}
