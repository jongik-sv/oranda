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

    private static readonly CatalogSource[] ThreeVersions =
    [
        Entry("session/list.11.sql", "session.list", 11),
        Entry("session/list.sql", "session.list", 19),
        Entry("session/list.23.sql", "session.list", 23),
    ];

    // TSK-03-07: 11 변형을 더해도 19·23 선택은 그대로이고, 11.2 서버는 11 변형을 받는다.
    [Theory]
    [InlineData(11, 11)]
    [InlineData(12, 11)]
    [InlineData(18, 11)]
    [InlineData(19, 19)]
    [InlineData(21, 19)]
    [InlineData(23, 23)]
    [InlineData(26, 23)]
    public void Eleven_variant_is_picked_only_below_19(int server, int expected)
    {
        var catalog = new SqlCatalog(ThreeVersions);

        Assert.Equal(expected, catalog.Get("session.list", server).Header.MinVersion);
        Assert.Equal($"SELECT {expected} FROM dual", catalog.Get("session.list", server).Sql);
    }

    [Fact]
    public void Server_info_11_2_gets_11_variant()
    {
        var catalog = new SqlCatalog(ThreeVersions);
        var server = new ServerInfo("11.2.0.2.0", null, null, null, null, null, false, 1,
            new ServerCapabilities(false, false, false, false, false));

        Assert.Equal(11, catalog.Get("session.list", server).Header.MinVersion);
    }

    [Fact]
    public void Server_10_is_below_every_candidate()
    {
        var catalog = new SqlCatalog(ThreeVersions);

        var error = Assert.Throws<CatalogQueryNotFoundException>(() => catalog.Get("session.list", 10));
        Assert.Equal(10, error.MajorVersion);
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
            Entry("session/b.sql", "session.b", 10),
        ];

        var error = Assert.Throws<CatalogFormatException>(() => new SqlCatalog(sources));

        Assert.Contains(error.Problems, p => p.Path == "session/a.sql" && p.Rule == CatalogRules.HeaderMissing);
        Assert.Contains(error.Problems, p => p.Path == "session/b.sql" && p.Rule == CatalogRules.MinVersionTooLow);
    }
}
