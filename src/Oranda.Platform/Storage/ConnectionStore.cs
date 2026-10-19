using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Storage;

namespace Oranda.Platform.Storage;

/// <summary>connections.json 읽기·쓰기·검증. 소유: TSK-03-04. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(IConnectionStore))]
internal sealed class ConnectionStore : IConnectionStore
{
    public Task<StoredDocument<ConnectionsDocument>> LoadAsync(CancellationToken ct)
        => throw new NotImplementedException("TSK-03-04");

    public Task SaveAsync(ConnectionsDocument document, CancellationToken ct)
        => throw new NotImplementedException("TSK-03-04");
}
