using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Transfer.Copy;
using Xunit.Abstractions;

namespace Oranda.Oracle.IntegrationTests.Transfer.Copy;

// 소유: 데이터 복사 PF5(LOB 쓰기 바인드). 이 PC 에서는 컴파일까지만 확인한다. 실행은 CI·다른 PC 의 컨테이너에서 한다.

/// <summary>
/// 복사 대상 쓰기의 LOB 바인드(PF5)를 실제 서버에서: LONG 바인드(<see cref="OracleLobWrite.LongBind"/>)로 복사한 결과가 옛 LOB 바인드
/// (<see cref="OracleLobWrite.Locator"/>)와 열 하나까지 같고 원본과도 같다(NULL·빈 값·상한 앞뒤 크기·한글 NCLOB). 측정 시험(두 바인드 번갈아 3번씩, 기본 건너뜀)은
/// <see cref="CopyMeasureFactAttribute"/>.
/// </summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class CopyLobWriteTests(OracleFreeFixture db, ITestOutputHelper output)
{
    private const string Columns = "ID NUMBER PRIMARY KEY, CL CLOB, NCL NCLOB, BL BLOB";
    private static readonly ResolvedPeriod NoPeriod = new(null, null);

    [Fact]
    public async Task Long_bind_copy_gives_the_same_lob_values_as_the_lob_bind_copy_and_the_source()
    {
        await using var scope = await CopyDbScope.CreateAsync(db, CopyDbScope.NewTag());
        await BothAsync(scope, "LOBW");
        await using (var conn = await OpenAsync(scope, scope.SrcUser))
        {
            await using var cmd = conn.CreateCommand();
            cmd.BindByName = true;
            cmd.CommandText = "INSERT INTO LOBW (ID, CL, NCL, BL) VALUES (:id, :cl, :ncl, :bl)";
            var id = cmd.Parameters.Add("id", OracleDbType.Decimal);
            var cl = cmd.Parameters.Add("cl", OracleDbType.Clob);
            var ncl = cmd.Parameters.Add("ncl", OracleDbType.NClob);
            var bl = cmd.Parameters.Add("bl", OracleDbType.Blob);
            var rowId = 0;
            foreach (var size in new[] { 1, 4000, 4001, LobWriteBinds.LongBindMaxLength, LobWriteBinds.LongBindMaxLength + 1, 65537, 300_000 })
            {
                id.Value = ++rowId;
                cl.Value = Text(size, 'a');
                ncl.Value = KoreanText(size);
                bl.Value = Bytes(size + 1, rowId);
                await cmd.ExecuteNonQueryAsync();
            }

            await ExecAsync(conn, "INSERT INTO LOBW (ID, CL, NCL, BL) VALUES (101, NULL, NULL, NULL)");
            await ExecAsync(conn, "INSERT INTO LOBW (ID, CL, NCL, BL) VALUES (102, EMPTY_CLOB(), EMPTY_CLOB(), EMPTY_BLOB())");
            await ExecAsync(conn, "INSERT INTO LOBW (ID, CL, NCL, BL) VALUES (103, 'x', EMPTY_CLOB(), NULL)");
            await ExecAsync(conn, "INSERT INTO LOBW SELECT 200 + LEVEL, 'r' || LEVEL, '행' || LEVEL, UTL_RAW.CAST_TO_RAW('b' || LEVEL) FROM DUAL CONNECT BY LEVEL <= 30");
            await ExecAsync(conn, "COMMIT");
        }

        var source = await SnapshotAsync(scope, scope.SrcUser);
        var longBind = await CopyAsync(scope, OracleLobWrite.LongBind);
        var lobBind = await CopyAsync(scope, OracleLobWrite.Locator);

        output.WriteLine("long: " + longBind.Stats + " | " + string.Join(" | ", longBind.Rows));
        output.WriteLine("lob : " + lobBind.Stats);
        Assert.Equal(lobBind.Rows, longBind.Rows); // 옛 바인드와 열 하나까지 같다(빈 값을 어떻게 넣든 옛 동작과 같다)
        static bool HasEmpty(string row) => row.StartsWith("102:", StringComparison.Ordinal) || row.StartsWith("103:", StringComparison.Ordinal);
        Assert.Equal(source.Where(r => !HasEmpty(r)), longBind.Rows.Where(r => !HasEmpty(r)));
        Assert.Contains("101:NULL,NULL,NULL", longBind.Rows);
        output.WriteLine("empty rows source: " + string.Join(" | ", source.Where(HasEmpty)) + " · long: " + string.Join(" | ", longBind.Rows.Where(HasEmpty)));
        Assert.True(longBind.Stats.LongRows > 0, longBind.Stats.ToString());
        Assert.Equal(0, longBind.Stats.Fallbacks);
        Assert.Equal(0, lobBind.Stats.LongRows);
    }

    /// <summary>
    /// 측정(PF5): LOB 열이 있는 테이블(PF3 측정과 같은 값 — 대부분 수 KB, 1000행마다 64K 를 넘는 CLOB)을 Oracle→Oracle 로 LOB 바인드·LONG 바인드를 번갈아
    /// 3번씩 복사해, 실행마다 시간 나눔과 바인드별 중앙값(쓰기 초·행/초)을 <c>PF5 measure …</c> 줄로 출력한다. 단언은 결과 건수만.
    /// <c>ORANDA_IT_COPY_MEASURE=1</c> 일 때만 돈다(행 수 <c>ORANDA_IT_COPY_MEASURE_ROWS</c>, 기본 5000).
    /// </summary>
    [CopyMeasureFact]
    public async Task Measure_oracle_to_oracle_lob_write_with_lob_bind_and_long_bind()
    {
        var rows = CopyMeasureFactAttribute.Rows();
        await using var scope = await CopyDbScope.CreateAsync(db, CopyDbScope.NewTag());
        await BothAsync(scope, "PF5_LOB");
        await scope.ExecSourceAsync(
            "BEGIN INSERT INTO PF5_LOB SELECT LEVEL, TO_CLOB(RPAD('c', MOD(LEVEL, 3000) + 1, 'c')), TO_NCLOB(RPAD('n', MOD(LEVEL, 500) + 1, 'n')), "
            + "TO_BLOB(UTL_RAW.CAST_TO_RAW(RPAD('b', MOD(LEVEL, 2000) + 1, 'b'))) FROM DUAL CONNECT BY LEVEL <= "
            + rows.ToString(CultureInfo.InvariantCulture) + "; COMMIT; END;");
        await scope.ExecSourceAsync(
            "DECLARE c CLOB; BEGIN c := RPAD('y', 4000, 'y'); FOR i IN 1..25 LOOP c := c || RPAD('y', 4000, 'y'); END LOOP; "
            + "UPDATE PF5_LOB SET CL = c WHERE MOD(ID, 1000) = 0; COMMIT; END;");

        var runs = new Dictionary<OracleLobWrite, List<CopyTableTimes>> { [OracleLobWrite.Locator] = [], [OracleLobWrite.LongBind] = [] };
        for (var round = 1; round <= 3; round++)
        {
            foreach (var mode in new[] { OracleLobWrite.Locator, OracleLobWrite.LongBind })
            {
                var bulk = scope.Bulk();
                bulk.LobWrite = mode;
                var sw = Stopwatch.StartNew();
                var result = await RunAsync(scope, "PF5_LOB", bulk);
                sw.Stop();

                var table = Assert.Single(result.Tables);
                Assert.True(table.TargetRows == rows, Summary(result));
                Assert.NotNull(table.Times);
                runs[mode].Add(table.Times);
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"PF5 measure O2O lob write={mode} round={round} rows={rows}: {sw.Elapsed.TotalSeconds:F1}s · {CopyLogText.Times(table.Times)} · {bulk.LobWriteStats}"));
            }
        }

        foreach (var (mode, times) in runs)
        {
            output.WriteLine(MedianLine("O2O", mode.ToString(), rows, times));
        }
    }

    /// <summary>측정 중앙값 줄: <c>PF5 measure O2O lob write=… median rows=…: 쓰기 1.23초 · 4,000행/초 (3회)</c>.</summary>
    internal static string MedianLine(string route, string mode, int rows, IReadOnlyList<CopyTableTimes> times)
    {
        var write = times.Select(t => t.Write.TotalSeconds).Order().ToList();
        var speed = times.Select(t => t.RowsPerSecond).Order().ToList();
        return string.Create(CultureInfo.InvariantCulture,
            $"PF5 measure {route} lob write={mode} median rows={rows}: 쓰기 {write[write.Count / 2]:F2}초 · {speed[speed.Count / 2]:N0}행/초 ({times.Count}회)");
    }

    // ---- 도우미 ----

    /// <summary>대상 LOBW 를 지우고 넣는 복사 한 번 뒤 대상 값 요약과 그 복사의 바인드 집계.</summary>
    private async Task<(List<string> Rows, LobWriteCounters Stats)> CopyAsync(CopyDbScope scope, OracleLobWrite mode)
    {
        var bulk = scope.Bulk();
        bulk.LobWrite = mode;
        var result = await RunAsync(scope, "LOBW", bulk);
        var table = Assert.Single(result.Tables);
        Assert.True(table.Outcome == CopyOutcome.Succeeded, Summary(result));
        return (await SnapshotAsync(scope, scope.DstUser), bulk.LobWriteStats);
    }

    private static async Task<CopyRunResult> RunAsync(CopyDbScope scope, string tableName, OracleBulkExecutionService bulk)
    {
        var job = scope.Job(new TableRule(tableName, RuleKind.All, TargetAction.DeleteAllInsert));
        var sessions = await scope.OpenSessionsAsync();
        var report = await scope.Precheck().RunAsync(sessions, job, NoPeriod, null, CancellationToken.None);
        Assert.False(report.HasErrors, string.Join(Environment.NewLine, report.Items.Where(i => i.Level == CheckLevel.Error).Select(i => i.Code + ": " + i.Message)));
        scope.Ui.ConfirmAnswer = false;
        var runner = new OracleCopyRunner(bulk, new OracleExecutionService(new ExecutionPolicy(), scope.Ui, scope.Bus, null, pageSize: 500), new NoRunLog());
        var decisions = new CopyDecisions(new HashSet<string>(StringComparer.Ordinal), new HashSet<ForeignKeyRef>(),
            report.Preview.Where(p => p.Policy == PolicyDecisionKind.Confirm).Select(p => p.Sql).ToHashSet(StringComparer.Ordinal),
            ProceedSequenceGap: false);
        return await runner.RunAsync(sessions, job, report, decisions, NoPeriod, null, new CopyRunControl(), CancellationToken.None);
    }

    private static Task BothAsync(CopyDbScope scope, string table)
        => Task.WhenAll(scope.CreateSourceTableAsync(table, Columns), scope.CreateTargetTableAsync(table, Columns));

    /// <summary>앱 경로를 거치지 않고 읽은 LOBW 행 요약: <c>ID:CL,NCL,BL</c>, 값은 NULL 또는 길이·SHA-256 앞 16자.</summary>
    private async Task<List<string>> SnapshotAsync(CopyDbScope scope, string user)
    {
        await using var conn = await OpenAsync(scope, user);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ID, CL, NCL, BL FROM LOBW ORDER BY ID";
        await using var reader = (OracleDataReader)await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            var clob = reader.IsDBNull(1) ? null : Describe(reader.GetOracleClob(1).Value);
            var nclob = reader.IsDBNull(2) ? null : Describe(reader.GetOracleClob(2).Value);
            var blob = reader.IsDBNull(3) ? null : Describe(reader.GetOracleBlob(3).Value);
            rows.Add(string.Create(CultureInfo.InvariantCulture, $"{reader.GetInt32(0)}:{clob ?? "NULL"},{nclob ?? "NULL"},{blob ?? "NULL"}"));
        }

        return rows;
    }

    private static string Describe(string text) =>
        "S" + text.Length.ToString(CultureInfo.InvariantCulture) + "#" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16];

    private static string Describe(byte[] bytes) =>
        "B" + bytes.Length.ToString(CultureInfo.InvariantCulture) + "#" + Convert.ToHexString(SHA256.HashData(bytes))[..16];

    /// <summary>끝 글자를 바꿔 잘린 값과 구별한다.</summary>
    private static string Text(int chars, char fill) => chars <= 1 ? new string('Z', chars) : new string(fill, chars - 1) + "Z";

    /// <summary>한글(국가 문자 집합 2바이트)로 채운 NCLOB 값.</summary>
    private static string KoreanText(int chars)
    {
        const string Pattern = "한글복사";
        var builder = new System.Text.StringBuilder(chars);
        for (var i = 0; i < chars; i++)
        {
            builder.Append(Pattern[i % Pattern.Length]);
        }

        return builder.ToString();
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 31) + seed);
        }

        return bytes;
    }

    private async Task<OracleConnection> OpenAsync(CopyDbScope scope, string user)
    {
        var conn = new OracleConnection(new ConnectionProfile("direct", user, db.DataSource).BuildConnectionString(scope.Password));
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static string Summary(CopyRunResult result)
        => result.Outcome + ": " + string.Join(" / ", result.Tables.Select(t => t.Table + "=" + t.Outcome + " " + t.Message))
           + " | notices: " + string.Join(" / ", result.Notices);

    private sealed class NoRunLog : ICopyRunLog
    {
        public Task<ICopyRunRecord> BeginAsync(CopyRunHeader header, CancellationToken ct) => Task.FromResult<ICopyRunRecord>(new Record());

        public Task<IReadOnlyList<UnfinishedRun>> FindUnfinishedAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<UnfinishedRun>>([]);

        public Task MarkRecoveredAsync(string runFile, CancellationToken ct) => Task.CompletedTask;

        private sealed class Record : ICopyRunRecord
        {
            public Task RecordDisabledAsync(IReadOnlyList<DisabledObject> objects, CancellationToken ct) => Task.CompletedTask;

            public Task RecordStatementAsync(string sql, CancellationToken ct) => Task.CompletedTask;

            public Task RecordTableAsync(TableRunResult result, CancellationToken ct) => Task.CompletedTask;

            public Task RecordFailedRowAsync(FailedRow row, CancellationToken ct) => Task.CompletedTask;

            public Task CompleteAsync(CopyOutcome outcome, CancellationToken ct) => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
