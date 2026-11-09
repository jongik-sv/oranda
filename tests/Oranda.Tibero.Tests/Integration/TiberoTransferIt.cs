using System.Globalization;
using System.Text;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Transfer;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 가져오기(TI1a)를 실제 서버로 확인한다: Tibero 실행 서비스 + Tibero 메타데이터로 만든 <see cref="TiberoDataTransferService"/> 가 CSV 를 <c>ORT_TI1A</c> 표에 넣는다.
/// 쓰기 시험이라 <c>ORANDA_IT_TIBERO_WRITE=1</c> 일 때만 돌고 표 하나를 만들었다 지운다(D3). 서버·드라이버가 없으면 아무것도 하지 않는다.
/// 접속 정보는 출력에 싣지 않는다. 작은 크기(표당 수백 행 이하, 배치 2~50)로 다중 배치·배치 실패 뒤 한 행씩 재시도·롤백을 본다(결정65 Q8).
/// </summary>
[Trait("Category", "Integration")]
public class TiberoTransferIt(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Columns =
        "ID NUMBER NOT NULL, NAME VARCHAR2(50), HIRED DATE, TS TIMESTAMP(6), NOTE CLOB, BIN RAW(20), AMT NUMBER(12,2), BLB BLOB, CONSTRAINT @T@_PK PRIMARY KEY (ID)";

    private static readonly string[] Header = ["id", "name", "hired", "ts", "note", "bin", "amt"];

    [TiberoWriteFact]
    public async Task Csv_import_in_batches_stores_typed_values_and_rolls_back_as_one_unit_on_failure()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var table = TiberoItSessions.ObjectName("TI1A");
        var owner = it.Env.User.ToUpperInvariant();
        var session = await it.OpenAsync();
        if (TiberoSessionQuery.Long(await TiberoSessionQuery.ScalarAsync(session, $"SELECT COUNT(*) FROM user_tables WHERE table_name = '{table}'")) > 0)
        {
            await ExecAsync(session, $"DROP TABLE {table}");
        }

        await ExecAsync(session, $"CREATE TABLE {table} ({Columns.Replace("@T@", table, StringComparison.Ordinal)})");
        try
        {
            var service = new TiberoDataTransferService(new TiberoExecutionService(new AllowAll(), new NoQuestions(), it.Events, null, dbmsOutput: false), new TiberoMetadataProvider(new Schema.MemoryMetadataCache(), it.Events, Timeout.InfiniteTimeSpan, TiberoCatalogQuery.RunAsync));
            var target = new DbObjectRef(owner, table, "TABLE");
            var options = new ImportOptions(TransferFormat.Csv, target, [.. Header.Select(h => new ColumnMapping(h, h.ToUpperInvariant()))]) { BatchSize = 2 };
            var note = new string('가', 4500);
            var csv = string.Join("\n",
                string.Join(",", Header),
                $"1,O'Neil,2026-03-05,2026-03-05 14:30:15.25,{note},0xCAFE,1234.5",
                "2,\"쉼표,큰따옴표\"\"\",2026-03-06 07:08:09,,,,",
                "3,,,,,00FF10,-0.25",
                "4,four,2026-12-31,2026-12-31 23:59:59.999999,short,,0",
                "5,five,,,,,99999999.99") + "\n";

            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv)))
            {
                var summary = await service.ImportAsync(session, options, stream, null, CancellationToken.None);
                foreach (var warning in summary.Warnings)
                {
                    output.WriteLine("TI1A 경고: " + warning);
                }

                Assert.Empty(summary.Warnings);
                Assert.Equal(5, summary.Rows);
            }

            Assert.False(session.HasPendingTransaction); // CommitAtEnd
            Assert.Equal(5, await CountAsync(session, table));
            Assert.Equal("O'Neil", await TextAsync(session, $"SELECT name FROM {table} WHERE id = 1"));
            Assert.Equal("쉼표,큰따옴표\"", await TextAsync(session, $"SELECT name FROM {table} WHERE id = 2"));
            Assert.Equal("Y", await TextAsync(session, $"SELECT CASE WHEN name IS NULL THEN 'Y' ELSE 'N' END FROM {table} WHERE id = 3"));
            Assert.Equal("2026-03-05 00:00:00", await TextAsync(session, $"SELECT TO_CHAR(hired, 'YYYY-MM-DD HH24:MI:SS') FROM {table} WHERE id = 1"));
            Assert.Equal("2026-03-06 07:08:09", await TextAsync(session, $"SELECT TO_CHAR(hired, 'YYYY-MM-DD HH24:MI:SS') FROM {table} WHERE id = 2"));
            Assert.Equal("2026-03-05 14:30:15.250000", await TextAsync(session, $"SELECT TO_CHAR(ts, 'YYYY-MM-DD HH24:MI:SS.FF6') FROM {table} WHERE id = 1"));
            Assert.Equal("2026-12-31 23:59:59.999999", await TextAsync(session, $"SELECT TO_CHAR(ts, 'YYYY-MM-DD HH24:MI:SS.FF6') FROM {table} WHERE id = 4"));
            Assert.Equal("4500", await TextAsync(session, $"SELECT DBMS_LOB.GETLENGTH(note) FROM {table} WHERE id = 1"));
            Assert.Equal("CAFE", await TextAsync(session, $"SELECT RAWTOHEX(bin) FROM {table} WHERE id = 1"));
            Assert.Equal("00FF10", await TextAsync(session, $"SELECT RAWTOHEX(bin) FROM {table} WHERE id = 3"));
            Assert.Equal("1234.5|-.25|0|99999999.99", await TextAsync(session,
                $"SELECT TO_CHAR(MAX(CASE WHEN id = 1 THEN amt END)) || '|' || TO_CHAR(MAX(CASE WHEN id = 3 THEN amt END)) || '|' || TO_CHAR(MAX(CASE WHEN id = 4 THEN amt END)) || '|' || TO_CHAR(MAX(CASE WHEN id = 5 THEN amt END)) FROM {table}"));

            // 같은 파일을 다시 넣으면 모든 행이 기본 키 위반: 배치가 실패하면 한 행씩 다시 보내 행마다 경고로 보고하고, 표는 그대로다.
            using (var again = new MemoryStream(Encoding.UTF8.GetBytes(csv)))
            {
                var summary = await service.ImportAsync(session, options, again, null, CancellationToken.None);
                output.WriteLine("TI1A 중복 키 경고 첫 줄: " + (summary.Warnings.Count > 0 ? summary.Warnings[0] : "<없음>"));
                Assert.Equal(0, summary.Rows);
                Assert.Equal(5, summary.Warnings.Count);
                Assert.All(summary.Warnings, w => Assert.StartsWith("행 ", w, StringComparison.Ordinal));
            }

            Assert.Equal(5, await CountAsync(session, table));
            Assert.False(session.HasPendingTransaction);

            // 값이 열 타입에 안 맞는 행은 보내기 전에 걸러져 경고로 남고 나머지는 들어간다.
            using (var mixed = new MemoryStream(Encoding.UTF8.GetBytes("id,name,amt\n10,ok,1\n11,bad,abc\n12,ok2,2\n")))
            {
                var partial = new ImportOptions(TransferFormat.Csv, target,
                    [new ColumnMapping("id", "ID"), new ColumnMapping("name", "NAME"), new ColumnMapping("amt", "AMT")]) { BatchSize = 50 };
                var summary = await service.ImportAsync(session, partial, mixed, null, CancellationToken.None);
                Assert.Equal(2, summary.Rows);
                Assert.Single(summary.Warnings);
                Assert.Equal(7, await CountAsync(session, table));
            }

            // BLOB: TO_BLOB(HEXTORAW) 는 2000바이트까지, BLOB 끼리 || 불가(TM0a 실측) → 2001바이트 행은 이유와 함께 경고, 나머지는 들어간다.
            using (var blobs = new MemoryStream(Encoding.UTF8.GetBytes(
                $"id,blb\n20,{new string('A', 4000)}\n21,{new string('B', 4002)}\n22,00FF\n")))
            {
                var blobOptions = new ImportOptions(TransferFormat.Csv, target, [new ColumnMapping("id", "ID"), new ColumnMapping("blb", "BLB")]) { BatchSize = 1 };
                var summary = await service.ImportAsync(session, blobOptions, blobs, null, CancellationToken.None);
                Assert.Equal(2, summary.Rows);
                var warning = Assert.Single(summary.Warnings);
                Assert.Contains("2000", warning, StringComparison.Ordinal);
                Assert.Equal("2000|2", await TextAsync(session,
                    $"SELECT (SELECT DBMS_LOB.GETLENGTH(blb) FROM {table} WHERE id = 20) || '|' || (SELECT DBMS_LOB.GETLENGTH(blb) FROM {table} WHERE id = 22) FROM DUAL"));
                Assert.Equal(9, await CountAsync(session, table));
            }
        }
        finally
        {
            await session.RollbackAsync(CancellationToken.None);
            await ExecAsync(session, $"DROP TABLE {table}");
        }
    }

    private static async Task<long> CountAsync(TiberoSession session, string table) =>
        TiberoSessionQuery.Long(await TiberoSessionQuery.ScalarAsync(session, $"SELECT COUNT(*) FROM {table}")) ?? -1;

    private static async Task<string?> TextAsync(TiberoSession session, string sql) =>
        TiberoSessionQuery.Str(await TiberoSessionQuery.ScalarAsync(session, sql));

    private static async Task ExecAsync(TiberoSession session, string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = session.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            session.CommandGate.Release();
        }
    }

    private sealed class AllowAll : IExecutionPolicy
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) =>
            ValueTask.FromResult(PolicyDecision.Allowed);
    }

    private sealed class NoQuestions : IExecutionInteraction
    {
        public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) =>
            Task.FromResult(ScriptErrorAction.Stop);
    }
}
