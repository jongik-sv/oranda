using System.Globalization;
using System.Runtime.ExceptionServices;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Transfer.Copy;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// Tibero 원본의 같은 시점 읽기 + 동시 복사(PF2b2, 조정자 회차 xdb-copy 결정 100)를 실제 서버로 잰다. 쓰기 시험(<c>ORANDA_IT_TIBERO_WRITE=1</c>,
/// <c>ORANDA_IT_TIBERO_*</c>, <c>ORANDA_TIBERO_DRIVER</c> 가 있을 때만), 첫 계정 하나만 쓰고 <c>ORT_PF2B2_</c> 이름만 만든다(두 번째 계정 없음).
/// <list type="bullet">
/// <item>같은 계정 복사는 실행기의 같은 DB 가드가 막으므로(<c>TiberoCopySameAccountIt</c>) 실행기 전체가 아니라 실행기의 레인 경로를 실제 부품으로 돈다:
/// 시작 TSN = <see cref="TiberoCopyDialect.ReadPoint"/> 의 조회를 실행기와 같은 차례로(대량 실행 <see cref="TiberoBulkExecutionService"/>, 서버 오류면 다음 방법),
/// 레인 2개 = 원본 세션(읽기 전용 프로필 + 읽기 전용 블록)·대상 세션 쌍 둘이 동시에 테이블 하나씩 <see cref="CopySqlBuilder.SourceSelect"/>
/// (<c>AS OF SCN :ORANDA_SCN</c> + 바인드)로 읽어 대상 테이블(<c>…_D</c>)에 배열 INSERT·COMMIT.</item>
/// <item>시점 증거: 시작 TSN 을 잡은 뒤, 레인이 읽기 전에 원본에 행을 넣고 커밋한다 — 대상에 그 행이 없어야 한다(원본에는 있다).</item>
/// <item>오류 번호 실측(단언 없음, 출력만): 미래 TSN·아주 옛 TSN(1)·TSN 뒤 DDL 을 같은 글로 읽어 보고 Tibero 오류 번호·분류·복사 안내 갈래
/// (<see cref="CopyReadPointErrors.Describe"/>)를 남긴다. 이 결과로 정의 바뀜·잘못된 TSN 번호를 번호표에 넣을지 정한다.</item>
/// <item>끝에 만든 테이블 넷을 <c>DROP … PURGE</c> 한다(처음에도). 접속 값(계정·DB·호스트)과 서버 메시지는 출력·단언 메시지에 넣지 않는다(코드·분류만).</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class TiberoCopyReadPointIt(ITestOutputHelper output)
{
    private static readonly TiberoCopyDialect Dialect = TiberoCopyDialect.Instance;

    private static readonly string SourceA = TiberoItSessions.ObjectName("PF2B2_A");
    private static readonly string SourceB = TiberoItSessions.ObjectName("PF2B2_B");
    private static readonly string TargetA = TiberoItSessions.ObjectName("PF2B2_A_D");
    private static readonly string TargetB = TiberoItSessions.ObjectName("PF2B2_B_D");
    private static readonly string[] Tables = [SourceA, SourceB, TargetA, TargetB];

    private static readonly ExecutionOptions Options = new() { Source = HistorySource.DataCopy, SkipHistory = true };

    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();

    /// <summary>원본에 처음 넣는 행 수(A·B)와 시작 TSN 뒤에 넣는 행 수.</summary>
    private const int RowsA = 300, RowsB = 200, LateRows = 50;

    /// <summary>늦게 넣는 행의 ID 시작(이 값보다 큰 ID 가 대상에 있으면 시작 TSN 뒤 데이터를 읽은 것).</summary>
    private const int LateBase = 100000;

    [TiberoWriteFact]
    public async Task Two_lanes_read_every_table_as_of_one_start_tsn_and_rows_added_after_it_are_not_copied()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var setup = await it.OpenAsync();
        await CleanupAsync(setup);
        Exception? failure = null;
        try
        {
            await BodyAsync(it, setup, it.Env.User.ToUpperInvariant());
        }
#pragma warning disable CA1031 // 본문 실패를 잡아 두고 정리한 뒤 그대로 다시 던진다(정리가 본문 실패를 가리지 않게).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex;
        }

        await CleanupAsync(setup);
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        var names = string.Join(", ", Tables.Select(t => "'" + t + "'"));
        Assert.True(await CountAsync(setup, $"SELECT COUNT(*) FROM user_tables WHERE table_name IN ({names})") == 0, "ORT_PF2B2_ cleanup failed: a test table is left");
    }

    private async Task BodyAsync(TiberoItSessions it, TiberoSession setup, string owner)
    {
        // ---- 준비: 원본 둘·대상 둘(같은 구조), 원본에 행 ----
        foreach (var table in Tables)
        {
            await ExecQuietAsync(setup, "create " + table, $"CREATE TABLE {table} (ID NUMBER PRIMARY KEY, NM VARCHAR2(20))");
        }

        await ExecQuietAsync(setup, "fill A", $"INSERT INTO {SourceA} (ID, NM) SELECT LEVEL, 'a ' || LEVEL FROM DUAL CONNECT BY LEVEL <= {RowsA}");
        await ExecQuietAsync(setup, "fill B", $"INSERT INTO {SourceB} (ID, NM) SELECT LEVEL, 'b ' || LEVEL FROM DUAL CONNECT BY LEVEL <= {RowsB}");
        await ExecQuietAsync(setup, "commit fill", "COMMIT");
        await Task.Delay(TimeSpan.FromSeconds(8)); // 표를 만든 직후의 시점으로는 읽지 못할 수 있다(Oracle ORA-01466 과 같은 규칙인지 모름, 프로브 IT 와 같은 기다림)

        var policy = new AllowAll();
        var bulk = new TiberoBulkExecutionService(policy, new NoQuestions(), it.Events);
        var codec = new TiberoCopyParts(bulk).Parts.Codec;
        var readOnly = it.Profile() with { ReadOnly = true };

        // ---- 시작 TSN: 실행기와 같은 차례(서버 오류면 다음 방법) ----
        var main = await it.OpenAsync(SessionPurpose.Dedicated, readOnly);
        var support = Assert.IsType<CopyReadPointSupport>(((ICopyDialect)Dialect).ReadPoint);
        CopyReadPoint? point = null;
        foreach (var query in support.Queries)
        {
            try
            {
                long? number = null;
                await foreach (var row in bulk.StreamRowsAsync(main, CopyCommonStatements.Query(query.Sql), NoBinds, Options, CancellationToken.None))
                {
                    number ??= row.Length > 0 ? codec.ToInt64(row[0]) : null;
                }

                output.WriteLine($"{query.Method}: {(number is { } n ? "ok" : "no value")}");
                if (number is > 0)
                {
                    point = new CopyReadPoint(number.Value, support);
                    output.WriteLine("start point taken by " + query.Method);
                    break;
                }
            }
#pragma warning disable CA1031 // 실측: 방법마다 되는지만 남긴다(형식 이름·오류 번호만). 실행기도 서버 오류면 다음 방법으로 간다.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                output.WriteLine($"{query.Method}: failed {Describe(ex)}");
            }
        }

        Assert.True(point is not null, "no way to read the current TSN worked (see output)");

        // ---- 시작 TSN 뒤에 원본에 행을 넣고 커밋(레인이 읽기 전) ----
        await ExecQuietAsync(setup, "late A", $"INSERT INTO {SourceA} (ID, NM) SELECT {LateBase} + LEVEL, 'late' FROM DUAL CONNECT BY LEVEL <= {LateRows}");
        await ExecQuietAsync(setup, "late B", $"INSERT INTO {SourceB} (ID, NM) SELECT {LateBase} + LEVEL, 'late' FROM DUAL CONNECT BY LEVEL <= {LateRows}");
        await ExecQuietAsync(setup, "commit late", "COMMIT");

        // ---- 레인 2개: 테이블 하나씩 동시에, 같은 시작 TSN 으로 읽어 대상에 넣는다 ----
        var job = new CopyJob
        {
            Id = "pf2b2-it", Name = "pf2b2-it", Source = new ConnectionRef("a", owner), Target = new ConnectionRef("b", owner),
            SourceOwner = owner, TargetOwner = owner, MaskSalt = "salt", ConsistentRead = true,
            Tables = [new TableRule(SourceA, RuleKind.All, TargetAction.DeleteAllInsert), new TableRule(SourceB, RuleKind.All, TargetAction.DeleteAllInsert)],
            Options = new CopyOptions { ConcurrentTables = 2 },
        };
        Assert.True(CopyConcurrency.Effective(job, report: null, Oranda.Core.Connection.Dbms.Tibero).ReadsAtPoint, "consistent read with two tables must read at the start point");
        var info = job.Tables.ToDictionary(r => CopyOwners.Key(job, r), r => new TableInfo(new DbObjectRef(owner, r.Table, "TABLE"),
            [new ColumnInfo("ID", 1, "NUMBER"), new ColumnInfo("NM", 2, "VARCHAR2(20)")]), StringComparer.Ordinal);
        var builder = new CopySqlBuilder(job, new ResolvedPeriod(null, null), info, info);

        var lanes = new List<(TiberoSession Source, TiberoSession Target)>();
        for (var i = 0; i < 2; i++)
        {
            lanes.Add((await it.OpenAsync(SessionPurpose.Dedicated, readOnly), await it.OpenAsync(SessionPurpose.Dedicated)));
        }

        var copied = await Task.WhenAll(
            LaneAsync(bulk, builder, point!, lanes[0], CopyOwners.Key(job, job.Tables[0]), owner, TargetA),
            LaneAsync(bulk, builder, point!, lanes[1], CopyOwners.Key(job, job.Tables[1]), owner, TargetB));
        output.WriteLine($"lane rows: A {copied[0].ToString(CultureInfo.InvariantCulture)}, B {copied[1].ToString(CultureInfo.InvariantCulture)}");

        Assert.True(await CountAsync(setup, $"SELECT COUNT(*) FROM {TargetA}") == RowsA, "target A must have only the rows before the start TSN");
        Assert.True(await CountAsync(setup, $"SELECT COUNT(*) FROM {TargetB}") == RowsB, "target B must have only the rows before the start TSN");
        Assert.True(await CountAsync(setup, $"SELECT COUNT(*) FROM {TargetA} WHERE ID > {LateBase}") == 0, "a row added after the start TSN was copied (A)");
        Assert.True(await CountAsync(setup, $"SELECT COUNT(*) FROM {TargetB} WHERE ID > {LateBase}") == 0, "a row added after the start TSN was copied (B)");
        Assert.True(await CountAsync(setup, $"SELECT COUNT(*) FROM {SourceA}") == RowsA + LateRows, "the late rows must be visible in the source now (A)");
        Assert.True(await CountAsync(setup, $"SELECT COUNT(*) FROM {SourceB}") == RowsB + LateRows, "the late rows must be visible in the source now (B)");

        // ---- 오류 번호 실측(출력만): 같은 글(AS OF SCN :ORANDA_SCN)로 읽기 실패를 만든다 ----
        var key = CopyOwners.Key(job, job.Tables[0]);
        await ProbeAsync(bulk, main, builder, key, new CopyReadPoint(point!.Number + 1_000_000_000_000L, support), "future TSN");
        await ProbeAsync(bulk, main, builder, key, new CopyReadPoint(1, support), "TSN 1");
        await ExecQuietAsync(setup, "ddl after the point", $"ALTER TABLE {SourceA} ADD (X NUMBER)");
        await ProbeAsync(bulk, main, builder, key, point, "DDL after the TSN");
    }

    /// <summary>
    /// 레인 하나: 원본 세션에 읽기 전용 블록(실행기처럼 ROLLBACK 뒤), 시작 TSN 으로 원본 SELECT 를 흘려 대상 테이블에 배열 INSERT(바인드 자리 = Tibero 방언) 후 COMMIT.
    /// 돌려주는 값 = 읽은 행 수.
    /// </summary>
    private static async Task<int> LaneAsync(TiberoBulkExecutionService bulk, CopySqlBuilder builder, CopyReadPoint point,
                                             (TiberoSession Source, TiberoSession Target) lane, string key, string owner, string target)
    {
        await RequireAsync(bulk.ExecuteArrayAsync(lane.Source, CopyCommonStatements.Rollback(), [], [], Options, CancellationToken.None), "source rollback");
        await RequireAsync(bulk.ExecuteArrayAsync(lane.Source, Dialect.SetTransactionReadOnly(), [], [], Options, CancellationToken.None), "read-only block");

        var select = builder.SourceSelect(key, ["ID", "NM"], point);
        Assert.True(select.Sql.Contains(" AS OF SCN :" + CopyReadPoint.BindName, StringComparison.Ordinal), "the source select has no AS OF clause");
        var rows = new List<object?[]>();
        await foreach (var row in bulk.StreamRowsAsync(lane.Source, CopyCommonStatements.Query(select), select.Binds, Options, CancellationToken.None))
        {
            rows.Add(row);
        }

        var columns = new[] { new BulkColumn("C1", "NUMBER"), new BulkColumn("C2", "VARCHAR2(20)") };
        var insert = $"INSERT INTO {CopyCommonStatements.Qualified(owner, target)} (\"ID\", \"NM\") VALUES ({Dialect.BindPlaceholder("C1", "NUMBER")}, {Dialect.BindPlaceholder("C2", "VARCHAR2(20)")})";
        await RequireAsync(bulk.ExecuteArrayAsync(lane.Target, CopyCommonStatements.ArrayDml(insert, 2), columns, rows, Options, CancellationToken.None), "target insert");
        await RequireAsync(bulk.ExecuteArrayAsync(lane.Target, CopyCommonStatements.Commit(), [], [], Options, CancellationToken.None), "target commit");
        await RequireAsync(bulk.ExecuteArrayAsync(lane.Source, CopyCommonStatements.Rollback(), [], [], Options, CancellationToken.None), "source rollback after the read");
        return rows.Count;
    }

    /// <summary>원본 SELECT 를 주어진 시점으로 읽어 보고, 성공이면 행 수, 실패면 Tibero 오류 번호·분류·복사 안내 갈래만 출력한다(서버 메시지는 찍지 않는다).</summary>
    private async Task ProbeAsync(TiberoBulkExecutionService bulk, TiberoSession session, CopySqlBuilder builder, string key, CopyReadPoint point, string label)
    {
        var select = builder.SourceSelect(key, ["ID"], point);
        try
        {
            await RequireAsync(bulk.ExecuteArrayAsync(session, CopyCommonStatements.Rollback(), [], [], Options, CancellationToken.None), "probe rollback");
            var count = 0;
            await foreach (var _ in bulk.StreamRowsAsync(session, CopyCommonStatements.Query(select), select.Binds, Options, CancellationToken.None))
            {
                count++;
            }

            output.WriteLine($"{label}: no error, rows {count.ToString(CultureInfo.InvariantCulture)}");
        }
#pragma warning disable CA1031 // 실측: 오류 번호·분류만 남긴다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var error = TiberoErrorClassifier.ToDbError(ex, select.Sql);
            var branch = error is null ? "not a driver error"
                : CopyReadPointErrors.Describe(key, point, error) is not { } failure ? "old handling (table fails)"
                : failure.AbortRun ? "read point failure, run stops" : "read point failure, table fails";
            output.WriteLine($"{label}: failed {Describe(ex)} kind {error?.Kind.ToString() ?? "-"} -> {branch}");
        }
    }

    private static async Task RequireAsync(Task<BulkResult> call, string what)
    {
        var result = await call;
        Assert.True(result.Status == ExecutionStatus.Succeeded, $"{what}: {result.Status} {result.Error?.Code}");
    }

    // ---- 도우미 ----

    /// <summary>미커밋 변경을 되돌리고 이 시험의 테이블을 지운다(PURGE). 오류는 무시한다(없는 객체 등).</summary>
    private static async Task CleanupAsync(TiberoSession session)
    {
        await IgnoreAsync(session, "ROLLBACK");
        foreach (var table in Tables)
        {
            await IgnoreAsync(session, $"DROP TABLE \"{table}\" CASCADE CONSTRAINTS PURGE");
        }
    }

    private static async Task IgnoreAsync(TiberoSession session, string sql)
    {
        try
        {
            await ExecAsync(session, sql);
        }
#pragma warning disable CA1031 // 정리 단계: 오류는 무시한다.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static Task<long> CountAsync(TiberoSession session, string sql) =>
        QuietAsync("count query", async () => TiberoSessionQuery.Long(await TiberoSessionQuery.ScalarAsync(session, sql)) ?? 0);

    /// <summary>예외 메시지에는 <c>소유자.이름</c>(접속 계정)이 실릴 수 있다. 형식 이름과 서버 오류 번호만 담아 다시 던진다.</summary>
    private static async Task<T> QuietAsync<T>(string what, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
#pragma warning disable CA1031 // 시험 실패로 바꿔 던진다(메시지에서 접속 계정을 뺀다).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            throw new Xunit.Sdk.XunitException(what + " failed: " + Describe(ex));
        }
    }

    private static string Describe(Exception ex)
    {
        var code = TiberoErrorClassifier.ToDbError(ex, null)?.Code;
        return ex.GetType().Name + (code is null ? string.Empty : " " + code);
    }

    private static async Task ExecQuietAsync(TiberoSession session, string what, string sql) =>
        await QuietAsync(what, async () => { await ExecAsync(session, sql); return true; });

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
