using System.Reflection;
using Oranda.Core.Catalog;

namespace Oranda.Tibero.Catalog;

/// <summary>
/// Oranda.Tibero 의 Catalog/**/*.sql 임베디드 리소스 수집. Oranda.Oracle 의 EmbeddedCatalog 와 같은 규칙
/// (csproj 의 EmbeddedResource 한 줄이 <c>Catalog/&lt;폴더&gt;/&lt;파일&gt;.sql</c> 이름으로 싣는다)이고,
/// 파일은 모두 <c>-- dbms: tibero</c> 머리를 단다. Oracle 파일과 같은 <c>id</c> 를 써도 되며 세션의 DBMS 로 갈린다.
/// </summary>
public static class TiberoEmbeddedCatalog
{
    /// <summary>리소스 이름 접두어(csproj LogicalName 과 같아야 한다).</summary>
    public const string ResourcePrefix = "Catalog/";

    /// <summary>
    /// Oranda.Tibero 에 실린 원문. Oracle 원문과 한 <see cref="SqlCatalog"/> 로 합치는 자리:
    /// <c>new SqlCatalog(oracleSources.Concat(TiberoEmbeddedCatalog.Sources))</c>. 합친 카탈로그는
    /// <see cref="SqlCatalog.Get(string, int, Oranda.Core.Connection.Dbms)"/> 가 DBMS 로 파일을 고른다.
    /// </summary>
    public static IReadOnlyList<CatalogSource> Sources => ReadSources(typeof(TiberoEmbeddedCatalog).Assembly);

    /// <summary>Tibero 원문만으로 만든 카탈로그. 형식 오류가 있으면 <see cref="CatalogFormatException"/>.</summary>
    public static SqlCatalog Load() => new(Sources);

    /// <summary>
    /// <paramref name="assembly"/> 의 <see cref="ResourcePrefix"/> 로 시작하는 .sql 리소스를 모두 읽는다.
    /// 경로 구분자는 '/' 로 맞추고 반환 경로에서 접두어를 뗀다(예: <c>meta/schemas.sql</c>). 이름순 정렬.
    /// </summary>
    public static IReadOnlyList<CatalogSource> ReadSources(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var sources = new List<CatalogSource>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var path = name.Replace('\\', '/');
            if (!path.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                || !path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"리소스 '{name}' 를 열 수 없다.");
            using var reader = new StreamReader(stream);
            sources.Add(new CatalogSource(path[ResourcePrefix.Length..], reader.ReadToEnd()));
        }

        sources.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return sources;
    }
}
