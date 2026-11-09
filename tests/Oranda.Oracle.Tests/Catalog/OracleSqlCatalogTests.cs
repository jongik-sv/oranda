using System.Reflection;
using Oranda.Core.Catalog;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Oracle.Catalog;

namespace Oranda.Oracle.Tests.Catalog;

// 소유: tb-base K3. Oracle 카탈로그 계약(키 등록·EmbeddedCatalog 와 같은 한 벌)과 공용 리소스 수집.
[Trait("Category", "Catalog")]
public class OracleSqlCatalogTests
{
    private static ServerInfo Server(string version) =>
        new(version, "FREE", "localhost", "FREEPDB1", "FREE", null, false, 1, new ServerCapabilities(false, true, true, false, false));

    [Fact]
    public void Returns_the_same_statement_as_EmbeddedCatalog()
    {
        var catalog = new OracleSqlCatalog();

        var viaService = catalog.Get("app.kill_session", Server("19.0.0.0.0"));

        Assert.Equal(Dbms.Oracle, viaService.Header.Dbms);
        Assert.Equal(EmbeddedCatalog.Text("app.kill_session"), viaService.Sql);
    }

    [Fact]
    public void Is_registered_under_the_oracle_key()
    {
        var attribute = typeof(OracleSqlCatalog).GetCustomAttributes<OrandaServiceAttribute>(inherit: false).Single();

        Assert.Equal(typeof(ISqlCatalog), attribute.ServiceType);
        Assert.Equal(Dbms.Oracle, attribute.ForDbms);
    }

    [Fact]
    public void CatalogResources_reads_by_prefix_and_strips_it()
    {
        var assembly = typeof(EmbeddedCatalog).Assembly;

        var all = CatalogResources.ReadSources(assembly);
        var app = CatalogResources.ReadSources(assembly, "Catalog/app/");

        Assert.Equal(EmbeddedCatalog.ReadSources(assembly).Select(s => s.Path), all.Select(s => s.Path));
        Assert.Contains(all, s => s.Path == "app/kill_session.sql");
        Assert.Contains(app, s => s.Path == "kill_session.sql");
        Assert.All(app, s => Assert.DoesNotContain('/', s.Path));
        Assert.Empty(CatalogResources.ReadSources(assembly, "NoSuchPrefix/"));
    }
}
