using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Xunit;
using static Oranda.Core.Tests.Schema.TableEdit.Samples;

namespace Oranda.Core.Tests.Schema.TableEdit;

/// <summary>중간 실패 뒤 남은 편집만 문장이 되는가(TE-5, Review Focus 3).</summary>
public sealed class TableDraftRebaseTests
{
    private const string T = "ALTER TABLE SAMPLE.ORDERS ";

    /// <summary>원본·편집에서 앞 <paramref name="applied"/> 문장이 성공했고 서버가 <paramref name="server"/> 로 바뀐 뒤 남은 문장.</summary>
    private static IReadOnlyList<string> Remaining(TableDraft original, TableDraft edited, int applied, TableInfo server)
    {
        var script = TableEditScriptBuilder.Build(original, edited, Dialect);
        var reread = TableDraftFactory.FromTable(server, Dialect);
        return Build(reread, TableDraftRebase.Rebase(original, edited, script, applied, reread));
    }

    /// <summary>NOTE→MEMO, AMOUNT(14,2), CODE 추가, CODE 에 UK.</summary>
    private static TableDraft Edit(TableDraft d) => d
        .WithColumn("NOTE", c => c with { Name = "MEMO" })
        .WithColumn("AMOUNT", c => c with { Precision = 14 })
        .AddColumn(new ColumnDraft("ncode", "CODE") { DataType = "VARCHAR2", Length = 10 })
        .AddConstraint(new ConstraintDraft("nuk", "UK_ORDERS_01", ConstraintKind.Unique) { ColumnKeys = ["ncode"] });

    /// <summary>앞 <paramref name="applied"/> 문장만 반영된 서버 테이블(서버 표기).</summary>
    private static TableInfo Server(int applied)
    {
        var t = Orders();
        var columns = t.Columns.ToList();
        var constraints = t.Constraints.ToList();
        if (applied >= 1)
        {
            columns[8] = columns[8] with { Name = "MEMO" };
        }

        if (applied >= 2)
        {
            columns[4] = columns[4] with { Precision = 14 };
        }

        if (applied >= 3)
        {
            columns.Add(new ColumnInfo("CODE", 10, "VARCHAR2") { Length = 10, CharSemantics = CharSemantics.Bytes });
        }

        if (applied >= 4)
        {
            constraints.Add(new ConstraintInfo("UK_ORDERS_01", ConstraintKind.Unique, ["CODE"]));
        }

        return t with { Columns = columns, Constraints = constraints };
    }

    private static IReadOnlyList<string> Remaining(int applied)
    {
        var (original, edited) = Start();
        return Remaining(original, Edit(edited), applied, Server(applied));
    }

    [Fact]
    public void Nothing_applied_leaves_the_full_edit()
    {
        Assert.Equal(
            [
                T + "RENAME COLUMN NOTE TO MEMO",
                T + "MODIFY (AMOUNT NUMBER(14,2))",
                T + "ADD (CODE VARCHAR2(10))",
                T + "ADD CONSTRAINT UK_ORDERS_01 UNIQUE (CODE)",
            ],
            Remaining(0));
    }

    [Fact]
    public void Partial_failure_recomputes_remaining()
    {
        Assert.Equal([T + "MODIFY (AMOUNT NUMBER(14,2))", T + "ADD (CODE VARCHAR2(10))", T + "ADD CONSTRAINT UK_ORDERS_01 UNIQUE (CODE)"], Remaining(1));
        Assert.Equal([T + "ADD CONSTRAINT UK_ORDERS_01 UNIQUE (CODE)"], Remaining(3));
        Assert.Empty(Remaining(4));
    }

    [Fact]
    public void Rename_onto_a_dropped_name_with_nothing_applied_keeps_both_rows_apart()
    {
        // 리뷰 지적: 이름으로만 맞추면 Y(→NOTE) 가 서버의 NOTE 를 가로채 Y 를 지우는 문장이 나왔다.
        var (original, edited) = Start();
        edited = edited
            .WithColumn("NOTE", c => c with { IsDropped = true })
            .WithColumn("CUSTOMER_NAME", c => c with { Name = "NOTE", Length = 60 });

        Assert.Equal(
            [T + "DROP COLUMN NOTE", T + "RENAME COLUMN CUSTOMER_NAME TO NOTE", T + "MODIFY (NOTE VARCHAR2(60 CHAR))"],
            Remaining(original, edited, 0, Orders()));
    }

    [Fact]
    public void Swap_with_only_the_temporary_rename_applied()
    {
        var (original, edited) = Start();
        var (note, name) = (edited.KeyOf("NOTE"), edited.KeyOf("CUSTOMER_NAME"));
        edited = edited.WithColumnKey(note, c => c with { Name = "CUSTOMER_NAME" }).WithColumnKey(name, c => c with { Name = "NOTE" });
        // 서버는 열 이름을 바꾸면 인덱스 열 이름도 따라 바꾼다.
        var server = Orders() with
        {
            Columns = [.. Orders().Columns.Select(c => c.Name == "CUSTOMER_NAME" ? c with { Name = "CUSTOMER_NAME$1" } : c)],
            Indexes = [.. Orders().Indexes.Select(i => i with { Columns = [.. i.Columns.Select(c => c == "CUSTOMER_NAME" ? "CUSTOMER_NAME$1" : c)] })],
        };

        Assert.Equal([T + "RENAME COLUMN NOTE TO CUSTOMER_NAME", T + "RENAME COLUMN CUSTOMER_NAME$1 TO NOTE"], Remaining(original, edited, 1, server));
    }

    [Fact]
    public void Applied_drop_is_not_repeated_and_pending_drop_stays()
    {
        var (original, edited) = Start();
        edited = edited.WithColumn("STATUS", c => c with { IsDropped = true }).WithColumn("NOTE", c => c with { IsDropped = true });
        // 문장: DROP CONSTRAINT CK_ORDERS_01(STATUS 를 씀) → DROP COLUMN STATUS → DROP COLUMN NOTE. 앞 둘이 성공.
        var server = Orders() with
        {
            Columns = [.. Orders().Columns.Where(c => c.Name != "STATUS")],
            Constraints = [.. Orders().Constraints.Where(c => c.Name != "CK_ORDERS_01")],
        };

        Assert.Equal([T + "DROP COLUMN NOTE"], Remaining(original, edited, 2, server));
    }

    [Fact]
    public void Recreated_constraint_whose_drop_ran_is_added_again()
    {
        var (original, edited) = Start();
        edited = edited.WithConstraint("CK_ORDERS_01", c => c with { Condition = "STATUS IN ('N','Y','C')" });
        var server = Orders() with { Constraints = [.. Orders().Constraints.Where(c => c.Name != "CK_ORDERS_01")] };

        Assert.Equal([T + "ADD CONSTRAINT CK_ORDERS_01 CHECK (STATUS IN ('N','Y','C'))"], Remaining(original, edited, 1, server));
    }

    [Fact]
    public void Server_notation_of_an_applied_virtual_column_is_not_a_change()
    {
        var (original, edited) = Start();
        edited = edited.AddColumn(new ColumnDraft("ntax", "TAX") { VirtualExpression = "amount * 0.1" }).WithColumn("NOTE", c => c with { Comment = "비고" });
        var server = Orders() with
        {
            Columns = [.. Orders().Columns, new ColumnInfo("TAX", 10, "NUMBER") { IsVirtual = true, DefaultValue = "\"AMOUNT\"*0.1" }],
        };

        Assert.Equal(["COMMENT ON COLUMN SAMPLE.ORDERS.NOTE IS '비고'"], Remaining(original, edited, 1, server));
    }

    [Fact]
    public void New_table_after_create_keeps_only_the_later_statements()
    {
        var empty = TableDraftFactory.Empty("SAMPLE");
        var draft = (empty with { Name = "ORDER_ITEMS" })
            .AddColumn(new ColumnDraft("ncol1", "ID") { DataType = "NUMBER", IsNullable = false })
            .AddConstraint(new ConstraintDraft("npk", "PK_ORDER_ITEMS", ConstraintKind.PrimaryKey) { ColumnKeys = ["ncol1"] })
            .AddIndex(new IndexDraft("nix", "IX_ORDER_ITEMS_01") { Columns = [new IndexColumnDraft("ncol1", true)] });
        var server = new TableInfo(new DbObjectRef("SAMPLE", "ORDER_ITEMS", "TABLE"), [new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }])
        {
            Constraints = [new ConstraintInfo("PK_ORDER_ITEMS", ConstraintKind.PrimaryKey, ["ID"])],
            Indexes = [new IndexInfo("PK_ORDER_ITEMS", true, ["ID"]) { ConstraintName = "PK_ORDER_ITEMS" }],
        };

        Assert.Equal(["CREATE INDEX SAMPLE.IX_ORDER_ITEMS_01 ON SAMPLE.ORDER_ITEMS (ID DESC)"], Remaining(empty, draft, 1, server));
    }
}
