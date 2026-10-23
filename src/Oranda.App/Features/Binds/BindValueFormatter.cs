using System.Globalization;
using Oranda.Core.Execution;

namespace Oranda.App.Features.Binds;

/// <summary>
/// 바인드 값 칸의 자료형별 정리와 검사(TSK-03-09). 칸을 떠날 때 <see cref="Normalize"/> 로 글을 고치고,
/// 못 읽는 값은 <see cref="Validate"/> 가 이유를 돌려준다(실행 때 막는 동작은 <see cref="BindInput.ToBindValue"/> 가 그대로 맡는다).
/// 빈 글은 NULL 이라 늘 그대로 둔다.
/// </summary>
public static class BindValueFormatter
{
    /// <summary>날짜·TIMESTAMP 의 기본 표시 서식(오라클 <c>YYYY-MM-DD HH24:MI:SS</c>).</summary>
    public const string DefaultDateFormat = "yyyy-MM-dd HH:mm:ss";

    private static readonly string[] DateInputs =
    [
        "yyyy-MM-dd", "yyyy-M-d", "yyyyMMdd", "yyyy/MM/dd", "yyyy/M/d", "yyyy.MM.dd",
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm",
        "yyyy/MM/dd HH:mm", "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm:ss.FFFFFFF", "yyyy.MM.dd HH:mm:ss", "yyyy.MM.dd HH:mm",
        "yyyyMMddHHmm", "yyyyMMddHHmmss", "yyyyMMdd HHmmss", "yyyyMMdd HH:mm:ss", "yyyyMMdd HH:mm",
    ];

    /// <summary>자료형에 맞게 정리한 글. 못 정리하면(숫자 아님 등) 원래 글 그대로.</summary>
    public static string Normalize(BindDataType type, string text, string? dateFormat = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return text;
        }

        switch (type)
        {
            case BindDataType.Number or BindDataType.BinaryDouble:
                {
                    var compact = StripNumber(text);
                    return TryNumber(compact) ? compact : text;
                }

            case BindDataType.Date or BindDataType.Timestamp:
                {
                    if (!TryDate(text, out var value))
                    {
                        return text;
                    }

                    var format = string.IsNullOrWhiteSpace(dateFormat) ? DefaultDateFormat : dateFormat;
                    var shown = value.ToString(format, CultureInfo.InvariantCulture);
                    var frac = value.Ticks % TimeSpan.TicksPerSecond;
                    if (type == BindDataType.Timestamp && frac != 0)
                    {
                        shown += "." + frac.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
                    }

                    return shown;
                }

            default:
                return text;
        }
    }

    /// <summary>값을 읽을 수 없는 이유(툴팁용). 읽을 수 있으면 null.</summary>
    public static string? Validate(BindDataType type, BindDirection direction, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || direction == BindDirection.Out)
        {
            return null;
        }

        return type switch
        {
            BindDataType.Number or BindDataType.BinaryDouble => TryNumber(StripNumber(text)) ? null : $"숫자가 아닙니다: '{text}'",
            BindDataType.Date or BindDataType.Timestamp => TryDate(text, out _) ? null : $"날짜를 읽을 수 없습니다: '{text}' (예: 2026-10-23, 20261023, 2026/10/23 13:05)",
            _ => null,
        };
    }

    private static string StripNumber(string text) =>
        text.Replace(",", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal).Trim();

    private static bool TryNumber(string compact) =>
        double.TryParse(compact, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) && !double.IsInfinity(v);

    private static bool TryDate(string text, out DateTime value) =>
        DateTime.TryParseExact(text.Trim(), DateInputs, CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
        || DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
}
