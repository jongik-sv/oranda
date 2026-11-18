using Oranda.Core.Connection;
using Oranda.Core.Schema.TableEdit.Partitions;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;

namespace Oranda.Core.Tests.Schema.TableEdit.Partitions.Advisor;

// 소유: pa-core PC5. 후보 → 파티션 편집 모델(설계 4.2).
public class PartitionAdviceApplierTests
{
    private static readonly DateOnly Today = new(2026, 11, 18);

    private static PartitionEvidence Evidence(params ColumnDistribution[] distributions) =>
        PartitionEvidence.Empty("SAMPLE", "ORDERS", Dbms.Oracle, new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Local)) with
        {
            Columns =
            [
                new EvidenceColumn("ORDER_DT", "DATE", false, 1000, 0, null, null),
                new EvidenceColumn("ORDER_YMD", "VARCHAR2", true, 1000, 0, null, new DateLike(DateLikeFormat.Yyyymmdd, 1.0, 500)),
                new EvidenceColumn("ORDER_YM", "NUMBER", true, 40, 0, null, new DateLike(DateLikeFormat.Yyyymm, 1.0, 500)),
                new EvidenceColumn("ORDER_DAY", "CHAR", true, 1000, 0, null, new DateLike(DateLikeFormat.YyyyDashMmDashDd, 1.0, 500)),
                new EvidenceColumn("STATUS", "VARCHAR2", false, 4, 0, "FREQUENCY", null),
                new EvidenceColumn("REGION_NO", "NUMBER", false, 5, 0, null, null),
                new EvidenceColumn("CUST_ID", "NUMBER", false, 50_000, 0, null, null),
            ],
            Distributions = distributions,
        };

    private static ColumnDistribution Monthly(string column, params string[] months) =>
        new(column, DistributionKind.Monthly, [.. months.Select(m => new DistributionBucket(m, 10))], 100, null);

    private static PartitionCandidate Range(string column, string? interval, PartitionLayout layout) =>
        new(1, "t", AdviceMethod.Range, [column], interval, layout, null, [], [], []);

    private static PartitionCandidate List(string column, params ListGroup[] groups) =>
        new(1, "t", AdviceMethod.List, [column], null, new PartitionLayout(AdviceLayoutKind.Values, Groups: groups), null, [], [], []);

    private static IReadOnlyList<(string Name, string? Bound)> Rows(AdviceDraft d) => [.. d.Draft.Partitions.Select(p => (p.Name, p.Bound))];

    [Fact]
    public void Date_key_with_month_interval_makes_one_row_after_the_earliest_month()
    {
        var c = Range("ORDER_DT", AdviceInterval.Month, new PartitionLayout(AdviceLayoutKind.Monthly));

        var d = PartitionAdviceApplier.Apply(c, Evidence(Monthly("ORDER_DT", "2024-05", "2024-03", "2026-11")), new EvidenceValueVault(), Today);

        Assert.Empty(d.Problems);
        Assert.Equal((PartitionMethod.Range, "NUMTOYMINTERVAL(1, 'MONTH')"), (d.Draft.Scheme!.Method, d.Draft.Scheme.Interval));
        Assert.Equal([("P_BEFORE_2024_04", "DATE '2024-04-01'")], Rows(d));
    }

    [Fact]
    public void Date_key_without_distribution_starts_like_easy_setup_and_day_year_units_work()
    {
        var month = PartitionAdviceApplier.Apply(Range("ORDER_DT", AdviceInterval.Month, new PartitionLayout(AdviceLayoutKind.Monthly)), Evidence(), new EvidenceValueVault(), Today);
        Assert.Equal([("P_BEFORE_2026_11", "DATE '2026-11-01'")], Rows(month));

        var year = PartitionAdviceApplier.Apply(Range("ORDER_DT", AdviceInterval.Year, new PartitionLayout(AdviceLayoutKind.Yearly)), Evidence(Monthly("ORDER_DT", "2023-07")), new EvidenceValueVault(), Today);
        Assert.Equal([("P_BEFORE_2024", "DATE '2024-01-01'")], Rows(year));
        Assert.Equal("NUMTOYMINTERVAL(1, 'YEAR')", year.Draft.Scheme!.Interval);

        var day = PartitionAdviceApplier.Apply(Range("ORDER_DT", AdviceInterval.Day, new PartitionLayout(AdviceLayoutKind.Daily)), Evidence(), new EvidenceValueVault(), Today);
        Assert.Equal([("P_BEFORE_2026_11_18", "DATE '2026-11-18'")], Rows(day));
    }

    [Fact]
    public void Datelike_string_key_lists_each_month_up_to_periods_ahead_and_a_maxvalue_row()
    {
        var c = Range("ORDER_YMD", AdviceInterval.None, new PartitionLayout(AdviceLayoutKind.Monthly, PeriodsAhead: 2));

        var d = PartitionAdviceApplier.Apply(c, Evidence(Monthly("ORDER_YMD", "2026-08", "2026-09")), new EvidenceValueVault(), Today);

        Assert.Null(d.Draft.Scheme!.Interval);
        Assert.Equal(
            [
                ("P_2026_08", "'20260901'"), ("P_2026_09", "'20261001'"), ("P_2026_10", "'20261101'"),
                ("P_2026_11", "'20261201'"), ("P_2026_12", "'20270101'"), ("P_2027_01", "'20270201'"), ("P_MAX", "MAXVALUE"),
            ],
            Rows(d));
    }

    [Fact]
    public void Datelike_bounds_follow_the_format_and_number_keys_have_no_quotes()
    {
        var number = PartitionAdviceApplier.Apply(Range("ORDER_YM", AdviceInterval.None, new PartitionLayout(AdviceLayoutKind.Monthly, PeriodsAhead: 0)), Evidence(Monthly("ORDER_YM", "2026-10")), new EvidenceValueVault(), Today);
        Assert.Equal([("P_2026_10", "202611"), ("P_2026_11", "202612"), ("P_MAX", "MAXVALUE")], Rows(number));

        var dashed = PartitionAdviceApplier.Apply(Range("ORDER_DAY", AdviceInterval.None, new PartitionLayout(AdviceLayoutKind.Monthly, PeriodsAhead: 0)), Evidence(), new EvidenceValueVault(), Today);
        Assert.Equal([("P_2026_11", "'2026-12-01'"), ("P_MAX", "MAXVALUE")], Rows(dashed)); // 분포 없으면 이번 달부터
    }

    [Fact]
    public void Date_key_with_interval_none_lists_months_with_date_literals()
    {
        var d = PartitionAdviceApplier.Apply(Range("ORDER_DT", AdviceInterval.None, new PartitionLayout(AdviceLayoutKind.Monthly, PeriodsAhead: 1)), Evidence(Monthly("ORDER_DT", "2026-11")), new EvidenceValueVault(), Today);

        Assert.Null(d.Draft.Scheme!.Interval);
        Assert.Equal([("P_2026_11", "DATE '2026-12-01'"), ("P_2026_12", "DATE '2027-01-01'"), ("P_MAX", "MAXVALUE")], Rows(d));
    }

    [Fact]
    public void Number_ranges_become_rows_as_given_with_a_numeric_interval()
    {
        var c = Range("CUST_ID", AdviceInterval.NumberPrefix + "10000", new PartitionLayout(AdviceLayoutKind.NumberRanges, Bounds: ["10000", "20000", "MAXVALUE"]));

        var d = PartitionAdviceApplier.Apply(c, Evidence(), new EvidenceValueVault(), Today);

        Assert.Equal("10000", d.Draft.Scheme!.Interval);
        Assert.Equal([("P1", "10000"), ("P2", "20000"), ("P_MAX", "MAXVALUE")], Rows(d));
    }

    [Fact]
    public void List_groups_unmask_values_quote_text_and_keep_default()
    {
        var vault = new EvidenceValueVault();
        var open = vault.Mask("STATUS", "OPEN");
        var odd = vault.Mask("STATUS", "O'X");
        var c = List("STATUS", new ListGroup("P_OPEN", [open, odd], false), new ListGroup("P_REST", [], true));

        var d = PartitionAdviceApplier.Apply(c, Evidence(), vault, Today);

        Assert.Empty(d.Problems);
        Assert.Equal(PartitionMethod.List, d.Draft.Scheme!.Method);
        Assert.Equal([("P_OPEN", "'OPEN', 'O''X'"), ("P_REST", "DEFAULT")], Rows(d));
    }

    [Fact]
    public void List_on_a_number_key_has_no_quotes_and_real_values_pass_when_nothing_was_masked()
    {
        var d = PartitionAdviceApplier.Apply(List("REGION_NO", new ListGroup("P_EAST", ["1", "2"], false)), Evidence(), new EvidenceValueVault(), Today);
        Assert.Equal([("P_EAST", "1, 2")], Rows(d));

        // 값 보내기를 켜서 금고가 비면 V1 같은 실제 값도 그대로 쓴다.
        var real = PartitionAdviceApplier.Apply(List("STATUS", new ListGroup("P_V", ["V1"], false)), Evidence(), new EvidenceValueVault(), Today);
        Assert.Empty(real.Problems);
        Assert.Equal([("P_V", "'V1'")], Rows(real));
    }

    [Fact]
    public void Unknown_placeholder_is_a_stop_problem()
    {
        var vault = new EvidenceValueVault();
        vault.Mask("STATUS", "OPEN");

        var d = PartitionAdviceApplier.Apply(List("STATUS", new ListGroup("P_X", ["V1", "V9"], false)), Evidence(), vault, Today);

        var p = Assert.Single(d.Problems);
        Assert.Equal((AdviceCheckLevel.Stop, AdviceCheckCodes.ValueUnknown, "STATUS"), (p.Level, p.Code, p.Subject));
        Assert.Equal(["V9"], p.Args);
    }

    [Fact]
    public void Hash_makes_count_rows_without_bounds()
    {
        var c = new PartitionCandidate(1, "t", AdviceMethod.Hash, ["CUST_ID"], null, new PartitionLayout(AdviceLayoutKind.Hash, HashCount: 4), null, [], [], []);

        var d = PartitionAdviceApplier.Apply(c, Evidence(), new EvidenceValueVault(), Today);

        Assert.Equal([("P1", null), ("P2", null), ("P3", null), ("P4", null)], Rows(d));
        Assert.Equal(PartitionMethod.Hash, d.Draft.Scheme!.Method);
    }

    [Fact]
    public void Subpartition_values_are_unmasked_and_local_indexes_copied()
    {
        var vault = new EvidenceValueVault();
        var open = vault.Mask("STATUS", "OPEN");
        var sub = new SubpartitionAdvice(AdviceMethod.List, ["STATUS"], [new ListGroup("SP_OPEN", [open], false), new ListGroup("SP_REST", [], true)]);
        var c = Range("ORDER_DT", AdviceInterval.Month, new PartitionLayout(AdviceLayoutKind.Monthly)) with { Subpartition = sub, LocalIndexes = ["IX_ORDERS_DT"] };

        var d = PartitionAdviceApplier.Apply(c, Evidence(), vault, Today);

        Assert.Equal(["OPEN"], d.Subpartition!.Groups![0].Values);
        Assert.True(d.Subpartition.Groups[1].IsDefault);
        Assert.Equal(["IX_ORDERS_DT"], d.LocalIndexes);
    }

    [Fact]
    public void Missing_key_column_or_unknown_method_gives_an_empty_draft_and_a_stop()
    {
        var missing = PartitionAdviceApplier.Apply(Range("NOPE", AdviceInterval.Month, new PartitionLayout(AdviceLayoutKind.Monthly)), Evidence(), new EvidenceValueVault(), Today);
        Assert.Equal(AdviceCheckCodes.KeyMissing, Assert.Single(missing.Problems).Code);
        Assert.Null(missing.Draft.Scheme);

        var unknown = PartitionAdviceApplier.Apply(Range("ORDER_DT", null, new PartitionLayout(AdviceLayoutKind.Monthly)) with { Method = "reference" }, Evidence(), new EvidenceValueVault(), Today);
        Assert.Equal(AdviceCheckCodes.MethodUnknown, Assert.Single(unknown.Problems).Code);
    }
}

// 소유: pa-core PC5 후속. 하위 파티션 템플릿·LOCAL 인덱스를 모델(PartitionScheme.SubpartitionTemplate·PartitionTableDraft.LocalIndexes)에 채움.
public class PartitionAdviceApplierModelTests
{
    private static readonly DateOnly Today = new(2026, 11, 18);

    private static PartitionEvidence Evidence() =>
        PartitionEvidence.Empty("SAMPLE", "ORDERS", Dbms.Oracle, new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Local)) with
        {
            Columns =
            [
                new EvidenceColumn("ORDER_DT", "DATE", false, 1000, 0, null, null),
                new EvidenceColumn("STATUS", "VARCHAR2", false, 4, 0, "FREQUENCY", null),
                new EvidenceColumn("REGION_NO", "NUMBER", false, 5, 0, null, null),
            ],
        };

    private static PartitionCandidate Monthly(SubpartitionAdvice? sub, params string[] local) =>
        new(1, "t", AdviceMethod.Range, ["ORDER_DT"], AdviceInterval.Month, new PartitionLayout(AdviceLayoutKind.Monthly), sub, local, [], []);

    [Fact]
    public void List_subpartition_becomes_template_rows_with_real_quoted_values_and_default()
    {
        var vault = new EvidenceValueVault();
        var open = vault.Mask("STATUS", "OPEN");
        var sub = new SubpartitionAdvice(AdviceMethod.List, ["status"], [new ListGroup("SP_OPEN", [open], false), new ListGroup("SP_REST", [], true)]);

        var d = PartitionAdviceApplier.Apply(Monthly(sub, "IX_ORDERS_DT"), Evidence(), vault, Today);

        var t = d.Draft.Scheme!.SubpartitionTemplate!;
        Assert.Equal((PartitionMethod.List, "STATUS"), (t.Method, Assert.Single(t.KeyColumns)));
        Assert.Equal([new SubpartitionTemplateRow("SP_OPEN", "'OPEN'"), new SubpartitionTemplateRow("SP_REST", "DEFAULT")], t.Rows);
        Assert.Equal(["IX_ORDERS_DT"], d.Draft.LocalIndexes);
        Assert.Equal(["IX_ORDERS_DT"], d.LocalIndexes); // 예전 칸도 같이
        Assert.Equal(["OPEN"], d.Subpartition!.Groups![0].Values);
        Assert.Empty(d.Problems);
    }

    [Fact]
    public void Hash_subpartition_count_and_number_list_values()
    {
        var hash = PartitionAdviceApplier.Apply(Monthly(new SubpartitionAdvice(AdviceMethod.Hash, ["REGION_NO"], HashCount: 8)), Evidence(), new EvidenceValueVault(), Today);
        Assert.Equal((PartitionMethod.Hash, 8), (hash.Draft.Scheme!.SubpartitionTemplate!.Method, hash.Draft.Scheme.SubpartitionTemplate.HashCount));

        var noCount = PartitionAdviceApplier.Apply(Monthly(new SubpartitionAdvice(AdviceMethod.Hash, ["REGION_NO"])), Evidence(), new EvidenceValueVault(), Today);
        Assert.Equal(PartitionAdviceApplier.DefaultHashCount, noCount.Draft.Scheme!.SubpartitionTemplate!.HashCount);

        var list = PartitionAdviceApplier.Apply(Monthly(new SubpartitionAdvice(AdviceMethod.List, ["REGION_NO"], [new ListGroup("SP_A", ["1", "2"], false)])), Evidence(), new EvidenceValueVault(), Today);
        Assert.Equal("1, 2", list.Draft.Scheme!.SubpartitionTemplate!.Rows[0].Values);
    }

    [Fact]
    public void Range_or_unknown_subpartition_and_missing_sub_key_stop_without_a_template()
    {
        var range = PartitionAdviceApplier.Apply(Monthly(new SubpartitionAdvice("range", ["REGION_NO"])), Evidence(), new EvidenceValueVault(), Today);
        var p = Assert.Single(range.Problems);
        Assert.Equal((AdviceCheckLevel.Stop, AdviceCheckCodes.MethodUnknown, "range"), (p.Level, p.Code, p.Subject));
        Assert.Equal(["subpartition"], p.Args);
        Assert.Null(range.Draft.Scheme!.SubpartitionTemplate);
        Assert.False(PartitionAdviceChecker.CanFill(range.Problems));

        var gone = PartitionAdviceApplier.Apply(Monthly(new SubpartitionAdvice(AdviceMethod.Hash, ["GONE"], HashCount: 4)), Evidence(), new EvidenceValueVault(), Today);
        Assert.Equal((AdviceCheckCodes.KeyMissing, "GONE"), (Assert.Single(gone.Problems).Code, gone.Problems[0].Subject));
        Assert.Null(gone.Draft.Scheme!.SubpartitionTemplate);
    }

    [Fact]
    public void Checker_local_unique_counts_the_subpartition_key()
    {
        // ORA-14039: 고유 LOCAL 인덱스는 파티션 키와 하위 키를 모두 품어야 한다.
        var e = Evidence() with
        {
            Indexes =
            [
                new EvidenceIndex("UX_DT", true, ["ORDER_DT"], false),
                new EvidenceIndex("UX_DT_ST", true, ["ORDER_DT", "STATUS"], false),
            ],
        };
        var sub = new SubpartitionAdvice(AdviceMethod.Hash, ["STATUS"], HashCount: 4);

        var checks = PartitionAdviceChecker.Check(Monthly(sub, "UX_DT", "UX_DT_ST"), e, new AdvisorSamples.Dialect(), AdvisorSamples.Oracle19());

        var stop = Assert.Single(checks, c => c.Code == AdviceCheckCodes.LocalUnique);
        Assert.Equal(("UX_DT", "STATUS"), (stop.Subject, stop.Args![0]));
        Assert.Contains(checks, c => c is { Code: AdviceCheckCodes.LocalOk, Subject: "UX_DT_ST" });
    }
}
