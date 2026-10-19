using Oranda.Core.Catalog;
using Oranda.Oracle.Catalog;

namespace Oranda.Oracle.Tests.Catalog;

// 소유: TSK-04-04. meta/ 폴더 카탈로그: id 존재, 라이선스 없음, 23 전용 열은 min-version 23 으로만.
[Trait("Category", "Catalog")]
public class MetaCatalogTests
{
    private static readonly SqlCatalog Catalog = EmbeddedCatalog.Load();

    [Theory]
    [InlineData("meta.schemas")]
    [InlineData("meta.objects")]
    [InlineData("meta.columns")]
    [InlineData("meta.constraints")]
    [InlineData("meta.indexes")]
    [InlineData("meta.table_comment")]
    [InlineData("meta.procedures")]
    [InlineData("meta.arguments")]
    public void Query_exists_for_19_and_needs_no_license_pack(string id)
    {
        var query = Catalog.Get(id, 19);

        Assert.Equal(CatalogLicense.None, query.Header.License);
        Assert.Equal(19, query.Header.MinVersion);
        Assert.Contains("@PFX@_", query.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Domain_column_is_selected_only_on_23_and_later()
    {
        Assert.Equal(19, Catalog.Get("meta.columns", 19).Header.MinVersion);
        Assert.Equal(19, Catalog.Get("meta.columns", 21).Header.MinVersion);
        Assert.Equal(23, Catalog.Get("meta.columns", 23).Header.MinVersion);
        Assert.Equal(23, Catalog.Get("meta.columns", 26).Header.MinVersion);
        Assert.DoesNotContain("c.domain_name", Catalog.Get("meta.columns", 19).Sql, StringComparison.Ordinal);
        Assert.Contains("c.domain_name", Catalog.Get("meta.columns", 23).Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Meta_queries_never_read_password_columns()
    {
        foreach (var header in Catalog.Entries.Where(h => h.Id.StartsWith("meta.", StringComparison.Ordinal)))
        {
            var sql = Catalog.Get(header.Id, 26).Sql;
            Assert.DoesNotContain("password", sql, StringComparison.OrdinalIgnoreCase);
        }
    }
}
