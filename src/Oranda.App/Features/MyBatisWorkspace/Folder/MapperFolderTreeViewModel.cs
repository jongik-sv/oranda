using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.Localization;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;

namespace Oranda.App.Features.MyBatisWorkspace.Folder;

/// <summary>폴더 트리 노드 종류.</summary>
public enum MapperFolderNodeKind
{
    Folder,
    File,
    Element,
}

/// <summary>폴더 트리 노드 하나(폴더 · 매퍼 파일 · 최상위 요소).</summary>
public sealed partial class MapperFolderTreeNode : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded;

    internal MapperFolderTreeNode(MapperFolderNodeKind kind, string name, string fullPath)
    {
        Kind = kind;
        Name = name;
        FullPath = fullPath;
    }

    public MapperFolderNodeKind Kind { get; }

    /// <summary>보이는 이름(폴더·파일 이름, 요소 id).</summary>
    public string Name { get; }

    /// <summary>폴더·파일 절대 경로(요소는 그 매퍼 경로).</summary>
    public string FullPath { get; }

    /// <summary>이름 오른쪽 흐린 글(파일 = namespace, 요소 = 요소 이름).</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>툴팁(파일 = 경로, 요소 = namespace.id).</summary>
    public string ToolTipText { get; init; } = string.Empty;

    /// <summary>아이콘 키(요소는 없음).</summary>
    public string? IconKey => Kind switch
    {
        MapperFolderNodeKind.Folder => "folder",
        MapperFolderNodeKind.File => "docEdit",
        _ => null,
    };

    /// <summary>요소 노드의 namespace·id·종류.</summary>
    public string? Namespace { get; init; }

    public string? ElementId { get; init; }

    public MyBatisStatementKind ElementKind { get; init; }

    /// <summary>sql 조각·resultMap 이라 「쓰는 곳 찾기」를 할 수 있다.</summary>
    public bool CanFindUsages => Kind == MapperFolderNodeKind.Element && ElementId is not null
        && ElementKind is MyBatisStatementKind.Sql or MyBatisStatementKind.ResultMap;

    public ObservableCollection<MapperFolderTreeNode> Children { get; } = [];
}

/// <summary>「쓰는 곳 찾기」 결과 한 줄.</summary>
public sealed record MapperFolderUsageRow(string MapperPath, string ElementId, string Text);

/// <summary>
/// 「문장」 창 폴더 트리 모드의 ViewModel(MB-10, TSK-03-07): 폴더 > 파일 > 요소. 두 번 누르면 파일·문장을 MyBatis 작업 공간으로 연다.
/// sql 조각·resultMap 은 우클릭 「쓰는 곳 찾기」로 폴더 안에서 쓰는 곳을 아래 목록에 보인다. 색인이 바뀌면 트리를 다시 만들고 펼침은 경로로 기억한다.
/// </summary>
public sealed partial class MapperFolderTreeViewModel : ObservableObject, IDisposable
{
    private const string KeyPrefix = "MyBatis.folder.";

    private readonly MapperFolderService _folder;
    private readonly HashSet<string> _expanded = new(MapperFolderIndex.PathComparer);
    private IShellText? _languageSource;
    private SqlEditorHost? _host;
    private bool _active;

    [ObservableProperty]
    private string _rootPath = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private MapperFolderTreeNode? _selectedNode;

    [ObservableProperty]
    private string _usageTitle = string.Empty;

    [ObservableProperty]
    private bool _hasUsages;

    public MapperFolderTreeViewModel(MapperFolderService folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        _folder = folder;
        Activate();
    }

    /// <summary>다루는 호스트. 기본은 <see cref="SqlEditorHost.Current"/>.</summary>
    public SqlEditorHost Host
    {
        get => _host ?? SqlEditorHost.Current;
        init => _host = value;
    }

    /// <summary>화면 글자 조회. 기본은 셸의 <see cref="UiText.Current"/>, 없으면 한국어 표(시험).</summary>
    internal IShellText? Text { get; init; }

    /// <summary>폴더 바로 아래 노드(하위 폴더 먼저, 그다음 파일).</summary>
    public ObservableCollection<MapperFolderTreeNode> Roots { get; } = [];

    /// <summary>「쓰는 곳 찾기」 결과.</summary>
    public ObservableCollection<MapperFolderUsageRow> Usages { get; } = [];

    private IShellText? ActiveText => Text ?? UiText.Current;

    /// <summary>파일 노드면 매퍼를, 요소 노드면 그 문장 탭을 연다. 폴더 노드는 접고 편다.</summary>
    [RelayCommand]
    public async Task OpenNodeAsync(MapperFolderTreeNode? node)
    {
        switch (node)
        {
            case { Kind: MapperFolderNodeKind.Folder }:
                node.IsExpanded = !node.IsExpanded;
                break;
            case { Kind: MapperFolderNodeKind.File }:
                await MapperFolderOpener.OpenMapperAsync(Host, node.FullPath, CancellationToken.None).ConfigureAwait(true);
                break;
            case { Kind: MapperFolderNodeKind.Element, ElementId: { } id }:
                await MapperFolderOpener.OpenStatementAsync(Host, node.FullPath, id, CancellationToken.None).ConfigureAwait(true);
                break;
        }
    }

    /// <summary>쓰는 곳 한 줄을 연다.</summary>
    [RelayCommand]
    public Task OpenUsageAsync(MapperFolderUsageRow? row) => row is null
        ? Task.CompletedTask
        : MapperFolderOpener.OpenStatementAsync(Host, row.MapperPath, row.ElementId, CancellationToken.None);

    /// <summary>sql 조각·resultMap 노드를 쓰는 곳을 아래 목록에 채운다.</summary>
    [RelayCommand]
    public void FindUsages(MapperFolderTreeNode? node)
    {
        node ??= SelectedNode;
        if (node is not { CanFindUsages: true, ElementId: { } id } || _folder.Index is not { } index)
        {
            return;
        }

        Usages.Clear();
        foreach (var u in index.FindUsages(node.Namespace, id, node.ElementKind))
        {
            var name = u.MapperNamespace is null ? u.ElementId : u.MapperNamespace + "." + u.ElementId;
            Usages.Add(new MapperFolderUsageRow(u.MapperPath, u.ElementId, u.Count > 1 ? $"{name} ×{u.Count}" : name));
        }

        UsageTitle = string.Format(CultureInfo.CurrentCulture, T("usage.title"), id, Usages.Count);
        HasUsages = true;
    }

    /// <summary>쓰는 곳 목록을 닫는다.</summary>
    [RelayCommand]
    public void CloseUsages()
    {
        Usages.Clear();
        HasUsages = false;
    }

    /// <summary>폴더를 다시 훑는다.</summary>
    [RelayCommand]
    public Task RefreshAsync() => _folder.Index is { } index ? _folder.OpenAsync(index.Root, CancellationToken.None) : Task.CompletedTask;

    /// <summary>폴더를 닫는다.</summary>
    [RelayCommand]
    public void CloseFolder() => _folder.Close();

    /// <summary>폴더·언어 사건을 받기 시작하고 트리를 지금 색인으로 맞춘다(여러 번 불러도 한 번만 구독). View 가 화면에 붙을 때 부른다.</summary>
    public void Activate()
    {
        if (_active)
        {
            return;
        }

        _active = true;
        _folder.Changed += OnFolderChanged;
        _languageSource = ActiveText;
        if (_languageSource is not null)
        {
            _languageSource.LanguageChanged += OnLanguageChanged;
        }

        Rebuild();
    }

    /// <summary>사건 구독을 푼다(View 가 화면에서 떨어질 때). 다시 <see cref="Activate"/> 하면 이어진다.</summary>
    public void Deactivate()
    {
        if (!_active)
        {
            return;
        }

        _active = false;
        _folder.Changed -= OnFolderChanged;
        if (_languageSource is not null)
        {
            _languageSource.LanguageChanged -= OnLanguageChanged;
            _languageSource = null;
        }
    }

    public void Dispose() => Deactivate();

    private void OnFolderChanged(object? sender, EventArgs e) => Rebuild();

    private void OnLanguageChanged(object? sender, EventArgs e) => UpdateStatus();

    private void Rebuild()
    {
        RememberExpanded(Roots);
        Roots.Clear();
        var index = _folder.Index;
        RootPath = index?.Root ?? string.Empty;
        if (index is not null)
        {
            var folders = new Dictionary<string, MapperFolderTreeNode>(MapperFolderIndex.PathComparer);
            foreach (var file in index.Files)
            {
                var dir = Path.GetDirectoryName(file.Path) ?? index.Root;
                ChildrenOf(dir, index.Root, folders).Add(FileNode(file));
            }
        }

        if (HasUsages)
        {
            CloseUsages(); // 색인이 바뀌면 옛 결과는 맞지 않을 수 있다
        }

        UpdateStatus();
    }

    // 폴더 경로의 노드 목록(루트면 Roots). 없는 중간 폴더 노드는 만든다. 하위 폴더는 파일보다 앞에 둔다.
    private ObservableCollection<MapperFolderTreeNode> ChildrenOf(string dir, string root, Dictionary<string, MapperFolderTreeNode> folders)
    {
        if (MapperFolderIndex.PathComparer.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar)))
        {
            return Roots;
        }

        if (!folders.TryGetValue(dir, out var node))
        {
            var parent = ChildrenOf(Path.GetDirectoryName(dir) ?? root, root, folders);
            node = new MapperFolderTreeNode(MapperFolderNodeKind.Folder, Path.GetFileName(dir), dir)
            {
                ToolTipText = dir,
                IsExpanded = _expanded.Contains(dir),
            };
            int at = 0;
            while (at < parent.Count && parent[at].Kind == MapperFolderNodeKind.Folder)
            {
                at++;
            }

            parent.Insert(at, node);
            folders[dir] = node;
        }

        return node.Children;
    }

    private MapperFolderTreeNode FileNode(MapperFolderFile file)
    {
        var node = new MapperFolderTreeNode(MapperFolderNodeKind.File, Path.GetFileName(file.Path), file.Path)
        {
            Detail = file.Namespace ?? string.Empty,
            ToolTipText = file.Path,
            IsExpanded = _expanded.Contains(file.Path),
        };
        foreach (var e in file.Elements)
        {
            node.Children.Add(new MapperFolderTreeNode(MapperFolderNodeKind.Element, e.Id ?? e.Name, file.Path)
            {
                Detail = e.Name,
                ToolTipText = file.Namespace is null || e.Id is null ? e.Id ?? e.Name : file.Namespace + "." + e.Id,
                Namespace = file.Namespace,
                ElementId = e.Id,
                ElementKind = e.Kind,
            });
        }

        return node;
    }

    private void RememberExpanded(IEnumerable<MapperFolderTreeNode> nodes)
    {
        foreach (var node in nodes.Where(n => n.Kind != MapperFolderNodeKind.Element))
        {
            if (node.IsExpanded)
            {
                _expanded.Add(node.FullPath);
            }
            else
            {
                _expanded.Remove(node.FullPath);
            }

            RememberExpanded(node.Children);
        }
    }

    private void UpdateStatus()
    {
        StatusText = _folder.Index switch
        {
            null => string.Empty,
            _ when _folder.IsScanning => T("status.scanning"),
            { SkippedCount: > 0 } index => string.Format(CultureInfo.CurrentCulture, T("status.skipped"), index.Files.Count, index.SkippedCount),
            { } index => string.Format(CultureInfo.CurrentCulture, T("status.count"), index.Files.Count),
        };
    }

    private string T(string key)
    {
        var full = KeyPrefix + key;
        return ActiveText?.Lookup(full) ?? StringCatalog.Get("ko", full) ?? full;
    }
}
