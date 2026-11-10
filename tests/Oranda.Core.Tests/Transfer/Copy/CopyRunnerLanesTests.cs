using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>
/// 동시 테이블 복사(PF2, 서버 없음·가짜 대량 실행·세션마다 따로 기록): 동시 ≤ N, 부모 단계 커밋 전 자식 시작 안 함, 정리 커밋이 레인보다 먼저,
/// 한 테이블 실패 시 계속·실패 행 상한은 테이블마다, 전체 중단 뒤 다른 레인은 다음 배치에서 멈춤, 중지 시 레인마다 ROLLBACK·다시 켜기는 모든 레인 뒤,
/// 레인 하나의 예상 못 한 예외, 로그 테이블 칸·원본/대상 구분, LOB 테이블 한 번에 하나, 레인 세션 모자람, 그리고 N=1 골든(문장·로그 순서가 c9d1a11c 그대로).
/// </summary>
public sealed class CopyRunnerLanesTests
{
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(15);

    // ---- 동시 수 ----

    [Fact]
    public async Task At_most_N_tables_load_at_once_each_on_its_own_session_pair()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2 }, lanes: 2, stages: null, ("A", 2), ("B", 2), ("C", 2), ("D", 2));
        f.Bulk.OnDml = async (_, _) =>
        {
            await f.Bulk.ActiveReached(2).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
            await Task.Delay(30, CancellationToken.None);
            return null;
        };

        var result = await f.RunAsync().WaitAsync(Quick);

        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, f.Bulk.MaxActive); // 둘이 함께 돌았고 셋은 없었다
        Assert.False(f.Bulk.SessionOverlap, "한 대상 세션에 배치 둘이 겹쳤다");
        Assert.Equal(["T1", "T2"], f.Bulk.DmlSessions().Distinct().Order(StringComparer.Ordinal));
        Assert.All(result.Tables, t => Assert.Equal(2, t.TargetRows));
        Assert.Equal(["A", "B", "C", "D"], result.Tables.Select(t => t.Table).Order(StringComparer.Ordinal));
        Assert.Contains(f.Progress.Logs, l => l.Phase == CopyRunner.PhaseStart && l.Text.EndsWith(" · 동시 테이블 2개", StringComparison.Ordinal));
        Assert.Contains(f.Progress.Reports, p => p.Stage == new CopyStage(2, 1, 1));
    }

    [Fact]
    public async Task A_child_stage_starts_only_after_the_parent_table_is_committed()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2 }, lanes: 2, stages: [["P"], ["C1", "C2"]], ("P", 4), ("C1", 2), ("C2", 2));
        f.Bulk.OnDml = async (call, _) =>
        {
            if (call.Table == "P")
            {
                await Task.Delay(50, CancellationToken.None); // 부모가 느려도 자식은 기다린다
            }

            return null;
        };

        var result = await f.RunAsync().WaitAsync(Quick);

        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        var events = f.Bulk.Events.ToList();
        var parentEnd = events.FindLastIndex(e => e.StartsWith("write-end P ", StringComparison.Ordinal));
        var parentCommit = events.FindIndex(parentEnd, e => e == "ctl T1 COMMIT");
        var firstChildRead = events.FindIndex(e => e.StartsWith("read C", StringComparison.Ordinal));
        Assert.True(parentEnd >= 0 && parentCommit > parentEnd, "부모 테이블 커밋이 없다");
        Assert.True(firstChildRead > parentCommit, "부모 커밋 전에 자식을 읽었다");
        Assert.Contains(f.Progress.Logs, l => l.Text == "동시 1개 · 단계 1/2: P");
        Assert.Contains(f.Progress.Logs, l => l.Text == "동시 2개 · 단계 2/2: C1, C2");
    }

    [Fact]
    public async Task The_cleanup_deletes_are_committed_on_the_main_target_before_any_lane_reads()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2 }, lanes: 2, stages: null, ("A", 2), ("B", 2));

        var result = await f.RunAsync().WaitAsync(Quick);

        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        var events = f.Bulk.Events.ToList();
        var deletes = events.Select((e, i) => (e, i)).Where(x => x.e.Contains("DELETE FROM", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, deletes.Count);
        Assert.All(deletes, d => Assert.StartsWith("exec T1 ", d.e, StringComparison.Ordinal)); // 정리는 주 대상 세션에서 직렬로
        var commit = events.FindIndex(e => e == "ctl T1 COMMIT");
        var firstRead = events.FindIndex(e => e.StartsWith("read ", StringComparison.Ordinal));
        Assert.True(commit > deletes.Max(d => d.i), "정리 커밋이 DELETE 앞이다");
        Assert.True(firstRead > commit, "정리 커밋 전에 레인이 읽기 시작했다");
        Assert.Contains(f.Progress.Logs, l => l.Text == "대상: COMMIT  — 동시 복사 전에 정리(DELETE) 확정" && l.Phase == CopyRunner.PhaseDelete);
    }

    [Fact]
    public async Task One_failing_table_does_not_stop_the_others_and_the_failed_row_limit_is_per_table()
    {
        // 상한 1: A·C 는 실패 행 1개씩(합치면 2개지만 테이블마다라 넘지 않는다), B 는 2개라 넘는다.
        var f = new Fixture(new CopyOptions { BatchSize = 2, MaxFailedRows = 1 }, lanes: 3, stages: null, ("A", 2), ("B", 2), ("C", 2));
        f.Bulk.OnDml = (call, _) => Task.FromResult(
            call.Table == "B" || call.Ids.Contains(2L)
                ? new BulkResult(ExecutionStatus.Failed, 0) { Error = new DbError("ORA-00001", "unique constraint violated") }
                : null);

        var result = await f.RunAsync().WaitAsync(Quick);

        Assert.Equal(CopyOutcome.Failed, result.Tables.Single(t => t.Table == "B").Outcome);
        Assert.Contains("상한", result.Tables.Single(t => t.Table == "B").Message, StringComparison.Ordinal);
        Assert.Equal(CopyOutcome.PartiallyFailed, result.Tables.Single(t => t.Table == "A").Outcome);
        Assert.Equal(CopyOutcome.PartiallyFailed, result.Tables.Single(t => t.Table == "C").Outcome);
        Assert.Equal(["A", "C"], result.FailedRows.Select(r => r.Table).Order(StringComparer.Ordinal));
        Assert.Equal(CopyOutcome.PartiallyFailed, result.Outcome);
    }

    // ---- 전체 중단·중지·예외 ----

    [Fact]
    public async Task An_abort_in_one_lane_stops_the_other_lane_at_its_next_batch()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2 }, lanes: 2, stages: null, ("A", 10), ("B", 2_000));
        f.Bulk.BeforeRow = async (table, index, ct) =>
        {
            if (table == "A" && index == 4)
            {
                await f.Bulk.DmlCountReached("B", 2).WaitAsync(Quick, ct); // B 가 배치를 쓰는 중에
                throw new FakeDbException("ORA-01555: snapshot too old", DbErrorKind.SnapshotTooOld);
            }
        };
        f.Bulk.OnDml = async (call, _) =>
        {
            if (call.Table == "B")
            {
                await Task.Delay(10, CancellationToken.None);
            }

            return null;
        };

        var result = await f.RunAsync().WaitAsync(Quick);

        Assert.Equal(CopyOutcome.Failed, result.Outcome);
        var b = result.Tables.Single(t => t.Table == "B");
        Assert.Equal(CopyOutcome.Failed, b.Outcome);
        Assert.Contains("다른 테이블에서 실행 전체를 멈춰", b.Message, StringComparison.Ordinal);
        Assert.True(f.Bulk.DmlRowCounts("B").Count < 1_000, "B 가 끝까지 썼다");
        var bLane = f.Bulk.DmlSessionOf("B");
        Assert.Contains($"ctl {bLane} ROLLBACK TO SAVEPOINT {CopyCommonStatements.TableSavepoint}", f.Bulk.Events);
        Assert.Contains(result.Notices, n => n.Contains("이전 시점 데이터를 읽지 못했습니다", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stop_rolls_back_every_lane_and_reenables_only_after_all_lanes_ended()
    {
        var fk = new DisabledObject(DisabledKind.ForeignKey, "DST", "A", "FK_A");
        var f = new Fixture(new CopyOptions { BatchSize = 2 }, lanes: 2, stages: null, ("A", 100), ("B", 100)) { ForeignKey = fk };
        f.Bulk.OnDml = async (_, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct); // 응답 없는 INSERT(취소는 듣는다)
            }
            catch (OperationCanceledException)
            {
                return new BulkResult(ExecutionStatus.Cancelled, 0);
            }

            return null;
        };
        using var cts = new CancellationTokenSource();

        var run = f.RunAsync([fk], cts.Token);
        await f.Bulk.ActiveReached(2).WaitAsync(Quick);
        await cts.CancelAsync();
        var result = await run.WaitAsync(Quick);

        Assert.Equal(CopyOutcome.Stopped, result.Outcome);
        var events = f.Bulk.Events.ToList();
        var enable = events.FindIndex(e => e.StartsWith("exec T1 ", StringComparison.Ordinal) && e.Contains("ENABLE CONSTRAINT", StringComparison.Ordinal));
        var laneRollback = events.IndexOf("ctl T2 ROLLBACK");
        var mainRollback = events.IndexOf("ctl T1 ROLLBACK");
        Assert.True(laneRollback >= 0 && mainRollback >= 0, "레인마다 대상 ROLLBACK 이 없다");
        Assert.True(enable > laneRollback && enable > mainRollback, "다시 켜기가 레인 ROLLBACK 보다 먼저다");
        Assert.Contains("ctl S2 ROLLBACK", events); // 레인 원본 읽기 전용 트랜잭션도 닫는다
        // 정리는 먼저 확정했다: 지운 행은 돌아오지 않는다고 테이블 결과에 남는다.
        Assert.All(result.Tables, t => Assert.Contains("대상 정리(DELETE)는 확정됐지만 적재를 끝내지 못했습니다", t.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unexpected_error_in_one_lane_cancels_the_others_and_comes_out_of_the_run()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2 }, lanes: 2, stages: null, ("A", 2_000), ("B", 4));
        f.Bulk.OnDml = async (call, ct) =>
        {
            if (call.Table == "B")
            {
                await f.Bulk.DmlCountReached("A", 1).WaitAsync(Quick, ct);
                throw new FakeCrashException();
            }

            try
            {
                await Task.Delay(20, ct);
            }
            catch (OperationCanceledException)
            {
                return new BulkResult(ExecutionStatus.Cancelled, 0);
            }

            return null;
        };

        await Assert.ThrowsAsync<FakeCrashException>(() => f.RunAsync().WaitAsync(Quick));

        Assert.True(f.Bulk.StreamEnded("A").IsCompleted, "다른 레인의 원본 읽기가 남았다");
        Assert.True(f.Bulk.DmlRowCounts("A").Count < 1_000, "다른 레인이 끝까지 썼다");
        var events = f.Bulk.Events.ToList();
        Assert.Contains("ctl T1 ROLLBACK", events);
        Assert.Contains("ctl T2 ROLLBACK", events);
    }

    // ---- 로그 ----

    [Fact]
    public async Task Log_lines_carry_the_lane_table_and_the_right_side()
    {
        // A 는 배치 셋(2·2·2), B 는 둘(2·1): 배치 줄 글로 어느 테이블 줄인지 안다. 첫 배치에서 둘이 함께 돌게 한다.
        var f = new Fixture(new CopyOptions { BatchSize = 2 }, lanes: 2, stages: null, ("A", 6), ("B", 3));
        f.Bulk.OnDml = async (call, _) =>
        {
            if (call.Batch == 1)
            {
                await f.Bulk.ActiveReached(2).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
            }

            return null;
        };

        var result = await f.RunAsync().WaitAsync(Quick);

        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, f.Bulk.MaxActive);
        var logs = f.Progress.Logs;
        Assert.Equal("A", Assert.Single(logs, l => l.Text == "배치 3 · 2행 보냄 · 누적 6행").Table);
        Assert.Equal("B", Assert.Single(logs, l => l.Text == "배치 2 · 1행 보냄 · 누적 3행").Table);
        foreach (var table in new[] { "A", "B" })
        {
            Assert.Equal(table, Assert.Single(logs, l => l.Text == $"원본: SELECT \"ID\", \"NAME\" FROM \"SRC\".\"{table}\"").Table);
            Assert.Equal(table, Assert.Single(logs, l => l.Text.StartsWith($"대상: INSERT INTO \"DST\".\"{table}\"", StringComparison.Ordinal)).Table);
            Assert.Single(logs, l => l.Text == "적재 시작" && l.Table == table && l.Phase == CopyRunner.PhaseLoad);
            Assert.Single(logs, l => l.Text == "대상: COMMIT  — 테이블마다 커밋" && l.Table == table);
            // 같은 시점 읽기를 끈 동시 복사: 테이블마다 원본 읽기 전용 트랜잭션을 새로 연다(원본 쪽 줄).
            Assert.Single(logs, l => l.Text == "원본: SET TRANSACTION READ ONLY  — 이 테이블을 읽을 시점" && l.Table == table);
        }

        // 둘째 레인 마무리: 대상은 「대상」, 원본은 「원본」 줄이고 표 칸은 비었다.
        Assert.Single(logs, l => l.Text == "대상: COMMIT  — 이 레인의 대상 트랜잭션 끝내기" && l.Table is null && l.Phase == CopyRunner.PhaseFinish);
        Assert.Single(logs, l => l.Text == "원본: ROLLBACK  — 이 레인의 원본 읽기 전용 트랜잭션 닫기" && l.Table is null && l.Phase == CopyRunner.PhaseFinish);
        Assert.Contains("ctl S2 ROLLBACK", f.Bulk.Events);
        Assert.Contains("ctl T2 COMMIT", f.Bulk.Events);
    }

    // ---- LOB·세션 ----

    [Fact]
    public async Task Tables_with_lob_columns_load_one_at_a_time()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2 }, lanes: 3, stages: null, ("L1", 4), ("L2", 4), ("A", 4)) { LobTables = ["L1", "L2"] };
        f.Bulk.OnDml = async (_, _) =>
        {
            await Task.Delay(40, CancellationToken.None);
            return null;
        };

        var result = await f.RunAsync().WaitAsync(Quick);

        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, f.Bulk.MaxActiveOf(t => t.StartsWith('L')));
    }

    [Fact]
    public async Task Fewer_lane_sessions_than_asked_runs_with_what_is_open_and_says_so()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2 }, lanes: 2, stages: null, concurrent: 3, ("A", 2), ("B", 2), ("C", 2));

        var result = await f.RunAsync().WaitAsync(Quick);

        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        Assert.Contains(f.Progress.Logs, l => l.Kind == CopyLogKind.Warning && l.Text.Contains("2개로 줄여 복사합니다", StringComparison.Ordinal));
        Assert.Contains(f.Progress.Logs, l => l.Text.EndsWith(" · 동시 테이블 2개", StringComparison.Ordinal));
        Assert.True(f.Bulk.MaxActive <= 2);
    }

    [Fact]
    public async Task A_lane_session_on_another_database_is_refused()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2 }, lanes: 2, stages: null, ("A", 2), ("B", 2));
        ((FakeSession)f.Workers[0].Target).DbKey = "OTHER/PDB1/DST";

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.RunAsync());
    }

    [Fact]
    public async Task Consistent_read_keeps_one_table_at_a_time_even_with_lane_sessions_and_logs_why()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2 }, lanes: 2, stages: null, ("A", 2), ("B", 2)) { ConsistentRead = true };

        var result = await f.RunAsync().WaitAsync(Quick);

        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        Assert.Equal(["T1"], f.Bulk.DmlSessions().Distinct());
        Assert.Contains(f.Progress.Logs, l => l.Text == "한 테이블씩 복사합니다: " + CopyConcurrency.ConsistentReadReason);
        Assert.DoesNotContain(f.Progress.Logs, l => l.Text.Contains("동시 복사 전에 정리", StringComparison.Ordinal));
    }

    // ---- N=1 골든 ----

    /// <summary>
    /// 동시 수 1(기본)이면 문장·로그가 PF2 전(c9d1a11c) 실행기와 글자 그대로 같다(걸린 시간·속도 숫자만 # 로 바꿔 비교). 기대값은 그 판의 실행기 순서를 적은 것:
    /// 시작 → 원본 준비 → 정리(자식 → 부모, 바로 커밋하지 않음) → 적재(부모 → 자식, 테이블마다 커밋) → 마무리 커밋 → 원본 닫기 → 건수 비교(정리 순서) → 끝.
    /// </summary>
    [Fact]
    public async Task One_table_at_a_time_keeps_the_statements_and_log_lines_exactly_as_before()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2 }, lanes: 1, stages: null, concurrent: null, ("P", 3), ("C", 2)) { ConsistentRead = true };

        var result = await f.RunAsync().WaitAsync(Quick);

        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        Assert.Equal(["C", "P"], result.Tables.Select(t => t.Table)); // 결과 순서 = 표 상태를 처음 만든 순서(정리, 자식 먼저)
        string[] expected =
        [
            "시작||Info|복사 시작: 작업 「n」 · 원본 s → 대상 t · 테이블 2개 · 커밋 테이블마다 · 배치 2행 · 실패 행 상한 100 · 같은 시점 기준 읽기",
            "원본 준비||Statement|원본: ROLLBACK  — 앞 트랜잭션 닫기",
            "원본 준비||Statement|원본: SET TRANSACTION READ ONLY  — 모든 테이블을 이 시점 기준으로 읽기",
            "정리|C|Statement|대상: DELETE FROM \"DST\".\"C\"",
            "정리|C|Info|→ 완료 · #",
            "정리|P|Statement|대상: DELETE FROM \"DST\".\"P\"",
            "정리|P|Info|→ 완료 · #",
            "적재|P|Info|적재 시작",
            "적재|P|Statement|대상: SAVEPOINT ORANDA_COPY_TABLE  — 이 테이블을 되돌릴 자리",
            "적재|P|Statement|원본: SELECT \"ID\", \"NAME\" FROM \"SRC\".\"P\"",
            "적재|P|Statement|대상: INSERT INTO \"DST\".\"P\" (\"ID\", \"NAME\") VALUES (:C1, :C2)  [바인드 배열 :C1…:C2, 배치 2행씩]",
            "적재|P|Info|배치 1 · 2행 보냄 · 누적 2행",
            "적재|P|Info|배치 2 · 1행 보냄 · 누적 3행",
            "적재|P|Statement|대상: COMMIT  — 테이블마다 커밋",
            "적재|P|Info|적재 끝 · 원본 3건 읽음 · 3행 넣음 · # · 적재 구간 # · 읽기 #(첫 행까지 #) · 변환 # · 쓰기 # · # · 테이블 전체 # · 기타 #",
            "적재|C|Info|적재 시작",
            "적재|C|Statement|대상: SAVEPOINT ORANDA_COPY_TABLE  — 이 테이블을 되돌릴 자리",
            "적재|C|Statement|원본: SELECT \"ID\", \"NAME\" FROM \"SRC\".\"C\"",
            "적재|C|Statement|대상: INSERT INTO \"DST\".\"C\" (\"ID\", \"NAME\") VALUES (:C1, :C2)  [바인드 배열 :C1…:C2, 배치 2행씩]",
            "적재|C|Info|배치 1 · 2행 보냄 · 누적 2행",
            "적재|C|Statement|대상: COMMIT  — 테이블마다 커밋",
            "적재|C|Info|적재 끝 · 원본 2건 읽음 · 2행 넣음 · # · 적재 구간 # · 읽기 #(첫 행까지 #) · 변환 # · 쓰기 # · # · 테이블 전체 # · 기타 #",
            "마무리||Statement|대상: COMMIT  — 대상 트랜잭션 끝내기",
            "마무리||Statement|원본: ROLLBACK  — 원본 읽기 전용 트랜잭션 닫기",
            "건수 비교|C|Statement|대상: SELECT COUNT(*) FROM \"DST\".\"C\"",
            "건수 비교|C|Info|→ 원본 2건 · 대상 2건 · 같음",
            "건수 비교|P|Statement|대상: SELECT COUNT(*) FROM \"DST\".\"P\"",
            "건수 비교|P|Info|→ 원본 3건 · 대상 3건 · 같음",
            "마무리||Info|끝: 성공 · 테이블 2개(성공 2) · 5행 넣음 · 실패 행 0 · 걸린 시간 #",
        ];
        Assert.Equal(expected, f.Progress.Logs.Select(Golden));

        // 보낸 문장 순서(세션별): 정리 DELETE 는 실행 엔진, 나머지는 대량 경로. 정리 바로 뒤 커밋이 없다(첫 테이블 커밋이 확정한다).
        Assert.Equal(
        [
            "ctl S1 ROLLBACK", "ctl S1 SET TRANSACTION READ ONLY",
            "exec T1 DELETE FROM \"DST\".\"C\"", "exec T1 DELETE FROM \"DST\".\"P\"",
            "ctl T1 SAVEPOINT ORANDA_COPY_TABLE", "ctl T1 SAVEPOINT ORANDA_COPY_BATCH", "ctl T1 SAVEPOINT ORANDA_COPY_BATCH", "ctl T1 COMMIT",
            "ctl T1 SAVEPOINT ORANDA_COPY_TABLE", "ctl T1 SAVEPOINT ORANDA_COPY_BATCH", "ctl T1 COMMIT",
            "ctl T1 COMMIT", "ctl S1 ROLLBACK",
        ], f.Bulk.Events.Where(e => e.StartsWith("ctl ", StringComparison.Ordinal) || e.StartsWith("exec ", StringComparison.Ordinal)));
        Assert.DoesNotContain(f.Progress.Reports, p => p.Stage is not null);
    }

    private static string Golden(CopyLogEntry l) =>
        $"{l.Phase}|{l.Table}|{l.Kind}|{Numbers().Replace(l.Text, "#")}";

    // 걸린 시간(0.42초·3분 05초)과 속도(1,234행/초)만 숫자를 지운다.
    private static Regex Numbers() => new(@"\d+분 \d{2}초|\d+\.\d{2}초|[\d,]+행/초", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // ---- 가짜 협력자 ----

    private sealed record DmlCall(string Table, int Batch, int Rows, string Session, IReadOnlyList<long> Ids);

    private sealed class Fixture
    {
        private readonly int _lanes;
        private readonly int? _concurrent;
        private readonly IReadOnlyList<IReadOnlyList<string>>? _stages;

        public Fixture(CopyOptions options, int lanes, IReadOnlyList<IReadOnlyList<string>>? stages, params (string Table, int Rows)[] tables)
            : this(options, lanes, stages, lanes, tables)
        {
        }

        public Fixture(CopyOptions options, int lanes, IReadOnlyList<IReadOnlyList<string>>? stages, int? concurrent, params (string Table, int Rows)[] tables)
        {
            Options = options;
            Tables = tables;
            _lanes = lanes;
            _concurrent = concurrent;
            _stages = stages;
            Source = new FakeSession(TestData.Profile(readOnly: true), TestData.Server()) { DbKey = "SRCDB/PDB1/SRC" };
            Target = new FakeSession(TestData.Profile() with { Name = "dev" }, TestData.Server()) { DbKey = "DSTDB/PDB1/DST" };
            Labels[Source] = "S1";
            Labels[Target] = "T1";
            for (var i = 2; i <= lanes; i++)
            {
                var pair = new CopySessions(
                    new FakeSession(TestData.Profile(readOnly: true), TestData.Server()) { DbKey = "SRCDB/PDB1/SRC" },
                    new FakeSession(TestData.Profile() with { Name = "dev" }, TestData.Server()) { DbKey = "DSTDB/PDB1/DST" });
                Workers.Add(pair);
                Labels[pair.Source] = "S" + i;
                Labels[pair.Target] = "T" + i;
            }

            Bulk = new ScriptedBulk(this);
        }

        public CopyOptions Options { get; }

        public IReadOnlyList<(string Table, int Rows)> Tables { get; }

        /// <summary>「같은 시점 기준 읽기」. 동시 복사 시험은 끈다(켜면 동시 복사를 하지 않는다).</summary>
        public bool ConsistentRead { get; init; }

        public IReadOnlyList<string> LobTables { get; init; } = [];

        public DisabledObject? ForeignKey { get; init; }

        public FakeSession Source { get; }

        public FakeSession Target { get; }

        public List<CopySessions> Workers { get; } = [];

        public Dictionary<ISession, string> Labels { get; } = new(ReferenceEqualityComparer.Instance);

        public ScriptedBulk Bulk { get; }

        public EventRecord Record { get; } = new();

        public ProgressLog Progress { get; } = new();

        public bool IsSource(ISession s) => Labels.TryGetValue(s, out var l) && l[0] == 'S';

        public string Label(ISession s) => Labels.TryGetValue(s, out var l) ? l : "?";

        public Task<CopyRunResult> RunAsync(IReadOnlyList<DisabledObject>? disable = null, CancellationToken ct = default)
        {
            var rules = Tables.Select(t => new TableRule(t.Table, RuleKind.All, TargetAction.DeleteAllInsert)).ToList();
            var job = new CopyJob
            {
                Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "DST"),
                SourceOwner = "SRC", TargetOwner = "DST", MaskSalt = "AA==", Tables = rules, ConsistentRead = ConsistentRead,
                Options = Options with { ConcurrentTables = _concurrent },
            };
            var plans = rules.Select(r => new CopyTablePlan(r, Info("SRC", r.Table), Info("DST", r.Table), ["ID", "NAME"], ["ID"])
            {
                DisablableForeignKeys = ForeignKey is { } fk && fk.Table == r.Table ? [fk] : [],
            }).ToList();
            var period = new ResolvedPeriod(null, null);
            var builder = new CopySqlBuilder(job, period, plans.ToDictionary(p => p.Rule.Table, p => p.Source), plans.ToDictionary(p => p.Rule.Table, p => p.Target));
            var preview = plans.Select(p => builder.TargetDelete(p.Rule.Table)).OfType<SqlWithBinds>()
                .Select(d => new PreviewStatement(d.Sql, PreviewSide.Target, PolicyDecisionKind.Allow, null, null)).ToList();
            var stages = _stages ?? [[.. Tables.Select(t => t.Table)]];
            var items = CopyConcurrency.Notice(job, stages, Dbms.Oracle) is { } notice ? new List<CheckItem> { notice } : [];
            var report = new PrecheckReport(items, plans, preview, new Dictionary<string, long>()) { Stages = stages };
            var parts = new CopyDbmsParts(Dbms.Oracle, new NoCatalog(), new FakeClassifier(), new FakeDialect(), new FakePrecheckDialect(), new SimpleCodec(), new FakeRecreateStep());
            var runner = new CopyRunner(Bulk, new ScriptedExecution(this), new FakeRunLog(Record), parts, parts);
            var decisions = new CopyDecisions(new HashSet<string>(), (disable ?? []).Select(ForeignKeyRef.Of).ToHashSet(), new HashSet<string>(), false);
            var sessions = new CopySessions(Source, Target) { Workers = [.. Workers.Take(_lanes - 1)] };
            return runner.RunAsync(sessions, job, report, decisions, period, Progress, new CopyRunControl(), ct);
        }

        public string TableOf(string sql) =>
            Tables.Select(t => t.Table).FirstOrDefault(t => sql.Contains("\"" + t + "\"", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("테이블을 모르는 문장: " + sql);

        private TableInfo Info(string owner, string table) =>
            new(new DbObjectRef(owner, table, "TABLE"),
            [
                new ColumnInfo("ID", 1, "NUMBER") { Precision = 10, Scale = 0, IsNullable = false },
                LobTables.Contains(table) ? new ColumnInfo("NAME", 2, "CLOB") : new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 20 },
            ])
            {
                Constraints = [new ConstraintInfo("PK_" + table, ConstraintKind.PrimaryKey, ["ID"])],
            };
    }

    /// <summary>
    /// 세션마다(라벨 S1·T1·S2·T2 …) 읽기·쓰기·트랜잭션 제어를 순서대로 남긴다. 표마다 정한 수의 원본 행을 내고(<see cref="BeforeRow"/>),
    /// 배열 DML 은 <see cref="OnDml"/> 로 답한다(null = 성공). 동시에 도는 배치 수의 최댓값과 한 대상 세션에 배치가 겹쳤는지를 잰다.
    /// </summary>
    private sealed class ScriptedBulk(Fixture fixture) : IBulkExecutionService
    {
        private readonly object _gate = new();
        private readonly ConcurrentDictionary<string, long> _inserted = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _batches = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _ended = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _signals = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<DmlCall> _dml = new();
        private readonly List<string> _active = [];
        private readonly HashSet<string> _busySessions = new(StringComparer.Ordinal);
        private readonly List<int> _activeCounts = [];
        private readonly List<IReadOnlyList<string>> _activeSnapshots = [];

        public Func<string, int, CancellationToken, ValueTask>? BeforeRow { get; set; }

        public Func<DmlCall, CancellationToken, Task<BulkResult?>>? OnDml { get; set; }

        public ConcurrentQueue<string> Events { get; } = new();

        public bool SessionOverlap { get; private set; }

        public int MaxActive
        {
            get { lock (_gate) { return _activeCounts.DefaultIfEmpty(0).Max(); } }
        }

        public int MaxActiveOf(Func<string, bool> table)
        {
            lock (_gate)
            {
                return _activeSnapshots.Select(s => s.Count(table)).DefaultIfEmpty(0).Max();
            }
        }

        public Task ActiveReached(int n) => Signal("active " + n).Task;

        public Task DmlCountReached(string table, int n) => Signal($"dml {table} {n}").Task;

        public Task StreamEnded(string table) => _ended.GetOrAdd(table, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public IReadOnlyList<int> DmlRowCounts(string table) => [.. _dml.Where(d => d.Table == table).Select(d => d.Rows)];

        public IReadOnlyList<string> DmlSessions() => [.. _dml.Select(d => d.Session)];

        public string DmlSessionOf(string table) => _dml.First(d => d.Table == table).Session;

        public async IAsyncEnumerable<object?[]> StreamRowsAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                                                 ExecutionOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            var table = fixture.TableOf(query.Text);
            if (!fixture.IsSource(s))
            {
                Events.Enqueue("count " + table);
                yield return [_inserted.GetValueOrDefault(table)]; // 대상 COUNT(*)
                yield break;
            }

            try
            {
                var rows = fixture.Tables.Single(t => t.Table == table).Rows;
                for (var i = 0; i < rows; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (BeforeRow is { } before)
                    {
                        await before(table, i, ct);
                    }

                    Events.Enqueue($"read {table} {i + 1} {fixture.Label(s)}");
                    yield return [(long)(i + 1), "n" + i];
                }
            }
            finally
            {
                _ = StreamEnded(table);
                _ended[table].TrySetResult();
            }
        }

        public async Task<BulkResult> ExecuteArrayAsync(ISession s, SqlStatement stmt, IReadOnlyList<BulkColumn> columns, IReadOnlyList<object?[]> rows,
                                                        ExecutionOptions options, CancellationToken ct)
        {
            var label = fixture.Label(s);
            if (columns.Count == 0)
            {
                Events.Enqueue($"ctl {label} {stmt.Text}");
                return new BulkResult(ExecutionStatus.Succeeded, 0); // 트랜잭션 제어
            }

            var table = fixture.TableOf(stmt.Text);
            // 한 행씩 다시 넣기도 배치 번호를 더한다(시험은 첫 배치 번호만 본다).
            if (rows.Count > 1 || !_batches.TryGetValue(table, out var batch))
            {
                batch = _batches.AddOrUpdate(table, 1, (_, n) => n + 1);
            }

            var call = new DmlCall(table, batch, rows.Count, label, [.. rows.Select(r => r[0] is long id ? id : -1)]);
            _dml.Enqueue(call);
            Events.Enqueue($"write-start {table} {call.Batch} {label}");
            Enter(table, label);
            try
            {
                var result = (OnDml is { } on ? await on(call, ct) : null) ?? new BulkResult(ExecutionStatus.Succeeded, rows.Count);
                if (result.Status == ExecutionStatus.Succeeded)
                {
                    _inserted.AddOrUpdate(table, rows.Count, (_, n) => n + rows.Count);
                }

                return result;
            }
            finally
            {
                Leave(table, label);
                Events.Enqueue($"write-end {table} {call.Batch} {label}");
                Signal($"dml {table} {_dml.Count(d => d.Table == table)}").TrySetResult();
            }
        }

        private void Enter(string table, string session)
        {
            int count;
            lock (_gate)
            {
                if (!_busySessions.Add(session))
                {
                    SessionOverlap = true;
                }

                _active.Add(table);
                count = _active.Count;
                _activeCounts.Add(count);
                _activeSnapshots.Add([.. _active]);
            }

            for (var i = 1; i <= count; i++)
            {
                Signal("active " + i).TrySetResult();
            }
        }

        private void Leave(string table, string session)
        {
            lock (_gate)
            {
                _busySessions.Remove(session);
                _active.Remove(table);
            }
        }

        private TaskCompletionSource Signal(string key) =>
            _signals.GetOrAdd(key, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    /// <summary>실행 엔진(정리 DELETE·끄기·다시 켜기): 세션 라벨과 함께 남기고 늘 성공한다. 레인과 주 흐름이 함께 부를 수 있어 동시 모음이다.</summary>
    private sealed class ScriptedExecution(Fixture fixture) : IExecutionService
    {
        public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds, ExecutionOptions? options, CancellationToken ct)
        {
            fixture.Bulk.Events.Enqueue($"exec {fixture.Label(s)} {stmt.Text}");
            return Task.FromResult(new ExecutionResult(stmt, ExecutionStatus.Succeeded));
        }

        public async IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode,
                                                                          ExecutionOptions? options, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var stmt in stmts)
            {
                yield return await ExecuteAsync(s, stmt, new Dictionary<string, BindValue>(), options, ct);
            }
        }
    }

    /// <summary>진행 보고·작업 로그 모음(레인·감시가 다른 스레드에서 보낸다).</summary>
    private sealed class ProgressLog : IProgress<CopyProgress>
    {
        private readonly List<CopyProgress> _all = [];

        public IReadOnlyList<CopyProgress> Reports
        {
            get { lock (_all) { return [.. _all.Where(p => p.Log is null)]; } }
        }

        public IReadOnlyList<CopyLogEntry> Logs
        {
            get { lock (_all) { return [.. _all.Where(p => p.Log is not null).Select(p => p.Log!)]; } }
        }

        public void Report(CopyProgress value)
        {
            lock (_all)
            {
                _all.Add(value);
            }
        }
    }

    private sealed class EventRecord : ICopyRunRecord
    {
        public Task RecordDisabledAsync(IReadOnlyList<DisabledObject> objects, CancellationToken ct) => Task.CompletedTask;

        public Task RecordStatementAsync(string sql, CancellationToken ct) => Task.CompletedTask;

        public Task RecordTableAsync(TableRunResult result, CancellationToken ct) => Task.CompletedTask;

        public Task RecordFailedRowAsync(FailedRow row, CancellationToken ct) => Task.CompletedTask;

        public Task CompleteAsync(CopyOutcome outcome, CancellationToken ct) => Task.CompletedTask;

        public Task RecordEventAsync(string kind, string? table, string text, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRunLog(EventRecord record) : ICopyRunLog
    {
        public Task<ICopyRunRecord> BeginAsync(CopyRunHeader header, CancellationToken ct) => Task.FromResult<ICopyRunRecord>(record);

        public Task<IReadOnlyList<UnfinishedRun>> FindUnfinishedAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<UnfinishedRun>>([]);

        public Task MarkRecoveredAsync(string runFile, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeDbException(string message, DbErrorKind kind) : DbException(message)
    {
        public DbErrorKind Kind { get; } = kind;
    }

    private sealed class FakeCrashException() : Exception("예상하지 못한 오류(시험)");

    private sealed class FakeClassifier : IDbErrorClassifier
    {
        public Dbms Dbms => Dbms.Oracle;

        public DbErrorKind Classify(int number) => DbErrorKind.Other;

        public DbError? FromException(Exception exception) =>
            exception is FakeDbException e ? new DbError("ORA-01555", e.Message) { Number = 1555, Kind = e.Kind } : null;
    }

    private sealed class SimpleCodec : IBulkValueCodec
    {
        public bool IsSupported(string serverType) => true;

        public BulkKind KindOf(string serverType) => serverType switch
        {
            "CLOB" => BulkKind.Clob,
            _ when serverType.StartsWith("VARCHAR", StringComparison.Ordinal) => BulkKind.Text,
            _ => BulkKind.Other,
        };

        public BindDataType BindTypeOf(string serverType) => BindDataType.Number;

        public string? ToKeyText(object? value, string serverType) => value is null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);

        public object? FromKeyText(string? text, string serverType) => text;

        public long? ToInt64(object? value) => value is long l ? l : null;

        public string? ToText(object? value) => value?.ToString();

        public string? NumberText(object? value) => value is long l ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

        public bool IsZero(object? value) => value is 0L;

        public long ToInt64Exact(object? value) => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);

        public object? ToPortable(object? value, string serverType) => value;

        public object? FromPortable(object? portable, string bindType) => portable;
    }

    private sealed class NoCatalog : Oranda.Core.Catalog.ISqlCatalog
    {
        public Oranda.Core.Catalog.CatalogQuery Get(string id, ServerInfo server) => throw new NotSupportedException("이 시험은 카탈로그 조회를 쓰지 않는다: " + id);
    }

    private sealed class FakeRecreateStep : ICopyRecreateStep
    {
        public Task<PreservedDdl> CaptureAsync(ISession target, string owner, string table, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<(string Parent, string Ddl, string? Name)>> SourceForeignKeysAsync(ISession source, string sourceOwner, string table,
            IReadOnlySet<string> parents, string targetOwner, CancellationToken ct) => throw new NotSupportedException();

        public Task<HashSet<string>> RecycleBinAsync(ISession target, string table, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakePrecheckDialect : ICopyPrecheckDialect
    {
        public bool SupportsReadPrivilege => false;

        public string? RecreateBlockedReason(ISession target) => null;

        public Task<IReadOnlyList<string>> ExtendedStringBlockersAsync(TableInfo source, Func<string, CancellationToken, Task<string?>> targetParameter,
                                                                       CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);

        public IReadOnlyList<(string CatalogId, string Label)> NotRestoredProbes => [];

        public string RecycleBinNote(string? tablespace) => string.Empty;

        public string? ReadConsistencyParameter => null;

        public (CheckLevel Level, string Message) ReadConsistencyHint(long estimatedSeconds, long? retention) => (CheckLevel.Pass, string.Empty);

        public string ChildRecordFoundError => "ORA-02292";
    }

    private sealed class FakeDialect : ICopyDialect
    {
        private static SqlStatement Ddl(string text) => new(text, default, StatementKind.Ddl);

        public string BindPlaceholder(string bindName, string serverType) => ":" + bindName;

        public SqlStatement DisableTrigger(string owner, string trigger) => Ddl($"ALTER TRIGGER \"{owner}\".\"{trigger}\" DISABLE");

        public SqlStatement DisableConstraint(DisabledObject fk) => Ddl($"ALTER TABLE \"{fk.Owner}\".\"{fk.Table}\" DISABLE CONSTRAINT \"{fk.Name}\"");

        public SqlStatement DropTable(string owner, string table) => Ddl($"DROP TABLE \"{owner}\".\"{table}\"");

        public SqlStatement NextValues(string owner, string sequence) => throw new NotSupportedException();

        public SqlStatement SetTransactionReadOnly() => new("SET TRANSACTION READ ONLY", default, StatementKind.TransactionControl);

        public SqlStatement Savepoint(string name) => new("SAVEPOINT " + name, default, StatementKind.TransactionControl);

        public SqlStatement RollbackToSavepoint(string name) => new("ROLLBACK TO SAVEPOINT " + name, default, StatementKind.TransactionControl);

        public SqlStatement EnableTrigger(string owner, string trigger) => Ddl($"ALTER TRIGGER \"{owner}\".\"{trigger}\" ENABLE");

        public SqlStatement DropConstraint(DisabledObject fk) => Ddl($"ALTER TABLE \"{fk.Owner}\".\"{fk.Table}\" DROP CONSTRAINT \"{fk.Name}\"");

        public SqlStatement EnableConstraint(DisabledObject fk, bool validate) => Ddl($"ALTER TABLE \"{fk.Owner}\".\"{fk.Table}\" ENABLE CONSTRAINT \"{fk.Name}\"");

        public SqlStatement RecycleBinOn() => throw new NotSupportedException();

        public string RestartIdentity(string owner, string table, string column) => throw new NotSupportedException();

        public string AddForeignKeyWithoutValidation(string addForeignKeyDdl) => addForeignKeyDdl;

        public bool IsParentKeysNotFound(DbError? dbError) => false;

        public bool IsReferencedByForeignKeys(DbError? dbError) => false;
    }
}
