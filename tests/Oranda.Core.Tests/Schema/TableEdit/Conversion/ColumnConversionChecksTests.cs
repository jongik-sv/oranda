using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Conversion;
using Oranda.Core.Sessions;
using Oranda.Core.Text;
using Xunit;
using static Oranda.Core.Tests.Schema.TableEdit.Samples;

namespace Oranda.Core.Tests.Schema.TableEdit.Conversion;

/// <summary>변환 검사 돌리기(✖ 변환 안 되는 행·참조 FK, ⚠ 확인 못 함·운영 큰 테이블)와 의뢰 스크립트의 변환 DML 예외(TE-44).</summary>
public sealed class ColumnConversionChecksTests
{
    private static readonly TableEditCheckOptions Options = new() { Environment = ConnectionEnvironment.Development, Timeout = TimeSpan.FromMilliseconds(200) };

    private static ColumnConversionRequest Request(string column = "CUSTOMER_NAME")
    {
        var (original, _) = Start();
        var source = original.Columns.Single(c => c.Name == column);
        var target = source with { DataType = "NUMBER", Length = null, CharSemantics = null, Precision = 12 };
        return new ColumnConversionRequest(original, source.RowKey, target, ColumnConversionDefaults.Expression(source, target));
    }

    private static Task<ColumnConversionCheckResult> Run(
        FakeConversionChecks conversions, ColumnConversionRequest? request = null, ColumnConversionMethod method = ColumnConversionMethod.ColumnSwap,
        TableEditCheckOptions? options = null, TableEditCheckRunnerTests.FakeChecks? checks = null) =>
        ColumnConversionCheckRunner.RunAsync(request ?? Request(), method, Dialect, conversions, checks ?? new TableEditCheckRunnerTests.FakeChecks { NumRows = 1_240_000 },
            null!, options ?? Options, CancellationToken.None);

    [Fact]
    public async Task Failing_rows_block_with_count_and_view_rows_sql()
    {
        var conversions = new FakeConversionChecks
        {
            Failures = new ConversionFailures(3) { ViewRowsSql = "SELECT * FROM SAMPLE.ORDERS t WHERE x", FirstError = "ORA-01722" },
        };

        var result = await Run(conversions);

        var fail = result.Findings[0];
        Assert.Equal((CheckSeverity.Block, CheckCode.ConversionFails, 3L, "ORA-01722"), (fail.Severity, fail.Code, fail.Count, fail.Detail));
        Assert.Equal("SELECT * FROM SAMPLE.ORDERS t WHERE x", fail.ViewRowsSql);
        Assert.True(result.IsBlocked);
        Assert.Equal(1_240_000, result.Rows);
        Assert.Equal(("TO_NUMBER(CUSTOMER_NAME)", "NUMBER"), (conversions.SeenExpression, conversions.SeenTarget!.DataType));
    }

    [Fact]
    public async Task No_failing_rows_pass_and_lower_bound_says_at_least()
    {
        var ok = await Run(new FakeConversionChecks());
        var atLeast = await Run(new FakeConversionChecks { Failures = new ConversionFailures(1) { IsLowerBound = true, FirstError = "ORA-01722" } });

        Assert.Contains(ok.Findings, f => (f.Severity, f.Code, f.Count) == (CheckSeverity.Ok, CheckCode.ConversionFails, 0L));
        Assert.False(ok.IsBlocked);
        Assert.Contains(atLeast.Findings, f => (f.Severity, f.Code, f.Detail) == (CheckSeverity.Block, CheckCode.ConversionFails, "atLeast"));
    }

    [Fact]
    public async Task Referencing_foreign_keys_block_the_swap_only()
    {
        var conversions = new FakeConversionChecks { ForeignKeys = [new ForeignKeyRef("SAMPLE", "ORDER_ITEMS", "FK_ITEMS_01")] };

        var swap = await Run(conversions);
        var rebuild = await Run(conversions, method: ColumnConversionMethod.Rebuild);

        Assert.Contains(swap.Findings, f => (f.Severity, f.Detail) == (CheckSeverity.Block, "referencedByForeignKey:SAMPLE.ORDER_ITEMS.FK_ITEMS_01"));
        Assert.DoesNotContain(rebuild.Findings, f => f.Detail?.StartsWith("referencedByForeignKey", StringComparison.Ordinal) == true);
        Assert.Equal(1, conversions.ForeignKeyCalls);
    }

    [Fact]
    public async Task Timeout_is_not_checked_and_unsupported_skips_server_reads()
    {
        var hang = await Run(new FakeConversionChecks { Hang = true });
        var dialect = new FakeTableEditDialect();
        dialect.Unsupported[TableEditFeature.ConvertingTypeChange] = "TableEdit.unsupported.x";
        var conversions = new FakeConversionChecks();

        var unsupported = await ColumnConversionCheckRunner.RunAsync(Request(), ColumnConversionMethod.ColumnSwap, dialect, conversions,
            new TableEditCheckRunnerTests.FakeChecks(), null!, Options, CancellationToken.None);

        Assert.Contains(hang.Findings, f => (f.Severity, f.Code, f.Detail) == (CheckSeverity.Warning, CheckCode.NotChecked, "timeout"));
        Assert.Contains(unsupported.Findings, f => f.Code == CheckCode.Unsupported);
        Assert.Equal((0, 0), (conversions.FailureCalls, conversions.ForeignKeyCalls));
    }

    [Fact]
    public async Task Production_large_table_warns_long_lock()
    {
        var production = Options with { Environment = ConnectionEnvironment.Production };

        var large = await Run(new FakeConversionChecks(), options: production);
        var small = await Run(new FakeConversionChecks(), options: production, checks: new TableEditCheckRunnerTests.FakeChecks { NumRows = 10 });

        Assert.Contains(large.Findings, f => (f.Severity, f.Code, f.Count) == (CheckSeverity.Warning, CheckCode.LongLock, 1_240_000L));
        Assert.DoesNotContain(small.Findings, f => f.Code == CheckCode.LongLock);
    }

    [Fact]
    public async Task Restored_check_and_keys_are_counted_with_the_converted_value()
    {
        // STATUS: CK_ORDERS_01 CHECK (status in ('N','Y')). CUSTOMER_ID: 서버 이름 UK SYS_C0013 (CUSTOMER_ID, CREATED) + FK
        var (original, _) = Start();
        var status = original.Columns.Single(c => c.Name == "STATUS");
        var statusRequest = new ColumnConversionRequest(original, status.RowKey, status with { DataType = "NUMBER", Length = null, CharSemantics = null }, "1");
        var customer = original.Columns.Single(c => c.Name == "CUSTOMER_ID");
        var customerRequest = new ColumnConversionRequest(original, customer.RowKey, customer with { Precision = 5 }, "CAST(CUSTOMER_ID AS NUMBER(5))");
        var conversions = new FakeConversionChecks { CheckViolations = 2, DuplicateKeys = 4 };

        var statusResult = await Run(conversions, statusRequest);
        var customerResult = await Run(conversions, customerRequest);

        Assert.Contains(statusResult.Findings, f => (f.Severity, f.Code, f.Subject, f.Count, f.Detail) == (CheckSeverity.Block, CheckCode.CheckViolations, "CK_ORDERS_01", 2L, "afterConversion"));
        Assert.Contains(customerResult.Findings, f => (f.Severity, f.Code, f.Subject, f.Count) == (CheckSeverity.Block, CheckCode.DuplicateKeys, "SYS_C0013", 4L));
        Assert.Contains("CK STATUS [ORDER_ID,CUSTOMER_ID,CUSTOMER_NAME,AMOUNT,QTY,CREATED,TOTAL,NOTE] status in ('N','Y')", conversions.Seen);
        Assert.Contains("KEY CUSTOMER_ID (CUSTOMER_ID,CREATED)", conversions.Seen);
        Assert.DoesNotContain(conversions.Seen, x => x.Contains("FK_ORDERS_01", StringComparison.Ordinal)); // FK 는 형식 비교(서버 없는 판정) 몫
    }

    [Fact]
    public async Task Checks_use_the_resolved_char_semantics_and_warn_about_foreign_keys_in_other_schemas()
    {
        var (original, _) = Start();
        var name = original.Columns.Single(c => c.Name == "CUSTOMER_NAME"); // VARCHAR2(50 CHAR)
        var request = new ColumnConversionRequest(original, name.RowKey, name with { Length = 20, CharSemantics = null }, "CUSTOMER_NAME");
        var conversions = new FakeConversionChecks();
        var noDictionary = Options with
        {
            Server = new ServerInfo("19.0.0.0.0", null, null, null, null, null, false, 1, new ServerCapabilities(false, false, false, false, false)),
        };

        var result = await Run(conversions, request, options: noDictionary);

        Assert.Equal(CharSemantics.Characters, conversions.SeenTarget!.CharSemantics); // 실행(1단계 ADD)과 같은 기준
        Assert.Contains(result.Findings, f => (f.Severity, f.Code, f.Detail) == (CheckSeverity.Warning, CheckCode.NotChecked, "foreignKeysInOtherSchemas"));
    }

    // ---------- 의뢰 스크립트 예외(TE-44) ----------

    private static readonly RequestScriptHeader Header = new("ORDERS 형식 변환", new DateOnly(2026, 11, 17), Dbms.Oracle);

    private static SqlStatement Of(string text, StatementKind kind) => new(text, default(TextRange), kind);

    [Fact]
    public void Request_script_rejects_dml_unless_conversion_dml_is_allowed()
    {
        SqlStatement[] statements = [Of("UPDATE SAMPLE.ORDERS SET C$1 = TO_NUMBER(C)", StatementKind.Dml)];

        Assert.Throws<InvalidOperationException>(() => RequestScriptWriter.Write(Header, statements, "\n"));
        Assert.Throws<InvalidOperationException>(() =>
            RequestScriptWriter.Write(Header with { AllowsConversionDml = true }, [Of("DELETE FROM SAMPLE.ORDERS", StatementKind.Dml)], "\n"));
        Assert.Throws<InvalidOperationException>(() => RequestScriptWriter.Write(Header, [Of("COMMIT", StatementKind.TransactionControl)], "\n"));
        Assert.Throws<InvalidOperationException>(() => // 임시 열(<열>$N)에 쓰는 변환 UPDATE 가 아니면 막는다
            RequestScriptWriter.Write(Header with { AllowsConversionDml = true }, [Of("UPDATE SAMPLE.ORDERS SET STATUS = 'Y'", StatementKind.Dml)], "\n"));
    }

    [Fact]
    public void Conversion_dml_gets_the_header_mark_and_one_commit()
    {
        var header = Header with { AllowsConversionDml = true };
        var plan = ColumnConversionBuilder.BuildSwap(Request(), Dialect, null);

        var withCommit = RequestScriptWriter.Write(header, plan.Statements, "\n");
        var withoutCommit = RequestScriptWriter.Write(header, [.. plan.Statements.Where(s => s.Kind != StatementKind.TransactionControl)], "\n");
        var ddlOnly = RequestScriptWriter.Write(header, [Of("ALTER TABLE SAMPLE.ORDERS DROP COLUMN X", StatementKind.Ddl)], "\n");

        Assert.Contains("\n" + ColumnConversionBuilder.DmlHeader + "\n", withCommit, StringComparison.Ordinal);
        Assert.Contains("UPDATE SAMPLE.ORDERS SET CUSTOMER_NAME$1 = TO_NUMBER(CUSTOMER_NAME);\n\nCOMMIT;\n", withCommit, StringComparison.Ordinal);
        Assert.Equal(1, withCommit.Split("COMMIT;").Length - 1);
        Assert.Equal(withCommit, withoutCommit); // COMMIT 이 없으면 붙여서 같은 글
        Assert.DoesNotContain("DML", ddlOnly, StringComparison.Ordinal);
    }

    internal sealed class FakeConversionChecks : IColumnConversionChecks
    {
        public ConversionFailures Failures { get; init; } = ConversionFailures.None;

        public IReadOnlyList<ForeignKeyRef> ForeignKeys { get; init; } = [];

        public bool Hang { get; init; }

        public int FailureCalls { get; private set; }

        public int ForeignKeyCalls { get; private set; }

        public string? SeenExpression { get; private set; }

        public ColumnDraft? SeenTarget { get; private set; }

        public Dbms Dbms => Dbms.Oracle;

        public async Task<ConversionFailures> FailingRowsAsync(
            ISession s, DbObjectRef table, ColumnDraft source, ColumnDraft target, string expression, CancellationToken ct)
        {
            FailureCalls++;
            (SeenExpression, SeenTarget) = (expression, target);
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return Failures;
        }

        public Task<IReadOnlyList<ForeignKeyRef>> ReferencingForeignKeysAsync(ISession s, DbObjectRef table, string column, CancellationToken ct)
        {
            ForeignKeyCalls++;
            return Task.FromResult(ForeignKeys);
        }

        public long CheckViolations { get; init; }

        public long DuplicateKeys { get; init; }

        public List<string> Seen { get; } = [];

        public Task<ConversionFailures> ConvertedCheckViolationsAsync(
            ISession s, DbObjectRef table, IReadOnlyList<string> otherColumns, string column, string expression, string condition, CancellationToken ct)
        {
            Seen.Add($"CK {column} [{string.Join(",", otherColumns)}] {condition}");
            return Task.FromResult(new ConversionFailures(CheckViolations));
        }

        public Task<ConversionFailures> ConvertedDuplicateKeysAsync(
            ISession s, DbObjectRef table, IReadOnlyList<string> otherColumns, string column, string expression, IReadOnlyList<string> keyColumns, CancellationToken ct)
        {
            Seen.Add($"KEY {column} ({string.Join(",", keyColumns)})");
            return Task.FromResult(new ConversionFailures(DuplicateKeys));
        }
    }
}
