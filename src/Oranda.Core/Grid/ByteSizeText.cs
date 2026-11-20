using System.Globalization;

namespace Oranda.Core.Grid;

/// <summary>
/// 용량(바이트) → 사람이 읽는 글자(SU-1). 1024 단위, 1024 미만은 「512 B」, 그 위는 소수 1자리 고정 「1.5 K」·「12.3 M」·「1.0 G」·「2.4 T」.
/// 화면 표시 전용이다 — 복사·내보내기 값은 원래 숫자를 그대로 쓴다.
/// </summary>
public static class ByteSizeText
{
    private static readonly string[] Units = ["K", "M", "G", "T"];

    /// <summary>바이트 수의 표시 글자. null = 빈 글, 음수 = 앞에 「-」를 붙인 같은 모양.</summary>
    public static string Format(long? bytes) => bytes is { } b ? Text(b) : string.Empty;

    /// <summary>정확한 바이트 수(툴팁용): 「12,897,485 B」. null = 빈 글.</summary>
    public static string Exact(long? bytes)
        => bytes is { } b ? b.ToString("N0", CultureInfo.InvariantCulture) + " B" : string.Empty;

    /// <summary>표 칸 값(정수·실수 아무 숫자형)의 표시 글자. 숫자가 아니면 null(호출 쪽이 원래대로 보인다).</summary>
    public static string? FromValue(object? value) => value switch
    {
        sbyte or byte or short or ushort or int or uint or long => Text(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        ulong u => Text(u),
        float or double or decimal => Text(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        _ => null,
    };

    private static string Text(double bytes)
    {
        var sign = bytes < 0 ? "-" : string.Empty;
        var size = Math.Abs(bytes);
        if (size < 1024)
        {
            return sign + Math.Round(size, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + " B";
        }

        var unit = 0;
        var value = size / 1024;
        // 반올림한 값이 1024.0 이 되면 다음 단위로 올린다(「1024.0 K」 대신 「1.0 M」). T 가 마지막이다.
        while (unit < Units.Length - 1 && Math.Round(value, 1, MidpointRounding.AwayFromZero) >= 1024)
        {
            value /= 1024;
            unit++;
        }

        return sign + value.ToString("0.0", CultureInfo.InvariantCulture) + " " + Units[unit];
    }
}
