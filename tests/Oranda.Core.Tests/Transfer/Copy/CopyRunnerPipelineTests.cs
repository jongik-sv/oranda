using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>
/// 실행기의 테이블 적재 흐름(PF1, 서버 없음·가짜 대량 실행): 읽기·쓰기 겹치기(결정 69)와 끄는 설정(옛 순서), 쓰기·읽기 실패와 중지 때 두 작업 정리,
/// 멈춤 표시(결정 70), 테이블당 대기 한도(결정 73), 돌아오지 않는 명령, 실행 기록의 사건 줄, 시간 나눔(결정 74).
/// </summary>
public sealed class CopyRunnerPipelineTests
{
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(10);

    // ---- 겹치기 ----

    [Fact]
    public async Task Overlap_reads_the_next_batches_while_a_batch_is_being_written()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2 }, ("A", 6));
        var overlapped = false;
        f.Bulk.OnDml = async (call, ct) =>
        {
            if (call.Batch == 1)
            {
                // 첫 배치를 쓰는 동안 원본이 넷째 행(둘째 배치)까지 읽어야 한다. 번갈아 하면 여기서 기다려도 오지 않는다.
                overlapped = await Completes(f.Bulk.ReadReached("A", 4), TimeSpan.FromSeconds(5));
            }

            return null;
        };

        var result = await f.RunAsync();

        Assert.True(overlapped, "첫 배치를 쓰는 동안 원본 읽기가 앞서 나가지 않았다");
        var events = f.Bulk.Events.ToList();
        Assert.True(events.IndexOf("read A 4") < events.IndexOf("write-end A 1"));
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Succeeded, table.Outcome);
        Assert.Equal(6, table.SourceRows);
        Assert.Equal(6, table.TargetRows);
        Assert.Equal([2, 2, 2], f.Bulk.DmlRowCounts("A"));
    }

    [Fact]
    public async Task Serial_setting_keeps_the_old_order_reading_one_batch_then_writing_it()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2, SerialReadWrite = true }, ("A", 5));

        var result = await f.RunAsync();

        Assert.Equal(
            ["read A 1", "read A 2", "write-start A 1", "write-end A 1",
             "read A 3", "read A 4", "write-start A 2", "write-end A 2",
             "read A 5", "write-start A 3", "write-end A 3"],
            f.Bulk.Events.Where(e => !e.StartsWith("count", StringComparison.Ordinal)));
        Assert.Equal(CopyOutcome.Succeeded, Assert.Single(result.Tables).Outcome);
        Assert.Contains(f.Progress.Logs, l => l.Text.Contains("읽기·쓰기 번갈아", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Write_side_stop_at_the_failed_row_limit_cancels_the_reader_and_both_tasks_end()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2, MaxFailedRows = 1 }, ("A", 100_000));
        f.Bulk.OnDml = (_, _) => Task.FromResult<BulkResult?>(new BulkResult(ExecutionStatus.Failed, 0) { Error = new DbError("ORA-00001", "unique") });

        var result = await f.RunAsync().WaitAsync(Quick);

        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Failed, table.Outcome);
        Assert.Contains("상한", table.Message, StringComparison.Ordinal);
        Assert.True(f.Bulk.StreamEnded("A").IsCompleted, "원본 읽기가 끝나지 않았다");
        Assert.True(table.SourceRows < 100_000, "쓰기가 멈췄는데 원본을 끝까지 읽었다");
        Assert.Contains("ROLLBACK TO SAVEPOINT " + CopyCommonStatements.TableSavepoint, f.Bulk.TargetControls);
        Assert.Empty(result.FailedRows); // 되돌린 표의 실패 행은 버린다(예전과 같다)
    }

    [Fact]
    public async Task Read_failure_discards_the_buffered_batches_rolls_the_table_back_and_the_next_table_runs()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2 }, ("A", 10), ("B", 2));
        f.Bulk.BeforeRow = (table, index, _) => table == "A" && index == 6 ? throw new FakeDbException("ORA-03113: end-of-file") : ValueTask.CompletedTask;
        f.Bulk.OnDml = async (call, _) =>
        {
            if (call.Table == "A" && call.Batch == 1)
            {
                // 첫 배치를 쓰는 동안 원본이 실패한다(둘째·셋째 배치는 버퍼에 있다). 읽기 작업이 실패를 남길 틈을 준다.
                await f.Bulk.StreamEnded("A").WaitAsync(Quick, CancellationToken.None);
                await Task.Delay(200, CancellationToken.None);
            }

            return null;
        };

        var result = await f.RunAsync();

        var a = result.Tables.Single(t => t.Table == "A");
        Assert.Equal(CopyOutcome.Failed, a.Outcome);
        Assert.Contains("원본을 읽지 못했습니다", a.Message, StringComparison.Ordinal);
        Assert.Equal([2], f.Bulk.DmlRowCounts("A")); // 버퍼의 배치는 보내지 않았다
        Assert.Contains("ROLLBACK TO SAVEPOINT " + CopyCommonStatements.TableSavepoint, f.Bulk.TargetControls);
        Assert.Equal(CopyOutcome.Succeeded, result.Tables.Single(t => t.Table == "B").Outcome);
    }

    // ---- 중지 ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_during_a_db_wait_cancels_the_command_and_ends_both_tasks_promptly(bool serial)
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2, SerialReadWrite = serial }, ("A", 1_000));
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Bulk.OnDml = async (_, ct) =>
        {
            writing.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct); // DB 가 응답하지 않는 명령(취소는 듣는다)
            }
            catch (OperationCanceledException)
            {
                return new BulkResult(ExecutionStatus.Cancelled, 0);
            }

            return null;
        };
        using var cts = new CancellationTokenSource();

        var run = f.RunAsync(cts.Token);
        await writing.Task.WaitAsync(Quick);
        await cts.CancelAsync();
        var result = await run.WaitAsync(Quick);

        Assert.Equal(CopyOutcome.Stopped, result.Outcome);
        Assert.True(f.Bulk.StreamEnded("A").IsCompleted, "원본 읽기가 남았다");
        Assert.Contains("ROLLBACK", f.Bulk.TargetControls); // 마무리는 대상 트랜잭션을 되돌린다
    }

    [Fact]
    public async Task Stop_while_a_command_ignores_cancel_gives_up_on_that_session_after_the_grace_time()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2 }, ("A", 100));
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Bulk.OnDml = async (_, _) =>
        {
            writing.TrySetResult();
            await release.Task; // 취소를 듣지 않는 명령
            return null;
        };
        using var cts = new CancellationTokenSource();
        try
        {
            var run = f.RunAsync(cts.Token);
            await writing.Task.WaitAsync(Quick);
            await cts.CancelAsync();
            var result = await run.WaitAsync(Quick);

            Assert.Equal(CopyOutcome.Stopped, result.Outcome);
            Assert.Contains(result.Notices, n => n.Contains("돌아오지 않아 대상 세션을 더 쓰지 않고", StringComparison.Ordinal));
            // 버린 대상 세션에는 마무리(커밋·롤백)를 보내지 않는다.
            Assert.DoesNotContain("ROLLBACK", f.Bulk.TargetControls);
            Assert.DoesNotContain("COMMIT", f.Bulk.TargetControls);
            Assert.Contains(f.Record.Events, e => e.Kind == "stuck");
        }
        finally
        {
            release.TrySetResult();
        }
    }

    // ---- 멈춤 표시 ----

    [Fact]
    public async Task A_slow_batch_write_shows_the_wait_in_the_phase_cell_and_the_work_log_and_clears_it_after()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2 }, ("A", 4));
        f.Bulk.OnDml = async (call, _) =>
        {
            if (call.Batch == 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(600), CancellationToken.None);
            }

            return null;
        };

        var result = await f.RunAsync();

        Assert.Equal(CopyOutcome.Succeeded, Assert.Single(result.Tables).Outcome);
        var log = Assert.Single(f.Progress.Logs, l => l.Text == "배치 1 쓰는 중 · 1분째 대기");
        Assert.Equal(CopyLogKind.Warning, log.Kind);
        Assert.Equal("A", log.Table);
        Assert.Equal(CopyRunner.PhaseLoad, log.Phase);
        var reports = f.Progress.Reports.Where(p => p.Waits is not null).ToList();
        var shown = reports.FindIndex(p => p.Waits!.Any(w => w.Side == CopyWaitSide.Target && w.Batch == 1));
        Assert.True(shown >= 0, "단계 칸 멈춤 표시를 보내지 않았다");
        Assert.Contains(reports.Skip(shown + 1), p => p.Waits!.Count == 0); // 풀리면 지운다
        Assert.Contains(f.Record.Events, e => e is { Kind: "wait", Table: "A" } && e.Text == "배치 1 쓰는 중 · 1분째 대기");
    }

    [Fact]
    public async Task A_slow_source_read_shows_the_reading_wait()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2 }, ("A", 2));
        f.Bulk.BeforeRow = async (_, index, ct) =>
        {
            if (index == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(600), ct);
            }
        };

        var result = await f.RunAsync();

        Assert.Equal(CopyOutcome.Succeeded, Assert.Single(result.Tables).Outcome);
        Assert.Contains(f.Progress.Logs, l => l.Text == "원본 읽는 중 · 1분째" && l.Table == "A");
        Assert.Contains(f.Progress.Reports, p => p.Waits is { Count: > 0 } w && w[0].Side == CopyWaitSide.Source);
    }

    [Fact]
    public void Wait_text_counts_the_minute_it_is_in()
    {
        Assert.Equal("배치 3 쓰는 중 · 1분째 대기", CopyLogText.Wait(new CopyWait(CopyWaitSide.Target, 3, TimeSpan.FromSeconds(10))));
        Assert.Equal("배치 3 쓰는 중 · 2분째 대기", CopyLogText.Wait(new CopyWait(CopyWaitSide.Target, 3, TimeSpan.FromSeconds(61))));
        Assert.Equal("원본 읽는 중 · 5분째", CopyLogText.Wait(new CopyWait(CopyWaitSide.Source, null, TimeSpan.FromMinutes(4.5))));
    }

    // ---- 대기 한도 ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wait_limit_cancels_a_hanging_write_fails_that_table_rolls_it_back_and_moves_to_the_next(bool serial)
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2, WaitLimitMinutes = 1, SerialReadWrite = serial }, ("A", 4), ("B", 2));
        f.Bulk.OnDml = async (call, ct) =>
        {
            if (call.Table == "A" && call.Batch == 2)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct); // 행 잠금을 기다리는 INSERT
                }
                catch (OperationCanceledException)
                {
                    return new BulkResult(ExecutionStatus.Cancelled, 0);
                }
            }

            return null;
        };

        var result = await f.RunAsync().WaitAsync(Quick);

        var a = result.Tables.Single(t => t.Table == "A");
        Assert.Equal(CopyOutcome.Failed, a.Outcome);
        Assert.Contains("대기 한도 초과(1분)", a.Message, StringComparison.Ordinal);
        Assert.Contains("배치 2 쓰는 중", a.Message, StringComparison.Ordinal);
        Assert.Contains("ROLLBACK TO SAVEPOINT " + CopyCommonStatements.TableSavepoint, f.Bulk.TargetControls);
        Assert.Equal(CopyOutcome.Succeeded, result.Tables.Single(t => t.Table == "B").Outcome);
        Assert.Equal(CopyOutcome.PartiallyFailed, result.Outcome);
        Assert.Contains(f.Record.Events, e => e is { Kind: "waitLimit", Table: "A" });
    }

    [Fact]
    public async Task Wait_limit_on_a_hanging_source_read_fails_that_table()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2, WaitLimitMinutes = 1 }, ("A", 4), ("B", 2));
        f.Bulk.BeforeRow = async (table, index, ct) =>
        {
            if (table == "A" && index == 2)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        };

        var result = await f.RunAsync().WaitAsync(Quick);

        var a = result.Tables.Single(t => t.Table == "A");
        Assert.Equal(CopyOutcome.Failed, a.Outcome);
        Assert.Contains("원본 읽는 중", a.Message, StringComparison.Ordinal);
        Assert.Equal(CopyOutcome.Succeeded, result.Tables.Single(t => t.Table == "B").Outcome);
    }

    [Fact]
    public async Task Wait_limit_whose_cancel_is_ignored_stops_the_run_and_leaves_the_target_session_alone()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2, WaitLimitMinutes = 1 }, ("A", 4), ("B", 2));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Bulk.OnDml = async (call, _) =>
        {
            if (call.Table == "A")
            {
                await release.Task; // 취소를 듣지 않는다
            }

            return null;
        };
        try
        {
            var result = await f.RunAsync().WaitAsync(Quick);

            Assert.Equal(CopyOutcome.Failed, result.Outcome);
            Assert.Contains(result.Notices, n => n.Contains("돌아오지 않아 대상 세션을 더 쓰지 않고 실행을 멈춥니다", StringComparison.Ordinal));
            Assert.Empty(f.Bulk.DmlRowCounts("B")); // 다음 테이블로 가지 않는다
            Assert.DoesNotContain("COMMIT", f.Bulk.TargetControls);
            Assert.DoesNotContain("ROLLBACK", f.Bulk.TargetControls);
            Assert.Contains("대기 한도 초과", result.Tables.Single(t => t.Table == "A").Message, StringComparison.Ordinal);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Each_source_row_is_written_once_in_both_modes_including_a_short_last_batch(bool serial)
    {
        // 원격 #155: 옛 가짜가 한 행짜리 마지막 배치를 앞 배치 번호로 셌다. 실제로 같은 배치를 두 번 쓰지 않는다는 것을 키로 고정한다.
        var f = new Fixture(new CopyOptions { BatchSize = 2, SerialReadWrite = serial }, ("A", 5));

        var result = await f.RunAsync();

        Assert.Equal(CopyOutcome.Succeeded, Assert.Single(result.Tables).Outcome);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], f.Bulk.WrittenIds("A").Cast<long>());
        Assert.Equal([2, 2, 1], f.Bulk.DmlRowCounts("A"));
    }

    // ---- 리뷰 PF1-1: 버린 펌프의 반대쪽 취소 ----

    [Fact]
    public async Task Stuck_target_write_still_cancels_the_reader_so_the_source_rollback_goes_right_away()
    {
        // 원본 세션 관문(실제 SessionCommandGate 흉내): 읽기가 끝나야 마무리의 원본 ROLLBACK 이 차례를 받는다.
        var f = new Fixture(new CopyOptions { BatchSize = 2, WaitLimitMinutes = 1 }, ("A", 1_000));
        f.Bulk.GateSource = true;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Bulk.OnDml = async (_, _) =>
        {
            await release.Task; // 취소를 듣지 않는 쓰기(읽기 작업은 꽉 찬 채널을 기다린다)
            return null;
        };
        try
        {
            var result = await f.RunAsync().WaitAsync(Quick);

            Assert.True(f.Bulk.StreamEnded("A").IsCompleted, "원본 읽기가 끝나지 않았다");
            // 시작의 「앞 트랜잭션 닫기」와 마무리의 「원본 읽기 전용 트랜잭션 닫기」(관문에서 2분 기다리지 않고 바로).
            Assert.Equal(2, f.Bulk.SourceControls.Count(c => c == "ROLLBACK"));
            Assert.True(result.TargetAbandoned);
            Assert.False(result.SourceAbandoned);
            Assert.DoesNotContain(result.Notices, n => n.Contains("원본 세션의 명령이 돌아오지 않아", StringComparison.Ordinal));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Stuck_target_write_in_serial_mode_gives_up_on_the_source_session_too_because_the_cursor_is_held()
    {
        // 번갈아 하면 멈춘 쓰기가 원본 커서를 쥔 채다: 원본 ROLLBACK 을 보내면 관문에서 2분 기다린다 → 원본 세션도 버리고 바로 끝낸다.
        var f = new Fixture(new CopyOptions { BatchSize = 2, WaitLimitMinutes = 1, SerialReadWrite = true }, ("A", 1_000));
        f.Bulk.GateSource = true;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Bulk.OnDml = async (_, _) =>
        {
            await release.Task;
            return null;
        };
        try
        {
            var result = await f.RunAsync().WaitAsync(Quick);

            Assert.True(result.TargetAbandoned);
            Assert.True(result.SourceAbandoned);
            Assert.True(result.SessionsAbandoned);
            Assert.Equal(1, f.Bulk.SourceControls.Count(c => c == "ROLLBACK")); // 시작 때 하나뿐: 버린 원본 세션에는 마무리를 보내지 않는다
            Assert.Contains(result.Notices, n => n.Contains("원본 읽기도 끝나지 않아 원본 세션도 더 쓰지 않습니다", StringComparison.Ordinal));
            Assert.Contains(result.Notices, n => n.Contains("원본 세션의 명령이 돌아오지 않아 원본 읽기 전용 트랜잭션을 닫지 않았습니다", StringComparison.Ordinal));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    // ---- 리뷰 PF1-2: 「배치마다 커밋」+ 대기 한도 ----

    [Fact]
    public async Task Per_batch_commit_wait_limit_drops_the_failed_rows_of_the_rolled_back_batch()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 4, Commit = CommitMode.PerBatch, WaitLimitMinutes = 1, MaxFailedRows = 100 }, ("A", 4));
        var single = 0;
        f.Bulk.OnDml = async (call, ct) =>
        {
            if (call.Rows > 1)
            {
                return new BulkResult(ExecutionStatus.Failed, 0) { Error = new DbError("ORA-00001", "unique") }; // 배치 실패 → 한 행씩
            }

            switch (Interlocked.Increment(ref single))
            {
                case 1:
                    return new BulkResult(ExecutionStatus.Failed, 0) { Error = new DbError("ORA-00001", "unique") }; // 실패 행 하나
                case 2:
                    try
                    {
                        await Task.Delay(Timeout.Infinite, ct); // 다음 행이 행 잠금을 기다린다 → 대기 한도
                    }
                    catch (OperationCanceledException)
                    {
                        return new BulkResult(ExecutionStatus.Cancelled, 0);
                    }

                    return null;
                default:
                    return null;
            }
        };

        var result = await f.RunAsync().WaitAsync(Quick);

        var a = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Failed, a.Outcome);
        Assert.Contains("대기 한도 초과(1분)", a.Message, StringComparison.Ordinal);
        Assert.Contains("ROLLBACK TO SAVEPOINT " + CopyCommonStatements.BatchSavepoint, f.Bulk.TargetControls);
        // 되돌린 배치의 실패 행은 결과에도 실행 기록에도 없다(마무리 커밋 때 확정하지 않는다).
        Assert.Empty(result.FailedRows);
        Assert.Empty(f.Record.FailedRows);
    }

    // ---- 리뷰 PF1-4: 대상 정리(DELETE)·테이블 커밋도 감시 ----

    [Fact]
    public async Task Wait_limit_on_a_hanging_delete_fails_only_that_table_shows_the_wait_and_the_other_table_loads()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2, WaitLimitMinutes = 1 }, TargetAction.DeleteAllInsert, ("A", 2), ("B", 2));
        f.Execution.OnExecute = async (stmt, ct) =>
        {
            if (stmt.Text.Contains("\"A\"", StringComparison.Ordinal))
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct); // 행 잠금을 기다리는 DELETE(취소는 듣는다)
                }
                catch (OperationCanceledException)
                {
                    return new ExecutionResult(stmt, ExecutionStatus.Cancelled);
                }
            }

            return null;
        };

        var result = await f.RunAsync().WaitAsync(Quick);

        var a = result.Tables.Single(t => t.Table == "A");
        Assert.Equal(CopyOutcome.Failed, a.Outcome);
        Assert.Contains("대기 한도 초과(1분)", a.Message, StringComparison.Ordinal);
        Assert.Contains("대상 정리(DELETE) 중", a.Message, StringComparison.Ordinal);
        Assert.Empty(f.Bulk.DmlRowCounts("A")); // 지우지 못한 테이블은 적재하지 않는다
        Assert.Equal(CopyOutcome.Succeeded, result.Tables.Single(t => t.Table == "B").Outcome);
        Assert.Contains(f.Progress.Logs, l => l is { Text: "대상 정리(DELETE) 중 · 1분째 대기", Table: "A", Phase: CopyRunner.PhaseDelete });
        Assert.Contains(f.Progress.Reports, p => p.Waits is { Count: > 0 } w && w[0].Step == CopyWaitStep.Delete && p.Phase == CopyRunner.PhaseDelete);
        Assert.Contains(f.Record.Events, e => e is { Kind: "waitLimit", Table: "A" });
    }

    [Fact]
    public async Task Delete_whose_cancel_is_ignored_gives_up_on_the_target_session_and_does_not_load()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2, WaitLimitMinutes = 1 }, TargetAction.DeleteAllInsert, ("A", 2), ("B", 2));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Execution.OnExecute = async (stmt, _) =>
        {
            if (stmt.Text.Contains("\"B\"", StringComparison.Ordinal))
            {
                await release.Task; // 취소를 듣지 않는다(정리는 자식 → 부모라 B 가 먼저다)
            }

            return null;
        };
        try
        {
            var result = await f.RunAsync().WaitAsync(Quick);

            Assert.True(result.TargetAbandoned);
            Assert.Contains(result.Notices, n => n.Contains("돌아오지 않아 대상 세션을 더 쓰지 않고 실행을 멈춥니다", StringComparison.Ordinal));
            Assert.Empty(f.Bulk.DmlRowCounts("A"));
            Assert.Empty(f.Bulk.DmlRowCounts("B"));
            Assert.DoesNotContain(f.Execution.Statements, s => s.Contains("\"A\"", StringComparison.Ordinal)); // 다음 DELETE 도 보내지 않는다
            Assert.DoesNotContain("COMMIT", f.Bulk.TargetControls);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Wait_limit_on_a_hanging_table_commit_rolls_that_table_back_and_the_next_table_runs()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2, WaitLimitMinutes = 1 }, ("A", 2), ("B", 2));
        var commits = 0;
        f.Bulk.OnTargetControl = async (sql, ct) =>
        {
            if (sql == "COMMIT" && Interlocked.Increment(ref commits) == 1)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct); // A 의 「테이블마다 커밋」이 응답하지 않는다(취소는 듣는다)
                }
                catch (OperationCanceledException)
                {
                    return new BulkResult(ExecutionStatus.Cancelled, 0);
                }
            }

            return null;
        };

        var result = await f.RunAsync().WaitAsync(Quick);

        var a = result.Tables.Single(t => t.Table == "A");
        Assert.Equal(CopyOutcome.Failed, a.Outcome);
        Assert.Contains("테이블 커밋 중", a.Message, StringComparison.Ordinal);
        Assert.Contains("이 테이블을 되돌렸습니다", a.Message, StringComparison.Ordinal);
        Assert.Contains("ROLLBACK TO SAVEPOINT " + CopyCommonStatements.TableSavepoint, f.Bulk.TargetControls);
        Assert.Equal(CopyOutcome.Succeeded, result.Tables.Single(t => t.Table == "B").Outcome);
    }

    [Fact]
    public void Wait_text_names_the_delete_and_commit_steps()
    {
        Assert.Equal("대상 정리(DELETE) 중 · 2분째 대기",
            CopyLogText.Wait(new CopyWait(CopyWaitSide.Target, null, TimeSpan.FromSeconds(70)) { Step = CopyWaitStep.Delete }));
        Assert.Equal("테이블 커밋 중 · 1분째 대기", CopyLogText.Wait(new CopyWait(CopyWaitSide.Target, null, TimeSpan.FromSeconds(10)) { Step = CopyWaitStep.Commit }));
    }

    // ---- 실행 기록·시간 나눔 ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Each_table_end_is_recorded_right_away_with_the_read_convert_write_split(bool serial)
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2, SerialReadWrite = serial }, ("A", 3), ("B", 2));
        f.Bulk.OnDml = async (_, _) =>
        {
            await Task.Delay(20, CancellationToken.None);
            return null;
        };

        var result = await f.RunAsync();

        Assert.Equal(["A", "B"], f.Record.Events.Where(e => e.Kind == "tableEnd").Select(e => e.Table));
        Assert.All(f.Record.Events.Where(e => e.Kind == "tableEnd"), e => Assert.Contains("행/초", e.Text, StringComparison.Ordinal));
        var a = result.Tables.Single(t => t.Table == "A");
        var times = Assert.IsType<CopyTableTimes>(a.Times);
        Assert.True(times.Write >= TimeSpan.FromMilliseconds(30), "쓰기 시간(배치 둘 × 20ms)을 재지 않았다: " + times.Write);
        Assert.True(times.RowsPerSecond > 0);
        Assert.Contains(f.Progress.Logs, l => l.Table == "A" && l.Text.StartsWith("적재 끝", StringComparison.Ordinal) && l.Text.Contains(" · 읽기 ", StringComparison.Ordinal)
                                              && l.Text.Contains(" · 쓰기 ", StringComparison.Ordinal));
        // 진행 보고에 넣은 수가 따라온다(첫 배치 뒤).
        Assert.Contains(f.Progress.Reports, p => p.Table == "A" && p.Written == 2);
    }

    [Fact]
    public void Times_text_shows_read_convert_write_and_rows_per_second()
    {
        var text = CopyLogText.Times(new CopyTableTimes(TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(0.05), TimeSpan.FromSeconds(3.4), 1234.4));
        Assert.Equal("읽기 1.20초 · 변환 0.05초 · 쓰기 3.40초 · 1,234행/초", text);
    }

    [Fact]
    public void Times_text_names_the_load_span_basis_the_table_total_and_the_rest()
    {
        var times = new CopyTableTimes(TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(0.05), TimeSpan.FromSeconds(3.4), 66_811)
        {
            Load = TimeSpan.FromSeconds(4.49), Total = TimeSpan.FromSeconds(10.7), FirstRow = TimeSpan.FromSeconds(0.3),
        };

        Assert.Equal("적재 구간 4.49초 · 읽기 1.20초(첫 행까지 0.30초) · 변환 0.05초 · 쓰기 3.40초 · 66,811행/초 · 테이블 전체 10.70초 · 기타 6.21초",
            CopyLogText.Times(times));
    }

    [Fact]
    public async Task Table_times_count_rows_per_second_over_the_load_span_and_put_delete_and_count_in_the_table_total()
    {
        // 원격 #155: 끔 10.7초인데 읽기 + 쓰기 4.4초 — 정리(DELETE)·건수 비교가 어디에도 안 보였다.
        var f = new Fixture(new CopyOptions { BatchSize = 2 }, TargetAction.DeleteAllInsert, ("A", 4));
        f.Execution.OnExecute = async (stmt, _) =>
        {
            await Task.Delay(300, CancellationToken.None); // 느린 DELETE
            return null;
        };
        f.Bulk.BeforeRow = async (_, index, _) =>
        {
            if (index == 0)
            {
                await Task.Delay(50, CancellationToken.None); // 질의 시작 → 첫 행
            }
        };

        var result = await f.RunAsync();

        var times = Assert.IsType<CopyTableTimes>(Assert.Single(result.Tables).Times);
        Assert.NotNull(times.Load);
        Assert.NotNull(times.Total);
        var (load, total) = (times.Load.Value, times.Total.Value);
        Assert.True(total - load >= TimeSpan.FromMilliseconds(250), $"테이블 전체에 정리 시간이 없다: 전체 {total} · 적재 {load}");
        Assert.True(times.Other >= TimeSpan.FromMilliseconds(250));
        Assert.True(times.FirstRow >= TimeSpan.FromMilliseconds(40), "첫 행까지를 따로 재지 않았다: " + times.FirstRow);
        Assert.Equal(4 / load.TotalSeconds, times.RowsPerSecond, 3); // 행/초 = 넣은 행 / 적재 구간
    }

    // ---- 가짜 협력자 ----

    private static async Task<bool> Completes(Task task, TimeSpan within)
    {
        try
        {
            await task.WaitAsync(within);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private sealed record DmlCall(string Table, int Batch, int Rows);

    private sealed record RecordedEvent(string Kind, string? Table, string Text);

    private sealed class Fixture
    {
        public Fixture(CopyOptions options, params (string Table, int Rows)[] tables)
            : this(options, TargetAction.AppendOnly, tables)
        {
        }

        /// <param name="action">대상 처리. 지우고 넣기(<see cref="TargetAction.DeleteAllInsert"/>)면 사전 점검 미리 보기에 DELETE 문장을 넣는다.</param>
        public Fixture(CopyOptions options, TargetAction action, params (string Table, int Rows)[] tables)
        {
            Tables = tables;
            var rules = tables.Select(t => new TableRule(t.Table, RuleKind.All, action)).ToList();
            Job = new CopyJob
            {
                Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "DST"),
                SourceOwner = "SRC", TargetOwner = "DST", MaskSalt = "AA==", Tables = rules, Options = options,
            };
            var plans = rules.Select(r => new CopyTablePlan(r, Info("SRC", r.Table), Info("DST", r.Table), ["ID", "NAME"], ["ID"])).ToList();
            var builder = new CopySqlBuilder(Job, new ResolvedPeriod(null, null), plans.ToDictionary(p => p.Rule.Table, p => p.Source, StringComparer.Ordinal),
                plans.ToDictionary(p => p.Rule.Table, p => p.Target, StringComparer.Ordinal));
            var preview = rules.Select(r => builder.TargetDelete(r.Table)?.Sql).OfType<string>()
                .Select(sql => new PreviewStatement(sql, PreviewSide.Target, PolicyDecisionKind.Allow, null, null)).ToList();
            Report = new PrecheckReport([], plans, preview, new Dictionary<string, long>());
            Source = new FakeSession(TestData.Profile(readOnly: true), TestData.Server()) { DbKey = "SRCDB/PDB1/SRC" };
            Target = new FakeSession(TestData.Profile() with { Name = "dev" }, TestData.Server()) { DbKey = "DSTDB/PDB1/DST" };
            Bulk = new ScriptedBulk(this);
            Record = new EventRecord();
            var parts = new CopyDbmsParts(Dbms.Oracle, new NoCatalog(), new FakeClassifier(), new FakeDialect(), new FakePrecheckDialect(), new SimpleCodec(), new FakeRecreateStep());
            Runner = new CopyRunner(Bulk, Execution, new FakeRunLog(Record), parts, parts)
            {
                // 멈춤 표시 0.2초, 감시 20ms, 취소한 명령을 0.5초 기다림, 대기 한도 1 = 0.3초.
                WatchTiming = new CopyWatchTiming(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(300)),
            };
        }

        public IReadOnlyList<(string Table, int Rows)> Tables { get; }

        public CopyJob Job { get; }

        public PrecheckReport Report { get; }

        public FakeSession Source { get; }

        public FakeSession Target { get; }

        public ScriptedBulk Bulk { get; }

        /// <summary>실행 엔진(대상 DELETE). 기본은 바로 성공.</summary>
        public ScriptedExecution Execution { get; } = new();

        public EventRecord Record { get; }

        public ProgressLog Progress { get; } = new();

        private CopyRunner Runner { get; }

        public Task<CopyRunResult> RunAsync(CancellationToken ct = default) => Runner.RunAsync(new CopySessions(Source, Target), Job, Report,
            new CopyDecisions(new HashSet<string>(), new HashSet<ForeignKeyRef>(), new HashSet<string>(), false),
            new ResolvedPeriod(null, null), Progress, new CopyRunControl(), ct);

        public string TableOf(string sql) =>
            Tables.Select(t => t.Table).FirstOrDefault(t => sql.Contains("\"" + t + "\"", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("표를 모르는 문장: " + sql);

        private static TableInfo Info(string owner, string table) =>
            new(new DbObjectRef(owner, table, "TABLE"),
            [
                new ColumnInfo("ID", 1, "NUMBER") { Precision = 10, Scale = 0, IsNullable = false },
                new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 20 },
            ])
            {
                Constraints = [new ConstraintInfo("PK_" + table, ConstraintKind.PrimaryKey, ["ID"])],
            };
    }

    /// <summary>
    /// 표마다 정한 수의 원본 행을 내고(행 앞 갈고리 <see cref="BeforeRow"/>), 배열 DML 은 <see cref="OnDml"/> 로 답한다(null = 성공).
    /// 읽기·쓰기 사건을 순서대로 남긴다(읽기는 스레드 풀에서 올 수 있어 동시 모음).
    /// </summary>
    private sealed class ScriptedBulk(Fixture fixture) : IBulkExecutionService, IDisposable
    {
        private readonly ConcurrentDictionary<string, long> _inserted = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _batches = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<(string, int), TaskCompletionSource> _reached = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _ended = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<DmlCall> _dml = new();
        private readonly SemaphoreSlim _sourceGate = new(1, 1);
        private readonly ConcurrentQueue<(string Table, object? Id)> _written = new();
        private int _newBatch;

        public Func<string, int, CancellationToken, ValueTask>? BeforeRow { get; set; }

        public Func<DmlCall, CancellationToken, Task<BulkResult?>>? OnDml { get; set; }

        /// <summary>대상 트랜잭션 제어(세이브포인트·COMMIT 등)에 답한다(null = 성공). 글을 받는다.</summary>
        public Func<string, CancellationToken, Task<BulkResult?>>? OnTargetControl { get; set; }

        /// <summary>
        /// 원본 세션의 명령 관문(실제 SessionCommandGate 흉내, D-041): 원본 읽기는 커서를 닫을 때까지 관문을 쥐고, 원본 트랜잭션 제어는 관문을 기다린다.
        /// </summary>
        public bool GateSource { get; set; }

        public ConcurrentQueue<string> Events { get; } = new();

        public ConcurrentQueue<string> TargetControls { get; } = new();

        /// <summary>원본 세션에 보낸 트랜잭션 제어(관문을 지난 뒤 남긴다).</summary>
        public ConcurrentQueue<string> SourceControls { get; } = new();

        public void Dispose() => _sourceGate.Dispose();

        public Task ReadReached(string table, int row) => Reached(table, row).Task;

        public Task StreamEnded(string table) => Ended(table).Task;

        public IReadOnlyList<int> DmlRowCounts(string table) => [.. _dml.Where(d => d.Table == table).Select(d => d.Rows)];

        /// <summary>대상에 넣은(성공한 DML 의) 행의 키(ID) 값, 넣은 차례대로.</summary>
        public IReadOnlyList<object?> WrittenIds(string table) => [.. _written.Where(w => w.Table == table).Select(w => w.Id)];

        public async IAsyncEnumerable<object?[]> StreamRowsAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                                                 ExecutionOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            var table = fixture.TableOf(query.Text);
            if (!ReferenceEquals(s, fixture.Source))
            {
                Events.Enqueue("count " + table);
                yield return [_inserted.GetValueOrDefault(table)]; // 대상 COUNT(*)
                yield break;
            }

            var gated = false;
            try
            {
                if (GateSource)
                {
                    await _sourceGate.WaitAsync(ct);
                    gated = true;
                }

                var rows = fixture.Tables.Single(t => t.Table == table).Rows;
                for (var i = 0; i < rows; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (BeforeRow is { } before)
                    {
                        await before(table, i, ct);
                    }

                    Events.Enqueue($"read {table} {i + 1}");
                    Reached(table, i + 1).TrySetResult();
                    yield return [(long)(i + 1), "n" + i];
                }
            }
            finally
            {
                if (gated)
                {
                    _sourceGate.Release();
                }

                Ended(table).TrySetResult();
            }
        }

        public async Task<BulkResult> ExecuteArrayAsync(ISession s, SqlStatement stmt, IReadOnlyList<BulkColumn> columns, IReadOnlyList<object?[]> rows,
                                                        ExecutionOptions options, CancellationToken ct)
        {
            if (columns.Count == 0)
            {
                // 트랜잭션 제어
                if (ReferenceEquals(s, fixture.Target))
                {
                    TargetControls.Enqueue(stmt.Text);
                    if (stmt.Text == "SAVEPOINT " + CopyCommonStatements.BatchSavepoint)
                    {
                        Volatile.Write(ref _newBatch, 1); // 다음 DML 은 새 배치
                    }

                    return (OnTargetControl is { } control ? await control(stmt.Text, ct) : null) ?? new BulkResult(ExecutionStatus.Succeeded, 0);
                }

                if (GateSource)
                {
                    await _sourceGate.WaitAsync(ct);
                    try
                    {
                        SourceControls.Enqueue(stmt.Text);
                    }
                    finally
                    {
                        _sourceGate.Release();
                    }
                }
                else
                {
                    SourceControls.Enqueue(stmt.Text);
                }

                return new BulkResult(ExecutionStatus.Succeeded, 0);
            }

            var table = fixture.TableOf(stmt.Text);
            // 배치 번호는 배치 세이브포인트마다 하나 는다(실행기가 배치마다 먼저 건다). 한 행씩 다시 넣기는 같은 배치 번호다.
            // 예전 가짜는 「여러 행 = 새 배치」로 셌다: 한 행짜리 마지막 배치를 앞 배치 번호로 보여 같은 배치를 다시 쓰는 것처럼 보였다(원격 #155).
            if (Interlocked.Exchange(ref _newBatch, 0) == 1 || !_batches.TryGetValue(table, out var batch))
            {
                batch = _batches.AddOrUpdate(table, 1, (_, n) => n + 1);
            }

            var call = new DmlCall(table, batch, rows.Count);
            _dml.Enqueue(call);
            Events.Enqueue($"write-start {table} {call.Batch}");
            var result = (OnDml is { } on ? await on(call, ct) : null) ?? new BulkResult(ExecutionStatus.Succeeded, rows.Count);
            if (result.Status == ExecutionStatus.Succeeded)
            {
                _inserted.AddOrUpdate(table, rows.Count, (_, n) => n + rows.Count);
                foreach (var row in rows)
                {
                    _written.Enqueue((table, row[0]));
                }
            }

            Events.Enqueue($"write-end {table} {call.Batch}");
            return result;
        }

        private TaskCompletionSource Ended(string table) =>
            _ended.GetOrAdd(table, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        private TaskCompletionSource Reached(string table, int row) =>
            _reached.GetOrAdd((table, row), _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    /// <summary>진행 보고·작업 로그 모음(감시가 다른 스레드에서 보내도 된다).</summary>
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
        private readonly ConcurrentQueue<RecordedEvent> _events = new();
        private readonly ConcurrentQueue<FailedRow> _failedRows = new();

        public IReadOnlyList<RecordedEvent> Events => [.. _events];

        /// <summary>실행 기록에 쓴 실패 행(확정된 것만 쓴다).</summary>
        public IReadOnlyList<FailedRow> FailedRows => [.. _failedRows];

        public Task RecordDisabledAsync(IReadOnlyList<DisabledObject> objects, CancellationToken ct) => Task.CompletedTask;

        public Task RecordStatementAsync(string sql, CancellationToken ct) => Task.CompletedTask;

        public Task RecordTableAsync(TableRunResult result, CancellationToken ct) => Task.CompletedTask;

        public Task RecordFailedRowAsync(FailedRow row, CancellationToken ct)
        {
            _failedRows.Enqueue(row);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(CopyOutcome outcome, CancellationToken ct) => Task.CompletedTask;

        public Task RecordEventAsync(string kind, string? table, string text, CancellationToken ct)
        {
            _events.Enqueue(new RecordedEvent(kind, table, text));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRunLog(EventRecord record) : ICopyRunLog
    {
        public Task<ICopyRunRecord> BeginAsync(CopyRunHeader header, CancellationToken ct) => Task.FromResult<ICopyRunRecord>(record);

        public Task<IReadOnlyList<UnfinishedRun>> FindUnfinishedAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<UnfinishedRun>>([]);

        public Task MarkRecoveredAsync(string runFile, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>실행 엔진(대상 정리 DELETE 등): <see cref="OnExecute"/> 로 답한다(null = 성공). 토큰을 듣는 명령·듣지 않는 명령을 흉내 낸다.</summary>
    private sealed class ScriptedExecution : IExecutionService
    {
        public Func<SqlStatement, CancellationToken, Task<ExecutionResult?>>? OnExecute { get; set; }

        public ConcurrentQueue<string> Statements { get; } = new();

        public async Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds,
                                                        ExecutionOptions? options, CancellationToken ct)
        {
            Statements.Enqueue(stmt.Text);
            return (OnExecute is { } on ? await on(stmt, ct) : null) ?? new ExecutionResult(stmt, ExecutionStatus.Succeeded);
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

    private sealed class FakeDbException(string message) : DbException(message);

    private sealed class FakeClassifier : IDbErrorClassifier
    {
        public Dbms Dbms => Dbms.Oracle;

        public DbErrorKind Classify(int number) => DbErrorKind.Other;

        public DbError? FromException(Exception exception) => exception is FakeDbException e ? new DbError("ORA-03113", e.Message) { Number = 3113 } : null;
    }

    private sealed class SimpleCodec : IBulkValueCodec
    {
        public bool IsSupported(string serverType) => true;

        public BulkKind KindOf(string serverType) => serverType.StartsWith("VARCHAR", StringComparison.Ordinal) ? BulkKind.Text : BulkKind.Other;

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
