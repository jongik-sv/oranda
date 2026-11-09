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

    private const string OverloadRule =
        "CASE WHEN COUNT(*) OVER (PARTITION BY p.owner, p.object_name, p.procedure_name) > 1 " +
        "THEN TO_CHAR(ROW_NUMBER() OVER (PARTITION BY p.owner, p.object_name, p.procedure_name ORDER BY p.member_no)) END";

    [Fact]
    public void Procedures_and_arguments_number_overloads_by_the_same_rule_and_arguments_end_with_subprogram_id()
    {
        // 멤버(procedures)와 인자(arguments)가 (이름, overload) 로 묶이려면 두 문장이 overload 를 같은 식으로 만들어야 한다.
        var catalog = TiberoEmbeddedCatalog.Load();
        var procedures = Normalize(catalog.Get("meta.procedures", 6, Dbms.Tibero).Sql);
        var arguments = Normalize(catalog.Get("meta.arguments", 6, Dbms.Tibero).Sql);

        Assert.Contains(OverloadRule, procedures, StringComparison.Ordinal);
        Assert.Contains(OverloadRule, arguments, StringComparison.Ordinal);
        Assert.Contains("p.member_no AS subprogram_id, o.object_type FROM", procedures, StringComparison.Ordinal);
        Assert.Contains("a.overload AS subprogram_id FROM all_arguments a", arguments, StringComparison.Ordinal); // 맨 끝 열
        Assert.DoesNotContain("a.overload,", arguments[..arguments.IndexOf("FROM all_arguments", StringComparison.Ordinal)], StringComparison.Ordinal); // 원래 번호를 overload 로 내지 않는다
    }

    [Fact]
    public void Objects_by_prefix_filters_like_objects_and_takes_pattern_and_max_rows()
    {
        var catalog = TiberoEmbeddedCatalog.Load();
        var byPrefix = catalog.Get("meta.objects_by_prefix", 6, Dbms.Tibero);
        var objects = catalog.Get("meta.objects", 6, Dbms.Tibero);

        Assert.Equal(Dbms.Tibero, byPrefix.Header.Dbms);
        Assert.Contains("LIKE :pattern ESCAPE '\\'", byPrefix.Sql, StringComparison.Ordinal);
        Assert.Contains("ROWNUM <= :max_rows", byPrefix.Sql, StringComparison.Ordinal);
        Assert.Contains("@PFX@_objects", byPrefix.Sql, StringComparison.Ordinal);

        // 같은 객체 집합: 제외하는 유형·이름 조건이 meta.objects 와 같다.
        var exclusion = Normalize(byPrefix.Sql);
        var start = exclusion.IndexOf("o.object_type NOT IN", StringComparison.Ordinal);
        var end = exclusion.IndexOf("ORDER BY", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        Assert.Contains(exclusion[start..end].Trim(), Normalize(objects.Sql), StringComparison.Ordinal);
    }

    private static string Normalize(string sql) => System.Text.RegularExpressions.Regex.Replace(sql, @"\s+", " ").Trim();

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
