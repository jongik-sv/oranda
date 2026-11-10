using System.Diagnostics;
using System.Globalization;
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

// 소유: 데이터 복사 PF1(속도·멈춤 대응). 이 PC 에서는 컴파일까지만 확인한다. 실행은 CI·다른 PC 의 컨테이너에서 한다.

/// <summary>
/// 복사 실행기의 테이블 적재 흐름을 실제 서버에서: 다른 세션이 잡은 행에 INSERT 해 실제 행 잠금 대기 → 테이블당 대기 한도(결정 73)로 빠져나와
/// 그 테이블은 실패·다음 테이블은 진행. 측정 시험(겹치기 켬·끔 시간, 기본 건너뜀)은 <see cref="CopyMeasureFactAttribute"/>.
/// </summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class CopyPipelineTests(OracleFreeFixture db, ITestOutputHelper output)
{
    private static readonly ResolvedPeriod NoPeriod = new(null, null);

    // ExecutionServiceTests 와 같은 관례: OOB 브레이크가 통하는 서버에서만 「취소가 바로 먹는다」를 단정한다(시험 이미지는 in-band 브레이크라
    // 서버가 잠금 대기 중에는 브레이크를 읽지 않는다). 그 밖에서는 대기 한도 취소 뒤 막던 세션을 놓아 명령이 돌아오게 한다.
    private static bool StrictCancel => Environment.GetEnvironmentVariable("ORANDA_IT_CANCEL_STRICT") == "1";

    [Fact]
    public async Task Wait_limit_gets_out_of_a_row_lock_wait_fails_that_table_and_copies_the_next()
    {
        await using var scope = await CopyDbScope.CreateAsync(db, CopyDbScope.NewTag());
        await BothAsync(scope, "LK_A", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)");
        await BothAsync(scope, "LK_B", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)");
        await scope.ExecSourceAsync(Block(
            "INSERT INTO LK_A SELECT LEVEL, 'a' || LEVEL FROM DUAL CONNECT BY LEVEL <= 10",
            "INSERT INTO LK_B SELECT LEVEL, 'b' || LEVEL FROM DUAL CONNECT BY LEVEL <= 5"));
        var job = scope.Job(new TableRule("LK_A", RuleKind.All, TargetAction.AppendOnly), new TableRule("LK_B", RuleKind.All, TargetAction.AppendOnly)) with
        {
            Options = new CopyOptions { WaitLimitMinutes = 1 },
        };
        var sessions = await scope.OpenSessionsAsync();
        var report = await PrecheckAsync(scope, sessions, job);

        // 다른 세션이 대상 LK_A 에 ID 1 을 넣고 커밋하지 않는다: 복사의 배열 INSERT 가 그 키의 행 잠금(enq: TX)을 기다린다.
        await using var blocker = new OracleConnection(new ConnectionProfile("blocker", scope.DstUser, db.DataSource).BuildConnectionString(scope.Password));
        await blocker.OpenAsync();
        var tx = blocker.BeginTransaction();
        await using (var insert = blocker.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO LK_A VALUES (1, 'blocker')";
            await insert.ExecuteNonQueryAsync();
        }

        var release = new BlockerRelease(tx, releaseOnLimit: !StrictCancel);
        var runner = new OracleCopyRunner(scope.Bulk(), new OracleExecutionService(new ExecutionPolicy(), scope.Ui, scope.Bus, null, pageSize: 500), new NoRunLog())
        {
            // 대기 한도 1 = 3초, 멈춤 표시 1초. 취소한 명령은 60초까지 기다린다(in-band 브레이크라도 막던 세션을 놓으면 돌아온다).
            WatchTiming = new CopyWatchTiming(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(3)),
        };
        scope.Ui.ConfirmAnswer = false;

        CopyRunResult result;
        try
        {
            result = await runner.RunAsync(sessions, job, report, Decisions(report), NoPeriod, release, new CopyRunControl(), CancellationToken.None)
                .WaitAsync(TimeSpan.FromMinutes(3));
        }
        finally
        {
            await release.ReleaseNowAsync();
        }

        output.WriteLine(Summary(result));
        var a = Assert.Single(result.Tables, t => t.Table == "LK_A");
        Assert.Equal(CopyOutcome.Failed, a.Outcome);
        Assert.Contains("대기 한도 초과", a.Message, StringComparison.Ordinal);
        Assert.Contains("배치 1 쓰는 중", a.Message, StringComparison.Ordinal);
        var b = Assert.Single(result.Tables, t => t.Table == "LK_B");
        Assert.Equal(CopyOutcome.Succeeded, b.Outcome);
        Assert.Equal(CopyOutcome.PartiallyFailed, result.Outcome);
        Assert.Contains(release.Logs, l => l.Text == "배치 1 쓰는 중 · 1분째 대기" && l.Table == "LK_A");
        Assert.Equal(5, await scope.CountAsync(scope.DstUser, "LK_B"));
        Assert.Equal(0, await scope.CountAsync(scope.DstUser, "LK_A")); // 그 테이블의 적재분은 되돌렸다(막던 행도 롤백했다)
    }

    /// <summary>
    /// 측정(결정 69·74): 수십만 행 테이블을 Oracle→Oracle 로 겹치기 켬·끔을 번갈아 3회씩 복사해 걸린 시간과 시간 나눔
    /// (적재 구간·읽기·첫 행·변환·쓰기·행/초·테이블 전체·기타)을 출력하고 끝에 켬·끔 각각 중앙값 줄을 낸다.
    /// 단언은 결과 건수만. <c>ORANDA_IT_COPY_MEASURE=1</c> 일 때만 돈다(행 수 <c>ORANDA_IT_COPY_MEASURE_ROWS</c>, 기본 300000).
    /// </summary>
    [CopyMeasureFact]
    public async Task Measure_oracle_to_oracle_copy_with_and_without_overlap()
    {
        var rows = CopyMeasureFactAttribute.Rows();
        await using var scope = await CopyDbScope.CreateAsync(db, CopyDbScope.NewTag());
        const string Columns = "ID NUMBER(10) PRIMARY KEY, NAME VARCHAR2(40), AMT NUMBER(12,2), DT DATE";
        await BothAsync(scope, "PF_MEAS", Columns);
        await scope.ExecSourceAsync(Block(
            "INSERT INTO PF_MEAS SELECT LEVEL, 'name ' || LEVEL, MOD(LEVEL, 100000) / 7, DATE '2026-01-01' + MOD(LEVEL, 365) FROM DUAL CONNECT BY LEVEL <= "
            + rows.ToString(CultureInfo.InvariantCulture)));

        // 켬·끔을 번갈아 3회씩(첫 회 예열 영향을 줄인다, 원격 #155). 회마다 줄 하나, 끝에 켬·끔 각각 값마다 중앙값.
        var runs = new List<(bool Overlap, TimeSpan Wall, CopyTableTimes Times)>();
        foreach (var overlap in new[] { true, false, true, false, true, false })
        {
            var job = scope.Job(new TableRule("PF_MEAS", RuleKind.All, TargetAction.DeleteAllInsert)) with
            {
                Options = new CopyOptions { SerialReadWrite = !overlap },
            };
            var sessions = await scope.OpenSessionsAsync();
            var report = await PrecheckAsync(scope, sessions, job);
            scope.Ui.ConfirmAnswer = false;
            var runner = new OracleCopyRunner(scope.Bulk(), new OracleExecutionService(new ExecutionPolicy(), scope.Ui, scope.Bus, null, pageSize: 500), new NoRunLog());
            var sw = Stopwatch.StartNew();
            var result = await runner.RunAsync(sessions, job, report, Decisions(report), NoPeriod, null, new CopyRunControl(), CancellationToken.None);
            sw.Stop();

            var table = Assert.Single(result.Tables);
            Assert.True(table.TargetRows == rows, Summary(result));
            var times = Assert.IsType<CopyTableTimes>(table.Times);
            runs.Add((overlap, sw.Elapsed, times));
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"PF1 measure O2O overlap={overlap} rows={rows}: 실행 전체 {sw.Elapsed.TotalSeconds:F2}s · ") + CopyLogText.Times(times));
        }

        foreach (var overlap in new[] { true, false })
        {
            output.WriteLine($"PF1 measure O2O overlap={overlap} rows={rows.ToString(CultureInfo.InvariantCulture)} 중앙값(3회): "
                             + MeasureMedian.Text(runs.Where(r => r.Overlap == overlap).Select(r => (r.Wall, r.Times)).ToList()));
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

    private static Task BothAsync(CopyDbScope scope, string table, string columns)
        => Task.WhenAll(scope.CreateSourceTableAsync(table, columns), scope.CreateTargetTableAsync(table, columns));

    private static string Block(params string[] statements)
        => "BEGIN " + string.Join(' ', statements.Select(s => s.TrimEnd(';') + ";")) + " COMMIT; END;";

    private static string Summary(CopyRunResult result)
        => result.Outcome + ": " + string.Join(" / ", result.Tables.Select(t => t.Table + "=" + t.Outcome + " " + t.Message))
           + " | notices: " + string.Join(" / ", result.Notices);

    /// <summary>
    /// 작업 로그를 모으고, 대기 한도로 명령을 취소했다는 줄이 오면(<paramref name="releaseOnLimit"/>) 2초 뒤 막던 트랜잭션을 롤백한다
    /// (in-band 브레이크 서버는 잠금이 풀려야 취소를 읽는다). 끝나면 <see cref="ReleaseNowAsync"/> 로 반드시 놓는다.
    /// </summary>
    private sealed class BlockerRelease(OracleTransaction tx, bool releaseOnLimit) : IProgress<CopyProgress>
    {
        private readonly object _gate = new();
        private readonly List<CopyLogEntry> _logs = [];
        private Task? _release;

        public IReadOnlyList<CopyLogEntry> Logs
        {
            get { lock (_gate) { return [.. _logs]; } }
        }

        public void Report(CopyProgress value)
        {
            if (value.Log is not { } entry)
            {
                return;
            }

            lock (_gate)
            {
                _logs.Add(entry);
                if (releaseOnLimit && _release is null && entry.Text.StartsWith("대기 한도(", StringComparison.Ordinal))
                {
                    _release = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2));
                        tx.Rollback();
                    });
                }
            }
        }

        public async Task ReleaseNowAsync()
        {
            Task? started;
            lock (_gate)
            {
                started = _release;
                _release ??= Task.CompletedTask;
            }

            if (started is not null)
            {
                await started;
                return;
            }

            tx.Rollback();
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

/// <summary>복사 측정 시험(기본 건너뜀): <c>ORANDA_IT_COPY_MEASURE=1</c> 일 때만 돈다. 행 수는 <c>ORANDA_IT_COPY_MEASURE_ROWS</c>(기본 300000).</summary>
public sealed class CopyMeasureFactAttribute : FactAttribute
{
    public CopyMeasureFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ORANDA_IT_COPY_MEASURE") != "1")
        {
            Skip = "측정 시험: ORANDA_IT_COPY_MEASURE=1 일 때만 돈다.";
        }
    }

    public static int Rows() =>
        int.TryParse(Environment.GetEnvironmentVariable("ORANDA_IT_COPY_MEASURE_ROWS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 300_000;
}

/// <summary>측정 시험의 중앙값 줄(원격 #155): 실행 전체·적재 구간·읽기·첫 행·변환·쓰기·행/초·테이블 전체·기타를 값마다 따로 중앙값으로.</summary>
internal static class MeasureMedian
{
    public static string Text(IReadOnlyList<(TimeSpan Wall, CopyTableTimes Times)> runs)
    {
        static string S(IEnumerable<TimeSpan> values) => Median(values.Select(v => v.TotalSeconds)).ToString("F2", CultureInfo.InvariantCulture) + "s";
        static IEnumerable<TimeSpan> Known(IEnumerable<TimeSpan?> values) => values.Where(v => v is not null).Select(v => v!.Value);
        return $"실행 전체 {S(runs.Select(r => r.Wall))} · 적재 구간 {S(Known(runs.Select(r => r.Times.Load)))} · 읽기 {S(runs.Select(r => r.Times.Read))}"
               + $"(첫 행까지 {S(Known(runs.Select(r => r.Times.FirstRow)))}) · 변환 {S(runs.Select(r => r.Times.Convert))} · 쓰기 {S(runs.Select(r => r.Times.Write))}"
               + $" · {Median(runs.Select(r => r.Times.RowsPerSecond)).ToString("N0", CultureInfo.InvariantCulture)}행/초(넣은 행 / 적재 구간)"
               + $" · 테이블 전체 {S(Known(runs.Select(r => r.Times.Total)))} · 기타 {S(Known(runs.Select(r => r.Times.Other)))}";
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
        {
            return 0;
        }

        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
