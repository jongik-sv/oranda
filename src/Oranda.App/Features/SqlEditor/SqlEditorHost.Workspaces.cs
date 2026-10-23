using System.Globalization;
using Oranda.App.Features.MyBatisWorkspace.Hooks;

namespace Oranda.App.Features.SqlEditor;

// SqlEditorHost 의 일부: 작업 공간 열기·닫기·이름·순서(TSK-10-07 에서 나눔, 동작 불변).
public sealed partial class SqlEditorHost
{
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

        if (_store is not null && workspace.MyBatis is not null)
        {
            // design.md 5.6: 복구 상태(곁 파일)는 세션이 붙어 있는 동안 저장본으로 남긴다(RemoveWorkspace 가 세션을 뗀다).
            foreach (var participant in MyBatisHooks.RecoveryParticipants)
            {
                await participant.SaveAsync(workspace, MyBatisRecoveryTarget.Saved, ct).ConfigureAwait(true);
            }

            index = Workspaces.IndexOf(workspace);
            if (index < 0)
            {
                return; // 저장하는 사이 다른 경로로 이미 닫혔다
            }
        }

        var snapshot = workspace.ToWorkspace();
        RemoveWorkspace(workspace, index);
        if (_store is not null)
        {
            await _store.SaveAsync(snapshot, ct).ConfigureAwait(true);
            await DeleteAutosaveAsync(snapshot.Id, ct).ConfigureAwait(true);
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
        _activationOrder.Remove(workspace);
        workspace.MyBatis = null; // design.md 10.1: 세션을 뗀다(문서 종류가 Sql 로 돌아가고 도구 창 규칙·구독이 풀린다)
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
}
