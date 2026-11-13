using System.Globalization;
using Oranda.Core.Execution;
using Oranda.Core.Rollback;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.Execution;

// ExecutionController 의 일부: 되돌리기 지점(설계 10.2 실행 로그 줄, 10.3 커밋 전 경고).
public sealed partial class ExecutionController
{
    /// <summary>
    /// 문장 하나의 원복 스크립트 실행 로그 줄(설계 10.2). 엔진이 준 <see cref="RollbackStatementSummary.LogLine"/>·<see cref="RollbackStatementSummary.Warning"/> 을
    /// 먼저 쓰고, 없으면 상태로 문구를 만든다. 원복 스크립트 없이 SCN 만 기록한 문장은 줄을 남기지 않는다(개발 접속 로그가 늘지 않게).
    /// </summary>
    internal static IReadOnlyList<(MessageKind Kind, string Text)> RollbackLogLines(RollbackStatementSummary? summary)
    {
        if (summary is null)
        {
            return [];
        }

        var lines = new List<(MessageKind, string)>();
        if (summary.ReturningAppended && summary.LogLine?.Contains("RETURNING", StringComparison.OrdinalIgnoreCase) != true)
        {
            lines.Add((MessageKind.Info, "원복 스크립트를 만들려고 문장 끝에 RETURNING ROWID INTO 를 붙여 실행했습니다."));
        }

        if (summary.Warning is { Length: > 0 } warning)
        {
            lines.Add((MessageKind.Warning, warning));
        }

        // 엔진은 행 수 다름을 LogLine 과 Warning 에 같은 글로 함께 채울 수 있다: 같은 글이면 경고 한 줄만 남긴다.
        if (summary.LogLine is { Length: > 0 } line)
        {
            if (string.Equals(line, summary.Warning, StringComparison.Ordinal))
            {
                return lines;
            }

            lines.Add((summary.State == RollbackScriptState.Incomplete && summary.Warning is null ? MessageKind.Warning : MessageKind.Info, line));
            return lines;
        }

        if (summary.Warning is not null)
        {
            return lines;
        }

        switch (summary.State)
        {
            case RollbackScriptState.Open or RollbackScriptState.Confirmed:
                lines.Add((MessageKind.Info, $"원복 스크립트 {summary.StepCount.ToString("N0", CultureInfo.CurrentCulture)}문장 (커밋하면 확정)"));
                break;
            case RollbackScriptState.Incomplete:
                lines.Add((MessageKind.Warning, "원복 스크립트 불완전 — 커밋 전에 확인하세요"));
                break;
            case RollbackScriptState.NotCreated when NotCreatedText(summary.Reason) is { } why:
                lines.Add((MessageKind.Info, "원복 스크립트를 만들지 않았습니다: " + why));
                break;
        }

        return lines;
    }

    /// <summary>운영 접속에서 원복 스크립트를 만들려다 만들지 않은 까닭(로그에 남길 것만). SCN 만 기록하는 보통 까닭은 null.</summary>
    private static string? NotCreatedText(NotCreatedReason reason) => reason switch
    {
        NotCreatedReason.OverLimit => "상한 넘음",
        NotCreatedReason.NoSecureStore => "안전한 저장소 없음",
        NotCreatedReason.Failed => "만드는 중 오류",
        _ => null,
    };

    private void LogRollback(ExecutionResult r)
    {
        foreach (var (kind, text) in RollbackLogLines(r.Rollback))
        {
            Log.AddMessage(kind, text);
        }
    }

    /// <summary>편집기에서 실행하는 <c>COMMIT</c> 문장(트랜잭션 제어 문장 중 첫 단어가 COMMIT).</summary>
    internal static bool IsCommitStatement(SqlStatement stmt)
    {
        if (stmt.Kind != StatementKind.TransactionControl)
        {
            return false;
        }

        var text = SkipLeadingComments(stmt.Text.AsSpan());
        return text.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase)
            && (text.Length == 6 || !(char.IsLetterOrDigit(text[6]) || text[6] is '_' or '$' or '#'));
    }

    /// <summary>앞의 공백·<c>--</c> 줄 주석·<c>/* */</c> 주석을 건너뛴다.</summary>
    private static ReadOnlySpan<char> SkipLeadingComments(ReadOnlySpan<char> text)
    {
        while (true)
        {
            text = text.TrimStart();
            if (text.StartsWith("--", StringComparison.Ordinal))
            {
                var nl = text.IndexOf('\n');
                text = nl < 0 ? [] : text[(nl + 1)..];
            }
            else if (text.StartsWith("/*", StringComparison.Ordinal))
            {
                var end = text.IndexOf("*/", StringComparison.Ordinal);
                text = end < 0 ? [] : text[(end + 2)..];
            }
            else
            {
                return text;
            }
        }
    }

    /// <summary>
    /// 커밋 전 경고(설계 10.3, RD-18): 설정 「잠근 행 수와 실행한 행 수가 다르면 커밋 전에 알리기」가 켜져 있고 그 세션의 열린 묶음에
    /// 행 수가 다른 문장이 있으면 묻는다. true = 커밋해도 된다. 자동 커밋은 이 길을 지나지 않는다.
    /// </summary>
    internal async Task<bool> ConfirmIncompleteCommitAsync(ISession session, CancellationToken ct)
    {
        if (!_d.RollbackSettings().WarnRowCountMismatch)
        {
            return true;
        }

        RollbackOpenState? open;
        try
        {
            open = _d.RollbackLedger()?.Open(session.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true; // 원장을 못 읽으면 묻지 않는다(커밋을 막지 않는다).
        }

        if (open is not { HasMismatch: true })
        {
            return true;
        }

        var ok = await _d.Dialogs.ConfirmIncompleteCommitAsync(
            "이 트랜잭션의 원복 스크립트는 불완전합니다: 잠근 행 수와 실행한 행 수가 다른 문장이 있습니다. 커밋할까요?", ct).ConfigureAwait(true);
        if (!ok)
        {
            Log.AddMessage(MessageKind.Info, "커밋하지 않았습니다.");
        }

        return ok;
    }
}
