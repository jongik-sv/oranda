using Oranda.Core.Metadata;
using Oranda.Tibero.Schema;
using static Oranda.Tibero.Tests.Schema.SchemaFakes;

namespace Oranda.Tibero.Tests.Schema;

/// <summary>
/// 테이블 편집기 메타데이터(TE-29)를 Tibero 카탈로그 끝 열과 <c>meta.table_kind</c> 에서 읽는지 본다(가짜 카탈로그 실행기).
/// 끝 열 위치: meta.columns [14] char_used · meta.constraints [9] delete_rule [10] r_column_name [11] index_name · meta.indexes [5] descend
/// [6] column_expression [7] tablespace_name [8] constraint_name. 예전 행 모양(끝 열 없음)이면 모두 기본값.
/// </summary>
public class TiberoMetadataTableEditTests
{
    private static readonly DbObjectRef Orders = new("SAMPLE", "ORDERS", "TABLE");

    private static readonly string[] ColumnHeader =
    [
        "COLUMN_NAME", "POSITION", "DATA_TYPE", "DATA_LENGTH", "DATA_PRECISION", "DATA_SCALE", "NULLABLE", "DATA_DEFAULT",
        "VIRTUAL_COLUMN", "IDENTITY_FLAG", "HIDDEN_COLUMN", "DOMAIN_NAME", "COMMENTS", "BYTE_LENGTH", "CHAR_USED",
    ];

    private static readonly string[] ConstraintHeader =
    [
        "CONSTRAINT_NAME", "CONSTRAINT_TYPE", "STATUS", "SEARCH_CONDITION", "R_OWNER", "R_TABLE_NAME", "COLUMN_NAME", "POSITION", "R_CONSTRAINT_NAME",
        "DELETE_RULE", "R_COLUMN_NAME", "INDEX_NAME", "VALIDATED", "GENERATED",
    ];

    private static readonly string[] IndexHeader =
    [
        "INDEX_NAME", "UNIQUENESS", "INDEX_TYPE", "COLUMN_NAME", "COLUMN_POSITION", "DESCEND", "COLUMN_EXPRESSION", "TABLESPACE_NAME", "CONSTRAINT_NAME",
    ];

    private static readonly string[] KindHeader = ["TABLESPACE_NAME", "TEMPORARY", "DURATION", "IOT_TYPE", "PARTITIONED", "EXTERNAL_COUNT"];

    [Fact]
    public async Task Table_edit_metadata_is_read_from_the_trailing_catalog_columns_and_the_table_kind()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = new TiberoMetadataProvider(Recording(calls, ById(
            ("meta.columns", Table(ColumnHeader,
                ["ID", 1m, "NUMBER", null, null, null, "N", null, "NO", null, "NO", null, null, null, null],
                ["NOTE", 2m, "VARCHAR", 10m, null, null, "Y", null, "NO", null, "NO", null, null, 10m, "C"],
                ["CODE", 3m, "CHAR", 3m, null, null, "Y", null, "NO", null, "NO", null, null, 3m, "B"],
                ["PA", 4m, "NUMBER", null, null, null, "Y", null, "NO", null, "NO", null, null, null, null],
                ["PB", 5m, "NUMBER", null, null, null, "Y", null, "NO", null, "NO", null, null, null, null])),
            ("meta.constraints", Table(ConstraintHeader,
                ["ORDERS_PK", "P", "ENABLED", null, null, null, "ID", 1m, null, null, null, "ORDERS_PK", "VALIDATED", "USER NAME"],
                ["ORDERS_FK", "R", "ENABLED", null, "SAMPLE", "ORDER_ITEMS", "PA", 1m, "ITEMS_PK", "CASCADE", "A", null, "VALIDATED", "USER NAME"],
                ["ORDERS_FK", "R", "ENABLED", null, "SAMPLE", "ORDER_ITEMS", "PB", 2m, "ITEMS_PK", "CASCADE", "B", null, "VALIDATED", "USER NAME"],
                ["ORDERS_FK2", "R", "ENABLED", null, "SAMPLE", "ORDER_ITEMS", "PA", 1m, "ITEMS_PK", "SET NULL", "A", null, "VALIDATED", "USER NAME"],
                ["ORDERS_FK3", "R", "ENABLED", null, "SAMPLE", "ORDER_ITEMS", "PB", 1m, "ITEMS_PK", "NO ACTION", "A", null, "NOT VALIDATED", "USER NAME"],
                ["SAMPLE_CON1", "U", "ENABLED", null, null, null, "CODE", 1m, null, null, null, "SAMPLE_CON1", "VALIDATED", "GENERATED NAME"])),
            ("meta.indexes", Table(IndexHeader,
                ["ORDERS_PK", "UNIQUE", "NORMAL", "ID", 1m, "ASC", null, "TS_DATA", "ORDERS_PK"],
                ["ORDERS_D", "NONUNIQUE", "NORMAL", "NOTE", 1m, "DESC", null, "TS_IDX", null],
                ["ORDERS_D", "NONUNIQUE", "NORMAL", "CODE", 2m, "ASC", null, "TS_IDX", null],
                ["ORDERS_F", "NONUNIQUE", "FUNCTION-BASED NORMAL", "- EXPRESSION COLUMN -", 1m, "ASC", "UPPER(\"NOTE\")", "TS_IDX", null],
                ["ORDERS_F", "NONUNIQUE", "FUNCTION-BASED NORMAL", "ID", 2m, "ASC", null, "TS_IDX", null])),
            ("meta.table_kind", Table(KindHeader, ["TS_DATA", "NO", null, null, "NO", 0m])))));
        await using var session = Session();

        var table = await provider.GetTableAsync(session, Orders, CancellationToken.None);

        Assert.Contains(calls, c => c.Id == "meta.table_kind" && Equals(c.Binds!["owner"], "SAMPLE") && Equals(c.Binds!["name"], "ORDERS"));
        Assert.Equal([null, CharSemantics.Characters, CharSemantics.Bytes, null, null], table.Columns.Select(c => c.CharSemantics));

        var byName = table.Constraints.ToDictionary(c => c.Name);
        Assert.Equal(DeleteRule.Cascade, byName["ORDERS_FK"].DeleteRule);
        Assert.Equal(["A", "B"], byName["ORDERS_FK"].ReferencedColumns);
        Assert.Equal(DeleteRule.SetNull, byName["ORDERS_FK2"].DeleteRule);
        Assert.Equal(DeleteRule.NoAction, byName["ORDERS_FK3"].DeleteRule);
        Assert.Empty(byName["ORDERS_PK"].ReferencedColumns);
        Assert.Equal(DeleteRule.NoAction, byName["SAMPLE_CON1"].DeleteRule);

        var index = table.Indexes.ToDictionary(i => i.Name);
        Assert.Equal(("ORDERS_PK", "TS_DATA"), (index["ORDERS_PK"].ConstraintName, index["ORDERS_PK"].Tablespace));
        Assert.Empty(index["ORDERS_PK"].Descending); // 모두 오름차순 = 빈 목록
        Assert.Empty(index["ORDERS_PK"].Expressions);
        Assert.Equal([true, false], index["ORDERS_D"].Descending);
        Assert.Null(index["ORDERS_D"].ConstraintName);
        Assert.Equal(["UPPER(\"NOTE\")", null], index["ORDERS_F"].Expressions);
        Assert.Equal("TS_IDX", index["ORDERS_F"].Tablespace);

        Assert.Equal((TableKind.Heap, false, "TS_DATA"), (table.Kind, table.IsPartitioned, table.Tablespace));
    }

    [Theory]
    [InlineData("YES", "SESSION", null, "NO", 0, TableKind.Temporary, false)]
    [InlineData("YES", "TRANSACTION", null, "NO", 0, TableKind.Temporary, false)]
    [InlineData("NO", null, "IOT", "NO", 0, TableKind.Iot, false)]
    [InlineData("NO", null, null, "YES", 0, TableKind.Heap, true)]
    [InlineData("NO", null, "IOT", "YES", 0, TableKind.Iot, true)]
    [InlineData("NO", null, null, "NO", 1, TableKind.External, false)]
    public async Task Table_kind_comes_from_temporary_iot_external_and_partitioned(
        string temporary, string? duration, string? iot, string partitioned, int external, TableKind kind, bool isPartitioned)
    {
        var provider = new TiberoMetadataProvider(Recording([], ById(
            ("meta.columns", Table(ColumnHeader, ["ID", 1m, "NUMBER", null, null, null, "N", null, "NO", null, "NO", null, null, null, null])),
            ("meta.table_kind", Table(KindHeader, [null, temporary, duration, iot, partitioned, (decimal)external])))));
        await using var session = Session();

        var table = await provider.GetTableAsync(session, Orders, CancellationToken.None);

        Assert.Equal((kind, isPartitioned, (string?)null), (table.Kind, table.IsPartitioned, table.Tablespace));
    }

    /// <summary>파티션 테이블이면(table_kind partitioned = YES) 방식·파티션·LOCAL 인덱스를 더 읽는다(T14). 아니면 묻지 않는다.</summary>
    [Fact]
    public async Task Partitioned_tables_read_the_scheme_partitions_and_local_indexes()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = new TiberoMetadataProvider(Recording(calls, ById(
            ("meta.columns", Table(ColumnHeader, ["ID", 1m, "NUMBER", null, null, null, "N", null, "NO", null, "NO", null, null, null, null])),
            ("meta.table_kind", Table(KindHeader, [null, "NO", null, null, "YES", 0m])),
            ("meta.part_scheme", Table(["PARTITIONING_TYPE", "SUBPARTITIONING_TYPE", "INTERVAL", "LEVEL_KIND", "COLUMN_NAME", "COLUMN_POSITION"],
                ["RANGE", "NONE", "NUMTOYMINTERVAL(1, 'MONTH')", "PART", "ID", 1m])),
            ("meta.detail_partitions", Table(["PARTITION_POSITION", "PARTITION_NAME", "PARTITIONING_TYPE", "SUBPARTITIONING_TYPE", "HIGH_VALUE", "TABLESPACE_NAME", "NUM_ROWS", "LAST_ANALYZED"],
                [1m, "P1", "RANGE", "NONE", "100", "TS_DATA", 0m, null], [2m, "P2", "RANGE", "NONE", "MAXVALUE", "TS_DATA", 7m, new DateTime(2026, 11, 1)])),
            ("meta.part_indexes", Table(["INDEX_NAME"], ["ORDERS_LIX"])))));
        await using var session = Session();

        var table = await provider.GetTableAsync(session, Orders, CancellationToken.None);

        var p = table.Partitioning!;
        Assert.Equal(("RANGE", "NUMTOYMINTERVAL(1, 'MONTH')", (string?)null), (p.Method, p.Interval, p.SubpartitionMethod));
        Assert.Equal(["ID"], p.KeyColumns);
        Assert.Equal([("P1", "100", (long?)null), ("P2", "MAXVALUE", (long?)7)], p.Partitions.Select(x => (x.Name, x.Bound, x.NumRows)));
        Assert.Equal(["ORDERS_LIX"], p.PartitionedIndexes);
        Assert.Contains(calls, c => c.Id == "meta.part_scheme");

        var plainCalls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var plain = new TiberoMetadataProvider(Recording(plainCalls, ById(
            ("meta.columns", Table(ColumnHeader, ["ID", 1m, "NUMBER", null, null, null, "N", null, "NO", null, "NO", null, null, null, null])),
            ("meta.table_kind", Table(KindHeader, [null, "NO", null, null, "NO", 0m])))));
        Assert.Null((await plain.GetTableAsync(session, Orders, CancellationToken.None)).Partitioning);
        Assert.DoesNotContain(plainCalls, c => c.Id.StartsWith("meta.part_", StringComparison.Ordinal));
    }

    /// <summary>예전 행 모양(끝 열 없음)과 테이블 종류 0행(뷰)이면 TE-29 값은 모두 기본값이고, 나머지 읽기는 그대로다.</summary>
    [Fact]
    public async Task Old_row_shapes_and_a_view_leave_the_table_edit_metadata_at_its_defaults()
    {
        var provider = new TiberoMetadataProvider(Recording([], ById(
            ("meta.columns", Table(ColumnHeader[..13], ["NOTE", 1m, "VARCHAR", 10m, null, null, "Y", null, "NO", null, "NO", null, null])),
            ("meta.constraints", Table(ConstraintHeader[..9],
                ["V_FK", "R", "ENABLED", null, "SAMPLE", "ORDER_ITEMS", "NOTE", 1m, "ITEMS_PK"])),
            ("meta.indexes", Table(IndexHeader[..5], ["V_IX", "NONUNIQUE", "NORMAL", "NOTE", 1m])))));
        await using var session = Session();

        var table = await provider.GetTableAsync(session, Orders with { ObjectType = "VIEW" }, CancellationToken.None);

        Assert.Null(Assert.Single(table.Columns).CharSemantics);
        var fk = Assert.Single(table.Constraints);
        Assert.Equal((DeleteRule.NoAction, 0), (fk.DeleteRule, fk.ReferencedColumns.Count));
        var index = Assert.Single(table.Indexes);
        Assert.Equal((0, 0, (string?)null, (string?)null), (index.Descending.Count, index.Expressions.Count, index.Tablespace, index.ConstraintName));
        Assert.Equal((TableKind.Heap, false, (string?)null), (table.Kind, table.IsPartitioned, table.Tablespace));
        Assert.Equal(["NOTE"], index.Columns);
    }
}
