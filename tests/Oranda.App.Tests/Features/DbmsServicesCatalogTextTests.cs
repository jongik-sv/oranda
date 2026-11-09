using Oranda.App.Features;
using Oranda.App.Shell.Composition;
using Oranda.Core.Catalog;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Oracle.Catalog;
using Oranda.Testing;

namespace Oranda.App.Tests.Features;

// 소유: tb-base K3(D-046). App 이 EmbeddedCatalog.Text 대신 쓰는 DbmsServices.CatalogText — 실제 앱 조립에서
// Oracle 세션이 키 등록된 ISqlCatalog(OracleSqlCatalog)로 가고, 고를 수 없으면 null(기능 끄기)이다.
public class DbmsServicesCatalogTextTests
{
    private static ServiceContainer Compose() =>
        AppComposition.Create(new AppCompositionOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "oranda-k3-" + Guid.NewGuid().ToString("N")) });

    [Fact]
    public void Oracle_session_gets_the_same_text_as_embedded_catalog()
    {
        using var c = Compose();
        var session = new FakeSession();

        var sql = DbmsServices.CatalogText(c, session, "app.kill_session", ("id", "12,34"));

        Assert.Equal(EmbeddedCatalog.Text("app.kill_session", ("id", "12,34")), sql);
        Assert.Contains("'12,34'", sql, StringComparison.Ordinal);
        Assert.NotNull(c.GetRequiredService<IDbmsServiceSelector>().Find<ISqlCatalog>(Dbms.Oracle));
    }

    [Fact]
    public void Unknown_id_or_missing_session_or_services_give_null()
    {
        using var c = Compose();
        var session = new FakeSession();

        Assert.Null(DbmsServices.CatalogText(c, session, "app.no_such_query"));
        Assert.Null(DbmsServices.CatalogText(c, null, "app.kill_session"));
        Assert.Null(DbmsServices.CatalogText(null, session, "app.kill_session"));
    }

    [Fact]
    public void Server_version_below_the_file_min_version_gives_null()
    {
        using var c = Compose();
        var session = new FakeSession(server: TestData.Server("10.2.0.5.0")); // app.kill_session = min-version 11

        Assert.Null(DbmsServices.CatalogText(c, session, "app.kill_session"));
    }
}
