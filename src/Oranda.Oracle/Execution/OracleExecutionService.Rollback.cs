using Oranda.Core.Execution;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Capture;
using Oranda.Core.Sessions;
using Oranda.Oracle.Rollback;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Execution;

// 되돌리기 지점 연결(설계 6.1·7.3·13.5, RB-10). 판단(트랜잭션 제어 해석, 끝낼 수 있는 문장, 판정 필요, 기록 방식)은 Core RollbackFlow 가 하고
// 여기는 보내기·통로·방언만 댄다. 되돌리기 기능의 실패는 사용자 문장을 막지 않는다(반목표 1).
internal sealed partial class OracleExecutionService
{
    /// <summary>
    /// 문장 하나의 되돌리기 준비(6.1 의 2단계: 정책 관문 앞, 명령 잠금 밖). 기록하지 않는 실행(<see cref="ChangeRecording.None"/>)과
    /// 기록 대상이 아닌 문장(질의·DDL·트랜잭션 제어)은 엔진을 부르지 않는다. 엔진은 준비 실패를 예외 없이 담고 사용자 취소만 전파한다.
    /// </summary>
    private async Task<RollbackPlan> PrepareRollbackAsync(OracleSession session, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds,
                                                          ExecutionOptions options, CancellationToken ct)
    {
        if (RollbackFlow.RecordingFor(session, options) == ChangeRecording.None || !RollbackFlow.IsRecordedWrite(stmt))
        {
            return RollbackPlan.None;
        }

        var shape = stmt.Dml ?? AnalyzeQuietly(stmt.Text); // 분석기는 쓰기 문장이 아니면 null(분류가 불확실한 DML 도 모양을 얻는다)
        var request = new RollbackRequest(session, stmt, binds, options) { Shape = shape };
        var extras = await session.Rollback.Capture.PrepareAsync(request, OracleRollbackChannel.Gated(session), session.Rollback.Dialect, ct);
        return new RollbackPlan(request, extras ?? ConfirmExtras.None);
    }

    /// <summary>준비 단계의 계약 밖 예외(다른 엔진 구현, 사용자 취소가 아닌 취소)도 사용자 문장을 막지 않는다: 원복 없이 진행한다.</summary>
    private async Task<RollbackPlan> PrepareRollbackQuietlyAsync(OracleSession session, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds,
                                                                 ExecutionOptions options, CancellationToken ct)
    {
        try
        {
            return await PrepareRollbackAsync(session, stmt, binds, options, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return RollbackPlan.None;
        }
    }

    /// <summary>분석기는 예외를 던지지 않는다는 계약이지만, 실패해도 사용자 문장은 그대로 간다(모양 없음 = 원복 대상 아님).</summary>
    private DmlShape? AnalyzeQuietly(string text)
    {
        try
        {
            return _analyzer.Analyze(text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>준비 결과. <see cref="Request"/> 가 null 이면 이 문장은 엔진에 넘기지 않는다(트랜잭션 끝 처리는 한다).</summary>
    private sealed record RollbackPlan(RollbackRequest? Request, ConfirmExtras Extras)
    {
        public static RollbackPlan None { get; } = new(null, ConfirmExtras.None);
    }

    /// <summary>
    /// 6.1 의 5단계(명령 잠금 안, 보내기 직전): 층 B 시점 읽기와 내부 저장점·잠금 읽기. 엔진에 넘기지 않는 문장은 null(받은 글·바인드를 그대로 보내고
    /// 결과의 <see cref="ExecutionResult.Rollback"/> 도 null).
    /// 사용자 취소는 전파한다(사용자 문장을 보내지 않는다). 그 밖의 엔진 실패(엔진 안 제한 시간 포함)는 원복 없이 사용자 문장을 그대로 보낸다
    /// (저장점 뒤 실패면 엔진이 스스로 되돌렸다, 조정 11:57). Tibero 공급자와 같은 규칙이다.
    /// </summary>
    private static async Task<IStatementCapture?> BeginRollbackAsync(OracleSession session, RollbackPlan plan, ConfirmAnswer answer, CancellationToken ct)
    {
        if (plan.Request is not { } request)
        {
            return null;
        }

        try
        {
            return await session.Rollback.Capture.BeginAsync(request, plan.Extras, answer, OracleRollbackChannel.Locked(session),
                session.Rollback.Dialect, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // 원복 없이 사용자 문장을 그대로 보낸다
        }
    }

    /// <summary>사용자 문장 성공 뒤 마무리(6.1 의 8단계). 엔진은 던지지 않는 계약이고, 실패해도 이미 성공한 사용자 문장 결과를 바꾸지 않는다.</summary>
    private static async Task<RollbackStatementSummary> CompleteQuietlyAsync(IStatementCapture capture, long? rows,
                                                                             IReadOnlyDictionary<string, object?> outValues, string? transactionId)
    {
        try
        {
            return await capture.CompleteAsync(rows, outValues, transactionId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new RollbackStatementSummary(RollbackScriptState.Incomplete, NotCreatedReason.Failed, 0)
            {
                Warning = "원복 스크립트를 마무리하지 못했습니다: " + ex.Message,
            };
        }
    }

    /// <summary>
    /// 실패·취소 뒤. 사용자 문장이 끝나기 전이면 <see cref="IStatementCapture.AbandonAsync"/>(이번 Begin 이 건 내부 저장점으로 되돌려 잠금을 푼다, RD-5).
    /// 사용자 문장은 끝났고 그 뒤 단계(OUT 값 읽기·트랜잭션 추적)가 실패했으면 되돌리지 않고 마무리한다(저장점으로 되돌리면 성공한 문장이 지워진다).
    /// 공급자는 <c>ROLLBACK TO</c> 를 대신 보내지 않는다.
    /// </summary>
    private static async Task<RollbackStatementSummary?> FinishAfterFailureAsync(OracleSession session, IStatementCapture? capture, bool executed,
                                                                                 long? rows, IReadOnlyDictionary<string, object?>? outValues,
                                                                                 string? transactionId, DbError? failure)
    {
        if (capture is null)
        {
            return null;
        }

        if (executed)
        {
            return await CompleteQuietlyAsync(capture, rows, outValues ?? new Dictionary<string, object?>(), transactionId);
        }

        var broken = session.State is SessionState.Broken or SessionState.Closed;
        try
        {
            var summary = await capture.AbandonAsync(failure, CancellationToken.None);
            return broken && summary.Warning is not null ? summary with { Warning = null } : summary; // 끊긴 세션은 서버가 이미 되돌렸다
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new RollbackStatementSummary(RollbackScriptState.NotCreated, NotCreatedReason.Failed, 0)
            {
                Warning = broken ? null : "원복 스크립트용 잠금을 풀지 못했습니다: " + ex.Message,
            };
        }
    }

    /// <summary>
    /// 문장 뒤 트랜잭션 끝(7.3): 성공한 <c>SAVEPOINT 이름</c> 은 원장에 표시하고, 끝이면(<see cref="RollbackFlow.EndAfter"/>)
    /// 판정·커밋 시점·원장 끝·이벤트(<see cref="OracleRollbackHooks.EndAsync"/>). 명령 잠금 안에서 부른다. 실패는 결과를 바꾸지 않는다.
    /// </summary>
    private static async Task AfterStatementAsync(OracleSession session, SqlStatement stmt, bool succeeded, bool pendingBefore, bool pendingAfter)
    {
        var hooks = session.Rollback;
        try
        {
            if (RollbackFlow.SavepointMarked(stmt, succeeded, hooks.Dialect.SavepointName) is { } name)
            {
                hooks.Ledger.MarkSavepoint(session.Id, name);
            }

            if (RollbackFlow.EndAfter(session.Id, stmt, succeeded, pendingBefore, pendingAfter) is { } ended)
            {
                await hooks.EndAsync(session, ended.Kind, ended.Source, ended.Savepoint);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 원장 표시·판정 실패는 사용자 문장의 결과를 바꾸지 않는다.
        }
    }

    /// <summary>사용자 결과에 실을 출력 값: 엔진이 붙인 <c>:ORANDA_RID</c> 는 뺀다(사용자 문장에 원래 있던 이름이면 둔다).</summary>
    private static Dictionary<string, object?> UserOutValues(Dictionary<string, object?> all, SqlStatement original)
    {
        const string rid = RollbackCaptureEngine.ReturningBind;
        if (!all.ContainsKey(rid) || original.BindNames.Contains(rid, StringComparer.OrdinalIgnoreCase))
        {
            return all;
        }

        var user = new Dictionary<string, object?>(all, StringComparer.OrdinalIgnoreCase);
        user.Remove(rid);
        return user;
    }
}
