using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Transfer.Copy;
using Xunit.Abstractions;

namespace Oranda.Oracle.IntegrationTests.Transfer.Copy;

// 소유: 데이터 복사 PF2b(같은 시점 읽기 + 동시 테이블 복사). 이 PC 에서는 컴파일까지만 확인한다. 실행은 CI·다른 PC 의 컨테이너에서 한다.

/// <summary>
/// 같은 시점 읽기를 켠 채 동시 복사(PF2b)를 실제 서버에서: 실행을 시작할 때 원본 SCN 하나를 잡아 모든 레인이 <c>AS OF SCN</c> 으로 읽는다.
/// <list type="bullet">
/// <item>시점 증거: SCN 을 잡았다는 로그가 나온 순간(정리·적재 전) 다른 세션이 원본에 행을 넣고 커밋한다 — 대상 건수가 넣기 전 건수와 같아야 한다.</item>
/// <item>다른 계정의 원본 테이블: FLASHBACK 권한이 없으면 사전 점검 오류(FlashbackNoPrivilege), 권한을 주면 복사된다.</item>
/// <item>SCN 읽는 방법: DBMS_FLASHBACK EXECUTE 권한이 있으면 정확한 SCN(GET_SYSTEM_CHANGE_NUMBER), 없으면 TIMESTAMP_TO_SCN(SYSTIMESTAMP).</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class CopyReadPointTests(OracleFreeFixture db, ITestOutputHelper output)
{
    private static readonly ResolvedPeriod NoPeriod = new(null, null);

    /// <summary>
    /// 표를 만든 직후의 SCN 으로는 그 표를 읽지 못한다(ORA-01466, DDL 시각→SCN 대응이 초 단위). TIMESTAMP_TO_SCN 은 몇 초 앞 SCN 일 수 있어
    /// <see cref="CopyDbScope"/> 의 DDL 뒤 기다림(3초)보다 넉넉히 기다린 뒤 실행한다.
    /// </summary>
    private static readonly TimeSpan PointSettle = TimeSpan.FromSeconds(8);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Consistent_read_with_lanes_reads_every_table_as_of_the_start_scn(int lanes)
    {
        await using var scope = await CopyDbScope.CreateAsync(db, CopyDbScope.NewTag());
        foreach (var create in new Func<string, string, Task>[] { scope.CreateSourceTableAsync, scope.CreateTargetTableAsync })
        {
            await create("PF2B_P", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)");
            await create("PF2B_C", "ID NUMBER PRIMARY KEY, P_ID NUMBER CONSTRAINT FK_PF2B_C REFERENCES PF2B_P(ID), NAME VARCHAR2(20)");
            await create("PF2B_X", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)");
            await create("PF2B_Y", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)");
        }

        await scope.ExecSourceAsync(Block(
            "INSERT INTO PF2B_P SELECT LEVEL, 'p ' || LEVEL FROM DUAL CONNECT BY LEVEL <= 2000",
            "INSERT INTO PF2B_C SELECT LEVEL, MOD(LEVEL, 2000) + 1, 'c ' || LEVEL FROM DUAL CONNECT BY LEVEL <= 4000",
            "INSERT INTO PF2B_X SELECT LEVEL, 'x ' || LEVEL FROM DUAL CONNECT BY LEVEL <= 3000",
            "INSERT INTO PF2B_Y SELECT LEVEL, 'y ' || LEVEL FROM DUAL CONNECT BY LEVEL <= 1500"));
        await Task.Delay(PointSettle);

        var job = scope.Job(
            new TableRule("PF2B_C", RuleKind.All, TargetAction.DeleteAllInsert),
            new TableRule("PF2B_P", RuleKind.All, TargetAction.DeleteAllInsert),
            new TableRule("PF2B_X", RuleKind.All, TargetAction.DeleteAllInsert),
            new TableRule("PF2B_Y", RuleKind.All, TargetAction.DeleteAllInsert)) with
        {
            ConsistentRead = true, // PF2b: 같은 시점 읽기를 켠 채 동시에
            Options = new CopyOptions { BatchSize = 500, ConcurrentTables = lanes },
        };
        var main = await scope.OpenSessionsAsync();
        var report = await PrecheckAsync(scope, main, job);
        Assert.Equal(CheckLevel.Confirm, Assert.Single(report.Items, i => i.Code == CopyCheckCodes.ConcurrentCopy).Level);
        Assert.Contains("AS OF SCN :ORANDA_SCN", Assert.Single(report.Items, i => i.Code == CopyCheckCodes.ReadPoint).Message, StringComparison.Ordinal);
        Assert.All(report.Preview.Where(p => p.Side == PreviewSide.Source), p => Assert.Contains(" AS OF SCN :ORANDA_SCN", p.Sql, StringComparison.Ordinal));
        var decision = CopyConcurrency.ForRun(job, report, Dbms.Oracle);
        Assert.True(decision.ReadsAtPoint);

        var workers = new List<CopySessions>();
        for (var i = 1; i < decision.Tables; i++)
        {
            workers.Add(await scope.OpenSessionsAsync());
        }

        var progress = new LogCollector(entry => entry.Text.StartsWith("→ 원본 SCN ", StringComparison.Ordinal)
            ? scope.ExecSourceAsync(Block(
                "INSERT INTO PF2B_X SELECT 100000 + LEVEL, 'late' FROM DUAL CONNECT BY LEVEL <= 50",
                "INSERT INTO PF2B_P SELECT 100000 + LEVEL, 'late' FROM DUAL CONNECT BY LEVEL <= 20",
                "UPDATE PF2B_Y SET NAME = 'late'"))
            : null);
        var runner = new OracleCopyRunner(scope.Bulk(), new OracleExecutionService(new ExecutionPolicy(), scope.Ui, scope.Bus, null, pageSize: 500), new NoRunLog());
        var result = await runner.RunAsync(main with { Workers = workers }, job, report, Decisions(report), NoPeriod, progress, new CopyRunControl(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromMinutes(5));

        output.WriteLine(Summary(result));
        output.WriteLine(string.Join(Environment.NewLine, progress.Logs.Where(l => l.Text.Contains("SCN", StringComparison.Ordinal)).Select(l => l.Text)));
        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        Assert.StartsWith("SCN ", result.ReadPoint, StringComparison.Ordinal);
        Assert.Equal(2000, await scope.CountAsync(scope.DstUser, "PF2B_P")); // 늦게 넣은 20행은 없다
        Assert.Equal(4000, await scope.CountAsync(scope.DstUser, "PF2B_C"));
        Assert.Equal(3000, await scope.CountAsync(scope.DstUser, "PF2B_X")); // 늦게 넣은 50행은 없다
        Assert.Equal(2020, await scope.CountAsync(scope.SrcUser, "PF2B_P")); // 원본에는 들어갔다
        Assert.Equal(0, await scope.CountAsync(scope.DstUser, "PF2B_Y", "NAME = 'late'")); // 늦은 UPDATE 도 보이지 않는다
        Assert.Contains(progress.Logs, l => l.Text.Contains(" AS OF SCN :ORANDA_SCN", StringComparison.Ordinal) && l.Text.StartsWith("원본: SELECT", StringComparison.Ordinal));
        Assert.Contains(progress.Logs, l => l.Text == "대상: COMMIT  — 동시 복사 전에 정리(DELETE) 확정");
    }

    [Fact]
    public async Task Another_accounts_table_needs_flashback_and_a_grant_lets_it_copy()
    {
        await using var scope = await CopyDbScope.CreateAsync(db, CopyDbScope.NewTag());
        await scope.ExecAppAsync("CREATE TABLE CODES (ID NUMBER PRIMARY KEY, NAME VARCHAR2(20))");
        await scope.ExecAppAsync(Block("INSERT INTO CODES SELECT LEVEL, 'code ' || LEVEL FROM DUAL CONNECT BY LEVEL <= 300"));
        await scope.ExecAppAsync($"GRANT SELECT ON CODES TO {scope.SrcUser}"); // SELECT 만(FLASHBACK 없음)
        await scope.CreateSourceTableAsync("PF2B_OWN", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)");
        await scope.ExecSourceAsync(Block("INSERT INTO PF2B_OWN SELECT LEVEL, 'own ' || LEVEL FROM DUAL CONNECT BY LEVEL <= 200"));
        await scope.CreateTargetTableAsync("CODES", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)");
        await scope.CreateTargetTableAsync("PF2B_OWN", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)");

        var job = scope.Job(
            new TableRule("CODES", RuleKind.All, TargetAction.DeleteAllInsert) { SourceOwner = scope.AppUser, TargetOwner = scope.DstUser },
            new TableRule("PF2B_OWN", RuleKind.All, TargetAction.DeleteAllInsert)) with
        {
            ConsistentRead = true,
            Options = new CopyOptions { ConcurrentTables = 2 },
        };
        var main = await scope.OpenSessionsAsync();
        var denied = await scope.Precheck().RunAsync(main, job, NoPeriod, null, CancellationToken.None);
        var error = Assert.Single(denied.Items, i => i.Code == CopyCheckCodes.FlashbackNoPrivilege);
        Assert.Equal($"{scope.AppUser}.CODES", error.Table);
        Assert.Contains("FLASHBACK", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(denied.Items, i => i.Code == CopyCheckCodes.FlashbackNoPrivilege && i.Table == "PF2B_OWN"); // 자기 테이블은 권한이 필요 없다

        await scope.ExecAppAsync($"GRANT FLASHBACK ON CODES TO {scope.SrcUser}");
        await Task.Delay(PointSettle);
        var report = await PrecheckAsync(scope, main, job);
        var worker = await scope.OpenSessionsAsync();
        var runner = new OracleCopyRunner(scope.Bulk(), new OracleExecutionService(new ExecutionPolicy(), scope.Ui, scope.Bus, null, pageSize: 500), new NoRunLog());
        var result = await runner.RunAsync(main with { Workers = [worker] }, job, report, Decisions(report), NoPeriod, null, new CopyRunControl(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromMinutes(5));

        output.WriteLine(Summary(result));
        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        Assert.NotNull(result.ReadPoint);
        Assert.Equal(300, await scope.CountAsync(scope.DstUser, "CODES"));
        Assert.Equal(200, await scope.CountAsync(scope.DstUser, "PF2B_OWN"));
    }

    [Theory]
    [InlineData(true, "DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER")]
    [InlineData(false, "TIMESTAMP_TO_SCN(SYSTIMESTAMP)")]
    public async Task Scn_is_read_exactly_with_dbms_flashback_or_without_privileges_by_timestamp(bool grantDbmsFlashback, string method)
    {
        await using var scope = await CopyDbScope.CreateAsync(db, CopyDbScope.NewTag());
        foreach (var table in new[] { "PF2B_A", "PF2B_B" })
        {
            await scope.CreateSourceTableAsync(table, "ID NUMBER PRIMARY KEY");
            await scope.CreateTargetTableAsync(table, "ID NUMBER PRIMARY KEY");
            await scope.ExecSourceAsync(Block($"INSERT INTO {table} SELECT LEVEL FROM DUAL CONNECT BY LEVEL <= 100"));
        }

        if (grantDbmsFlashback)
        {
            await scope.ExecAdminAsync($"GRANT EXECUTE ON SYS.DBMS_FLASHBACK TO {scope.SrcUser}");
        }

        await Task.Delay(PointSettle);
        var job = scope.Job(new TableRule("PF2B_A", RuleKind.All, TargetAction.DeleteAllInsert), new TableRule("PF2B_B", RuleKind.All, TargetAction.DeleteAllInsert)) with
        {
            ConsistentRead = true,
            Options = new CopyOptions { ConcurrentTables = 2 },
        };
        var main = await scope.OpenSessionsAsync();
        var report = await PrecheckAsync(scope, main, job);
        var worker = await scope.OpenSessionsAsync();
        var progress = new LogCollector(null);
        var runner = new OracleCopyRunner(scope.Bulk(), new OracleExecutionService(new ExecutionPolicy(), scope.Ui, scope.Bus, null, pageSize: 500), new NoRunLog());
        var result = await runner.RunAsync(main with { Workers = [worker] }, job, report, Decisions(report), NoPeriod, progress, new CopyRunControl(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromMinutes(5));

        output.WriteLine(Summary(result));
        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        var line = Assert.Single(progress.Logs, l => l.Text.StartsWith("→ 원본 SCN ", StringComparison.Ordinal));
        output.WriteLine(line.Text);
        Assert.EndsWith($"(AS OF SCN :ORANDA_SCN, {method})", line.Text, StringComparison.Ordinal);
    }

    // ---- 도우미 ----

    private static async Task<PrecheckReport> PrecheckAsync(CopyDbScope scope, CopySessions sessions, CopyJob job)
    {
        var report = await scope.Precheck().RunAsync(sessions, job, NoPeriod, null, CancellationToken.None);
        Assert.False(report.HasErrors, string.Join(Environment.NewLine, report.Items.Where(i => i.Level == CheckLevel.Error).Select(i => i.Code + ": " + i.Message)));
        return report;
    }

    private static CopyDecisions Decisions(PrecheckReport report)
        => new(new HashSet<string>(StringComparer.Ordinal), new HashSet<ForeignKeyRef>(),
            report.Preview.Where(p => p.Policy == PolicyDecisionKind.Confirm).Select(p => p.Sql).ToHashSet(StringComparer.Ordinal),
            ProceedSequenceGap: false);

    private static string Block(params string[] statements)
        => "BEGIN " + string.Join(' ', statements.Select(s => s.TrimEnd(';') + ";")) + " COMMIT; END;";

    private static string Summary(CopyRunResult result)
        => result.Outcome + " " + result.ReadPoint + ": " + string.Join(" / ", result.Tables.Select(t => t.Table + "=" + t.Outcome + " " + t.Message))
           + " | notices: " + string.Join(" / ", result.Notices);

    /// <summary>
    /// 작업 로그 모음. <paramref name="onLog"/> 는 로그 줄마다 실행기 흐름 안에서 부르고, 돌려준 작업이 끝날 때까지 실행기를 붙잡는다(시점을 잡은 순간 원본을 바꾸는 시험).
    /// </summary>
    private sealed class LogCollector(Func<CopyLogEntry, Task?>? onLog) : IProgress<CopyProgress>
    {
        private readonly List<CopyLogEntry> _logs = [];

        public IReadOnlyList<CopyLogEntry> Logs
        {
            get { lock (_logs) { return [.. _logs]; } }
        }

        public void Report(CopyProgress value)
        {
            if (value.Log is { } entry)
            {
                lock (_logs)
                {
                    _logs.Add(entry);
                }

                onLog?.Invoke(entry)?.GetAwaiter().GetResult();
            }
        }
    }

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
