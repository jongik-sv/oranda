using Oranda.Core.Catalog;
using Oranda.Oracle.Catalog;

namespace Oranda.Oracle.Tests.Tuning;

// 소유: TSK-06-06. AWR 계획 단계(두 plan_hash 비교) — 카탈로그 머리(팩·바인드). 줄 → 계획 변환은 Core.Tests TuningRowsTests 로 옮겼다.
public class AwrPlanStepsTests
{
    [Fact]
    public void Awr_plan_steps_query_needs_the_diagnostics_pack_and_binds_sql_id_and_hash()
    {
        var q = EmbeddedCatalog.Load().Get("tuning.awr_plan_steps", 19);
        Assert.Equal(CatalogLicense.Diagnostics, q.Header.License);
        Assert.Contains(":sql_id", q.Sql, StringComparison.Ordinal);
        Assert.Contains(":plan_hash_value", q.Sql, StringComparison.Ordinal);
        Assert.Contains("dba_hist_sql_plan", q.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Awr_plan_steps_filter_by_the_container_dbid_from_12c_and_by_the_dbid_on_11g()
    {
        // PDB 에서는 AWR 줄의 dbid 가 PDB 의 것(con_dbid)이라 v$database.dbid 로 거르면 줄이 안 나온다(26ai FREEPDB1 실측). 11g 는 con_dbid 열이 없다.
        var catalog = EmbeddedCatalog.Load();
        Assert.Contains("d.con_dbid", catalog.Get("tuning.awr_plan_steps", 19).Sql, StringComparison.Ordinal);
        Assert.Contains("d.con_dbid", catalog.Get("tuning.awr_plan_steps", 12).Sql, StringComparison.Ordinal);
        var old = catalog.Get("tuning.awr_plan_steps", 11).Sql;
        Assert.DoesNotContain("con_dbid", old, StringComparison.Ordinal);
        Assert.Contains("SELECT d.dbid FROM v$database d", old, StringComparison.Ordinal);
        Assert.Equal(CatalogLicense.Diagnostics, catalog.Get("tuning.awr_plan_steps", 11).Header.License);
    }
}
