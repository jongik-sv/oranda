using System.Reflection;
using System.Text.RegularExpressions;
using Oranda.Core.Catalog;

namespace Oranda.Oracle.Catalog;

/// <summary>
/// Catalog/**/*.sql 임베디드 리소스 자동 수집. Oranda.Oracle.csproj 의 EmbeddedResource 한 줄이
/// 모든 .sql 을 <c>Catalog/&lt;폴더&gt;/&lt;파일&gt;.sql</c> 이름으로 넣으므로, 기능 Task 는 파일만 더하면 된다.
/// </summary>
public static partial class EmbeddedCatalog
{
    /// <summary>리소스 이름 접두어(csproj LogicalName 과 같아야 한다).</summary>
    public const string ResourcePrefix = "Catalog/";

    /// <summary>Oranda.Oracle 에 실린 카탈로그. 형식 오류가 있으면 <see cref="CatalogFormatException"/>.</summary>
    public static SqlCatalog Load() => new(ReadSources(typeof(EmbeddedCatalog).Assembly));

    private static readonly Lazy<SqlCatalog> Shared = new(Load);

    [GeneratedRegex("@([A-Z_]+)@", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    /// <summary>서버 버전과 무관한 문장을 꺼낼 때 쓰는 버전(가장 높은 후보를 고른다).</summary>
    private const int LatestMajor = 23;

    /// <summary>
    /// 서버 버전에 따라 갈리지 않는 문장 하나를 id 로 꺼낸다. <paramref name="args"/> 의 <c>@KEY@</c> 를 값으로 바꾼다(키는 소문자로 넘겨도 되고 파일 쪽은 대문자)
    /// (바인드로 보낼 수 없는 식별자·리터럴 자리). 값은 호출자가 이스케이프해서 넘긴다.
    /// </summary>
    public static string Text(string id, params (string Key, string Value)[] args)
    {
        var sql = Shared.Value.Get(id, LatestMajor).Sql;
        if (args.Length == 0)
        {
            return sql;
        }

        // 한 번에 바꾼다: 값 안에 @KEY@ 모양 글이 있어도 다시 바뀌지 않는다.
        var map = args.ToDictionary(a => a.Key.ToUpperInvariant(), a => a.Value, StringComparer.Ordinal);
        return Placeholder().Replace(sql, m => map.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

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
