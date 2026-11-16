using System.Diagnostics;
using System.Globalization;
using Oranda.Core.Execution;

namespace Oranda.Core.Transfer.Copy;

public partial class CopyRunner
{
    // ---- 「실패 행만 다시 넣기」 ----

    private async Task RetryTableAsync(RunState run, string tableName, IReadOnlyList<FailedRow> rows, CancellationToken ct)
    {
        var plan = run.Report.Order.FirstOrDefault(p => string.Equals(run.Key(p), tableName, StringComparison.Ordinal));
        if (plan is null)
        {
            run.Notices.Add($"{tableName}: 사전 점검 결과에 없는 표라 다시 넣지 않았습니다.");
            return;
        }

        var table = run.Table(plan);
        var sw = Stopwatch.StartNew();
        try
        {
            if (plan.KeyColumns.Count == 0)
            {
                table.Fail("키 열이 없어 실패 행을 다시 읽을 수 없습니다.");
                return;
            }

            var shape = ShapeFor(run, plan);
            var dml = CopyCommonStatements.ArrayDml(DmlFor(run.Builder, plan, tableName, shape), shape.CopyColumns.Count);
            var query = CopyCommonStatements.Query(run.Builder.SourceSelectByKeys(tableName, shape.CopyColumns, plan.KeyColumns));
            run.Progress(tableName, 0, rows.Count, PhaseRetry);
            LogDroppedColumns(run, table, shape);
            run.Log(CopyLogText.Statement(run.SideOf(run.Source), query.Text)
                    + $"  [바인드 키 :K1…:K{plan.KeyColumns.Count.ToString(CultureInfo.InvariantCulture)}, {rows.Count.ToString("N0", CultureInfo.InvariantCulture)}행을 한 행씩]", CopyLogKind.Statement);
            run.Log(CopyLogText.Statement(run.SideOf(run.Target), dml.Text), CopyLogKind.Statement);
            var savepoint = await ControlLoggedAsync(run, run.Target, _dialect.Savepoint(CopyCommonStatements.TableSavepoint), ct, "이 테이블을 되돌릴 자리");
            if (!RunState.Accept(savepoint, table, "다시 넣기를 시작하지 못했습니다", ct))
            {
                return;
            }

            table.SavepointSet = true;
            foreach (var failedRow in rows)
            {
                run.Progress(tableName, table.SourceRows, rows.Count, PhaseRetry);
                var binds = shape.KeyBinds(failedRow);
                object?[]? source = null;
                await foreach (var row in _bulk.StreamRowsAsync(run.Source, query, binds, SourceReadOptions(run), ct))
                {
                    source = shape.Apply(row);
                }

                table.SourceRows++;
                if (source is null)
                {
                    // 목록에서 빼지 않는다: 사용자가 그 행이 왜 안 들어갔는지 결과에서 봐야 한다.
                    table.PendingFailed.Add(failedRow with { Error = "원본에 이 키의 행이 없습니다(그 사이 지워졌거나 키 값이 바뀌었습니다)." });
                    table.FailedRows++;
                    continue;
                }

                // 교차 복사: 적재와 같은 값 변환. 못 바꾸면 그 행만 다시 실패 행으로 남긴다(D5).
                var values = source;
                if (shape.Converts && !shape.TryToTarget(source, out values, out var conversionError))
                {
                    table.PendingFailed.Add(failedRow with { Error = conversionError! });
                    table.FailedRows++;
                    continue;
                }

                var one = await _bulk.ExecuteArrayAsync(run.Target, dml, shape.Columns, [values], run.BulkOptions, ct);
                if (one.Status == ExecutionStatus.Succeeded)
                {
                    table.Loaded++;
                    continue;
                }

                if (one.Status != ExecutionStatus.Failed)
                {
                    RunState.Accept(one, table, "행을 넣지 못했습니다", ct);
                    await RollbackTableAsync(run, table, ct);
                    return;
                }

                table.PendingFailed.Add(failedRow with { Error = ErrorText(one.Error) });
                table.FailedRows++;
            }

            // 다시 넣기는 표마다 커밋한다. 커밋하지 못한 표의 입력 행은 끝낼 때 그대로 돌려준다(FinishAsync).
            var commit = await ControlLoggedAsync(run, run.Target, CopyCommonStatements.Commit(), ct, "테이블마다 커밋");
            // 연결 끊김, 또는 보내는 도중 중지로 취소되면 서버에서 확정됐는지 알 수 없다.
            table.CommitUnknown = (commit.Status == ExecutionStatus.Failed && commit.Error is { Kind: DbErrorKind.Disconnected })
                                  || (commit.Status == ExecutionStatus.Cancelled && ct.IsCancellationRequested);
            if (RunState.Accept(commit, table, "커밋하지 못했습니다", ct))
            {
                await ConfirmFailedRowsAsync(run, table);
                table.LoadCompleted = true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or FormatException)
        {
            await RollbackTableAsync(run, table, ct);
            table.Fail(SafeError(ex.Message));
        }
        catch (BulkDeniedException ex)
        {
            await RollbackTableAsync(run, table, ct);
            table.Fail("정책이 원본 읽기를 거부했습니다: " + SafeError(ex.Message));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && CopyErrors.IsServer(_errors, ex))
        {
            await RollbackTableAsync(run, table, ct);
            table.Fail("원본을 다시 읽지 못했습니다: " + SafeError(ex.Message));
        }
        finally
        {
            table.Elapsed += sw.Elapsed;
            var end = LogTableEnd(run, table, sw.Elapsed);

            // 실행 기록(copy-runs)에도 바로 한 줄: 표 결과 줄은 마무리 때 한꺼번에 쓰므로 실행 중에는 이 줄로 어디까지 왔는지 안다(결정 70).
            await RecordEventAsync(run, "tableEnd", table.Name, end);
        }
    }
}
