using Oranda.Core.Composition;
using Oranda.Core.History;
using Oranda.Core.Settings;
using Oranda.Core.Storage;

namespace Oranda.Platform.Storage;

/// <summary>실행 이력(history·history_fts). 소유: TSK-06-06. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(IHistoryStore))]
internal sealed class SqliteHistoryStore : IHistoryStore
{
    public Task<long> AddAsync(HistoryEntry entry, CancellationToken ct)
        => throw new NotImplementedException("TSK-06-06");

    public Task<IReadOnlyList<HistoryEntry>> SearchAsync(HistoryQuery query, CancellationToken ct)
        => throw new NotImplementedException("TSK-06-06");

    public Task DeleteAsync(IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotImplementedException("TSK-06-06");

    public Task ClearAsync(Guid? profileId, CancellationToken ct)
        => throw new NotImplementedException("TSK-06-06");

    public Task PruneAsync(HistorySettings settings, CancellationToken ct)
        => throw new NotImplementedException("TSK-06-06");
}
