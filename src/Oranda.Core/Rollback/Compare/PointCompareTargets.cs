using System.Globalization;

namespace Oranda.Core.Rollback.Compare;

/// <summary>
/// 목표 값 행 하나(「이 행 상태로 되돌리기」): 결과 그리드에서 읽은 표 열 이름 → 값(.NET 값). 결과에 없는 열은 들어 있지 않다.
/// 키가 ROWID 인 표는 <c>ROWID</c> 열 값(글)로 짝짓는다.
/// </summary>
public sealed record CompareTargetRow(IReadOnlyDictionary<string, object?> Values);

/// <summary>
/// 목표 값의 출처가 시점이 아니라 결과 행일 때의 분류(플래시백 결과 행 → 화면 3). 기준(B) = 목표 행, 지금(N) = DB 에서 읽은 행으로
/// <see cref="PointComparePlanner.Classify"/> 의 두 시점 비교를 그대로 쓴다: 지금 있고 다르면 UPDATE, 지금 없으면 INSERT, 같으면 할 일 없음.
/// 그리드 값과 읽는 식(고정 형식 글)의 모양이 달라 두 쪽 값을 같은 모양(<see cref="Normalize"/>)으로 맞춘 뒤 견준다.
/// </summary>
public static class PointCompareTargets
{
    public const string RowIdColumn = "ROWID";

    /// <summary>
    /// 목표 행과 지금 행(<paramref name="now"/>, 표 열 순서)을 견준다. 되돌리는 열은 목표 행에 있는 열뿐이다.
    /// 지금 없는 행을 넣어야 하는데 결과에 빠진 열이 있으면 그 행은 만들지 않고 <paramref name="notes"/> 에 이유를 남긴다.
    /// </summary>
    public static ComparePlan Plan(RollbackTable table, IReadOnlyList<CompareTargetRow> targets, IReadOnlyList<CompareSnapshotRow> now, out IReadOnlyList<string> notes)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(now);
        var cols = table.Columns;
        var keyPos = PointComparePlanner.KeyPositions(table);
        var nowRows = now.Select(r => r with { Values = [.. r.Values.Select((v, i) => Normalize(cols[i].Kind, v))] }).ToList();
        var nowByKey = new Dictionary<string, CompareSnapshotRow>(StringComparer.Ordinal);
        foreach (var r in nowRows)
        {
            if (KeyOf(keyPos, r) is { } k)
            {
                nowByKey[k] = r;
            }
        }

        var present = new HashSet<int>();
        var skippedLtz = new SortedSet<string>(StringComparer.Ordinal);
        var baseRows = new List<CompareSnapshotRow>(targets.Count);
        foreach (var t in targets)
        {
            var values = new object?[cols.Count];
            string? rowId = null;
            if (keyPos.Count == 0 && t.Values.TryGetValue(RowIdColumn, out var rid) && rid is not (null or DBNull))
            {
                rowId = Convert.ToString(rid, CultureInfo.InvariantCulture);
            }

            for (var i = 0; i < cols.Count; i++)
            {
                if (cols[i].Kind != RollbackValueKind.Large && t.Values.TryGetValue(cols[i].Name, out var v))
                {
                    if (cols[i].Kind == RollbackValueKind.TimestampLtz && v is DateTime && !keyPos.Contains(i))
                    {
                        skippedLtz.Add(cols[i].Name); // 그리드 값은 세션 시간대의 벽시계라 UTC 로 바꿀 수 없다(바꾸면 시각이 어긋난 값을 쓴다)
                        continue;
                    }

                    values[i] = Normalize(cols[i].Kind, v);
                    present.Add(i);
                }
            }

            var row = new CompareSnapshotRow(values) { RowId = rowId };
            if (KeyOf(keyPos, row) is { } key && nowByKey.TryGetValue(key, out var current))
            {
                for (var i = 0; i < cols.Count; i++)
                {
                    if (!present.Contains(i))
                    {
                        values[i] = current.Values[i]; // 결과에 없는 열은 지금 값 그대로(바꾸지 않는다)
                    }
                }
            }

            baseRows.Add(row);
        }

        var revert = Enumerable.Range(0, cols.Count)
            .Where(i => present.Contains(i) && !keyPos.Contains(i) && !cols[i].IsVirtual && cols[i].Kind != RollbackValueKind.Large)
            .ToArray();
        var plan = PointComparePlanner.Classify(table, baseRows, null, nowRows, revert);

        var list = new List<string>();
        var missing = cols.Where((c, i) => !present.Contains(i) && !c.IsVirtual && c.Kind != RollbackValueKind.Large && !keyPos.Contains(i)).Select(c => c.Name).ToList();
        // 목표 행의 키로만 지금 행을 읽으므로 「지움」은 생길 수 없다. 키 모양이 어긋나 생겼다면 지우지 않는다(지운 뒤 INSERT 실패 방지).
        var rows = plan.Rows.Where(r => r.Kind != CompareKind.Remove).ToList();
        if (skippedLtz.Count > 0)
        {
            list.Add($"열({string.Join(", ", skippedLtz)})은 TIMESTAMP WITH LOCAL TIME ZONE 이라 그리드 값으로 되돌리지 않습니다(지금 값 그대로).");
        }

        if (keyPos.Count == 0 && rows.Any(r => r.Kind != CompareKind.Same))
        {
            list.Add("ROWID 로 짝짓습니다. 지운 행의 ROWID 는 다른 행이 다시 쓸 수 있으니 미리 보기에서 키 값을 확인하세요.");
        }

        if (missing.Count > 0 && rows.RemoveAll(r => r.Kind == CompareKind.Restore) is var dropped and > 0)
        {
            list.Add($"지금 없는 행 {dropped.ToString(CultureInfo.InvariantCulture)}개는 INSERT 해야 하는데 결과에 열({string.Join(", ", missing)})이 없어 만들지 않았습니다. 모든 열을 조회한 결과에서 쓰세요.");
        }

        var large = cols.Where(c => c.Kind == RollbackValueKind.Large && !c.IsVirtual).Select(c => c.Name).ToList();
        if (large.Count > 0 && rows.Any(r => r.Kind == CompareKind.Restore))
        {
            list.Add($"큰 열({string.Join(", ", large)})은 되살리지 않습니다(INSERT 에서 빠져 NULL 이나 기본값이 됩니다).");
        }

        notes = list;
        var warnings = plan.Warnings.Where(w => w != PointComparePlanner.NoCommitPointWarning).ToList(); // 시점이 아니라 결과 행과 견준다
        return plan with { Rows = rows, Warnings = warnings };
    }

    /// <summary>
    /// 값을 견줄 모양으로 맞춘다: 그리드의 .NET 값과 읽는 식(설계 6.6, 고정 형식 글)으로 읽은 값이 같은 값이면 같은 것이 되게 한다.
    /// 숫자는 decimal, 날짜·시각은 마스크 모양 글(앞뒤 공백 없음), 간격은 <c>+일 시:분:초.소수7</c>·<c>+년-월</c>, RAW 는 대문자 16진 글.
    /// 결과는 방언 리터럴(<see cref="IRollbackDialect.Literal"/>)이 그대로 받는다. 바꿀 수 없는 값은 그대로 둔다(리터럴에서 걸러진다).
    /// </summary>
    public static object? Normalize(RollbackValueKind kind, object? value)
    {
        var inv = CultureInfo.InvariantCulture;
        if (value is null or DBNull)
        {
            return null;
        }

        return (kind, value) switch
        {
            (RollbackValueKind.Number, decimal d) => d,
            (RollbackValueKind.Number, int or long or short or byte or sbyte or ushort or uint or ulong) => Convert.ToDecimal(value, inv),
            (RollbackValueKind.Number, double d) when double.IsFinite(d) => (decimal)d,
            (RollbackValueKind.Number, float f) when float.IsFinite(f) => (decimal)f,
            (RollbackValueKind.Number, string s) => Digits(s) <= 28 && decimal.TryParse(s.Trim(), NumberStyles.Float, inv, out var n) ? n : s.Trim(), // 28자리를 넘으면 decimal 이 반올림한다
            (RollbackValueKind.BinaryFloat or RollbackValueKind.BinaryDouble, string s) => double.TryParse(s.Trim(), NumberStyles.Float, inv, out var f) ? f : s,
            (RollbackValueKind.Date, DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm:ss", inv),
            (RollbackValueKind.Date, DateTimeOffset o) => o.DateTime.ToString("yyyy-MM-dd HH:mm:ss", inv),
            (RollbackValueKind.Timestamp, DateTime dt) => Fraction(dt),
            (RollbackValueKind.Timestamp, DateTimeOffset o) => Fraction(o.DateTime),
            (RollbackValueKind.TimestampTz, DateTimeOffset o) => Fraction(o.DateTime) + " " + o.ToString("zzz", inv),
            (RollbackValueKind.TimestampLtz, DateTimeOffset o) => Fraction(o.UtcDateTime),
            (RollbackValueKind.TimestampLtz, DateTime dt) when dt.Kind == DateTimeKind.Utc => Fraction(dt),
            (RollbackValueKind.Date or RollbackValueKind.Timestamp or RollbackValueKind.TimestampTz or RollbackValueKind.TimestampLtz, string s) => s.Trim(),
            (RollbackValueKind.IntervalDaySecond, TimeSpan ts) => DaySecond(ts),
            (RollbackValueKind.IntervalDaySecond, string s) => ParseDaySecond(s) is { } ts ? DaySecond(ts) : s.Trim(),
            (RollbackValueKind.IntervalYearMonth, _) => YearMonth(Convert.ToString(value, inv) ?? string.Empty),
            (RollbackValueKind.Raw, byte[] b) => Convert.ToHexString(b),
            (RollbackValueKind.Raw, string s) => s.Trim().ToUpperInvariant(),
            (RollbackValueKind.Text or RollbackValueKind.NationalText or RollbackValueKind.RowId, not string) => Convert.ToString(value, inv),
            _ => value,
        };
    }

    private static int Digits(string s) => s.Count(char.IsAsciiDigit);

    private static string? KeyOf(IReadOnlyList<int> keyPos, CompareSnapshotRow row)
        => keyPos.Count == 0
            ? row.RowId is { Length: > 0 } rid ? "r:" + rid : null
            : PointComparePlanner.KeyText([.. keyPos.Select(p => row.Values[p])]);

    private static string Fraction(DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "00";

    private static string DaySecond(TimeSpan ts)
    {
        var sign = ts < TimeSpan.Zero ? "-" : "+";
        var a = ts.Duration();
        return sign + a.Days.ToString(CultureInfo.InvariantCulture) + " " + a.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture);
    }

    /// <summary><c>TO_CHAR(INTERVAL DAY TO SECOND)</c> 모양(<c>+000000001 02:03:04.500000</c>) → TimeSpan. 모르는 모양이면 null.</summary>
    private static TimeSpan? ParseDaySecond(string text)
    {
        var s = text.Trim();
        var negative = s.StartsWith('-');
        s = s.TrimStart('+', '-');
        var parts = s.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var days))
        {
            return null;
        }

        var clock = parts[1].Split(':');
        if (clock.Length != 3 || !int.TryParse(clock[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(clock[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
            || !decimal.TryParse(clock[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var sec))
        {
            return null;
        }

        var ticks = (((days * 24L) + h) * 60L + m) * TimeSpan.TicksPerMinute + (long)Math.Round(sec * TimeSpan.TicksPerSecond);
        return TimeSpan.FromTicks(negative ? -ticks : ticks);
    }

    /// <summary><c>+000000001-02</c> → <c>+1-02</c>. 모르는 모양이면 앞뒤 공백만 뗀다.</summary>
    private static string YearMonth(string text)
    {
        var s = text.Trim();
        var sign = s.StartsWith('-') ? "-" : "+";
        var parts = s.TrimStart('+', '-').Split('-');
        return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var y)
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var mo)
            ? sign + y.ToString(CultureInfo.InvariantCulture) + "-" + mo.ToString("00", CultureInfo.InvariantCulture)
            : s;
    }
}
