namespace Oranda.Core.Transfer.Copy;

/// <summary>상대 기간을 실제 날짜 범위로 바꾼다. 경계는 시작 포함·끝 제외다.</summary>
public static class PeriodCalculator
{
    public static ResolvedPeriod Resolve(CopyPeriod period, DateTime today)
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
            PeriodKind.Custom => new ResolvedPeriod(period.From, period.To),
            _ => throw new ArgumentOutOfRangeException(nameof(period)),
        };
    }
}
