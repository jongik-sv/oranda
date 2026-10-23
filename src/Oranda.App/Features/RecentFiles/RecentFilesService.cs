using System.Text.Json;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Menus;
using Oranda.Core.Composition;
using Oranda.Core.Shell;
using Oranda.Core.Storage;

namespace Oranda.App.Features.RecentFiles;

/// <summary>최근 목록 종류: 파일(.sql·MyBatis 매퍼 .xml 등) / 작업 공간(.md·.sqd).</summary>
public enum RecentKind
{
    File,
    Workspace,
}

/// <summary>
/// 최근 파일·최근 작업 공간(각 <see cref="MaxItems"/> 개, 맨 위가 최근). 편집기 호스트의 <see cref="SqlEditorHost.FileUsed"/>(열기·저장)를 듣고 맨 위로 올린다(중복 없음).
/// 앱 데이터 폴더의 <c>recent-files.json</c> 에 남는다(<c>mybatis-folders.json</c> 과 같은 관례 — 손상·IO 오류 = 빈 목록).
/// 파일 메뉴 「최근 파일」·「최근 작업 공간」 하위 메뉴에 줄을 채우고(<see cref="MenuItemSources"/>), 없는 파일을 누르면 목록에서 빼고 상태 표시줄로 알린다.
/// UI 스레드에서 쓴다.
/// </summary>
[OrandaService(typeof(RecentFilesService))]
public sealed class RecentFilesService : IDisposable
{
    internal const int MaxItems = 10;

    /// <summary>하위 메뉴 키(그 안 「목록 지우기」 명령의 자리 <c>menu:file/recentFiles/…</c> 로 생긴다).</summary>
    internal const string FilesMenuKey = "Menu.file.recentFiles";

    internal const string WorkspacesMenuKey = "Menu.file.recentWorkspaces";

    /// <summary>메뉴 줄 회색 경로의 최대 글자 수(넘으면 가운데를 줄인다).</summary>
    internal const int DetailLength = 56;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IServiceProvider? _services;
    private readonly List<IDisposable> _menus = [];
    private string? _file;
    private Lists? _lists;
    private SqlEditorHost? _host;

    public RecentFilesService(IServiceProvider services) => _services = services;

    /// <summary>시험용: 목록 파일은 <paramref name="dataDirectory"/>(null 이면 메모리만).</summary>
    internal RecentFilesService(string? dataDirectory) =>
        _file = dataDirectory is null ? string.Empty : Path.Combine(dataDirectory, "recent-files.json");

    public IReadOnlyList<string> Files => Current.Files;

    public IReadOnlyList<string> Workspaces => Current.Workspaces;

    /// <summary>목록이 바뀌었다(시작 화면이 다시 그린다).</summary>
    public event EventHandler? Changed;

    /// <summary>시험용·기본: 명령(<c>oranda.file.open</c>·<c>oranda.file.openWorkspaceFile</c>)을 경로 인자로 실행한다.</summary>
    internal Func<string, string, Task>? Execute { get; init; }

    private Lists Current => _lists ??= Load();

    /// <summary>종류를 모를 때 경로가 들어갈 목록: 작업 공간으로 여는 파일(.workspace.md·.sqd)이면 작업 공간, 나머지는 파일. 호스트 기록은 종류를 함께 준다(<see cref="FileUse"/>).</summary>
    public static RecentKind KindOf(string path) =>
        WorkspaceFiles.OpensAsWorkspace(path) && !WorkspaceFiles.IsMyBatis(path) ? RecentKind.Workspace : RecentKind.File;

    public IReadOnlyList<string> ListOf(RecentKind kind) => kind == RecentKind.Workspace ? Workspaces : Files;

    /// <summary>호스트의 열기·저장을 듣고 파일 메뉴 하위 메뉴 줄을 건다(앱 시작 뒤 한 번, <see cref="RecentFilesStartup"/>).</summary>
    public void Attach(SqlEditorHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (ReferenceEquals(_host, host))
        {
            return;
        }

        Detach();
        _host = host;
        host.FileUsed += OnFileUsed;
        _menus.Add(MenuItemSources.Register(FilesMenuKey, new Source(this, RecentKind.File)));
        _menus.Add(MenuItemSources.Register(WorkspacesMenuKey, new Source(this, RecentKind.Workspace)));
    }

    public void Dispose() => Detach();

    /// <summary>경로를 그 목록 맨 위로(이미 있으면 옮긴다. 다른 목록에 있으면 거기서 뺀다). 넘치면 오래된 것부터 버린다. <paramref name="kind"/> 가 없으면 확장자로 정한다.</summary>
    public void Add(string path, RecentKind? kind = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.GetFullPath(path);
        Current.Files.RemoveAll(p => Same(p, full));
        Current.Workspaces.RemoveAll(p => Same(p, full));
        var list = Mutable(kind ?? KindOf(full));
        list.Insert(0, full);
        if (list.Count > MaxItems)
        {
            list.RemoveRange(MaxItems, list.Count - MaxItems);
        }

        Save();
    }

    public void Remove(string path, RecentKind? kind = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (Mutable(kind ?? KindOf(path)).RemoveAll(p => Same(p, path)) > 0)
        {
            Save();
        }
    }

    public void Clear(RecentKind kind)
    {
        var list = Mutable(kind);
        if (list.Count == 0)
        {
            return;
        }

        list.Clear();
        Save();
    }

    /// <summary>
    /// 최근 항목을 연다. 파일이 없으면 목록에서 빼고 상태 표시줄에 한 줄 알린 뒤 false.
    /// 파일은 <c>oranda.file.open</c>(매퍼 .xml 은 MyBatis 작업 공간으로), 작업 공간은 <c>oranda.file.openWorkspaceFile</c> 로 연다.
    /// </summary>
    public async Task<bool> OpenAsync(string path, RecentKind kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            Remove(path, kind);
            SaveFeedback.Show(new SaveNotice(RecentFilesText.Missing(Path.GetFileName(path)), SaveFeedbackKind.Failure));
            return false;
        }

        var command = kind == RecentKind.Workspace ? "oranda.file.openWorkspaceFile" : "oranda.file.open";
        if (Execute is { } execute)
        {
            await execute(command, path).ConfigureAwait(true);
            return true;
        }

        if (_services?.GetService(typeof(ICommandRegistry)) is ICommandRegistry registry && _services.GetService(typeof(ContextKeyService)) is ContextKeyService keys)
        {
            await registry.ExecuteAsync(command, keys, path).ConfigureAwait(true);
        }

        return true;
    }

    /// <summary>메뉴 줄: 파일 이름 + 회색 폴더 경로(가운데 줄임), 툴팁 = 전체 경로. 비었으면 회색 안내 한 줄.</summary>
    internal IReadOnlyList<MenuSourceItem> MenuItems(RecentKind kind)
    {
        var list = ListOf(kind);
        if (list.Count == 0)
        {
            return [new MenuSourceItem(kind == RecentKind.Workspace ? RecentFilesText.NoWorkspaces : RecentFilesText.NoFiles)];
        }

        return [.. list.Select(p => new MenuSourceItem(
            Path.GetFileName(p),
            ShortenMiddle(Path.GetDirectoryName(p) ?? string.Empty, DetailLength),
            p,
            () => OpenAsync(p, kind)))];
    }

    /// <summary>긴 글의 가운데를 「…」 로 줄여 <paramref name="max"/> 글자에 맞춘다(앞쪽 드라이브·뒤쪽 폴더 이름이 남는다).</summary>
    internal static string ShortenMiddle(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= max || max < 3)
        {
            return text;
        }

        var head = (max - 1) / 2;
        var tail = max - 1 - head;
        return string.Concat(text.AsSpan(0, head), "…", text.AsSpan(text.Length - tail));
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private List<string> Mutable(RecentKind kind) => kind == RecentKind.Workspace ? Current.Workspaces : Current.Files;

    private void OnFileUsed(object? sender, FileUse use) => Add(use.Path, use.Workspace ? RecentKind.Workspace : RecentKind.File);

    private void Detach()
    {
        if (_host is { } host)
        {
            host.FileUsed -= OnFileUsed;
            _host = null;
        }

        foreach (var m in _menus)
        {
            m.Dispose();
        }

        _menus.Clear();
    }

    private string? ListFile
    {
        get
        {
            if (_file is null && _services?.GetService(typeof(IAppDataPaths)) is IAppDataPaths paths)
            {
                _file = Path.Combine(paths.DataDirectory, "recent-files.json");
            }

            return string.IsNullOrEmpty(_file) ? null : _file;
        }
    }

    private Lists Load()
    {
        try
        {
            if (ListFile is { } file && File.Exists(file) && JsonSerializer.Deserialize<Lists>(File.ReadAllText(file), Json) is { } read)
            {
                return new Lists
                {
                    Files = Clean(read.Files),
                    Workspaces = Clean(read.Workspaces),
                };
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 목록 파일이 손상되면 비어 있는 것으로 시작한다(MapperFolderService 와 같다).
        }

        return new Lists();
    }

    private static List<string> Clean(List<string>? list) =>
        [.. (list ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxItems)];

    private void Save()
    {
        if (ListFile is { } file)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, JsonSerializer.Serialize(Current, Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 목록을 못 저장해도 이번 실행에서는 계속 쓸 수 있다.
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary><c>recent-files.json</c> 의 내용.</summary>
    private sealed class Lists
    {
        public List<string> Files { get; set; } = [];

        public List<string> Workspaces { get; set; } = [];
    }

    private sealed class Source(RecentFilesService owner, RecentKind kind) : IMenuItemsSource
    {
        public IReadOnlyList<MenuSourceItem> Items() => owner.MenuItems(kind);
    }
}

/// <summary>앱 시작 뒤 최근 목록을 편집기 호스트에 잇는다(첫 프레임 뒤, D-006).</summary>
[OrandaService(typeof(IStartupTask))]
public sealed class RecentFilesStartup(RecentFilesService service) : IStartupTask
{
    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    public Task RunAsync(CancellationToken ct)
    {
        service.Attach(SqlEditorHost.Current);
        return Task.CompletedTask;
    }
}
