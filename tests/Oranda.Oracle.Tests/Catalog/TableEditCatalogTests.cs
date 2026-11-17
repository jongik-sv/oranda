using System.Text.RegularExpressions;
using Oranda.Core.Catalog;
using Oranda.Oracle.Catalog;

namespace Oranda.Oracle.Tests.Catalog;

// 계획 Task 3: tableedit/ 폴더(실행 전 검사 읽기)와 meta.* 에 붙인 테이블 편집기 열(TE-29).
[Trait("Category", "Catalog")]
public partial class TableEditCatalogTests
{
    private static readonly SqlCatalog Catalog = EmbeddedCatalog.Load();

    [Theory]
    [InlineData("tableedit.count_rows")]
    [InlineData("tableedit.num_rows")]
    [InlineData("tableedit.column_def")]
    [InlineData("tableedit.length_overflow")]
    [InlineData("tableedit.number_overflow")]
    [InlineData("tableedit.null_count")]
    [InlineData("tableedit.nonnull_count")]
    [InlineData("tableedit.duplicate_keys")]
    [InlineData("tableedit.orphans")]
    [InlineData("tableedit.check_violations")]
    [InlineData("tableedit.preview_expression")]
    [InlineData("tableedit.validate_expression")]
    [InlineData("tableedit.table_name_exists")]
    [InlineData("tableedit.column_name_exists")]
    [InlineData("tableedit.constraint_name_exists")]
    [InlineData("tableedit.index_name_exists")]
    [InlineData("tableedit.dependents")]
    [InlineData("tableedit.conversion_scan")]
    [InlineData("tableedit.conversion_rows")]
    [InlineData("tableedit.referencing_fks")]
    [InlineData("tableedit.conversion_check_violations")]
    [InlineData("tableedit.conversion_duplicate_keys")]
    [InlineData("meta.table_props")]
    [InlineData("meta.table_lobs")]
    public void Check_queries_run_from_11g_and_need_no_license_pack(string id)
    {
        var query = Catalog.Get(id, 11);

        Assert.Equal(11, query.Header.MinVersion);
        Assert.Equal(CatalogLicense.None, query.Header.License);
        Assert.Equal(query.Sql, Catalog.Get(id, 26).Sql); // 판마다 갈리지 않는다
    }

    [Fact]
    public void Check_queries_only_read()
    {
        foreach (var id in Catalog.Entries.Select(e => e.Id).Where(i => i.StartsWith("tableedit.", StringComparison.Ordinal)).Distinct())
        {
            var body = Body(Catalog.Get(id, 26).Sql);
            // 형식 변환 행 시험(설계 18.9)만 PL/SQL 블록이다(변수에 담아 보기만 함).
            Assert.StartsWith(id is "tableedit.conversion_scan" or "tableedit.conversion_rows" ? "DECLARE" : "SELECT", body.TrimStart(), StringComparison.Ordinal);
            Assert.DoesNotMatch(Writes(), body);
        }
    }

    [Fact]
    public void Conversion_validate_needs_12c_and_leaves_user_text_out()
    {
        var query = Catalog.Get("tableedit.conversion_validate", 19);

        Assert.Equal(12, query.Header.MinVersion);
        Assert.Equal(CatalogLicense.None, query.Header.License);
        Assert.Contains("@WHERE@", query.Sql, StringComparison.Ordinal);
        Assert.Throws<CatalogQueryNotFoundException>(() => Catalog.Get("tableedit.conversion_validate", 11));
    }

    [Theory]
    [InlineData("tableedit.conversion_scan")]
    [InlineData("tableedit.conversion_rows")]
    public void Conversion_blocks_take_the_user_expression_only_as_a_bound_query_and_rethrow_cancel(string id)
    {
        var sql = Catalog.Get(id, 11).Sql;

        Assert.DoesNotContain("@EXPRESSION@", sql, StringComparison.Ordinal); // 사용자 식은 바인드 :query(SELECT 한 문장)로만
        Assert.Contains(":query", sql, StringComparison.Ordinal);
        Assert.Contains("@TYPE@", sql, StringComparison.Ordinal);
        Assert.Matches(@"IF SQLCODE = -1013 THEN\s+RAISE;", sql);
    }

    [Theory]
    [InlineData("tableedit.conversion_check_violations")]
    [InlineData("tableedit.conversion_duplicate_keys")]
    public void Converted_constraint_checks_put_the_user_text_on_its_own_lines(string id)
    {
        var sql = Catalog.Get(id, 11).Sql.ReplaceLineEndings("\n");

        Assert.Contains("(\n@EXPRESSION@\n) AS @COL@", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(19)]
    [InlineData(23)]
    public void Columns_add_the_editor_fields_after_byte_length(int version)
    {
        var sql = Catalog.Get("meta.columns", version).Sql;
        var byteLength = sql.IndexOf("AS byte_length", StringComparison.Ordinal);

        foreach (var name in new[] { "c.char_used", "identity_generation", "identity_options", "on_null_flag" })
        {
            Assert.True(sql.IndexOf(name, StringComparison.Ordinal) > byteLength, name + " is not after byte_length");
        }
    }

    [Fact]
    public void Identity_options_come_from_the_identity_view_from_19_only()
    {
        Assert.Contains("_tab_identity_cols", Catalog.Get("meta.columns", 19).Sql, StringComparison.Ordinal);
        Assert.Contains("_tab_identity_cols", Catalog.Get("meta.columns", 23).Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("_tab_identity_cols", Catalog.Get("meta.columns", 11).Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(19)]
    public void Constraints_add_delete_rule_referenced_column_and_index_name(int version)
    {
        var sql = Catalog.Get("meta.constraints", version).Sql;

        Assert.Contains("c.r_constraint_name,\n       c.delete_rule, rcc.column_name AS r_column_name, c.index_name", sql, StringComparison.Ordinal);
        Assert.Contains("rcc.position = cc.position", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Indexes_add_descend_expression_and_tablespace()
    {
        var sql = Catalog.Get("meta.indexes", 11).Sql;

        Assert.Contains("ic.column_position,\n       ic.descend, ie.column_expression, i.tablespace_name", sql, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN @PFX@_ind_expressions ie", sql, StringComparison.Ordinal);
    }

    private static string Body(string sql) =>
        string.Join('\n', sql.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));

    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|MERGE|ALTER|DROP|CREATE|TRUNCATE|GRANT|LOCK)\b")]
    private static partial Regex Writes();
}
