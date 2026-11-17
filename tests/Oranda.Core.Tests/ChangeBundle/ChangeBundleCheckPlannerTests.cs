using Oranda.Core.ChangeBundle;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.Core.Tests.ChangeBundle;

// 소유: te-bundle(T10). 묶음 문장 실행 전 검사 계획(설계 9.2 해석 범위·10장).
public sealed class ChangeBundleCheckPlannerTests
{
    private static readonly TableEditCheckOptions Dev = new() { Environment = ConnectionEnvironment.Development };
    private static readonly TableEditCheckOptions Prod = new() { Environment = ConnectionEnvironment.Production };

    private static IReadOnlyList<ChangeBundleStatementPlan> Plan(TableEditCheckOptions options, params string[] statements) =>
        ChangeBundleCheckPlanner.Plan(statements, "SAMPLE", options);

    [Fact]
    public void Modify_length_and_not_null_become_overflow_and_null_checks()
    {
        var p = Assert.Single(Plan(Dev, "ALTER TABLE SAMPLE.ORDERS MODIFY (NOTE VARCHAR2(20 CHAR) NOT NULL, CODE NCHAR(3), AMOUNT NUMBER(10,2))"));

        Assert.Equal(new DbObjectRef("SAMPLE", "ORDERS", "TABLE"), p.Table);
        Assert.Empty(p.Plan.Findings);
        Assert.Collection(p.Plan.Checks,
            c =>
            {
                Assert.Equal(TableEditCheckKind.Overflow, c.Kind);
                Assert.Equal("NOTE", c.Column);
                Assert.Equal(20, c.Target!.Length);
                Assert.Equal(CharSemantics.Characters, c.Target.CharSemantics);
                Assert.Equal("#1 NOTE", c.Subject);
            },
            c => Assert.Equal((TableEditCheckKind.Nulls, "NOTE"), (c.Kind, c.Column)),
            c => Assert.Equal((TableEditCheckKind.Overflow, "CODE", "NCHAR"), (c.Kind, c.Column, c.Target!.DataType)));
    }

    [Fact]
    public void Modify_without_parentheses_and_without_semantics_counts_characters()
    {
        var c = Assert.Single(Assert.Single(Plan(Dev, "alter table orders modify note varchar2(10)")).Plan.Checks);

        Assert.Equal("NOTE", c.Column);
        Assert.Null(c.Target!.CharSemantics); // 세션 기본을 모른다 — 글자로 세어 거짓 ✖ 를 내지 않는다
        Assert.Equal(CharSemantics.Bytes, Assert.Single(Assert.Single(Plan(Dev, "ALTER TABLE SAMPLE.T MODIFY (A VARCHAR2(10 BYTE))")).Plan.Checks).Target!.CharSemantics);
    }

    [Fact]
    public void Primary_key_also_counts_null_rows_per_column()
    {
        var checks = Assert.Single(Plan(Dev, "ALTER TABLE SAMPLE.ORDERS ADD CONSTRAINT PK_ORDERS PRIMARY KEY (ORDER_ID, LINE_NO)")).Plan.Checks;

        Assert.Equal([TableEditCheckKind.DuplicateKeys, TableEditCheckKind.Nulls, TableEditCheckKind.Nulls], checks.Select(c => c.Kind));
        Assert.Equal(["ORDER_ID", "LINE_NO"], checks.Skip(1).Select(c => c.Column));
    }

    [Fact]
    public void A_table_changed_by_an_earlier_statement_is_not_data_checked()
    {
        var plans = Plan(Dev,
            "DROP TABLE SAMPLE.ORDERS",
            "CREATE TABLE SAMPLE.ORDERS (CODE VARCHAR2(10))",
            "ALTER TABLE SAMPLE.ORDERS ADD CONSTRAINT UK_CODE UNIQUE (CODE)",
            "ALTER TABLE SAMPLE.ORDER_ITEMS MODIFY (NOTE NOT NULL)");

        var uk = plans.Single(p => p.Index == 2);
        Assert.Empty(uk.Plan.Checks);
        Assert.Equal(CheckCode.NotChecked, Assert.Single(uk.Plan.Findings).Code);
        Assert.Equal(TableEditCheckKind.Nulls, Assert.Single(plans.Single(p => p.Index == 3).Plan.Checks).Kind); // 다른 테이블은 그대로
    }

    [Fact]
    public void Earlier_constraint_only_changes_keep_later_data_checks_but_added_columns_do_not()
    {
        var plans = Plan(Dev,
            "ALTER TABLE SAMPLE.ORDERS ADD CONSTRAINT PK_ORDERS PRIMARY KEY (ORDER_ID)",
            "ALTER TABLE SAMPLE.ORDERS MODIFY (NOTE NOT NULL)",
            "ALTER TABLE SAMPLE.ORDER_ITEMS ADD (MEMO VARCHAR2(10))",
            "ALTER TABLE SAMPLE.ORDER_ITEMS MODIFY (MEMO NOT NULL)",
            "UPDATE SAMPLE.T SET A = 1",
            "ALTER TABLE SAMPLE.T MODIFY (A NOT NULL)");

        Assert.Equal(TableEditCheckKind.Nulls, Assert.Single(plans.Single(p => p.Index == 1).Plan.Checks).Kind);
        Assert.Empty(plans.Single(p => p.Index == 3).Plan.Checks);
        Assert.Empty(plans.Single(p => p.Index == 5).Plan.Checks);
    }

    [Fact]
    public void If_exists_is_skipped_before_the_name()
    {
        var p = Assert.Single(Plan(Dev, "DROP TABLE IF EXISTS SAMPLE.OLD_ORDERS"));

        Assert.Equal(new DbObjectRef("SAMPLE", "OLD_ORDERS", "TABLE"), p.Table);
    }

    [Fact]
    public void Add_constraints_become_duplicate_orphan_and_violation_checks()
    {
        var plans = Plan(Dev,
            "ALTER TABLE SAMPLE.ORDERS ADD CONSTRAINT PK_ORDERS PRIMARY KEY (ORDER_ID)",
            "ALTER TABLE SAMPLE.ORDER_ITEMS ADD CONSTRAINT FK_ITEMS_ORDER FOREIGN KEY (ORDER_ID) REFERENCES SAMPLE.ORDERS (ORDER_ID)",
            "ALTER TABLE SAMPLE.ORDERS ADD CONSTRAINT CK_QTY CHECK (QTY > 0 AND NOTE <> ')')",
            "ALTER TABLE SAMPLE.ORDERS ADD (CONSTRAINT UK_CODE UNIQUE (CODE, \"Mixed\"))");

        Assert.Equal([0, 1, 2, 3], plans.Select(p => p.Index));
        Assert.Equal(TableEditCheckKind.DuplicateKeys, plans[0].Plan.Checks[0].Kind);
        var fk = Assert.Single(plans[1].Plan.Checks);
        Assert.Equal(TableEditCheckKind.Orphans, fk.Kind);
        Assert.Equal(new DbObjectRef("SAMPLE", "ORDERS", "TABLE"), fk.ReferencedTable);
        Assert.Equal(["ORDER_ID"], fk.ReferencedColumns);
        Assert.Equal("QTY > 0 AND NOTE <> ')'", Assert.Single(plans[2].Plan.Checks).Condition);
        Assert.Equal(["CODE", "Mixed"], Assert.Single(plans[3].Plan.Checks).Columns);
    }

    [Fact]
    public void Novalidate_constraint_and_fk_without_columns_are_not_data_checked()
    {
        var plans = Plan(Dev,
            "ALTER TABLE SAMPLE.ORDERS ADD CONSTRAINT CK_QTY CHECK (QTY > 0) ENABLE NOVALIDATE",
            "ALTER TABLE SAMPLE.ORDER_ITEMS ADD FOREIGN KEY (ORDER_ID) REFERENCES SAMPLE.ORDERS");

        Assert.Empty(plans); // 운영이 아니면 잠금 ⚠ 도 없다
    }

    [Fact]
    public void Drops_are_irreversible_warnings_and_columns_and_tables_ask_dependents()
    {
        var plans = Plan(Dev,
            "ALTER TABLE SAMPLE.ORDERS DROP COLUMN NOTE",
            "ALTER TABLE SAMPLE.ORDERS DROP CONSTRAINT CK_QTY",
            "DROP TABLE SAMPLE.OLD_ORDERS PURGE",
            "DROP INDEX SAMPLE.IX_ORDERS_NOTE",
            "ALTER TABLE SAMPLE.ORDERS SET UNUSED (A, B)");

        Assert.All(plans, p => Assert.All(p.Plan.Findings, f => Assert.Equal((CheckSeverity.Warning, CheckCode.Irreversible), (f.Severity, f.Code))));
        Assert.Equal("#1 NOTE", Assert.Single(plans[0].Plan.Findings).Subject);
        Assert.Equal(TableEditCheckKind.Dependents, Assert.Single(plans[0].Plan.Checks).Kind);
        Assert.Empty(plans[1].Plan.Checks);
        Assert.Equal(TableEditCheckKind.Dependents, Assert.Single(plans[2].Plan.Checks).Kind);
        Assert.Equal("INDEX", plans[3].Table.ObjectType);
        Assert.Equal(2, plans[4].Plan.Findings.Count);
    }

    [Fact]
    public void Production_adds_one_large_table_check_per_locked_table()
    {
        var plans = Plan(Prod,
            "ALTER TABLE SAMPLE.ORDERS MODIFY (NOTE VARCHAR2(20))",
            "ALTER TABLE SAMPLE.ORDERS ADD CONSTRAINT CK_QTY CHECK (QTY > 0)",
            "ALTER TABLE SAMPLE.ORDERS DROP COLUMN OLD_NOTE");

        Assert.Single(plans.SelectMany(p => p.Plan.Checks), c => c.Kind == TableEditCheckKind.LargeTable);
        Assert.Contains(plans[0].Plan.Checks, c => c.Kind == TableEditCheckKind.LargeTable);
        Assert.DoesNotContain(plans[2].Plan.Checks, c => c.Kind == TableEditCheckKind.LargeTable);
    }

    [Theory]
    [InlineData("CREATE TABLE SAMPLE.T (A NUMBER)")]
    [InlineData("GRANT SELECT ON SAMPLE.ORDERS TO SAMPLE_APP")]
    [InlineData("COMMENT ON COLUMN SAMPLE.ORDERS.NOTE IS 'ALTER TABLE X MODIFY (A VARCHAR2(1) NOT NULL)'")]
    [InlineData("ALTER TABLE SAMPLE.ORDERS MODIFY CONSTRAINT PK_ORDERS DISABLE")]
    [InlineData("ALTER TABLE SAMPLE.ORDERS@OTHER_DB MODIFY (NOTE VARCHAR2(1))")]
    [InlineData("ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(10))")]
    [InlineData("ALTER TABLE SAMPLE.ORDERS MODIFY (NOTE VARCHAR2(10) DEFAULT 'it''s')")]
    [InlineData("ALTER TABLE SAMPLE.ORDERS MODIFY (NOTE DEFAULT 'unterminated)")]
    public void Statements_outside_the_parsed_range_or_without_checks_are_sent_without_a_plan(string statement)
    {
        var plans = Plan(Dev, statement);

        if (statement.Contains("it''s", StringComparison.Ordinal))
        {
            Assert.Equal(TableEditCheckKind.Overflow, Assert.Single(Assert.Single(plans).Plan.Checks).Kind); // 문자열 안 따옴표에 속지 않는다
            return;
        }

        Assert.Empty(plans);
    }

    [Fact]
    public void Comments_and_strings_do_not_fool_the_parser()
    {
        var p = Assert.Single(Plan(Dev, "/* MODIFY (X VARCHAR2(1) NOT NULL) */ ALTER TABLE \"Sample\".\"Orders\" -- DROP COLUMN Y\n MODIFY (\"Note\" VARCHAR2(5))"));

        Assert.Equal(new DbObjectRef("Sample", "Orders", "TABLE"), p.Table);
        Assert.Equal("Note", Assert.Single(p.Plan.Checks).Column);
    }
}
