using System.Text.RegularExpressions;
using Oranda.Core.Catalog;
using Oranda.Oracle.Catalog;

namespace Oranda.Oracle.Tests.Catalog;

// 소유: TSK-09-01. session/ 폴더 카탈로그: id 존재, 팩 불필요(license 태그 쿼리 금지), gv$ 사용, ASH·AWR 미사용.
[Trait("Category", "Catalog")]
public partial class SessionCatalogTests
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
    [InlineData("session.io")]
    [InlineData("session.wait_history")]
    [InlineData("session.longops")]
    [InlineData("session.open_cursors")]
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
        foreach (var id in new[] { "session.list", "session.detail", "session.blocking_tree", "session.transaction", "session.locks", "session.stats", "session.trace_file",
            "session.io", "session.wait_history", "session.longops", "session.open_cursors" })
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

    /// <summary>
    /// 바인드 이름이 상세 코드가 묶는 것과 정확히 같아야 한다(BindByName 에서 남는·빠진 바인드는 실행 때에야 ORA-01008/01036).
    /// SERIAL# 열이 있는 뷰만 serial 을 묶는다.
    /// </summary>
    [Theory]
    [InlineData("session.io", "inst_id,sid")]
    [InlineData("session.wait_history", "inst_id,sid")]
    [InlineData("session.open_cursors", "inst_id,sid")]
    [InlineData("session.locks", "inst_id,sid")]
    [InlineData("session.stats", "inst_id,sid")]
    [InlineData("session.longops", "inst_id,serial,sid")]
    [InlineData("session.transaction", "inst_id,serial,sid")]
    public void Detail_section_queries_bind_the_names_the_service_adds(string id, string expected)
    {
        var binds = BindName().Matches(Catalog.Get(id, 26).Sql).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected, string.Join(',', binds.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void Long_ops_show_only_unfinished_work_newest_first()
    {
        var sql = Catalog.Get("session.longops", 26).Sql;

        Assert.Contains("l.totalwork > 0 AND l.sofar < l.totalwork", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY l.start_time DESC", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_cursors_are_capped()
    {
        var sql = Catalog.Get("session.open_cursors", 26).Sql;

        Assert.Contains("ORDER BY", sql, StringComparison.Ordinal);
        Assert.EndsWith("FETCH FIRST 200 ROWS ONLY", sql, StringComparison.Ordinal);
    }

    [GeneratedRegex(@":([a-z_]+)")]
    private static partial Regex BindName();
}
