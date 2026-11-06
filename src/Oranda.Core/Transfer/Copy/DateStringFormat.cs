using System.Globalization;
using System.Text;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 문자열 열에 저장된 날짜의 형식. 토큰은 <c>YYYY</c>, <c>MM</c>, <c>DD</c>, <c>HH24</c>, <c>MI</c>, <c>SS</c> 만 받는다.
/// <c>MON</c> 처럼 언어에 따라 순서가 틀어지는 토큰은 받지 않는다.
/// </summary>
public static class DateStringFormat
{
    public static IReadOnlyList<string> KnownFormats { get; } =
        ["YYYYMMDD", "YYYYMMDDHH24MISS", "YYYY-MM-DD", "YYYY-MM-DD HH24:MI:SS", "YYYYMM"];

    private static readonly string[] Tokens = ["YYYY", "HH24", "MM", "DD", "MI", "SS"];
    private const string Separators = "-/:. ";

    public static string? GuessFromLength(int length) => length switch
    {
        6 => "YYYYMM",
        8 => "YYYYMMDD",
        10 => "YYYY-MM-DD",
        14 => "YYYYMMDDHH24MISS",
        19 => "YYYY-MM-DD HH24:MI:SS",
        _ => null,
    };

    private static readonly string[] Order = ["YYYY", "MM", "DD", "HH24", "MI", "SS"];

    /// <summary>
    /// 토큰이 <c>YYYY → MM → DD → HH24 → MI → SS</c> 순서로 앞에서부터 건너뛰지 않고 한 번씩만 나와야 한다.
    /// 문자열 비교가 날짜 순서와 같아야 하므로 <c>DD/MM/YYYY</c> 같은 형식과 연도만 있는 형식은 받지 않는다.
    /// </summary>
    public static bool IsValid(string format)
    {
        if (string.IsNullOrEmpty(format)) return false;
        var next = 0;
        foreach (var part in Parse(format))
        {
            if (part is null) return false;
            if (part.Length == 1 && Separators.Contains(part[0])) continue;
            if (next >= Order.Length || part != Order[next]) return false;
            next++;
        }
        return next >= 2;
    }

    public static bool HasDay(string format) => format.Contains("DD", StringComparison.Ordinal);

    public static string Format(DateTime value, string format)
    {
        var sb = new StringBuilder();
        foreach (var part in ParseOrThrow(format))
        {
            sb.Append(part switch
            {
                "YYYY" => value.Year.ToString("0000", CultureInfo.InvariantCulture),
                "MM" => value.Month.ToString("00", CultureInfo.InvariantCulture),
                "DD" => value.Day.ToString("00", CultureInfo.InvariantCulture),
                "HH24" => value.Hour.ToString("00", CultureInfo.InvariantCulture),
                "MI" => value.Minute.ToString("00", CultureInfo.InvariantCulture),
                "SS" => value.Second.ToString("00", CultureInfo.InvariantCulture),
                _ => part,
            });
        }
        return sb.ToString();
    }

    /// <summary>
    /// 기간 경계를 형식 문자열로 바꾼다. 일 단위가 없는 형식(<c>YYYYMM</c>)은 달 경계로 맞춘다:
    /// 시작은 그 달, 끝은 끝 경계가 달의 1일 0시면 그 달 아니면 다음 달이다.
    /// 시작 또는 끝이 없으면 해당 값은 빈 문자열이다.
    /// </summary>
    public static (string From, string To) Bounds(ResolvedPeriod period, string format)
    {
        var from = period.From is { } f ? Format(f, format) : "";
        string to;
        if (period.To is not { } t) to = "";
        else if (!HasDay(format) && !(t.Day == 1 && t.TimeOfDay == TimeSpan.Zero))
            to = Format(new DateTime(t.Year, t.Month, 1).AddMonths(1), format);
        else to = Format(t, format);
        return (from, to);
    }

    private static IEnumerable<string> ParseOrThrow(string format)
    {
        foreach (var part in Parse(format))
            yield return part ?? throw new FormatException($"지원하지 않는 날짜 형식: {format}");
    }

    // 알 수 없는 글자를 만나면 null 을 하나 내고 멈춘다.
    private static IEnumerable<string?> Parse(string format)
    {
        var i = 0;
        while (i < format.Length)
        {
            string? token = null;
            foreach (var t in Tokens)
            {
                if (string.CompareOrdinal(format, i, t, 0, t.Length) == 0) { token = t; break; }
            }
            if (token is not null) { yield return token; i += token.Length; continue; }
            if (Separators.Contains(format[i])) { yield return format[i].ToString(); i++; continue; }
            yield return null;
            yield break;
        }
    }
}
