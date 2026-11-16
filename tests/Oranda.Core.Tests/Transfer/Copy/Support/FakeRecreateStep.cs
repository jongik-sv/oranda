using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Core.Tests.Transfer.Copy;

internal sealed class FakeRecreateStep : ICopyRecreateStep
{
    public Task<PreservedDdl> CaptureAsync(ISession target, string owner, string table, CancellationToken ct) => Task.FromResult(PreservedDdl.Empty);

    public Task<IReadOnlyList<(string Parent, string Ddl, string? Name)>> SourceForeignKeysAsync(ISession source, string sourceOwner, string table,
        IReadOnlySet<string> parents, string targetOwner, CancellationToken ct) => Task.FromResult<IReadOnlyList<(string Parent, string Ddl, string? Name)>>([]);

    public Task<HashSet<string>> RecycleBinAsync(ISession target, string table, CancellationToken ct) => throw new NotSupportedException();
}
