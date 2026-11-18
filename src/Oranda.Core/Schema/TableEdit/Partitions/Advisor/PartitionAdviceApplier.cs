using System.Globalization;
using System.Text.RegularExpressions;

namespace Oranda.Core.Schema.TableEdit.Partitions.Advisor;

/// <summary>
/// 채우기 결과. <see cref="Draft"/> 를 파티션 편집기에 넣는다.
/// 하위 파티션 템플릿은 <see cref="PartitionScheme"/> 에 바꾸기용 모델이 생기기 전까지 <see cref="Subpartition"/> 에 따로 담는다(값은 되돌린 실제 값, pa-script 머지 뒤 모델로 옮김).
/// </summary>
/// <param name="LocalIndexes">LOCAL 로 만들 인덱스(PA-12)</param>
/// <param name="Problems">채우면서 생긴 확인(✖ = <see cref="AdviceCheckCodes.ValueUnknown"/> 등, 있으면 채우지 않음)</param>
public sealed record AdviceDraft(
    PartitionTableDraft Draft,
    SubpartitionAdvice? Subpartition,
    IReadOnlyList<string> LocalIndexes,
    IReadOnlyList<AdviceCheck> Problems);

/// <summary>
/// 후보 → 파티션 편집 모델(설계 4.2). 줄 키는 새 줄(<see cref="DraftKeys.New"/>).
/// <list type="bullet">
/// <item>RANGE + DATE·TIMESTAMP + 월·일·년 간격: 줄 하나(<c>P_BEFORE_…</c>, 분포의 가장 이른 기간 다음 경계, 분포가 없으면 쉬운 설정처럼 이번 기간 시작) + INTERVAL.</item>
/// <item>RANGE + 날짜형 문자열·숫자(또는 간격 없는 DATE): 분포 첫 기간부터 이번 기간 + <see cref="PartitionLayout.PeriodsAhead"/>(기본 12)까지 기간마다 한 줄 + <c>P_MAX</c>(MAXVALUE).</item>
/// <item>RANGE + <see cref="PartitionLayout.Bounds"/>: 그대로 줄로(<c>P1</c>…, MAXVALUE = <c>P_MAX</c>), <c>number:n</c> 간격 = INTERVAL n.</item>
/// <item>LIST: 묶음마다 한 줄, 기본 묶음 = <c>DEFAULT</c>. 자리표시자는 금고로 되돌리고, 못 되돌리면 ✖.</item>
/// <item>HASH: 개수만큼 <c>P1</c>…<c>Pn</c>(개수가 없으면 4).</item>
/// </list>
/// </summary>
public static partial class PartitionAdviceApplier
{
    /// <summary>개수가 없을 때 HASH 파티션 수.</summary>
    public const int DefaultHashCount = 4;

    /// <summary>INTERVAL 없는 범위에서 오늘 뒤로 만드는 기본 기간 수(PA-10).</summary>
    public const int DefaultPeriodsAhead = 12;

    /// <summary>한 번에 만드는 줄 수 상한(분포가 아주 오래된 기간부터일 때).</summary>
    public const int MaxRows = 1000;

    /// <param name="vault">프롬프트를 만들 때의 금고(LIST 값 되돌리기)</param>
    /// <param name="today">「오늘」(앞으로 N 기간·첫 경계 기준)</param>
    public static AdviceDraft Apply(PartitionCandidate candidate, PartitionEvidence evidence, EvidenceValueVault vault, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(vault);

        if (AdviceMethod.ToMethod(candidate.Method) is not { } method)
        {
            return Fail(new AdviceCheck(AdviceCheckLevel.Stop, AdviceCheckCodes.MethodUnknown, candidate.Method));
        }

        if (candidate.KeyColumns.Count == 0)
        {
            return Fail(new AdviceCheck(AdviceCheckLevel.Stop, AdviceCheckCodes.KeyMissing));
        }

        var keys = new List<EvidenceColumn>();
        foreach (var name in candidate.KeyColumns)
        {
            if (evidence.Column(name) is not { } column)
            {
                return Fail(new AdviceCheck(AdviceCheckLevel.Stop, AdviceCheckCodes.KeyMissing, name));
            }

            keys.Add(column);
        }

        var problems = new List<AdviceCheck>();
        var scheme = new PartitionScheme(method, [.. keys.Select(k => k.Name)]);
        List<PartitionDraft> rows;
        switch (method)
        {
            case PartitionMethod.Hash:
                rows = [.. Enumerable.Range(1, Math.Max(1, candidate.Layout.HashCount ?? DefaultHashCount)).Select(i => Row("P" + Invariant(i), null))];
                break;
            case PartitionMethod.List:
                rows = [.. (candidate.Layout.Groups ?? []).Select(g => Row(g.Name, ListBound(g, keys[0], vault, problems)))];
                break;
            default:
                (rows, var interval, var problem) = RangeRows(candidate, keys[0], evidence, today);
                if (problem is not null)
                {
                    return Fail(problem);
                }

                scheme = scheme with { Interval = interval };
                break;
        }

        var sub = candidate.Subpartition is { } sp ? Unmask(sp, evidence, vault, problems) : null;
        return new AdviceDraft(new PartitionTableDraft(scheme, rows), sub, [.. candidate.LocalIndexes], problems);
    }

    private static AdviceDraft Fail(AdviceCheck problem) => new(PartitionTableDraft.None, null, [], [problem]);

    private static PartitionDraft Row(string name, string? bound) => new(DraftKeys.New(), name) { Bound = bound };

    private static string Invariant(int i) => i.ToString(CultureInfo.InvariantCulture);

    // ---- RANGE

    private static (List<PartitionDraft> Rows, string? Interval, AdviceCheck? Problem) RangeRows(PartitionCandidate c, EvidenceColumn key, PartitionEvidence evidence, DateOnly today)
    {
        var interval = c.Interval?.Trim().ToLowerInvariant();
        if (c.Layout.Bounds is { Count: > 0 } bounds || string.Equals(c.Layout.Kind, AdviceLayoutKind.NumberRanges, StringComparison.OrdinalIgnoreCase))
        {
            var given = c.Layout.Bounds ?? [];
            var numberStep = interval is not null && interval.StartsWith(AdviceInterval.NumberPrefix, StringComparison.Ordinal)
                ? interval[AdviceInterval.NumberPrefix.Length..].Trim()
                : null;
            var listed = given.Select((b, i) => IsMaxValue(b) ? Row("P_MAX", "MAXVALUE") : Row("P" + Invariant(i + 1), b.Trim())).ToList();
            return (listed, string.IsNullOrEmpty(numberStep) ? null : numberStep, null);
        }

        var unit = UnitOf(interval, c.Layout.Kind);
        var isDate = PartitionBounds.KindOf(key.DataType) == PartitionKeyKind.Date;
        if (!isDate && key.DateLike is null)
        {
            return ([], null, new AdviceCheck(AdviceCheckLevel.Stop, AdviceCheckCodes.KeyType, key.Name));
        }

        if (key.DateLike?.Format == DateLikeFormat.Yyyymm && unit == IntervalUnit.Day)
        {
            unit = IntervalUnit.Month; // YYYYMM 은 하루 단위가 없다
        }

        var todayStart = PartitionBounds.PeriodStart(today.ToDateTime(TimeOnly.MinValue), unit);
        DateTime? earliest = EarliestMonth(evidence, key.Name) is { } m ? PartitionBounds.PeriodStart(m, unit) : null;

        if (isDate && interval is AdviceInterval.Month or AdviceInterval.Day or AdviceInterval.Year)
        {
            var bound = earliest is { } e ? Shift(e, unit, 1) : todayStart;
            return ([Row("P_BEFORE_" + NamePart(bound, unit), PartitionBounds.DateSql(bound))], PartitionBounds.IntervalOf(unit), null);
        }

        var start = earliest ?? todayStart;
        var last = Shift(todayStart, unit, Math.Max(0, c.Layout.PeriodsAhead ?? DefaultPeriodsAhead));
        var result = new List<PartitionDraft>();
        for (var p = start; p <= last && result.Count < MaxRows; p = Shift(p, unit, 1))
        {
            result.Add(Row("P_" + NamePart(p, unit), BoundText(Shift(p, unit, 1), key)));
        }

        result.Add(Row("P_MAX", "MAXVALUE"));
        return (result, null, null);
    }

    private static IntervalUnit UnitOf(string? interval, string? layoutKind) => interval switch
    {
        AdviceInterval.Day => IntervalUnit.Day,
        AdviceInterval.Year => IntervalUnit.Year,
        AdviceInterval.Month => IntervalUnit.Month,
        _ => layoutKind?.Trim().ToLowerInvariant() switch
        {
            AdviceLayoutKind.Daily => IntervalUnit.Day,
            AdviceLayoutKind.Yearly => IntervalUnit.Year,
            _ => IntervalUnit.Month,
        },
    };

    private static DateTime Shift(DateTime d, IntervalUnit unit, int amount) => unit switch
    {
        IntervalUnit.Day => d.AddDays(amount),
        IntervalUnit.Year => d.AddYears(amount),
        _ => d.AddMonths(amount),
    };

    private static string NamePart(DateTime d, IntervalUnit unit) =>
        d.ToString(unit == IntervalUnit.Year ? "yyyy" : unit == IntervalUnit.Day ? "yyyy_MM_dd" : "yyyy_MM", CultureInfo.InvariantCulture);

    /// <summary>경계 글: DATE 열 = <c>DATE '…'</c>, 날짜형 = 형식대로(문자열은 따옴표, 숫자는 그대로).</summary>
    private static string BoundText(DateTime bound, EvidenceColumn key)
    {
        if (key.DateLike is not { } dateLike || PartitionBounds.KindOf(key.DataType) == PartitionKeyKind.Date)
        {
            return PartitionBounds.DateSql(bound);
        }

        var text = bound.ToString(
            dateLike.Format switch
            {
                DateLikeFormat.Yyyymm => "yyyyMM",
                DateLikeFormat.YyyyDashMmDashDd => "yyyy-MM-dd",
                _ => "yyyyMMdd",
            },
            CultureInfo.InvariantCulture);
        return PartitionBounds.KindOf(key.DataType) == PartitionKeyKind.Number ? text : "'" + text + "'";
    }

    /// <summary>그 열 월별 분포의 가장 이른 달(<c>YYYY-MM</c> 꼬리표). 없으면 null.</summary>
    private static DateTime? EarliestMonth(PartitionEvidence evidence, string column) =>
        evidence.Distributions
            .Where(d => d.Kind == DistributionKind.Monthly && string.Equals(d.Column, column, StringComparison.OrdinalIgnoreCase))
            .SelectMany(d => d.Buckets)
            .Select(b => DateTime.TryParseExact(b.Label, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m) ? m : (DateTime?)null)
            .Where(m => m is not null)
            .Min();

    private static bool IsMaxValue(string bound) => string.Equals(bound.Trim(), "MAXVALUE", StringComparison.OrdinalIgnoreCase);

    // ---- LIST

    private static string ListBound(ListGroup group, EvidenceColumn key, EvidenceValueVault vault, List<AdviceCheck> problems)
    {
        if (group.IsDefault)
        {
            return "DEFAULT";
        }

        var kind = PartitionBounds.KindOf(key.DataType);
        return string.Join(", ", RealValues(group.Values ?? [], key.Name, vault, problems).Select(v => Literal(v, kind)));
    }

    private static string Literal(string value, PartitionKeyKind kind)
    {
        if (string.Equals(value, "NULL", StringComparison.OrdinalIgnoreCase))
        {
            return "NULL";
        }

        return kind switch
        {
            PartitionKeyKind.Number => value,
            PartitionKeyKind.Date => PartitionBounds.ToSql(value, PartitionKeyKind.Date) ?? value,
            _ => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'",
        };
    }

    /// <summary>자리표시자를 실제 값으로. 금고가 비었으면(값 보내기 켬) 그대로. 못 되돌린 자리표시자는 ✖ 하나로 모은다.</summary>
    private static List<string> RealValues(IReadOnlyList<string> values, string column, EvidenceValueVault vault, List<AdviceCheck> problems)
    {
        var real = new List<string>(values.Count);
        var unknown = new List<string>();
        foreach (var v in values)
        {
            if (vault.Count > 0 && Placeholder().IsMatch(v))
            {
                if (vault.Unmask(column, v) is { } value)
                {
                    real.Add(value);
                }
                else
                {
                    unknown.Add(v.Trim());
                }
            }
            else
            {
                real.Add(v);
            }
        }

        if (unknown.Count > 0)
        {
            problems.Add(new AdviceCheck(AdviceCheckLevel.Stop, AdviceCheckCodes.ValueUnknown, column, unknown));
        }

        return real;
    }

    private static SubpartitionAdvice Unmask(SubpartitionAdvice sp, PartitionEvidence evidence, EvidenceValueVault vault, List<AdviceCheck> problems)
    {
        if (sp.Groups is not { Count: > 0 } groups || sp.KeyColumns.Count == 0)
        {
            return sp;
        }

        var column = evidence.Column(sp.KeyColumns[0])?.Name ?? sp.KeyColumns[0];
        return sp with { Groups = [.. groups.Select(g => g with { Values = g.IsDefault ? [] : RealValues(g.Values ?? [], column, vault, problems) })] };
    }

    [GeneratedRegex(@"^\s*V\d+\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
