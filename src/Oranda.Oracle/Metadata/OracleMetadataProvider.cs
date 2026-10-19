using Oranda.Core.Composition;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Metadata;

/// <summary>메타데이터 제공자 본체(스키마·객체·테이블·프로시저 인자, dba_/all_/user_ 자동 선택). DDL·상세는 .Details.cs(TSK-07-03), 소스·오류는 .Source.cs(TSK-08-01). 소유: TSK-04-04. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(IMetadataProvider))]
internal sealed partial class OracleMetadataProvider : IMetadataProvider
{
    public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct)
        => throw new NotImplementedException("TSK-04-04");

    public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct)
        => throw new NotImplementedException("TSK-04-04");

    public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct)
        => throw new NotImplementedException("TSK-04-04");

    public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct)
        => throw new NotImplementedException("TSK-04-04");
}
