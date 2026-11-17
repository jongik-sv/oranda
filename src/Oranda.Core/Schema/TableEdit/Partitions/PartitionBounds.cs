using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Oranda.Core.Schema.TableEdit.Partitions;

/// <summary>파티션 키 열 하나의 값 종류(경계를 보통 값으로 쓰고 읽는 데 쓴다). 키가 여러 열이거나 모르는 형식이면 <see cref="Other"/>.</summary>
public enum PartitionKeyKind
{
    Other,
    Date,
    Number,
    Text,
}

/// <summary>자동 추가(INTERVAL) 단위. <see cref="Custom"/> = 고를 수 없는 식(직접 쓴 식·숫자 간격).</summary>
public enum IntervalUnit
{
    Off,
    Day,
    Month,
    Year,
    Custom,
}

/// <summary>
/// 파티션 경계를 사람이 쓰는 보통 값과 SQL 사이에서 바꾸고(설계 18.4 쉬운 편집), 다음 파티션·이름·자동 추가 식을 짐작한다.
/// 보통 값: 날짜 <c>2026-12-01</c>(시각이 있으면 <c>2026-12-01 13:00:00</c>), 수 <c>1000</c>, 글 <c>A</c>, 목록은 쉼표로 잇는다. <c>MAXVALUE</c>·<c>DEFAULT</c>·<c>NULL</c> 은 그대로.
/// SQL: 날짜 = <c>DATE 'YYYY-MM-DD'</c>(시각이 있으면 <c>TO_DATE('…', 'YYYY-MM-DD HH24:MI:SS')</c>), 글 = 작은따옴표. Oracle·Tibero 공통(Tibero 실측 11).
/// 이미 SQL 꼴(따옴표·괄호·DATE 등)로 쓴 값은 그대로 둔다 — 직접 쓴 식을 깨지 않는다.
/// </summary>
public static partial class PartitionBounds
{
    /// <summary>열 형식 → 키 종류.</summary>
    public static PartitionKeyKind KindOf(string? dataType)
    {
        var t = (dataType ?? string.Empty).Trim().ToUpperInvariant();
        return t.StartsWith("DATE", StringComparison.Ordinal) || t.StartsWith("TIMESTAMP", StringComparison.Ordinal) ? PartitionKeyKind.Date
            : t is "NUMBER" or "INTEGER" or "INT" or "SMALLINT" or "FLOAT" or "DECIMAL" or "NUMERIC" or "BINARY_FLOAT" or "BINARY_DOUBLE" ? PartitionKeyKind.Number
            : t.Contains("CHAR", StringComparison.Ordinal) ? PartitionKeyKind.Text
            : PartitionKeyKind.Other;
    }

    /// <summary>SQL 경계 → 보통 값(목록은 항목마다). 모르는 꼴은 그대로.</summary>
    public static string ToPlain(string? bound)
    {
        if (string.IsNullOrWhiteSpace(bound))
        {
            return string.Empty;
        }

        return string.Join(", ", SplitItems(bound).Select(PlainItem));
    }

    /// <summary>보통 값 → SQL 경계(목록은 항목마다). 빈 글 = null.</summary>
    public static string? ToSql(string? plain, PartitionKeyKind kind)
    {
        if (string.IsNullOrWhiteSpace(plain))
        {
            return null;
        }

        return string.Join(", ", SplitItems(plain).Select(i => SqlItem(i, kind)));
    }

    /// <summary>보통 값 날짜(경계 하나)를 읽는다. 날짜가 아니면 null.</summary>
    public static DateTime? AsDate(string? bound) =>
        DateTime.TryParseExact(ToPlain(bound), ["yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>보통 값 수(경계 하나)를 읽는다. 수가 아니면 null.</summary>
    public static decimal? AsNumber(string? bound) =>
        decimal.TryParse(ToPlain(bound), NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>
    /// 범위 파티션의 다음 줄(「＋ 다음 파티션」): 마지막 두 경계의 간격(날짜 = 달·날, 수 = 차)을 이어 이름과 경계를 만든다.
    /// 경계가 하나뿐이면 날짜는 매달 1일이면 한 달, 아니면 하루. 짐작할 수 없으면(MAXVALUE·식) null.
    /// </summary>
    public static (string Name, string Bound)? NextRange(IReadOnlyList<string?> bounds)
    {
        var known = bounds.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        if (known.Count == 0)
        {
            return null;
        }

        var last = known[^1];
        var prev = known.Count > 1 ? known[^2] : null;
        if (AsDate(last) is { } lastDate)
        {
            var step = DateStep(prev is null ? null : AsDate(prev), lastDate);
            var next = step.Months > 0 ? lastDate.AddMonths(step.Months) : lastDate.AddDays(step.Days);
            return (DateName(lastDate, step), DateSql(next));
        }

        if (AsNumber(last) is { } lastNumber && prev is not null && AsNumber(prev) is { } prevNumber && lastNumber > prevNumber)
        {
            var next = lastNumber + (lastNumber - prevNumber);
            return (NumberName(lastNumber), Number(next));
        }

        return null;
    }

    /// <summary>
    /// 범위 경계로 파티션 이름 짓기: 구간 시작(= 앞 경계)으로 <c>P_YYYY</c>·<c>P_YYYY_MM</c>·<c>P_YYYY_MM_DD</c>, 수는 <c>P_&lt;시작&gt;</c>.
    /// 앞 경계가 없으면 <c>P_BEFORE_…</c>(경계 미만 전부). 목록은 첫 값. 지을 수 없으면 null.
    /// </summary>
    public static string? SuggestName(string? previousBound, string? bound, PartitionMethod method)
    {
        if (string.IsNullOrWhiteSpace(bound))
        {
            return null;
        }

        if (method == PartitionMethod.List)
        {
            var first = PlainItem(SplitItems(bound)[0]);
            var label = Regex.Replace(first.ToUpperInvariant(), "[^A-Z0-9]+", "_").Trim('_');
            return label.Length == 0 ? null : "P_" + (label.Length > 24 ? label[..24] : label);
        }

        if (AsDate(bound) is { } end)
        {
            if (AsDate(previousBound) is { } start)
            {
                return DateName(start, DateStep(start, end));
            }

            return "P_BEFORE_" + end.ToString(end.Day == 1 ? "yyyy_MM" : "yyyy_MM_dd", CultureInfo.InvariantCulture);
        }

        if (AsNumber(bound) is { } n)
        {
            return AsNumber(previousBound) is { } p ? NumberName(p) : "P_BEFORE_" + Label(n);
        }

        return null;
    }

    /// <summary>자동 추가 식 → 단위. 비면 Off, 고를 수 없는 식은 Custom.</summary>
    public static IntervalUnit UnitOf(string? interval)
    {
        if (string.IsNullOrWhiteSpace(interval))
        {
            return IntervalUnit.Off;
        }

        var t = Regex.Replace(interval.ToUpperInvariant(), @"\s+", string.Empty);
        return t switch
        {
            "NUMTODSINTERVAL(1,'DAY')" => IntervalUnit.Day,
            "NUMTOYMINTERVAL(1,'MONTH')" => IntervalUnit.Month,
            "NUMTOYMINTERVAL(1,'YEAR')" or "NUMTOYMINTERVAL(12,'MONTH')" => IntervalUnit.Year,
            _ => IntervalUnit.Custom,
        };
    }

    /// <summary>단위 → 자동 추가 식(Off = null, Custom 은 식을 만들 수 없어 null).</summary>
    public static string? IntervalOf(IntervalUnit unit) => unit switch
    {
        IntervalUnit.Day => "NUMTODSINTERVAL(1, 'DAY')",
        IntervalUnit.Month => "NUMTOYMINTERVAL(1, 'MONTH')",
        IntervalUnit.Year => "NUMTOYMINTERVAL(1, 'YEAR')",
        _ => null,
    };

    /// <summary>날짜 경계들의 간격으로 자동 추가 단위를 짐작한다(매달 1일 = 월, 매년 1월 1일 = 년, 그 밖 = 일). 날짜가 아니면 Month.</summary>
    public static IntervalUnit GuessUnit(IReadOnlyList<string?> bounds)
    {
        var dates = bounds.Select(AsDate).Where(d => d is not null).Select(d => d!.Value).ToList();
        if (dates.Count == 0)
        {
            return IntervalUnit.Month;
        }

        var step = DateStep(dates.Count > 1 ? dates[^2] : null, dates[^1]);
        return step.Months >= 12 && step.Months % 12 == 0 ? IntervalUnit.Year : step.Months > 0 ? IntervalUnit.Month : IntervalUnit.Day;
    }

    /// <summary>쉬운 바꾸기(일반 → 파티션)의 첫 경계: 오늘이 든 기간의 시작(일 = 오늘, 월 = 이번 달 1일, 년 = 올해 1월 1일).</summary>
    public static DateTime PeriodStart(DateTime today, IntervalUnit unit) => unit switch
    {
        IntervalUnit.Day => today.Date,
        IntervalUnit.Year => new DateTime(today.Year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
        _ => new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Unspecified),
    };

    /// <summary>날짜 → SQL 경계.</summary>
    public static string DateSql(DateTime d) => d.TimeOfDay == TimeSpan.Zero
        ? "DATE '" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'"
        : "TO_DATE('" + d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "', 'YYYY-MM-DD HH24:MI:SS')";

    private static (int Months, int Days) DateStep(DateTime? prev, DateTime last)
    {
        if (prev is { } p && p < last)
        {
            if (p.Day == 1 && last.Day == 1 && p.TimeOfDay == TimeSpan.Zero && last.TimeOfDay == TimeSpan.Zero)
            {
                return (((last.Year - p.Year) * 12) + last.Month - p.Month, 0);
            }

            return (0, Math.Max(1, (int)(last.Date - p.Date).TotalDays));
        }

        return last.Day == 1 ? (1, 0) : (0, 1);
    }

    private static string DateName(DateTime start, (int Months, int Days) step) =>
        "P_" + start.ToString(step.Months >= 12 && step.Months % 12 == 0 && start.Month == 1 ? "yyyy" : step.Months > 0 ? "yyyy_MM" : "yyyy_MM_dd", CultureInfo.InvariantCulture);

    private static string NumberName(decimal start) => "P_" + Label(start);

    private static string Label(decimal n) => Number(n).Replace("-", "M", StringComparison.Ordinal).Replace('.', '_');

    private static string Number(decimal n) => n.ToString("0.############################", CultureInfo.InvariantCulture);

    private static string PlainItem(string item)
    {
        var t = item.Trim();
        if (DateLiteral().Match(t) is { Success: true } m)
        {
            var date = m.Groups["d"].Value;
            var time = m.Groups["t"].Value;
            return time.Length == 0 || time == "00:00:00" ? date : date + " " + time;
        }

        if (ToDateCall().Match(t) is { Success: true } call && NetDateFormat(call.Groups["m"].Value) is { } format
            && TryParseDate(call.Groups["v"].Value.Trim(), format, out var parsed))
        {
            return parsed.TimeOfDay == TimeSpan.Zero
                ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : parsed.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        if (t.Length >= 2 && t[0] == '\'' && t[^1] == '\'')
        {
            var inner = t[1..^1];
            // 쉼표·따옴표가 든 글은 따옴표를 두어야 다시 SQL 로 돌릴 때 같은 값이 된다.
            return inner.Contains(',', StringComparison.Ordinal) || inner.Contains('\'', StringComparison.Ordinal) || inner.Trim() != inner || inner.Length == 0 ? t : inner;
        }

        return t;
    }

    private static string SqlItem(string item, PartitionKeyKind kind)
    {
        var t = item.Trim();
        var upper = t.ToUpperInvariant();
        if (upper is "MAXVALUE" or "DEFAULT" or "NULL" || t.StartsWith('\'') || t.Contains('(', StringComparison.Ordinal)
            || upper.StartsWith("DATE ", StringComparison.Ordinal) || upper.StartsWith("TIMESTAMP ", StringComparison.Ordinal))
        {
            return t; // 이미 SQL 꼴
        }

        switch (kind)
        {
            case PartitionKeyKind.Date when DateTime.TryParseExact(t, ["yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM", "yyyy"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d):
                return DateSql(d);
            case PartitionKeyKind.Text:
                return "'" + t.Replace("'", "''", StringComparison.Ordinal) + "'";
            default:
                return t;
        }
    }

    /// <summary>맨 바깥 쉼표로 나눈다(따옴표·괄호 안의 쉼표는 두고).</summary>
    private static List<string> SplitItems(string text)
    {
        var items = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '\'')
            {
                quoted = !quoted;
            }
            else if (!quoted && c == '(')
            {
                depth++;
            }
            else if (!quoted && c == ')')
            {
                depth--;
            }
            else if (!quoted && depth == 0 && c == ',')
            {
                items.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        items.Add(current.ToString());
        return items;
    }

    /// <summary>
    /// 날짜 형식 글(<c>SYYYY-MM-DD HH24:MI:SS</c>·<c>DD-MM-YYYY HH24:mi:ss</c>·<c>YYYYMMDD</c> 등) → .NET 형식. 연·월·일·시(24)·분·초와 구분 글자(<c>- / : . 공백</c>)만 안다.
    /// 그 밖의 요소(<c>MON</c>·<c>HH12</c>·<c>AM</c> 등)가 있으면 null(경계를 그대로 둔다).
    /// </summary>
    private static (string Full, string DateOnly)? NetDateFormat(string mask)
    {
        var format = new StringBuilder();
        var m = mask.ToUpperInvariant();
        var dateOnly = -1;
        var parts = 0;
        var i = 0;
        while (i < m.Length)
        {
            var (token, net) = m.AsSpan(i) switch
            {
                var r when r.StartsWith("SYYYY") => ("SYYYY", "yyyy"),
                var r when r.StartsWith("YYYY") => ("YYYY", "yyyy"),
                var r when r.StartsWith("HH24") => ("HH24", "HH"),
                var r when r.StartsWith("MM") => ("MM", "MM"),
                var r when r.StartsWith("DD") => ("DD", "dd"),
                var r when r.StartsWith("MI") => ("MI", "mm"),
                var r when r.StartsWith("SS") => ("SS", "ss"),
                var r when "-/:. ".Contains(r[0], StringComparison.Ordinal) => (r[..1].ToString(), "\\" + r[0]),
                _ => (string.Empty, string.Empty),
            };
            if (token.Length == 0)
            {
                return null;
            }

            if (token == "HH24" && dateOnly < 0)
            {
                dateOnly = format.Length;
            }

            parts |= token switch { "SYYYY" or "YYYY" => 1, "MM" => 2, "DD" => 4, _ => 0 };
            format.Append(net);
            i += token.Length;
        }

        // 연·월·일이 다 있어야 한다: 빠진 요소를 서버는 이번 해·이번 달·1일로, .NET 은 1월·오늘로 채워 값이 달라진다.
        if (parts != 7)
        {
            return null;
        }

        var full = format.ToString();
        return (full, dateOnly < 0 ? full : full[..dateOnly].TrimEnd('\\', ' '));
    }

    /// <summary>형식대로 읽고, 안 되면 시각 앞까지만(서버는 형식보다 짧은 값 <c>TO_DATE('2026-11-01', 'YYYY-MM-DD HH24:MI:SS')</c> 도 받는다).</summary>
    private static bool TryParseDate(string value, (string Full, string DateOnly) format, out DateTime parsed) =>
        DateTime.TryParseExact(value, format.Full, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
        || (format.DateOnly != format.Full
            && DateTime.TryParseExact(value, format.DateOnly, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed));

    /// <summary>사전·문장의 날짜 리터럴 경계: <c>TIMESTAMP' 2026-10-01 00:00:00'</c>, <c>DATE '2026-10-01'</c>.</summary>
    [GeneratedRegex(@"^(?:TIMESTAMP|DATE)\s*'\s*(?<d>\d{4}-\d{2}-\d{2})(?:\s+(?<t>\d{2}:\d{2}:\d{2})(?:\.0+)?)?\s*'$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DateLiteral();

    /// <summary>
    /// <c>TO_DATE('값', '형식'[, 'NLS…'])</c>: Oracle 사전 <c>TO_DATE(' 2026-10-01 00:00:00', 'SYYYY-MM-DD HH24:MI:SS', 'NLS_CALENDAR=GREGORIAN')</c>,
    /// Tibero 사전(만든 문장 글 그대로, INTERVAL 로 서버가 만든 파티션은 <c>TO_DATE('01-02-2027 00:00:00', 'DD-MM-YYYY HH24:mi:ss')</c>).
    /// </summary>
    [GeneratedRegex(@"^TO_DATE\(\s*'(?<v>[^']*)'\s*,\s*'(?<m>[^']*)'\s*(?:,\s*'[^']*'\s*)?\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ToDateCall();
}
