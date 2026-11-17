using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Xunit;
using static Oranda.Core.Tests.Schema.TableEdit.Samples;

namespace Oranda.Core.Tests.Schema.TableEdit;

/// <summary>T1b 리뷰에서 나온 경우(다시 만드는 열의 제약·인덱스, 이름 바꾼 열의 식, 형식 판정, 지우는 열을 쓰는 식, 2차 속성 막기).</summary>
public sealed class TableEditReviewFixTests
{
    private const string T = "ALTER TABLE SAMPLE.ORDERS ";
    private static readonly TableEditCheckOptions Dev = new() { Environment = ConnectionEnvironment.Development };

    private static TableInfo WithTotalIndexes() => Orders() with
    {
        Indexes =
        [
            .. Orders().Indexes,
            new IndexInfo("IX_ORDERS_05", false, ["TOTAL"]) { IndexType = "NORMAL" },
        ],
        Constraints = [.. Orders().Constraints, new ConstraintInfo("UK_ORDERS_02", ConstraintKind.Unique, ["ORDER_ID", "TOTAL"])],
    };

    [Fact]
    public void Constraints_and_indexes_on_a_recreated_column_are_recreated_around_it()
    {
        var dialect = new FakeTableEditDialect { SupportsVirtualColumnModify = false };
        var (original, edited) = Start(WithTotalIndexes(), dialect);
        edited = edited.WithColumn("TOTAL", c => c with { VirtualExpression = "AMOUNT * QTY * 2" });

        Assert.Equal(
            [
                T + "DROP CONSTRAINT UK_ORDERS_02",
                "DROP INDEX SAMPLE.IX_ORDERS_05",
                T + "DROP COLUMN TOTAL",
                T + "ADD (TOTAL NUMBER AS (AMOUNT * QTY * 2) VIRTUAL)",
                T + "ADD CONSTRAINT UK_ORDERS_02 UNIQUE (ORDER_ID, TOTAL)",
                "CREATE INDEX SAMPLE.IX_ORDERS_05 ON SAMPLE.ORDERS (TOTAL)",
            ],
            Build(original, edited, dialect));
    }

    [Fact]
    public void Recreated_function_index_and_check_use_the_renamed_column()
    {
        var (original, edited) = Start();
        edited = edited
            .WithColumn("NOTE", c => c with { Name = "MEMO" })
            .WithColumn("STATUS", c => c with { Name = "STATE" })
            .WithIndex("IX_ORDERS_02", i => i with { IsUnique = true })
            .WithConstraint("CK_ORDERS_01", c => c with { IsEnabled = true, ColumnKeys = [] });

        Assert.Equal(
            [
                T + "DROP CONSTRAINT CK_ORDERS_01",
                "DROP INDEX SAMPLE.IX_ORDERS_02",
                T + "RENAME COLUMN STATUS TO STATE",
                T + "RENAME COLUMN NOTE TO MEMO",
                T + "ADD CONSTRAINT CK_ORDERS_01 CHECK (STATE in ('N','Y'))",
                "CREATE UNIQUE INDEX SAMPLE.IX_ORDERS_02 ON SAMPLE.ORDERS (UPPER(\"MEMO\"))",
            ],
            Build(original, edited));
    }

    [Fact]
    public void Rename_names_keeps_literals_qualified_names_and_quotes()
    {
        var renames = new Dictionary<string, string> { ["A"] = "B", ["B"] = "A", ["X"] = "Mixed" };

        Assert.Equal("B + \"A\" + 'a' + s.a + X_1", TableEditExpression.RenameNames("a + \"B\" + 'a' + s.a + X_1", renames));
        Assert.Equal("\"Mixed\" * 2", TableEditExpression.RenameNames("x * 2", renames));
    }

    [Fact]
    public void Timestamp_fraction_and_interval_precision_shrink_need_an_empty_column()
    {
        var table = Orders() with
        {
            Columns =
            [
                .. Orders().Columns,
                new ColumnInfo("TS", 10, "TIMESTAMP(6)"),
                new ColumnInfo("IV", 11, "INTERVAL DAY(2) TO SECOND(6)"),
                new ColumnInfo("D", 12, "DATE"),
            ],
        };
        var (original, edited) = Start(table);
        edited = edited
            .WithColumn("TS", c => c with { DataType = "TIMESTAMP(3)" })
            .WithColumn("IV", c => c with { DataType = "INTERVAL DAY(1) TO SECOND(6)" })
            .WithColumn("D", c => c with { DataType = "TIMESTAMP" });

        var checks = TableEditCheckPlanner.Plan(original, edited, Dialect, Dev).Checks;

        Assert.Equal(["IV", "TS"], checks.Where(c => c.Kind == TableEditCheckKind.NonNull).Select(c => c.Column).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(checks, c => c.Column == "D");
    }

    [Fact]
    public void Char_varchar_switch_needs_the_same_size_or_an_empty_column()
    {
        var (original, edited) = Start();

        Assert.Empty(TableEditCheckPlanner.Plan(original, edited.WithColumn("STATUS", c => c with { DataType = "VARCHAR2" }), Dialect, Dev).Checks);
        Assert.Equal(TableEditCheckKind.NonNull,
            Assert.Single(TableEditCheckPlanner.Plan(original, edited.WithColumn("STATUS", c => c with { DataType = "VARCHAR2", Length = 5 }), Dialect, Dev).Checks).Kind);
    }

    [Fact]
    public void Expression_or_check_on_a_dropped_column_is_blocked()
    {
        var (original, edited) = Start();
        edited = edited
            .WithColumn("NOTE", c => c with { IsDropped = true })
            .AddColumn(new ColumnDraft("ntax", "TAX") { VirtualExpression = "LENGTH(note)" })
            .AddConstraint(new ConstraintDraft("nck", "CK_ORDERS_02", ConstraintKind.Check) { Condition = "NOTE IS NOT NULL" });

        var blocked = TableEditCheckPlanner.Plan(original, edited, Dialect, Dev).Findings.Where(f => f.Code == CheckCode.InvalidDefinition).ToList();

        Assert.Equal([("ntax", "droppedColumn:NOTE"), ("nck", "droppedColumn:NOTE")], blocked.Select(f => (f.RowKey, f.Detail)));
        Assert.All(blocked, f => Assert.Equal(CheckSeverity.Block, f.Severity));
    }

    [Fact]
    public void Virtual_to_plain_not_null_without_default_needs_an_empty_table()
    {
        var (original, edited) = Start();
        edited = edited.WithColumn("TOTAL", c => c with { VirtualExpression = null, IsNullable = false });

        Assert.Contains(TableEditCheckPlanner.Plan(original, edited, Dialect, Dev).Checks, c => c is { Kind: TableEditCheckKind.Rows, Subject: "TOTAL" });
    }

    [Fact]
    public void Phase_two_attributes_are_blocked_until_their_statements_exist()
    {
        var (original, edited) = Start();
        edited = (edited with { Name = "SALES" })
            .WithColumn("NOTE", c => c with { IsInvisible = true })
            .AddColumn(new ColumnDraft("nseq", "SEQ") { DataType = "NUMBER", Identity = new IdentityInfo(IdentityGeneration.Always) });

        var blocked = TableEditCheckPlanner.Plan(original, edited, Dialect, Dev).Findings.Where(f => f.Detail == TableEditCheckPlanner.NotYetReasonKey).ToList();

        Assert.Equal([TableDraft.TableRowKey, original.KeyOf("NOTE"), "nseq"], blocked.Select(f => f.RowKey));
        Assert.Single(TableEditCheckPlanner.Plan(TableDraftFactory.Empty("SAMPLE"),
            (TableDraftFactory.Empty("SAMPLE") with { Name = "T1", Kind = TableKind.Temporary }).AddColumn(new ColumnDraft("c1", "ID") { DataType = "NUMBER" }),
            Dialect, Dev).Findings, f => f.Detail == TableEditCheckPlanner.NotYetReasonKey);
    }

    [Fact]
    public void Missing_char_used_is_guessed_from_byte_length()
    {
        var table = Orders() with
        {
            Columns =
            [
                new ColumnInfo("A", 1, "VARCHAR2") { Length = 10, ByteLength = 40 },
                new ColumnInfo("B", 2, "VARCHAR2") { Length = 10, ByteLength = 10 },
                new ColumnInfo("C", 3, "VARCHAR2") { Length = 10 },
            ],
            Constraints = [],
            Indexes = [],
        };
        var (original, edited) = Start(table);

        Assert.Equal([CharSemantics.Characters, CharSemantics.Bytes, null], original.Columns.Select(c => c.CharSemantics));
        Assert.Equal([T + "MODIFY (A VARCHAR2(20 CHAR))"], Build(original, edited.WithColumn("A", c => c with { Length = 20 })));
    }

    [Fact]
    public async Task Production_table_without_statistics_warns_about_size()
    {
        var plan = new TableEditCheckPlan([], [new TableEditCheck(TableEditCheckKind.LargeTable, CheckCode.LongLock, TableDraft.TableRowKey, "ORDERS")]);

        var findings = await TableEditCheckRunner.RunAsync(plan, OrdersRef, new TableEditCheckRunnerTests.FakeChecks { NumRows = null }, null!, Dev, default);

        Assert.Equal((CheckSeverity.Warning, "unknown"), (Assert.Single(findings).Severity, findings[0].Detail));
    }
}
