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
    [InlineData("meta.table_props")]
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
            Assert.StartsWith("SELECT", body.TrimStart(), StringComparison.Ordinal);
            Assert.DoesNotMatch(Writes(), body);
        }
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
