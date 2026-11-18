using System.Globalization;
using System.Text;
using Oranda.Core.Text;

namespace Oranda.Core.Schema.TableEdit.Partitions;

/// <summary>월 경계 글의 꼴(월 추가 스크립트가 같은 꼴로 다음 경계를 쓴다).</summary>
public enum MonthBoundFormat
{
    /// <summary><c>DATE 'YYYY-MM-01'</c>(사전의 <c>TO_DATE(…)</c> 도 읽음).</summary>
    Date,

    /// <summary>글 <c>'YYYYMMDD'</c>.</summary>
    TextYyyymmdd,

    /// <summary>글 <c>'YYYYMM'</c>.</summary>
    TextYyyymm,

    /// <summary>글 <c>'YYYY-MM-DD'</c>.</summary>
    TextDashed,

    /// <summary>수 <c>YYYYMMDD</c>.</summary>
    NumberYyyymmdd,

    /// <summary>수 <c>YYYYMM</c>.</summary>
    NumberYyyymm,
}

/// <summary>월 추가 한 줄: <c>SPLIT PARTITION 최대 AT (Bound) INTO (PARTITION Name, PARTITION 최대)</c>.</summary>
public sealed record MonthSplit(string Name, string Bound);

/// <summary>월 추가 계획: 나눌 MAXVALUE 파티션과 새 달 줄(이른 달부터).</summary>
public sealed record MonthAddPlan(string MaxPartition, MonthBoundFormat Format, IReadOnlyList<MonthSplit> Months);

/// <summary>
/// 월 추가 스크립트(파티션 추천 설계 PA-5): 자동 추가(INTERVAL)를 못 쓰는 범위 키(날짜형 문자열·숫자, 또는 INTERVAL 을 끈 날짜)는 마지막 MAXVALUE 파티션을
/// 달마다 나눠 다음 달 파티션을 만든다. 조건 = 범위 · 키 열 하나 · INTERVAL 없음 · 마지막 줄 MAXVALUE · 그 앞 경계가 달 첫날(꼴은 <see cref="MonthBoundFormat"/>).
/// 문장 = <c>ALTER TABLE o.t SPLIT PARTITION P_MAX AT (다음 경계) INTO (PARTITION P_YYYYMM, PARTITION P_MAX) UPDATE INDEXES</c>. 실행하지 않는 스크립트 글로만 낸다.
/// </summary>
public static class PartitionMonthScript
{
    /// <summary>기본 달 수(앞으로 12개월).</summary>
    public const int DefaultMonths = 12;

    /// <summary>계획. 조건이 안 맞으면 null. 새 이름은 앞 파티션 이름 꼴(<c>P_2026_01</c>·<c>P_202601</c>)을 따르고, 모르면 <c>P_YYYYMM</c>. 이미 있는 이름은 건너뛴다.</summary>
    public static MonthAddPlan? Plan(PartitionTableDraft table, int months = DefaultMonths)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(months);
        if (table.Scheme is not { Method: PartitionMethod.Range, KeyColumns.Count: 1 } scheme || !string.IsNullOrWhiteSpace(scheme.Interval))
        {
            return null;
        }

        var rows = table.Partitions.Where(p => !p.IsDropped && p.MergedInto is null).ToList();
        if (rows.Count < 2 || PartitionDiff.Canonical(rows[^1].Bound) != "MAXVALUE" || Parse(rows[^2].Bound) is not { } last)
        {
            return null;
        }

        var (start, format) = last;
        var naming = NameFormat(rows[^2].Name, start.AddMonths(-1));
        var taken = rows.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var list = new List<MonthSplit>(months);
        for (var i = 0; i < months; i++)
        {
            var month = start.AddMonths(i);
            var name = naming.Prefix + month.ToString(naming.Format, CultureInfo.InvariantCulture);
            if (taken.Add(name))
            {
                list.Add(new MonthSplit(name, Write(month.AddMonths(1), format)));
            }
        }

        return list.Count == 0 ? null : new MonthAddPlan(rows[^1].Name, format, list);
    }

    /// <summary>
    /// 문장 목록(끝 <c>;</c> 없음). 방언이 나누기 뒤 전역 인덱스를 남기지 못하면(Tibero) 끝에 <paramref name="globalIndexes"/> 의 <c>ALTER INDEX … REBUILD</c>.
    /// </summary>
    public static IReadOnlyList<string> Statements(string owner, string table, MonthAddPlan plan, ITableEditDialect dialect, IReadOnlyList<string>? globalIndexes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dialect);
        string Q(string name) => SqlIdentifier.Quote(name, dialect.IsReservedWord);
        var target = Q(owner) + "." + Q(table);
        var result = plan.Months
            .Select(m => $"ALTER TABLE {target} SPLIT PARTITION {Q(plan.MaxPartition)} AT ({m.Bound}) INTO (PARTITION {Q(m.Name)}, PARTITION {Q(plan.MaxPartition)}) UPDATE INDEXES")
            .ToList();
        if (!dialect.KeepsGlobalIndexesOnSplitMerge)
        {
            result.AddRange((globalIndexes ?? []).Select(i => $"ALTER INDEX {Q(owner)}.{Q(i)} REBUILD"));
        }

        return result;
    }

    /// <summary>저장할 스크립트 글(머리 주석 + 문장마다 <c>;</c>).</summary>
    public static string Script(string owner, string table, MonthAddPlan plan, ITableEditDialect dialect, IReadOnlyList<string>? globalIndexes = null)
    {
        var statements = Statements(owner, table, plan, dialect, globalIndexes);
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"-- 월 추가: {owner}.{table}  ({plan.Months[0].Name} ~ {plan.Months[^1].Name}, {plan.Months.Count}개월)");
        sb.AppendLine("-- 이 키는 자동 추가(INTERVAL)를 쓸 수 없어 마지막 MAXVALUE 파티션을 달마다 나눈다. 검토한 뒤 직접 실행한다(달마다 또는 몇 달 앞서).");
        sb.AppendLine(CultureInfo.InvariantCulture, $"-- 나누는 동안 {plan.MaxPartition} 의 행을 옮긴다(MAXVALUE 파티션에 행이 많으면 오래 걸린다).");
        sb.AppendLine();
        foreach (var s in statements)
        {
            sb.Append(s).AppendLine(";");
        }

        return sb.ToString();
    }

    /// <summary>경계 글 → 달 첫날과 꼴. 달 첫날이 아니거나 모르는 꼴이면 null.</summary>
    internal static (DateTime Month, MonthBoundFormat Format)? Parse(string? bound)
    {
        var text = bound?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (PartitionBounds.AsDate(text) is { } date && !text.StartsWith('\''))
        {
            return date.Day == 1 && date.TimeOfDay == TimeSpan.Zero ? (date, MonthBoundFormat.Date) : null;
        }

        var quoted = text.Length > 1 && text[0] == '\'' && text[^1] == '\'';
        var value = quoted ? text[1..^1] : text;
        (string Pattern, MonthBoundFormat Format)[] forms = quoted
            ? [("yyyyMMdd", MonthBoundFormat.TextYyyymmdd), ("yyyyMM", MonthBoundFormat.TextYyyymm), ("yyyy-MM-dd", MonthBoundFormat.TextDashed)]
            : [("yyyyMMdd", MonthBoundFormat.NumberYyyymmdd), ("yyyyMM", MonthBoundFormat.NumberYyyymm)];
        foreach (var (pattern, format) in forms)
        {
            if (value.Length == pattern.Length && DateTime.TryParseExact(value, pattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                return d.Day == 1 ? (d, format) : null;
            }
        }

        return null;
    }

    private static string Write(DateTime month, MonthBoundFormat format) => format switch
    {
        MonthBoundFormat.Date => PartitionBounds.DateSql(month),
        MonthBoundFormat.TextYyyymmdd => "'" + month.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "'",
        MonthBoundFormat.TextYyyymm => "'" + month.ToString("yyyyMM", CultureInfo.InvariantCulture) + "'",
        MonthBoundFormat.TextDashed => "'" + month.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'",
        MonthBoundFormat.NumberYyyymmdd => month.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        _ => month.ToString("yyyyMM", CultureInfo.InvariantCulture),
    };

    /// <summary>앞 파티션 이름이 그 달(<paramref name="month"/>)을 <c>yyyy_MM</c>·<c>yyyyMM</c> 로 끝내면 같은 앞머리·꼴, 아니면 <c>P_</c> + <c>yyyyMM</c>.</summary>
    private static (string Prefix, string Format) NameFormat(string previousName, DateTime month)
    {
        foreach (var format in new[] { "yyyy_MM", "yyyyMM" })
        {
            var tail = month.ToString(format, CultureInfo.InvariantCulture);
            if (previousName.EndsWith(tail, StringComparison.Ordinal) && previousName.Length > tail.Length)
            {
                return (previousName[..^tail.Length], format);
            }
        }

        return ("P_", "yyyyMM");
    }
}
