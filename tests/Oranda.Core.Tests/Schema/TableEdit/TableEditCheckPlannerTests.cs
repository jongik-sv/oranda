using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Xunit;
using static Oranda.Core.Tests.Schema.TableEdit.Samples;

namespace Oranda.Core.Tests.Schema.TableEdit;

/// <summary>편집 → 검사 계획(설계 10장 ✖/⚠ 각 조건).</summary>
public sealed class TableEditCheckPlannerTests
{
    private static readonly TableEditCheckOptions Dev = new() { Environment = ConnectionEnvironment.Development };

    private static TableEditCheckPlan Plan(TableDraft original, TableDraft edited, ITableEditDialect? dialect = null, TableEditCheckOptions? options = null) =>
        TableEditCheckPlanner.Plan(original, edited, dialect ?? Dialect, options ?? Dev);

    [Fact]
    public void No_edit_plans_nothing()
    {
        var (original, edited) = Start();

        Assert.Equal(TableEditCheckPlan.Empty.Findings, Plan(original, edited).Findings);
        Assert.Empty(Plan(original, edited).Checks);
    }

    [Fact]
    public void Shrinking_a_character_column_checks_overflow_with_the_original_name()
    {
        var (original, edited) = Start();
        edited = edited.WithColumn("NOTE", c => c with { Name = "MEMO", Length = 50 });

        var check = Assert.Single(Plan(original, edited).Checks);

        Assert.Equal((TableEditCheckKind.Overflow, CheckCode.Overflow, "NOTE"), (check.Kind, check.Code, check.Column));
        Assert.Equal(50, check.Target!.Length);
        Assert.Equal(CharSemantics.Bytes, check.Target.CharSemantics);
    }

    [Fact]
    public void Growing_a_column_needs_no_check()
    {
        var (original, edited) = Start();
        edited = edited.WithColumn("NOTE", c => c with { Length = 500 }).WithColumn("AMOUNT", c => c with { Precision = 20 });

        Assert.Empty(Plan(original, edited).Checks);
    }

    [Fact]
    public void Byte_to_char_semantics_with_the_same_number_is_growth_and_char_to_byte_is_a_shrink()
    {
        var (original, edited) = Start();

        Assert.Empty(Plan(original, edited.WithColumn("NOTE", c => c with { CharSemantics = CharSemantics.Characters })).Checks);
        Assert.Equal(TableEditCheckKind.Overflow,
            Assert.Single(Plan(original, edited.WithColumn("CUSTOMER_NAME", c => c with { CharSemantics = CharSemantics.Bytes })).Checks).Kind);
    }

    [Fact]
    public void Type_family_change_and_number_shrink_need_an_empty_column()
    {
        var (original, edited) = Start();
        edited = edited
            .WithColumn("NOTE", c => c with { DataType = "NUMBER", Length = null, CharSemantics = null })
            .WithColumn("AMOUNT", c => c with { Precision = 8 })
            .WithColumn("STATUS", c => c with { DataType = "VARCHAR2", Length = 1 });

        var checks = Plan(original, edited).Checks;

        Assert.Equal(
            [("AMOUNT", TableEditCheckKind.NonNull), ("NOTE", TableEditCheckKind.NonNull)],
            checks.Select(c => (c.Column, c.Kind)).OrderBy(c => c.Column));
        Assert.All(checks, c => Assert.Equal(CheckCode.IncompatibleTypeChange, c.Code));
    }

    [Fact]
    public void Not_null_on_an_existing_column_counts_nulls()
    {
        var (original, edited) = Start();
        edited = edited.WithColumn("NOTE", c => c with { IsNullable = false });

        var check = Assert.Single(Plan(original, edited).Checks);

        Assert.Equal((TableEditCheckKind.Nulls, CheckCode.NullRows, "NOTE"), (check.Kind, check.Code, check.Column));
    }

    [Fact]
    public void New_not_null_column_without_default_needs_an_empty_table()
    {
        var (original, edited) = Start();
        var withoutDefault = edited.AddColumn(new ColumnDraft("n1", "CODE") { DataType = "NUMBER", IsNullable = false });
        var withDefault = edited.AddColumn(new ColumnDraft("n1", "CODE") { DataType = "NUMBER", IsNullable = false, DefaultValue = "0" });

        var rows = Assert.Single(Plan(original, withoutDefault).Checks);
        Assert.Equal((TableEditCheckKind.Rows, CheckCode.NullRows, "n1"), (rows.Kind, rows.Code, rows.RowKey));
        Assert.DoesNotContain(Plan(original, withDefault).Checks, c => c.Kind == TableEditCheckKind.Rows);
    }

    [Fact]
    public void Key_constraints_check_duplicates_nulls_orphans_and_violations()
    {
        var table = Orders() with { Constraints = [], Indexes = [] };
        var (original, edited) = Start(table);
        edited = edited
            .AddConstraint(new ConstraintDraft("nk1", "PK_ORDERS", ConstraintKind.PrimaryKey) { ColumnKeys = [edited.KeyOf("ORDER_ID"), edited.KeyOf("NOTE")] })
            .AddConstraint(new ConstraintDraft("nk2", "FK_ORDERS_01", ConstraintKind.ForeignKey)
            {
                ColumnKeys = [edited.KeyOf("CUSTOMER_ID")], ReferencedTable = new DbObjectRef("SAMPLE", "CUSTOMERS", "TABLE"), ReferencedColumns = ["ID"],
            })
            .AddConstraint(new ConstraintDraft("nk3", "CK_ORDERS_01", ConstraintKind.Check) { Condition = "AMOUNT >= 0" });

        var checks = Plan(original, edited).Checks.Where(c => c.Kind != TableEditCheckKind.NameExists).ToList();

        Assert.Contains(checks, c => c is { Kind: TableEditCheckKind.DuplicateKeys, Code: CheckCode.DuplicateKeys, RowKey: "nk1" } && c.Columns.SequenceEqual(["ORDER_ID", "NOTE"]));
        // PK 열 가운데 NULL 허용인 열만 NULL 을 센다(ORDER_ID 는 이미 NOT NULL).
        Assert.Single(checks, c => c.Kind == TableEditCheckKind.Nulls && c.Column == "NOTE" && c.RowKey == "nk1");
        var fk = Assert.Single(checks, c => c.Kind == TableEditCheckKind.Orphans);
        Assert.Equal((CheckCode.OrphanRows, "CUSTOMERS"), (fk.Code, fk.ReferencedTable!.Name));
        Assert.Equal(["CUSTOMER_ID"], fk.Columns);
        Assert.Equal(["ID"], fk.ReferencedColumns);
        Assert.Single(checks, c => c is { Kind: TableEditCheckKind.CheckViolations, Condition: "AMOUNT >= 0" });
        Assert.Equal(4, checks.Count);
    }

    [Fact]
    public void Disabled_new_constraint_and_constraint_on_a_new_column_skip_data_checks()
    {
        var (original, edited) = Start();
        edited = edited
            .AddColumn(new ColumnDraft("n1", "CODE") { DataType = "NUMBER" })
            .AddConstraint(new ConstraintDraft("nk1", "UK_ORDERS_01", ConstraintKind.Unique) { ColumnKeys = ["n1"] })
            .AddConstraint(new ConstraintDraft("nk2", "CK_ORDERS_02", ConstraintKind.Check) { Condition = "AMOUNT >= 0", IsEnabled = false });

        Assert.All(Plan(original, edited).Checks, c => Assert.Equal(TableEditCheckKind.NameExists, c.Kind));
    }

    [Fact]
    public void Enabling_a_disabled_constraint_checks_the_data()
    {
        var table = Orders() with { Constraints = [new ConstraintInfo("CK_ORDERS_01", ConstraintKind.Check, ["STATUS"]) { Condition = "STATUS IN ('N','Y')", IsEnabled = false }] };
        var (original, edited) = Start(table);
        edited = edited.WithConstraint("CK_ORDERS_01", c => c with { IsEnabled = true });

        Assert.Equal(TableEditCheckKind.CheckViolations, Assert.Single(Plan(original, edited).Checks).Kind);
    }

    [Fact]
    public void Virtual_expression_is_previewed_with_its_columns_or_left_for_after_the_run()
    {
        var (original, edited) = Start();
        var changed = edited.WithColumn("TOTAL", c => c with { VirtualExpression = "AMOUNT * QTY * 2" });
        var onNew = edited
            .AddColumn(new ColumnDraft("n1", "RATE") { DataType = "NUMBER" })
            .AddColumn(new ColumnDraft("n2", "TAX") { VirtualExpression = "amount * rate" });

        var check = Assert.Single(Plan(original, changed).Checks);
        Assert.Equal((TableEditCheckKind.Expression, CheckCode.ExpressionFails, "AMOUNT * QTY * 2"), (check.Kind, check.Code, check.Expression));
        Assert.Equal(["AMOUNT", "QTY"], check.Columns);
        var plan = Plan(original, onNew);
        Assert.DoesNotContain(plan.Checks, c => c.Kind == TableEditCheckKind.Expression);
        Assert.Single(plan.Findings, f => f is { Severity: CheckSeverity.Warning, Code: CheckCode.NotChecked, RowKey: "n2" });
    }

    [Fact]
    public void New_table_checks_its_name_and_expression_syntax_only()
    {
        var draft = (TableDraftFactory.Empty("SAMPLE") with { Name = "ORDER_ITEMS" })
            .AddColumn(new ColumnDraft("c1", "PRICE") { DataType = "NUMBER", IsNullable = false })
            .AddColumn(new ColumnDraft("c2", "PRICE_X2") { VirtualExpression = "PRICE * 2" })
            .AddConstraint(new ConstraintDraft("nk1", "PK_ORDER_ITEMS", ConstraintKind.PrimaryKey) { ColumnKeys = ["c1"] });

        var checks = Plan(TableDraftFactory.Empty("SAMPLE"), draft).Checks;

        Assert.Contains(checks, c => c is { Kind: TableEditCheckKind.NameExists, NameKind: TableEditNameKind.Table, Name: "ORDER_ITEMS" });
        Assert.Contains(checks, c => c is { Kind: TableEditCheckKind.NameExists, NameKind: TableEditNameKind.Constraint, Name: "PK_ORDER_ITEMS" });
        Assert.Contains(checks, c => c is { Kind: TableEditCheckKind.NameExists, NameKind: TableEditNameKind.Index, Name: "PK_ORDER_ITEMS" });
        var syntax = Assert.Single(checks, c => c.Kind == TableEditCheckKind.ExpressionSyntax);
        Assert.Equal(["PRICE", "PRICE_X2"], syntax.ColumnDrafts.Select(c => c.Name));
        Assert.Equal(4, checks.Count);
    }

    [Fact]
    public void Names_freed_in_the_same_edit_are_not_checked_on_the_server()
    {
        var (original, edited) = Start();
        edited = edited
            .WithConstraint("CK_ORDERS_01", c => c with { IsDropped = true })
            .AddConstraint(new ConstraintDraft("nk1", "CK_ORDERS_01", ConstraintKind.Check) { Condition = "1 = 1" })
            .WithIndex("IX_ORDERS_02", i => i with { Name = "IX_ORDERS_09" })
            .AddIndex(new IndexDraft("ni1", "IX_ORDERS_02") { Columns = [new IndexColumnDraft(edited.KeyOf("NOTE"))] });

        var names = Plan(original, edited).Checks.Where(c => c.Kind == TableEditCheckKind.NameExists).Select(c => c.Name).ToList();

        Assert.Equal(["IX_ORDERS_09"], names);
    }

    [Fact]
    public void Duplicate_names_within_the_draft_are_blocked_without_the_server()
    {
        var (original, edited) = Start();
        edited = edited
            .AddColumn(new ColumnDraft("n1", "NOTE") { DataType = "DATE" })
            .AddIndex(new IndexDraft("ni1", "IX_ORDERS_01") { Columns = [new IndexColumnDraft("n1")] });

        var findings = Plan(original, edited).Findings.Where(f => f.Code == CheckCode.NameExists).ToList();

        Assert.Equal([("n1", "NOTE"), ("ni1", "IX_ORDERS_01")], findings.Select(f => (f.RowKey, f.Subject)));
        Assert.All(findings, f => Assert.Equal(CheckSeverity.Block, f.Severity));
    }

    [Fact]
    public void Drops_are_irreversible_warnings_with_a_dependents_check()
    {
        var dialect = new FakeTableEditDialect { PurgesOnDrop = true };
        var (original, edited) = Start(dialect: dialect);
        edited = edited.WithColumn("NOTE", c => c with { IsDropped = true }).WithIndex("IX_ORDERS_01", i => i with { IsDropped = true });

        var plan = Plan(original, edited, dialect);

        Assert.Equal(["NOTE", "IX_ORDERS_01"], plan.Findings.Where(f => f.Code == CheckCode.Irreversible).Select(f => f.Subject));
        Assert.All(plan.Findings.Where(f => f.Code == CheckCode.Irreversible), f => Assert.Equal(("purge", CheckSeverity.Warning), (f.Detail, f.Severity)));
        Assert.Single(plan.Checks, c => c.Kind == TableEditCheckKind.Dependents);
    }

    [Fact]
    public void Server_named_constraint_is_a_warning()
    {
        var (original, edited) = Start();
        edited = edited.WithConstraint("SYS_C0013", c => c with { IsEnabled = false });

        var finding = Assert.Single(Plan(original, edited).Findings);

        Assert.Equal((CheckSeverity.Warning, CheckCode.ServerName, "SYS_C0013"), (finding.Severity, finding.Code, finding.Subject));
    }

    [Fact]
    public void Virtual_recreate_is_a_warning()
    {
        var dialect = new FakeTableEditDialect { SupportsVirtualColumnModify = false };
        var (original, edited) = Start(dialect: dialect);
        edited = edited.WithColumn("TOTAL", c => c with { VirtualExpression = "AMOUNT + 1" });

        Assert.Single(Plan(original, edited, dialect).Findings, f => f is { Code: CheckCode.VirtualRecreated, Severity: CheckSeverity.Warning });
    }

    [Fact]
    public void Tibero_warns_that_dropped_column_values_resurface_when_the_table_has_a_virtual_column()
    {
        var tibero = new FakeTableEditDialect { Dbms = Dbms.Tibero };
        var (original, edited) = Start(dialect: tibero);
        Assert.Contains(original.Columns, c => c.IsVirtual); // 예시 테이블에 가상 열 TOTAL
        edited = edited.WithColumn("NOTE", c => c with { IsDropped = true });

        var finding = Assert.Single(Plan(original, edited, tibero).Findings, f => f.Code == CheckCode.TiberoDroppedColumnData);
        Assert.Equal((CheckSeverity.Warning, TableDraft.TableRowKey), (finding.Severity, finding.RowKey));

        // Oracle 은 이 결함이 없다.
        var (o2, e2) = Start();
        Assert.DoesNotContain(Plan(o2, e2.WithColumn("NOTE", c => c with { IsDropped = true })).Findings, f => f.Code == CheckCode.TiberoDroppedColumnData);
    }

    [Fact]
    public void Tibero_does_not_warn_without_a_virtual_column_or_without_a_drop()
    {
        var tibero = new FakeTableEditDialect { Dbms = Dbms.Tibero };
        var (original, edited) = Start(dialect: tibero);
        Assert.DoesNotContain(Plan(original, edited.WithColumn("NOTE", c => c with { Length = 500 }), tibero).Findings, f => f.Code == CheckCode.TiberoDroppedColumnData);

        var plain = original with { Columns = [.. original.Columns.Where(c => !c.IsVirtual)] };
        Assert.DoesNotContain(Plan(plain, plain.WithColumn("NOTE", c => c with { IsDropped = true }), tibero).Findings, f => f.Code == CheckCode.TiberoDroppedColumnData);
    }

    [Fact]
    public void Unsupported_features_are_blocked_with_the_reason_key()
    {
        var dialect = new FakeTableEditDialect();
        dialect.Unsupported[TableEditFeature.DescendingIndex] = "tableEdit.unsupported.desc";
        dialect.Unsupported[TableEditFeature.ForeignKeyOnDeleteSetNull] = "tableEdit.unsupported.setNull";
        var (original, edited) = Start(dialect: dialect);
        edited = edited
            .AddIndex(new IndexDraft("ni1", "IX_ORDERS_03") { Columns = [new IndexColumnDraft(edited.KeyOf("NOTE"), Descending: true)] })
            .AddConstraint(new ConstraintDraft("nk1", "FK_ORDERS_02", ConstraintKind.ForeignKey)
            {
                ColumnKeys = [edited.KeyOf("CUSTOMER_ID")], ReferencedTable = new DbObjectRef("SAMPLE", "CUSTOMERS", "TABLE"), ReferencedColumns = ["ID"],
                DeleteRule = DeleteRule.SetNull,
            });

        var blocked = Plan(original, edited, dialect).Findings.Where(f => f.Code == CheckCode.Unsupported).ToList();

        Assert.Equal(
            [("nk1", "tableEdit.unsupported.setNull"), ("ni1", "tableEdit.unsupported.desc")],
            blocked.Select(f => (f.RowKey, f.Detail)));
        Assert.All(blocked, f => Assert.Equal(CheckSeverity.Block, f.Severity));
    }

    [Fact]
    public void Production_lock_taking_edit_checks_the_table_size()
    {
        var (original, edited) = Start();
        var production = new TableEditCheckOptions { Environment = ConnectionEnvironment.Production };

        Assert.Contains(Plan(original, edited.WithColumn("NOTE", c => c with { IsNullable = false }), options: production).Checks,
            c => c is { Kind: TableEditCheckKind.LargeTable, Code: CheckCode.LongLock });
        Assert.DoesNotContain(Plan(original, edited.WithColumn("NOTE", c => c with { Comment = "x" }), options: production).Checks,
            c => c.Kind == TableEditCheckKind.LargeTable);
        Assert.DoesNotContain(Plan(original, edited.WithColumn("NOTE", c => c with { IsNullable = false })).Checks,
            c => c.Kind == TableEditCheckKind.LargeTable);
    }
}
