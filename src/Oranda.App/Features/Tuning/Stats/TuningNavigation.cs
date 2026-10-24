using System;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Shell;
using Oranda.Core.Plans;

namespace Oranda.App.Features.Tuning.Stats;

/// <summary>
/// 다른 창(계획 창·SQL 모니터)이 「통계 보기」「자식 커서」를 대상과 함께 여는 길.
/// <see cref="ShellCommandRunner.RunAsync"/> 는 명령 ID 만 넘기므로, 대상을 여기 맡긴 뒤 명령을 부르면 처리기가 꺼내 쓴다(한 번 쓰면 비운다).
/// 레지스트리로 직접 부를 때는 매개변수로 <see cref="TableStatsTarget"/>·<c>"OWNER.TABLE"</c>·<see cref="CursorRef"/>·sql_id 글을 넘겨도 된다.
/// </summary>
public static class TuningNavigation
{
    public const string StatsCommandId = "oranda.tuning.stats";

    public const string ChildCursorsCommandId = "oranda.tuning.childCursors";

    private static TableStatsTarget? _pendingStats;
    private static CursorRef? _pendingCursor;

    /// <summary>표 통계 탭을 연다. <paramref name="owner"/> 가 비면 접속 계정의 표.</summary>
    public static Task OpenStatsAsync(string? owner, string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        Interlocked.Exchange(ref _pendingStats, new TableStatsTarget(owner?.Trim() ?? string.Empty, table.Trim()));
        return ShellCommandRunner.RunAsync(StatsCommandId);
    }

    /// <summary>sql_id 의 자식 커서 탭을 연다(<paramref name="instanceId"/> 는 RAC 일 때 처음 거를 인스턴스).</summary>
    public static Task OpenChildCursorsAsync(string sqlId, int? instanceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlId);
        Interlocked.Exchange(ref _pendingCursor, new CursorRef(sqlId.Trim(), null, instanceId));
        return ShellCommandRunner.RunAsync(ChildCursorsCommandId);
    }

    /// <summary>맡긴 표를 꺼낸다(없으면 null). 소유자가 비었으면 처리기가 접속 계정으로 채운다.</summary>
    internal static TableStatsTarget? TakeStats() => Interlocked.Exchange(ref _pendingStats, null);

    /// <summary>맡긴 sql_id 를 꺼낸다(없으면 null).</summary>
    internal static CursorRef? TakeCursor() => Interlocked.Exchange(ref _pendingCursor, null);
}
