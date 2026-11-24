using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Schema;

/// <summary>
/// DB 정보(인스턴스·데이터베이스·버전·옵션·파라미터·NLS). 소유: TSK-07-04.
/// 구역 하나를 읽지 못해도(권한 부족 등) 나머지는 보여 준다(<see cref="DatabaseInfoSections"/>).
/// V$ 뷰를 읽을 권한이 없는 계정(ORA-00942)이면 인스턴스·데이터베이스는 SYS_CONTEXT 대신 질의로,
/// 버전은 PRODUCT_COMPONENT_VERSION(PUBLIC)이나 접속 때 얻은 버전으로, NLS 는 세션 값만으로 채운다.
/// </summary>
[OrandaService(typeof(IDatabaseInfoProvider), Dbms = Dbms.Oracle)]
internal sealed class OracleDatabaseInfoProvider : IDatabaseInfoProvider
{
    private static readonly DatabaseInfoSections.Plan[] Plan =
    [
        new("인스턴스", "dbinfo.instance", true, FallbackId: "dbinfo.instance_basic"),
        new("데이터베이스", "dbinfo.database", true, FallbackId: "dbinfo.database_basic"),
        new("버전", "dbinfo.version", false, FallbackId: "dbinfo.version_basic", VersionFromSession: true),
        new("옵션", "dbinfo.options", false),
        new("파라미터", "dbinfo.parameters", false),
        new("NLS", "dbinfo.nls", false, FallbackId: "dbinfo.nls_session"),
    ];

    public Task<IReadOnlyList<DetailSection>> GetAsync(ISession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        return DatabaseInfoSections.LoadAsync(s, Plan, async (id, token) =>
        {
            var table = await SchemaQuery.RunAsync(s, id, null, _ => { }, token);
            return new DatabaseInfoSections.Table(table.Columns, table.Rows);
        }, ct);
    }
}
