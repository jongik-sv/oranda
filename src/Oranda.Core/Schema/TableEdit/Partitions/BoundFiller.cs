using System.Globalization;

namespace Oranda.Core.Schema.TableEdit.Partitions;

/// <summary>「경계 채우기…」의 간격 단위(설계 18.4, 시안 ⑥).</summary>
public enum BoundUnit
{
    Day,
    Month,
    Year,

    /// <summary>숫자 키 간격.</summary>
    Number,
}

/// <summary>
/// 경계 채우기 입력. 날짜 단위는 <see cref="StartDate"/>, 숫자는 <see cref="StartNumber"/> 를 쓴다. 줄 하나 = 「시작 + i×간격 이상, 시작 + (i+1)×간격 미만」.
/// </summary>
/// <param name="Count">만들 줄 수(1 이상)</param>
/// <param name="Step">간격(1 이상, 날짜 단위는 그 단위 수)</param>
public sealed record BoundFill(BoundUnit Unit, int Count, int Step = 1)
{
    public DateOnly StartDate { get; init; }

    public decimal StartNumber { get; init; }

    /// <summary>이름 앞머리(기본 <c>P_</c>).</summary>
    public string Prefix { get; init; } = "P_";

    public string? Tablespace { get; init; }
}

/// <summary>
/// 경계 채우기(「2026-01 부터 매월 12개」): 범위 파티션 새 줄 목록을 만든다. 경계(LESS THAN) = 다음 구간의 시작,
/// 날짜는 <c>DATE 'YYYY-MM-DD'</c>(Oracle·Tibero 공통, Tibero 실측 11), 숫자는 소수점 그대로.
/// 이름 = 구간 시작으로 <c>P_YYYY_MM_DD</c>(일)·<c>P_YYYY_MM</c>(월)·<c>P_YYYY</c>(년)·<c>P_&lt;시작 수&gt;</c>(숫자, 음수는 <c>M</c>, 소수점은 <c>_</c>).
/// 이미 있는 이름과 겹치는 줄은 만들지 않는다(같은 구간을 두 번 채워도 겹치지 않게).
/// </summary>
public static class BoundFiller
{
    /// <summary>줄 목록. 날짜가 9999-12-31 을 넘거나 간격 계산이 넘치면 <see cref="ArgumentOutOfRangeException"/>.</summary>
    public static IReadOnlyList<PartitionDraft> Fill(BoundFill fill, IReadOnlyCollection<string>? existingNames = null)
    {
        ArgumentNullException.ThrowIfNull(fill);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fill.Count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fill.Step);
        var taken = new HashSet<string>(existingNames ?? [], StringComparer.Ordinal);
        var result = new List<PartitionDraft>(fill.Count);
        for (var i = 0; i < fill.Count; i++)
        {
            (string Name, string Bound) row;
            try
            {
                row = fill.Unit == BoundUnit.Number ? NumberRow(fill, i) : DateRow(fill, i);
            }
            catch (OverflowException ex)
            {
                throw new ArgumentOutOfRangeException(nameof(fill), fill.Count, "경계 채우기 범위가 너무 크다: " + ex.Message);
            }

            var (name, bound) = row;
            if (!taken.Add(name))
            {
                continue;
            }

            result.Add(new PartitionDraft(DraftKeys.New(), name) { Bound = bound, Tablespace = fill.Tablespace });
        }

        return result;
    }

    private static (string Name, string Bound) DateRow(BoundFill fill, int i)
    {
        var start = Shift(fill.StartDate, fill.Unit, checked(i * fill.Step));
        var end = Shift(fill.StartDate, fill.Unit, checked((i + 1) * fill.Step));
        var name = fill.Unit switch
        {
            BoundUnit.Day => start.ToString("yyyy_MM_dd", CultureInfo.InvariantCulture),
            BoundUnit.Month => start.ToString("yyyy_MM", CultureInfo.InvariantCulture),
            _ => start.ToString("yyyy", CultureInfo.InvariantCulture),
        };
        return (fill.Prefix + name, "DATE '" + end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'");
    }

    private static DateOnly Shift(DateOnly start, BoundUnit unit, int amount) => unit switch
    {
        BoundUnit.Day => start.AddDays(amount),
        BoundUnit.Month => start.AddMonths(amount),
        _ => start.AddYears(amount),
    };

    private static (string Name, string Bound) NumberRow(BoundFill fill, int i)
    {
        var start = fill.StartNumber + ((decimal)i * fill.Step);
        var end = fill.StartNumber + ((decimal)(i + 1) * fill.Step);
        var label = start.ToString("0.############################", CultureInfo.InvariantCulture).Replace("-", "M", StringComparison.Ordinal).Replace('.', '_');
        return (fill.Prefix + label, end.ToString("0.############################", CultureInfo.InvariantCulture));
    }
}
