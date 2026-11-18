using Oranda.Core.Connection;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Partitions;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;
using Oranda.Testing;
using static Oranda.Core.Tests.Schema.TableEdit.Partitions.Advisor.AdvisorSamples;

namespace Oranda.Core.Tests.Schema.TableEdit.Partitions.Advisor;

// 소유: pa-core-l1 PL2. 설계 4.1 code 마다 정상·경계.
public class PartitionAdviceCheckerTests
{
    private static readonly Dialect Oracle = new();

    private static IReadOnlyList<AdviceCheck> Run(PartitionCandidate c, PartitionEvidence? e = null, ITableEditDialect? d = null, ServerInfo? s = null, EvidenceValueVault? vault = null) =>
        PartitionAdviceChecker.Check(c, e ?? Orders(), d ?? Oracle, s ?? Oracle19(), vault);

    private static AdviceCheck? Find(IEnumerable<AdviceCheck> checks, string code) => checks.FirstOrDefault(c => c.Code == code);

    private static AdviceCheck One(IEnumerable<AdviceCheck> checks, string code) => Assert.Single(checks, c => c.Code == code);

    [Fact]
    public void Good_monthly_interval_on_a_big_fresh_table_has_no_warning_or_stop()
    {
        var checks = Run(MonthlyRange("IX_ORDERS_DT"), Orders() with { Keys = [] });

        Assert.All(checks, c => Assert.Equal(AdviceCheckLevel.Ok, c.Level));
        Assert.Equal("IX_ORDERS_DT", One(checks, AdviceCheckCodes.LocalOk).Subject);
        Assert.True(PartitionAdviceChecker.CanFill(checks));
        Assert.Equal("TableEdit.partition.ai.check.local-ok", checks[0].TextKey);
    }

    [Fact]
    public void Unknown_method_layout_interval_or_mismatched_layout_stops()
    {
        Assert.Equal(["weird", "method"], One(Run(MonthlyRange() with { Method = "weird" }), AdviceCheckCodes.MethodUnknown).Args);
        Assert.Equal(["weekly", "layout"], One(Run(MonthlyRange() with { Layout = new PartitionLayout("weekly") }), AdviceCheckCodes.MethodUnknown).Args);
        Assert.Equal(["quarter", "interval"], One(Run(MonthlyRange() with { Interval = "quarter" }), AdviceCheckCodes.MethodUnknown).Args);
        Assert.Equal(["number:abc", "interval"], One(Run(MonthlyRange() with { Interval = "number:abc" }), AdviceCheckCodes.MethodUnknown).Args);
        Assert.Equal(["values", "layout"], One(Run(MonthlyRange() with { Layout = new PartitionLayout(AdviceLayoutKind.Values) }), AdviceCheckCodes.MethodUnknown).Args);
        var sub = MonthlyRange() with { Subpartition = new SubpartitionAdvice("range", ["STATUS"]) };
        Assert.Equal(["range", "subpartition"], One(Run(sub), AdviceCheckCodes.MethodUnknown).Args);

        Assert.Null(Find(Run(MonthlyRange() with { Method = " RANGE ", Interval = " Month " }), AdviceCheckCodes.MethodUnknown)); // 대소문자·공백은 봐줌
    }

    [Fact]
    public void Key_missing_for_absent_virtual_or_empty_keys()
    {
        Assert.Equal("NOPE", One(Run(MonthlyRange() with { KeyColumns = ["NOPE"] }), AdviceCheckCodes.KeyMissing).Subject);
        Assert.Equal("AMOUNT_V", One(Run(NumberRanges("10") with { KeyColumns = ["AMOUNT_V"] }), AdviceCheckCodes.KeyMissing).Subject);
        Assert.Null(One(Run(MonthlyRange() with { KeyColumns = [] }), AdviceCheckCodes.KeyMissing).Subject);
        var sub = MonthlyRange() with { Subpartition = new SubpartitionAdvice(AdviceMethod.Hash, ["GONE"], HashCount: 4) };
        Assert.Equal("GONE", One(Run(sub), AdviceCheckCodes.KeyMissing).Subject);

        Assert.Null(Find(Run(MonthlyRange() with { KeyColumns = ["order_dt"] }), AdviceCheckCodes.KeyMissing)); // 대소문자 무시
    }

    [Fact]
    public void Key_type_stops_lob_keys_and_intervals_on_wrong_types()
    {
        Assert.Equal(["CLOB"], One(Run(Hash(8, "MEMO")), AdviceCheckCodes.KeyType).Args);
        Assert.Equal("CUSTOMER_NAME", One(Run(MonthlyRange() with { KeyColumns = ["CUSTOMER_NAME"] }), AdviceCheckCodes.KeyType).Subject); // 날짜형 아닌 문자열 + 월
        Assert.NotNull(Find(Run(MonthlyRange() with { Interval = "number:1000" }), AdviceCheckCodes.KeyType)); // DATE + 숫자 간격
        Assert.NotNull(Find(Run(MonthlyRange() with { KeyColumns = ["ORDER_DT", "ORDER_ID"] }), AdviceCheckCodes.KeyType)); // INTERVAL 은 열 하나
        var daily = MonthlyRange() with { KeyColumns = ["ORDER_YM"], Interval = AdviceInterval.None, Layout = new PartitionLayout(AdviceLayoutKind.Daily) };
        Assert.Equal(["YYYYMM"], One(Run(daily), AdviceCheckCodes.KeyType).Args); // YYYYMM 으로 날마다는 못 나눔

        Assert.Null(Find(Run(NumberRanges("100") with { Interval = "number:1000" }), AdviceCheckCodes.KeyType)); // NUMBER + 숫자 간격
        Assert.Null(Find(Run(Hash(8)), AdviceCheckCodes.KeyType));
    }

    [Fact]
    public void String_or_number_date_key_with_month_warns_interval_string_but_date_key_does_not()
    {
        var ymd = MonthlyRange() with { KeyColumns = ["ORDER_YMD"] };
        var check = One(Run(ymd), AdviceCheckCodes.IntervalString);

        Assert.Equal((AdviceCheckLevel.Warn, "ORDER_YMD"), (check.Level, check.Subject));
        Assert.NotNull(Find(Run(ymd with { Interval = AdviceInterval.None }), AdviceCheckCodes.IntervalString));
        Assert.Null(Find(Run(MonthlyRange()), AdviceCheckCodes.IntervalString));
    }

    [Fact]
    public void Interval_with_null_rows_stops_and_range_without_interval_warns()
    {
        var cancel = MonthlyRange() with { KeyColumns = ["CANCEL_DT"] };

        var stop = One(Run(cancel), AdviceCheckCodes.IntervalNull);
        Assert.Equal((AdviceCheckLevel.Stop, "CANCEL_DT", "1200"), (stop.Level, stop.Subject, stop.Args![0]));
        Assert.Null(Find(Run(cancel), AdviceCheckCodes.RangeNull));

        var warn = One(Run(cancel with { Interval = AdviceInterval.None }), AdviceCheckCodes.RangeNull);
        Assert.Equal(AdviceCheckLevel.Warn, warn.Level);
        Assert.Null(Find(Run(cancel with { Interval = AdviceInterval.None }), AdviceCheckCodes.IntervalNull));

        Assert.Null(Find(Run(MonthlyRange()), AdviceCheckCodes.IntervalNull)); // NULL 0 행
    }

    [Fact]
    public void Null_rows_seen_only_in_the_distribution_count_too()
    {
        var e = Orders();
        e = e with { Columns = [.. e.Columns.Select(c => c.Name == "REGION" ? c with { NumNulls = null } : c)] };
        var list = StatusList(new ListGroup("P_A", ["SEOUL_ZQ", "BUSAN_ZQ"], false)) with { KeyColumns = ["REGION"] };
        var range = list with { Method = AdviceMethod.Range, Layout = new PartitionLayout(AdviceLayoutKind.NumberRanges, Bounds: ["M"]) };

        Assert.Equal("3", One(Run(range, e), AdviceCheckCodes.RangeNull).Args![0]);
    }

    [Fact]
    public void Datelike_ratio_stops_below_95_warns_below_100_and_passes_at_100()
    {
        var ym = MonthlyRange() with { KeyColumns = ["ORDER_YM"], Interval = AdviceInterval.None };
        var warn = One(Run(ym), AdviceCheckCodes.DateLikeRatio);
        Assert.Equal((AdviceCheckLevel.Warn, "97.0", "500"), (warn.Level, warn.Args![0], warn.Args[1]));

        var e = Orders();
        var low = e with { Columns = [.. e.Columns.Select(c => c.Name == "ORDER_YM" ? c with { DateLike = new DateLike(DateLikeFormat.Yyyymm, 0.9499, 500) } : c)] };
        Assert.Equal(AdviceCheckLevel.Stop, One(Run(ym, low), AdviceCheckCodes.DateLikeRatio).Level);
        var edge = e with { Columns = [.. e.Columns.Select(c => c.Name == "ORDER_YM" ? c with { DateLike = new DateLike(DateLikeFormat.Yyyymm, 0.95, 500) } : c)] };
        Assert.Equal(AdviceCheckLevel.Warn, One(Run(ym, edge), AdviceCheckCodes.DateLikeRatio).Level);

        Assert.Null(Find(Run(ym with { KeyColumns = ["ORDER_YMD"] }), AdviceCheckCodes.DateLikeRatio)); // 100%
    }

    [Fact]
    public void Pk_without_the_key_warns_global_unless_its_index_goes_local()
    {
        var check = One(Run(MonthlyRange()), AdviceCheckCodes.PkGlobal);
        Assert.Equal((AdviceCheckLevel.Warn, "PK_ORDERS"), (check.Level, check.Subject));

        Assert.Null(Find(Run(Hash(8)), AdviceCheckCodes.PkGlobal)); // PK 열 = 키
        Assert.Null(Find(Run(MonthlyRange("PK_ORDERS")), AdviceCheckCodes.PkGlobal)); // local-unique ✖ 가 대신 말함
    }

    [Fact]
    public void Unique_local_index_without_the_key_stops_and_prefixed_local_is_ok()
    {
        var checks = Run(MonthlyRange("PK_ORDERS", "UX_ORDERS_ID_DT", "IX_ORDERS_STATUS_DT"));

        Assert.Equal("PK_ORDERS", One(checks, AdviceCheckCodes.LocalUnique).Subject);
        Assert.Null(Find(checks, AdviceCheckCodes.LocalOk)); // UX 는 키를 포함하지만 선두가 아님, STATUS_DT 도 비접두
        Assert.False(PartitionAdviceChecker.CanFill(checks));

        var sub = MonthlyRange("UX_ORDERS_ID_DT") with { Subpartition = new SubpartitionAdvice(AdviceMethod.Hash, ["STATUS"], HashCount: 4) };
        Assert.NotNull(Find(Run(sub), AdviceCheckCodes.LocalUnique)); // 하위 키도 고유 인덱스에 있어야 함
    }

    [Fact]
    public void Unknown_local_index_stops_as_index_missing()
    {
        var check = One(Run(MonthlyRange("IX_NOPE")), AdviceCheckCodes.IndexMissing);

        Assert.Equal((AdviceCheckLevel.Stop, "IX_NOPE"), (check.Level, check.Subject));
    }

    [Fact]
    public void Hash_count_not_a_power_of_two_warns_and_missing_count_stops()
    {
        Assert.Equal(["6"], One(Run(Hash(6)), AdviceCheckCodes.HashPow2).Args);
        Assert.Equal(AdviceCheckLevel.Stop, One(Run(Hash(null)), AdviceCheckCodes.HashPow2).Level);
        Assert.Equal(AdviceCheckLevel.Stop, One(Run(Hash(0)), AdviceCheckCodes.HashPow2).Level);
        Assert.Null(Find(Run(Hash(1)), AdviceCheckCodes.HashPow2));
        Assert.Null(Find(Run(Hash(16)), AdviceCheckCodes.HashPow2));

        var sub = MonthlyRange() with { Subpartition = new SubpartitionAdvice(AdviceMethod.Hash, ["ORDER_ID"], HashCount: 3) };
        Assert.Equal("ORDER_ID", One(Run(sub), AdviceCheckCodes.HashPow2).Subject);
    }

    [Fact]
    public void List_that_misses_a_top_value_without_default_warns_with_the_count_and_rows()
    {
        var check = One(Run(StatusList(new ListGroup("P_OPEN", ["OPEN_ZQ"], false), new ListGroup("P_CLOSED", ["CLOSED_ZQ"], false))), AdviceCheckCodes.ListCover);

        Assert.Equal((AdviceCheckLevel.Warn, "STATUS"), (check.Level, check.Subject));
        Assert.Equal(["1", "1000"], check.Args);
        Assert.Null(Find(Run(StatusList(new ListGroup("P_OPEN", ["OPEN_ZQ"], false), new ListGroup("P_REST", [], true))), AdviceCheckCodes.ListCover));
        Assert.Null(Find(Run(StatusList(new ListGroup("P_ALL", ["OPEN_ZQ", "CLOSED_ZQ", "HOLD_ZQ"], false))), AdviceCheckCodes.ListCover));
    }

    [Fact]
    public void List_placeholders_are_unmasked_for_cover_and_unknown_ones_stop()
    {
        var (_, vault) = PartitionAdvicePrompt.Build(Orders(), new AdvicePromptOptions(AdviceGoal.Query, false, "en"));
        var all = StatusList(new ListGroup("P_ALL", ["V1", "V2", "V3"], false));

        Assert.Null(Find(Run(all, vault: vault), AdviceCheckCodes.ListCover));
        Assert.Null(Find(Run(all, vault: vault), AdviceCheckCodes.ValueUnknown));

        var bad = StatusList(new ListGroup("P_ALL", ["V1", "V2", "V3", "V4", "V9"], true)); // V4 = REGION 값, V9 = 없음
        var checks = Run(bad, vault: vault);
        Assert.Equal(["V4", "V9"], checks.Where(c => c.Code == AdviceCheckCodes.ValueUnknown).Select(c => c.Args![0]));
        Assert.All(checks.Where(c => c.Code == AdviceCheckCodes.ValueUnknown), c => Assert.Equal(AdviceCheckLevel.Stop, c.Level));
    }

    [Fact]
    public void Bounds_out_of_order_duplicated_or_empty_stop()
    {
        Assert.Equal(["50"], One(Run(NumberRanges("100", "50")), AdviceCheckCodes.BoundsOrder).Args);
        Assert.Equal(["100"], One(Run(NumberRanges("100", "100")), AdviceCheckCodes.BoundsOrder).Args);
        Assert.Equal(["MAXVALUE"], One(Run(NumberRanges("MAXVALUE", "100")), AdviceCheckCodes.BoundsOrder).Args);
        Assert.NotNull(Find(Run(NumberRanges()), AdviceCheckCodes.BoundsOrder));
        Assert.NotNull(Find(Run(StatusList(new ListGroup("A", ["OPEN_ZQ"], false), new ListGroup("B", ["OPEN_ZQ"], false))), AdviceCheckCodes.BoundsOrder));
        Assert.NotNull(Find(Run(StatusList(new ListGroup("A", [], true), new ListGroup("B", [], true))), AdviceCheckCodes.BoundsOrder));

        Assert.Null(Find(Run(NumberRanges("9", "10", "100", "MAXVALUE")), AdviceCheckCodes.BoundsOrder)); // 숫자 비교(글 비교면 "10" < "9")
    }

    [Fact]
    public void Dialect_that_cannot_partition_or_interval_stops_and_no_online_conversion_warns()
    {
        var noPart = new Dialect();
        noPart.Unsupported[TableEditFeature.Partitioning] = "TableEdit.unsupported.edition";
        Assert.Equal(["partitioning", "TableEdit.unsupported.edition"], One(Run(MonthlyRange(), d: noPart), AdviceCheckCodes.Dialect).Args);

        var noInterval = new Dialect();
        noInterval.UnsupportedPartition[PartitionOperation.SetInterval] = "k.interval";
        Assert.Equal(AdviceCheckLevel.Stop, One(Run(MonthlyRange(), d: noInterval), AdviceCheckCodes.Dialect).Level);
        Assert.Null(Find(Run(NumberRanges("10"), d: noInterval), AdviceCheckCodes.Dialect)); // INTERVAL 안 씀

        var tibero = new Dialect { Dbms = Dbms.Tibero, Online = false };
        var rebuild = One(Run(MonthlyRange(), d: tibero), AdviceCheckCodes.Dialect);
        Assert.Equal((AdviceCheckLevel.Warn, "rebuild"), (rebuild.Level, rebuild.Args![0]));

        Assert.Equal(AdviceCheckLevel.Warn, One(Run(MonthlyRange(), s: TestData.Server("12.1.0.2")), AdviceCheckCodes.Dialect).Level); // 12.1 = 다시 만들기
        Assert.Null(Find(Run(MonthlyRange()), AdviceCheckCodes.Dialect)); // 19c
    }

    [Fact]
    public void Small_table_warns_under_a_million_rows_or_ten_thousand_blocks()
    {
        var e = Orders();
        Assert.Equal(["999999", "800000"], One(Run(MonthlyRange(), e with { NumRows = 999_999 }), AdviceCheckCodes.SmallTable).Args);
        Assert.NotNull(Find(Run(MonthlyRange(), e with { Blocks = 9_999 }), AdviceCheckCodes.SmallTable));

        Assert.Null(Find(Run(MonthlyRange(), e with { NumRows = 1_000_000, Blocks = 10_000 }), AdviceCheckCodes.SmallTable));
        Assert.Null(Find(Run(MonthlyRange(), e with { NumRows = null, Blocks = null }), AdviceCheckCodes.SmallTable)); // 모르면 말하지 않음
    }

    [Fact]
    public void Stats_older_than_thirty_days_or_never_collected_warn()
    {
        var e = Orders();
        Assert.Equal(["31"], One(Run(MonthlyRange(), e with { LastAnalyzed = CollectedAt.AddDays(-31) }), AdviceCheckCodes.StatsOld).Args);
        Assert.Empty(One(Run(MonthlyRange(), e with { LastAnalyzed = null }), AdviceCheckCodes.StatsOld).Args!);

        Assert.Null(Find(Run(MonthlyRange(), e with { LastAnalyzed = CollectedAt.AddDays(-30) }), AdviceCheckCodes.StatsOld));
    }
}
