using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Metadata;

/// <summary>PL/SQL 소스·컴파일 오류. 소유: TSK-08-01.</summary>
internal sealed partial class OracleMetadataProvider
{
    public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct)
        => throw new NotImplementedException("TSK-08-01");

    public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct)
        => throw new NotImplementedException("TSK-08-01");
}
