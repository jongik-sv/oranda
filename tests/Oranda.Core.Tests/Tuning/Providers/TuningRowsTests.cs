using Oranda.Core.Tuning;
using Oranda.Core.Tuning.Providers;

namespace Oranda.Core.Tests.Tuning.Providers;

// 튜닝 질의 행 조립(DB 없음). Oracle.IntegrationTests 의 TuningLogicTests(TSK-06-02)·TuningRecommendLogicTests(TSK-14-17),
// Oracle.Tests 의 AwrPlanStepsTests(TSK-06-06)·TuningReportCatalogTests(TSK-06-07) 에서 옮겼다(P0a).
public class TuningRowsTests
{
    [Fact]
    public void Shared_cursor_rows_unfold_to_one_reason_per_y_column()
    {
        string[] names = ["SQL_ID", "ADDRESS", "CHILD_ADDRESS", "CHILD_NUMBER", "UNBOUND_CURSOR", "BIND_MISMATCH", "OPTIMIZER_MISMATCH", "REASON", "CON_ID"];
        var rows = new List<object?[]>
        {
            new object?[] { "abc", new byte[] { 1 }, new byte[] { 2 }, 1m, "N", "Y", "Y", "<ChildNode/>", 3m },
            new object?[] { "abc", new byte[] { 1 }, new byte[] { 3 }, 0m, "N", "N", "N", null, 3m },
            new object?[] { "Y", new byte[] { 1 }, new byte[] { 4 }, 2m, "Y", "N", "N", "  ", 3m },
        };

        var reasons = TuningRows.ToReasons(names, rows);

        Assert.Equal([(1, "BIND_MISMATCH"), (1, "OPTIMIZER_MISMATCH"), (2, "UNBOUND_CURSOR")], reasons.Select(r => (r.ChildNumber, r.Reason)));
        Assert.Equal("<ChildNode/>", reasons[0].Detail);
        Assert.Null(reasons[2].Detail); // 빈 reason 은 상세 없음. SQL_ID 값 "Y" 는 사유가 아니다.
    }

    [Fact]
    public void Shared_cursor_without_reason_column_on_11g_still_unfolds()
    {
        string[] names = ["INST_ID", "SQL_ID", "CHILD_NUMBER", "ROLL_INVALID_MISMATCH"];

        var reasons = TuningRows.ToReasons(names, [new object?[] { 1m, "a", 4m, "Y" }]);

        var r = Assert.Single(reasons);
        Assert.Equal(4, r.ChildNumber);
        Assert.Equal("ROLL_INVALID_MISMATCH", r.Reason);
        Assert.Null(r.Detail);
        Assert.Empty(TuningRows.ToReasons(names, []));
    }

    [Fact]
    public void Histogram_frequency_is_difference_from_previous_endpoint()
    {
        var buckets = TuningRows.ToBuckets([(9000, "1"), (9100, "2"), (9100, "3"), (10000, null)]);

        Assert.Equal([9000L, 100, 0, 900], buckets.Select(b => b.Frequency));
        Assert.Null(buckets[3].EndpointValue);
        Assert.Empty(TuningRows.ToBuckets([]));
        Assert.Equal(0, TuningRows.ToBuckets([(5, "a"), (3, "b")])[1].Frequency); // 뒤집힌 순서는 0
    }

    [Fact]
    public void Ash_share_is_ratio_of_samples()
    {
        var rows = TuningRows.ToAshRows([("ON CPU", null, 3), ("db file sequential read", "User I/O", 1)]);

        Assert.Equal(0.75, rows[0].Share, 3);
        Assert.Equal(0.25, rows[1].Share, 3);
        Assert.Equal(0, TuningRows.ToAshRows([("x", null, 0)])[0].Share);
    }

    [Theory]
    [InlineData("/opt/oracle/diag/rdbms/free/FREE/trace/FREE_ora_12.trc", "FREE_ora_12.trc")]
    [InlineData(@"C:\app\diag\rdbms\orcl\orcl\trace\orcl_ora_7.trc", "orcl_ora_7.trc")]
    [InlineData("plain.trc", "plain.trc")]
    public void Trace_file_name_drops_the_directory(string path, string name)
    {
        Assert.Equal(name, TuningRows.TraceFileName(path));
    }

    [Fact]
    public void Sql_id_list_drops_blanks_duplicates_and_non_identifiers()
    {
        Assert.Null(TuningRows.SqlIdList(null));
        Assert.Null(TuningRows.SqlIdList([" ", "a'b"]));
        Assert.Equal("abc,def", TuningRows.SqlIdList(["abc", " abc ", "def", "x,y"]));
        Assert.Equal(TuningRows.MaxSqlIds, TuningRows.SqlIdList([.. Enumerable.Range(0, 300).Select(i => "id" + i)])!.Split(',').Length);
    }

    [Fact]
    public void Rows_map_by_catalog_column_order()
    {
        var c = TuningRows.ToCandidate([1m, "abc", 42m, "SELECT 1", 3m, 4m, 5m, 6m, 7m, 8m, 9m, 10m, "17415914164128958688", 11m, 12m, 13m, 14m, null, 99m, "APP_OWNER"]);
        Assert.Equal(("abc", 1, 42L, 3L, 4L, 10L, "17415914164128958688", "APP_OWNER"), (c.SqlId, c.InstanceId, c.PlanHashValue, c.Executions, c.ElapsedMicros, c.VersionCount, c.ForceMatchingSignature, c.ParsingSchema));
        Assert.Equal(14, c.ClusterWaitMicros);

        var g = TuningRows.ToLiteralGroup(["123", 12m, 100m, 200m, 150m, 300m, 0m, 50m, 100m, 1m, "s1", "SELECT 2", null]);
        Assert.Equal(("123", 12, "s1", 100L, true), (g.ForceMatchingSignature, g.SqlCount, g.Sample.SqlId, g.Sample.Executions, g.Sample.IsLiteralGroup));

        var p = TuningRows.ToPlanFinding([3m, "TABLE ACCESS", "FULL", "APP_OWNER", "T", 1000m, null, null]);
        Assert.Equal((3, "TABLE ACCESS", "FULL", 1000L, (long?)null), (p.Id, p.Operation, p.Options, p.Cardinality, p.TableBlocks));
    }

    [Fact]
    public void Awr_rows_become_a_plan_in_id_order_with_hash_and_indented_text()
    {
        object?[][] rows =
        [
            [1m, 0m, 1m, "TABLE ACCESS", "FULL", "MES", "LOT_HIST", 9000m, 120000m, null, "\"EQP_ID\"=:1"],
            [0m, null, 0m, "SELECT STATEMENT", null, null, null, 9000m, null, null, null],
        ];

        var plan = TuningRows.ToAwrPlan(rows, 1838229974);

        Assert.Equal(1838229974, plan.PlanHashValue);
        Assert.Equal([0, 1], plan.Steps.Select(s => s.Id));
        var full = plan.Steps[1];
        Assert.Equal(0, full.ParentId);
        Assert.Equal(1, full.Depth);
        Assert.Equal("FULL", full.Options);
        Assert.Equal("LOT_HIST", full.ObjectName);
        Assert.Equal(9000, full.Cost);
        Assert.Equal(120000, full.EstimatedRows);
        Assert.Equal("\"EQP_ID\"=:1", full.FilterPredicates);
        Assert.Contains("Plan hash value: 1838229974", plan.Text, StringComparison.Ordinal);
        Assert.Contains("   1 |  TABLE ACCESS FULL | MES.LOT_HIST | cost 9000", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_awr_rows_give_an_empty_plan()
    {
        var plan = TuningRows.ToAwrPlan([], 1);
        Assert.Empty(plan.Steps);
        Assert.Equal(1, plan.PlanHashValue);
    }

    [Fact]
    public void Sql_stats_row_maps_sums_and_an_absent_cursor_is_null()
    {
        var last = new DateTime(2026, 10, 24, 14, 0, 0);
        var row = TuningRows.ToSqlStats([4m, 2m, 18220m, 33561000000m, 9402000000m, 768519600m, 565220840m, 22009760m, 6040000000m, 1m, 2m, 3m, 120m, 36000m, "MESADM", "MES-WEB", last, "2026-10-20/08:02:11"]);

        Assert.NotNull(row);
        Assert.Equal(4, row.Children);
        Assert.Equal(2, row.Instances);
        Assert.Equal(18220, row.Executions);
        Assert.Equal(768519600, row.BufferGets);
        Assert.Equal(6040000000, row.ApplicationWaitMicros);
        Assert.Equal(3, row.UserIoWaitMicros);
        Assert.Equal(36000, row.Fetches);
        Assert.Equal("MESADM", row.ParsingSchema);
        Assert.Equal(last, row.LastActiveTime);
        Assert.Equal("2026-10-20/08:02:11", row.FirstLoadTime);
        Assert.Null(TuningRows.ToSqlStats([0m, 0m, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null]));
    }

    [Fact]
    public void Environment_rows_keep_known_kinds_only()
    {
        var rows = TuningRows.ToEnvRows([("VERSION", "banner", "Oracle Database 11g"), ("PARAMETER", "optimizer_mode", "ALL_ROWS"), ("NLS", "NLS_SORT", null), ("OTHER", "x", "y"), ("NLS", null, "z")]);

        Assert.Equal([TuningEnvKind.Version, TuningEnvKind.Parameter, TuningEnvKind.Nls], rows.Select(r => r.Kind));
        Assert.Null(rows[2].Value);
    }
}
