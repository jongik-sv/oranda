using System.Globalization;
using System.Text.RegularExpressions;

namespace Oranda.Core.Schema.TableEdit.Partitions.Advisor;

/// <summary>한 문장(sql_id)이 읽은 조건 글. <see cref="Executions"/> = 그 문장의 실행 수 합.</summary>
public sealed record PlanStatement(string SqlId, long Executions, IReadOnlyList<string> Predicates);

/// <summary>
/// 근거 공급자(Oracle·Tibero)가 같이 쓰는 순수 규칙(설계 2026-11-18 5장). DB 를 읽지 않으니 단위 시험으로 본다:
/// 조건 글에서 표 열 찾기, 날짜형 판정, 월별·상위 값 분포 만들기, 표본 비율.
/// </summary>
public static partial class EvidenceProviderRules
{
    /// <summary>날짜형 판정에 읽는 행 수.</summary>
    public const int DateSampleRows = 500;

    /// <summary>날짜형으로 보는 최소 비율(PA-10).</summary>
    public const double DateLikeMinRatio = 0.95;

    /// <summary>월별 분포가 담는 개월 수(지금 달 포함, 거꾸로).</summary>
    public const int MonthsBack = 36;

    /// <summary>월별 분포가 담는 앞날 개월 수. 그보다 먼 앞날(9999-12 같은 끝 날짜)은 「나머지」로 간다.</summary>
    public const int MonthsAhead = 12;

    /// <summary>표본 없이 전체를 읽는 행 수 상한(PA-1).</summary>
    public const long FullScanRows = 200_000;

    /// <summary>표본으로 읽을 목표 행 수(PA-1).</summary>
    public const long TargetSampleRows = 50_000;

    /// <summary>조건을 읽을 문장 수(실행 수 순).</summary>
    public const int MaxStatements = 200;

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND", "OR", "NOT", "IS", "NULL", "IN", "LIKE", "BETWEEN", "ESCAPE", "EXISTS", "ANY", "ALL", "SOME", "CASE", "WHEN", "THEN", "ELSE", "END",
        "AS", "ASC", "DESC", "DATE", "TIMESTAMP", "INTERVAL", "TRUE", "FALSE", "SELECT", "FROM", "WHERE", "ROWID", "ROWNUM", "SYSDATE", "SYSTIMESTAMP",
    };

    /// <summary>
    /// 조건 글(<c>"T"."COL"='X'</c>, <c>"COL"&gt;=:B1</c>, <c>TO_CHAR("T"."COL")</c> …)에서 <paramref name="columns"/> 에 있는 열 이름을 찾는다(사전 이름대로 돌려줌).
    /// 따옴표 이름은 글자 그대로, 따옴표 없는 이름은 대소문자 무시로 비교한다. 문자열 리터럴·바인드 이름·함수 이름·SQL 낱말은 건너뛰고,
    /// <c>별칭.열</c> 은 마지막 마디만 열로 본다.
    /// </summary>
    public static IReadOnlySet<string> ColumnsIn(string? predicate, IReadOnlyCollection<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(predicate) || columns.Count == 0)
        {
            return found;
        }

        var exact = new HashSet<string>(columns, StringComparer.Ordinal);
        var upper = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in columns)
        {
            upper.TryAdd(c, c);
        }

        var text = predicate;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '\'')
            {
                i = SkipLiteral(text, i);
                continue;
            }

            if (ch == ':' && i + 1 < text.Length && (char.IsLetterOrDigit(text[i + 1]) || text[i + 1] == '_'))
            {
                i = SkipWord(text, i + 1); // 바인드 이름
                continue;
            }

            if (ch != '"' && !IsWordStart(ch))
            {
                i++;
                continue;
            }

            // 별칭.열 사슬 하나를 읽는다. 마지막 마디가 열 후보.
            var last = (Name: string.Empty, Quoted: false);
            var next = i;
            var parts = 0;
            while (true)
            {
                string name;
                bool quoted;
                if (next < text.Length && text[next] == '"')
                {
                    var end = text.IndexOf('"', next + 1);
                    if (end < 0)
                    {
                        next = text.Length;
                        break;
                    }

                    name = text[(next + 1)..end];
                    quoted = true;
                    next = end + 1;
                }
                else if (next < text.Length && IsWordStart(text[next]))
                {
                    var end = SkipWord(text, next);
                    name = text[next..end];
                    quoted = false;
                    next = end;
                }
                else
                {
                    break;
                }

                last = (name, quoted);
                parts++;
                if (next < text.Length && text[next] == '.')
                {
                    next++;
                    continue;
                }

                break;
            }

            i = Math.Max(next, i + 1);
            if (parts == 0 || last.Name.Length == 0 || NextNonSpace(text, next) == '(')
            {
                continue; // 함수 이름이거나 읽을 것이 없음
            }

            if (last.Quoted)
            {
                if (exact.Contains(last.Name))
                {
                    found.Add(last.Name);
                }
            }
            else if ((parts > 1 || !Keywords.Contains(last.Name)) && upper.TryGetValue(last.Name, out var column))
            {
                found.Add(column);
            }
        }

        return found;
    }

    /// <summary>
    /// 문장들의 조건 글 → 열마다 쓰인 정도. 한 문장은 열이 여러 번 나와도 한 번(문장 수·실행 수에 한 번씩) 센다.
    /// <see cref="PredicateUse.Share"/> = 읽은 문장 실행 수 합 중 그 열이 나온 문장 실행 수 몫. 실행 수 순(같으면 이름 순)으로 정렬. 쓰이지 않은 열은 빠진다.
    /// </summary>
    public static IReadOnlyList<PredicateUse> BuildPredicateUse(IEnumerable<PlanStatement> statements, IReadOnlyCollection<string> columns)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(columns);
        var perColumn = new Dictionary<string, (long Executions, int Statements)>(StringComparer.Ordinal);
        long total = 0;
        foreach (var statement in statements)
        {
            var executions = Math.Max(0, statement.Executions);
            total += executions;
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var predicate in statement.Predicates)
            {
                used.UnionWith(ColumnsIn(predicate, columns));
            }

            foreach (var column in used)
            {
                var (e, n) = perColumn.GetValueOrDefault(column);
                perColumn[column] = (e + executions, n + 1);
            }
        }

        return [.. perColumn
            .Select(p => new PredicateUse(p.Key, p.Value.Executions, p.Value.Statements, total > 0 ? (double)p.Value.Executions / total : 0))
            .OrderByDescending(u => u.Executions)
            .ThenBy(u => u.Column, StringComparer.Ordinal)];
    }

    /// <summary>
    /// 표본 값(글자 그대로) → 날짜형 판정. 가장 많이 맞는 형식의 비율이 <see cref="DateLikeMinRatio"/> 이상이면 그 형식, 아니면 null.
    /// 빈 값·공백만 있는 값은 세지 않는다. 형식이 맞아도 실제 달력에 있는 날(달)이어야 맞는 것으로 센다.
    /// </summary>
    public static DateLike? DetectDateLike(IEnumerable<string?> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var counts = new Dictionary<DateLikeFormat, int>
        {
            [DateLikeFormat.Yyyymmdd] = 0,
            [DateLikeFormat.Yyyymm] = 0,
            [DateLikeFormat.YyyyDashMmDashDd] = 0,
        };
        var size = 0;
        foreach (var raw in samples)
        {
            var value = raw?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            size++;
            foreach (var format in counts.Keys)
            {
                if (MatchesFormat(value, format))
                {
                    counts[format]++;
                    break;
                }
            }
        }

        if (size == 0)
        {
            return null;
        }

        var best = counts.OrderByDescending(p => p.Value).First();
        var ratio = (double)best.Value / size;
        return ratio >= DateLikeMinRatio ? new DateLike(best.Key, ratio, size) : null;
    }

    /// <summary>값 하나가 그 형식이고 실제 달력에 있는 날(달)인가.</summary>
    public static bool MatchesFormat(string value, DateLikeFormat format)
    {
        ArgumentNullException.ThrowIfNull(value);
        switch (format)
        {
            case DateLikeFormat.Yyyymmdd:
                return value.Length == 8 && value.All(char.IsAsciiDigit)
                    && DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            case DateLikeFormat.Yyyymm:
                return value.Length == 6 && value.All(char.IsAsciiDigit)
                    && DateTime.TryParseExact(value + "01", "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            default:
                return value.Length == 10 && DashDate().IsMatch(value)
                    && DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        }
    }

    /// <summary>
    /// 월별 건수 SQL 이 열에서 <c>YYYY-MM</c> 글을 만드는 식 조각. <paramref name="quotedColumn"/> 은 따옴표 친 열 이름.
    /// DATE·TIMESTAMP(<paramref name="format"/> null) = <c>TO_CHAR(열,'YYYY-MM')</c>, 문자·숫자 날짜형 = 앞 4자 + '-' + 다음 2자(yyyy-MM-dd 는 앞 7자).
    /// </summary>
    public static string MonthLabelExpression(DateLikeFormat? format, string quotedColumn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quotedColumn);
        return format switch
        {
            null => $"TO_CHAR({quotedColumn}, 'YYYY-MM')",
            DateLikeFormat.YyyyDashMmDashDd => $"SUBSTR(TO_CHAR({quotedColumn}), 1, 7)",
            // 이어붙이기(||)는 NULL 을 빈 글로 보므로 NULL 행이 '-' 가 되지 않게 먼저 거른다.
            _ => $"CASE WHEN {quotedColumn} IS NULL THEN NULL ELSE SUBSTR(TO_CHAR({quotedColumn}), 1, 4) || '-' || SUBSTR(TO_CHAR({quotedColumn}), 5, 2) END",
        };
    }

    /// <summary>
    /// 달마다 건수 묶음(SQL 이 <c>YYYY-MM</c> 글로 묶어 줌, NULL 글 = 빈 값) → 분포.
    /// <paramref name="now"/> 달에서 거꾸로 <see cref="MonthsBack"/> 개월 ~ 앞으로 <see cref="MonthsAhead"/> 개월이 버킷(오래된 순),
    /// 그 밖과 달 모양이 아닌 글은 <see cref="ColumnDistribution.RestCount"/>. NULL 은 <see cref="ColumnDistribution.NullCount"/>.
    /// </summary>
    public static ColumnDistribution BuildMonthly(string column, IEnumerable<(string? Label, long Count)> groups, double? samplePercent, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentNullException.ThrowIfNull(groups);
        var first = new DateTime(now.Year, now.Month, 1).AddMonths(-(MonthsBack - 1));
        var last = new DateTime(now.Year, now.Month, 1).AddMonths(MonthsAhead);
        var buckets = new SortedDictionary<string, long>(StringComparer.Ordinal);
        long rest = 0, nulls = 0, rows = 0;
        foreach (var (label, count) in groups)
        {
            rows += count;
            if (string.IsNullOrWhiteSpace(label))
            {
                nulls += count;
            }
            else if (TryMonth(label.Trim(), out var month) && month >= first && month <= last)
            {
                var key = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                buckets[key] = buckets.GetValueOrDefault(key) + count;
            }
            else
            {
                rest += count;
            }
        }

        return new ColumnDistribution(column, DistributionKind.Monthly, [.. buckets.Select(p => new DistributionBucket(p.Key, p.Value))], rows, samplePercent, rest, nulls);
    }

    /// <summary>
    /// 값 묶음(NULL 값은 <paramref name="nullCount"/> 로 따로) → 상위 값 분포. 버킷은 건수 큰 순 앞 <paramref name="top"/> 개,
    /// 나머지(<paramref name="totalRows"/> − NULL − 버킷 합) = <see cref="ColumnDistribution.RestCount"/>.
    /// </summary>
    public static ColumnDistribution BuildTopValues(string column, IEnumerable<(string? Value, long Count)> groups, long totalRows, long nullCount, int top, double? samplePercent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentNullException.ThrowIfNull(groups);
        var buckets = groups
            .Where(g => g.Value is not null)
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Value, StringComparer.Ordinal)
            .Take(Math.Max(0, top))
            .Select(g => new DistributionBucket(g.Value!, g.Count))
            .ToList();
        var rest = Math.Max(0, totalRows - nullCount - buckets.Sum(b => b.Count));
        return new ColumnDistribution(column, DistributionKind.TopValues, buckets, totalRows, samplePercent, rest, nullCount);
    }

    /// <summary>
    /// 표본 비율(%, PA-1): 행 수를 모르거나 <see cref="FullScanRows"/> 이하 = null(표본 없이 전체),
    /// 그 밖 = 목표 행 수 / 행 수 × 100 을 0.01~10 사이로.
    /// </summary>
    public static double? SamplePercentFor(long? numRows)
    {
        if (numRows is not { } rows || rows <= FullScanRows)
        {
            return null;
        }

        return Math.Clamp((double)TargetSampleRows / rows * 100, 0.01, 10);
    }

    /// <summary><c>SAMPLE (p)</c> 절(앞에 공백 하나). 표본이 없으면 빈 글. 소수점은 늘 점.</summary>
    public static string SampleClause(double? samplePercent)
    {
        if (samplePercent is not { } p)
        {
            return string.Empty;
        }

        var bounded = Math.Clamp(p, 0.000001, 99.999999);
        return " SAMPLE (" + bounded.ToString("0.######", CultureInfo.InvariantCulture) + ")";
    }

    private static bool TryMonth(string label, out DateTime month)
    {
        month = default;
        return label.Length == 7 && label[4] == '-'
            && DateTime.TryParseExact(label + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out month);
    }

    private static int SkipLiteral(string text, int start)
    {
        var i = start + 1;
        while (i < text.Length)
        {
            if (text[i] == '\'')
            {
                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i += 2; // '' = 따옴표 문자
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return text.Length;
    }

    private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';

    private static int SkipWord(string text, int start)
    {
        var i = start;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$' or '#'))
        {
            i++;
        }

        return i;
    }

    private static char NextNonSpace(string text, int from)
    {
        for (var i = from; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return text[i];
            }
        }

        return '\0';
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DashDate();
}
