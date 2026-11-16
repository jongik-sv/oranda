using System.Diagnostics;
using System.Globalization;
using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy.CrossDbms;

namespace Oranda.Core.Transfer.Copy;

public partial class CopyRunner
{
    // ---- 3. 적재 ----

    private async Task LoadAllAsync(RunState run, CopyRunControl control, CancellationToken ct)
    {
        foreach (var plan in run.Report.Order)
        {
            if (run.Aborted)
            {
                return;
            }

            var table = run.Table(plan);
            if (table.Outcome == CopyOutcome.Failed)
            {
                continue;
            }

            await LoadTableAsync(run, plan, table, control, ct);
        }
    }

    private async Task LoadTableAsync(RunState run, CopyTablePlan plan, TableState table, CopyRunControl control, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var shape = ShapeFor(run, plan);
            var dml = CopyCommonStatements.ArrayDml(DmlFor(run.Builder, plan, table.Name, shape, freshTable: plan.CreateMissing), shape.CopyColumns.Count);
            var estimated = run.Report.EstimatedRows.TryGetValue(table.Name, out var e) ? e : (long?)null;
            run.Progress(table.Name, 0, estimated, PhaseLoad);
            run.Log("적재 시작" + (estimated is { } est ? $" · 예상 {est.ToString("N0", CultureInfo.InvariantCulture)}건" : string.Empty));
            LogDroppedColumns(run, table, shape);
            if (UsesExternalTool(run, plan))
            {
                // 외부 적재 도구(조정 결정 88, CopyRunner.External.cs). 못 넣어 테이블을 정리했으면 아래 기본 방식으로 다시 넣는다.
                if (await LoadExternalAsync(run, plan, table, shape, estimated, ct) != ExternalEnd.Fallback)
                {
                    return;
                }

                run.Log("기본 방식(배열 INSERT)으로 다시 넣습니다", CopyLogKind.Warning);
            }

            // 실패 행 상한을 넘거나 원본 읽기가 실패하면 이 표의 적재분만 되돌린다(앞 표의 정리·적재는 남긴다).
            var savepoint = await ControlLoggedAsync(run, run.Target, _dialect.Savepoint(CopyCommonStatements.TableSavepoint), ct, "이 테이블을 되돌릴 자리");
            if (!RunState.Accept(savepoint, table, "적재를 시작하지 못했습니다", ct))
            {
                return;
            }

            table.SavepointSet = true;
            if (ReadsPerTable(run) && !await BeginTableReadAsync(run, table, ct))
            {
                return;
            }

            var select = run.Builder.SourceSelect(table.Name, shape.CopyColumns, run.ReadPoint);
            LogLoadStatements(run, plan, shape, select, dml);
            // 읽기·쓰기(겹치기 또는 옛 동작 번갈아)·멈춤 감시·대기 한도는 PumpTableAsync(CopyRunner.Pipeline.cs).
            table.Pumped = true;
            var pumpStarted = Stopwatch.GetTimestamp();
            PumpResult pumped;
            try
            {
                pumped = await PumpTableAsync(run, new TableLoad(plan, table, shape, dml, select, estimated, control), ct);
            }
            finally
            {
                table.Times.AddLoad(pumpStarted); // 적재 구간(행/초의 기준, 원격 #155)
            }

            switch (pumped.End)
            {
                case PumpEnd.ReadFailed:
                    return; // 원본 읽기 실패: 이미 되돌리고 실패로 뒀다
                case PumpEnd.WaitLimit:
                    await StopAtWaitLimitAsync(run, table, pumped.Side!, ct);
                    return;
                case PumpEnd.Stuck:
                    await AbandonStuckAsync(run, table, pumped.Side!, ct, pumped.SourceHeld);
                    return;
            }

            if (table.Outcome == CopyOutcome.Failed)
            {
                await RollbackTableAsync(run, table, ct);
                return;
            }

            if (run.Job.Options.Commit == CommitMode.PerTable)
            {
                // 커밋도 멈춤 표시·대기 한도 아래 보낸다(리뷰 PF1-4). 한도를 넘으면 취소한 뒤 테이블을 되돌려 본다(되돌리면 커밋이 되지 않은 것이다).
                var watched = await WatchTargetCommandAsync(run, table, estimated, CopyWaitStep.Commit,
                    token => ControlLoggedAsync(run, run.Target, CopyCommonStatements.Commit(), token, "테이블마다 커밋"), ct);
                if (watched.End == PumpEnd.Stuck)
                {
                    await AbandonStuckAsync(run, table, watched.Side, ct);
                    return;
                }

                if (watched.End == PumpEnd.WaitLimit
                    || (watched.Result is { Status: not ExecutionStatus.Succeeded } && watched.Side.LimitHit && !ct.IsCancellationRequested))
                {
                    await StopCommitAtWaitLimitAsync(run, table, watched.Side, ct);
                    return;
                }

                var commit = watched.Result!;
                if (RunState.Accept(commit, table, "커밋하지 못했습니다", ct))
                {
                    run.DeletesPersisted = true;
                    await ConfirmFailedRowsAsync(run, table);
                }
            }

            table.LoadCompleted = table.Outcome != CopyOutcome.Failed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // 규칙·열 정보가 맞지 않음(마스킹 고정값 변환 실패, 키로 쓸 수 없는 형식 등). 이 표만 실패로 두고 다음 표로 간다.
            await RollbackTableAsync(run, table, ct);
            table.Fail(SafeError(ex.Message));
        }
        catch (BulkDeniedException ex)
        {
            await RollbackTableAsync(run, table, ct);
            table.Fail("정책이 원본 읽기를 거부했습니다: " + SafeError(ex.Message));
        }
        finally
        {
            table.Elapsed += sw.Elapsed;
            var end = LogTableEnd(run, table, sw.Elapsed);

            // 실행 기록(copy-runs)에도 바로 한 줄: 표 결과 줄은 마무리 때 한꺼번에 쓰므로 실행 중에는 이 줄로 어디까지 왔는지 안다(결정 70).
            await RecordEventAsync(run, "tableEnd", table.Name, end);

            // 적재를 마친 테이블만(실패·되돌린 테이블은 결과가 정한다): 막대는 예상 건수가 아니라 실제로 읽은 수로 가득 찬다.
            if (table.LoadCompleted)
            {
                run.ReportLoadEnd(table.Name, table.SourceRows, table.Loaded);
            }
        }
    }

    /// <summary>
    /// 표의 열 배치. 원본·대상 DBMS 가 같으면 예전 그대로(열 = 점검 열, 바인드 = 원본 형식, 값 변환 없음).
    /// 다르면 교차 복사 배치(<see cref="CrossTypeMap"/>: 옮길 형식이 없는 열 빼기, 바인드 = 원본 형식을 대상 DBMS 로 옮긴 형식, 값 = 이식 값을 거쳐 대상 코덱).
    /// </summary>
    private RowShape ShapeFor(RunState run, CopyTablePlan plan) =>
        _sourceDbms == _targetDbms
            ? RowShape.Create(_sourceCodec, run.Job, plan)
            : RowShape.Create(_sourceCodec, run.Job, plan, new CrossSides(_sourceDbms, _targetDbms, _codec));

    /// <summary>교차 복사에서 옮길 형식이 없어 뺀 열을 알림(작업 로그 경고 + 결과 알림) 한 줄로 남긴다. 같은 DBMS 면 아무것도 하지 않는다.</summary>
    private void LogDroppedColumns(RunState run, TableState table, RowShape shape)
    {
        if (shape.Dropped.Count == 0)
        {
            return;
        }

        var columns = string.Join(", ", shape.Dropped.Select(d => $"{d.Name}({d.DataType} — {DropReasonText(d.Reason)})"));
        run.Notices.Add($"{table.Name}: {_targetDbms} 로 옮길 형식이 없는 열을 빼고 복사합니다: {columns}.");
    }

    private static string DropReasonText(CrossDropReason reason) => reason switch
    {
        CrossDropReason.LongSource => "LONG·LONG RAW 는 대량 읽기를 지원하지 않음",
        CrossDropReason.RowId => "ROWID 는 다른 DB 에서 뜻이 없음",
        CrossDropReason.LocalTimeZone => "TIMESTAMP WITH LOCAL TIME ZONE 은 옮기지 않음",
        _ => "대량 읽기를 지원하지 않는 형식",
    };

    /// <summary>원본 SELECT(바인드 요약)·대상 INSERT/MERGE(표마다 한 번)·마스킹 열(이름과 방식만, 값은 남기지 않는다).</summary>
    private static void LogLoadStatements(RunState run, CopyTablePlan plan, RowShape shape, SqlWithBinds select, SqlStatement dml)
    {
        run.LogStatement(run.Source, select.Sql, select.Binds);
        run.Log(CopyLogText.Statement(run.SideOf(run.Target), dml.Text)
                + $"  [바인드 배열 :C1…:C{shape.CopyColumns.Count.ToString(CultureInfo.InvariantCulture)}, 배치 {run.Job.Options.BatchSize.ToString("N0", CultureInfo.InvariantCulture)}행씩]",
                CopyLogKind.Statement);
        var masks = plan.Rule.Masks.Where(m => shape.CopyColumns.Contains(m.Column, StringComparer.Ordinal)).ToList();
        if (masks.Count > 0)
        {
            run.Log("마스킹(앱에서 바꿔 보냄, 값은 남기지 않음): " + string.Join(", ", masks.Select(m => $"{m.Column}({MaskText(m)})")));
        }
    }

    private static string MaskText(MaskRule m) => m.Kind switch
    {
        MaskKind.Fixed => "고정값",
        MaskKind.KeepFirst => $"앞 {m.Count.ToString(CultureInfo.InvariantCulture)}자 남김",
        MaskKind.KeepLast => $"뒤 {m.Count.ToString(CultureInfo.InvariantCulture)}자 남김",
        MaskKind.Hash => "해시",
        _ => "NULL",
    };

    private static string LogTableEnd(RunState run, TableState table, TimeSpan elapsed)
    {
        var text = $"적재 끝 · 원본 {table.SourceRows.ToString("N0", CultureInfo.InvariantCulture)}건 읽음 · {table.Loaded.ToString("N0", CultureInfo.InvariantCulture)}행 넣음"
                   + (table.FailedRows > 0 ? $" · 실패 {table.FailedRows.ToString("N0", CultureInfo.InvariantCulture)}행" : string.Empty)
                   + (table.WasRolledBack ? " · 되돌림" : string.Empty)
                   + " · " + CopyLogText.Elapsed(elapsed)
                   // 시간 나눔(결정 74): 겹치기 켬·끔 전후 비교용. 적재 흐름을 지난 표만.
                   + (table.Pumped ? " · " + CopyLogText.Times(table.Times.ToTimes(table.Loaded, elapsed)) : string.Empty);
        run.Log(text, table.Outcome == CopyOutcome.Failed ? CopyLogKind.Error : CopyLogKind.Info, table.Name);
        return text;
    }

    /// <summary>
    /// 배치 하나를 보낸다. 실패하면 배치 세이브포인트로 되돌린 뒤 한 행씩 다시 넣어 실패 행을 찾는다(설계 8.2).
    /// 표를 더 진행하면 true, 그만두면(실패 행 상한 초과·거부) false.
    /// </summary>
    /// <param name="writer">대상 쓰기 감시(멈춤 표시·대기 한도): 일시 정지를 기다리는 동안은 쉬는 중, 배치를 보내는 동안은 응답을 기다리는 중으로 둔다.</param>
    private async Task<bool> FlushAsync(RunState run, TableState table, RowShape shape, SqlStatement dml,
                                        List<object?[]> batch, CopyRunControl control, SideWatch writer, CancellationToken ct)
    {
        if (StopForOtherLaneAbort(run, table))
        {
            return false; // 동시 복사(PF2): 다른 레인이 실행 전체를 멈췄다 — 이 배치를 보내지 않고 표를 멈춘다(늦어도 배치 하나)
        }

        await control.WaitIfPausedAsync(ct);
        writer.Begin(table.Batches + 1);
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await SendBatchAsync(run, table, shape, dml, batch, writer, ct);
        }
        finally
        {
            writer.Idle();

            // 쓰기 시간 = 배치를 보낸 시간(세이브포인트·배열 DML·한 행씩 다시 넣기·배치 커밋)에서 값 변환 시간을 뺀 것(결정 74, 변환은 SendBatchAsync 가 따로 더한다).
            table.Times.AddWrite(started);
        }
    }

    /// <summary><see cref="FlushAsync"/> 의 본문(일시 정지를 지난 뒤). 한 행씩 다시 넣을 때는 행마다 감시 시각을 새로 잡는다(움직이는 중이면 멈춤이 아니다).</summary>
    private async Task<bool> SendBatchAsync(RunState run, TableState table, RowShape shape, SqlStatement dml,
                                            List<object?[]> batch, SideWatch writer, CancellationToken ct)
    {
        var perBatch = run.Job.Options.Commit == CommitMode.PerBatch;
        var pendingAtBatchStart = table.PendingFailed.Count;
        table.PendingAtBatchStart = pendingAtBatchStart; // 대기 한도로 이 배치를 되돌릴 때 쓴다(StopAtWaitLimitAsync)
        var savepoint = await ControlAsync(run.Target, _dialect.Savepoint(CopyCommonStatements.BatchSavepoint), ct);
        if (!RunState.Accept(savepoint, table, "배치를 시작하지 못했습니다", ct))
        {
            return false;
        }

        table.Batches++;

        // 같은 DBMS: 보낼 행 = 읽은 행 그대로. 교차: 값을 대상 DBMS 값으로 바꾼 새 행, 못 바꾼 행은 여기서 실패 행이 된다(키는 원본 값, D5).
        var rows = batch;
        var sources = batch;
        var conversionFailed = 0;
        if (shape.Converts)
        {
            var convertStarted = Stopwatch.GetTimestamp();
            long failedRowTime = 0; // 실패 행 처리(상한 초과면 ROLLBACK TO 왕복) = 대상 DB 시간: 변환에서 빼고 쓰기에 남긴다
            var converted = new List<object?[]>(batch.Count);
            var kept = new List<object?[]>(batch.Count);
            try
            {
                foreach (var source in batch)
                {
                    if (shape.TryToTarget(source, out var values, out var error))
                    {
                        converted.Add(values);
                        kept.Add(source);
                        continue;
                    }

                    var failedAt = Stopwatch.GetTimestamp();
                    var over = await AddFailedRowAsync(run, table, new FailedRow(table.Name, shape.KeyOf(source), error!), pendingAtBatchStart, ct);
                    failedRowTime += Stopwatch.GetTimestamp() - failedAt;
                    if (over)
                    {
                        return false;
                    }
                }
            }
            finally
            {
                // 상한 초과로 일찍 끝나도 변환 시간을 남긴다(쓰기 시간에서 빠진다, 결정 74·리뷰 PF1-5).
                table.Times.AddBatchConvert(convertStarted, failedRowTime);
            }

            rows = converted;
            sources = kept;
            conversionFailed = batch.Count - rows.Count;
            if (rows.Count == 0)
            {
                return await EndBatchAsync(run, table, 0, conversionFailed, perBatch, ct);
            }
        }

        var r = await _bulk.ExecuteArrayAsync(run.Target, dml, shape.Columns, rows, run.BulkOptions, ct);
        if (r.Status == ExecutionStatus.Succeeded)
        {
            table.Loaded += rows.Count;
        }
        else if (r.Status == ExecutionStatus.Failed)
        {
            run.Log($"배치 {table.Batches.ToString(CultureInfo.InvariantCulture)} 실패({Describe(r)}) → 배치 시작점으로 되돌리고 한 행씩 다시 넣습니다", CopyLogKind.Warning);
            // 배열 DML 이 실패하면 실패한 행 앞의 행이 트랜잭션에 남아 있을 수 있다.
            var back = await ControlLoggedAsync(run, run.Target, _dialect.RollbackToSavepoint(CopyCommonStatements.BatchSavepoint), ct);
            if (!RunState.Accept(back, table, "실패한 배치를 되돌리지 못했습니다", ct))
            {
                return false;
            }

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var one = await _bulk.ExecuteArrayAsync(run.Target, dml, shape.Columns, [row], run.BulkOptions, ct);
                writer.Touch();
                if (one.Status == ExecutionStatus.Succeeded)
                {
                    table.Loaded++;
                    continue;
                }

                if (one.Status != ExecutionStatus.Failed)
                {
                    RunState.Accept(one, table, "행을 넣지 못했습니다", ct);
                    return false;
                }

                // 실패 행 키는 원본 값으로 남긴다(교차 복사면 sources[i] 가 바꾸기 전 원본 행, 같은 DBMS 면 row 그대로).
                if (await AddFailedRowAsync(run, table, new FailedRow(table.Name, shape.KeyOf(sources[i]), ErrorText(one.Error)), pendingAtBatchStart, ct))
                {
                    return false;
                }
            }
        }
        else
        {
            RunState.Accept(r, table, "배치를 넣지 못했습니다", ct);
            return false;
        }

        return await EndBatchAsync(run, table, rows.Count, conversionFailed, perBatch, ct);
    }

    /// <summary>
    /// 실패 행 하나를 대기 목록에 넣고 작업 로그에 남긴다. 상한을 넘으면 이 배치를 되돌리고(이 배치의 대기 행도 뺀다) 표를 실패로 둔 뒤 true(그만둔다).
    /// </summary>
    private async Task<bool> AddFailedRowAsync(RunState run, TableState table, FailedRow failed, int pendingAtBatchStart, CancellationToken ct)
    {
        // 실패 행은 그 표의 적재가 확정(커밋)될 때 결과·실행 기록에 넣는다. 되돌린 적재의 실패 행으로
        // 「실패 행만 다시 넣기」를 하면 나머지 행이 빠진 표가 성공으로 보이기 때문이다.
        table.PendingFailed.Add(failed);
        table.FailedRows++;
        run.Log($"실패 행 {table.FailedRows.ToString("N0", CultureInfo.InvariantCulture)}: {failed.Error}", CopyLogKind.Error);
        if (table.FailedRows <= run.Job.Options.MaxFailedRows)
        {
            return false;
        }

        // 이 배치에서 한 행씩 넣은 행도 되돌린다(「배치마다 커밋」이면 표 세이브포인트가 없으므로 여기서만 되돌릴 수 있다).
        var undo = await ControlLoggedAsync(run, run.Target, _dialect.RollbackToSavepoint(CopyCommonStatements.BatchSavepoint), ct, "상한을 넘은 배치 되돌리기");
        if (undo.Status == ExecutionStatus.Succeeded)
        {
            // 이 배치의 실패 행만 뺀다. 앞 배치의 대기 행은 표 되돌리기가 성공할 때 함께 버린다(RolledBack).
            table.PendingFailed.RemoveRange(pendingAtBatchStart, table.PendingFailed.Count - pendingAtBatchStart);
        }
        else
        {
            run.Notices.Add($"{table.Name}: 상한을 넘은 배치를 되돌리지 못했습니다: " + Describe(undo));
        }

        table.Fail($"실패 행이 상한({run.Job.Options.MaxFailedRows.ToString(CultureInfo.InvariantCulture)})을 넘어 이 테이블을 멈췄습니다.");
        return true;
    }

    /// <summary>배치 끝: 작업 로그 한 줄, 「배치마다 커밋」이면 커밋하고 실패 행을 확정한다. 표를 더 진행하면 true.</summary>
    private async Task<bool> EndBatchAsync(RunState run, TableState table, int sent, int conversionFailed, bool perBatch, CancellationToken ct)
    {
        // sent = 실제로 보낸 행 수. 교차 복사에서 값 변환에 실패해 보내지 않은 행이 있으면 그 수를 덧붙인다(같은 DBMS 는 conversionFailed 가 0 이라 글이 그대로).
        var batchText = $"배치 {table.Batches.ToString(CultureInfo.InvariantCulture)} · {sent.ToString("N0", CultureInfo.InvariantCulture)}행 보냄"
                        + (conversionFailed > 0 ? $"(값 변환 실패 {conversionFailed.ToString("N0", CultureInfo.InvariantCulture)}행 제외)" : string.Empty)
                        + $" · 누적 {table.Loaded.ToString("N0", CultureInfo.InvariantCulture)}행";
        if (perBatch)
        {
            var commit = await ControlAsync(run.Target, CopyCommonStatements.Commit(), ct);
            if (!RunState.Accept(commit, table, "커밋하지 못했습니다", ct))
            {
                return false;
            }

            run.Log(batchText + " · COMMIT");

            run.DeletesPersisted = true;
            await ConfirmFailedRowsAsync(run, table);
        }
        else
        {
            run.Log(batchText);
        }

        return true;
    }

    /// <summary>
    /// 표 시작 세이브포인트로 되돌린다. 세이브포인트를 세우기 전이면(열 배치·문장을 만들다 실패) 되돌릴 것이 없다.
    /// 「배치마다 커밋」은 커밋이 세이브포인트를 지우므로 되돌릴 것이 없다.
    /// </summary>
    private async Task RollbackTableAsync(RunState run, TableState table, CancellationToken ct)
    {
        // 「배치마다 커밋」 적재는 커밋이 세이브포인트를 지운다. 다시 넣기는 커밋 단위와 상관없이 표마다 커밋하므로 되돌릴 수 있다.
        if (!table.SavepointSet || (run.Job.Options.Commit == CommitMode.PerBatch && !run.IsRetry))
        {
            return;
        }

        var token = ct.IsCancellationRequested ? CancellationToken.None : ct;
        var r = await ControlLoggedAsync(run, run.Target, _dialect.RollbackToSavepoint(CopyCommonStatements.TableSavepoint), token, "이 테이블의 적재분 되돌리기");
        if (r.Status != ExecutionStatus.Succeeded)
        {
            run.Notices.Add("실패한 테이블의 적재분을 되돌리지 못했습니다: " + Describe(r));
            return;
        }

        table.RolledBack();
    }

    /// <summary>
    /// 확정(커밋)된 표의 실패 행을 결과와 실행 기록에 넣는다. 「실패 행만 다시 넣기」는 이 목록만 쓴다.
    /// 기록 실패는 예외로 올리지 않는다(행은 이미 커밋됐고, 결과에는 남는다).
    /// </summary>
    private static async Task ConfirmFailedRowsAsync(RunState run, TableState table)
    {
        // 커밋은 이미 끝났다: 결과에 먼저 옮기고 확정 표시를 한 뒤 기록한다. 기록은 중지 토큰과 상관없는 새 토큰으로,
        // 실패(시간 초과 포함)는 여기서 가둔다(호출한 쪽의 마무리·다시 켜기를 끊지 않게).
        var rows = table.PendingFailed.ToList();
        table.PendingFailed.Clear();
        table.Confirmed = true;
        lock (run.FailedRows)
        {
            run.FailedRows.AddRange(rows); // 동시 복사(PF2)면 레인 여럿이 더한다
        }
        if (run.Record is not { } record || rows.Count == 0)
        {
            return;
        }

        try
        {
            using var step = new CancellationTokenSource(CleanupTimeout);
            foreach (var row in rows)
            {
                await record.RecordFailedRowAsync(row, step.Token);
            }
        }
        catch (Exception ex)
        {
            // 기록 호출만 감싼다: 어떤 예외든(권한·디스크 등) 마무리·다시 켜기를 끊지 않게 모두 가둔다(3차 리뷰 2).
            run.Notices.Add("실행 기록에 실패 행을 남기지 못했습니다: " + SafeError(ex.Message));
        }
    }

    /// <summary>
    /// 표를 읽기 전마다 원본 읽기 전용 트랜잭션을 새로 여는가(<see cref="BeginTableReadAsync"/>): 「같은 시점 기준 읽기」를 껐거나,
    /// 같은 시점은 원본 SCN(<see cref="RunState.ReadPoint"/>)이 맞추는 동시 복사(PF2b — 레인 원본 세션에도 DB 수준 READ ONLY 를 건다)일 때.
    /// </summary>
    private static bool ReadsPerTable(RunState run) => !run.Job.ConsistentRead || run.ReadPoint is not null;

    /// <summary>
    /// 「같은 시점 기준 읽기」를 끈 경우: 표를 읽기 전마다 원본 트랜잭션을 닫고 읽기 전용 트랜잭션을 새로 연다
    /// (표마다 따로 읽되, 원본에는 언제나 DB 수준 READ ONLY 가 걸려 있게 한다). 같은 시점 읽기 + 동시 복사(PF2b)도 같다 — 시점은 SELECT 의 AS OF 절이 정한다.
    /// </summary>
    private async Task<bool> BeginTableReadAsync(RunState run, TableState table, CancellationToken ct)
    {
        var rollback = await ControlLoggedAsync(run, run.Source, CopyCommonStatements.Rollback(), ct);
        if (!RunState.Accept(rollback, table, "원본 트랜잭션을 정리하지 못했습니다", ct))
        {
            return false;
        }

        var set = await ControlLoggedAsync(run, run.Source, _sourceDialect.SetTransactionReadOnly(), ct,
            run.ReadPoint is { } point ? $"읽기 전용(시점은 원본 {point.Text})" : "이 테이블을 읽을 시점");
        return RunState.Accept(set, table, "원본에서 읽기 전용 트랜잭션을 시작하지 못했습니다", ct);
    }

    private string DmlFor(CopySqlBuilder builder, CopyTablePlan plan, string key, RowShape shape, bool freshTable = false)
    {
        // 사전 점검의 필요 권한(UPDATE)과 같은 판정을 쓴다. 이번에 새로 만든 빈 표(「없으면 만들기」)는 MERGE 가 필요 없어 INSERT 로 넣는다(AppendOnly 와 같다).
        var merge = !freshTable && PrecheckRules.UsesMerge(plan.Rule, plan.KeyColumns);
        if (!merge && !freshTable && plan.Rule.Action == TargetAction.MergeByPk)
        {
            throw new InvalidOperationException($"{key}: PK 기준 MERGE 에 쓸 키 열이 없습니다.");
        }

        // 값 자리 글은 방언이 정한다(바인드 이름·바인드 형식 = 행 배치의 열). Oracle 은 :Cn 그대로라 글이 바뀌지 않는다.
        // 대상에 보내는 DML 이라 대상 방언이다. 형식은 같은 DBMS 면 원본 열 형식, 교차면 원본 형식을 대상 DBMS 로 옮긴 형식(D3)이다.
        // 열은 행 배치의 복사 열이다(교차 복사에서 옮길 형식이 없는 열은 빠졌다).
        // 교차는 교차 쓰기 값 자리(ICopyDialect.CrossBindPlaceholder): Tibero 는 TZ 를 같은 DBMS 값 자리로는 받지 않는다(win144 #137).
        string ValueAt(int i) => _sourceDbms != _targetDbms
            ? _dialect.CrossBindPlaceholder(shape.Columns[i].BindName, shape.Columns[i].ServerType)
            : _dialect.BindPlaceholder(shape.Columns[i].BindName, shape.Columns[i].ServerType);
        return merge ? builder.Merge(key, shape.CopyColumns, plan.KeyColumns, ValueAt) : builder.Insert(key, shape.CopyColumns, ValueAt);
    }
}
