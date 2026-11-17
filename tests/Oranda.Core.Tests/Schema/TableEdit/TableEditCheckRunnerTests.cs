using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Sessions;
using Xunit;
using static Oranda.Core.Tests.Schema.TableEdit.Samples;

namespace Oranda.Core.Tests.Schema.TableEdit;

/// <summary>검사 계획 돌리기(✖/⚠/✔ 판정, 시간 제한·실패 = 「확인 못 함」, 「그 행 보기」 SQL).</summary>
public sealed class TableEditCheckRunnerTests
{
    private static readonly TableEditCheckOptions Options = new() { Environment = ConnectionEnvironment.Production, Timeout = TimeSpan.FromMilliseconds(200) };

    private static Task<IReadOnlyList<CheckFinding>> Run(TableEditCheckPlan plan, FakeChecks checks, CancellationToken ct = default) =>
        TableEditCheckRunner.RunAsync(plan, OrdersRef, checks, null!, Options, ct);

    private static TableEditCheckPlan Plan(params TableEditCheck[] checks) => new([], checks);

    [Fact]
    public async Task Counts_above_zero_block_with_view_rows_sql_and_zero_passes()
    {
        var checks = new FakeChecks { Nulls = 3 };
        var plan = Plan(
            new TableEditCheck(TableEditCheckKind.Nulls, CheckCode.NullRows, "c9", "NOTE") { Column = "NOTE" },
            new TableEditCheck(TableEditCheckKind.DuplicateKeys, CheckCode.DuplicateKeys, "k1", "UK_X") { Columns = ["NOTE", "LEVEL"] });

        var findings = await Run(plan, checks);

        var nulls = findings[0];
        Assert.Equal((CheckSeverity.Block, CheckCode.NullRows, 3L), (nulls.Severity, nulls.Code, nulls.Count));
        Assert.Equal("SELECT * FROM SAMPLE.ORDERS t WHERE t.NOTE IS NULL", nulls.ViewRowsSql);
        Assert.Equal((CheckSeverity.Ok, CheckCode.DuplicateKeys, 0L), (findings[1].Severity, findings[1].Code, findings[1].Count));
        Assert.Null(findings[1].ViewRowsSql);
    }

    [Fact]
    public async Task Timeout_and_read_failure_are_not_checked_warnings()
    {
        var checks = new FakeChecks { Hang = true, Fail = "ORA-00942" };
        var plan = Plan(
            new TableEditCheck(TableEditCheckKind.Overflow, CheckCode.Overflow, "c9", "NOTE") { Column = "NOTE", Target = new ColumnDraft("c9", "NOTE") { DataType = "VARCHAR2", Length = 5 } },
            new TableEditCheck(TableEditCheckKind.CheckViolations, CheckCode.CheckViolations, "k2", "CK") { Condition = "1 = 0" });

        var findings = await Run(plan, checks);

        Assert.Equal([("c9", "timeout"), ("k2", "ORA-00942")], findings.Select(f => (f.RowKey, f.Detail)));
        Assert.All(findings, f => Assert.Equal((CheckSeverity.Warning, CheckCode.NotChecked), (f.Severity, f.Code)));
    }

    [Fact]
    public async Task User_cancel_is_thrown()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var plan = Plan(new TableEditCheck(TableEditCheckKind.Rows, CheckCode.NullRows, "n1", "CODE"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(plan, new FakeChecks { Hang = true }, cts.Token));
    }

    [Fact]
    public async Task Expression_names_dependents_and_table_size()
    {
        var checks = new FakeChecks { ExpressionError = "ORA-01476: divisor is equal to zero", ExistingName = "IX_ORDERS_09", NumRows = 2_000_000 };
        checks.DependentList.Add(new DbObjectRef("SAMPLE", "V_ORDERS", "VIEW"));
        var plan = new TableEditCheckPlan(
            [new CheckFinding(CheckSeverity.Warning, CheckCode.Irreversible, "c9") { Subject = "NOTE" }],
            [
                new TableEditCheck(TableEditCheckKind.LargeTable, CheckCode.LongLock, TableDraft.TableRowKey, "ORDERS"),
                new TableEditCheck(TableEditCheckKind.Expression, CheckCode.ExpressionFails, "c8", "TOTAL") { Expression = "AMOUNT / 0", Columns = ["AMOUNT"] },
                new TableEditCheck(TableEditCheckKind.NameExists, CheckCode.NameExists, "i9", "IX_ORDERS_09") { Name = "IX_ORDERS_09", NameKind = TableEditNameKind.Index },
                new TableEditCheck(TableEditCheckKind.NameExists, CheckCode.NameExists, "i8", "IX_ORDERS_08") { Name = "IX_ORDERS_08", NameKind = TableEditNameKind.Index },
                new TableEditCheck(TableEditCheckKind.Dependents, CheckCode.Irreversible, TableDraft.TableRowKey, "ORDERS"),
            ]);

        var findings = await Run(plan, checks);

        Assert.Equal(
            [
                (CheckSeverity.Block, CheckCode.ExpressionFails, "c8"),
                (CheckSeverity.Block, CheckCode.NameExists, "i9"),
                (CheckSeverity.Warning, CheckCode.Irreversible, "c9"),
                (CheckSeverity.Warning, CheckCode.LongLock, TableDraft.TableRowKey),
                (CheckSeverity.Warning, CheckCode.Irreversible, TableDraft.TableRowKey),
                (CheckSeverity.Ok, CheckCode.NameExists, "i8"),
            ],
            findings.Select(f => (f.Severity, f.Code, f.RowKey)));
        Assert.Equal("ORA-01476: divisor is equal to zero", findings[0].Detail);
        Assert.Equal(2_000_000L, findings[3].Count);
        Assert.Equal("V_ORDERS", Assert.Single(findings[4].Dependents).Name);
        Assert.Equal(5, checks.PreviewRows);
        Assert.Equal(["AMOUNT"], checks.PreviewColumns);
    }

    [Fact]
    public async Task Small_production_table_passes_the_size_check()
    {
        var findings = await Run(Plan(new TableEditCheck(TableEditCheckKind.LargeTable, CheckCode.LongLock, TableDraft.TableRowKey, "ORDERS")), new FakeChecks { NumRows = 10 });

        Assert.Equal(CheckSeverity.Ok, Assert.Single(findings).Severity);
    }

    [Fact]
    public async Task Planned_edit_runs_end_to_end()
    {
        var (original, edited) = Start();
        edited = edited.WithColumn("NOTE", c => c with { Length = 50, IsNullable = false });
        var plan = TableEditCheckPlanner.Plan(original, edited, Dialect, Options);

        var findings = await Run(plan, new FakeChecks { Overflow = 2, NumRows = 5 });

        Assert.Equal(
            [(CheckSeverity.Block, CheckCode.Overflow), (CheckSeverity.Ok, CheckCode.NullRows), (CheckSeverity.Ok, CheckCode.LongLock)],
            findings.Select(f => (f.Severity, f.Code)));
        Assert.Equal("SELECT * FROM SAMPLE.ORDERS t WHERE LENGTHB(t.NOTE) > 50", findings[0].ViewRowsSql);
    }

    [Fact]
    public void Check_sql_shapes()
    {
        var parent = new DbObjectRef("SAMPLE", "CUSTOMERS", "TABLE");

        Assert.Equal("LENGTH(t.NAME) > 10", TableEditCheckSql.OverflowWhere("NAME", new ColumnDraft("k", "NAME") { DataType = "VARCHAR2", Length = 10, CharSemantics = CharSemantics.Characters }));
        Assert.Equal("LENGTH(t.NAME) > 10", TableEditCheckSql.OverflowWhere("NAME", new ColumnDraft("k", "NAME") { DataType = "NVARCHAR2", Length = 10 }));
        Assert.Equal("UTL_RAW.LENGTH(t.R) > 4", TableEditCheckSql.OverflowWhere("R", new ColumnDraft("k", "R") { DataType = "RAW", Length = 4 }));
        Assert.Equal(
            "(t.A IS NOT NULL OR t.\"LEVEL\" IS NOT NULL) AND EXISTS (SELECT 1 FROM SAMPLE.ORDERS d"
            + " WHERE DECODE(d.A, t.A, 1, 0) = 1 AND DECODE(d.\"LEVEL\", t.\"LEVEL\", 1, 0) = 1 AND d.ROWID <> t.ROWID)",
            TableEditCheckSql.DuplicateWhere(OrdersRef, ["A", "LEVEL"]));
        Assert.Equal(
            "t.CUSTOMER_ID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM SAMPLE.CUSTOMERS p WHERE p.ID = t.CUSTOMER_ID)",
            TableEditCheckSql.OrphanWhere(["CUSTOMER_ID"], parent, ["ID"]));
        Assert.Equal("SELECT COUNT(*) FROM SAMPLE.ORDERS t WHERE NOT (AMOUNT >= 0)", TableEditCheckSql.CountSql(OrdersRef, TableEditCheckSql.CheckViolationWhere("AMOUNT >= 0")));
    }

    internal sealed class FakeChecks : ITableEditChecks
    {
        public long Nulls { get; init; }

        public long Overflow { get; init; }

        public long? NumRows { get; init; }

        public bool Hang { get; init; }

        public string? Fail { get; init; }

        public string? ExpressionError { get; init; }

        public string? ExistingName { get; init; }

        public List<DbObjectRef> DependentList { get; } = [];

        public int PreviewRows { get; private set; }

        public IReadOnlyList<string> PreviewColumns { get; private set; } = [];

        public Dbms Dbms => Dbms.Oracle;

        public async Task<long> CountRowsAsync(ISession s, DbObjectRef table, CancellationToken ct) => await Answer(0, ct);

        public Task<long?> NumRowsAsync(ISession s, DbObjectRef table, CancellationToken ct) => Task.FromResult(NumRows);

        public async Task<long> OverflowCountAsync(ISession s, DbObjectRef table, string column, ColumnDraft target, CancellationToken ct) => await Answer(Overflow, ct);

        public Task<long> NullCountAsync(ISession s, DbObjectRef table, string column, CancellationToken ct) => Task.FromResult(Nulls);

        public Task<long> NonNullCountAsync(ISession s, DbObjectRef table, string column, CancellationToken ct) => Task.FromResult(0L);

        public Task<long> DuplicateKeyCountAsync(ISession s, DbObjectRef table, IReadOnlyList<string> columns, CancellationToken ct) => Task.FromResult(0L);

        public Task<long> OrphanCountAsync(
            ISession s, DbObjectRef table, IReadOnlyList<string> columns, DbObjectRef referencedTable, IReadOnlyList<string> referencedColumns, CancellationToken ct) =>
            Task.FromResult(0L);

        public Task<long> CheckViolationCountAsync(ISession s, DbObjectRef table, string condition, CancellationToken ct) =>
            Fail is { } message ? throw new InvalidOperationException(message) : Task.FromResult(0L);

        public Task<ExpressionPreview> PreviewExpressionAsync(
            ISession s, DbObjectRef table, string expression, IReadOnlyList<string> columns, int rows, CancellationToken ct)
        {
            (PreviewRows, PreviewColumns) = (rows, columns);
            return Task.FromResult(new ExpressionPreview([], [], ExpressionError));
        }

        public Task<string?> ValidateExpressionAsync(ISession s, string owner, IReadOnlyList<ColumnDraft> columns, string expression, CancellationToken ct) =>
            Task.FromResult(ExpressionError);

        public Task<bool> NameExistsAsync(ISession s, string owner, string name, TableEditNameKind kind, DbObjectRef? table, CancellationToken ct) =>
            Task.FromResult(name == ExistingName);

        public Task<IReadOnlyList<DbObjectRef>> DependentsAsync(ISession s, DbObjectRef table, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DbObjectRef>>(DependentList);

        private async Task<long> Answer(long value, CancellationToken ct)
        {
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return value;
        }
    }
}
