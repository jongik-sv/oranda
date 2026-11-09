using Oranda.Core.Catalog;
using Oranda.Core.Composition;
using Oranda.Core.Connection;

namespace Oranda.Oracle.Catalog;

/// <summary>
/// Oracle 세션의 SQL 카탈로그(<c>Dbms = Oracle</c> 키 등록). <see cref="EmbeddedCatalog"/> 와 같은 한 벌을 쓰므로 카탈로그를 두 번 읽지 않는다.
/// <c>SqlCatalogDispatcher</c> 가 서버의 DBMS 로 이 구현을 고른다.
/// </summary>
[OrandaService(typeof(ISqlCatalog), Dbms = Dbms.Oracle)]
internal sealed class OracleSqlCatalog : ISqlCatalog
{
    public CatalogQuery Get(string id, ServerInfo server) => EmbeddedCatalog.SharedCatalog.Get(id, server);
}
