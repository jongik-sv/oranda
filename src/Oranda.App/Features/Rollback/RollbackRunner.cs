using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Execution;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Script;
using Oranda.Core.Sessions;
using Oranda.Core.Text;

namespace Oranda.App.Features.Rollback;

/// <summary>원복 문장 하나의 결과(되돌리기 지점 설계 11.3).</summary>
public enum RollbackStepOutcome
{
    /// <summary>1행을 되돌렸다.</summary>
    Reverted,

    /// <summary>0행: 그 뒤 다른 사람이 바꾼 행.</summary>
    ZeroRows,

    /// <summary>같은 키가 이미 있음(<see cref="DbErrorKind.ConstraintViolation"/>).</summary>
    Conflict,

    /// <summary>그 밖의 오류, 거부, 1행이 아닌 영향 행.</summary>
    Error,
}

/// <summary>원복 문장 하나의 결과 줄.</summary>
public sealed record RollbackStepResult(int Seq, string Sql, RollbackStepOutcome Outcome, string? Message)
{
    /// <summary>결과 목록 한 줄: 「[2] 그 뒤 다른 사람이 바꾼 행 · UPDATE SAMPLE.PRODUCTS SET …」.</summary>
    public string Line => $"[{Seq.ToString(CultureInfo.InvariantCulture)}] {Message} · {RollbackScriptWriter.Summarize(Sql)}";
}

/// <summary>원복 실행 요약: 되돌림 n, 0행 n, 충돌 n, 오류 n. 커밋·롤백은 사용자가 한다.</summary>
public sealed record RollbackRunSummary(IReadOnlyList<RollbackStepResult> Steps, bool Cancelled)
{
    public int Reverted => Steps.Count(s => s.Outcome == RollbackStepOutcome.Reverted);

    public int ZeroRows => Steps.Count(s => s.Outcome == RollbackStepOutcome.ZeroRows);

    public int Conflicts => Steps.Count(s => s.Outcome == RollbackStepOutcome.Conflict);

    public int Errors => Steps.Count(s => s.Outcome == RollbackStepOutcome.Error);

    /// <summary>「되돌림 14 · 0행 1 · 충돌 1 · 오류 0. 커밋하지 않았습니다…」.</summary>
    public string Text =>
        string.Format(CultureInfo.CurrentCulture, "되돌림 {0} · 0행 {1} · 충돌 {2} · 오류 {3}", Reverted, ZeroRows, Conflicts, Errors)
        + (Cancelled ? " · 중간에 멈춤" : string.Empty)
        + ". 커밋하지 않았습니다. 확인한 뒤 커밋이나 롤백을 직접 하세요.";
}

/// <summary>
/// 원복 실행(되돌리기 지점 설계 11.3, RD-8). 원복 문장을 실행 엔진(정책 관문)으로 보내되, 미리 확인한 문장(<see cref="ExecutionOptions.PreConfirmed"/>)이라
/// 문장마다 다시 묻지 않고 <see cref="ChangeRecording.None"/> 으로 잠금 읽기를 하지 않는다. 문장마다 영향 행 수가 1인지 보고, 0행·충돌·오류는 표시만 하고 계속한다.
/// 커밋하지 않는다. 1행을 바꾼 문장들의 반대 문장 묶음은 <see cref="ReverseAll"/> 이 만들어 원장에 따로 기록한다(<see cref="RollbackOrigin.RollbackRun"/>).
/// </summary>
public sealed class RollbackRunner(IExecutionService execution, IRollbackLedger? ledger)
{
    /// <summary>
    /// 1행을 바꾼 원복 문장들(실행 차례) → 반대 문장 묶음(되돌릴 차례, 역순)(RD-8). 기본은 Core 의 <see cref="RollbackReverse.ReverseAll"/>.
    /// </summary>
    public Func<IReadOnlyList<RollbackStep>, IReadOnlyList<RollbackStep>> ReverseAll { get; init; } = steps => RollbackReverse.ReverseAll(steps, out _);

    /// <summary>
    /// 스크립트의 실행 순서(실행한 문장의 역순, 문장 안 원복 문장 순서 그대로). 무효 문장(문서나 묶음 정보에서 무효로 표시된 것)은 뺀다.
    /// 스크립트 글(<see cref="RollbackScriptWriter.Write"/>)과 같은 판단이다.
    /// </summary>
    public static IReadOnlyList<(int Seq, RollbackStep Step)> Order(RollbackScriptDocument document, RollbackGroup? group = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var invalid = group?.Statements.Where(s => s.Invalidated).Select(s => s.Seq).ToHashSet() ?? [];
        return [.. document.Statements.Where(s => !s.Invalidated && !invalid.Contains(s.Seq)).OrderByDescending(s => s.Seq).SelectMany(s => s.Steps.Select(step => (s.Seq, step)))];
    }

    /// <summary>원복 문장을 차례로 실행한다. 사용자가 취소하면 거기서 멈추고 지금까지의 요약을 돌려준다.</summary>
    public async Task<RollbackRunSummary> RunAsync(ISession session, RollbackGroup group, RollbackScriptDocument document,
                                                   IProgress<RollbackStepResult>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(group);
        var order = Order(document, group);
        var results = new List<RollbackStepResult>();
        var executed = new List<RollbackStep>();
        var cancelled = false;
        foreach (var (seq, step) in order)
        {
            if (ct.IsCancellationRequested || session.State is SessionState.Broken or SessionState.Closed)
            {
                cancelled = true;
                break;
            }

            var sql = step.Sql.Trim();
            var stmt = new SqlStatement(sql, new TextRange(0, sql.Length), StatementKind.Dml) { Verb = FirstWord(sql) };
            // 확인은 실행 전에 한 번 받았다: 문장마다 그 글 하나만 미리 확인한 것으로 넘긴다(큰 집합을 문장마다 훑지 않게).
            // 원복 문장에는 실제 행 값이 들어 있으므로 평문 실행 이력에 남기지 않는다(RD-10).
            var options = new ExecutionOptions
            {
                Source = Oranda.Core.History.HistorySource.Editor,
                PreConfirmed = new HashSet<string>(StringComparer.Ordinal) { sql },
                Recording = ChangeRecording.None,
                SkipHistory = true,
            };
            ExecutionResult r;
            try
            {
                r = await execution.ExecuteAsync(session, stmt, new Dictionary<string, BindValue>(), options, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                var failed = new RollbackStepResult(seq, sql, RollbackStepOutcome.Error, ex.Message);
                results.Add(failed);
                progress?.Report(failed);
                continue;
            }

            if (r.Status == ExecutionStatus.Cancelled)
            {
                cancelled = true;
                break;
            }

            var result = Classify(seq, sql, r, step.ExpectOneRow);
            results.Add(result);
            progress?.Report(result);
            if (result.Outcome == RollbackStepOutcome.Reverted && r.RowsAffected == 1) // RD-8: 한 행을 바꾼 문장만 반대 문장이 정확하다
            {
                executed.Add(step);
            }
        }

        RecordInverse(session, group, executed);
        return new RollbackRunSummary(results, cancelled);
    }

    internal static RollbackStepResult Classify(int seq, string sql, ExecutionResult r, bool expectOneRow = true) => r.Status switch
    {
        ExecutionStatus.Succeeded when r.RowsAffected == 1 || (!expectOneRow && r.RowsAffected > 0) => new(seq, sql, RollbackStepOutcome.Reverted, null),
        ExecutionStatus.Succeeded when r.RowsAffected == 0 => new(seq, sql, RollbackStepOutcome.ZeroRows, "그 뒤 다른 사람이 바꾼 행"),
        ExecutionStatus.Succeeded => new(seq, sql, RollbackStepOutcome.Error, $"영향 행 {r.RowsAffected?.ToString(CultureInfo.InvariantCulture) ?? "?"} (1행이어야 함)"),
        ExecutionStatus.Failed when r.Error?.Kind == DbErrorKind.ConstraintViolation => new(seq, sql, RollbackStepOutcome.Conflict,
            r.Error.Code == "ORA-00001" ? "충돌: 같은 키가 이미 있음" : "충돌: 제약 조건 위반 · " + r.Error.Message),
        ExecutionStatus.Denied => new(seq, sql, RollbackStepOutcome.Error, "거부됨: " + (r.Policy?.Message ?? string.Empty)),
        _ => new(seq, sql, RollbackStepOutcome.Error, r.Error?.Message ?? "실패"),
    };

    /// <summary>반대 문장 묶음을 원장에 둔다(RD-8): 그 세션의 다음 끝(커밋·롤백)을 따른다. 만들기·기록 실패는 원복 실행을 막지 않는다.</summary>
    private void RecordInverse(ISession session, RollbackGroup group, List<RollbackStep> executed)
    {
        if (ledger is null || executed.Count == 0)
        {
            return;
        }

        IReadOnlyList<RollbackStep> inverses;
        try
        {
            inverses = ReverseAll(executed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }

        if (inverses.Count == 0)
        {
            return;
        }

        var record = new RollbackStatementRecord
        {
            SessionId = session.Id,
            ProfileId = session.Profile.Id,
            ProfileName = session.Profile.Name,
            DbKey = session.DbKey,
            ServerName = group.ServerName,
            Environment = session.Profile.Environment,
            Dbms = session.Profile.Dbms,
            Source = Oranda.Core.History.HistorySource.Editor,
            ExecutedAt = DateTimeOffset.Now,
            Text = $"-- 원복 스크립트 #{group.Id.ToString(CultureInfo.InvariantCulture)} 실행",
            Table = group.Tables.Count > 0 ? group.Tables[0] : null,
            RowsAffected = inverses.Count,
            State = RollbackScriptState.Open,
            Steps = inverses, // 이미 되돌릴 차례(역순)
        };
        try
        {
            ledger.RecordDerived(session.Id, RollbackOrigin.RollbackRun, group.Id, record);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 원장 기록 실패: 원복 실행 결과는 그대로 둔다(되돌리기의 되돌리기만 없다).
        }
    }

    private static string FirstWord(string sql)
    {
        var end = 0;
        while (end < sql.Length && char.IsLetter(sql[end]))
        {
            end++;
        }

        return sql[..end].ToUpperInvariant();
    }
}
