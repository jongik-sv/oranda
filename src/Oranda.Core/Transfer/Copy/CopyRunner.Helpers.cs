using System.Globalization;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Sessions;

namespace Oranda.Core.Transfer.Copy;

public partial class CopyRunner
{
    // ---- 실행 도우미 ----

    /// <summary>바인드 없는 트랜잭션 제어(대량 경로: 읽기 전용 원본에서도 SET TRANSACTION·COMMIT·ROLLBACK 은 지난다).</summary>
    private Task<BulkResult> ControlAsync(ISession s, SqlStatement stmt, CancellationToken ct) =>
        _bulk.ExecuteArrayAsync(s, stmt, [], [], new ExecutionOptions { Source = HistorySource.DataCopy, SkipHistory = true }, ct);

    /// <summary>트랜잭션 제어(<see cref="ControlAsync"/>)를 작업 로그에 남기며 보낸다. 배치마다 도는 세이브포인트에는 쓰지 않는다(줄이 넘친다).</summary>
    private async Task<BulkResult> ControlLoggedAsync(RunState run, ISession s, SqlStatement stmt, CancellationToken ct, string? note = null)
    {
        run.Log(CopyLogText.Statement(run.SideOf(s), stmt.Text) + (note is null ? string.Empty : "  — " + note), CopyLogKind.Statement);
        var r = await ControlAsync(s, stmt, ct);
        if (r.Status != ExecutionStatus.Succeeded)
        {
            run.Log("→ " + Describe(r), CopyLogKind.Error);
        }

        return r;
    }

    /// <summary>실행 엔진 결과 한 줄: 완료(영향 행 수·걸린 시간) 또는 실패 사유.</summary>
    private static void LogResult(RunState run, ExecutionResult r)
    {
        if (r.Status == ExecutionStatus.Succeeded)
        {
            var rows = r.RowsAffected is { } n ? n.ToString("N0", CultureInfo.InvariantCulture) + "행 · " : string.Empty;
            run.Log($"→ 완료 · {rows}{CopyLogText.Elapsed(r.Elapsed)}");
        }
        else
        {
            run.Log("→ " + Describe(r), CopyLogKind.Error);
        }
    }

    /// <summary>DELETE·ALTER: 실행 엔진(정책·이력)으로 보내고, 성공하면 실행 기록에도 같은 글을 남긴다.</summary>
    private async Task<ExecutionResult> ExecuteLoggedAsync(RunState run, ISession s, SqlStatement stmt,
                                                           IReadOnlyDictionary<string, BindValue> binds, CancellationToken ct, ExecutionOptions? options = null)
    {
        run.LogStatement(s, stmt.Text, binds);
        var r = await _execution.ExecuteAsync(s, stmt, binds, options ?? run.HistoryOptions, ct);
        LogResult(run, r);
        if (r.Status == ExecutionStatus.Cancelled && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        if (r.Status == ExecutionStatus.Succeeded && run.Record is { } record)
        {
            await record.RecordStatementAsync(stmt.Text, ct);
        }

        return r;
    }

    private async Task<List<object?[]>> RowsAsync(RunState run, ISession s, SqlStatement query,
                                                 IReadOnlyDictionary<string, BindValue> binds, CancellationToken ct)
    {
        var rows = new List<object?[]>();
        await foreach (var row in _bulk.StreamRowsAsync(s, query, binds, run.BulkOptions, ct))
        {
            rows.Add(row);
        }

        return rows;
    }

    private async Task<object?> ScalarAsync(RunState run, ISession s, SqlStatement query,
                                            IReadOnlyDictionary<string, BindValue> binds, CancellationToken ct)
    {
        var rows = await RowsAsync(run, s, query, binds, ct);
        return rows.Count > 0 && rows[0].Length > 0 ? rows[0][0] : null;
    }

    private static string Describe(ExecutionResult r) =>
        r.Status switch
        {
            ExecutionStatus.Denied => "정책 거부: " + (r.Policy?.Message ?? "거부"),
            ExecutionStatus.Cancelled => "확인을 받지 못해 실행하지 않았습니다" + (r.Policy?.Message is { } m ? ": " + m : string.Empty),
            _ => ErrorText(r.Error),
        };

    internal static string Describe(BulkResult r) =>
        r.Status switch
        {
            ExecutionStatus.Denied => "정책 거부: " + (r.Policy?.Message ?? "거부"),
            ExecutionStatus.Cancelled => "확인을 받지 못해 실행하지 않았습니다" + (r.Policy?.Message is { } m ? ": " + m : string.Empty),
            _ => ErrorText(r.Error),
        };

    /// <summary>실패 행 기록에는 오류 문구만 남긴다(서버 오류 문구에는 행 값이 들어가지 않는다).</summary>
    private static string ErrorText(DbError? e) =>
        e is null ? "알 수 없는 오류"
        : SafeError(e.Message.StartsWith(e.Code, StringComparison.Ordinal) ? e.Message : e.Code + ": " + e.Message);

    /// <summary>
    /// 서버 오류 문구에서 행 값을 지운다. 23ai 부터 일부 오류가 값을 문구에 넣는다
    /// (ORA-01722 「unable to convert string value containing '…'」, ORA-03301 「row with column values (…)」).
    /// 첫 줄만 남기고 작은따옴표 안의 글자와 「column values (…)」 를 지운다. 열·제약 이름(큰따옴표)은 남긴다.
    /// 실행 기록·표 결과·알림에 남는 오류 문구는 모두 이것을 거친다(행 값은 디스크에 남기지 않는다, 설계 4장).
    /// </summary>
    internal static string SafeError(string message)
    {
        var line = message.Split('\n', 2)[0].TrimEnd('\r');
        line = QuotedValue.Replace(line, "'…'");
        return ColumnValues.Replace(line, "column values (…)");
    }
}
