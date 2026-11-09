using System.Reflection;
using Oranda.Core.Catalog;
using Oranda.Core.Connection;

namespace Oranda.Oracle.Catalog;

/// <summary>
/// Catalog/**/*.sql 임베디드 리소스 자동 수집. Oranda.Oracle.csproj 의 EmbeddedResource 한 줄이
/// 모든 .sql 을 <c>Catalog/&lt;폴더&gt;/&lt;파일&gt;.sql</c> 이름으로 넣으므로, 기능 Task 는 파일만 더하면 된다.
/// 수집은 Core 의 <see cref="CatalogResources"/>, <c>@KEY@</c> 채움은 <see cref="CatalogText"/> 를 쓴다.
/// </summary>
public static class EmbeddedCatalog
{
    /// <summary>리소스 이름 접두어(csproj LogicalName 과 같아야 한다).</summary>
    public const string ResourcePrefix = CatalogResources.ResourcePrefix;

    /// <summary>Oranda.Oracle 에 실린 카탈로그. 형식 오류가 있으면 <see cref="CatalogFormatException"/>.</summary>
    public static SqlCatalog Load() => new(ReadSources(typeof(EmbeddedCatalog).Assembly));

    private static readonly Lazy<SqlCatalog> Shared = new(Load);

    /// <summary><see cref="Text"/> 와 <c>OracleSqlCatalog</c> 가 함께 쓰는 한 벌(두 번 읽지 않는다).</summary>
    internal static SqlCatalog SharedCatalog => Shared.Value;

    /// <summary>서버 버전과 무관한 문장을 꺼낼 때 쓰는 버전(가장 높은 후보를 고른다).</summary>
    private const int LatestMajor = 23;

    /// <summary>
    /// 서버 버전에 따라 갈리지 않는 문장 하나를 id 로 꺼낸다. <paramref name="args"/> 의 <c>@KEY@</c> 를 값으로 바꾼다(<see cref="CatalogText.Fill"/>)
    /// (바인드로 보낼 수 없는 식별자·리터럴 자리). 값은 호출자가 이스케이프해서 넘긴다.
    /// </summary>
    public static string Text(string id, params (string Key, string Value)[] args)
        => CatalogText.Fill(Shared.Value.Get(id, LatestMajor, Dbms.Oracle).Sql, args);

    /// <summary>
    /// <paramref name="assembly"/> 의 <see cref="ResourcePrefix"/> 로 시작하는 .sql 리소스를 모두 읽는다(<see cref="CatalogResources.ReadSources"/>).
    /// 반환 경로에서 접두어를 뗀다(예: <c>session/list.sql</c>). 이름순 정렬.
    /// </summary>
    public static IReadOnlyList<CatalogSource> ReadSources(Assembly assembly)
        => CatalogResources.ReadSources(assembly, ResourcePrefix);
}
