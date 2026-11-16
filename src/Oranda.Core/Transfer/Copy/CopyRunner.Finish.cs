using System.Diagnostics;
using System.Globalization;
using Oranda.Core.Execution;
using Oranda.Core.Text;

namespace Oranda.Core.Transfer.Copy;

public partial class CopyRunner
{
    // ---- 5. 끝내기(트랜잭션 → 다시 켜기 → 건수 → 기록) ----

    /// <summary>
    /// 중지·예외 뒤에도 반드시 지나간다. <c>ct</c> 를 쓰지 않고 단계마다 새 토큰을 쓴다(한 단계가 시간을 다 써도 다음 단계는 돈다).
    /// 대상 트랜잭션을 끝내지 못했으면 다시 켜기(DDL, 묵시 커밋)를 하지 않는다: 켜면 되돌리려던 행이 확정된다.
    /// </summary>
    private async Task FinishAsync(RunState run, bool faulted)
    {
        run.Enter(PhaseFinish);
        if (run.Stopped)
        {
            run.Log("중지 요청을 받았습니다. 대상 변경을 되돌리고 끈 것을 다시 켭니다.", CopyLogKind.Warning);
        }
        else if (faulted)
        {
            run.Log("예상하지 못한 오류로 멈춥니다. 대상 변경을 되돌리고 끈 것을 다시 켭니다.", CopyLogKind.Error);
        }

        var ended = false;
        try
        {
            // 끝내지 못한 레인(PF2)이 있으면 그 트랜잭션이 남아 다시 켜기(DDL)를 막는다: 끝내지 못한 것으로 둔다.
            ended = await EndTargetTransactionAsync(run, faulted) && !run.LaneLeftOpen;
        }
        catch (Exception ex) when (IsCleanupFailure(ex))
        {
            run.Notices.Add("대상 트랜잭션을 끝내지 못했습니다: " + SafeError(ex.Message));
        }

        if (ended)
        {
            await PurgeUnloadedNewTablesAsync(run);
            run.EnableFailed = !await EnableAsync(run);
            await RestoreAsync(run);
        }
        else
        {
            if (run.Tables.Values.Any(t => t.CreatedNew && (!t.LoadCompleted || run.Job.Options.Commit == CommitMode.AtEnd)))
            {
                // 지우는 DROP 도 DDL(묵시 커밋)이라 대상 트랜잭션을 끝내지 못했으면 보내지 않는다.
                var left = run.Tables.Values.Where(t => t.CreatedNew && (!t.LoadCompleted || run.Job.Options.Commit == CommitMode.AtEnd)).Select(t => t.TargetName);
                run.Notices.Add("대상 트랜잭션을 끝내지 못해 적재를 끝내지 못한 새 테이블을 지우지 않았습니다. 세션을 정리한 뒤 직접 지우세요(DROP … PURGE): " + string.Join(", ", left));
            }

            if (run.Disabled.Count > 0)
            {
                run.EnableFailed = true;
                run.Notices.Add("대상 트랜잭션을 끝내지 못해 끈 트리거·FK 를 다시 켜지 않았습니다(켜면 미커밋 행이 확정됩니다). 세션을 정리한 뒤 직접 켜세요: "
                                + string.Join(", ", run.Disabled.Select(d => d.Owner + "." + d.Name)));
            }

            if (run.Tables.Values.Any(t => (t.Created && t.Preserved is { } p && p.Triggers.Count + p.Grants.Count + p.ReferencingForeignKeys.Count > 0)
                                           || (!t.Dropped && t.DetachedReferencing.Count > 0)))
            {
                // 되살리기도 DDL 이라 미커밋 행을 확정한다. 문장 원문은 NoteRecreated 가 표마다 남긴다.
                run.RestoreFailed = true;
                run.Notices.Add("대상 트랜잭션을 끝내지 못해 다시 만든 테이블의 트리거·권한·참조 FK 를 되살리지 않았습니다. 세션을 정리한 뒤 아래 DDL 을 직접 실행하세요.");
            }
        }

        NoteRecreated(run, restored: ended);

        if (run.SourceAbandoned)
        {
            // 돌아오지 않는 원본 명령이 세션을 쥐고 있다: 보내도 차례를 못 받는다. 세션을 닫으면 읽기 전용 트랜잭션도 끝난다.
            run.Notices.Add("원본 세션의 명령이 돌아오지 않아 원본 읽기 전용 트랜잭션을 닫지 않았습니다. 복사 창을 닫아 세션을 정리하세요.");
        }
        else
        {
            try
            {
                using var step = new CancellationTokenSource(CleanupTimeout);
                run.Enter(PhaseFinish);
                var rollback = await ControlLoggedAsync(run, run.Source, CopyCommonStatements.Rollback(), step.Token, "원본 읽기 전용 트랜잭션 닫기");
                if (rollback.Status != ExecutionStatus.Succeeded)
                {
                    run.Notices.Add("원본 읽기 전용 트랜잭션을 닫지 못했습니다: " + Describe(rollback));
                }
            }
            catch (Exception ex) when (IsCleanupFailure(ex))
            {
                run.Notices.Add("원본 읽기 전용 트랜잭션을 닫지 못했습니다: " + SafeError(ex.Message));
            }
        }

        if (!run.IsRetry)
        {
            // 적재를 끝내지 못한 표(상한 초과·중지·ORA-01555)는 「배치마다 커밋」이면 앞 배치의 실패 행이 확정돼 있다.
            // 그 행만 다시 넣으면 나머지 배치가 빠진 표가 성공으로 보이므로 결과에서 빼고 다시 실행하라고 적는다.
            // 실행 기록에는 앞 배치 커밋 때 쓴 failedRow 줄이 남는다(기록에 무효 표시 메서드가 없다). 그 표의 표 결과 메시지
            // (「적재를 끝내지 못해 … 다시 실행하세요」)가 그 줄들을 덮는다는 것이 기록의 계약이다(3차 리뷰 1, T13 문서).
            foreach (var t in run.Tables.Values.Where(t => !t.LoadCompleted))
            {
                if (run.FailedRows.RemoveAll(r => r.Table == t.Name) > 0)
                {
                    t.Note("적재를 끝내지 못해 「실패 행만 다시 넣기」를 쓸 수 없습니다. 다시 실행하세요.");
                }
            }
        }

        if (run.IsRetry)
        {
            // 확정(커밋)하지 못한 표의 입력 행은 사라지지 않게 그대로 돌려준다(중지·실패·시작하지 못한 표).
            var confirmed = run.Tables.Values.Where(t => t.Confirmed).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            // 커밋 도중 연결이 끊긴 표는 확정됐는지 알 수 없다: 행은 돌려주되 이미 들어갔을 수 있다고 적는다(3차 리뷰 3).
            var unknown = run.Tables.Values.Where(t => t.CommitUnknown).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            var unconfirmed = run.RetryInputs.Where(r => !confirmed.Contains(r.Table))
                .Select(r => unknown.Contains(r.Table) ? r with { Error = "확정 여부 모름(커밋 중 연결이 끊김, 이미 들어갔을 수 있음): " + r.Error } : r)
                .ToList();
            run.FailedRows.AddRange(unconfirmed);
            if (run.Record is { } retryRecord)
            {
                try
                {
                    using var step = new CancellationTokenSource(CleanupTimeout);
                    foreach (var row in unconfirmed)
                    {
                        await retryRecord.RecordFailedRowAsync(row, step.Token);
                    }
                }
                catch (Exception ex)
                {
                    run.Notices.Add("실행 기록에 실패 행을 남기지 못했습니다: " + SafeError(ex.Message));
                }
            }

            // 다시 넣기는 기간 조건 없이 키로 읽으므로 「같은 조건의 대상 건수」가 없다. 넣은 행 수를 보인다.
            foreach (var t in run.Tables.Values)
            {
                t.TargetRows = t.Loaded;
            }
        }
        else if (!run.Stopped && !faulted && !run.TargetAbandoned)
        {
            using var step = new CancellationTokenSource(LongCleanupTimeout);
            await CountTargetsAsync(run, step.Token);
        }

        NoteDeletedButNotLoaded(run);
        LogEnd(run, faulted);

        if (run.Record is not { } record)
        {
            return;
        }

        try
        {
            using var step = new CancellationTokenSource(CleanupTimeout);
            foreach (var t in run.Tables.Values)
            {
                await record.RecordTableAsync(t.ToResult(), step.Token);
            }

            // 다시 켜지 못한 객체가 있으면 기록을 끝내지 않는다: 다음에 작업을 열 때 「다시 켜기」 목록으로 보인다(설계 9장).
            if (!run.EnableFailed)
            {
                await record.CompleteAsync(run.Outcome(faulted), step.Token);
            }
        }
        catch (Exception ex)
        {
            run.Notices.Add("실행 기록을 마무리하지 못했습니다: " + SafeError(ex.Message));
        }
    }

    private bool IsCleanupFailure(Exception ex) =>
        CopyErrors.IsServer(_errors, ex) || ex is OperationCanceledException or InvalidOperationException or IOException or BulkDeniedException;

    /// <summary>작업 로그 끝줄: 결과·표 수·넣은 행·실패 행·걸린 시간.</summary>
    private static void LogEnd(RunState run, bool faulted)
    {
        var outcome = run.Outcome(faulted);
        var results = run.Tables.Values.Select(t => t.ToResult()).ToList();
        var text = (outcome switch
                   {
                       CopyOutcome.Succeeded => "끝: 성공",
                       CopyOutcome.PartiallyFailed => "끝: 일부 실패",
                       CopyOutcome.Stopped => "끝: 중지",
                       _ => "끝: 실패",
                   })
                   + $" · 테이블 {results.Count.ToString(CultureInfo.InvariantCulture)}개(성공 {results.Count(r => r.Outcome == CopyOutcome.Succeeded).ToString(CultureInfo.InvariantCulture)})"
                   + $" · {run.Tables.Values.Sum(t => t.Loaded).ToString("N0", CultureInfo.InvariantCulture)}행 넣음"
                   + $" · 실패 행 {run.FailedRows.Count.ToString("N0", CultureInfo.InvariantCulture)}"
                   + " · 걸린 시간 " + CopyLogText.Elapsed(run.Clock.Elapsed);
        run.Enter(PhaseFinish);
        run.Log(text, outcome is CopyOutcome.Succeeded ? CopyLogKind.Info : outcome is CopyOutcome.Failed ? CopyLogKind.Error : CopyLogKind.Warning);
    }

    /// <summary>
    /// 정리(DELETE)는 모든 표에 먼저 하므로(설계 8.2, 자식 → 부모), 한 표의 커밋이 다른 표의 DELETE 도 확정한다.
    /// 적재를 끝내지 못한 표의 DELETE 가 확정됐으면 그 사실을 표 결과와 알림에 적는다(다시 실행하면 같은 결과가 된다).
    /// </summary>
    private static void NoteDeletedButNotLoaded(RunState run)
    {
        if (run.Stopped)
        {
            foreach (var t in run.Tables.Values.Where(t => !t.LoadCompleted))
            {
                t.MarkStopped();
            }
        }

        if (!run.DeletesPersisted)
        {
            return;
        }

        // 다시 만들지 못한 표(표가 없음)는 다시 실행해도 사전 점검이 막는다: 휴지통 안내(NoteRecreated)만 한다.
        var tables = run.Tables.Values.Where(t => t.Deleted && !t.LoadCompleted && !(t.Recreate && !t.Created)).ToList();
        foreach (var t in tables)
        {
            if (t.Recreate)
            {
                if (t.Created)
                {
                    t.Degrade("테이블을 지우고 다시 만들었지만 적재를 끝내지 못했습니다(비어 있거나 일부만 들어 있습니다). 다시 실행하거나 휴지통에서 되돌리세요.");
                }
            }
            else
            {
                t.Note("대상 정리(DELETE)는 확정됐지만 적재를 끝내지 못했습니다. 다시 실행하세요.");
            }
        }

        if (tables.Count > 0)
        {
            run.Notices.Add("대상 행을 지운 뒤 적재를 끝내지 못한 테이블이 있습니다(" + string.Join(", ", tables.Select(t => t.Name))
                            + "). 다시 실행하면 같은 결과가 됩니다.");
        }
    }

    /// <summary>다시 켜기(DDL, 묵시 커밋) 전에 대상 트랜잭션을 끝낸다. 끝났으면(커밋 또는 롤백 성공) true.</summary>
    private async Task<bool> EndTargetTransactionAsync(RunState run, bool faulted)
    {
        if (run.TargetAbandoned)
        {
            // 취소에도 돌아오지 않는 대상 명령이 세션을 쥐고 있다(AbandonStuckAsync): 커밋·롤백을 보내도 차례를 못 받는다.
            // 끝내지 못한 것으로 두어 다시 켜기(DDL, 묵시 커밋)도 하지 않는다. 세션을 닫으면 커밋하지 않은 적재는 되돌아간다.
            run.Notices.Add("대상 세션의 명령이 돌아오지 않아 대상 트랜잭션을 끝내지 못했습니다. 복사 창을 닫아 세션을 정리하면 커밋하지 않은 변경은 되돌아갑니다.");
            return false;
        }

        // 전체 중단(원본 준비·끄기 실패, ORA-01555)도 되돌린다: 커밋하면 아직 적재하지 않은 뒤쪽 표의 DELETE 까지 확정된다.
        var rollback = run.Stopped || faulted || run.Aborted
                       || (run.Job.Options.Commit == CommitMode.AtEnd && run.Tables.Values.Any(t => t.Outcome == CopyOutcome.Failed));
        if (!rollback)
        {
            using var commitStep = new CancellationTokenSource(CleanupTimeout);
            var commit = await ControlLoggedAsync(run, run.Target, CopyCommonStatements.Commit(), commitStep.Token, "대상 트랜잭션 끝내기");
            if (commit.Status == ExecutionStatus.Succeeded)
            {
                run.DeletesPersisted = true;
                foreach (var t in run.Tables.Values.Where(t => !t.WasRolledBack && (t.PendingFailed.Count > 0 || t.LoadCompleted)))
                {
                    await ConfirmFailedRowsAsync(run, t);
                }

                return true;
            }

            run.CommitFailed = true;
            run.Notices.Add("대상 커밋을 하지 못해 되돌립니다: " + Describe(commit));
        }

        // 큰 적재를 되돌리는 ROLLBACK 은 오래 걸릴 수 있다.
        using var step = new CancellationTokenSource(LongCleanupTimeout);
        var r = await ControlLoggedAsync(run, run.Target, CopyCommonStatements.Rollback(), step.Token, "확정하지 않은 대상 변경 되돌리기");
        if (r.Status != ExecutionStatus.Succeeded)
        {
            run.Notices.Add("대상 롤백을 하지 못했습니다: " + Describe(r));
            return false;
        }

        run.TargetRolledBack = true;

        // 확정되지 않은 적재는 모두 되돌아갔다: 그 실패 행은 버린다.
        foreach (var t in run.Tables.Values.Where(t => t.PendingFailed.Count > 0 || (!t.Confirmed && t.FailedRows > 0)))
        {
            t.RolledBack();
        }

        // 다시 만든 표는 DROP·CREATE 가 되돌아가지 않는다: 적재까지 끝냈지만 확정하지 못한 표는 이제 비어 있다(중지여도 알린다).
        // 새로 만든 표(「없으면 만들기」)는 옛 표가 없다: 비어 있다고만 알린다(끝낼 때 적재를 끝내지 못한 표는 지운다, PurgeUnloadedNewTablesAsync).
        foreach (var t in run.Tables.Values.Where(t => t.CreatedNew && t.LoadCompleted && !t.Confirmed && run.Job.Options.Commit != CommitMode.AtEnd))
        {
            t.Degrade("적재를 되돌려 이번에 만든 테이블이 비어 있습니다. 다시 실행하세요(테이블이 있으므로 만들지 않고 규칙대로 넣습니다).");
        }

        var emptied = run.Tables.Values.Where(t => t.Created && !t.CreatedNew && t.LoadCompleted && !t.Confirmed).ToList();
        foreach (var t in emptied)
        {
            t.Degrade("적재를 되돌려 다시 만든 테이블이 비어 있습니다(DROP·CREATE 는 되돌릴 수 없습니다). 옛 행은 휴지통의 옛 테이블에 있습니다. 다시 실행하세요.");
        }

        if (emptied.Count > 0)
        {
            run.Notices.Add("다시 만든 테이블(" + string.Join(", ", emptied.Select(t => t.Name))
                            + ")는 적재를 되돌려 비어 있습니다. DROP·CREATE 는 되돌릴 수 없어 옛 행은 휴지통의 옛 테이블에 있습니다.");
        }

        if (run.Job.Options.Commit == CommitMode.AtEnd && !run.Stopped)
        {
            run.Notices.Add(run.Tables.Values.Any(t => t.Created)
                ? "「모두 끝난 뒤 한 번 커밋」이라 실패 때문에 적재를 되돌렸습니다(다시 만든 테이블의 DROP·CREATE 는 되돌리지 못합니다)."
                : "「모두 끝난 뒤 한 번 커밋」이라 실패 때문에 모든 변경을 되돌렸습니다.");
            run.RolledBackAll = true;
        }

        return true;
    }

    /// <summary>
    /// 끈 순서의 반대로 켠다. FK 는 VALIDATE 가 실패하면 NOVALIDATE 로 켜고 부모 없는 행 수를 알린다. 모두 켰으면 true.
    /// 객체마다 따로 토큰을 주고 예외를 가둔다: 하나가 실패해도(시간 초과·기록 실패 포함) 나머지는 켠다.
    /// </summary>
    private async Task<bool> EnableAsync(RunState run)
    {
        var allEnabled = true;
        for (var i = run.Disabled.Count - 1; i >= 0; i--)
        {
            var obj = run.Disabled[i];
            run.Progress(run.DisabledKeys.GetValueOrDefault(obj, obj.Table), 0, null, PhaseEnable); // 끌 때와 같은 규칙 키
            try
            {
                if (obj.Kind == DisabledKind.Trigger)
                {
                    var r = await CleanupStatementAsync(run, _dialect.EnableTrigger(obj.Owner, obj.Name), CleanupTimeout);
                    if (r.Status != ExecutionStatus.Succeeded)
                    {
                        allEnabled = false;
                        run.Notices.Add($"트리거 {obj.Owner}.{obj.Name} 을(를) 다시 켜지 못했습니다. 직접 켜세요: {Describe(r)}");
                    }

                    continue;
                }

                // VALIDATE 는 자식 표 전체를 읽으므로 오래 걸릴 수 있다.
                var validated = await CleanupStatementAsync(run, _dialect.EnableConstraint(obj, validate: true), LongCleanupTimeout);
                if (validated.Status == ExecutionStatus.Succeeded)
                {
                    continue;
                }

                var loose = await CleanupStatementAsync(run, _dialect.EnableConstraint(obj, validate: false), CleanupTimeout);
                if (loose.Status != ExecutionStatus.Succeeded)
                {
                    allEnabled = false;
                    run.Notices.Add($"FK {obj.Owner}.{obj.Name} 을(를) 다시 켜지 못했습니다. 직접 켜세요: {Describe(loose)}");
                    continue;
                }

                using var count = new CancellationTokenSource(LongCleanupTimeout);
                var orphans = await OrphanCountAsync(run, obj, count.Token);
                run.Notices.Add($"FK {obj.Owner}.{obj.Name}: 부모 없는 행 {orphans} 때문에 ENABLE NOVALIDATE 로 켰습니다.");
            }
            catch (Exception ex) when (IsCleanupFailure(ex))
            {
                allEnabled = false;
                run.Notices.Add($"{obj.Owner}.{obj.Name} 을(를) 다시 켜지 못했습니다. 직접 켜세요: {SafeError(ex.Message)}");
            }
        }

        return allEnabled;
    }

    /// <summary>정리 문장 하나를 자기 토큰으로 실행하고, 성공하면 실행 기록에 남긴다. 기록 실패는 문장 결과를 바꾸지 않는다.</summary>
    private async Task<ExecutionResult> CleanupStatementAsync(RunState run, SqlStatement stmt, TimeSpan timeout)
    {
        using var step = new CancellationTokenSource(timeout);
        run.LogStatement(run.Target, stmt.Text);
        var r = await _execution.ExecuteAsync(run.Target, stmt, NoBinds, run.HistoryOptions, step.Token);
        LogResult(run, r);
        if (r.Status == ExecutionStatus.Succeeded && run.Record is { } record)
        {
            try
            {
                await record.RecordStatementAsync(stmt.Text, step.Token);
            }
            catch (Exception ex)
            {
                run.Notices.Add("실행 기록에 문장을 남기지 못했습니다: " + SafeError(ex.Message));
            }
        }

        return r;
    }

    private async Task<string> OrphanCountAsync(RunState run, DisabledObject fk, CancellationToken token)
    {
        try
        {
            var cols = await RowsAsync(run, run.Target, CopyCommonStatements.Query(CopyCatalogQueries.Sql(_catalog, run.Target.Server, "copy.fk_columns")),
                new Dictionary<string, BindValue>
                {
                    ["OWNER"] = new(BindDataType.Varchar2, fk.Owner),
                    ["NAME"] = new(BindDataType.Varchar2, fk.Name),
                }, token);
            if (cols.Count == 0)
            {
                return "수 모름";
            }

            var child = cols.Select(c => SqlIdentifier.QuoteAlways((string)c[0]!)).ToList();
            var parent = cols.Select(c => SqlIdentifier.QuoteAlways((string)c[1]!)).ToList();
            var parentTable = CopyCommonStatements.Qualified((string)cols[0][2]!, (string)cols[0][3]!);
            var notNull = string.Join(" AND ", child.Select(c => $"c.{c} IS NOT NULL"));
            var join = string.Join(" AND ", child.Select((c, i) => $"p.{parent[i]} = c.{c}"));
            var sql = $"SELECT COUNT(*) FROM {CopyCommonStatements.Qualified(fk.Owner, fk.Table)} c WHERE {notNull}"
                      + $" AND NOT EXISTS (SELECT 1 FROM {parentTable} p WHERE {join})";
            var n = await ScalarAsync(run, run.Target, CopyCommonStatements.Query(sql), NoBinds, token);
            return _codec.NumberText(n) is { } text ? text + "건" : "수 모름";
        }
        catch (Exception ex) when (CopyErrors.IsServer(_errors, ex) || ex is BulkDeniedException)
        {
            return "수 모름";
        }
    }

    /// <summary>원본 건수 = 적재할 때 읽은 행 수, 대상 건수 = 같은 조건으로 대상에서 COUNT(*).</summary>
    private async Task CountTargetsAsync(RunState run, CancellationToken token)
    {
        foreach (var table in run.Tables.Values.Where(t => !t.Purged && !(t.CreateRequested && !t.Created)))
        {
            run.Progress(table.Name, table.SourceRows, null, PhaseCount);
            var countStarted = Stopwatch.GetTimestamp();
            try
            {
                var count = run.Builder.TargetCount(table.Name);
                run.LogStatement(run.Target, count.Sql, count.Binds);
                var n = await ScalarAsync(run, run.Target, CopyCommonStatements.Query(count), count.Binds, token);
                table.TargetRows = _codec.NumberText(n) is not null ? _codec.ToInt64Exact(n) : -1;
                run.Log($"→ 원본 {table.SourceRows.ToString("N0", CultureInfo.InvariantCulture)}건 · 대상 {(table.TargetRows < 0 ? "모름" : table.TargetRows.ToString("N0", CultureInfo.InvariantCulture))}건"
                        + (table.TargetRows == table.SourceRows ? " · 같음" : " · 다름"),
                        table.TargetRows == table.SourceRows ? CopyLogKind.Info : CopyLogKind.Warning);
            }
            catch (Exception ex) when (CopyErrors.IsServer(_errors, ex) || ex is BulkDeniedException or InvalidOperationException)
            {
                table.TargetRows = -1;
                table.Note("대상 건수를 세지 못했습니다: " + SafeError(ex.Message));
            }
            finally
            {
                table.Times.AddOutside(countStarted); // 결과의 테이블 전체 경과에 든다(원격 #155)
            }
        }
    }
}
