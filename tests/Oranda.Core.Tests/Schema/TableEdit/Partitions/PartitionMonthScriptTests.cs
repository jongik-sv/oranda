using Oranda.Core.Connection;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Partitions;

namespace Oranda.Core.Tests.Schema.TableEdit.Partitions;

/// <summary>월 추가 스크립트(PA-5): INTERVAL 없는 범위 + 마지막 MAXVALUE → 다음 달 SPLIT. 예시 테이블 = SAMPLE.ORDERS.</summary>
public class PartitionMonthScriptTests
{
    private static readonly Dialect Oracle = new(keepsGlobal: true);

    private static PartitionTableDraft Table(string key, params (string Name, string Bound)[] rows) =>
        new(new PartitionScheme(PartitionMethod.Range, [key]), [.. rows.Select((r, i) => new PartitionDraft("p" + i, r.Name) { Bound = r.Bound })]);

    [Fact]
    public void Text_yyyymmdd_key_splits_maxvalue_month_by_month_in_the_same_format()
    {
        var plan = PartitionMonthScript.Plan(Table("YMD", ("P_202601", "'20260201'"), ("P_202602", "'20260301'"), ("P_MAX", "MAXVALUE")), 3)!;

        Assert.Equal(("P_MAX", MonthBoundFormat.TextYyyymmdd), (plan.MaxPartition, plan.Format));
        Assert.Equal(
        [
            "ALTER TABLE SAMPLE.ORDERS SPLIT PARTITION P_MAX AT ('20260401') INTO (PARTITION P_202603, PARTITION P_MAX) UPDATE INDEXES",
            "ALTER TABLE SAMPLE.ORDERS SPLIT PARTITION P_MAX AT ('20260501') INTO (PARTITION P_202604, PARTITION P_MAX) UPDATE INDEXES",
            "ALTER TABLE SAMPLE.ORDERS SPLIT PARTITION P_MAX AT ('20260601') INTO (PARTITION P_202605, PARTITION P_MAX) UPDATE INDEXES",
        ], PartitionMonthScript.Statements("SAMPLE", "ORDERS", plan, Oracle));
    }

    [Theory]
    [InlineData("'202612'", "'202701'", "P_2026_12")]
    [InlineData("'2026-12-01'", "'2027-01-01'", "P_2026_12")]
    [InlineData("20261201", "20270101", "P_2026_12")]
    [InlineData("202612", "202701", "P_2026_12")]
    [InlineData("DATE '2026-12-01'", "DATE '2027-01-01'", "P_2026_12")]
    [InlineData("TO_DATE(' 2026-12-01 00:00:00', 'SYYYY-MM-DD HH24:MI:SS', 'NLS_CALENDAR=GREGORIAN')", "DATE '2027-01-01'", "P_2026_12")]
    public void Every_month_format_keeps_its_own_literal_and_follows_the_previous_name(string last, string next, string name)
    {
        // 앞 이름이 P_2026_11(= 마지막 경계 앞 달)이면 같은 꼴 P_YYYY_MM 을 잇는다.
        var plan = PartitionMonthScript.Plan(Table("K", ("P_2026_11", last), ("P_MAX", "MAXVALUE")), 1)!;
        Assert.Equal(new MonthSplit(name, next), Assert.Single(plan.Months));
    }

    [Fact]
    public void Not_eligible_without_maxvalue_with_interval_or_with_a_non_month_bound()
    {
        Assert.Null(PartitionMonthScript.Plan(Table("K", ("P1", "'20260201'"), ("P2", "'20260301'"))));
        Assert.Null(PartitionMonthScript.Plan(Table("K", ("P1", "'20260215'"), ("P_MAX", "MAXVALUE"))));
        Assert.Null(PartitionMonthScript.Plan(Table("K", ("P1", "1000"), ("P_MAX", "MAXVALUE"))));
        Assert.Null(PartitionMonthScript.Plan(Table("K", ("P_MAX", "MAXVALUE"))));
        var withInterval = Table("K", ("P1", "DATE '2026-02-01'"), ("P_MAX", "MAXVALUE"));
        Assert.Null(PartitionMonthScript.Plan(withInterval with { Scheme = withInterval.Scheme! with { Interval = "NUMTOYMINTERVAL(1, 'MONTH')" } }));
        Assert.Null(PartitionMonthScript.Plan(PartitionTableDraft.None));
        Assert.Null(PartitionMonthScript.Plan(new PartitionTableDraft(new PartitionScheme(PartitionMethod.List, ["K"]), [])));
    }

    /// <summary>이미 있는 이름(사람이 앞서 만든 달)은 건너뛰고, 나누기 뒤 전역 인덱스를 못 남기는 방언은 끝에 REBUILD.</summary>
    [Fact]
    public void Existing_names_are_skipped_and_tibero_like_dialects_rebuild_global_indexes()
    {
        var plan = PartitionMonthScript.Plan(Table("YM", ("P_202605", "'202601'"), ("P_202601", "'202602'"), ("P_MAX", "MAXVALUE")), 5)!;
        Assert.Equal(["P_202602", "P_202603", "P_202604", "P_202606"], plan.Months.Select(m => m.Name));

        var statements = PartitionMonthScript.Statements("SAMPLE", "ORDERS", plan, new Dialect(keepsGlobal: false), ["ORDERS_PK"]);
        Assert.Equal("ALTER INDEX SAMPLE.ORDERS_PK REBUILD", statements[^1]);
        Assert.DoesNotContain(PartitionMonthScript.Statements("SAMPLE", "ORDERS", plan, Oracle, ["ORDERS_PK"]), s => s.Contains("REBUILD", StringComparison.Ordinal));

        var script = PartitionMonthScript.Script("SAMPLE", "ORDERS", plan, Oracle);
        Assert.StartsWith("-- 월 추가: SAMPLE.ORDERS  (P_202602 ~ P_202606, 4개월)", script, StringComparison.Ordinal);
        Assert.Contains("UPDATE INDEXES;", script, StringComparison.Ordinal);
    }

    private sealed class Dialect(bool keepsGlobal) : ITableEditDialect
    {
        public Dbms Dbms => Dbms.Oracle;

        public IReadOnlyList<string> TypeNames => ["NUMBER"];

        public bool SupportsVirtualColumnModify => true;

        public bool PurgesOnDrop => false;

        public bool KeepsGlobalIndexesOnSplitMerge => keepsGlobal;

        public int MaxIdentifierBytes(ServerInfo? server) => 128;

        public string? UnsupportedReasonKey(TableEditFeature feature, ServerInfo? server) => null;
    }
}
