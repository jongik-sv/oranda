using System.Reflection;
using Oranda.Core.Catalog;

namespace Oranda.Oracle.Catalog;

/// <summary>
/// Catalog/**/*.sql 임베디드 리소스 자동 수집. Oranda.Oracle.csproj 의 EmbeddedResource 한 줄이
/// 모든 .sql 을 <c>Catalog/&lt;폴더&gt;/&lt;파일&gt;.sql</c> 이름으로 넣으므로, 기능 Task 는 파일만 더하면 된다.
/// </summary>
public static class EmbeddedCatalog
{
    /// <summary>리소스 이름 접두어(csproj LogicalName 과 같아야 한다).</summary>
    public const string ResourcePrefix = "Catalog/";

    /// <summary>Oranda.Oracle 에 실린 카탈로그. 형식 오류가 있으면 <see cref="CatalogFormatException"/>.</summary>
    public static SqlCatalog Load() => new(ReadSources(typeof(EmbeddedCatalog).Assembly));

    /// <summary>
    /// <paramref name="assembly"/> 의 <see cref="ResourcePrefix"/> 로 시작하는 .sql 리소스를 모두 읽는다.
    /// 경로 구분자는 '/' 로 맞추고, 반환 경로에서 접두어를 뗀다(예: <c>session/list.sql</c>). 이름순 정렬.
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
