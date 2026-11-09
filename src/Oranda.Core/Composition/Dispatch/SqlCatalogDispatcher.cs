using Oranda.Core.Catalog;
using Oranda.Core.Connection;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>
/// 서버의 DBMS(<see cref="ServerInfo.Dbms"/>)로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(ISqlCatalog), Dbms = …)]</c>.
/// 키 등록: Oracle = <c>OracleSqlCatalog</c>, Tibero = <c>TiberoSqlCatalog</c>. 그 DBMS 용 구현이 없으면 <see cref="DbmsNotSupportedException"/>.
/// App 은 <c>DbmsServices.CatalogText</c> 로 이 구현에 닿는다(기능 코드가 <c>EmbeddedCatalog</c> 를 직접 쓰던 자리의 대체).
/// </summary>
[OrandaService(typeof(ISqlCatalog))]
public sealed class SqlCatalogDispatcher : ISqlCatalog
{
    private readonly IDbmsServiceSelector _selector;

    public SqlCatalogDispatcher(IDbmsServiceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    public CatalogQuery Get(string id, ServerInfo server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return _selector.Get<ISqlCatalog>(server.Dbms).Get(id, server);
    }
}
