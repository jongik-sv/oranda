using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.DbTools;

/// <summary>실행 엔진을 거친 문장 한 건의 결과 요약.</summary>
/// <param name="Status">실행 상태(정책 관문이 막으면 <see cref="ExecutionStatus.Denied"/>)</param>
/// <param name="Message">사람이 읽는 설명(오류·거부 사유). 성공이면 null</param>
public sealed record StatementOutcome(ExecutionStatus Status, string? Message)
{
    public bool Succeeded => Status == ExecutionStatus.Succeeded;
}

/// <summary>
/// 객체 관리 도구 공용 도우미. 상태를 바꾸는 문장은 반드시 <see cref="IExecutionService"/>(정책 관문 → 위험 명령 확인 → 읽기 전용 세션 차단)로 보낸다.
/// </summary>
internal static class DbToolsSupport
{
    public static async Task<StatementOutcome> RunAsync(IExecutionService exec, ISession session, SqlStatement stmt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(exec);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(stmt);

        var result = await exec.ExecuteAsync(session, stmt, new Dictionary<string, BindValue>(),
            new ExecutionOptions { Source = HistorySource.SchemaBrowser }, ct);
        if (result.ResultSet is { } rs)
        {
            await rs.DisposeAsync();
        }

        var message = result.Status switch
        {
            ExecutionStatus.Succeeded => null,
            ExecutionStatus.Denied => result.Policy?.Message ?? "정책 관문이 막았다",
            ExecutionStatus.Cancelled => "취소했다",
            _ => result.Error is { } e ? $"{e.Code} {e.Message}".Trim().ReplaceLineEndings(" ") : "실행하지 못했다",
        };
        return new StatementOutcome(result.Status, message);
    }

    /// <summary>접속 사용자 이름(스키마 기본값). 비어 있으면 null.</summary>
    public static string? DefaultOwner(ISession session)
    {
        var user = session.Profile.User;
        return string.IsNullOrWhiteSpace(user) ? null : user.Trim().Trim('"').ToUpperInvariant();
    }

    public static string FormatSize(long? bytes)
    {
        if (bytes is not { } b)
        {
            return string.Empty;
        }

        return b switch
        {
            >= 1L << 30 => (b / (double)(1L << 30)).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " GB",
            >= 1L << 20 => (b / (double)(1L << 20)).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " MB",
            >= 1L << 10 => (b / (double)(1L << 10)).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " KB",
            _ => b.ToString(System.Globalization.CultureInfo.InvariantCulture) + " B",
        };
    }
}
