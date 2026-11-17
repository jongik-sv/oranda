using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Xunit;
using static Oranda.Core.Tests.Schema.TableEdit.Samples;

namespace Oranda.Core.Tests.Schema.TableEdit;

/// <summary>새 테이블(설계 7.5): CREATE 1개 + FK 뒤 ALTER + COMMENT + CREATE INDEX.</summary>
public sealed class NewTableScriptTests
{
    private static TableDraft NewItems()
    {
        var draft = TableDraftFactory.Empty("SAMPLE") with { Name = "ORDER_ITEMS", Comment = "주문 품목" };
        return draft
            .AddColumn(new ColumnDraft("c1", "ITEM_ID") { DataType = "NUMBER", Precision = 10, IsNullable = false })
            .AddColumn(new ColumnDraft("c2", "ORDER_ID") { DataType = "NUMBER", Precision = 10, IsNullable = false })
            .AddColumn(new ColumnDraft("c3", "NAME") { DataType = "VARCHAR2", Length = 100, Comment = "품목 이름" })
            .AddColumn(new ColumnDraft("c4", "PRICE") { DataType = "NUMBER", Precision = 12, Scale = 2, DefaultValue = "0" })
            .AddColumn(new ColumnDraft("c5", "PRICE_X2") { VirtualExpression = "PRICE * 2" })
            .AddConstraint(new ConstraintDraft("k1", "FK_ORDER_ITEMS_01", ConstraintKind.ForeignKey)
            {
                ColumnKeys = ["c2"], ReferencedTable = new DbObjectRef("SAMPLE", "ORDERS", "TABLE"), ReferencedColumns = ["ORDER_ID"],
                DeleteRule = DeleteRule.Cascade,
            })
            .AddConstraint(new ConstraintDraft("k2", "PK_ORDER_ITEMS", ConstraintKind.PrimaryKey) { ColumnKeys = ["c1"] })
            .AddConstraint(new ConstraintDraft("k3", "CK_ORDER_ITEMS_01", ConstraintKind.Check) { Condition = "PRICE >= 0" })
            .AddIndex(new IndexDraft("i1", "IX_ORDER_ITEMS_01") { Columns = [new IndexColumnDraft("c2"), new IndexColumnDraft("c3", Descending: true)] });
    }

    [Fact]
    public void New_table_is_one_create_then_fk_comments_and_indexes()
    {
        var texts = Build(TableDraftFactory.Empty("SAMPLE"), NewItems());

        Assert.Equal(
            [
                "CREATE TABLE SAMPLE.ORDER_ITEMS (ITEM_ID NUMBER(10) NOT NULL, ORDER_ID NUMBER(10) NOT NULL, NAME VARCHAR2(100),"
                + " PRICE NUMBER(12,2) DEFAULT 0, PRICE_X2 AS (PRICE * 2) VIRTUAL,"
                + " CONSTRAINT PK_ORDER_ITEMS PRIMARY KEY (ITEM_ID), CONSTRAINT CK_ORDER_ITEMS_01 CHECK (PRICE >= 0))",
                "ALTER TABLE SAMPLE.ORDER_ITEMS ADD CONSTRAINT FK_ORDER_ITEMS_01 FOREIGN KEY (ORDER_ID) REFERENCES SAMPLE.ORDERS (ORDER_ID) ON DELETE CASCADE",
                "COMMENT ON TABLE SAMPLE.ORDER_ITEMS IS '주문 품목'",
                "COMMENT ON COLUMN SAMPLE.ORDER_ITEMS.NAME IS '품목 이름'",
                "CREATE INDEX SAMPLE.IX_ORDER_ITEMS_01 ON SAMPLE.ORDER_ITEMS (ORDER_ID, NAME DESC)",
            ],
            texts);
    }

    [Fact]
    public void New_table_create_has_no_change_target_and_the_page_of_each_part()
    {
        var script = TableEditScriptBuilder.Build(TableDraftFactory.Empty("SAMPLE"), NewItems(), Dialect);

        Assert.Null(script.Statements[0].Statement.ChangeTarget);
        Assert.Equal("CREATE", script.Statements[0].Statement.Verb);
        Assert.Equal(
            [TableEditPage.Columns, TableEditPage.Constraints, TableEditPage.Description, TableEditPage.Columns, TableEditPage.Indexes],
            script.Statements.Select(s => s.Page));
        Assert.Equal(TableDraft.TableRowKey, script.Statements[0].RowKey);
        Assert.Equal("k1", script.Statements[1].RowKey);
    }

    [Fact]
    public void New_table_skips_dropped_rows_and_unnamed_constraints_get_no_name_clause()
    {
        var draft = NewItems()
            .WithColumn("PRICE_X2", c => c with { IsDropped = true })
            .WithConstraint("CK_ORDER_ITEMS_01", c => c with { Name = string.Empty, IsEnabled = false })
            .WithIndex("IX_ORDER_ITEMS_01", i => i with { IsDropped = true });

        var texts = Build(TableDraftFactory.Empty("SAMPLE"), draft);

        Assert.Equal(
            "CREATE TABLE SAMPLE.ORDER_ITEMS (ITEM_ID NUMBER(10) NOT NULL, ORDER_ID NUMBER(10) NOT NULL, NAME VARCHAR2(100), PRICE NUMBER(12,2) DEFAULT 0,"
            + " CONSTRAINT PK_ORDER_ITEMS PRIMARY KEY (ITEM_ID), CHECK (PRICE >= 0) DISABLE)",
            texts[0]);
        Assert.DoesNotContain(texts, t => t.StartsWith("CREATE INDEX", StringComparison.Ordinal));
    }

    [Fact]
    public void New_table_with_a_tablespace_states_it()
    {
        var draft = NewItems() with { Tablespace = "USERS" };

        Assert.EndsWith(") TABLESPACE USERS", Build(TableDraftFactory.Empty("SAMPLE"), draft)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void New_table_without_columns_produces_no_statements()
    {
        var draft = TableDraftFactory.Empty("SAMPLE") with { Name = "T1" };

        Assert.Empty(Build(TableDraftFactory.Empty("SAMPLE"), draft));
    }

    [Fact]
    public void New_table_diff_marks_every_row_added()
    {
        var changes = TableDraftDiff.Compute(TableDraftFactory.Empty("SAMPLE"), NewItems(), Dialect);

        Assert.All(changes.Columns, d => Assert.Equal(DraftRowState.Added, d.State));
        Assert.Equal((5, 3, 1), (changes.CountFor(TableEditPage.Columns), changes.CountFor(TableEditPage.Constraints), changes.CountFor(TableEditPage.Indexes)));
        Assert.False(changes.Renamed);
    }
}
