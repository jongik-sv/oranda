using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Oranda.Core.Tuning;
using static Oranda.App.Features.Tuning.Stats.TuningText;

namespace Oranda.App.Features.Tuning.Stats;

/// <summary>clustering factor 판정(블록 수의 2 배 이하 좋음, 행 수의 50 % 이상 나쁨, 그 사이 보통).</summary>
public enum ClusteringQuality
{
    Unknown,
    Good,
    Fair,
    Poor,
}

/// <summary>숫자 표기(천 단위 쉼표, 없으면 빈 글).</summary>
internal static class StatsFormat
{
    public static string Count(long? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string Bytes(long? value) => value is { } v ? v.ToString("N0", CultureInfo.InvariantCulture) + " B" : string.Empty;

    /// <summary>밀도: 0.001 아래는 지수 표기(8.0e-8), 그 위는 소수 넷째 자리.</summary>
    public static string Density(double? value)
    {
        if (value is not { } d)
        {
            return string.Empty;
        }

        if (d == 0)
        {
            return "0";
        }

        return d < 0.001
            ? d.ToString("0.0e-0", CultureInfo.InvariantCulture)
            : d.ToString("0.####", CultureInfo.InvariantCulture);
    }

    public static string Date(DateTime? value) => value?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string ShortDate(DateTime? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <summary>열 통계 한 줄.</summary>
public sealed class ColumnStatsItem(ColumnStatsRow row)
{
    public ColumnStatsRow Row { get; } = row;

    public string Name => Row.ColumnName;

    /// <summary>히스토그램이 있는 열(NONE 이 아님).</summary>
    public bool HasHistogram => !string.IsNullOrEmpty(Row.Histogram) && !string.Equals(Row.Histogram, "NONE", StringComparison.OrdinalIgnoreCase);

    public string HistogramText => HasHistogram ? Row.Histogram : T("없음", "None");

    /// <summary>격자 칸(열·고유 값·NULL·밀도·히스토그램·버킷·마지막 수집).</summary>
    public IReadOnlyList<object?> Cells =>
    [
        Name,
        StatsFormat.Count(Row.NumDistinct),
        StatsFormat.Count(Row.NumNulls),
        StatsFormat.Density(Row.Density),
        HistogramText,
        StatsFormat.Count(Row.NumBuckets),
        StatsFormat.Date(Row.LastAnalyzed),
    ];
}

/// <summary>인덱스 통계 한 줄. clustering factor 판정은 표의 블록·행 수로 한다.</summary>
public sealed class IndexStatsItem
{
    public IndexStatsItem(IndexStatsRow row, IReadOnlyList<string>? columns, long? tableBlocks, long? tableRows, bool staleKnown = true)
    {
        ArgumentNullException.ThrowIfNull(row);
        Row = row;
        StaleKnown = staleKnown;
        ColumnsText = columns is { Count: > 0 } ? string.Join(", ", columns) : string.Empty;
        Quality = Judge(row.ClusteringFactor, tableBlocks, tableRows);
        // 막대 = CF 가 블록 수(가장 좋음)에서 행 수(가장 나쁨)까지 어디쯤인지.
        BarFraction = row.ClusteringFactor is { } factor && tableRows is > 0
            ? Math.Clamp((double)factor / tableRows.Value, 0.02, 1)
            : 0;
        QualityText = Quality switch
        {
            ClusteringQuality.Good when tableBlocks is > 0 && row.ClusteringFactor is { } cf =>
                T("좋음 · 블록의 ", "Good · ") + Ratio(cf, tableBlocks.Value) + T(" 배", "× blocks"),
            ClusteringQuality.Fair when tableBlocks is > 0 && row.ClusteringFactor is { } cf =>
                T("보통 · ", "Fair · ") + Ratio(cf, tableBlocks.Value) + T(" 배", "× blocks"),
            ClusteringQuality.Poor => T("나쁨 · 행 수에 가까움", "Poor · close to row count"),
            _ => T("모름", "Unknown"),
        };
    }

    public IndexStatsRow Row { get; }

    /// <summary>stale 을 판정할 수 있는가(비 DBA 는 all_ind_statistics 가 남의 인덱스를 안 보여 늘 거짓으로 온다).</summary>
    public bool StaleKnown { get; }

    /// <summary>stale 칸을 위험색으로(통계 없음·stale 예).</summary>
    public bool StaleDanger => Row.LastAnalyzed is null || (StaleKnown && Row.Stale);

    public string ColumnsText { get; }

    public ClusteringQuality Quality { get; }

    public string QualityText { get; }

    /// <summary>CF 막대 길이(0~1).</summary>
    public double BarFraction { get; }

    public bool IsUnique => string.Equals(Row.Uniqueness, "UNIQUE", StringComparison.OrdinalIgnoreCase);

    /// <summary>격자 칸(인덱스·열·고유·BLEVEL·leaf 블록·고유 키·clustering factor·CF 판정·마지막 수집·stale).</summary>
    public IReadOnlyList<object?> Cells =>
    [
        Row.IndexName,
        ColumnsText,
        Row.Uniqueness,
        Row.BLevel?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        StatsFormat.Count(Row.LeafBlocks),
        StatsFormat.Count(Row.DistinctKeys),
        StatsFormat.Count(Row.ClusteringFactor),
        QualityText,
        StatsFormat.ShortDate(Row.LastAnalyzed),
        Row.LastAnalyzed is null ? T("통계 없음", "No stats") : !StaleKnown ? T("판정 불가", "Unknown") : Row.Stale ? T("예", "Yes") : T("아니요", "No"),
    ];

    /// <summary>CF 판정. 블록·행 수를 모르면 Unknown.</summary>
    public static ClusteringQuality Judge(long? clusteringFactor, long? tableBlocks, long? tableRows)
    {
        if (clusteringFactor is not { } cf || tableBlocks is not > 0 || tableRows is not > 0)
        {
            return ClusteringQuality.Unknown;
        }

        if (cf <= tableBlocks.Value * 2)
        {
            return ClusteringQuality.Good;
        }

        return cf >= tableRows.Value * 0.5 ? ClusteringQuality.Poor : ClusteringQuality.Fair;
    }

    private static string Ratio(long cf, long blocks)
    {
        var r = (double)cf / blocks;
        return r < 10 ? r.ToString("0.0", CultureInfo.InvariantCulture) : r.ToString("N0", CultureInfo.InvariantCulture);
    }
}

/// <summary>히스토그램 막대 하나(가로 막대: 값 이름 | 막대 | 수).</summary>
public sealed class HistogramBarItem
{
    public HistogramBarItem(string label, long frequency, double fraction, double share)
    {
        Label = label;
        Frequency = frequency;
        Fraction = Math.Clamp(fraction, 0, 1);
        Share = share;
    }

    /// <summary>끝점 값(없으면 끝점 번호).</summary>
    public string Label { get; }

    public long Frequency { get; }

    /// <summary>가장 큰 막대 대비 길이(0~1). 막대 칸은 이 비율의 별(*) 폭을 쓴다.</summary>
    public double Fraction { get; }

    /// <summary>전체 대비 비율(0~1).</summary>
    public double Share { get; }

    /// <summary>막대 값(0~100, 아주 작은 버킷도 한 점은 보이게).</summary>
    public double Percent => Frequency > 0 ? Math.Max(Fraction * 100, 0.6) : 0;

    /// <summary>막대 옆 수를 비율로 보일지(거짓 = 행 수).</summary>
    public bool ShowShare { get; init; }

    public string ValueText => ShowShare ? ShareText : CountText;

    public string CountText => Frequency.ToString("N0", CultureInfo.InvariantCulture);

    public string ShareText => (Share * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %";

    /// <summary>같은 막대를 행 수·비율 표시만 바꿔 새로 만든다.</summary>
    public HistogramBarItem WithShare(bool share) => new(Label, Frequency, Fraction, Share) { ShowShare = share };

    /// <summary>버킷을 막대로. 막대 길이는 가장 큰 버킷 대비, 비율은 전체 대비.</summary>
    public static IReadOnlyList<HistogramBarItem> From(IReadOnlyList<HistogramBucket> buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        if (buckets.Count == 0)
        {
            return [];
        }

        var max = Math.Max(1, buckets.Max(b => b.Frequency));
        var total = Math.Max(1, buckets.Sum(b => Math.Max(0, b.Frequency)));
        return buckets
            .Select(b => new HistogramBarItem(
                string.IsNullOrEmpty(b.EndpointValue) ? "#" + b.EndpointNumber.ToString(CultureInfo.InvariantCulture) : b.EndpointValue!,
                b.Frequency,
                (double)Math.Max(0, b.Frequency) / max,
                (double)Math.Max(0, b.Frequency) / total))
            .ToList();
    }
}
