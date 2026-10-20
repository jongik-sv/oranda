using System.Globalization;

namespace Oranda.Language.Editing;

/// <summary>
/// 지금 값과 N분 전 값을 칸마다 견주는 계산(TSK-13-05). 행은 키 값으로 짝짓는다(정렬·필터와 무관).
/// </summary>
public static class FlashbackDiff
{
    /// <summary>키 값 목록 → 짝짓기용 글. 숫자는 문화권과 무관하게 같은 글이 된다.</summary>
    public static string KeyText(IEnumerable<object?> keyValues)
    {
        ArgumentNullException.ThrowIfNull(keyValues);
        return string.Join('\u0001', keyValues.Select(Text));
    }

    /// <summary>두 행에서 값이 다른 칸 번호(0 부터). 길이는 짧은 쪽에 맞춘다.</summary>
    public static IReadOnlyList<int> ChangedColumns(IReadOnlyList<object?> now, IReadOnlyList<object?> past)
    {
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(past);
        var changed = new List<int>();
        for (var i = 0; i < Math.Min(now.Count, past.Count); i++)
        {
            if (!SameValue(now[i], past[i]))
            {
                changed.Add(i);
            }
        }

        return changed;
    }

    /// <summary>NULL·DBNull 은 같고, 숫자는 값으로, 바이트 배열은 내용으로 견준다. 나머지는 글로 견준다.</summary>
    public static bool SameValue(object? a, object? b)
    {
        var an = a is null or DBNull;
        var bn = b is null or DBNull;
        if (an || bn)
        {
            return an && bn;
        }

        if (a is byte[] x && b is byte[] y)
        {
            return x.AsSpan().SequenceEqual(y);
        }

        return string.Equals(Text(a), Text(b), StringComparison.Ordinal);
    }

    private static string Text(object? v) => v switch
    {
        null or DBNull => string.Empty,
        byte[] b => Convert.ToHexString(b),
        decimal d => d.ToString("G29", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("R", CultureInfo.InvariantCulture),
        DateTime t => t.Ticks.ToString(CultureInfo.InvariantCulture),
        DateTimeOffset o => o.UtcTicks.ToString(CultureInfo.InvariantCulture),
        IFormattable f => NormalizeNumber(f),
        _ => v.ToString() ?? string.Empty,
    };

    private static string NormalizeNumber(IFormattable f) =>
        f is int or long or short or byte or sbyte or ushort or uint or ulong
            ? Convert.ToDecimal(f, CultureInfo.InvariantCulture).ToString("G29", CultureInfo.InvariantCulture)
            : f.ToString(null, CultureInfo.InvariantCulture);
}
