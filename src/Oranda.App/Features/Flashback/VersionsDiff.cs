using System.Globalization;
using Oranda.Language.Editing;

namespace Oranda.App.Features.Flashback;

/// <summary>
/// VERSIONS 결과의 열 역할. 모든 번호는 결과 열 번호(ordinal)다. 없는 열은 -1.
/// <see cref="Data"/> 는 앞 버전과 견줄 데이터 열(의사열·키 열을 뺀 나머지)이다.
/// </summary>
public sealed record VersionsLayout(IReadOnlyList<int> Keys, int StartScn, int StartTime, int Operation, IReadOnlyList<int> Data);

/// <summary>
/// 「이 행 변경 이력」 등 <c>VERSIONS BETWEEN</c> 결과에서 같은 키 행을 시간 순으로 늘어놓고 바로 앞 버전과 값이 다른 데이터 칸을 찾는다.
/// 시간 순 = <c>VERSIONS_STARTSCN</c>, 없으면 <c>VERSIONS_STARTTIME</c>(NULL = 기간 앞부터 있던 가장 오래된 버전), 둘 다 없으면 결과 순서.
/// 키의 첫 버전·삭제(D) 버전은 칠하지 않는다. 행 수 N 에 대해 정렬 한 번(N log N)과 칸 비교 한 번.
/// </summary>
public static partial class VersionsDiff
{
    /// <summary>VERSIONS 의사열(<c>VERSIONS_STARTSCN</c> 등)·<c>ORA_ROWSCN</c>·<c>ROWID</c> 류인가.</summary>
    public static bool IsPseudo(string? name) =>
        name is not null
        && (name.StartsWith("VERSIONS_", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ORA_ROWSCN", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ROWID", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ROWIDTOCHAR(ROWID)", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 열 역할을 정한다. <paramref name="sources"/> 는 결과 열마다 분석기가 아는 원래 열 이름(별칭을 붙인 단순 열 참조면 그 열, 식이면 null) —
    /// 모르면 null(결과 열 이름만 본다). <paramref name="keys"/> 는 키 열 번호(비면 결과 전체를 한 키로 본다).
    /// </summary>
    public static VersionsLayout Layout(IReadOnlyList<string> names, IReadOnlyList<string?>? sources, IReadOnlyList<int> keys)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(keys);
        int scn = -1, time = -1, op = -1;
        var data = new List<int>();
        for (var i = 0; i < names.Count; i++)
        {
            var source = sources is not null && i < sources.Count ? sources[i] : null;
            var role = Role(source) ?? Role(names[i]);
            switch (role)
            {
                case "VERSIONS_STARTSCN" when scn < 0:
                    scn = i;
                    continue;
                case "VERSIONS_STARTTIME" when time < 0:
                    time = i;
                    continue;
                case "VERSIONS_OPERATION" when op < 0:
                    op = i;
                    continue;
            }

            if (role is not null || IsPseudo(names[i]) || IsPseudo(source) || keys.Contains(i))
            {
                continue;
            }

            data.Add(i);
        }

        return new VersionsLayout(keys, scn, time, op, data);

        static string? Role(string? n) => IsPseudo(n) ? n!.ToUpperInvariant() : null;
    }

    /// <summary>
    /// 행마다 앞 버전과 값이 다른 데이터 열 번호. <paramref name="rows"/> 는 결과 순서(원래 행 번호)의 행 값이며 아직 읽지 못한 행은 null(건너뛴다).
    /// 바뀐 칸이 없는 행은 결과에 넣지 않는다. <paramref name="newestFirst"/> = 결과가 최신순인가(문장의 ORDER BY 로 안다면). null 이면 값으로 짐작한다
    /// (서로 다른 첫 두 시각, 그것도 없으면 NULL(가장 오래된 버전)이 끝에 몰렸는가). 같은 시각끼리·시계 열이 없을 때 앞뒤를 이 방향으로 정한다.
    /// </summary>
    public static IReadOnlyDictionary<int, int[]> Changed(VersionsLayout layout, IReadOnlyList<object?[]?> rows, bool? newestFirst = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(rows);
        var result = new Dictionary<int, int[]>();
        foreach (var list in Ordered(layout, rows, newestFirst))
        {
            for (var n = 1; n < list.Count; n++)
            {
                var cur = rows[list[n]]!;
                if (layout.Operation >= 0 && IsDelete(cur[layout.Operation]))
                {
                    continue; // 삭제 버전: 값은 지우기 앞 그대로라 칠하지 않는다
                }

                var prev = rows[list[n - 1]]!;
                var changed = layout.Data.Where(d => !FlashbackDiff.SameValue(prev[d], cur[d])).ToArray();
                if (changed.Length > 0)
                {
                    result[list[n]] = changed;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 같은 키 행끼리 묶어 시간 순(가장 오래된 버전 먼저)으로 늘어놓은 원래 행 번호 목록. 키 묶음은 결과에 처음 나온 차례다.
    /// 「차이 요약」(AI)이 앞 버전 값과 함께 읽는다. 읽지 못한 행(null)은 빠진다.
    /// </summary>
    public static IReadOnlyList<List<int>> Ordered(VersionsLayout layout, IReadOnlyList<object?[]?> rows, bool? newestFirst = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(rows);
        var clock = layout.StartScn >= 0 ? layout.StartScn : layout.StartTime;
        var descending = newestFirst ?? (clock >= 0 && IsDescending(rows, clock));

        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var order = new List<List<int>>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i] is not { } row)
            {
                continue;
            }

            var key = layout.Keys.Count == 0 ? string.Empty : FlashbackDiff.KeyText(layout.Keys.Select(k => row[k]));
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = [];
                order.Add(list);
            }

            list.Add(i);
        }

        foreach (var list in order)
        {
            if (clock >= 0)
            {
                // 시간 순(NULL 이 가장 앞). 같은 시각이면 결과 정렬 방향을 따른다(최신순 결과면 뒤 행이 앞 버전).
                list.Sort((a, b) => Compare(rows[a]![clock], rows[b]![clock]) is var c && c != 0 ? c : descending ? b.CompareTo(a) : a.CompareTo(b));
            }
            else if (descending)
            {
                list.Reverse();
            }
        }

        return order;
    }

    private static bool IsDelete(object? op) =>
        op is not (null or DBNull) && string.Equals(Convert.ToString(op, CultureInfo.InvariantCulture)?.Trim(), "D", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 결과가 시간 내림차순(최신순)인가: 값이 있는 첫 두 다른 시각을 결과 순서로 견준다. 다른 시각이 없으면
    /// NULL 시각(가장 오래된 버전)이 값 있는 행보다 모두 뒤에 있을 때 최신순으로 본다.
    /// </summary>
    private static bool IsDescending(IReadOnlyList<object?[]?> rows, int clock)
    {
        object? first = null;
        int firstNull = -1, lastValue = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row is null)
            {
                continue;
            }

            if (row[clock] is null or DBNull)
            {
                firstNull = firstNull < 0 ? i : firstNull;
                continue;
            }

            lastValue = i;

            if (first is null)
            {
                first = row[clock];
                continue;
            }

            var c = Compare(first, row[clock]);
            if (c != 0)
            {
                return c > 0;
            }
        }

        return firstNull > lastValue && lastValue >= 0;
    }

    /// <summary>문장의 ORDER BY 첫 항목이 VERSIONS_STARTSCN·VERSIONS_STARTTIME 이면 그 방향(DESC = 최신순), 아니면 null.</summary>
    public static bool? OrderNewestFirst(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var m = OrderBy().Match(text);
        return m.Success ? m.Groups["dir"].Value.Equals("DESC", StringComparison.OrdinalIgnoreCase) : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\bORDER\s+BY\s+(?:\w+\.)?""?VERSIONS_START(?:SCN|TIME)""?(?:\s+(?<dir>ASC|DESC))?\b",System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex OrderBy();

    /// <summary>시각·SCN 비교. NULL 이 가장 앞. 숫자는 값으로, 날짜는 시각으로, 나머지는 같은 형식이면 CompareTo, 아니면 글로.</summary>
    internal static int Compare(object? a, object? b)
    {
        var an = a is null or DBNull;
        var bn = b is null or DBNull;
        if (an || bn)
        {
            return an == bn ? 0 : an ? -1 : 1;
        }

        if (IsNumber(a!) && IsNumber(b!))
        {
            return a is double or float || b is double or float
                ? Convert.ToDouble(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(b, CultureInfo.InvariantCulture))
                : Convert.ToDecimal(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(b, CultureInfo.InvariantCulture));
        }

        if (Instant(a!) is { } x && Instant(b!) is { } y)
        {
            return x.CompareTo(y);
        }

        return a!.GetType() == b!.GetType() && a is IComparable ca
            ? ca.CompareTo(b)
            : string.CompareOrdinal(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture));
    }

    private static bool IsNumber(object v) => v is decimal or int or long or short or byte or sbyte or ushort or uint or ulong or double or float;

    private static DateTime? Instant(object v) => v switch
    {
        DateTime t => t,
        DateTimeOffset o => o.UtcDateTime,
        _ => null,
    };
}
