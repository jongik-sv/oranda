using System.Text;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer;
using Oranda.Testing;

namespace Oranda.Core.Tests.Transfer;

/// <summary>
/// Core <see cref="DataTransferService"/> 가 방언 가짜(<see cref="IDataTransferDialect"/>)로 가져오기 문장을 만든다 — Oracle 문법(DUAL)이 Core 에 남아 있지 않다는 증거.
/// 같은 코드가 Tibero 방언으로도 돈다.
/// </summary>
public sealed class DataTransferServiceTests
{
    private static readonly DbObjectRef Target = new("SCOTT", "EMP", "TABLE");

    private static readonly TableInfo Emp = new(Target,
    [
        new ColumnInfo("ID", 1, "NUMBER"),
        new ColumnInfo("NAME", 2, "VARCHAR2"),
    ]);

    private static (DataTransferService Service, FakeExecutionService Exec, FakeSession Session) Create()
    {
        var exec = new FakeExecutionService();
        var meta = new FakeMetadataProvider();
        meta.Tables[Target] = Emp;
        return (new DataTransferService(exec, meta, new FakeDialect(), new FakeConverter()), exec, new FakeSession());
    }

    private static ImportOptions Options(int batchSize) =>
        new(TransferFormat.Csv, Target, [new ColumnMapping("id", "ID"), new ColumnMapping("name", "NAME")]) { BatchSize = batchSize };

    private static MemoryStream Csv(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Batch_goes_out_as_one_insert_made_of_dialect_row_selects_joined_by_union_all()
    {
        var (service, exec, session) = Create();

        var summary = await service.ImportAsync(session, Options(batchSize: 3), Csv("id,name\n1,Kim\n2,Lee\n3,Park\n"), null, CancellationToken.None);

        Assert.Equal(3, summary.Rows);
        Assert.Empty(summary.Warnings);
        Assert.Equal("MARK ORANDA_IMPORT", exec.Calls[0].Statement.Text); // 방언의 세이브포인트 문장
        var insert = exec.Calls[1].Statement.Text;
        Assert.StartsWith("INSERT INTO ", insert, StringComparison.Ordinal);
        Assert.EndsWith(") ROW[1, 'Kim'] UNION ALL ROW[2, 'Lee'] UNION ALL ROW[3, 'Park']", insert, StringComparison.Ordinal);
        Assert.DoesNotContain("DUAL", insert, StringComparison.Ordinal);
        Assert.Equal(2, exec.Calls.Count);
        Assert.Equal(1, session.CommitCount);
    }

    [Fact]
    public async Task A_single_row_batch_uses_values_and_a_failed_import_rolls_back_with_the_dialect_statement()
    {
        var (service, exec, session) = Create();
        exec.Responder = call => call.Statement.Text.StartsWith("INSERT", StringComparison.Ordinal)
            ? throw new IOException("boom")
            : new Oranda.Core.Execution.ExecutionResult(call.Statement, Oranda.Core.Execution.ExecutionStatus.Succeeded);
        session.HasPendingTransaction = true;

        await Assert.ThrowsAsync<IOException>(() => service.ImportAsync(session, Options(batchSize: 1), Csv("id,name\n1,Kim\n"), null, CancellationToken.None));

        Assert.Contains(exec.Calls, c => c.Statement.Text.Contains(" VALUES (1, 'Kim')", StringComparison.Ordinal));
        Assert.Equal("BACK ORANDA_IMPORT", exec.Calls[^1].Statement.Text);
    }

    private sealed class FakeDialect : IDataTransferDialect
    {
        public string SelectRow(IReadOnlyList<string> literals) => "ROW[" + string.Join(", ", literals) + "]";

        public string SavepointStatement(string name) => "MARK " + name;

        public string RollbackToSavepointStatement(string name) => "BACK " + name;
    }

    private sealed class FakeConverter : IImportValueConverter
    {
        public ColumnDataKind KindOf(string dataType) => dataType == "NUMBER" ? ColumnDataKind.Number : ColumnDataKind.Text;

        public string ToLiteral(ColumnInfo column, object? cell) =>
            cell is string s && column.DataType != "NUMBER" ? "'" + s + "'" : Convert.ToString(cell, System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";

        public bool TryConvert(ColumnInfo column, object? cell, out string literal, out string? reason)
        {
            literal = ToLiteral(column, cell);
            reason = null;
            return true;
        }
    }
}
