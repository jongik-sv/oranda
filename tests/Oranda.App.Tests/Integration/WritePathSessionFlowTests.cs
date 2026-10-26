using System.Text;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Transfer;
using Xunit.Abstractions;

namespace Oranda.App.Tests.Integration;

/// <summary>
/// TSK-14-08(D-040) 쓰기 경로의 트랜잭션 경계를 실제 서버에서 본다: 가져오기 「마지막에 커밋」 이 실패·취소되면 시작 지점(SAVEPOINT)까지만 되돌려
/// 그 세션의 다른 미커밋 변경은 남기고, 세션을 닫을 때는 닫기 전에 명시적으로 ROLLBACK 해 로그오프가 미커밋 변경을 확정하지 않는다.
/// 표 이름은 SWP_ 로 시작한다(다른 레인의 통합 시험과 겹치지 않게).
/// </summary>
[Trait("Category", "Integration")]
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class WritePathSessionFlowTests(ItDb db, ITestOutputHelper output) : IClassFixture<ItDb>
{
    private static string NewTable() => "SWP_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static async Task<string> CountAsync(FlowRig rig, string table) =>
        await FlowRig.CellAsync((await rig.RunSqlAsync($"select count(*) from {table}")).Selected!);

    private sealed class CancelAfter(CancellationTokenSource cts, long rows) : IProgress<long>
    {
        public void Report(long value)
        {
            if (value >= rows)
            {
                cts.Cancel();
            }
        }
    }

    [Fact]
    public Task Cancelled_import_with_commit_at_end_rolls_back_to_its_savepoint_and_keeps_earlier_uncommitted_work() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        var keep = NewTable();
        var dst = NewTable();
        db.Exec($"create table {keep} (id number)");
        db.Exec($"create table {dst} (id number(5))");
        using var rig = new FlowRig(db);
        try
        {
            await rig.ConnectAsync();
            await rig.RunSqlAsync($"insert into {keep} values (42)"); // 가져오기 전에 이 세션에 남은 미커밋 변경
            var session = rig.Session;
            Assert.True(session.HasPendingTransaction);

            var service = rig.Container.GetRequiredService<IDataTransferService>();
            var csv = "ID\n" + string.Join('\n', Enumerable.Range(1, 10)) + "\n";
            using var cts = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportAsync(session,
                new ImportOptions(TransferFormat.Csv, new Oranda.Core.Metadata.DbObjectRef(db.User, dst, "TABLE"), [new ColumnMapping("ID", "ID")])
                {
                    BatchSize = 2,
                    CommitAtEnd = true,
                },
                new MemoryStream(Encoding.UTF8.GetBytes(csv)), new CancelAfter(cts, 4), cts.Token));

            var kept = await CountAsync(rig, keep);
            var imported = await CountAsync(rig, dst);
            output.WriteLine($"{db.Service}: 앞 미커밋 {kept}행, 가져온 표 {imported}행, 미커밋={session.HasPendingTransaction}");
            Assert.Equal("1", kept); // 세션 전체 ROLLBACK 이 아니다
            Assert.Equal("0", imported); // 가져오기 몫만 되돌렸다
            Assert.True(session.HasPendingTransaction);
            Assert.Equal("0", Convert.ToString(db.Scalar($"select count(*) from {keep}"), System.Globalization.CultureInfo.InvariantCulture)); // 커밋하지도 않았다
            await rig.Controller.RollbackAsync(session);
        }
        finally
        {
            db.Exec($"drop table {keep} purge");
            db.Exec($"drop table {dst} purge");
        }
    });

    [Fact]
    public Task Closing_a_session_with_uncommitted_changes_does_not_commit_them() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        var t = NewTable();
        db.Exec($"create table {t} (id number)");
        using var rig = new FlowRig(db);
        try
        {
            await rig.ConnectAsync();
            await rig.RunSqlAsync($"insert into {t} values (1)");
            var session = rig.Session;
            Assert.True(session.HasPendingTransaction);

            await rig.Service.DisconnectAsync(session, default); // 사용자가 「롤백」 을 골랐거나 미커밋이 없다고 본 닫기와 같은 경로

            var rows = Convert.ToString(db.Scalar($"select count(*) from {t}"), System.Globalization.CultureInfo.InvariantCulture);
            output.WriteLine($"{db.Service}: 닫은 뒤 남은 행 {rows}");
            Assert.Equal("0", rows);
        }
        finally
        {
            db.Exec($"drop table {t} purge");
        }
    });
}
