using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Metadata;

/// <summary>객체 상세·DDL(DBMS_METADATA). 소유: TSK-07-03.</summary>
internal sealed partial class OracleMetadataProvider
{
    public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct)
        => throw new NotImplementedException("TSK-07-03");

    public Task<ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct)
        => throw new NotImplementedException("TSK-07-03");
}
