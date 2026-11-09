using Oranda.Core.Catalog;
using Oranda.Core.Connection;
using Oranda.Tibero.Catalog;

namespace Oranda.Tibero.Tests.Catalog;

[Trait("Category", "Catalog")]
public class TiberoEmbeddedCatalogTests
{
    [Fact]
    public void Every_sql_file_under_tibero_catalog_is_embedded()
    {
        var catalogDirectory = Path.Combine(FindRepositoryRoot(), "src", "Oranda.Tibero", "Catalog");
        var onDisk = Directory.EnumerateFiles(catalogDirectory, "*.sql", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(catalogDirectory, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(onDisk);
        Assert.Equal(onDisk, TiberoEmbeddedCatalog.Sources.Select(s => s.Path).ToList());
    }

    [Fact]
    public void Product_catalog_passes_build_checks_and_every_file_targets_tibero()
    {
        var problems = SqlCatalog.Validate(TiberoEmbeddedCatalog.Sources);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

        var catalog = TiberoEmbeddedCatalog.Load();
        Assert.NotEmpty(catalog.Entries);
        Assert.All(catalog.Entries, h => Assert.Equal(Dbms.Tibero, h.Dbms));
    }

    [Fact]
    public void Sample_is_selected_for_tibero_6_and_7_but_never_for_oracle()
    {
        var catalog = TiberoEmbeddedCatalog.Load();

        Assert.Equal(Dbms.Tibero, catalog.Get("meta.schemas", 6, Dbms.Tibero).Header.Dbms);
        Assert.Equal(Dbms.Tibero, catalog.Get("meta.schemas", 7, Dbms.Tibero).Header.Dbms);
        Assert.Throws<CatalogQueryNotFoundException>(() => catalog.Get("meta.schemas", 19, Dbms.Oracle));
        Assert.Throws<CatalogQueryNotFoundException>(() => catalog.Get("meta.schemas", 5, Dbms.Tibero));
    }

    [Fact]
    public void Sources_merge_with_oracle_style_sources_and_dbms_picks_the_file()
    {
        // Oracle 원문(여기서는 같은 id 의 대용 파일)과 합쳐도 중복 검사에 걸리지 않고 DBMS 로 갈린다.
        var oracle = new CatalogSource("meta/schemas.sql",
            "-- id: meta.schemas\n-- min-version: 11\n-- license: none\nSELECT 'oracle' FROM dual");

        var merged = new SqlCatalog(new[] { oracle }.Concat(TiberoEmbeddedCatalog.Sources));

        Assert.Equal("SELECT 'oracle' FROM dual", merged.Get("meta.schemas", 19, Dbms.Oracle).Sql);
        Assert.Contains("@PFX@_users", merged.Get("meta.schemas", 7, Dbms.Tibero).Sql, StringComparison.Ordinal);
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
