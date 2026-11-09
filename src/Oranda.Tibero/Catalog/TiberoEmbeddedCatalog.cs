using Oranda.Core.Catalog;

namespace Oranda.Tibero.Catalog;

/// <summary>
/// Oranda.Tibero 의 Catalog/**/*.sql 임베디드 리소스 수집. Oranda.Oracle 의 EmbeddedCatalog 와 같은 규칙
/// (csproj 의 EmbeddedResource 한 줄이 <c>Catalog/&lt;폴더&gt;/&lt;파일&gt;.sql</c> 이름으로 싣는다, 수집은 Core 의 <see cref="CatalogResources"/>)이고,
/// 파일은 모두 <c>-- dbms: tibero</c> 머리를 단다. Oracle 파일과 같은 <c>id</c> 를 써도 되며 세션의 DBMS 로 갈린다.
/// </summary>
public static class TiberoEmbeddedCatalog
{
    /// <summary>리소스 이름 접두어(csproj LogicalName 과 같아야 한다).</summary>
    public const string ResourcePrefix = CatalogResources.ResourcePrefix;

    /// <summary>
    /// Oranda.Tibero 에 실린 원문. Oracle 원문과 한 <see cref="SqlCatalog"/> 로 합치는 자리:
    /// <c>new SqlCatalog(oracleSources.Concat(TiberoEmbeddedCatalog.Sources))</c>. 합친 카탈로그는
    /// <see cref="SqlCatalog.Get(string, int, Oranda.Core.Connection.Dbms)"/> 가 DBMS 로 파일을 고른다.
    /// </summary>
    public static IReadOnlyList<CatalogSource> Sources => CatalogResources.ReadSources(typeof(TiberoEmbeddedCatalog).Assembly, ResourcePrefix);

    /// <summary>Tibero 원문만으로 만든 카탈로그. 형식 오류가 있으면 <see cref="CatalogFormatException"/>.</summary>
    public static SqlCatalog Load() => new(Sources);
}
