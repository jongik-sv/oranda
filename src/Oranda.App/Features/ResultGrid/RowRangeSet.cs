namespace Oranda.App.Features.ResultGrid;

/// <summary>
/// 행 번호 구간 목록 계산(양끝 포함). 목록은 늘 번호순이고 서로 겹치거나 맞닿지 않는다 —
/// 1만 행을 골라도 구간이 하나라 더하기·빼기·찾기가 행 수가 아니라 구간 수에 비례한다.
/// </summary>
internal static class RowRangeSet
{
    /// <summary>구간들을 번호순으로 정렬하고 겹치거나 이어진 것을 합친다. 0 ~ <paramref name="rowCount"/>-1 밖은 잘라 낸다.</summary>
    public static List<(long First, long Last)> Normalize(IEnumerable<(long First, long Last)> ranges, long rowCount)
    {
        var sorted = ranges.Select(r => (First: Math.Max(0, Math.Min(r.First, r.Last)), Last: Math.Min(rowCount - 1, Math.Max(r.First, r.Last))))
            .Where(r => r.First <= r.Last).OrderBy(r => r.First).ToList();
        var result = new List<(long First, long Last)>(sorted.Count);
        foreach (var r in sorted)
        {
            if (result.Count > 0 && r.First <= result[^1].Last + 1)
            {
                result[^1] = (result[^1].First, Math.Max(result[^1].Last, r.Last));
            }
            else
            {
                result.Add(r);
            }
        }

        return result;
    }

    public static List<(long First, long Last)> Add(IEnumerable<(long First, long Last)> ranges, long first, long last) =>
        Normalize(ranges.Append((first, last)), long.MaxValue);

    public static bool Contains(IReadOnlyList<(long First, long Last)> ranges, long row)
    {
        var lo = 0;
        var hi = ranges.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (row < ranges[mid].First)
            {
                hi = mid - 1;
            }
            else if (row > ranges[mid].Last)
            {
                lo = mid + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>한 행을 뺀다(구간 가운데면 둘로 나뉜다).</summary>
    public static List<(long First, long Last)> Remove(IEnumerable<(long First, long Last)> ranges, long row)
    {
        var result = new List<(long First, long Last)>();
        foreach (var r in ranges)
        {
            if (row < r.First || row > r.Last)
            {
                result.Add(r);
                continue;
            }

            if (r.First < row)
            {
                result.Add((r.First, row - 1));
            }

            if (row < r.Last)
            {
                result.Add((row + 1, r.Last));
            }
        }

        return result;
    }
}
