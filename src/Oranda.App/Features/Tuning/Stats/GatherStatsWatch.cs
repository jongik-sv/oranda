using System;
using System.Collections.Generic;
using System.Linq;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.Tuning.Stats;

/// <summary>
/// 편집기·스크립트에서 <c>DBMS_STATS.GATHER_TABLE_STATS</c> 를 직접 돌려 성공하면 확인 창 길과 같이 그 테이블만 새로 읽는다
/// (<see cref="GatherStatsCommandHandler.RefreshAsync"/>, 결함 2026-11-22). 스키마 브라우저가 <c>StatementExecuted</c> 를 받아 부른다
/// (시작 작업을 따로 두지 않는다). 확인 창 길(이력 출처 <see cref="GatherStatsRunner.Source"/>)은 스스로 새로 읽어 여기서 세지 않는다.
/// </summary>
public static class GatherStatsWatch
{
    /// <summary>실행 알림 하나를 본다. 통계를 모은 테이블이 있으면 <paramref name="post"/>(UI 스레드)로 새로 읽기를 건다.</summary>
    public static void OnExecuted(IServiceProvider services, StatementExecuted e, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(post);
        if (e.Result.Status != ExecutionStatus.Succeeded || e.Statement.Text.IndexOf("GATHER_TABLE_STATS", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return; // 대부분의 실행은 여기서 끝난다(세션을 찾지 않는다)
        }

        var session = (services.GetService(typeof(ISessionManager)) as ISessionManager)?.Sessions.FirstOrDefault(s => s.Id == e.SessionId);
        var targets = TargetsOf(e, session);
        if (targets.Count > 0)
        {
            post(() => _ = GatherStatsCommandHandler.RefreshAsync(services, session, targets));
        }
    }

    /// <summary>성공한 편집기 실행이 통계를 모은 테이블(없으면 빈 목록). 소유자 자리의 <c>USER</c> 는 그 세션 계정.</summary>
    internal static IReadOnlyList<TableStatsTarget> TargetsOf(StatementExecuted e, ISession? session)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Result.Status != ExecutionStatus.Succeeded || e.Options.Source == GatherStatsRunner.Source)
        {
            return [];
        }

        return GatherStatsSqlTargets.Find(e.Statement.Text, session?.Server.SessionUser ?? session?.Profile.User);
    }
}
