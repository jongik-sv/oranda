using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Xunit;
using static Oranda.Core.Tests.Schema.TableEdit.Samples;

namespace Oranda.Core.Tests.Schema.TableEdit;

/// <summary>편집 모델 만들기(설계 7.1·7.3·7.5, TE-30).</summary>
public sealed class TableDraftFactoryTests
{
    [Fact]
    public void From_table_moves_the_virtual_expression_out_of_the_default()
    {
        var draft = TableDraftFactory.FromTable(Orders(), Dialect);

        var total = draft.Columns.Single(c => c.Name == "TOTAL");
        Assert.Equal("\"AMOUNT\"*\"QTY\"", total.VirtualExpression);
        Assert.Null(total.DefaultValue);
        Assert.True(total.IsVirtual);
        Assert.Equal("'N'\n", draft.Columns.Single(c => c.Name == "STATUS").DefaultValue);
    }

    [Fact]
    public void From_table_keeps_order_positions_and_unique_row_keys()
    {
        var draft = TableDraftFactory.FromTable(Orders(), Dialect);

        Assert.Equal(Enumerable.Range(1, 9).Select(i => (int?)i), draft.Columns.Select(c => c.OriginalPosition));
        var keys = draft.Columns.Select(c => c.RowKey).Concat(draft.Constraints.Select(c => c.RowKey)).Concat(draft.Indexes.Select(i => i.RowKey)).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(("SAMPLE", "ORDERS", "주문", false), (draft.Owner, draft.Name, draft.Comment, draft.IsNewTable));
        Assert.Equal("USERS", draft.Tablespace);
    }

    [Fact]
    public void From_table_hides_server_named_not_null_checks()
    {
        var draft = TableDraftFactory.FromTable(Orders(), Dialect);

        Assert.Equal(["PK_ORDERS", "CK_ORDERS_01", "FK_ORDERS_01", "SYS_C0013"], draft.Constraints.Select(c => c.Name));
    }

    [Fact]
    public void From_table_points_constraint_columns_at_row_keys()
    {
        var draft = TableDraftFactory.FromTable(Orders(), Dialect);

        var uk = draft.Constraints.Single(c => c.Name == "SYS_C0013");
        Assert.Equal([draft.KeyOf("CUSTOMER_ID"), draft.KeyOf("CREATED")], uk.ColumnKeys);
        var fk = draft.Constraints.Single(c => c.Name == "FK_ORDERS_01");
        Assert.Equal(("CUSTOMERS", "PK_CUSTOMERS"), (fk.ReferencedTable!.Name, fk.ReferencedConstraint));
        Assert.Equal(["ID"], fk.ReferencedColumns);
    }

    [Fact]
    public void From_table_reads_descending_columns_and_function_expressions()
    {
        var draft = TableDraftFactory.FromTable(Orders(), Dialect);

        var desc = draft.Indexes.Single(i => i.Name == "IX_ORDERS_01");
        Assert.Equal(IndexKind.Normal, desc.Kind);
        Assert.Equal([new IndexColumnDraft(draft.KeyOf("CUSTOMER_NAME")), new IndexColumnDraft(draft.KeyOf("CREATED"), Descending: true)], desc.Columns);
        var function = draft.Indexes.Single(i => i.Name == "IX_ORDERS_02");
        Assert.Equal(IndexKind.FunctionBased, function.Kind);
        Assert.Equal("UPPER(\"NOTE\")", Assert.Single(function.Columns).Expression);
        Assert.Null(function.Columns[0].ColumnKey);
        Assert.Equal("PK_ORDERS", draft.Indexes.Single(i => i.Name == "PK_ORDERS").ConstraintName);
    }

    [Fact]
    public void From_table_matches_a_constraint_index_by_name_when_the_provider_did_not_say()
    {
        var table = Orders() with { Indexes = [new IndexInfo("PK_ORDERS", true, ["ORDER_ID"])] };

        var draft = TableDraftFactory.FromTable(table, Dialect);

        Assert.Equal("PK_ORDERS", Assert.Single(draft.Indexes).ConstraintName);
    }

    [Fact]
    public void From_table_maps_index_types()
    {
        var table = Orders() with
        {
            Indexes =
            [
                new IndexInfo("IX_B", false, ["STATUS"]) { IndexType = "BITMAP" },
                new IndexInfo("IX_R", false, ["STATUS"]) { IndexType = "NORMAL/REV" },
                new IndexInfo("IX_D", false, ["NOTE"]) { IndexType = "DOMAIN" },
            ],
        };

        var draft = TableDraftFactory.FromTable(table, Dialect);

        Assert.Equal([IndexKind.Bitmap, IndexKind.Other, IndexKind.Other], draft.Indexes.Select(i => i.Kind));
    }

    [Fact]
    public void Identity_column_has_no_default_in_the_draft()
    {
        var table = Orders() with
        {
            Columns = [new ColumnInfo("ID", 1, "NUMBER") { IsIdentity = true, DefaultValue = "\"SAMPLE\".\"ISEQ$$_7\".nextval", IsNullable = false }],
            Constraints = [],
            Indexes = [],
        };

        var column = Assert.Single(TableDraftFactory.FromTable(table, Dialect).Columns);

        Assert.Null(column.DefaultValue);
        Assert.Null(column.Identity); // 속성을 못 읽었으면 짐작하지 않는다
        Assert.True(column.IsIdentityUnknown);
    }

    [Fact]
    public void Empty_is_a_new_table_without_rows()
    {
        var draft = TableDraftFactory.Empty("SAMPLE");

        Assert.True(draft.IsNewTable);
        Assert.Equal(("SAMPLE", string.Empty), (draft.Owner, draft.Name));
        Assert.Empty(draft.Columns);
    }

    [Fact]
    public void Copy_structure_renames_constraints_and_indexes_by_rule_and_skips_unread_identity()
    {
        var table = Orders() with
        {
            Columns = [.. Orders().Columns, new ColumnInfo("SEQ", 10, "NUMBER") { IsIdentity = true, DefaultValue = "\"SAMPLE\".\"ISEQ$$_7\".nextval" }],
        };

        var copy = TableDraftFactory.CopyStructure(table, "SAMPLE", "ORDERS_BAK", Dialect, 30);

        Assert.True(copy.IsNewTable);
        Assert.Equal(("SAMPLE", "ORDERS_BAK", "주문"), (copy.Owner, copy.Name, copy.Comment));
        Assert.Null(copy.Tablespace);
        Assert.Null(copy.Columns.Single(c => c.Name == "SEQ").Identity);
        Assert.False(copy.Columns.Single(c => c.Name == "SEQ").IsIdentityUnknown);
        Assert.Null(copy.Columns.Single(c => c.Name == "SEQ").DefaultValue);
        Assert.Equal(["PK_ORDERS_BAK", "CK_ORDERS_BAK_01", "FK_ORDERS_BAK_01", "UK_ORDERS_BAK_01"], copy.Constraints.Select(c => c.Name));
        Assert.All(copy.Constraints, c => Assert.True(c.IsAutoName));
        // 제약 인덱스는 제약이 만들므로 옮기지 않는다.
        Assert.Equal(["IX_ORDERS_BAK_01", "IX_ORDERS_BAK_02"], copy.Indexes.Select(i => i.Name));
        Assert.All(copy.Indexes, i => Assert.Null(i.Tablespace));
        Assert.Equal("\"AMOUNT\"*\"QTY\"", copy.Columns.Single(c => c.Name == "TOTAL").VirtualExpression);
        Assert.All(copy.Columns, c => Assert.Null(c.OriginalPosition));
    }

    [Fact]
    public void Copy_structure_uses_fresh_row_keys_that_still_match_up()
    {
        var original = TableDraftFactory.FromTable(Orders(), Dialect);

        var copy = TableDraftFactory.CopyStructure(Orders(), "SAMPLE", "ORDERS_BAK", Dialect, 30);

        Assert.Empty(copy.Columns.Select(c => c.RowKey).Intersect(original.Columns.Select(c => c.RowKey)));
        var uk = copy.Constraints.Single(c => c.Kind == ConstraintKind.Unique);
        Assert.Equal(["CUSTOMER_ID", "CREATED"], uk.ColumnKeys.Select(k => copy.Column(k)!.Name));
    }

    [Fact]
    public void Copy_structure_builds_one_create_table_and_its_followers()
    {
        var copy = TableDraftFactory.CopyStructure(Orders(), "SAMPLE", "ORDERS_BAK", Dialect, 30);

        var texts = Build(TableDraftFactory.Empty("SAMPLE"), copy);

        Assert.Equal(
            "CREATE TABLE SAMPLE.ORDERS_BAK (ORDER_ID NUMBER(10) NOT NULL, CUSTOMER_ID NUMBER(10), CUSTOMER_NAME VARCHAR2(50 CHAR),"
            + " STATUS CHAR(1 BYTE) DEFAULT 'N', AMOUNT NUMBER(12,2), QTY NUMBER(*,0), CREATED DATE DEFAULT SYSDATE NOT NULL,"
            + " TOTAL NUMBER AS (\"AMOUNT\"*\"QTY\") VIRTUAL, NOTE VARCHAR2(200 BYTE),"
            + " CONSTRAINT PK_ORDERS_BAK PRIMARY KEY (ORDER_ID), CONSTRAINT UK_ORDERS_BAK_01 UNIQUE (CUSTOMER_ID, CREATED),"
            + " CONSTRAINT CK_ORDERS_BAK_01 CHECK (status in ('N','Y')))",
            texts[0]);
        Assert.Equal(
            [
                "ALTER TABLE SAMPLE.ORDERS_BAK ADD CONSTRAINT FK_ORDERS_BAK_01 FOREIGN KEY (CUSTOMER_ID) REFERENCES SAMPLE.CUSTOMERS (ID)",
                "COMMENT ON TABLE SAMPLE.ORDERS_BAK IS '주문'",
                "COMMENT ON COLUMN SAMPLE.ORDERS_BAK.NOTE IS '메모'",
                "CREATE INDEX SAMPLE.IX_ORDERS_BAK_01 ON SAMPLE.ORDERS_BAK (CUSTOMER_NAME, CREATED DESC)",
                "CREATE INDEX SAMPLE.IX_ORDERS_BAK_02 ON SAMPLE.ORDERS_BAK (UPPER(\"NOTE\"))",
            ],
            texts.Skip(1));
    }

    [Fact]
    public void Names_from_input_follow_the_unquoted_upper_rule()
    {
        Assert.Equal("ORDER_ID", TableEditNames.FromInput(" order_id "));
        Assert.Equal("Mixed", TableEditNames.FromInput("\"Mixed\""));
        Assert.Equal("a\"b", TableEditNames.FromInput("\"a\"\"b\""));
        Assert.Equal(string.Empty, TableEditNames.FromInput("  "));
    }
}
