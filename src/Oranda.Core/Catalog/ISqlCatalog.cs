using System.Diagnostics.CodeAnalysis;
using Oranda.Core.Connection;

namespace Oranda.Core.Catalog;

/// <summary>
/// 딕셔너리 SQL 카탈로그(TRD 4.2). 기능 코드는 SQL 문자열을 직접 갖지 않고 id 로 여기서 꺼낸다.
/// 규칙은 docs/design/catalog.md.
/// </summary>
public interface ISqlCatalog
{
    /// <summary>
    /// 같은 <paramref name="id"/> 후보 가운데 서버 주 버전 이하에서 가장 높은 min-version 을 고른다.
    /// 없으면 <see cref="CatalogQueryNotFoundException"/>.
    /// </summary>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "TRD 6장 계약 이름(ISqlCatalog.Get). VB 소비자 없음.")]
    CatalogQuery Get(string id, ServerInfo server);
}
