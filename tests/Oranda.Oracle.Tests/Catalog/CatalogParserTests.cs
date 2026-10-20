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
    [InlineData(10)]
    [InlineData(9)]
    [InlineData(0)]
    public void Min_version_below_11_is_rejected(int version)
    {
        var (_, problems) = CatalogParser.Parse(new CatalogSource("session/x.sql",
            $"-- id: session.x\n-- min-version: {version}\n-- license: none\nSELECT 1 FROM dual"));

        Assert.Contains(problems, p => p.Rule == CatalogRules.MinVersionTooLow);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(18)]
    public void Min_version_11_and_above_is_accepted(int version)
    {
        var (query, problems) = CatalogParser.Parse(new CatalogSource("session/x.sql",
            $"-- id: session.x\n-- min-version: {version}\n-- license: none\nSELECT 1 FROM dual"));

        Assert.Empty(problems);
        Assert.Equal(version, query!.Header.MinVersion);
    }

    // TSK-03-07: 11g 변형(min-version 12 미만)은 12c 이후 문법·열·뷰를 쓸 수 없다.
    [Theory]
    [InlineData("SELECT 1 FROM dual FETCH FIRST 10 ROWS ONLY", "FETCH FIRST")]
    [InlineData("SELECT 1 FROM dual fetch  next :n rows only", "FETCH  NEXT")]
    [InlineData("SELECT 1 FROM dual OFFSET 5 ROWS", "OFFSET 5 ROWS")]
    [InlineData("SELECT * FROM (SELECT x FROM t FETCH FIRST :max_rows ROWS ONLY) r", "FETCH FIRST")]
    [InlineData("SELECT c.identity_column FROM all_tab_cols c", "IDENTITY_COLUMN")]
    [InlineData("SELECT 1 FROM all_tab_cols c WHERE c.user_generated = 'YES'", "USER_GENERATED")]
    [InlineData("SELECT c.search_condition_vc FROM all_constraints c", "SEARCH_CONDITION_VC")]
    [InlineData("SELECT o.editionable FROM all_objects o", "EDITIONABLE")]
    [InlineData("SELECT v.banner_full FROM v$version v", "BANNER_FULL")]
    [InlineData("SELECT i.version_full FROM v$instance i", "VERSION_FULL")]
    [InlineData("SELECT d.cdb FROM v$database d", ".CDB")]
    [InlineData("SELECT i.edition FROM v$instance i", ".EDITION")]
    [InlineData("SELECT SYS_CONTEXT('USERENV', 'CON_NAME') FROM dual", "CON_NAME")]
    [InlineData("SELECT s.con_id FROM v$session s", "CON_ID")]
    [InlineData("SELECT name FROM v$pdbs", "V$PDBS")]
    [InlineData("SELECT * FROM cdb_objects", "CDB_OBJECTS")]
    public void Eleven_variant_using_12c_feature_is_rejected(string body, string feature)
    {
        var (query, problems) = CatalogParser.Parse(new CatalogSource("session/x.11.sql",
            $"-- id: session.x\n-- min-version: 11\n-- license: none\n{body}"));

        Assert.Null(query);
        var problem = Assert.Single(problems);
        Assert.Equal(CatalogRules.Pre12cFeature, problem.Rule);
        Assert.Contains(feature, problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT * FROM (SELECT x FROM t ORDER BY x) WHERE ROWNUM <= :max_rows")]
    [InlineData("SELECT c.search_condition, c.hidden_column, i.version FROM all_constraints c, all_tab_cols t, v$instance i")]
    [InlineData("SELECT 1 FROM dual -- FETCH FIRST 1 ROWS ONLY 는 주석이라 괜찮다\n/* identity_column */")]
    [InlineData("SELECT 'x' AS edition, o.edition_name FROM all_objects o")]
    public void Eleven_variant_without_12c_feature_is_accepted(string body)
    {
        var (query, problems) = CatalogParser.Parse(new CatalogSource("session/x.11.sql",
            $"-- id: session.x\n-- min-version: 11\n-- license: none\n{body}"));

        Assert.Empty(problems);
        Assert.NotNull(query);
    }

    [Theory]
    [InlineData(19)]
    [InlineData(23)]
    public void Files_for_19_and_later_may_use_12c_features(int version)
    {
        var (_, problems) = CatalogParser.Parse(new CatalogSource("session/x.sql",
            $"-- id: session.x\n-- min-version: {version}\n-- license: none\nSELECT c.identity_column FROM t c FETCH FIRST 1 ROWS ONLY"));

        Assert.Empty(problems);
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
