using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Core.Tests.Transfer.Copy;

internal sealed class FakePrecheckDialect : ICopyPrecheckDialect
{
    public bool SupportsReadPrivilege => false;

    public string? RecreateBlockedReason(ISession target) => null;

    public Task<IReadOnlyList<string>> ExtendedStringBlockersAsync(TableInfo source, Func<string, CancellationToken, Task<string?>> targetParameter,
                                                                   CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);

    public IReadOnlyList<(string CatalogId, string Label)> NotRestoredProbes => [];

    public string RecycleBinNote(string? tablespace) => string.Empty;

    public string? ReadConsistencyParameter => null;

    public (CheckLevel Level, string Message) ReadConsistencyHint(long estimatedSeconds, long? retention) => (CheckLevel.Pass, string.Empty);

    public string ChildRecordFoundError => "ORA-02292";
}
