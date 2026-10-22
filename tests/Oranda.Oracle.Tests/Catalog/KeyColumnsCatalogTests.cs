using Oranda.Core.Catalog;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Metadata;

namespace Oranda.Oracle.Tests.Catalog;

// 소유: TSK-13-34. 객체 상세가 인덱스·제약 줄을 고를 때 읽는 meta.index_columns · meta.constraint_columns 와 줄 해석.
[Trait("Category", "Catalog")]
public class KeyColumnsCatalogTests
{
    private static readonly SqlCatalog Catalog = EmbeddedCatalog.Load();

    [Theory]
    [InlineData("meta.index_columns", ":index_name", "@PFX@_ind_expressions")]
    [InlineData("meta.constraint_columns", ":constraint_name", "r_constraint_name")]
    public void Queries_run_on_11g_and_later_without_a_license_and_bind_the_table_and_key(string id, string keyBind, string mustUse)
    {
        foreach (var version in new[] { 11, 19, 23 })
        {
            var q = Catalog.Get(id, version);
            Assert.Equal(11, q.Header.MinVersion);
            Assert.Equal(CatalogLicense.None, q.Header.License);
            Assert.Contains(":owner", q.Sql, StringComparison.Ordinal);
            Assert.Contains(":name", q.Sql, StringComparison.Ordinal);
            Assert.Contains(keyBind, q.Sql, StringComparison.Ordinal);
            Assert.Contains(mustUse, q.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain("FETCH FIRST", q.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("search_condition_vc", q.Sql, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Throws<CatalogQueryNotFoundException>(() => Catalog.Get(id, 10)); // 제공자가 잡아 빈 목록으로 돌려준다
    }

    [Fact]
    public void Index_row_plain_column_keeps_its_name()
    {
        var c = OracleTableKeyDetailProvider.ToIndexColumn([1m, "EMPNO", "ASC", null], 0);
        Assert.Equal(new Oranda.Core.Metadata.IndexColumnDetail(1, "EMPNO", null, false), c);
    }

    [Fact]
    public void Index_row_descending_column_uses_the_unquoted_name_from_the_expression()
    {
        var c = OracleTableKeyDetailProvider.ToIndexColumn([2m, "SYS_NC00009$", "DESC", "\"ENAME\""], 0);
        Assert.Equal(new Oranda.Core.Metadata.IndexColumnDetail(2, "ENAME", null, true), c);

        // 한글·공백 열 이름도 따옴표만 벗긴다.
        var k = OracleTableKeyDetailProvider.ToIndexColumn([1m, "SYS_NC00011$", "DESC", "\"이 름\""], 0);
        Assert.Equal("이 름", k.Column);
        Assert.Null(k.Expression);
    }

    [Fact]
    public void Index_row_function_based_column_keeps_the_expression()
    {
        var c = OracleTableKeyDetailProvider.ToIndexColumn([1m, "SYS_NC00010$", "ASC", "UPPER(\"ENAME\")"], 0);
        Assert.Equal("UPPER(\"ENAME\")", c.Expression);
        Assert.False(c.Descending);

        var d = OracleTableKeyDetailProvider.ToIndexColumn([null, "SYS_NC00012$", "DESC", "  \"A\"||\"B\" "], 4);
        Assert.Equal(5, d.Position); // 위치가 없으면 줄 순서
        Assert.Equal("\"A\"||\"B\"", d.Expression);
        Assert.True(d.Descending);
    }
}
