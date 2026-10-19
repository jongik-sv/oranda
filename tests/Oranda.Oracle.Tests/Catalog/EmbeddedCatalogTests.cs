using Oranda.Core.Catalog;
using Oranda.Oracle.Catalog;

namespace Oranda.Oracle.Tests.Catalog;

/// <summary>
/// 빌드 검사(TRD 4.2): 제품 카탈로그(Oranda.Oracle 의 Catalog/**/*.sql)가 모두 수집되고 형식 규칙을 지키는지.
/// 수집 장치 자체는 이 시험 어셈블리에 같은 규칙으로 실은 고정 파일(Catalog/Fixtures)로 확인한다.
/// 제품 카탈로그의 파일 개수·id 목록은 단정하지 않는다(기능 Task 가 늘린다).
/// </summary>
[Trait("Category", "Catalog")]
public class EmbeddedCatalogTests
{
    private static readonly System.Reflection.Assembly ProductAssembly = typeof(EmbeddedCatalog).Assembly;

    [Fact]
    public void Collects_sql_resources_with_folder_paths()
    {
        var sources = EmbeddedCatalog.ReadSources(typeof(EmbeddedCatalogTests).Assembly);

        Assert.Contains(sources, s => s.Path == "session/list.sql");
        Assert.Contains(sources, s => s.Path == "session/list.23.sql");
        Assert.Contains(sources, s => s.Path == "sql/top.sql");
    }

    [Fact]
    public void Collected_fixtures_load_and_select_by_version()
    {
        var catalog = new SqlCatalog(EmbeddedCatalog.ReadSources(typeof(EmbeddedCatalogTests).Assembly));

        Assert.Equal("SELECT 19 AS v FROM dual", catalog.Get("session.list", 19).Sql);
        Assert.Equal("SELECT 23 AS v FROM dual", catalog.Get("session.list", 26).Sql);
        Assert.Equal(CatalogRac.Gv, catalog.Get("session.list", 19).Header.Rac);
    }

    [Fact]
    public void Every_sql_file_under_oracle_catalog_is_embedded()
    {
        var catalogDirectory = Path.Combine(FindRepositoryRoot(), "src", "Oranda.Oracle", "Catalog");
        var onDisk = Directory.EnumerateFiles(catalogDirectory, "*.sql", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(catalogDirectory, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        var embedded = EmbeddedCatalog.ReadSources(ProductAssembly).Select(s => s.Path).ToList();

        Assert.Equal(onDisk, embedded);
    }

    [Fact]
    public void Product_catalog_passes_build_checks()
    {
        var problems = SqlCatalog.Validate(EmbeddedCatalog.ReadSources(ProductAssembly));

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Product_catalog_loads()
    {
        var catalog = EmbeddedCatalog.Load();

        Assert.IsAssignableFrom<ISqlCatalog>(catalog);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Oranda.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Oranda.sln 을 찾지 못했다.");
    }
}
