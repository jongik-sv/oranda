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
}
