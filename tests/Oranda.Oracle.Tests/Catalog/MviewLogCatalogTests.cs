using Oranda.Core.Catalog;
using Oranda.Oracle.Catalog;

namespace Oranda.Oracle.Tests.Catalog;

// 소유: TSK-13-08. 스키마 브라우저 Snapshot Log 폴더가 쓰는 meta.mview_log.
[Trait("Category", "Catalog")]
public class MviewLogCatalogTests
{
    private static readonly SqlCatalog Catalog = EmbeddedCatalog.Load();

    [Fact]
    public void Query_exists_for_19_without_license_and_binds_log_owner_and_master()
    {
        var query = Catalog.Get("meta.mview_log", 19);

        Assert.Equal(CatalogLicense.None, query.Header.License);
        Assert.Equal(11, query.Header.MinVersion); // 11g 에도 all_mview_logs 의 같은 열이 있다(TSK-03-07)
        Assert.Contains("@PFX@_mview_logs", query.Sql, StringComparison.Ordinal);
        Assert.Contains("l.log_owner = :owner", query.Sql, StringComparison.Ordinal);
        Assert.Contains("l.master = :name", query.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("FETCH FIRST", query.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Server_older_than_11_has_no_variant_and_the_loader_reports_it_instead_of_crashing()
    {
        // 트리 제공자가 이 예외를 잡아 폴더만 비운다. 11.2 는 같은 쿼리를 쓴다(TSK-03-07).
        Assert.Equal(11, Catalog.Get("meta.mview_log", 11).Header.MinVersion);
        Assert.Throws<CatalogQueryNotFoundException>(() => Catalog.Get("meta.mview_log", 10));
    }
}
