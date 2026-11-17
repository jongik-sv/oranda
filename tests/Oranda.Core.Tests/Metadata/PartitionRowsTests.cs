using Oranda.Core.Metadata;

namespace Oranda.Core.Tests.Metadata;

/// <summary>파티션 카탈로그 행 → <see cref="PartitioningInfo"/>(T14). 열 모양은 Oracle·Tibero 공통.</summary>
public class PartitionRowsTests
{
    [Fact]
    public void Scheme_keys_partitions_and_local_indexes_are_read_in_order()
    {
        object?[][] scheme =
        [
            ["RANGE", "HASH", "NUMTOYMINTERVAL(1, 'MONTH') ", "PART", "ORDER_DATE", 1m],
            ["RANGE", "HASH", "NUMTOYMINTERVAL(1, 'MONTH') ", "SUB", "CUSTOMER_ID", 1m],
            ["RANGE", "HASH", "NUMTOYMINTERVAL(1, 'MONTH') ", "PART", "REGION", 2m],
        ];
        object?[][] partitions =
        [
            [1m, "P_2026_09", "RANGE", "HASH", " DATE '2026-10-01' ", "TS_DATA", 182400m, new DateTime(2026, 11, 1)],
            [2m, "SAMPLE_P1914300", "RANGE", "HASH", "TO_DATE('01-12-2026 00:00:00', 'DD-MM-YYYY HH24:mi:ss')", null, 0m, null],
        ];

        var info = PartitionRows.Build(scheme, partitions, [["ORDERS_LIX"]])!;

        Assert.Equal(("RANGE", "NUMTOYMINTERVAL(1, 'MONTH')", "HASH"), (info.Method, info.Interval, info.SubpartitionMethod));
        Assert.Equal(["ORDER_DATE", "REGION"], info.KeyColumns);
        Assert.Equal(["CUSTOMER_ID"], info.SubpartitionKeyColumns);
        Assert.Equal(["ORDERS_LIX"], info.PartitionedIndexes);
        Assert.Equal(("P_2026_09", 1, "DATE '2026-10-01'", "TS_DATA", (long?)182400), (info.Partitions[0].Name, info.Partitions[0].Position, info.Partitions[0].Bound, info.Partitions[0].Tablespace, info.Partitions[0].NumRows));
        Assert.Null(info.Partitions[1].NumRows); // 통계 없음(LAST_ANALYZED NULL) — Tibero 는 0 을 준다
    }

    [Fact]
    public void No_scheme_rows_means_not_partitioned_and_none_subpartitioning_is_null()
    {
        Assert.Null(PartitionRows.Build([], [], []));

        var info = PartitionRows.Build([["HASH", "NONE", null, "PART", "ID", 1m]], [[1m, "P1", "HASH", "NONE", null, null, null, null]], [])!;
        Assert.Equal(("HASH", (string?)null, (string?)null, (string?)null), (info.Method, info.Interval, info.SubpartitionMethod, info.Partitions[0].Bound));
        Assert.Empty(info.SubpartitionKeyColumns);
    }
}
