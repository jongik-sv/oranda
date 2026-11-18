namespace Oranda.Core.Schema.TableEdit.Partitions.Advisor;

/// <summary>
/// 분포 표본 비율(설계 PA-1). 행 수 ≤ 20만(또는 모름) = 표본 없이 전체, 그 밖 = 약 5만 행이 되게 <c>SAMPLE(p)</c>, p 는 0.01~10.
/// </summary>
public static class EvidenceSampling
{
    /// <summary>표본 없이 전체를 읽는 행 수 상한.</summary>
    public const long FullScanRows = 200_000;

    /// <summary>표본 목표 행 수.</summary>
    public const long TargetRows = 50_000;

    public const double MinPercent = 0.01;

    public const double MaxPercent = 10;

    /// <summary>표본 비율(%). null = 표본 없이 전체.</summary>
    public static double? PercentFor(long? numRows)
    {
        if (numRows is not { } rows || rows <= FullScanRows)
        {
            return null;
        }

        var p = (double)TargetRows / rows * 100;
        return Math.Round(Math.Clamp(p, MinPercent, MaxPercent), 4);
    }

    /// <summary>분포 열 하나에서 읽을 행 수 어림(확인 창 문구). 행 수를 모르면 null.</summary>
    public static long? PlannedRows(long? numRows, double? samplePercent = null)
    {
        if (numRows is not { } rows)
        {
            return null;
        }

        var p = samplePercent ?? PercentFor(rows);
        return p is { } percent ? (long)Math.Round(rows * percent / 100) : rows;
    }
}
