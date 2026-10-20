using System.Globalization;
using Oranda.Core.Workspaces;

namespace Oranda.App.Features.SqlEditor;

// SqlEditorHost 의 일부: 작업 공간 저장소(IWorkspaceStore) 저장·복원(TSK-10-07 에서 나눔, 동작 불변).
public sealed partial class SqlEditorHost
{
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
}
