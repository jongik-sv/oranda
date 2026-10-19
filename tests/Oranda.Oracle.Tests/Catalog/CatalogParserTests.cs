using Oranda.Core.Catalog;

namespace Oranda.Oracle.Tests.Catalog;

[Trait("Category", "Catalog")]
public class CatalogParserTests
{
    [Fact]
    public void Reads_all_header_fields_and_strips_inline_comments()
    {
        var source = new CatalogSource("session/list.sql", """
            -- id: session.list
            -- min-version: 19
            -- requires: v$session, gv$session   -- 권한 판단용
            -- license: diagnostics
            -- rac: gv
            SELECT 1 FROM dual
            """);

        var (query, problems) = CatalogParser.Parse(source);

        Assert.Empty(problems);
        Assert.NotNull(query);
        Assert.Equal("session.list", query.Header.Id);
        Assert.Equal(19, query.Header.MinVersion);
        Assert.Equal(["v$session", "gv$session"], query.Header.Requires);
        Assert.Equal(CatalogLicense.Diagnostics, query.Header.License);
        Assert.Equal(CatalogRac.Gv, query.Header.Rac);
        Assert.Equal("SELECT 1 FROM dual", query.Sql);
    }

    [Fact]
    public void Optional_fields_default_to_empty_requires_and_v()
    {
        var (query, problems) = CatalogParser.Parse(new CatalogSource("meta/tables.sql",
            "-- id: meta.tables\r\n-- min-version: 23\r\n-- license: none\r\nSELECT 1 FROM dual\r\n"));

        Assert.Empty(problems);
        Assert.Empty(query!.Header.Requires);
        Assert.Equal(CatalogRac.V, query.Header.Rac);
    }

    [Fact]
    public void File_without_header_is_header_missing()
    {
        var (query, problems) = CatalogParser.Parse(new CatalogSource("session/x.sql", "SELECT 1 FROM dual"));

        Assert.Null(query);
        Assert.Contains(problems, p => p.Rule == CatalogRules.HeaderMissing);
    }

    [Theory]
    [InlineData("-- min-version: 19\n-- license: none\nSELECT 1 FROM dual")]           // id 없음
    [InlineData("-- id: session.x\n-- license: none\nSELECT 1 FROM dual")]             // min-version 없음
    [InlineData("-- id: session.x\n-- min-version: 19\nSELECT 1 FROM dual")]           // license 없음
    [InlineData("-- id: session.x\n-- min-version: 19c\n-- license: none\nSELECT 1")]  // 정수 아님
    [InlineData("-- id: session.x\n-- min-version: 19\n-- license: tune\nSELECT 1")]   // license 값
    [InlineData("-- id: session.x\n-- min-version: 19\n-- license: none\n-- rac: all\nSELECT 1")]
    [InlineData("-- id: session.x\n-- min-version: 19\n-- license: none\n-- version: 19\nSELECT 1")] // 모르는 키
    [InlineData("-- id: session.x\n-- id: session.y\n-- min-version: 19\n-- license: none\nSELECT 1")]
    [InlineData("-- id: Session.X\n-- min-version: 19\n-- license: none\nSELECT 1")]   // id 형식
    public void Invalid_header_is_header_invalid(string text)
    {
        var (query, problems) = CatalogParser.Parse(new CatalogSource("session/x.sql", text));

        Assert.Null(query);
        Assert.Contains(problems, p => p.Rule == CatalogRules.HeaderInvalid);
    }

    [Theory]
    [InlineData(18)]
    [InlineData(12)]
    [InlineData(11)]
    public void Min_version_below_19_is_rejected(int version)
    {
        var (_, problems) = CatalogParser.Parse(new CatalogSource("session/x.sql",
            $"-- id: session.x\n-- min-version: {version}\n-- license: none\nSELECT 1 FROM dual"));

        Assert.Contains(problems, p => p.Rule == CatalogRules.MinVersionTooLow);
    }

    [Theory]
    [InlineData("sql/x.sql")]
    [InlineData("x.sql")]
    public void Id_prefix_must_match_top_folder(string path)
    {
        var (_, problems) = CatalogParser.Parse(new CatalogSource(path,
            "-- id: session.x\n-- min-version: 19\n-- license: none\nSELECT 1 FROM dual"));

        Assert.Contains(problems, p => p.Rule == CatalogRules.IdFolderMismatch);
    }

    [Fact]
    public void Header_without_body_is_body_empty()
    {
        var (_, problems) = CatalogParser.Parse(new CatalogSource("session/x.sql",
            "-- id: session.x\n-- min-version: 19\n-- license: none\n"));

        Assert.Contains(problems, p => p.Rule == CatalogRules.BodyEmpty);
    }
}
