using Oranda.Core.Composition;
using Oranda.Core.Connection;

namespace Oranda.Platform.Import;

/// <summary>SQL Developer connections.json 가져오기. 소유: TSK-03-05. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(IConnectionImporter))]
internal sealed class SqlDeveloperImporter : IConnectionImporter
{
    public string SourceName
        => throw new NotImplementedException("TSK-03-05");

    public IReadOnlyList<string> FileExtensions
        => throw new NotImplementedException("TSK-03-05");

    public Task<IReadOnlyList<ImportedConnection>> ImportAsync(string path, CancellationToken ct)
        => throw new NotImplementedException("TSK-03-05");
}
