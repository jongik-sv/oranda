using Oranda.Core.Catalog;
using Oranda.Oracle.Catalog;

namespace Oranda.Oracle.Tests.Catalog;

// 소유: TSK-09-01. session/ 폴더 카탈로그: id 존재, 팩 불필요(license 태그 쿼리 금지), gv$ 사용, ASH·AWR 미사용.
[Trait("Category", "Catalog")]
public class SessionCatalogTests
{
    private static readonly SqlCatalog Catalog = EmbeddedCatalog.Load();

    [Theory]
    [InlineData("session.list")]
    [InlineData("session.detail")]
    [InlineData("session.blocking_tree")]
    [InlineData("session.sql_text")]
    [InlineData("session.transaction")]
    [InlineData("session.locks")]
    [InlineData("session.stats")]
    [InlineData("session.plan")]
    [InlineData("session.trace_file")]
    public void Query_exists_for_19_and_26(string id)
    {
        Assert.Equal(19, Catalog.Get(id, 19).Header.MinVersion);
        Assert.Equal(id, Catalog.Get(id, 26).Header.Id);
    }

    [Fact]
    public void Session_queries_need_no_license_pack_and_avoid_pack_views()
    {
        var headers = Catalog.Entries.Where(h => h.Id.StartsWith("session.", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(headers);
        foreach (var header in headers)
        {
            Assert.Equal(CatalogLicense.None, header.License);
            var sql = Catalog.Get(header.Id, 26).Sql;
            foreach (var view in new[] { "active_session_history", "dba_hist_", "sql_monitor", "sqltune", "advisor" })
            {
                Assert.DoesNotContain(view, sql, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Instance_wide_queries_use_gv_views()
    {
        foreach (var id in new[] { "session.list", "session.detail", "session.blocking_tree", "session.transaction", "session.locks", "session.stats", "session.trace_file" })
        {
            var query = Catalog.Get(id, 19);
            Assert.Equal(CatalogRac.Gv, query.Header.Rac);
            Assert.Contains("gv$", query.Sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Blocking_tree_reads_blocking_and_final_blocking_session()
    {
        var sql = Catalog.Get("session.blocking_tree", 19).Sql;

        Assert.Contains("blocking_session", sql, StringComparison.Ordinal);
        Assert.Contains("final_blocking_session", sql, StringComparison.Ordinal);
    }
}
