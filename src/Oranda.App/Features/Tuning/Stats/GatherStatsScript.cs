using System;
using System.Globalization;
using System.Text;
using Oranda.Core.Tuning;
using static Oranda.App.Features.Tuning.Stats.TuningText;

namespace Oranda.App.Features.Tuning.Stats;

/// <summary>
/// 「통계 수집 스크립트 만들기」(TN-16): Core <see cref="TuningScripts.DbmsStats"/> 문안 앞에 대상·지금 상태 줄을 붙인다.
/// 새 편집기 탭에 넣기만 하고 DB 로 보내지 않는다(문안 머리 주석이 「DB 에 씁니다」를 알린다).
/// </summary>
public static class GatherStatsScript
{
    /// <param name="target">대상 표.</param>
    /// <param name="stats">지금 통계(stale·마지막 수집 표시). 없으면 null.</param>
    /// <param name="now">만든 시각.</param>
    public static TuningScript Build(TableStatsTarget target, TableStatsRow? stats, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(target);
        var state = stats switch
        {
            null or { LastAnalyzed: null } => T("통계 없음", "no statistics"),
            { Stale: true } => "stale",
            { LastAnalyzed: { } at } => T("마지막 수집 ", "last gathered ") + at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        };
        var core = TuningScripts.DbmsStats(target.Owner, target.Table);
        var sb = new StringBuilder();
        sb.Append("-- ").Append(T("대상: ", "Target: ")).Append(target.Display).Append(" · ").Append(state).Append(" · ")
            .AppendLine(now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        sb.AppendLine(core.Body);
        return core with { Body = sb.ToString() };
    }

    /// <summary>편집기 탭 제목 <c>gather_LOT_HIST.sql</c>(파일 이름에 못 쓰는 글자는 _).</summary>
    public static string Title(TableStatsTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var name = new StringBuilder();
        foreach (var c in target.Table)
        {
            name.Append(char.IsLetterOrDigit(c) || c is '_' or '$' or '#' ? c : '_');
        }

        return "gather_" + name + ".sql";
    }
}
