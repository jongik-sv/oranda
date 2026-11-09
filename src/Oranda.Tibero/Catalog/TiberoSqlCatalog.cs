using Oranda.Core.Catalog;
using Oranda.Core.Composition;
using Oranda.Core.Connection;

namespace Oranda.Tibero.Catalog;

/// <summary>
/// Tibero 세션의 SQL 카탈로그(<c>Dbms = Tibero</c> 키 등록). Oranda.Tibero 에 실린 원문만으로 만들고, 처음 쓸 때 읽는다.
/// <c>SqlCatalogDispatcher</c> 가 서버의 DBMS 로 이 구현을 고른다.
/// </summary>
[OrandaService(typeof(ISqlCatalog), Dbms = Dbms.Tibero)]
internal sealed class TiberoSqlCatalog : ISqlCatalog
{
    private readonly Lazy<SqlCatalog> _catalog = new(TiberoEmbeddedCatalog.Load);

    public CatalogQuery Get(string id, ServerInfo server) => _catalog.Value.Get(id, server);
}
