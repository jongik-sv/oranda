using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using Oranda.Testing;

namespace Oranda.Core.Tests.Schema.TableEdit.Partitions.Advisor;

// 소유: pa-core PC2. 근거 모으기(설계 5장 열 고르기, PA-1 표본, 권한 없음 → 건너뜀, 진행·취소).
public class PartitionEvidenceCollectorTests
{
    private static readonly DbObjectRef Orders = new("SAMPLE", "ORDERS", "TABLE");

    private static FakeSession Session() => new(TestData.Profile());

    private static TableInfo OrdersTable() => new(Orders,
    [
        new ColumnInfo("ORDER_ID", 1, "NUMBER") { IsNullable = false, Precision = 12, Scale = 0 },
        new ColumnInfo("ORDER_DT", 2, "DATE") { IsNullable = false },
        new ColumnInfo("ORDER_YMD", 3, "VARCHAR2") { Length = 8 },
        new ColumnInfo("STATUS", 4, "VARCHAR2") { Length = 10 },
        new ColumnInfo("CUST_ID", 5, "NUMBER") { Precision = 10, Scale = 0 },
        new ColumnInfo("MEMO", 6, "CLOB"),
        new ColumnInfo("YEAR_V", 7, "NUMBER") { IsVirtual = true },
    ])
    {
        Constraints =
        [
            new ConstraintInfo("PK_ORDERS", ConstraintKind.PrimaryKey, ["ORDER_ID"]),
            new ConstraintInfo("FK_ORDERS_CUST", ConstraintKind.ForeignKey, ["CUST_ID"]),
            new ConstraintInfo("CK_STATUS", ConstraintKind.Check, ["STATUS"]),
        ],
        Indexes =
        [
            new IndexInfo("PK_ORDERS", true, ["ORDER_ID"]) { ConstraintName = "PK_ORDERS" },
            new IndexInfo("IX_ORDERS_DT", false, ["ORDER_DT", "STATUS"]),
        ],
    };

    private static (PartitionEvidenceCollector Collector, FakeMetadataProvider Metadata, StatsTuning Tuning, ScriptedProvider Provider) Build(long? numRows = 3_000_000)
    {
        var metadata = new FakeMetadataProvider();
        metadata.Tables[Orders] = OrdersTable();
        var tuning = new StatsTuning(numRows);
        var provider = new ScriptedProvider();
        var selector = new DbmsServiceTable().Add<IDbErrorClassifier>(Dbms.Oracle, () => new DenyClassifier());
        return (new PartitionEvidenceCollector(metadata, tuning, provider, selector), metadata, tuning, provider);
    }

    [Fact]
    public async Task Columns_keys_indexes_and_statistics_come_together()
    {
        var (collector, _, _, _) = Build();

        var e = await collector.CollectAsync(Session(), Orders, EvidenceOptions.Default, null, default);

        Assert.Equal((3_000_000L, 40_000L), (e.NumRows!.Value, e.Blocks!.Value));
        Assert.Equal(new DateTime(2026, 11, 1), e.LastAnalyzed);
        Assert.Equal(7, e.Columns.Count);
        var status = e.Column("status")!;
        Assert.Equal((12L, 0L, "FREQUENCY"), (status.NumDistinct!.Value, status.NumNulls!.Value, status.Histogram));
        Assert.Null(e.Column("ORDER_DT")!.Histogram); // NONE = 없음
        Assert.False(e.Column("ORDER_DT")!.Nullable);
        Assert.True(e.Column("YEAR_V")!.IsVirtual);
        Assert.Equal([("PK_ORDERS", EvidenceKeyKind.Primary), ("FK_ORDERS_CUST", EvidenceKeyKind.Foreign)], e.Keys.Select(k => (k.Name, k.Kind)));
        Assert.Equal([("PK_ORDERS", true, true), ("IX_ORDERS_DT", false, false)], e.Indexes.Select(i => (i.Name, i.Unique, i.PrimaryKey)));
        Assert.Equal(["ORDER_DT", "STATUS"], e.Indexes[1].Columns);
        Assert.Equal(Dbms.Oracle, e.Dbms);
    }

    [Fact]
    public async Task Distribution_columns_are_dates_datelike_and_low_cardinality_codes_ordered_by_predicate_use()
    {
        var (collector, _, _, provider) = Build();
        provider.Predicates = [new PredicateUse("STATUS", 900, 4, 0.6), new PredicateUse("ORDER_YMD", 300, 2, 0.2)];
        provider.DateLikes["ORDER_YMD"] = new DateLike(DateLikeFormat.Yyyymmdd, 1.0, 500);

        var e = await collector.CollectAsync(Session(), Orders, EvidenceOptions.Default, null, default);

        // STATUS(조건 900) → ORDER_YMD(300) → ORDER_DT(조건 없음, 날짜). PK·CLOB·가상·고유값 많은 CUST_ID 는 빠짐.
        Assert.Equal(["STATUS", "ORDER_YMD", "ORDER_DT"], e.Distributions.Select(d => d.Column));
        Assert.Equal([DistributionKind.TopValues, DistributionKind.Monthly, DistributionKind.Monthly], e.Distributions.Select(d => d.Kind));
        Assert.Equal(DateLikeFormat.Yyyymmdd, e.Column("ORDER_YMD")!.DateLike!.Format);
        Assert.Contains(("ORDER_YMD", (DateLikeFormat?)DateLikeFormat.Yyyymmdd), provider.MonthlyCalls.Select(c => (c.Column, c.Format)));
        Assert.Contains(("ORDER_DT", (DateLikeFormat?)null), provider.MonthlyCalls.Select(c => (c.Column, c.Format)));
        Assert.DoesNotContain("ORDER_ID", provider.DateLikeCalls); // PK 단일 열은 날짜형 판정도 하지 않음
        Assert.Equal(20, provider.TopCalls.Single().Top);
        Assert.Equal(2, e.Predicates.Count);
    }

    [Fact]
    public async Task Datelike_below_95_percent_is_kept_on_the_column_but_not_sampled_monthly()
    {
        var (collector, _, _, provider) = Build();
        provider.DateLikes["ORDER_YMD"] = new DateLike(DateLikeFormat.Yyyymmdd, 0.9, 500);

        var e = await collector.CollectAsync(Session(), Orders, EvidenceOptions.Default, null, default);

        Assert.Equal(0.9, e.Column("ORDER_YMD")!.DateLike!.MatchRatio);
        Assert.DoesNotContain("ORDER_YMD", provider.MonthlyCalls.Select(c => c.Column));
    }

    [Fact]
    public async Task At_most_eight_distribution_columns()
    {
        var metadata = new FakeMetadataProvider();
        metadata.Tables[Orders] = new TableInfo(Orders, [.. Enumerable.Range(1, 12).Select(i => new ColumnInfo("D" + i, i, "DATE"))]);
        var provider = new ScriptedProvider();
        var collector = new PartitionEvidenceCollector(metadata, new StatsTuning(1000), provider);

        var e = await collector.CollectAsync(Session(), Orders, EvidenceOptions.Default, null, default);

        Assert.Equal(8, e.Distributions.Count);
        Assert.Equal("D1", e.Distributions[0].Column); // 조건 사용이 같으면 열 순서
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0L, null)]
    [InlineData(200_000L, null)]
    [InlineData(200_001L, 10.0)]
    [InlineData(1_000_000L, 5.0)]
    [InlineData(100_000_000L, 0.05)]
    [InlineData(10_000_000_000L, 0.01)]
    [InlineData(1_000_000_000L, 0.01)]
    [InlineData(300_000L, 10.0)]
    public void Sample_percent_follows_pa_1(long? numRows, double? expected)
    {
        Assert.Equal(expected, EvidenceSampling.PercentFor(numRows));
    }

    [Fact]
    public void Planned_rows_is_the_row_count_or_about_fifty_thousand()
    {
        Assert.Equal(150_000L, EvidenceSampling.PlannedRows(150_000));
        Assert.Equal(50_000L, EvidenceSampling.PlannedRows(1_000_000));
        Assert.Equal(30_000L, EvidenceSampling.PlannedRows(300_000)); // 10% 상한
        Assert.Null(EvidenceSampling.PlannedRows(null));
    }

    [Fact]
    public async Task Sample_percent_reaches_the_provider_and_a_fixed_option_wins()
    {
        var (collector, _, _, provider) = Build(numRows: 1_000_000);

        await collector.CollectAsync(Session(), Orders, EvidenceOptions.Default, null, default);
        Assert.All(provider.MonthlyCalls, c => Assert.Equal(5.0, c.Percent));

        provider.MonthlyCalls.Clear();
        await collector.CollectAsync(Session(), Orders, new EvidenceOptions(SamplePercent: 1.5), null, default);
        Assert.All(provider.MonthlyCalls, c => Assert.Equal(1.5, c.Percent));
    }

    [Fact]
    public async Task Off_options_are_skipped_without_asking_the_provider()
    {
        var (collector, _, _, provider) = Build();

        var e = await collector.CollectAsync(Session(), Orders, new EvidenceOptions(Predicates: false, Distributions: false), null, default);

        Assert.Equal(0, provider.Calls);
        Assert.Contains(new EvidenceSkip(EvidenceItem.Predicates, EvidenceSkipReason.Off), e.Skipped);
        Assert.Contains(new EvidenceSkip(EvidenceItem.Distributions, EvidenceSkipReason.Off), e.Skipped);
        Assert.Empty(e.Distributions);
    }

    [Fact]
    public async Task No_privilege_and_unsupported_become_skips_and_collection_goes_on()
    {
        var (collector, _, _, provider) = Build();
        provider.PredicateError = new DeniedException();
        provider.TopReturnsNull = true;

        var e = await collector.CollectAsync(Session(), Orders, EvidenceOptions.Default, null, default);

        Assert.Contains(e.Skipped, s => s is { Item: EvidenceItem.Predicates, Reason: EvidenceSkipReason.NoPrivilege });
        Assert.Contains(e.Skipped, s => s is { Item: EvidenceItem.Distributions, Reason: EvidenceSkipReason.NotSupported, Column: "STATUS" });
        Assert.Contains(e.Distributions, d => d.Column == "ORDER_DT"); // 다른 열은 계속
    }

    [Fact]
    public async Task Other_errors_are_failed_skips_with_the_message()
    {
        var (collector, _, _, provider) = Build();
        provider.MonthlyError = new InvalidOperationException("boom");

        var e = await collector.CollectAsync(Session(), Orders, EvidenceOptions.Default, null, default);

        Assert.Contains(e.Skipped, s => s is { Item: EvidenceItem.Distributions, Reason: EvidenceSkipReason.Failed, Column: "ORDER_DT", Detail: "boom" });
    }

    [Fact]
    public async Task Missing_statistics_and_unsupported_tuning_are_skips()
    {
        var metadata = new FakeMetadataProvider();
        metadata.Tables[Orders] = OrdersTable();
        var e = await new PartitionEvidenceCollector(metadata, new StatsTuning(null), new ScriptedProvider())
            .CollectAsync(Session(), Orders, EvidenceOptions.Default, null, default);
        Assert.Contains(new EvidenceSkip(EvidenceItem.Statistics, EvidenceSkipReason.NoStatistics), e.Skipped);

        var bare = await new PartitionEvidenceCollector(metadata, new NoTuning(), new ScriptedProvider())
            .CollectAsync(Session(), Orders, EvidenceOptions.Default, null, default);
        Assert.Contains(new EvidenceSkip(EvidenceItem.Statistics, EvidenceSkipReason.NotSupported), bare.Skipped);
        Assert.Equal(7, bare.Columns.Count);
    }

    [Fact]
    public async Task Progress_reports_steps_in_order_and_each_distribution_column()
    {
        var (collector, _, _, _) = Build();
        var seen = new List<EvidenceProgress>();

        await collector.CollectAsync(Session(), Orders, EvidenceOptions.Default, new SyncProgress(seen.Add), default);

        Assert.Equal([EvidenceStep.ColumnsAndKeys, EvidenceStep.Statistics, EvidenceStep.Predicates, EvidenceStep.Distributions], seen.Select(p => p.Step).Distinct());
        var dist = seen.Where(p => p is { Step: EvidenceStep.Distributions, Column: not null }).ToList();
        Assert.Equal(dist.Count, dist[0].Total);
        Assert.Equal(Enumerable.Range(1, dist.Count), dist.Select(p => p.Index));
    }

    [Fact]
    public async Task Cancel_in_the_middle_throws_and_is_not_a_skip()
    {
        var (collector, _, _, provider) = Build();
        using var cts = new CancellationTokenSource();
        provider.OnMonthly = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collector.CollectAsync(Session(), Orders, EvidenceOptions.Default, null, cts.Token));
    }

    private sealed class SyncProgress(Action<EvidenceProgress> report) : IProgress<EvidenceProgress>
    {
        public void Report(EvidenceProgress value) => report(value);
    }

    private sealed class DeniedException() : Exception("ORA-01031");

    private sealed class DenyClassifier : IDbErrorClassifier
    {
        public Dbms Dbms => Dbms.Oracle;

        public DbErrorKind Classify(int number) => DbErrorKind.Other;

        public DbError? FromException(Exception exception) =>
            exception is DeniedException ? new DbError("ORA-01031", "insufficient privileges") { Number = 1031, Kind = DbErrorKind.InsufficientPrivileges } : null;
    }

    private sealed class NoTuning : ITuningService
    {
    }

    private sealed class StatsTuning(long? numRows) : ITuningService
    {
        public Task<TableStatsRow?> GetTableStatsAsync(ISession s, string owner, string table, CancellationToken ct) =>
            Task.FromResult(numRows is null ? null : new TableStatsRow(owner, table, numRows, 40_000, 120, new DateTime(2026, 11, 1), false));

        public Task<IReadOnlyList<ColumnStatsRow>> GetColumnStatsAsync(ISession s, string owner, string table, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ColumnStatsRow>>(
            [
                new("ORDER_ID", 3_000_000, 0, null, "NONE", 1, null),
                new("ORDER_DT", 1000, 0, null, "NONE", 1, null),
                new("ORDER_YMD", 1000, 5, null, "NONE", 1, null),
                new("STATUS", 12, 0, null, "FREQUENCY", 12, null),
                new("CUST_ID", 50_000, 0, null, "NONE", 1, null),
            ]);
    }

    private sealed class ScriptedProvider : IPartitionEvidenceProvider
    {
        public int Calls { get; private set; }

        public IReadOnlyList<PredicateUse> Predicates { get; set; } = [];

        public Exception? PredicateError { get; set; }

        public Exception? MonthlyError { get; set; }

        public bool TopReturnsNull { get; set; }

        public Action? OnMonthly { get; set; }

        public Dictionary<string, DateLike> DateLikes { get; } = [];

        public List<string> DateLikeCalls { get; } = [];

        public List<(string Column, DateLikeFormat? Format, double? Percent)> MonthlyCalls { get; } = [];

        public List<(string Column, int Top)> TopCalls { get; } = [];

        public Task<IReadOnlyList<PredicateUse>?> GetPredicateUseAsync(ISession s, string owner, string table, CancellationToken ct)
        {
            Calls++;
            return PredicateError is { } e ? Task.FromException<IReadOnlyList<PredicateUse>?>(e) : Task.FromResult<IReadOnlyList<PredicateUse>?>(Predicates);
        }

        public Task<DateLike?> DetectDateLikeAsync(ISession s, string owner, string table, string column, CancellationToken ct)
        {
            Calls++;
            DateLikeCalls.Add(column);
            return Task.FromResult(DateLikes.GetValueOrDefault(column));
        }

        public Task<ColumnDistribution?> SampleMonthlyAsync(ISession s, string owner, string table, string column, DateLikeFormat? format, double? samplePercent, CancellationToken ct)
        {
            Calls++;
            MonthlyCalls.Add((column, format, samplePercent));
            OnMonthly?.Invoke();
            ct.ThrowIfCancellationRequested();
            return MonthlyError is { } e
                ? Task.FromException<ColumnDistribution?>(e)
                : Task.FromResult<ColumnDistribution?>(new ColumnDistribution(column, DistributionKind.Monthly, [new DistributionBucket("2026-01", 10)], 10, samplePercent));
        }

        public Task<ColumnDistribution?> SampleTopValuesAsync(ISession s, string owner, string table, string column, int top, double? samplePercent, CancellationToken ct)
        {
            Calls++;
            TopCalls.Add((column, top));
            return Task.FromResult(TopReturnsNull ? null : new ColumnDistribution(column, DistributionKind.TopValues, [new DistributionBucket("OPEN", 7)], 10, samplePercent));
        }
    }
}
