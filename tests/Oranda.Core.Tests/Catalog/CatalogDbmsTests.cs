using Oranda.Core.Catalog;
using Oranda.Core.Connection;

namespace Oranda.Core.Tests.Catalog;

// 소유: tb-core C5(D-046). 카탈로그 파일의 dbms 머리 키와 DBMS 별 선택.

public class CatalogDbmsTests
{
    private static CatalogSource Entry(string path, string id, int minVersion, string? dbms = null, string body = "SELECT 1 FROM dual") =>
        new(path, $"-- id: {id}\n{(dbms is null ? string.Empty : $"-- dbms: {dbms}\n")}-- min-version: {minVersion}\n-- license: none\n{body}");

    private static ServerInfo Server(string version, Dbms dbms) =>
        new(version, null, null, null, null, null, false, 0, new ServerCapabilities(false, false, false, false, false), Dbms: dbms);

    [Fact]
    public void Header_without_dbms_is_oracle()
    {
        var (query, problems) = CatalogParser.Parse(Entry("session/list.sql", "session.list", 19));

        Assert.Empty(problems);
        Assert.Equal(Dbms.Oracle, query!.Header.Dbms);
    }

    [Fact]
    public void Tibero_header_parses_with_min_version_6_and_rejects_below_the_tibero_floor()
    {
        var (ok, okProblems) = CatalogParser.Parse(Entry("session/list.sql", "session.list", 6, "tibero"));
        var (low, lowProblems) = CatalogParser.Parse(Entry("session/list.sql", "session.list", 5, "tibero"));

        Assert.Empty(okProblems);
        Assert.Equal(Dbms.Tibero, ok!.Header.Dbms);
        Assert.Equal(6, ok.Header.MinVersion);
        Assert.Null(low);
        Assert.Contains(lowProblems, p => p.Rule == CatalogRules.MinVersionTooLow && p.Message.Contains("Tibero", StringComparison.Ordinal));
        Assert.Equal(6, CatalogHeader.LowestMinVersionFor(Dbms.Tibero));
        Assert.Equal(11, CatalogHeader.LowestMinVersionFor(Dbms.Oracle));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("Tibero")]
    public void Unknown_dbms_value_is_header_invalid(string value)
    {
        var (query, problems) = CatalogParser.Parse(Entry("session/list.sql", "session.list", 6, value));

        Assert.Null(query);
        Assert.Contains(problems, p => p.Rule == CatalogRules.HeaderInvalid && p.Message.Contains("oracle|tibero", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, p => p.Rule == CatalogRules.MinVersionTooLow); // 하한을 모르면 하한 검사는 건너뛴다
    }

    [Fact]
    public void Missing_dbms_with_a_tibero_min_version_hints_the_dbms_key()
    {
        var (_, problems) = CatalogParser.Parse(Entry("session/list.sql", "session.list", 6));

        var low = Assert.Single(problems, p => p.Rule == CatalogRules.MinVersionTooLow);
        Assert.Contains("dbms: tibero", low.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Selection_uses_only_files_of_the_requested_dbms()
    {
        var catalog = new SqlCatalog(
        [
            Entry("session/list.sql", "session.list", 19),
            Entry("session/list.t.sql", "session.list", 6, "tibero", "SELECT 2 FROM dual"),
            Entry("session/tibero_only.sql", "session.tibero_only", 6, "tibero"),
        ]);

        Assert.Equal(Dbms.Oracle, catalog.Get("session.list", 19).Header.Dbms);
        Assert.Equal(Dbms.Oracle, catalog.Get("session.list", Server("19.0.0.0.0", Dbms.Oracle)).Header.Dbms);
        Assert.Equal(Dbms.Tibero, catalog.Get("session.list", 6, Dbms.Tibero).Header.Dbms);

        var tibero = catalog.Get("session.list", Server("7.2.1", Dbms.Tibero) with { Version = "7.0" });
        Assert.Equal(Dbms.Tibero, tibero.Header.Dbms);
        Assert.Equal("SELECT 2 FROM dual", tibero.Sql);

        // 다른 DBMS 파일이 버전으로는 더 맞아도 고르지 않는다(버전 순서만으로는 맞는 답이 나오지 않는 경우).
        Assert.Equal(Dbms.Tibero, catalog.Get("session.list", 19, Dbms.Tibero).Header.Dbms);
        var mixed = new SqlCatalog(
        [
            Entry("session/list.sql", "session.list", 11),
            Entry("session/list.t.sql", "session.list", 12, "tibero"),
        ]);
        Assert.Equal(Dbms.Oracle, mixed.Get("session.list", 19).Header.Dbms);
    }

    [Fact]
    public void No_fallback_to_another_dbms_and_the_error_names_the_dbms()
    {
        var catalog = new SqlCatalog(
        [
            Entry("session/list.sql", "session.list", 11),
            Entry("session/tibero_only.sql", "session.tibero_only", 6, "tibero"),
        ]);

        var oracle = Assert.Throws<CatalogQueryNotFoundException>(() => catalog.Get("session.tibero_only", 19));
        Assert.Equal(Dbms.Oracle, oracle.Dbms);
        Assert.Equal("SQL 카탈로그에 서버 버전 19 에서 쓸 수 있는 'session.tibero_only' 가 없다.", oracle.Message);

        // Oracle 파일(11)이 버전으로는 맞아도(19) Tibero 요청에는 내주지 않는다.
        var tibero = Assert.Throws<CatalogQueryNotFoundException>(() => catalog.Get("session.list", Server("19.0", Dbms.Tibero)));
        Assert.Equal(Dbms.Tibero, tibero.Dbms);
        Assert.Contains("Tibero", tibero.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_id_and_min_version_in_different_dbms_is_not_duplicate_but_same_dbms_is()
    {
        CatalogSource[] distinct =
        [
            Entry("session/list.sql", "session.list", 11),
            Entry("session/list.t.sql", "session.list", 11, "tibero"),
        ];
        CatalogSource[] twice =
        [
            Entry("session/list.t.sql", "session.list", 6, "tibero"),
            Entry("session/list.t2.sql", "session.list", 6, "tibero"),
        ];

        Assert.Empty(SqlCatalog.Validate(distinct));
        Assert.Equal(2, SqlCatalog.Validate(twice).Count(p => p.Rule == CatalogRules.DuplicateId));
    }

    [Fact]
    public void Pre12c_rule_applies_to_oracle_files_only()
    {
        const string Body = "SELECT * FROM t FETCH FIRST 1 ROWS ONLY";

        var (tibero, tiberoProblems) = CatalogParser.Parse(Entry("session/list.sql", "session.list", 6, "tibero", Body));
        var (_, oracleProblems) = CatalogParser.Parse(Entry("session/list.sql", "session.list", 11, null, Body));

        Assert.Empty(tiberoProblems);
        Assert.NotNull(tibero);
        Assert.Contains(oracleProblems, p => p.Rule == CatalogRules.Pre12cFeature);
    }
}
