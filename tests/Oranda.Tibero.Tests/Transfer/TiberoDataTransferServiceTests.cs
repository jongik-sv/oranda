using System.Text;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer;
using Oranda.Testing;
using Oranda.Tibero.Transfer;

namespace Oranda.Tibero.Tests.Transfer;

/// <summary>Tibero 내보내기·가져오기 서비스(TB5): Core 본문 + Tibero 방언·변환기. 가짜 실행 서비스로 보낸 문장을 확인한다(서버 없음).</summary>
public sealed class TiberoDataTransferServiceTests
{
    private static readonly DbObjectRef Target = new("SCOTT", "EMP", "TABLE");

    private static readonly TableInfo Emp = new(Target,
    [
        new ColumnInfo("ID", 1, "NUMBER"),
        new ColumnInfo("NAME", 2, "VARCHAR"),
        new ColumnInfo("HIRED", 3, "DATE"),
    ]);

    [Fact]
    public void Dialect_makes_dual_selects_and_oracle_style_savepoints()
    {
        var dialect = TiberoDataTransferDialect.Instance;

        Assert.Equal("SELECT 1, 'a' FROM DUAL", dialect.SelectRow(["1", "'a'"]));
        Assert.Equal("SAVEPOINT ORANDA_IMPORT", dialect.SavepointStatement("ORANDA_IMPORT"));
        Assert.Equal("ROLLBACK TO SAVEPOINT ORANDA_IMPORT", dialect.RollbackToSavepointStatement("ORANDA_IMPORT"));
    }

    [Fact]
    public async Task Import_batch_is_one_insert_of_tibero_typed_literals_joined_by_union_all()
    {
        var exec = new FakeExecutionService();
        var meta = new FakeMetadataProvider();
        meta.Tables[Target] = Emp;
        var service = new TiberoDataTransferService(exec, meta);
        var options = new ImportOptions(TransferFormat.Csv, Target,
            [new ColumnMapping("id", "ID"), new ColumnMapping("name", "NAME"), new ColumnMapping("hired", "HIRED")]) { BatchSize = 2 };
        using var csv = new MemoryStream(Encoding.UTF8.GetBytes("id,name,hired\n1,O'Neil,2026-03-05\n2,,2026-03-06 07:08:09\n"));

        var summary = await service.ImportAsync(new FakeSession(), options, csv, null, CancellationToken.None);

        Assert.Equal(2, summary.Rows);
        Assert.Equal("SAVEPOINT ORANDA_IMPORT", exec.Calls[0].Statement.Text);
        Assert.EndsWith(
            ") SELECT 1, 'O''Neil', TO_DATE('2026-03-05 00:00:00', 'YYYY-MM-DD HH24:MI:SS') FROM DUAL" +
            " UNION ALL SELECT 2, NULL, TO_DATE('2026-03-06 07:08:09', 'YYYY-MM-DD HH24:MI:SS') FROM DUAL",
            exec.Calls[1].Statement.Text, StringComparison.Ordinal);
    }
}
