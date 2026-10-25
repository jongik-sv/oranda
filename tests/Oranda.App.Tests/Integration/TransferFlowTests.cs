using Xunit.Abstractions;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Oranda.App.Features.DataTransfer;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer;

namespace Oranda.App.Tests.Integration;

/// <summary>
/// TSK-11-01 시나리오 S6 「결과 → 내보내기 → 가져오기」(TSK-06-07 + 06-02 + 메타데이터).
/// 실행 질의를 CSV·JSON·XLSX·INSERT 로 내보내고, 만든 파일을 다른 표로 가져와 값이 왕복하는지 본다.
/// </summary>
[Trait("Category", "Integration")]
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class TransferFlowTests(ItDb db, ITestOutputHelper output) : IClassFixture<ItDb>
{
    private static string NewTable() => "T_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static SqlStatement Query(string sql) => new(sql, default, StatementKind.Query) { Verb = "SELECT" };

    private const string Cols = "id number primary key, name varchar2(60), amt number(10,2), d date";

    private const string Seed = "(1, 'plain', 12.50, date '2026-01-02')";

    private static async Task<(string Src, string Dst)> PrepareAsync(FlowRig rig)
    {
        var src = NewTable();
        var dst = NewTable();
        await rig.RunSqlAsync(
            $"create table {src} ({Cols});\ncreate table {dst} ({Cols});\n" +
            $"insert into {src} values {Seed};\n" +
            $"insert into {src} values (2, 'a,b \"q\" 한글', 0, null);\n" +
            $"insert into {src} values (3, null, null, date '2026-12-31');\n" +
            $"insert into {src} values (4, 'line1' || chr(10) || 'line2', -3.14, date '2000-02-29');\ncommit;",
            "oranda.exec.runScript");
        return (src, dst);
    }

    private static async Task DropAsync(FlowRig rig, params string[] tables)
    {
        foreach (var t in tables)
        {
            await rig.RunSqlAsync($"drop table {t} purge");
        }
    }

    private string Fingerprint(string table) =>
        db.Text($"select listagg(id || '|' || nvl(name, '~') || '|' || nvl(to_char(amt), '~') || '|' || nvl(to_char(d, 'YYYY-MM-DD'), '~'), ';') within group (order by id) from {table}");

    [Fact]
    public Task S6_1_Csv_export_then_import_round_trips_nulls_quotes_newlines_and_korean() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        await rig.ConnectAsync();
        var (src, dst) = await PrepareAsync(rig);
        var file = Path.Combine(Path.GetTempPath(), $"oranda-itest-{Guid.NewGuid():N}.csv");
        try
        {
            var service = rig.Container.GetRequiredService<IDataTransferService>();
            var export = new ExportDialogViewModel(service, new ExportSource(rig.Session, Query($"select id, name, amt, d from {src} order by id"), new Dictionary<string, BindValue>()))
            {
                Format = TransferFormat.Csv,
                FilePath = file,
            };
            await export.StartAsync();
            Assert.False(export.StatusIsError, export.Status);
            Assert.Equal(4, export.RowCount);

            var text = await File.ReadAllTextAsync(file);
            Assert.Contains("ID,NAME,AMT,D", text, StringComparison.Ordinal);
            Assert.Contains("\"a,b \"\"q\"\" 한글\"", text, StringComparison.Ordinal); // 쉼표·따옴표 이스케이프
            Assert.Contains("line1\nline2", text.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);

            var import = new ImportDialogViewModel(service, rig.Container.GetRequiredService<IMetadataProvider>(), rig.Session)
            {
                FilePath = file,
                TargetTable = dst,
            };
            await import.LoadAsync();
            Assert.True(import.IsReady, import.Status);
            Assert.Equal(["ID", "NAME", "AMT", "D"], import.Mappings.Select(m => m.Target).ToArray()); // 머리글로 자동 매핑
            await import.StartAsync();
            Assert.False(import.StatusIsError, import.Status + " / " + string.Join(" | ", import.ErrorRows));
            Assert.Equal(4, import.InsertedRows);

            Assert.Equal(Fingerprint(src), Fingerprint(dst));
            output.WriteLine("S6-1 OK: CSV 내보내기→가져오기 값 왕복(NULL·쉼표·따옴표·줄바꿈·한글·날짜·소수)");
        }
        finally
        {
            File.Delete(file);
            await DropAsync(rig, src, dst);
        }
    });

    [Fact]
    public Task S6_2_Import_boundaries_duplicate_key_rolls_back_unknown_column_skipped_and_bad_file() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        await rig.ConnectAsync();
        var (src, dst) = await PrepareAsync(rig);
        var dir = Path.Combine(Path.GetTempPath(), "oranda-itest-s62-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var service = rig.Container.GetRequiredService<IDataTransferService>();
            var meta = rig.Container.GetRequiredService<IMetadataProvider>();

            // 오류: 중복 키가 섞인 파일은 오류 행만 보고되고 정상 행은 들어간다(되돌림은 아래에서 삭제로).
            var dup = Path.Combine(dir, "dup.csv");
            await File.WriteAllTextAsync(dup, "ID,NAME,AMT,D\n1,one,1,2026-01-01\n2,two,2,2026-01-02\n2,two-again,3,2026-01-03\n", new UTF8Encoding(false));
            var vm = new ImportDialogViewModel(service, meta, rig.Session) { FilePath = dup, TargetTable = dst };
            await vm.LoadAsync();
            await vm.StartAsync();
            // 설계: 오류 행은 건너뛰고 따로 보고하며 나머지는 넣는다. 행 번호는 머리글을 센 파일 줄 번호라
            // 머리글 + 데이터 3행째(중복 키)는 「행 4」. 오류 코드는 앞에 한 번만 붙는다. 서버에는 정상 2행만 있다.
            Assert.True(vm.HasErrors, vm.Status);
            var errorLine = Assert.Single(vm.ErrorRows);
            Assert.StartsWith("행 4: ORA-00001: ", errorLine, StringComparison.Ordinal);
            Assert.DoesNotContain("ORA-00001: ORA-00001", errorLine, StringComparison.Ordinal); // 26ai 는 뒤 줄에 세부 정보로 코드를 다시 적는다(ORA-03301)
            Assert.Equal("2", db.Text($"select count(*) from {dst}"));
            Assert.Equal("two", db.Text($"select name from {dst} where id = 2")); // 중복 행이 기존 값을 덮지 않았다
            await rig.RunSqlAsync($"delete from {dst}");
            await rig.Run("oranda.txn.commit");

            // 경계: 원본에 대상에 없는 열이 있으면 매핑에서 건너뛰고 나머지는 들어간다. 빈 값은 NULL.
            var extra = Path.Combine(dir, "extra.csv");
            await File.WriteAllTextAsync(extra, "ID,NAME,EXTRA_COL\n10,열 건너뜀,zzz\n11,,yyy\n", new UTF8Encoding(false));
            var vm2 = new ImportDialogViewModel(service, meta, rig.Session) { FilePath = extra, TargetTable = dst };
            await vm2.LoadAsync();
            Assert.Contains(vm2.Mappings, m => m.IsSkipped && m.Source == "EXTRA_COL");
            await vm2.StartAsync();
            Assert.False(vm2.StatusIsError, vm2.Status);
            Assert.Equal("열 건너뜀", db.Text($"select name from {dst} where id = 10"));
            Assert.Null(db.Scalar($"select name from {dst} where id = 11"));

            // 오류: 숫자 열에 글자가 있으면 그 행만 오류로 보고(또는 전체 실패), 서버 상태가 어중간하게 남지 않는다.
            var before = db.Text($"select count(*) from {dst}");
            var bad = Path.Combine(dir, "bad.csv");
            await File.WriteAllTextAsync(bad, "ID,NAME,AMT\n20,ok,1\n21,notnum,abc\n", new UTF8Encoding(false));
            var vm3 = new ImportDialogViewModel(service, meta, rig.Session) { FilePath = bad, TargetTable = dst };
            await vm3.LoadAsync();
            Assert.Contains(vm3.Mappings, m => m.PreviewIsError);
            await vm3.StartAsync();
            var after = db.Text($"select count(*) from {dst}");
            Assert.True(after == before || after == (int.Parse(before, System.Globalization.CultureInfo.InvariantCulture) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), $"before={before} after={after}");

            // 오류: 없는 파일·없는 표는 로드 단계에서 오류로 알리고 가져오기 버튼이 열리지 않는다.
            var missing = new ImportDialogViewModel(service, meta, rig.Session) { FilePath = Path.Combine(dir, "nope.csv"), TargetTable = dst };
            await missing.LoadAsync();
            Assert.False(missing.IsReady);
            Assert.True(missing.StatusIsError);
            var noTable = new ImportDialogViewModel(service, meta, rig.Session) { FilePath = dup, TargetTable = "NO_SUCH_TABLE_X" };
            await noTable.LoadAsync();
            Assert.False(noTable.IsReady);

            // 경계: 헤더 없는 파일(HasHeader=false)은 열 순서대로 매핑 후보가 만들어진다. 빈 파일은 읽을 행 없음.
            var empty = Path.Combine(dir, "empty.csv");
            await File.WriteAllTextAsync(empty, string.Empty);
            var vm4 = new ImportDialogViewModel(service, meta, rig.Session) { FilePath = empty, TargetTable = dst };
            await vm4.LoadAsync();
            Assert.False(vm4.IsReady);
            output.WriteLine("S6-2 OK: 중복 키 롤백·열 건너뜀·빈 값 NULL·숫자 오류·없는 파일/표·빈 파일");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            await DropAsync(rig, src, dst);
        }
    });

    [Fact]
    public Task S6_3_Json_xlsx_and_insert_exports_are_valid_and_xlsx_reimports() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        await rig.ConnectAsync();
        var (src, dst) = await PrepareAsync(rig);
        var dir = Path.Combine(Path.GetTempPath(), "oranda-itest-s63-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var service = rig.Container.GetRequiredService<IDataTransferService>();
            var source = new ExportSource(rig.Session, Query($"select id, name, amt, d from {src} order by id"), new Dictionary<string, BindValue>()) { SuggestedTableName = dst };

            async Task<ExportDialogViewModel> Export(TransferFormat format, string file)
            {
                var vm = new ExportDialogViewModel(service, source) { Format = format, FilePath = file };
                await vm.StartAsync();
                Assert.False(vm.StatusIsError, $"{format}: {vm.Status}");
                return vm;
            }

            // JSON: 문서로 해석되고 4행, 한글 보존.
            var json = Path.Combine(dir, "o.json");
            await Export(TransferFormat.Json, json);
            using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(json)))
            {
                Assert.Equal(4, doc.RootElement.GetArrayLength());
                Assert.Contains("한글", doc.RootElement.ToString(), StringComparison.Ordinal);
            }

            // INSERT 문: 표 이름이 들어가고, 그 스크립트를 실행하면 같은 데이터가 된다(앱의 스크립트 실행으로 다시 넣는다).
            var sqlFile = Path.Combine(dir, "o.sql");
            await Export(TransferFormat.Insert, sqlFile);
            var inserts = await File.ReadAllTextAsync(sqlFile);
            Assert.Contains("INSERT INTO", inserts.ToUpperInvariant(), StringComparison.Ordinal);
            Assert.Contains(dst, inserts.ToUpperInvariant(), StringComparison.Ordinal);
            var run = await rig.RunSqlAsync(inserts, "oranda.exec.runScript");
            Assert.DoesNotContain(run.Tabs, t => t.IsError);
            await rig.Run("oranda.txn.commit");
            Assert.Equal(Fingerprint(src), Fingerprint(dst));
            await rig.RunSqlAsync($"delete from {dst}");
            await rig.Run("oranda.txn.commit");

            // XLSX: zip 서명, 헤더 옵션 끄기, 다시 가져오면 값 왕복.
            var xlsx = Path.Combine(dir, "o.xlsx");
            await Export(TransferFormat.Xlsx, xlsx);
            using (var zip = ZipFile.OpenRead(xlsx))
            {
                Assert.Contains(zip.Entries, e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal));
            }

            var import = new ImportDialogViewModel(service, rig.Container.GetRequiredService<IMetadataProvider>(), rig.Session) { FilePath = xlsx, TargetTable = dst };
            await import.LoadAsync();
            Assert.True(import.IsReady, import.Status);
            await import.StartAsync();
            Assert.False(import.StatusIsError, import.Status + string.Join(" | ", import.ErrorRows));
            Assert.Equal(Fingerprint(src), Fingerprint(dst));

            // 오류: 쓸 수 없는 경로는 오류 상태로 끝나고 파일이 남지 않으며, 질의 자체가 틀리면 오류로 알린다.
            var badPath = Path.Combine(dir, "no-such-dir", "x.csv");
            var failing = new ExportDialogViewModel(service, source) { Format = TransferFormat.Csv, FilePath = badPath };
            await failing.StartAsync();
            Assert.True(failing.StatusIsError);
            Assert.False(File.Exists(badPath));
            var badQuery = new ExportDialogViewModel(service, new ExportSource(rig.Session, Query("select * from no_such_table_x"), new Dictionary<string, BindValue>()))
            {
                Format = TransferFormat.Csv,
                FilePath = Path.Combine(dir, "bq.csv"),
            };
            await badQuery.StartAsync();
            Assert.True(badQuery.StatusIsError || badQuery.Status.Contains("ORA-", StringComparison.Ordinal), badQuery.Status);
            Assert.False(File.Exists(Path.Combine(dir, "bq.csv")), "실패한 내보내기가 만들다 만 파일을 남겼다");

            // 경계: 0행 결과도 헤더만 있는 파일이 만들어진다.
            var zero = new ExportDialogViewModel(service, new ExportSource(rig.Session, Query($"select id, name from {src} where 1 = 0"), new Dictionary<string, BindValue>()))
            {
                Format = TransferFormat.Csv,
                FilePath = Path.Combine(dir, "zero.csv"),
            };
            await zero.StartAsync();
            Assert.Equal(0, zero.RowCount);
            Assert.StartsWith("ID,NAME", await File.ReadAllTextAsync(Path.Combine(dir, "zero.csv")), StringComparison.Ordinal);
            output.WriteLine("S6-3 OK: JSON·INSERT(재실행 동일)·XLSX(재가져오기 동일)·쓸 수 없는 경로·틀린 질의·0행");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            await DropAsync(rig, src, dst);
        }
    });
}
