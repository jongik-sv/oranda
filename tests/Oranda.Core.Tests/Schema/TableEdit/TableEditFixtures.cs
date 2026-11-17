using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.Core.Tests.Schema.TableEdit;

/// <summary>시험용 방언: 기본 멤버는 Core 기본(Oracle 규칙) 그대로, 지원 여부만 고친다.</summary>
internal sealed class FakeTableEditDialect : ITableEditDialect
{
    public Dbms Dbms { get; init; } = Dbms.Oracle;

    public IReadOnlyList<string> TypeNames { get; } = ["VARCHAR2", "NUMBER", "DATE"];

    public int MaxBytes { get; init; } = 30;

    public bool SupportsVirtualColumnModify { get; init; } = true;

    public bool PurgesOnDrop { get; init; }

    public Dictionary<TableEditFeature, string> Unsupported { get; } = [];

    public int MaxIdentifierBytes(ServerInfo? server) => MaxBytes;

    public string? UnsupportedReasonKey(TableEditFeature feature, ServerInfo? server) => Unsupported.GetValueOrDefault(feature);
}

/// <summary>예시 테이블(SAMPLE.ORDERS·SAMPLE.ORDER_ITEMS). 서버가 돌려주는 표기를 흉내 낸다(따옴표 붙인 가상 식, 줄바꿈 붙은 기본값 등).</summary>
internal static class Samples
{
    public static readonly FakeTableEditDialect Dialect = new();

    public static DbObjectRef OrdersRef { get; } = new("SAMPLE", "ORDERS", "TABLE");

    public static TableInfo Orders() => new(OrdersRef,
    [
        new ColumnInfo("ORDER_ID", 1, "NUMBER") { Precision = 10, Scale = 0, IsNullable = false },
        new ColumnInfo("CUSTOMER_ID", 2, "NUMBER") { Precision = 10, Scale = 0 },
        new ColumnInfo("CUSTOMER_NAME", 3, "VARCHAR2") { Length = 50, ByteLength = 200, CharSemantics = CharSemantics.Characters },
        new ColumnInfo("STATUS", 4, "CHAR") { Length = 1, ByteLength = 1, CharSemantics = CharSemantics.Bytes, DefaultValue = "'N'\n" },
        new ColumnInfo("AMOUNT", 5, "NUMBER") { Precision = 12, Scale = 2 },
        new ColumnInfo("QTY", 6, "NUMBER") { Scale = 0 },
        new ColumnInfo("CREATED", 7, "DATE") { DefaultValue = "SYSDATE ", IsNullable = false },
        new ColumnInfo("TOTAL", 8, "NUMBER") { IsVirtual = true, DefaultValue = "\"AMOUNT\"*\"QTY\"" },
        new ColumnInfo("NOTE", 9, "VARCHAR2") { Length = 200, ByteLength = 200, CharSemantics = CharSemantics.Bytes, Comment = "메모" },
    ])
    {
        Comment = "주문",
        Constraints =
        [
            new ConstraintInfo("PK_ORDERS", ConstraintKind.PrimaryKey, ["ORDER_ID"]),
            new ConstraintInfo("SYS_C0011", ConstraintKind.Check, ["ORDER_ID"]) { Condition = "\"ORDER_ID\" IS NOT NULL" },
            new ConstraintInfo("SYS_C0012", ConstraintKind.Check, ["CREATED"]) { Condition = "\"CREATED\" IS NOT NULL" },
            new ConstraintInfo("CK_ORDERS_01", ConstraintKind.Check, ["STATUS"]) { Condition = "status in ('N','Y')" },
            new ConstraintInfo("FK_ORDERS_01", ConstraintKind.ForeignKey, ["CUSTOMER_ID"])
            {
                ReferencedTable = new DbObjectRef("SAMPLE", "CUSTOMERS", "TABLE"),
                ReferencedConstraint = "PK_CUSTOMERS",
                ReferencedColumns = ["ID"],
            },
            new ConstraintInfo("SYS_C0013", ConstraintKind.Unique, ["CUSTOMER_ID", "CREATED"]),
        ],
        Indexes =
        [
            new IndexInfo("PK_ORDERS", true, ["ORDER_ID"]) { IndexType = "NORMAL", ConstraintName = "PK_ORDERS", Tablespace = "USERS" },
            new IndexInfo("SYS_C0013", true, ["CUSTOMER_ID", "CREATED"]) { IndexType = "NORMAL", ConstraintName = "SYS_C0013" },
            // Oracle 은 DESC 인덱스를 함수 기반으로 보이고 열 자리에 SYS_NC…$ 와 식 "CREATED" 를 준다.
            new IndexInfo("IX_ORDERS_01", false, ["CUSTOMER_NAME", "SYS_NC00010$"])
            {
                IndexType = "FUNCTION-BASED NORMAL", Descending = [false, true], Expressions = [null, "\"CREATED\""], Tablespace = "USERS",
            },
            new IndexInfo("IX_ORDERS_02", false, ["SYS_NC00011$"]) { IndexType = "FUNCTION-BASED NORMAL", Expressions = ["UPPER(\"NOTE\")"] },
        ],
        Tablespace = "USERS",
    };

    /// <summary>원본과 편집 모델(같은 값)으로 시작.</summary>
    public static (TableDraft Original, TableDraft Edited) Start(TableInfo? table = null, ITableEditDialect? dialect = null)
    {
        var draft = TableDraftFactory.FromTable(table ?? Orders(), dialect ?? Dialect);
        return (draft, draft);
    }

    public static string KeyOf(this TableDraft d, string column) => d.Columns.Single(c => c.Name == column).RowKey;

    public static TableDraft WithColumn(this TableDraft d, string column, Func<ColumnDraft, ColumnDraft> change) =>
        d with { Columns = [.. d.Columns.Select(c => c.Name == column ? change(c) : c)] };

    public static TableDraft WithColumnKey(this TableDraft d, string rowKey, Func<ColumnDraft, ColumnDraft> change) =>
        d with { Columns = [.. d.Columns.Select(c => c.RowKey == rowKey ? change(c) : c)] };

    public static TableDraft WithConstraint(this TableDraft d, string name, Func<ConstraintDraft, ConstraintDraft> change) =>
        d with { Constraints = [.. d.Constraints.Select(c => c.Name == name ? change(c) : c)] };

    public static TableDraft WithIndex(this TableDraft d, string name, Func<IndexDraft, IndexDraft> change) =>
        d with { Indexes = [.. d.Indexes.Select(i => i.Name == name ? change(i) : i)] };

    public static TableDraft AddColumn(this TableDraft d, ColumnDraft column) => d with { Columns = [.. d.Columns, column] };

    public static TableDraft AddConstraint(this TableDraft d, ConstraintDraft c) => d with { Constraints = [.. d.Constraints, c] };

    public static TableDraft AddIndex(this TableDraft d, IndexDraft i) => d with { Indexes = [.. d.Indexes, i] };

    public static IReadOnlyList<string> Texts(this TableEditScript script) => [.. script.Statements.Select(s => s.Statement.Text)];

    public static IReadOnlyList<string> Build(TableDraft original, TableDraft edited, ITableEditDialect? dialect = null) =>
        TableEditScriptBuilder.Build(original, edited, dialect ?? Dialect).Texts();
}
