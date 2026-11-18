using Oranda.Core.Connection;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Partitions;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;
using Oranda.Testing;

namespace Oranda.Core.Tests.Schema.TableEdit.Partitions.Advisor;

// 소유: pa-core-l1. 추천 시험용 근거·후보(SAMPLE.ORDERS). 실제 값은 눈에 띄는 글(…_ZQ)로 둬 가림 시험이 찾기 쉽게 한다.
internal static class AdvisorSamples
{
    public static readonly DateTime CollectedAt = new(2026, 11, 18, 9, 0, 0, DateTimeKind.Local);

    public static PartitionEvidence Orders() => new(
        "SAMPLE",
        "ORDERS",
        Dbms.Oracle,
        "19.3.0.0.0",
        NumRows: 50_000_000,
        Blocks: 800_000,
        LastAnalyzed: CollectedAt.AddDays(-3),
        Columns:
        [
            new EvidenceColumn("ORDER_ID", "NUMBER", false, 50_000_000, 0, null, null),
            new EvidenceColumn("ORDER_DT", "DATE", false, 900_000, 0, null, null),
            new EvidenceColumn("CANCEL_DT", "DATE", true, 1_000, 1_200, null, null),
            new EvidenceColumn("ORDER_YMD", "VARCHAR2", false, 1_000, 0, null, new DateLike(DateLikeFormat.Yyyymmdd, 1.0, 500)),
            new EvidenceColumn("ORDER_YM", "NUMBER", false, 36, 0, null, new DateLike(DateLikeFormat.Yyyymm, 0.97, 500)),
            new EvidenceColumn("STATUS", "CHAR", false, 3, 0, "FREQUENCY", null),
            new EvidenceColumn("REGION", "VARCHAR2", true, 3, 0, "FREQUENCY", null),
            new EvidenceColumn("CUSTOMER_NAME", "VARCHAR2", true, 3_000_000, 10, null, null),
            new EvidenceColumn("MEMO", "CLOB", true, null, null, null, null),
            new EvidenceColumn("AMOUNT_V", "NUMBER", true, null, null, null, null, IsVirtual: true),
        ],
        Indexes:
        [
            new EvidenceIndex("PK_ORDERS", true, ["ORDER_ID"], true),
            new EvidenceIndex("IX_ORDERS_DT", false, ["ORDER_DT"], false),
            new EvidenceIndex("IX_ORDERS_STATUS_DT", false, ["STATUS", "ORDER_DT"], false),
            new EvidenceIndex("UX_ORDERS_ID_DT", true, ["ORDER_ID", "ORDER_DT"], false),
        ],
        Keys: [new EvidenceKey("PK_ORDERS", EvidenceKeyKind.Primary, ["ORDER_ID"])],
        Predicates: [new PredicateUse("ORDER_DT", 120_000, 14, 0.72), new PredicateUse("STATUS", 30_000, 5, 0.18)],
        Distributions:
        [
            new ColumnDistribution("ORDER_DT", DistributionKind.Monthly, [new("2026-09", 1_000), new("2026-10", 1_100), new("2026-11", 400)], 50_000, 0.1, RestCount: 200),
            new ColumnDistribution("STATUS", DistributionKind.TopValues, [new("OPEN_ZQ", 30_000), new("CLOSED_ZQ", 19_000), new("HOLD_ZQ", 1_000)], 50_000, 0.1),
            new ColumnDistribution("REGION", DistributionKind.TopValues, [new("SEOUL_ZQ", 25_000), new("BUSAN_ZQ", 25_000)], 50_000, 0.1, RestCount: 0, NullCount: 3),
        ],
        Skipped: [new EvidenceSkip(EvidenceItem.Predicates, EvidenceSkipReason.NoPrivilege, Detail: "ORA-01031 SECRET_DETAIL_ZQ")],
        CollectedAt);

    /// <summary>ORDER_DT 월 INTERVAL 범위(문제없는 후보).</summary>
    public static PartitionCandidate MonthlyRange(params string[] localIndexes) => new(
        1,
        "ORDER_DT 월 범위",
        AdviceMethod.Range,
        ["ORDER_DT"],
        AdviceInterval.Month,
        new PartitionLayout(AdviceLayoutKind.Monthly, "2026-09"),
        null,
        localIndexes,
        ["조건 72%"],
        []);

    public static PartitionCandidate StatusList(params ListGroup[] groups) => new(
        2,
        "STATUS 목록",
        AdviceMethod.List,
        ["STATUS"],
        null,
        new PartitionLayout(AdviceLayoutKind.Values, Groups: groups),
        null,
        [],
        ["값 3개"],
        []);

    public static PartitionCandidate Hash(int? count, string key = "ORDER_ID") => new(
        3,
        "해시",
        AdviceMethod.Hash,
        [key],
        null,
        new PartitionLayout(AdviceLayoutKind.Hash, HashCount: count),
        null,
        [],
        [],
        []);

    public static PartitionCandidate NumberRanges(params string[] bounds) => new(
        1,
        "금액 범위",
        AdviceMethod.Range,
        ["ORDER_ID"],
        AdviceInterval.None,
        new PartitionLayout(AdviceLayoutKind.NumberRanges, Bounds: bounds),
        null,
        [],
        [],
        []);

    public static ServerInfo Oracle19() => TestData.Server("19.3.0.0.0");

    /// <summary>파티션 능력을 고칠 수 있는 방언. 나머지는 Core 기본(Oracle 규칙).</summary>
    public sealed class Dialect : ITableEditDialect
    {
        public Dbms Dbms { get; init; } = Dbms.Oracle;

        public IReadOnlyList<string> TypeNames { get; } = ["VARCHAR2", "NUMBER", "DATE"];

        public bool SupportsVirtualColumnModify => true;

        public bool PurgesOnDrop => false;

        public Dictionary<TableEditFeature, string> Unsupported { get; } = [];

        public Dictionary<PartitionOperation, string> UnsupportedPartition { get; } = [];

        public bool? Online { get; init; }

        public int MaxIdentifierBytes(ServerInfo? server) => 128;

        public string? UnsupportedReasonKey(TableEditFeature feature, ServerInfo? server) => Unsupported.GetValueOrDefault(feature);

        public string? UnsupportedPartitionReasonKey(PartitionOperation operation, PartitionMethod method, ServerInfo? server) => UnsupportedPartition.GetValueOrDefault(operation);

        public bool SupportsOnlinePartitionConversion(ServerInfo? server) => Online ?? PartitionDefaults.SupportsOnlinePartitionConversion(server);
    }
}
