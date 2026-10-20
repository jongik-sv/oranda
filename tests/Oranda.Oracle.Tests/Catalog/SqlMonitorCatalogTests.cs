using Oranda.Core.Catalog;
using Oranda.Oracle.Catalog;

namespace Oranda.Oracle.Tests.Catalog;

// 소유: TSK-09-02. sql/ 폴더 카탈로그: id 존재, 팩 불필요(license none), ASH·AWR 미사용.
[Trait("Category", "Catalog")]
public class SqlMonitorCatalogTests
{
    private static readonly SqlCatalog Catalog = EmbeddedCatalog.Load();

    [Theory]
    [InlineData("sql.top")]
    [InlineData("sql.full_text")]
    [InlineData("sql.binds")]
    public void Query_exists_for_19_and_26(string id)
    {
        Assert.InRange(Catalog.Get(id, 19).Header.MinVersion, 11, 19);
        Assert.Equal(id, Catalog.Get(id, 11).Header.Id); // 11.2 (TSK-03-07)
        Assert.Equal(id, Catalog.Get(id, 26).Header.Id);
    }

    [Fact]
    public void Sql_monitor_queries_need_no_pack_and_avoid_pack_views()
    {
        var headers = Catalog.Entries.Where(h => h.Id.StartsWith("sql.", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, headers.Select(h => h.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var header in headers)
        {
            Assert.Equal(CatalogLicense.None, header.License);
            foreach (var version in new[] { 11, 26 })
            {
                var sql = Catalog.Get(header.Id, version).Sql;
                foreach (var view in new[] { "active_session_history", "dba_hist_", "sql_monitor", "sqltune", "advisor" })
                {
                    Assert.DoesNotContain(view, sql, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    [Fact]
    public void Top_sql_reads_gv_sql_with_all_condition_binds()
    {
        var sql = Catalog.Get("sql.top", 26).Sql;
        Assert.Contains("gv$sql", sql, StringComparison.OrdinalIgnoreCase);
        foreach (var bind in new[] { ":order_by", ":top", ":min_elapsed_us", ":min_buffer_gets", ":min_executions", ":text", ":schema", ":active_secs" })
        {
            Assert.Contains(bind, sql, StringComparison.Ordinal);
        }

        Assert.Contains("gv$sql_bind_capture", Catalog.Get("sql.binds", 26).Sql, StringComparison.OrdinalIgnoreCase);
    }
}
