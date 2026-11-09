using Oranda.Core.Catalog;
using Oranda.Core.Connection;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>
/// 서버의 DBMS(<see cref="ServerInfo.Dbms"/>)로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(ISqlCatalog), Dbms = …)]</c>.
/// 아직 키 등록한 카탈로그가 없다(Oracle 쪽이 나중에 단다) — 그 전에는 <see cref="DbmsNotSupportedException"/>.
/// 지금 컨테이너에서 <see cref="ISqlCatalog"/> 를 꺼내는 곳은 없다(기능은 <c>EmbeddedCatalog</c> 를 직접 쓴다).
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
