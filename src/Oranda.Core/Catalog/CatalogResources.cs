using System.Reflection;

namespace Oranda.Core.Catalog;

/// <summary>
/// DBMS 어셈블리에 임베디드된 Catalog/**/*.sql 수집(Oracle·Tibero 공용). 접두어만 보고 모으므로 폴더 목록이 없다 —
/// 새 폴더·파일은 csproj 의 EmbeddedResource 한 줄(<c>Catalog/&lt;폴더&gt;/&lt;파일&gt;.sql</c> 이름)만 따르면 된다.
/// </summary>
public static class CatalogResources
{
    /// <summary>리소스 이름 접두어(각 csproj 의 LogicalName 과 같아야 한다).</summary>
    public const string ResourcePrefix = "Catalog/";

    /// <summary>
    /// <paramref name="assembly"/> 의 <paramref name="prefix"/> 로 시작하는 .sql 리소스를 모두 읽는다.
    /// 경로 구분자는 '/' 로 맞추고 반환 경로에서 접두어를 뗀다(예: <c>session/list.sql</c>). 이름순 정렬.
    /// </summary>
    public static IReadOnlyList<CatalogSource> ReadSources(Assembly assembly, string prefix = ResourcePrefix)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(prefix);

        var sources = new List<CatalogSource>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var path = name.Replace('\\', '/');
            if (!path.StartsWith(prefix, StringComparison.Ordinal)
                || !path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"리소스 '{name}' 를 열 수 없다.");
            using var reader = new StreamReader(stream);
            sources.Add(new CatalogSource(path[prefix.Length..], reader.ReadToEnd()));
        }

        sources.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return sources;
    }
}
