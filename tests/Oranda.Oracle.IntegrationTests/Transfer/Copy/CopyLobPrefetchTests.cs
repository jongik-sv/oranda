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

// 소유: 데이터 복사 PF3(LOB 미리 받기). 이 PC 에서는 컴파일까지만 확인한다. 실행은 CI·다른 PC 의 컨테이너에서 한다.

/// <summary>
/// 복사 원본 읽기의 LOB 미리 받기(PF3, <see cref="CopyRunner.LobPrefetchSize"/>)를 실제 서버에서: 미리 받기 크기보다 짧은 값·딱 그 크기·한 글자(바이트) 넘는 값·
/// 아주 큰 값·NULL·빈 값(EMPTY_CLOB/EMPTY_BLOB) 모두 미리 받기를 켠 복사와 끈 복사(<see cref="CopyOptions.NoLobPrefetch"/>)의 결과가 같고,
/// 빈 값이 아닌 값은 원본과 글자·바이트까지 같다. 측정 시험(켬·끔 시간, 기본 건너뜀)은 <see cref="CopyMeasureFactAttribute"/>.
/// </summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class CopyLobPrefetchTests(OracleFreeFixture db, ITestOutputHelper output)
{
    private const string Columns = "ID NUMBER PRIMARY KEY, CL CLOB, NCL NCLOB, BL BLOB";
    private static readonly ResolvedPeriod NoPeriod = new(null, null);

    [Fact]
    public async Task Lob_values_large_small_null_and_empty_copy_the_same_with_prefetch_on_as_off()
    {
        await using var scope = await CopyDbScope.CreateAsync(db, CopyDbScope.NewTag());
        await BothAsync(scope, "LOBP");
        var limit = CopyRunner.LobPrefetchSize;
        var cases = new (int Id, int Chars, int Bytes)[]
        {
            (1, 3, 3),                     // 작은 값
            (2, limit - 1, limit - 1),     // 미리 받기 크기 안
            (3, limit, limit),             // 딱 그 크기
            (4, limit + 1, limit + 1),     // 한 글자(바이트) 넘음: 나머지를 따로 읽는다
            (5, 300_000, 300_001),         // 아주 큰 값
        };

        await using (var conn = await OpenAsync(scope, scope.SrcUser))
        {
            await using var cmd = conn.CreateCommand();
            cmd.BindByName = true;
            cmd.CommandText = "INSERT INTO LOBP (ID, CL, NCL, BL) VALUES (:id, :cl, :ncl, :bl)";
            var id = cmd.Parameters.Add("id", OracleDbType.Decimal);
            var cl = cmd.Parameters.Add("cl", OracleDbType.Clob);
            var ncl = cmd.Parameters.Add("ncl", OracleDbType.NClob);
            var bl = cmd.Parameters.Add("bl", OracleDbType.Blob);
            foreach (var (rowId, chars, bytes) in cases)
            {
                id.Value = rowId;
                cl.Value = Text(chars, 'a');
                ncl.Value = KoreanText(chars);
                bl.Value = Bytes(bytes, rowId);
                await cmd.ExecuteNonQueryAsync();
            }

            await ExecAsync(conn, "INSERT INTO LOBP (ID, CL, NCL, BL) VALUES (6, NULL, NULL, NULL)");
            await ExecAsync(conn, "INSERT INTO LOBP (ID, CL, NCL, BL) VALUES (7, EMPTY_CLOB(), EMPTY_CLOB(), EMPTY_BLOB())");
            await ExecAsync(conn, "COMMIT");
        }

        var source = await SnapshotAsync(scope, scope.SrcUser);
        var on = await CopyAsync(scope, noLobPrefetch: false);
        var off = await CopyAsync(scope, noLobPrefetch: true);

        output.WriteLine("on : " + string.Join(" | ", on));
        output.WriteLine("off: " + string.Join(" | ", off));
        Assert.Equal(off, on); // 켬·끔 결과가 열 하나까지 같다(빈 값을 어떻게 넣든 옛 동작과 같다)
        Assert.Equal(source.Where(r => !r.StartsWith("7:", StringComparison.Ordinal)), on.Where(r => !r.StartsWith("7:", StringComparison.Ordinal)));
        Assert.Contains("6:NULL,NULL,NULL", on);
    }

    /// <summary>
    /// 측정(PF3): LOB 열이 있는 테이블(대부분 수 KB, 1000행마다 64K 를 넘는 값)을 Oracle→Oracle 로 미리 받기 켬·끔 각각 복사해 걸린 시간과 시간 나눔
    /// (읽기·변환·쓰기·행/초)을 출력한다. 단언은 결과 건수만. <c>ORANDA_IT_COPY_MEASURE=1</c> 일 때만 돈다
    /// (행 수 = <c>ORANDA_IT_COPY_MEASURE_ROWS</c>, 기본 5000).
    /// </summary>
    [CopyMeasureFact]
    public async Task Measure_oracle_to_oracle_lob_copy_with_and_without_prefetch()
    {
        var rows = CopyMeasureFactAttribute.Rows();
        await using var scope = await CopyDbScope.CreateAsync(db, CopyDbScope.NewTag());
        await BothAsync(scope, "PF_LOB");
        await scope.ExecSourceAsync(
            "BEGIN INSERT INTO PF_LOB SELECT LEVEL, TO_CLOB(RPAD('c', MOD(LEVEL, 3000) + 1, 'c')), TO_NCLOB(RPAD('n', MOD(LEVEL, 500) + 1, 'n')), "
            + "TO_BLOB(UTL_RAW.CAST_TO_RAW(RPAD('b', MOD(LEVEL, 2000) + 1, 'b'))) FROM DUAL CONNECT BY LEVEL <= "
            + rows.ToString(CultureInfo.InvariantCulture) + "; COMMIT; END;");
        await scope.ExecSourceAsync(
            "DECLARE c CLOB; BEGIN c := RPAD('y', 4000, 'y'); FOR i IN 1..25 LOOP c := c || RPAD('y', 4000, 'y'); END LOOP; "
            + "UPDATE PF_LOB SET CL = c WHERE MOD(ID, 1000) = 0; COMMIT; END;");

        foreach (var prefetch in new[] { true, false, true })
        {
            var sw = Stopwatch.StartNew();
            var result = await RunAsync(scope, "PF_LOB", noLobPrefetch: !prefetch);
            sw.Stop();

            var table = Assert.Single(result.Tables);
            Assert.True(table.TargetRows == rows, Summary(result));
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"PF3 measure O2O lob prefetch={prefetch} rows={rows}: {sw.Elapsed.TotalSeconds:F1}s · ")
                + (table.Times is { } times ? CopyLogText.Times(times) : "-"));
        }
    }

    // ---- 도우미 ----

    /// <summary>대상 LOBP 를 지우고 넣는 복사 한 번 뒤 대상 값 요약.</summary>
    private async Task<List<string>> CopyAsync(CopyDbScope scope, bool noLobPrefetch)
    {
        var result = await RunAsync(scope, "LOBP", noLobPrefetch);
        var table = Assert.Single(result.Tables);
        Assert.True(table.Outcome == CopyOutcome.Succeeded, Summary(result));
        return await SnapshotAsync(scope, scope.DstUser);
    }

    private static async Task<CopyRunResult> RunAsync(CopyDbScope scope, string tableName, bool noLobPrefetch)
    {
        var job = scope.Job(new TableRule(tableName, RuleKind.All, TargetAction.DeleteAllInsert)) with
        {
            Options = new CopyOptions { NoLobPrefetch = noLobPrefetch },
        };
        var sessions = await scope.OpenSessionsAsync();
        var report = await scope.Precheck().RunAsync(sessions, job, NoPeriod, null, CancellationToken.None);
        Assert.False(report.HasErrors, string.Join(Environment.NewLine, report.Items.Where(i => i.Level == CheckLevel.Error).Select(i => i.Code + ": " + i.Message)));
        scope.Ui.ConfirmAnswer = false;
        var runner = new OracleCopyRunner(scope.Bulk(), new OracleExecutionService(new ExecutionPolicy(), scope.Ui, scope.Bus, null, pageSize: 500), new NoRunLog());
        var decisions = new CopyDecisions(new HashSet<string>(StringComparer.Ordinal), new HashSet<ForeignKeyRef>(),
            report.Preview.Where(p => p.Policy == PolicyDecisionKind.Confirm).Select(p => p.Sql).ToHashSet(StringComparer.Ordinal),
            ProceedSequenceGap: false);
        return await runner.RunAsync(sessions, job, report, decisions, NoPeriod, null, new CopyRunControl(), CancellationToken.None);
    }

    private static Task BothAsync(CopyDbScope scope, string table)
        => Task.WhenAll(scope.CreateSourceTableAsync(table, Columns), scope.CreateTargetTableAsync(table, Columns));

    /// <summary>
    /// 앱 경로를 거치지 않고(드라이버 기본 = 위치자) 읽은 LOBP 행 요약: <c>ID:CL,NCL,BL</c>, 값은 NULL 또는 길이·SHA-256 앞 16자.
    /// </summary>
    private async Task<List<string>> SnapshotAsync(CopyDbScope scope, string user)
    {
        await using var conn = await OpenAsync(scope, user);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ID, CL, NCL, BL FROM LOBP ORDER BY ID";
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
