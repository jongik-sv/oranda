using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Shell;
using Oranda.Core.Shell;
using Oranda.Core.Storage;

namespace Oranda.App.Features.HistoryScripts;

/// <summary>스크립트 트리 노드: 연결 폴더(루트), 하위 폴더, 스크립트 파일.</summary>
public sealed partial class ScriptNode : ObservableObject
{
    private bool _loaded;

    [ObservableProperty]
    private bool _isExpanded;

    public ScriptNode(string name, string fullPath, bool isDirectory, bool isRoot = false)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        IsRoot = isRoot;
        if (isDirectory)
        {
            Children.Add(Placeholder); // 펼치기 화살표를 보이게 하는 자리. 펼치면 실제 내용으로 바뀐다.
        }
    }

    private static ScriptNode Placeholder { get; } = new("…", string.Empty, false);

    public string Name { get; }

    public string FullPath { get; }

    public bool IsDirectory { get; }

    /// <summary>트리 앞 아이콘 키: 폴더·SQL 파일. 자리 표시(「…」)는 아이콘이 없다.</summary>
    public string? IconKey => IsDirectory ? "folder" : FullPath.Length == 0 ? null : "fileSql";

    public bool IsRoot { get; }

    public ObservableCollection<ScriptNode> Children { get; } = [];

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            LoadChildren();
        }
    }

    /// <summary>폴더 안을 읽는다(폴더 먼저, 이름 순). 이미 읽었어도 다시 읽는다.</summary>
    public void LoadChildren()
    {
        _loaded = true;
        Children.Clear();
        if (!IsDirectory || !Directory.Exists(FullPath))
        {
            return;
        }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(FullPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (!new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.Hidden))
                {
                    Children.Add(new ScriptNode(Path.GetFileName(dir), dir, true));
                }
            }

            foreach (var file in Directory.EnumerateFiles(FullPath).Where(ScriptsViewModel.IsScriptFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                Children.Add(new ScriptNode(Path.GetFileName(file), file, false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 읽을 수 없는 폴더는 비어 있는 것으로 보인다.
        }
    }

    internal bool IsLoaded => _loaded;
}

/// <summary>
/// <c>oranda.panel.scripts</c> — 로컬 폴더를 연결해 스크립트 파일 트리를 보여 준다(EXEC-09). 파일은 사용자의 폴더에 그대로 있고
/// Oranda 는 연결 목록만 기억한다. 열기는 활성 편집기에 넣기(또는 <see cref="OpenRequested"/> 를 받는 편집기), 실행은 <c>@파일</c> 로 한다.
/// </summary>
[OrandaToolPanel("oranda.panel.scripts", ToolPanelKind.Tool, DockLocation.Left, Order = 20)]
public sealed partial class ScriptsViewModel : ToolPanelViewModelBase
{
    /// <summary>트리에 보이는 스크립트 확장자.</summary>
    private static readonly string[] Extensions = [".sql", ".pls", ".pks", ".pkb", ".plb", ".prc", ".fnc", ".trg", ".vw", ".txt"];

    private readonly IServiceProvider? _services;
    private ScriptFolderList? _folders;

    [ObservableProperty]
    private ScriptNode? _selectedNode;

    [ObservableProperty]
    private string _statusMessage = "폴더를 연결하세요";

    /// <summary>시험·디자이너용: 서비스 없이 만든다(메모리 목록).</summary>
    public ScriptsViewModel()
    {
        _folders = new ScriptFolderList();
    }

    /// <summary>셸(DI)이 쓰는 생성자. 목록 파일은 처음 쓸 때 읽는다.</summary>
    public ScriptsViewModel(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>시험용: 지정한 목록을 쓴다.</summary>
    internal ScriptsViewModel(ScriptFolderList folders)
    {
        _folders = folders;
        Reload();
    }

    public ObservableCollection<ScriptNode> Roots { get; } = [];

    /// <summary>선택한 스크립트 파일 경로(폴더거나 선택이 없으면 null).</summary>
    public string? SelectedFilePath => SelectedNode is { IsDirectory: false } n && !string.IsNullOrEmpty(n.FullPath) ? n.FullPath : null;

    /// <summary>편집기가 새 탭으로 열 수 있게 알린다. 아무도 받지 않으면 활성 편집기에 내용을 넣는다.</summary>
    public event EventHandler<string>? OpenRequested;

    public static bool IsScriptFile(string path)
        => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    partial void OnSelectedNodeChanged(ScriptNode? value)
        => HistoryScriptsCommands.SelectedScriptPath = SelectedFilePath;

    /// <summary>연결 폴더 목록을 읽어 트리 루트를 다시 만든다.</summary>
    public void Reload()
    {
        var list = EnsureList();
        Roots.Clear();
        foreach (var folder in list.Folders)
        {
            var node = new ScriptNode(Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } n ? n : folder, folder, true, isRoot: true);
            Roots.Add(node);
        }

        StatusMessage = Roots.Count == 0 ? "폴더를 연결하세요" : $"폴더 {Roots.Count}개 연결됨";
    }

    /// <summary>폴더를 연결한다. 없는 폴더면 false.</summary>
    public bool AddFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            StatusMessage = "폴더를 찾을 수 없다: " + folder;
            return false;
        }

        var added = EnsureList().Add(folder);
        Reload();
        if (added && Roots.LastOrDefault() is { } node)
        {
            node.IsExpanded = true;
        }

        return added;
    }

    /// <summary>연결을 끊는다(폴더와 파일은 그대로).</summary>
    public void RemoveFolder(ScriptNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.IsRoot && EnsureList().Remove(root.FullPath))
        {
            Reload();
        }
    }

    /// <summary>선택 폴더(또는 전체)를 디스크에서 다시 읽는다.</summary>
    public void Refresh(ScriptNode? node = null)
    {
        if (node is { IsDirectory: true })
        {
            node.LoadChildren();
            return;
        }

        foreach (var r in Roots.Where(r => r.IsLoaded))
        {
            r.LoadChildren();
        }
    }

    /// <summary>파일을 연다: 편집기가 받으면 새 탭, 아니면 활성 편집기 커서 위치에 내용을 넣는다.</summary>
    public async Task OpenAsync(ScriptNode node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.IsDirectory)
        {
            node.IsExpanded = !node.IsExpanded;
            return;
        }

        if (OpenRequested is { } handler)
        {
            handler(this, node.FullPath);
            return;
        }

        if (_services is null)
        {
            return;
        }

        var text = await File.ReadAllTextAsync(node.FullPath, ct).ConfigureAwait(true);
        var ok = await HistoryScriptsCommands.SendToEditorAsync(_services, text, node.Name, ct).ConfigureAwait(true);
        StatusMessage = ok ? $"{node.Name} 을(를) 편집기에 넣었다" : "편집기가 열려 있지 않거나 취소했다";
    }

    /// <summary>파일을 <c>@파일</c> 로 활성 세션에서 실행한다.</summary>
    public async Task RunAsync(ScriptNode node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (_services is null || node.IsDirectory)
        {
            return;
        }

        var result = await HistoryScriptsCommands.RunSqlPlusAsync(_services, "@\"" + node.FullPath + "\"", skipHistory: false, ct).ConfigureAwait(true);
        if (result?.ResultSet is not null)
        {
            await result.ResultSet.DisposeAsync().ConfigureAwait(true);
        }

        StatusMessage = result is null ? "연결된 세션이 없다" : result.Status == Oranda.Core.Execution.ExecutionStatus.Succeeded ? $"{node.Name} 실행 완료" : $"{node.Name} 실행 중 오류: {result.Error?.Message}";
    }

    private ScriptFolderList EnsureList()
    {
        if (_folders is not null)
        {
            return _folders;
        }

        var paths = _services?.GetService(typeof(IAppDataPaths)) as IAppDataPaths;
        _folders = paths is null ? new ScriptFolderList() : new ScriptFolderList(paths.DataDirectory);
        return _folders;
    }
}
