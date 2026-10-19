using Oranda.Core.Composition;
using Oranda.Core.Storage;
using Oranda.Core.Workspaces;

namespace Oranda.Platform.Storage;

/// <summary>작업 공간·자동 저장·복구. 소유: TSK-04-02. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(IWorkspaceStore))]
internal sealed class WorkspaceStore : IWorkspaceStore
{
    public Task<StoredDocument<Workspace>?> LoadAsync(string id, CancellationToken ct)
        => throw new NotImplementedException("TSK-04-02");

    public Task SaveAsync(Workspace workspace, CancellationToken ct)
        => throw new NotImplementedException("TSK-04-02");

    public Task AutosaveAsync(Workspace workspace, CancellationToken ct)
        => throw new NotImplementedException("TSK-04-02");

    public Task<Workspace?> LoadAutosaveAsync(string id, CancellationToken ct)
        => throw new NotImplementedException("TSK-04-02");

    public Task DeleteAutosaveAsync(string id, CancellationToken ct)
        => throw new NotImplementedException("TSK-04-02");
}
