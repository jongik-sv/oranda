using System.Data;
using System.Data.Common;
using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Tibero.Driver;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// R1x 확인: 결과 그리드(<see cref="TiberoExecutionService"/>)와 대량 읽기(<see cref="TiberoBulkExecutionService.StreamRowsAsync"/>)가
/// BINARY_FLOAT·BINARY_DOUBLE·LONG·LONG RAW 를 정확히 읽는다(<see cref="TiberoReadTypesMeasureIt"/> 가 잰 드라이버 오동작의 수정).
/// <list type="bullet">
/// <item><c>ORANDA_IT_TIBERO_WRITE=1</c> 일 때만 돈다(<see cref="TiberoItSessions.SkipWrite"/>). 표는 <c>ORT_R1X_*</c> 이고 끝에 ROLLBACK 뒤 DROP … PURGE 로 지운다.</item>
/// <item>BINARY_* 는 TO_BINARY_FLOAT/DOUBLE 글자로 넣고, LONG·LONG RAW 는 바인드(<see cref="TiberoParamKind.Long"/>·<see cref="TiberoParamKind.LongRaw"/>)로 넣는다.
/// 같은 세션이라 커밋 전 값이 보인다.</item>
/// <item>실행: <c>ORANDA_IT_TIBERO_WRITE=1 dotnet test tests/Oranda.Tibero.Tests --filter "FullyQualifiedName~TiberoReadTypesIt" --logger "console;verbosity=detailed"</c></item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class TiberoReadTypesIt
{
    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();

    private sealed record FloatCase(int Id, string F, string D, float ExpectedF, double ExpectedD);

    private static readonly FloatCase[] FloatCases =
    [
        new(1, "-2.25", "-2.25", -2.25f, -2.25d),
        new(2, "2.25", "2.25", 2.25f, 2.25d),
        new(3, "-1.5", "-1.5", -1.5f, -1.5d),
        new(4, "0", "0", 0f, 0d),
        new(5, "NaN", "NaN", float.NaN, double.NaN),
        new(6, "3.4028234E38", "1.7976931348623157E308", float.MaxValue, double.MaxValue),
        new(7, "-3.4028234E38", "-1.7976931348623157E308", -float.MaxValue, -double.MaxValue),
    ];

    private static readonly int[] LongLengths = [1, 4001, 40000];

    private static readonly int[] LongRawLengths = [1, 2001, 40000];

    // =================================================================================================================
    // BINARY_FLOAT / BINARY_DOUBLE
    // =================================================================================================================

    [TiberoWriteFact]
    public async Task Binary_float_and_double_read_exactly_in_the_grid_and_in_the_bulk_stream()
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

        var s = await it.OpenAsync();
        var t = TiberoItSessions.ObjectName("R1X_AFLT");
        await CleanupAsync(s, t);
        try
        {
            await ExecAsync(s, $"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, F BINARY_FLOAT, D BINARY_DOUBLE)");
            foreach (var c in FloatCases)
            {
                await ExecAsync(s, $"INSERT INTO {t} (ID, F, D) VALUES ({N(c.Id)}, TO_BINARY_FLOAT('{c.F}'), TO_BINARY_DOUBLE('{c.D}'))");
            }

            await ExecAsync(s, $"INSERT INTO {t} (ID, F, D) VALUES (99, NULL, NULL)");
            var select = $"SELECT ID, F, D FROM {t} ORDER BY ID";

            var grid = await GridRowsAsync(it, s, select);
            Assert.Equal(FloatCases.Length + 1, grid.Count);
            foreach (var (c, row) in FloatCases.Zip(grid))
            {
                AssertFloat(c.ExpectedF, row[1], $"grid F id={N(c.Id)}");
                AssertDouble(c.ExpectedD, row[2], $"grid D id={N(c.Id)}");
            }

            Assert.Null(grid[^1][1]);
            Assert.Null(grid[^1][2]);

            var bulk = await BulkRowsAsync(it, s, select);
            Assert.Equal(FloatCases.Length + 1, bulk.Count);
            foreach (var (c, row) in FloatCases.Zip(bulk))
            {
                AssertFloat(c.ExpectedF, row[1], $"bulk F id={N(c.Id)}");
                AssertDouble(c.ExpectedD, row[2], $"bulk D id={N(c.Id)}");
            }

            Assert.Null(bulk[^1][1]);
            Assert.Null(bulk[^1][2]);
        }
        finally
        {
            await CleanupAsync(s, t);
        }
    }

    // =================================================================================================================
    // LONG
    // =================================================================================================================

    [TiberoWriteFact]
    public async Task Long_reads_the_full_text_in_the_grid_and_in_the_bulk_stream()
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

        var s = await it.OpenAsync();
        var t = TiberoItSessions.ObjectName("R1X_ALONG");
        await CleanupAsync(s, t);
        try
        {
            await ExecAsync(s, $"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, L LONG)");
            foreach (var length in LongLengths)
            {
                var text = LongText(length);
                await ExecAsync(s, $"INSERT INTO {t} (ID, L) VALUES ({N(length)}, :C1)", cmd => AddParam(s, cmd, "C1", TiberoParamKind.Long, text));
            }

            await ExecAsync(s, $"INSERT INTO {t} (ID, L) VALUES (0, NULL)");
            var select = $"SELECT ID, L FROM {t} ORDER BY ID";

            // ID 순서: 0(NULL), 1, 4001, 40000
            var grid = await GridRowsAsync(it, s, select);
            var bulk = await BulkRowsAsync(it, s, select);

            foreach (var (name, rows) in new[] { ("grid", grid), ("bulk", bulk) })
            {
                Assert.Equal(LongLengths.Length + 1, rows.Count);
                Assert.Null(rows[0][1]);
                for (var i = 0; i < LongLengths.Length; i++)
                {
                    var actual = Assert.IsType<string>(rows[i + 1][1]);
                    Assert.True(TiberoItSessions.Same(LongText(LongLengths[i]), actual), $"{name} LONG length={N(LongLengths[i])} got length={N(actual.Length)}");
                }
            }
        }
        finally
        {
            await CleanupAsync(s, t);
        }
    }

    // =================================================================================================================
    // LONG RAW
    // =================================================================================================================

    [TiberoWriteFact]
    public async Task Long_raw_reads_the_full_bytes_in_the_grid_and_in_the_bulk_stream()
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

        var s = await it.OpenAsync();
        var t = TiberoItSessions.ObjectName("R1X_ALRAW");
        await CleanupAsync(s, t);
        try
        {
            await ExecAsync(s, $"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, R LONG RAW)");
            foreach (var length in LongRawLengths)
            {
                var bytes = Pattern(length);
                await ExecAsync(s, $"INSERT INTO {t} (ID, R) VALUES ({N(length)}, :C1)", cmd => AddParam(s, cmd, "C1", TiberoParamKind.LongRaw, bytes));
            }

            await ExecAsync(s, $"INSERT INTO {t} (ID, R) VALUES (0, NULL)");
            var select = $"SELECT ID, R FROM {t} ORDER BY ID";

            var grid = await GridRowsAsync(it, s, select);
            var bulk = await BulkRowsAsync(it, s, select);

            foreach (var (name, rows) in new[] { ("grid", grid), ("bulk", bulk) })
            {
                Assert.Equal(LongRawLengths.Length + 1, rows.Count);
                Assert.Null(rows[0][1]);
                for (var i = 0; i < LongRawLengths.Length; i++)
                {
                    var actual = Assert.IsType<byte[]>(rows[i + 1][1]);
                    Assert.True(Pattern(LongRawLengths[i]).AsSpan().SequenceEqual(actual), $"{name} LONG RAW length={N(LongRawLengths[i])} got length={N(actual.Length)}");
                }
            }
        }
        finally
        {
            await CleanupAsync(s, t);
        }
    }

    // =================================================================================================================
    // 읽기 도우미
    // =================================================================================================================

    /// <summary>결과 그리드가 쓰는 경로(<see cref="TiberoExecutionService"/> → 결과 집합 첫 페이지)의 행들.</summary>
    private static async Task<List<object?[]>> GridRowsAsync(TiberoItSessions it, TiberoSession s, string sql)
    {
        var service = new TiberoExecutionService(new AllowAll(), new NoQuestions(), it.Events, null, dbmsOutput: false);
        var result = await service.ExecuteAsync(s, new SqlStatement(sql, default, StatementKind.Query), NoBinds, null, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var resultSet = Assert.IsAssignableFrom<IResultSet>(result.ResultSet);
        await using (resultSet)
        {
            var page = await resultSet.GetPageAsync(0, CancellationToken.None);
            Assert.True(page.IsLast);
            var rows = new List<object?[]>();
            for (var r = 0; r < page.RowCount; r++)
            {
                rows.Add([.. page.Columns.Select(column => column[r])]);
            }

            return rows;
        }
    }

    /// <summary>대량 읽기 경로(<see cref="TiberoBulkExecutionService.StreamRowsAsync"/>)의 행들.</summary>
    private static async Task<List<object?[]>> BulkRowsAsync(TiberoItSessions it, TiberoSession s, string sql)
    {
        var service = new TiberoBulkExecutionService(new AllowAll(), new NoQuestions(), it.Events);
        var rows = new List<object?[]>();
        await foreach (var row in service.StreamRowsAsync(s, new SqlStatement(sql, default, StatementKind.Query), NoBinds, ExecutionOptions.Default,
                           CancellationToken.None))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static void AssertFloat(float expected, object? actual, string what)
    {
        var value = Assert.IsType<float>(actual);
        if (float.IsNaN(expected))
        {
            Assert.True(float.IsNaN(value), what);
            return;
        }

        Assert.True(BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(value), $"{what}: expected {expected.ToString("R", CultureInfo.InvariantCulture)} got {value.ToString("R", CultureInfo.InvariantCulture)}");
    }

    private static void AssertDouble(double expected, object? actual, string what)
    {
        var value = Assert.IsType<double>(actual);
        if (double.IsNaN(expected))
        {
            Assert.True(double.IsNaN(value), what);
            return;
        }

        Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(value), $"{what}: expected {expected.ToString("R", CultureInfo.InvariantCulture)} got {value.ToString("R", CultureInfo.InvariantCulture)}");
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string LongText(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = (char)('a' + (i % 26));
        }

        return new string(chars);
    }

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    // =================================================================================================================
    // 실행 도우미
    // =================================================================================================================

    private static void AddParam(TiberoSession s, DbCommand cmd, string name, TiberoParamKind kind, object? value)
    {
        var driver = s.Driver ?? throw new InvalidOperationException("이 시험은 실제 드라이버 세션이 필요하다.");
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        driver.SetParameterKind(p, kind);
        p.Direction = ParameterDirection.Input;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private static async Task<int> ExecAsync(TiberoSession s, string sql, Action<DbCommand>? prepare = null)
    {
        await s.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var cmd = s.CreateCommand(sql);
            prepare?.Invoke(cmd);
            return await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            s.CommandGate.Release();
        }
    }

    private static async Task<bool> SilentAsync(TiberoSession s, string sql)
    {
        try
        {
            await ExecAsync(s, sql);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task CleanupAsync(TiberoSession s, string table)
    {
        await SilentAsync(s, "ROLLBACK");
        if (!await SilentAsync(s, $"DROP TABLE {table} CASCADE CONSTRAINTS PURGE"))
        {
            await SilentAsync(s, $"DROP TABLE {table} CASCADE CONSTRAINTS");
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
