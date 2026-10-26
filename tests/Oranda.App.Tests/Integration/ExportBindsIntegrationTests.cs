using System.Text;
using Oranda.App.Features.DataTransfer;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer;
using Xunit.Abstractions;

namespace Oranda.App.Tests.Integration;

/// <summary>
/// TSK-12-03: 바인드 변수가 있는 질의의 결과를 서버에서 다시 읽는 내보내기(CSV·xlsx)·전체 행 수 세기·서버 거르기가
/// 결과를 만들 때 쓴 값으로 실제 Oracle 에서 같은 행을 돌려주는지. 고치기 전에는 내보내기가 빈 바인드로 질의를 보내
/// 「바인드 변수 :ID 의 값이 없습니다」 로 실패했다.
/// </summary>
[Trait("Category", "Integration")]
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class ExportBindsIntegrationTests(ItDb db, ITestOutputHelper output) : IClassFixture<ItDb>
{
    private static string Sql(string table) =>
        $"select id, name, d from {table} where id >= :id and name like :nm and d >= :d and (:n is null or id <> :n) order by id";

    private static string NewTable() => "T_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static void ResetStatics()
    {
        ResultGridCommands.Active = null;
        ResultGridCommands.Filter = null;
        ResultGridCommands.CountFiltered = null;
        ResultGridCommands.CountRows = null;
    }

    private static async Task<string> SeedAsync(FlowRig rig)
    {
        var t = NewTable();
        await rig.RunSqlAsync(
            $"create table {t} (id number primary key, name varchar2(40), d date);\n" +
            $"insert into {t} values (1, 'alpha', date '2026-01-01');\n" +
            $"insert into {t} values (2, 'alps', date '2026-02-01');\n" +
            $"insert into {t} values (3, 'beta', date '2026-03-01');\n" +
            $"insert into {t} values (4, 'alpine', date '2026-04-01');\n" +
            $"insert into {t} values (5, 'gamma', date '2026-05-01');\n" +
            $"insert into {t} values (6, null, date '2026-06-01');\ncommit;",
            "oranda.exec.runScript");
        return t;
    }

    [Fact]
    public Task Reexport_count_and_server_filter_use_the_bind_values_the_result_was_made_with() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        ResetStatics();
        using var rig = new FlowRig(db);
        await rig.ConnectAsync();
        var table = await SeedAsync(rig);
        var dir = Path.Combine(Path.GetTempPath(), "oranda-itest-expbind-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // 바인드 값을 넣고 실행한다: id >= 2, name like 'al%', d >= 2026-02-01, :n 은 NULL → 2·4번 행.
            var tab = rig.Host.NewDocument(Sql(table));
            tab.Select(0, 0);
            await rig.Run("oranda.exec.runStatement"); // 처음 보는 이름은 목록에 더하고 실행을 미룬다
            var id = tab.Binds.Find("ID")!;
            id.Type = BindDataType.Number;
            id.Text = "2";
            tab.Binds.Find("NM")!.Text = "al%";
            var d = tab.Binds.Find("D")!;
            d.Type = BindDataType.Date;
            d.Text = "2026-02-01";
            tab.Binds.Find("N")!.Type = BindDataType.Number;
            await rig.Run("oranda.exec.runStatement");

            var results = (ResultTabsViewModel)tab.ResultPane!;
            var pane = results.Selected!.Pane!;
            await pane.Model!.LoadFirstPageAsync();
            Assert.Equal(2, pane.Model.LoadedRowCount);

            // 고치기 전 동작 재현: 빈 바인드로 서버에 다시 물으면 서버 쪽 실행이 값이 없다고 실패한다.
            var service = rig.Container.GetRequiredService<IDataTransferService>();
            var stmt = results.Selected.Statement!;
            var old = new ExportDialogViewModel(service, new ExportSource(rig.Session, stmt, new Dictionary<string, BindValue>()))
            {
                Format = TransferFormat.Csv,
                FilePath = Path.Combine(dir, "old.csv"),
            };
            await old.StartAsync();
            output.WriteLine("고치기 전 재현: " + old.Status);
            Assert.True(old.StatusIsError);
            Assert.Contains("바인드 변수", old.Status, StringComparison.Ordinal);
            Assert.False(File.Exists(old.FilePath));

            // 목록을 비워도(실행 뒤 값을 바꿔도) 내보내기는 결과를 만든 값을 쓴다.
            tab.Binds.Find("ID")!.Text = "99";
            tab.Binds.Clear();
            var source = DataTransferCommandHandler.Build(tab, null, rig.Session)!;
            Assert.Null(source.BindProblem);

            var csv = Path.Combine(dir, "o.csv");
            var exportCsv = new ExportDialogViewModel(service, source) { Format = TransferFormat.Csv, FilePath = csv };
            await exportCsv.StartAsync();
            Assert.False(exportCsv.StatusIsError, exportCsv.Status);
            Assert.Equal(2, exportCsv.RowCount);
            var lines = (await File.ReadAllTextAsync(csv)).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
            Assert.Equal(3, lines.Length); // 머리글 + 2행
            Assert.Contains("alps", lines[1], StringComparison.Ordinal);
            Assert.Contains("alpine", lines[2], StringComparison.Ordinal);

            var xlsx = Path.Combine(dir, "o.xlsx");
            var exportXlsx = new ExportDialogViewModel(service, source) { Format = TransferFormat.Xlsx, FilePath = xlsx };
            await exportXlsx.StartAsync();
            Assert.False(exportXlsx.StatusIsError, exportXlsx.Status);
            Assert.Equal(2, exportXlsx.RowCount);

            // xlsx 를 같은 모양의 표에 다시 가져와 값(행·이름·날짜)이 원본과 같은지 본다.
            var dst = NewTable();
            await rig.RunSqlAsync($"create table {dst} (id number primary key, name varchar2(40), d date)");
            var import = new ImportDialogViewModel(service, rig.Container.GetRequiredService<IMetadataProvider>(), rig.Session) { FilePath = xlsx, TargetTable = dst };
            await import.LoadAsync();
            Assert.True(import.IsReady, import.Status);
            await import.StartAsync();
            Assert.False(import.StatusIsError, import.Status);
            const string Finger = "select listagg(id || '|' || name || '|' || to_char(d, 'YYYY-MM-DD'), ';') within group (order by id) from ";
            Assert.Equal("2|alps|2026-02-01;4|alpine|2026-04-01", db.Text(Finger + dst));

            // 전체 행 수 세기·서버 거르기도 같은 값으로: 목록이 비었는데도 (예전엔 목록 값만 봐서) 실패하지 않는다.
            ResetStatics();
            ExecutionCommands.Install(rig.Container);
            ResultGridCommands.Active = pane.Model;
            await pane.CountAllRowsAsync();
            Assert.Equal(2, pane.CountedRowCount);

            pane.WhereText = "id > 2";
            await pane.ApplyFilterAsync();
            await pane.Model!.LoadFirstPageAsync();
            Assert.Equal(1, pane.Model.LoadedRowCount);
            Assert.True(pane.IsFiltered);

            // MyBatis 처럼 「바인드 변수」 목록을 거치지 않고 값을 정해 실행한 결과도 같다.
            var prepared = new SqlStatement($"select id from {table} where id = :pid", default, StatementKind.Query) { Verb = "SELECT", BindNames = ["pid"] };
            Assert.True(await rig.Controller.RunPreparedAsync(tab, prepared, new Dictionary<string, BindValue> { ["pid"] = new(BindDataType.Number, 4m) }));
            var preparedSource = DataTransferCommandHandler.Build(tab, null, rig.Session)!;
            Assert.Null(preparedSource.BindProblem);
            var one = Path.Combine(dir, "one.csv");
            var exportOne = new ExportDialogViewModel(service, preparedSource) { Format = TransferFormat.Csv, FilePath = one };
            await exportOne.StartAsync();
            Assert.False(exportOne.StatusIsError, exportOne.Status);
            Assert.Equal(1, exportOne.RowCount);
            Assert.Contains("4", await File.ReadAllTextAsync(one, Encoding.UTF8), StringComparison.Ordinal);
            output.WriteLine("TSK-12-03 OK: 서버 재조회 CSV·xlsx 내보내기·행 수 세기·서버 거르기·값 지정 실행이 바인드 값을 그대로 쓴다");
        }
        finally
        {
            ResetStatics();
            Directory.Delete(dir, recursive: true);
            await rig.RunSqlAsync($"drop table {table} purge");
        }
    });
}
