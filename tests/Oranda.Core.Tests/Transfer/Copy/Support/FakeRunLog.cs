using Oranda.Core.Transfer.Copy;

namespace Oranda.Core.Tests.Transfer.Copy;

internal sealed class FakeRunLog(ICopyRunRecord record) : ICopyRunLog
{
    public Task<ICopyRunRecord> BeginAsync(CopyRunHeader header, CancellationToken ct) => Task.FromResult<ICopyRunRecord>(record);

    public Task<IReadOnlyList<UnfinishedRun>> FindUnfinishedAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<UnfinishedRun>>([]);

    public Task MarkRecoveredAsync(string runFile, CancellationToken ct) => Task.CompletedTask;
}
