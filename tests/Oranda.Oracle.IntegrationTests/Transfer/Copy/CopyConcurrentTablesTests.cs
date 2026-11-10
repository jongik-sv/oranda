using System.Diagnostics;
using System.Globalization;
using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Transfer.Copy;
using Xunit.Abstractions;

namespace Oranda.Oracle.IntegrationTests.Transfer.Copy;

// 소유: 데이터 복사 PF2(동시 테이블 수). 이 PC 에서는 컴파일까지만 확인한다. 실행은 CI·다른 PC 의 컨테이너에서 한다.

/// <summary>
/// 동시 테이블 복사(PF2)를 실제 서버에서: 레인 세션 쌍 N 개(주 쌍 + 추가 N-1 쌍)로 FK 부모·자식이 있는 작업을 복사한다. 대상 FK 를 켠 채라
/// 자식 단계가 부모 커밋 전에 돌면 ORA-02291 로 실패한다 — 성공이 단계 순서의 증거다. 측정(N=1 대 N=4, 기본 건너뜀)은 <see cref="CopyMeasureFactAttribute"/>.
/// </summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class CopyConcurrentTablesTests(OracleFreeFixture db, ITestOutputHelper output)
{
    private static readonly ResolvedPeriod NoPeriod = new(null, null);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Parents_and_children_with_foreign_keys_copy_in_lanes_stage_by_stage(int lanes)
    {
        await using var scope = await CopyDbScope.CreateAsync(db, CopyDbScope.NewTag());
        foreach (var create in new Func<string, string, Task>[] { scope.CreateSourceTableAsync, scope.CreateTargetTableAsync })
        {
            await create("PF2_P1", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)");
            await create("PF2_P2", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)");
            await create("PF2_C1", "ID NUMBER PRIMARY KEY, P_ID NUMBER CONSTRAINT FK_PF2_C1 REFERENCES PF2_P1(ID), NAME VARCHAR2(20)");
            await create("PF2_C2", "ID NUMBER PRIMARY KEY, P_ID NUMBER CONSTRAINT FK_PF2_C2 REFERENCES PF2_P2(ID), NAME VARCHAR2(20)");
            await create("PF2_X", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)");
        }

        await scope.ExecSourceAsync(Block(
            "INSERT INTO PF2_P1 SELECT LEVEL, 'p1 ' || LEVEL FROM DUAL CONNECT BY LEVEL <= 3000",
            "INSERT INTO PF2_P2 SELECT LEVEL, 'p2 ' || LEVEL FROM DUAL CONNECT BY LEVEL <= 2000",
            "INSERT INTO PF2_C1 SELECT LEVEL, MOD(LEVEL, 3000) + 1, 'c1 ' || LEVEL FROM DUAL CONNECT BY LEVEL <= 6000",
            "INSERT INTO PF2_C2 SELECT LEVEL, MOD(LEVEL, 2000) + 1, 'c2 ' || LEVEL FROM DUAL CONNECT BY LEVEL <= 4000",
            "INSERT INTO PF2_X SELECT LEVEL, 'x ' || LEVEL FROM DUAL CONNECT BY LEVEL <= 2500"));
        // 대상에 옛 행: 정리(DELETE, 자식 → 부모)가 지우고 확정한 뒤 레인이 넣는다(같은 키라 지우지 않았으면 ORA-00001).
        await scope.ExecTargetAsync(Block(
            "INSERT INTO PF2_P1 SELECT LEVEL, 'old' FROM DUAL CONNECT BY LEVEL <= 10",
            "INSERT INTO PF2_C1 SELECT LEVEL, LEVEL, 'old' FROM DUAL CONNECT BY LEVEL <= 10",
            "INSERT INTO PF2_X SELECT LEVEL, 'old' FROM DUAL CONNECT BY LEVEL <= 10"));

        var job = scope.Job(
            new TableRule("PF2_C1", RuleKind.All, TargetAction.DeleteAllInsert),
            new TableRule("PF2_C2", RuleKind.All, TargetAction.DeleteAllInsert),
            new TableRule("PF2_P1", RuleKind.All, TargetAction.DeleteAllInsert),
            new TableRule("PF2_P2", RuleKind.All, TargetAction.DeleteAllInsert),
            new TableRule("PF2_X", RuleKind.All, TargetAction.DeleteAllInsert)) with
        {
            ConsistentRead = false, // 같은 시점 읽기를 켜면 동시 복사를 하지 않는다
            Options = new CopyOptions { BatchSize = 500, ConcurrentTables = lanes },
        };
        var main = await scope.OpenSessionsAsync();
        var report = await PrecheckAsync(scope, main, job);
        var confirm = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.ConcurrentCopy);
        Assert.Equal(CheckLevel.Confirm, confirm.Level);
        Assert.Contains(CopyConcurrency.DeleteCommitWarning, confirm.Message, StringComparison.Ordinal);
        var stages = Assert.IsAssignableFrom<IReadOnlyList<IReadOnlyList<string>>>(report.Stages);
        Assert.Equal(["PF2_P1", "PF2_P2", "PF2_X"], stages[0].Order(StringComparer.Ordinal));
        Assert.Equal(["PF2_C1", "PF2_C2"], stages[1].Order(StringComparer.Ordinal));

        var workers = new List<CopySessions>();
        for (var i = 1; i < lanes; i++)
        {
            workers.Add(await scope.OpenSessionsAsync());
        }

        var progress = new LogCollector();
        var runner = new OracleCopyRunner(scope.Bulk(), new OracleExecutionService(new ExecutionPolicy(), scope.Ui, scope.Bus, null, pageSize: 500), new NoRunLog());
        var result = await runner.RunAsync(main with { Workers = workers }, job, report, Decisions(report), NoPeriod, progress, new CopyRunControl(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromMinutes(5));

        output.WriteLine(Summary(result));
        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        Assert.All(result.Tables, t => Assert.Equal(t.SourceRows, t.TargetRows));
        Assert.Equal(6000, await scope.CountAsync(scope.DstUser, "PF2_C1"));
        Assert.Equal(2500, await scope.CountAsync(scope.DstUser, "PF2_X"));
        var width = Math.Min(lanes, 3);
        Assert.Contains(progress.Logs, l => l.Text.StartsWith($"동시 {width.ToString(CultureInfo.InvariantCulture)}개 · 단계 1/2", StringComparison.Ordinal));
        Assert.Contains(progress.Logs, l => l.Text.StartsWith($"동시 {Math.Min(lanes, 2).ToString(CultureInfo.InvariantCulture)}개 · 단계 2/2", StringComparison.Ordinal));
        Assert.Contains(progress.Logs, l => l.Text == "대상: COMMIT  — 동시 복사 전에 정리(DELETE) 확정");
        Assert.Contains(progress.Logs, l => l.Text == "대상: COMMIT  — 이 레인의 대상 트랜잭션 끝내기");
    }

    /// <summary>
    /// 측정(PF2): 같은 크기 테이블 4개를 Oracle→Oracle 로 동시 1개·4개 각각 복사해 걸린 시간과 테이블별 행/초를 출력한다(둘 다 같은 시점 읽기 끔).
    /// 단언은 결과 건수만. <c>ORANDA_IT_COPY_MEASURE=1</c> 일 때만 돈다(전체 행 수 <c>ORANDA_IT_COPY_MEASURE_ROWS</c>, 기본 300000 — 테이블마다 그 4분의 1).
    /// </summary>
    [CopyMeasureFact]
    public async Task Measure_oracle_to_oracle_copy_one_versus_four_tables_at_once()
    {
        var rows = Math.Max(4, CopyMeasureFactAttribute.Rows() / 4);
        await using var scope = await CopyDbScope.CreateAsync(db, CopyDbScope.NewTag());
        const string Columns = "ID NUMBER(10) PRIMARY KEY, NAME VARCHAR2(40), AMT NUMBER(12,2), DT DATE";
        var tables = new[] { "PF2_M1", "PF2_M2", "PF2_M3", "PF2_M4" };
        foreach (var table in tables)
        {
            await scope.CreateSourceTableAsync(table, Columns);
            await scope.CreateTargetTableAsync(table, Columns);
            await scope.ExecSourceAsync(Block(
                $"INSERT INTO {table} SELECT LEVEL, 'name ' || LEVEL, MOD(LEVEL, 100000) / 7, DATE '2026-01-01' + MOD(LEVEL, 365) FROM DUAL CONNECT BY LEVEL <= "
                + rows.ToString(CultureInfo.InvariantCulture)));
        }

        foreach (var lanes in new[] { 1, 4, 1, 4 })
        {
            var job = scope.Job([.. tables.Select(t => new TableRule(t, RuleKind.All, TargetAction.DeleteAllInsert))]) with
            {
                ConsistentRead = false,
                Options = new CopyOptions { ConcurrentTables = lanes == 1 ? null : lanes },
            };
            var main = await scope.OpenSessionsAsync();
            var report = await PrecheckAsync(scope, main, job);
            var workers = new List<CopySessions>();
            for (var i = 1; i < lanes; i++)
            {
                workers.Add(await scope.OpenSessionsAsync());
            }

            var runner = new OracleCopyRunner(scope.Bulk(), new OracleExecutionService(new ExecutionPolicy(), scope.Ui, scope.Bus, null, pageSize: 500), new NoRunLog());
            var sw = Stopwatch.StartNew();
            var result = await runner.RunAsync(main with { Workers = workers }, job, report, Decisions(report), NoPeriod, null, new CopyRunControl(), CancellationToken.None);
            sw.Stop();

            Assert.True(result.Tables.All(t => t.TargetRows == rows), Summary(result));
            var total = rows * (long)tables.Length;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"PF2 measure O2O lanes={lanes} rows={total}: {sw.Elapsed.TotalSeconds:F1}s · {total / Math.Max(sw.Elapsed.TotalSeconds, 0.001):N0}행/초 · ")
                + string.Join(" / ", result.Tables.Select(t => t.Table + " " + (t.Times is { } times ? CopyLogText.Times(times) : "-"))));
        }
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
        => result.Outcome + ": " + string.Join(" / ", result.Tables.Select(t => t.Table + "=" + t.Outcome + " " + t.Message))
           + " | notices: " + string.Join(" / ", result.Notices);

    private sealed class LogCollector : IProgress<CopyProgress>
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
