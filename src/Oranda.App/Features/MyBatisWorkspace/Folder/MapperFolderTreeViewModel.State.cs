using System.Collections.Specialized;
using System.ComponentModel;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Features.MyBatisWorkspace.Folder;

// MapperFolderTreeViewModel 의 일부: 요소 노드의 탭 열림·지금 탭·변경 표시. 열린 MyBatis 작업 공간 세션의 사건(StateChanged)만 따른다(폴링 없음).
// 세션 사건이 오면 그 매퍼의 이미 만든 요소 노드만 다시 맞춘다. 노드 값은 바뀔 때만 알림이 나간다.
public sealed partial class MapperFolderTreeViewModel
{
    // 매퍼 경로 → 만든 요소 노드(파일 노드의 자식은 처음 읽을 때 만든다). Rebuild 가 비운다.
    private readonly Dictionary<string, List<MapperFolderTreeNode>> _elementNodes = new(MapperFolderIndex.PathComparer);
    private readonly Dictionary<SqlWorkspaceViewModel, MyBatisWorkspaceSession?> _sessions = new(ReferenceEqualityComparer.Instance);
    private SqlEditorHost? _hookedHost;

    private List<MapperFolderTreeNode> TrackElements(string mapperPath, List<MapperFolderTreeNode> nodes)
    {
        _elementNodes[mapperPath] = nodes;
        HookHost();
        ApplyStates(mapperPath, nodes);
        return nodes;
    }

    // 호스트는 처음 요소 노드를 만들 때 붙는다: 생성자가 Activate 를 부를 때는 시험의 Host 초기화 구문이 아직 돌지 않았다.
    private void HookHost()
    {
        if (!_active || _hookedHost is not null)
        {
            return;
        }

        _hookedHost = Host;
        _hookedHost.Workspaces.CollectionChanged += OnWorkspacesChanged;
        _hookedHost.ActiveWorkspaceChanged += OnStateActiveWorkspaceChanged;
        foreach (var ws in _hookedHost.Workspaces)
        {
            HookWorkspace(ws);
        }
    }

    private void UnhookHost()
    {
        if (_hookedHost is null)
        {
            return;
        }

        _hookedHost.Workspaces.CollectionChanged -= OnWorkspacesChanged;
        _hookedHost.ActiveWorkspaceChanged -= OnStateActiveWorkspaceChanged;
        foreach (var ws in _sessions.Keys.ToList())
        {
            UnhookWorkspace(ws);
        }

        _hookedHost = null;
    }

    private void HookWorkspace(SqlWorkspaceViewModel ws)
    {
        if (_sessions.ContainsKey(ws))
        {
            return;
        }

        ws.PropertyChanged += OnStateWorkspacePropertyChanged;
        _sessions[ws] = null;
        HookSession(ws);
    }

    private void UnhookWorkspace(SqlWorkspaceViewModel ws)
    {
        if (!_sessions.TryGetValue(ws, out var session))
        {
            return;
        }

        ws.PropertyChanged -= OnStateWorkspacePropertyChanged;
        if (session is not null)
        {
            session.StateChanged -= OnStateSessionChanged;
        }

        _sessions.Remove(ws);
    }

    private void HookSession(SqlWorkspaceViewModel ws)
    {
        var next = ws.MyBatis as MyBatisWorkspaceSession;
        if (_sessions.TryGetValue(ws, out var old) && ReferenceEquals(old, next))
        {
            return;
        }

        if (old is not null)
        {
            old.StateChanged -= OnStateSessionChanged;
        }

        if (next is not null)
        {
            next.StateChanged += OnStateSessionChanged;
        }

        _sessions[ws] = next;
    }

    private void OnWorkspacesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var ws in e.OldItems?.OfType<SqlWorkspaceViewModel>() ?? [])
        {
            UnhookWorkspace(ws);
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var ws in _sessions.Keys.Where(w => !_hookedHost!.Workspaces.Contains(w)).ToList())
            {
                UnhookWorkspace(ws);
            }
        }

        foreach (var ws in _hookedHost!.Workspaces)
        {
            HookWorkspace(ws);
        }

        RefreshAllStates();
    }

    private void OnStateWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SqlWorkspaceViewModel.MyBatis) && sender is SqlWorkspaceViewModel ws)
        {
            HookSession(ws);
            RefreshAllStates();
        }
    }

    private void OnStateActiveWorkspaceChanged(object? sender, EventArgs e) => RefreshAllStates(); // 지금 탭은 활성 작업 공간에만 있다

    private void OnStateSessionChanged(object? sender, EventArgs e)
    {
        if (sender is MyBatisWorkspaceSession session && _elementNodes.TryGetValue(session.MapperPath, out var nodes))
        {
            ApplyStates(session.MapperPath, nodes);
        }
        else
        {
            RefreshAllStates(); // 다른 이름으로 옮긴 매퍼 등: 모두 다시 맞춘다
        }
    }

    private void RefreshAllStates()
    {
        foreach (var (path, nodes) in _elementNodes)
        {
            ApplyStates(path, nodes);
        }
    }

    /// <summary>매퍼 하나의 요소 노드 표시를 그 매퍼의 세션(없으면 모두 끔)으로 맞춘다. 같은 id 는 세션의 <c>Find</c> 처럼 열 수 있는 요소를 먼저.</summary>
    private void ApplyStates(string mapperPath, List<MapperFolderTreeNode> nodes)
    {
        var session = _sessions.Values.FirstOrDefault(s => s is not null && MapperFolderIndex.PathComparer.Equals(s.MapperPath, mapperPath));
        Dictionary<string, MyBatisStatementEntry>? byId = null;
        MyBatisStatementEntry? current = null;
        if (session is not null)
        {
            byId = new Dictionary<string, MyBatisStatementEntry>(StringComparer.Ordinal);
            foreach (var entry in session.VisibleEntries.Where(e => e.Id is not null).OrderBy(e => e.CanOpen ? 0 : 1))
            {
                byId.TryAdd(entry.Id!, entry);
            }

            current = ReferenceEquals(session.Workspace, _hookedHost?.ActiveWorkspace) ? session.ActiveEntry : null;
        }

        foreach (var node in nodes)
        {
            var entry = node.ElementId is { } id && byId is not null ? byId.GetValueOrDefault(id) : null;
            bool modified = entry?.IsModified ?? false;
            node.IsOpen = entry?.IsOpen ?? false;
            node.IsCurrent = entry is { IsOpen: true } && ReferenceEquals(entry, current);
            node.IsUnsaved = modified;
            node.IsSaved = !modified && (entry?.IsSavedChange ?? false);
        }
    }
}
