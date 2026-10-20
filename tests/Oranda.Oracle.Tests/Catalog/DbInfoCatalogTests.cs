using System.Text.RegularExpressions;
using Oranda.Core.Catalog;
using Oranda.Oracle.Catalog;

namespace Oranda.Oracle.Tests.Catalog;

// 소유: TSK-07-04. dbinfo/ 폴더 카탈로그 중 소스 본문 검색·정규식 검사·열 이름 검색:
// id 존재, 라이선스 없음, dba_/all_ 자동 선택, 바인드 이름이 제공자가 묶는 것과 정확히 같다(사용자 글자는 바인드로만).
[Trait("Category", "Catalog")]
public partial class DbInfoCatalogTests
{
    private static readonly SqlCatalog Catalog = EmbeddedCatalog.Load();

    [Theory]
    [InlineData("dbinfo.source_search")]
    [InlineData("dbinfo.regex_check")]
    [InlineData("dbinfo.column_search")]
    public void Query_exists_for_19_and_26_and_needs_no_license_pack(string id)
    {
        Assert.InRange(Catalog.Get(id, 19).Header.MinVersion, 11, 19);
        Assert.Equal(id, Catalog.Get(id, 11).Header.Id); // 11.2 (TSK-03-07)
        Assert.Equal(id, Catalog.Get(id, 26).Header.Id);
        Assert.Equal(CatalogLicense.None, Catalog.Get(id, 26).Header.License);
    }

    /// <summary>BindByName 에서 남는·빠진 바인드는 실행 때에야 ORA-01008/01036 이 된다. 제공자가 묶는 이름과 정확히 맞춘다.</summary>
    [Theory]
    [InlineData("dbinfo.source_search", "flags,max_rows,needle,owner,pattern,types")]
    [InlineData("dbinfo.regex_check", "flags,pattern")]
    [InlineData("dbinfo.column_search", "max_rows,name_pattern,owner")]
    public void Binds_are_exactly_what_the_provider_binds(string id, string expected)
    {
        var binds = BindName().Matches(Catalog.Get(id, 26).Sql).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected.Split(','), binds.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("dbinfo.source_search")]
    [InlineData("dbinfo.regex_check")]
    [InlineData("dbinfo.column_search")]
    public void User_text_goes_only_through_binds_not_through_text_placeholders(string id)
    {
        var sql = Catalog.Get(id, 26).Sql;

        // @PFX@(dba/all) 말고는 글자를 바꿔 끼우는 자리가 없다.
        Assert.DoesNotContain("@", sql.Replace("@PFX@", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Source_search_reads_source_view_of_the_session_prefix_and_caps_rows()
    {
        var sql = Catalog.Get("dbinfo.source_search", 26).Sql;

        Assert.Contains("FROM @PFX@_source s", sql, StringComparison.Ordinal);
        Assert.Contains("s.type IN ('PACKAGE', 'PACKAGE BODY', 'PROCEDURE', 'FUNCTION', 'TRIGGER', 'TYPE', 'TYPE BODY')", sql, StringComparison.Ordinal);
        Assert.Contains("(:needle IS NULL OR INSTR(UPPER(s.text), UPPER(:needle)) > 0)", sql, StringComparison.Ordinal);
        Assert.Contains("REGEXP_LIKE(RTRIM(s.text, CHR(13) || CHR(10)), :pattern, :flags)", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY s.owner, s.name, s.type, s.line", sql, StringComparison.Ordinal);
        Assert.EndsWith("FETCH FIRST :max_rows ROWS ONLY", sql.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void Regex_check_evaluates_the_pattern_once_on_dual()
    {
        var sql = Catalog.Get("dbinfo.regex_check", 26).Sql;

        Assert.Contains("FROM dual", sql, StringComparison.Ordinal);
        Assert.Contains("REGEXP_LIKE('x', :pattern, :flags)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Column_search_matches_names_case_insensitively_skips_recycle_bin_and_caps_before_reading_types()
    {
        var sql = Catalog.Get("dbinfo.column_search", 26).Sql;

        Assert.Contains("FROM @PFX@_tab_columns c", sql, StringComparison.Ordinal);
        Assert.Contains("UPPER(c.column_name) LIKE UPPER(:name_pattern) ESCAPE '\\'", sql, StringComparison.Ordinal);
        Assert.Contains("c.table_name NOT LIKE 'BIN$%'", sql, StringComparison.Ordinal);
        Assert.Contains("FROM @PFX@_objects o", sql, StringComparison.Ordinal);
        Assert.Contains("o.object_type IN ('TABLE', 'VIEW', 'MATERIALIZED VIEW', 'CLUSTER')", sql, StringComparison.Ordinal);

        // 객체 유형(스칼라 부질의)은 잘라 낸 줄에만 읽는다: FETCH FIRST 가 안쪽 질의에 있다.
        Assert.True(sql.IndexOf("FETCH FIRST :max_rows ROWS ONLY) r", StringComparison.Ordinal) > sql.IndexOf("FROM @PFX@_tab_columns", StringComparison.Ordinal));
    }

    [GeneratedRegex(@":([a-z_]+)")]
    private static partial Regex BindName();
}
