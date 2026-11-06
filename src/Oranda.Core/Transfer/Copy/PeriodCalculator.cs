namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 상대 기간을 실제 날짜 범위로 바꾼다. 돌려주는 범위는 항상 시작 포함·끝 제외다(SQL 은 <c>&gt;=</c>, <c>&lt;</c>).
/// 직접 지정에서 「끝 날짜 포함」이면 끝 날짜 다음 날을 끝 경계로 쓴다.
/// 전기일 시각(<see cref="CopyPeriod.DayStartHour"/>)이 있으면 「오늘」은 그 시각 기준 업무 날짜다(7시 기준이면 새벽 5시는 전날).
/// </summary>
public static class PeriodCalculator
{
    public static ResolvedPeriod Resolve(CopyPeriod period, DateTime today)
    {
        ArgumentNullException.ThrowIfNull(period);
        var hour = Math.Clamp(period.DayStartHour, 0, 23);
        return Dates(period, today.AddHours(-hour)) with { DayStartHour = hour };
    }

    private static ResolvedPeriod Dates(CopyPeriod period, DateTime today)
    {
        var tomorrow = today.Date.AddDays(1);
        return period.Kind switch
        {
            PeriodKind.None => new ResolvedPeriod(null, null),
            PeriodKind.LastWeek => new ResolvedPeriod(tomorrow.AddDays(-7), tomorrow),
            PeriodKind.LastMonth => new ResolvedPeriod(tomorrow.AddMonths(-1), tomorrow),
            PeriodKind.Last3Months => new ResolvedPeriod(tomorrow.AddMonths(-3), tomorrow),
            PeriodKind.PreviousMonth => new ResolvedPeriod(
                new DateTime(today.Year, today.Month, 1).AddMonths(-1),
                new DateTime(today.Year, today.Month, 1)),
            PeriodKind.Custom => new ResolvedPeriod(period.From,
                period.EndInclusive && period.To is { } to ? to.Date.AddDays(1) : period.To),
            _ => throw new ArgumentOutOfRangeException(nameof(period)),
        };
    }
}
