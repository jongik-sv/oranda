using Oranda.Core.Execution;
using Oranda.Core.Journal;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Xunit;
using static Oranda.Core.Tests.Schema.TableEdit.Samples;

namespace Oranda.Core.Tests.Schema.TableEdit;

/// <summary>차이 → 문장(설계 8.2 표 모든 줄, 8.3 순서, TE-11).</summary>
public sealed class TableEditScriptBuilderTests
{
    private const string T = "ALTER TABLE SAMPLE.ORDERS ";

    [Fact]
    public void No_edit_produces_no_statements_for_the_sample_table()
    {
        var (original, edited) = Start();

        var script = TableEditScriptBuilder.Build(original, edited, Dialect);

        Assert.True(script.IsEmpty);
        Assert.Equal(string.Empty, script.PreviewText());
    }

    [Fact]
    public void No_edit_produces_no_statements_when_the_server_reread_differs_only_in_notation()
    {
        // 사용자가 쓴 표기(편집 모델)와 서버가 돌려준 표기(다시 읽은 원본)가 다를 뿐인 경우(TE-5 다시 계산, 설계 8.4).
        var (original, edited) = Start();
        edited = edited
            .WithColumn("TOTAL", c => c with { VirtualExpression = "amount * qty" })
            .WithColumn("STATUS", c => c with { DefaultValue = "'N'" })
            .WithColumn("CREATED", c => c with { DefaultValue = "sysdate" })
            .WithColumn("QTY", c => c with { DataType = "number", Precision = null, Scale = 0 })
            .WithColumn("NOTE", c => c with { DataType = "varchar2" })
            .WithConstraint("CK_ORDERS_01", c => c with { Condition = "STATUS IN ('N', 'Y')" });

        Assert.Empty(Build(original, edited));
    }

    [Fact]
    public void Edit_then_revert_produces_no_statements()
    {
        var (original, edited) = Start();
        var changed = edited.WithColumn("NOTE", c => c with { Length = 400, Comment = "x" });
        Assert.NotEmpty(Build(original, changed));

        var reverted = changed.WithColumn("NOTE", c => c with { Length = 200, Comment = "메모" });

        Assert.Empty(Build(original, reverted));
    }

    [Fact]
    public void Add_column_with_default_and_not_null()
    {
        var (original, edited) = Start();
        edited = edited.AddColumn(new ColumnDraft("n1", "DISCOUNT") { DataType = "NUMBER", Precision = 5, Scale = 2, DefaultValue = "0", IsNullable = false });

        Assert.Equal([T + "ADD (DISCOUNT NUMBER(5,2) DEFAULT 0 NOT NULL)"], Build(original, edited));
    }

    [Fact]
    public void New_character_column_does_not_state_length_semantics()
    {
        // TE-6: 새 열은 길이 기준을 쓰지 않아 세션 기본값을 따른다.
        var (original, edited) = Start();
        edited = edited.AddColumn(new ColumnDraft("n1", "CODE") { DataType = "VARCHAR2", Length = 20 });

        Assert.Equal([T + "ADD (CODE VARCHAR2(20))"], Build(original, edited));
    }

    [Fact]
    public void Add_virtual_column_with_and_without_type()
    {
        var (original, edited) = Start();
        edited = edited
            .AddColumn(new ColumnDraft("n1", "TAX") { VirtualExpression = "AMOUNT * 0.1" })
            .AddColumn(new ColumnDraft("n2", "GROSS") { DataType = "NUMBER", VirtualExpression = "AMOUNT + 1" });

        Assert.Equal([T + "ADD (TAX AS (AMOUNT * 0.1) VIRTUAL)", T + "ADD (GROSS NUMBER AS (AMOUNT + 1) VIRTUAL)"], Build(original, edited));
    }

    [Fact]
    public void Drop_column_is_marked_irreversible_with_data_loss_target()
    {
        var (original, edited) = Start();
        edited = edited.WithColumn("NOTE", c => c with { IsDropped = true });

        var script = TableEditScriptBuilder.Build(original, edited, Dialect);

        var s = Assert.Single(script.Statements);
        Assert.Equal(T + "DROP COLUMN NOTE", s.Statement.Text);
        Assert.Equal(TableEditMarks.Irreversible, s.Marks);
        Assert.Equal(original.KeyOf("NOTE"), s.RowKey);
        Assert.Equal(TableEditPage.Columns, s.Page);
        Assert.Equal(DataLossKind.DropColumns, s.Statement.ChangeTarget!.DataLoss);
        Assert.Equal(["NOTE"], s.Statement.ChangeTarget.DroppedColumns);
    }

    [Fact]
    public void Removing_the_row_from_the_list_also_drops_the_column()
    {
        var (original, edited) = Start();
        edited = edited with { Columns = [.. edited.Columns.Where(c => c.Name != "NOTE")] };

        Assert.Equal([T + "DROP COLUMN NOTE"], Build(original, edited));
    }

    [Fact]
    public void Rename_column()
    {
        var (original, edited) = Start();
        edited = edited.WithColumn("NOTE", c => c with { Name = "MEMO" });

        Assert.Equal([T + "RENAME COLUMN NOTE TO MEMO"], Build(original, edited));
    }

    [Fact]
    public void Modify_type_keeps_the_original_length_semantics()
    {
        var (original, edited) = Start();
        edited = edited
            .WithColumn("CUSTOMER_NAME", c => c with { Length = 100 })
            .WithColumn("NOTE", c => c with { Length = 300, CharSemantics = null });

        Assert.Equal(
            [T + "MODIFY (CUSTOMER_NAME VARCHAR2(100 CHAR))", T + "MODIFY (NOTE VARCHAR2(300 BYTE))"],
            Build(original, edited));
    }

    [Fact]
    public void Modify_number_precision_and_type()
    {
        var (original, edited) = Start();
        edited = edited
            .WithColumn("AMOUNT", c => c with { Precision = 14, Scale = 2 })
            .WithColumn("QTY", c => c with { Precision = 8 });

        Assert.Equal([T + "MODIFY (AMOUNT NUMBER(14,2))", T + "MODIFY (QTY NUMBER(8))"], Build(original, edited));
    }

    [Fact]
    public void Modify_nullable_both_ways()
    {
        var (original, edited) = Start();
        edited = edited
            .WithColumn("CREATED", c => c with { IsNullable = true })
            .WithColumn("NOTE", c => c with { IsNullable = false });

        Assert.Equal([T + "MODIFY (CREATED NULL)", T + "MODIFY (NOTE NOT NULL)"], Build(original, edited));
    }

    [Fact]
    public void Modify_default_and_remove_default()
    {
        var (original, edited) = Start();
        edited = edited
            .WithColumn("NOTE", c => c with { DefaultValue = "'-'" })
            .WithColumn("STATUS", c => c with { DefaultValue = "  " });

        Assert.Equal([T + "MODIFY (STATUS DEFAULT NULL)", T + "MODIFY (NOTE DEFAULT '-')"], Build(original, edited));
    }

    [Fact]
    public void Several_attributes_of_one_column_make_one_modify()
    {
        // TE-11: 한 열 = MODIFY 하나, 순서는 형식 → DEFAULT → NULL.
        var (original, edited) = Start();
        edited = edited.WithColumn("NOTE", c => c with { Length = 400, DefaultValue = "'-'", IsNullable = false });

        Assert.Equal([T + "MODIFY (NOTE VARCHAR2(400 BYTE) DEFAULT '-' NOT NULL)"], Build(original, edited));
    }

    [Fact]
    public void Modify_virtual_expression()
    {
        var (original, edited) = Start();
        edited = edited.WithColumn("TOTAL", c => c with { VirtualExpression = "AMOUNT * QTY * 2" });

        Assert.Equal([T + "MODIFY (TOTAL AS (AMOUNT * QTY * 2))"], Build(original, edited));
    }

    [Fact]
    public void Virtual_expression_is_recreated_when_the_dialect_cannot_modify_it()
    {
        var dialect = new FakeTableEditDialect { SupportsVirtualColumnModify = false };
        var (original, edited) = Start(dialect: dialect);
        edited = edited.WithColumn("TOTAL", c => c with { VirtualExpression = "AMOUNT * QTY * 2" });

        var script = TableEditScriptBuilder.Build(original, edited, dialect);

        Assert.Equal([T + "DROP COLUMN TOTAL", T + "ADD (TOTAL NUMBER AS (AMOUNT * QTY * 2) VIRTUAL)"], script.Texts());
        Assert.All(script.Statements, s => Assert.Equal(TableEditMarks.Recreate, s.Marks));
    }

    [Fact]
    public void Comments_on_table_and_columns_and_clearing_a_comment()
    {
        var (original, edited) = Start();
        edited = (edited with { Comment = "주문 목록" })
            .WithColumn("NOTE", c => c with { Comment = null })
            .WithColumn("STATUS", c => c with { Comment = "상태 'N'/'Y'" });

        Assert.Equal(
            [
                "COMMENT ON TABLE SAMPLE.ORDERS IS '주문 목록'",
                "COMMENT ON COLUMN SAMPLE.ORDERS.STATUS IS '상태 ''N''/''Y'''",
                "COMMENT ON COLUMN SAMPLE.ORDERS.NOTE IS ''",
            ],
            Build(original, edited));
    }

    [Fact]
    public void Null_and_empty_comment_are_the_same()
    {
        var table = Orders() with { Comment = null };
        var (original, edited) = Start(table);
        edited = edited with { Comment = string.Empty };

        Assert.Empty(Build(original, edited));
    }

    [Fact]
    public void Add_constraints_of_every_kind()
    {
        var (original, edited) = Start();
        var name = edited.KeyOf("CUSTOMER_NAME");
        var customer = edited.KeyOf("CUSTOMER_ID");
        edited = edited
            .AddConstraint(new ConstraintDraft("n1", "FK_ORDERS_02", ConstraintKind.ForeignKey)
            {
                ColumnKeys = [customer], ReferencedTable = new DbObjectRef("SAMPLE", "CUSTOMERS", "TABLE"), ReferencedColumns = ["ID"],
                DeleteRule = DeleteRule.SetNull,
            })
            .AddConstraint(new ConstraintDraft("n2", "CK_ORDERS_02", ConstraintKind.Check) { Condition = "AMOUNT >= 0", IsEnabled = false })
            .AddConstraint(new ConstraintDraft("n3", "UK_ORDERS_01", ConstraintKind.Unique) { ColumnKeys = [name] });

        Assert.Equal(
            [
                T + "ADD CONSTRAINT UK_ORDERS_01 UNIQUE (CUSTOMER_NAME)",
                T + "ADD CONSTRAINT CK_ORDERS_02 CHECK (AMOUNT >= 0) DISABLE",
                T + "ADD CONSTRAINT FK_ORDERS_02 FOREIGN KEY (CUSTOMER_ID) REFERENCES SAMPLE.CUSTOMERS (ID) ON DELETE SET NULL",
            ],
            Build(original, edited));
    }

    [Fact]
    public void Add_primary_key_and_cascade_fk()
    {
        var table = Orders() with { Constraints = [], Indexes = [] };
        var (original, edited) = Start(table);
        edited = edited
            .AddConstraint(new ConstraintDraft("n1", "PK_ORDERS", ConstraintKind.PrimaryKey) { ColumnKeys = [edited.KeyOf("ORDER_ID")] })
            .AddConstraint(new ConstraintDraft("n2", "FK_ORDERS_01", ConstraintKind.ForeignKey)
            {
                ColumnKeys = [edited.KeyOf("CUSTOMER_ID")], ReferencedTable = new DbObjectRef("SAMPLE", "CUSTOMERS", "TABLE"), ReferencedColumns = ["ID"],
                DeleteRule = DeleteRule.Cascade,
            });

        Assert.Equal(
            [
                T + "ADD CONSTRAINT PK_ORDERS PRIMARY KEY (ORDER_ID)",
                T + "ADD CONSTRAINT FK_ORDERS_01 FOREIGN KEY (CUSTOMER_ID) REFERENCES SAMPLE.CUSTOMERS (ID) ON DELETE CASCADE",
            ],
            Build(original, edited));
    }

    [Fact]
    public void Drop_enable_disable_and_rename_constraints()
    {
        var (original, edited) = Start();
        edited = edited
            .WithConstraint("CK_ORDERS_01", c => c with { IsEnabled = false })
            .WithConstraint("FK_ORDERS_01", c => c with { IsDropped = true })
            .WithConstraint("PK_ORDERS", c => c with { Name = "PK_ORDERS_X" });

        Assert.Equal(
            [
                T + "DROP CONSTRAINT FK_ORDERS_01",
                T + "RENAME CONSTRAINT PK_ORDERS TO PK_ORDERS_X",
                T + "DISABLE CONSTRAINT CK_ORDERS_01",
            ],
            Build(original, edited));
    }

    [Fact]
    public void Changing_a_constraint_definition_drops_and_adds_it()
    {
        var (original, edited) = Start();
        edited = edited.WithConstraint("CK_ORDERS_01", c => c with { Condition = "STATUS IN ('N','Y','C')" });

        var script = TableEditScriptBuilder.Build(original, edited, Dialect);

        Assert.Equal([T + "DROP CONSTRAINT CK_ORDERS_01", T + "ADD CONSTRAINT CK_ORDERS_01 CHECK (STATUS IN ('N','Y','C'))"], script.Texts());
        Assert.All(script.Statements, s => Assert.True(s.Marks.HasFlag(TableEditMarks.Recreate)));
    }

    [Fact]
    public void Server_named_constraint_statements_are_marked()
    {
        // TE-8: 서버 이름 제약은 그 이름으로 문장을 만들고 ⚠.
        var (original, edited) = Start();
        edited = edited.WithConstraint("SYS_C0013", c => c with { IsEnabled = false });

        var s = Assert.Single(TableEditScriptBuilder.Build(original, edited, Dialect).Statements);

        Assert.Equal(T + "DISABLE CONSTRAINT SYS_C0013", s.Statement.Text);
        Assert.True(s.Marks.HasFlag(TableEditMarks.ServerName));
    }

    [Fact]
    public void Indexes_create_drop_rename_and_definition_change()
    {
        var (original, edited) = Start();
        edited = edited
            .AddIndex(new IndexDraft("n1", "IX_ORDERS_03")
            {
                IsUnique = true, Tablespace = "USERS",
                Columns = [new IndexColumnDraft(edited.KeyOf("CUSTOMER_NAME")), new IndexColumnDraft(edited.KeyOf("CREATED"), Descending: true)],
            })
            .WithIndex("IX_ORDERS_02", i => i with { IsDropped = true })
            .WithIndex("IX_ORDERS_01", i => i with { Name = "IX_ORDERS_09" });

        Assert.Equal(
            [
                "DROP INDEX SAMPLE.IX_ORDERS_02",
                "ALTER INDEX SAMPLE.IX_ORDERS_01 RENAME TO IX_ORDERS_09",
                "CREATE UNIQUE INDEX SAMPLE.IX_ORDERS_03 ON SAMPLE.ORDERS (CUSTOMER_NAME, CREATED DESC) TABLESPACE USERS",
            ],
            Build(original, edited));
    }

    [Fact]
    public void Index_column_change_is_drop_and_create()
    {
        var (original, edited) = Start();
        edited = edited.WithIndex("IX_ORDERS_01", i => i with { Columns = [i.Columns[1], i.Columns[0]] });

        Assert.Equal(
            ["DROP INDEX SAMPLE.IX_ORDERS_01", "CREATE INDEX SAMPLE.IX_ORDERS_01 ON SAMPLE.ORDERS (CREATED DESC, CUSTOMER_NAME) TABLESPACE USERS"],
            Build(original, edited));
    }

    [Fact]
    public void Constraint_index_is_not_edited_on_the_index_page()
    {
        var (original, edited) = Start();
        edited = edited.WithIndex("PK_ORDERS", i => i with { Name = "X", IsDropped = true });

        Assert.Empty(Build(original, edited));
    }

    [Fact]
    public void Statement_order_follows_the_design()
    {
        // 설계 8.3: FK 지우기 → 제약 지우기 → 인덱스 지우기 → 열 이름 → MODIFY → 열 추가(일반 → 가상) → 열 지우기
        //          → 제약 추가(PK·UK → CHECK → FK)·ENABLE/DISABLE → 인덱스 만들기·이름 → 주석.
        var (original, edited) = Start();
        edited = (edited with { Comment = "c" })
            .WithConstraint("FK_ORDERS_01", c => c with { IsDropped = true })
            .WithConstraint("CK_ORDERS_01", c => c with { IsDropped = true })
            .WithIndex("IX_ORDERS_02", i => i with { IsDropped = true })
            .WithColumn("CUSTOMER_NAME", c => c with { Name = "CUST_NAME" })
            .WithColumn("AMOUNT", c => c with { Precision = 14 })
            .WithColumn("NOTE", c => c with { IsDropped = true })
            .AddColumn(new ColumnDraft("v1", "TAX") { VirtualExpression = "AMOUNT * 0.1" })
            .AddColumn(new ColumnDraft("n1", "CODE") { DataType = "VARCHAR2", Length = 10 })
            .AddConstraint(new ConstraintDraft("nk1", "FK_ORDERS_02", ConstraintKind.ForeignKey)
            {
                ColumnKeys = [edited.KeyOf("CUSTOMER_ID")], ReferencedTable = new DbObjectRef("SAMPLE", "CUSTOMERS", "TABLE"), ReferencedColumns = ["ID"],
            })
            .AddConstraint(new ConstraintDraft("nk2", "CK_ORDERS_02", ConstraintKind.Check) { Condition = "CODE IS NOT NULL" })
            .AddConstraint(new ConstraintDraft("nk3", "UK_ORDERS_01", ConstraintKind.Unique) { ColumnKeys = ["n1"] })
            .AddIndex(new IndexDraft("ni1", "IX_ORDERS_03") { Columns = [new IndexColumnDraft("n1")] });

        Assert.Equal(
            [
                T + "DROP CONSTRAINT FK_ORDERS_01",
                T + "DROP CONSTRAINT CK_ORDERS_01",
                "DROP INDEX SAMPLE.IX_ORDERS_02",
                T + "RENAME COLUMN CUSTOMER_NAME TO CUST_NAME",
                T + "MODIFY (AMOUNT NUMBER(14,2))",
                T + "ADD (CODE VARCHAR2(10))",
                T + "ADD (TAX AS (AMOUNT * 0.1) VIRTUAL)",
                T + "DROP COLUMN NOTE",
                T + "ADD CONSTRAINT UK_ORDERS_01 UNIQUE (CODE)",
                T + "ADD CONSTRAINT CK_ORDERS_02 CHECK (CODE IS NOT NULL)",
                T + "ADD CONSTRAINT FK_ORDERS_02 FOREIGN KEY (CUSTOMER_ID) REFERENCES SAMPLE.CUSTOMERS (ID)",
                "CREATE INDEX SAMPLE.IX_ORDERS_03 ON SAMPLE.ORDERS (CODE)",
                "COMMENT ON TABLE SAMPLE.ORDERS IS 'c'",
            ],
            Build(original, edited));
    }

    [Fact]
    public void Drop_and_readd_same_name_drops_first()
    {
        // 설계 8.3 예외: 같은 이름으로 지우고 다시 넣으면 지우기가 먼저다.
        var (original, edited) = Start();
        edited = edited
            .WithColumn("NOTE", c => c with { IsDropped = true })
            .AddColumn(new ColumnDraft("n1", "NOTE") { DataType = "CLOB" });

        Assert.Equal([T + "DROP COLUMN NOTE", T + "ADD (NOTE CLOB)"], Build(original, edited));
    }

    [Fact]
    public void Rename_then_add_old_name()
    {
        var (original, edited) = Start();
        edited = edited
            .WithColumn("NOTE", c => c with { Name = "MEMO" })
            .AddColumn(new ColumnDraft("n1", "NOTE") { DataType = "CLOB" });

        Assert.Equal([T + "RENAME COLUMN NOTE TO MEMO", T + "ADD (NOTE CLOB)"], Build(original, edited));
    }

    [Fact]
    public void Rename_onto_a_dropped_name_drops_first()
    {
        var (original, edited) = Start();
        edited = edited
            .WithColumn("NOTE", c => c with { IsDropped = true })
            .WithColumn("CUSTOMER_NAME", c => c with { Name = "NOTE" });

        Assert.Equal([T + "DROP COLUMN NOTE", T + "RENAME COLUMN CUSTOMER_NAME TO NOTE"], Build(original, edited));
    }

    [Fact]
    public void Swapped_column_names_go_through_a_temporary_name()
    {
        var (original, edited) = Start();
        var (note, name) = (edited.KeyOf("NOTE"), edited.KeyOf("CUSTOMER_NAME"));
        edited = edited
            .WithColumnKey(note, c => c with { Name = "CUSTOMER_NAME" })
            .WithColumnKey(name, c => c with { Name = "NOTE" });

        Assert.Equal(
            [
                T + "RENAME COLUMN CUSTOMER_NAME TO CUSTOMER_NAME$1",
                T + "RENAME COLUMN NOTE TO CUSTOMER_NAME",
                T + "RENAME COLUMN CUSTOMER_NAME$1 TO NOTE",
            ],
            Build(original, edited));
    }

    [Fact]
    public void Chained_renames_are_ordered()
    {
        var (original, edited) = Start();
        var (note, name) = (edited.KeyOf("NOTE"), edited.KeyOf("CUSTOMER_NAME"));
        edited = edited
            .WithColumnKey(name, c => c with { Name = "NOTE" })
            .WithColumnKey(note, c => c with { Name = "MEMO" });

        Assert.Equal([T + "RENAME COLUMN NOTE TO MEMO", T + "RENAME COLUMN CUSTOMER_NAME TO NOTE"], Build(original, edited));
    }

    [Fact]
    public void Dropping_a_column_drops_its_constraints_and_indexes_first()
    {
        var (original, edited) = Start();
        edited = edited.WithColumn("CREATED", c => c with { IsDropped = true });

        Assert.Equal(
            [
                T + "DROP CONSTRAINT SYS_C0013",
                "DROP INDEX SAMPLE.IX_ORDERS_01",
                T + "DROP COLUMN CREATED",
            ],
            Build(original, edited));
    }

    [Fact]
    public void Constraint_on_a_renamed_column_is_unchanged()
    {
        var (original, edited) = Start();
        edited = edited.WithColumn("ORDER_ID", c => c with { Name = "ID" });

        Assert.Equal([T + "RENAME COLUMN ORDER_ID TO ID"], Build(original, edited));
    }

    [Fact]
    public void Names_are_quoted_only_outside_the_plain_upper_rule_and_for_reserved_words()
    {
        var (original, edited) = Start();
        edited = edited
            .AddColumn(new ColumnDraft("n1", "Mixed") { DataType = "DATE" })
            .AddColumn(new ColumnDraft("n2", "LEVEL") { DataType = "NUMBER" })
            .AddColumn(new ColumnDraft("n3", "주문일") { DataType = "DATE" });

        Assert.Equal([T + "ADD (\"Mixed\" DATE)", T + "ADD (\"LEVEL\" NUMBER)", T + "ADD (\"주문일\" DATE)"], Build(original, edited));
    }

    [Fact]
    public void Statements_carry_the_classifier_metadata()
    {
        var (original, edited) = Start();
        edited = (edited with { Comment = "c" })
            .WithColumn("AMOUNT", c => c with { Precision = 14 })
            .WithIndex("IX_ORDERS_02", i => i with { IsDropped = true })
            .WithIndex("IX_ORDERS_01", i => i with { Name = "IX_ORDERS_09" })
            .AddIndex(new IndexDraft("ni1", "IX_ORDERS_03") { Columns = [new IndexColumnDraft(edited.KeyOf("NOTE"))] });

        var all = TableEditScriptBuilder.Build(original, edited, Dialect).SqlStatements;

        Assert.All(all, s => Assert.Equal(StatementKind.Ddl, s.Kind));
        var drop = all.Single(s => s.Text.StartsWith("DROP INDEX", StringComparison.Ordinal));
        Assert.Equal(("DROP", "SAMPLE.IX_ORDERS_02"), (drop.Verb, drop.DangerousTarget));
        Assert.Equal(new ChangeTarget("SAMPLE", "IX_ORDERS_02", "INDEX", ChangeOperation.Drop), drop.ChangeTarget);
        var modify = all.Single(s => s.Text.Contains("MODIFY", StringComparison.Ordinal));
        Assert.Equal(new ChangeTarget("SAMPLE", "ORDERS", "TABLE", ChangeOperation.Alter), modify.ChangeTarget);
        var rename = all.Single(s => s.Text.StartsWith("ALTER INDEX", StringComparison.Ordinal));
        Assert.Equal(new ChangeTarget("SAMPLE", "IX_ORDERS_01", "INDEX", ChangeOperation.Alter), rename.ChangeTarget);
        Assert.All(all.Where(s => s.Text.StartsWith("CREATE", StringComparison.Ordinal) || s.Text.StartsWith("COMMENT", StringComparison.Ordinal)),
            s => Assert.Null(s.ChangeTarget));
        Assert.Equal("COMMENT", all[^1].Verb);
    }

    [Fact]
    public void Preview_text_is_the_statement_texts_with_terminators()
    {
        var (original, edited) = Start();
        edited = (edited with { Comment = "c" }).WithColumn("NOTE", c => c with { Name = "MEMO" });

        var script = TableEditScriptBuilder.Build(original, edited, Dialect);

        Assert.Equal(T + "RENAME COLUMN NOTE TO MEMO;\r\nCOMMENT ON TABLE SAMPLE.ORDERS IS 'c';", script.PreviewText("\r\n"));
    }

    [Fact]
    public void Lower_case_owner_and_table_are_quoted()
    {
        var table = Orders() with { Table = new DbObjectRef("sample", "Orders", "TABLE") };
        var (original, edited) = Start(table);
        edited = edited with { Comment = "c" };

        Assert.Equal(["COMMENT ON TABLE \"sample\".\"Orders\" IS 'c'"], Build(original, edited));
    }
}
