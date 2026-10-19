using Oranda.Core.Catalog;
using Oranda.Core.Connection;

namespace Oranda.Oracle.Tests.Catalog;

[Trait("Category", "Catalog")]
public class SqlCatalogTests
{
    private static CatalogSource Entry(string path, string id, int minVersion) =>
        new(path, $"-- id: {id}\n-- min-version: {minVersion}\n-- license: none\nSELECT {minVersion} FROM dual");

    private static readonly CatalogSource[] TwoVersions =
    [
        Entry("session/list.sql", "session.list", 19),
        Entry("session/list.23.sql", "session.list", 23),
    ];

    [Theory]
    [InlineData(19, 19)]
    [InlineData(21, 19)]
    [InlineData(23, 23)]
    [InlineData(26, 23)]
    public void Picks_highest_min_version_not_above_server(int server, int expected)
    {
        var catalog = new SqlCatalog(TwoVersions);

        Assert.Equal(expected, catalog.Get("session.list", server).Header.MinVersion);
    }

    [Fact]
    public void Get_by_server_info_uses_major_version()
    {
        var catalog = new SqlCatalog(TwoVersions);
        var server = new ServerInfo("23.26.0.0.0", null, null, null, null, null, false, 1,
            new ServerCapabilities(false, false, false, false, false));

        Assert.Equal(23, catalog.Get("session.list", server).Header.MinVersion);
    }

    [Fact]
    public void Server_below_every_candidate_is_not_found()
    {
        var catalog = new SqlCatalog(TwoVersions);

        var error = Assert.Throws<CatalogQueryNotFoundException>(() => catalog.Get("session.list", 18));
        Assert.Equal("session.list", error.Id);
    }

    [Fact]
    public void Unknown_id_is_not_found()
    {
        var catalog = new SqlCatalog(TwoVersions);

        Assert.Throws<CatalogQueryNotFoundException>(() => catalog.Get("session.nothing", 23));
    }

    [Fact]
    public void Same_id_and_min_version_twice_is_duplicate()
    {
        CatalogSource[] sources =
        [
            Entry("session/list.sql", "session.list", 19),
            Entry("session/list_copy.sql", "session.list", 19),
        ];

        var problems = SqlCatalog.Validate(sources);

        Assert.Equal(2, problems.Count(p => p.Rule == CatalogRules.DuplicateId));
        Assert.Throws<CatalogFormatException>(() => new SqlCatalog(sources));
    }

    [Fact]
    public void Same_id_with_different_min_versions_is_allowed()
    {
        Assert.Empty(SqlCatalog.Validate(TwoVersions));
    }

    [Fact]
    public void Format_error_is_reported_with_every_problem()
    {
        CatalogSource[] sources =
        [
            new("session/a.sql", "SELECT 1 FROM dual"),
            Entry("session/b.sql", "session.b", 18),
        ];

        var error = Assert.Throws<CatalogFormatException>(() => new SqlCatalog(sources));

        Assert.Contains(error.Problems, p => p.Path == "session/a.sql" && p.Rule == CatalogRules.HeaderMissing);
        Assert.Contains(error.Problems, p => p.Path == "session/b.sql" && p.Rule == CatalogRules.MinVersionTooLow);
    }
}
