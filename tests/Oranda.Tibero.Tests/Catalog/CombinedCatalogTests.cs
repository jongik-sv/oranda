using Oranda.Core.Catalog;
using Oranda.Core.Connection;
using Oranda.Oracle;
using Oranda.Oracle.Catalog;
using Oranda.Tibero.Catalog;

namespace Oranda.Tibero.Tests.Catalog;

/// <summary>실제 Oracle 원문과 Tibero 원문을 한 <see cref="SqlCatalog"/> 로 합쳐도 형식·중복 검사를 통과하고 DBMS 로 갈리는지(S3).</summary>
public class CombinedCatalogTests
{
    private static SqlCatalog Combined() =>
        new(EmbeddedCatalog.ReadSources(typeof(OracleAssembly).Assembly).Concat(TiberoEmbeddedCatalog.Sources));

    [Fact]
    public void Combined_sources_pass_validation()
    {
        var sources = EmbeddedCatalog.ReadSources(typeof(OracleAssembly).Assembly).Concat(TiberoEmbeddedCatalog.Sources).ToList();
        Assert.NotEmpty(sources);
        Assert.Empty(SqlCatalog.Validate(sources));
    }

    [Fact]
    public void Same_id_is_chosen_by_dbms_and_never_crosses_over()
    {
        var catalog = Combined();

        var oracle = catalog.Get("meta.schemas", 19, Dbms.Oracle);
        var tibero = catalog.Get("meta.schemas", 6, Dbms.Tibero);

        Assert.Equal(Dbms.Oracle, oracle.Header.Dbms);
        Assert.Equal(Dbms.Tibero, tibero.Header.Dbms);
        Assert.Equal(oracle.Header.Id, tibero.Header.Id); // 같은 id 가 DBMS 로만 갈린다(어셈블리마다 Catalog/ 루트가 달라 경로는 같을 수 있다)
        Assert.Equal(6, tibero.Header.MinVersion);
    }

    [Fact]
    public void An_id_only_oracle_has_is_not_served_to_tibero()
    {
        var catalog = Combined();
        var oracleOnly = catalog.Entries.First(e => e.Dbms == Dbms.Oracle && !catalog.Entries.Any(o => o.Id == e.Id && o.Dbms == Dbms.Tibero));

        Assert.Throws<CatalogQueryNotFoundException>(() => catalog.Get(oracleOnly.Id, 7, Dbms.Tibero));
    }
}
