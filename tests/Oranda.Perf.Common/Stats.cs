using System.Globalization;

namespace Oranda.Perf.Common;

/// <summary>표본 한 묶음의 요약. 단위는 호출하는 쪽이 정한다(보통 ms 또는 MB).</summary>
public sealed record Summary(int N, double Min, double Median, double P95, double Max)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"n={N} min={Min:F1} median={Median:F1} p95={P95:F1} max={Max:F1}");
}

/// <summary>중앙값·백분위. 백분위는 가장 가까운 순위(nearest-rank) 방식이다: n 개 중 ceil(n*p/100) 번째로 작은 값.</summary>
public static class Stats
{
    public static double Median(IReadOnlyCollection<double> values)
    {
        var sorted = Sorted(values);
        var n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2;
    }

    /// <summary>nearest-rank 백분위. <paramref name="percent"/> 는 0 초과 100 이하.</summary>
    public static double Percentile(IReadOnlyCollection<double> values, double percent)
    {
        if (double.IsNaN(percent) || percent <= 0 || percent > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "0 초과 100 이하여야 한다.");
        }

        var sorted = Sorted(values);
        var rank = (int)Math.Ceiling(sorted.Length * percent / 100.0);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }

    public static Summary Summarize(IReadOnlyCollection<double> values)
    {
        var sorted = Sorted(values);
        return new Summary(sorted.Length, sorted[0], Median(sorted), Percentile(sorted, 95), sorted[^1]);
    }

    private static double[] Sorted(IReadOnlyCollection<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            throw new ArgumentException("표본이 하나도 없다.", nameof(values));
        }

        if (values.Any(double.IsNaN))
        {
            throw new ArgumentException("표본에 NaN 이 있다.", nameof(values));
        }

        var array = values.ToArray();
        Array.Sort(array);
        return array;
    }
}
