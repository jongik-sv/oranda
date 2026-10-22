using System.Globalization;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

/// <summary>열 조건의 종류(열 머리글 자동 필터, TSK-13-26).</summary>
public enum ConditionKind
{
    Contains,
    Equals,
    StartsWith,

    /// <summary>숫자·날짜 열은 값 범위, 그 밖의 열은 표시 글자의 사전순 범위. 한쪽 끝을 비우면 그쪽은 제한하지 않는다.</summary>
    Between,
    IsNull,
    IsNotNull,
}

/// <summary>열 하나에 거는 조건. <paramref name="Text"/>·<paramref name="Text2"/> 는 사용자가 친 글(범위는 시작·끝).</summary>
public sealed record ColumnCondition(ConditionKind Kind, string Text = "", string Text2 = "");

/// <summary>
/// 열 하나의 자동 필터. <see cref="Values"/> 가 있으면 화면에 보이는 글이 그 집합에 든 행만(NULL 은 <see cref="IncludeNull"/>),
/// <see cref="Condition"/> 이 있으면 조건도 맞아야 한다(둘은 AND).
/// </summary>
public sealed record ColumnFilter
{
    public IReadOnlySet<string>? Values { get; init; }

    public bool IncludeNull { get; init; } = true;

    public ColumnCondition? Condition { get; init; }

    public bool IsEmpty => Values is null && Condition is null;
}

/// <summary>
/// 가져온 행 안에서 거르는 조건 전체(빠른 거르기 글 + 열별 자동 필터, 서로 AND). 열은 결과 열 ordinal 로 가리킨다.
/// 서버에 다시 묻지 않으므로 서버 거르기 줄(GRID-10)과 독립이다. 값 비교는 화면에 보이는 글(<see cref="GridDisplayOptions"/>) 기준이다.
/// </summary>
public sealed record LocalFilterSpec
{
    public static readonly LocalFilterSpec Empty = new();

    /// <summary>빠른 거르기 글. 비어 있으면 쓰지 않는다. 일부 일치·대소문자 무시.</summary>
    public string QuickText { get; init; } = string.Empty;

    /// <summary>빠른 거르기가 보는 열 ordinal. null 이면 그때 보이는 모든 열.</summary>
    public IReadOnlyList<int>? QuickColumns { get; init; }

    public IReadOnlyDictionary<int, ColumnFilter> Columns { get; init; } = new Dictionary<int, ColumnFilter>();

    public bool HasQuick => QuickText.Length > 0;

    public bool IsEmpty => !HasQuick && Columns.Values.All(c => c.IsEmpty);

    /// <summary>이 열 필터만 바꾼(비우면 지운) 새 조건.</summary>
    public LocalFilterSpec WithColumn(int ordinal, ColumnFilter? filter)
    {
        var map = new Dictionary<int, ColumnFilter>(Columns);
        if (filter is null || filter.IsEmpty)
        {
            map.Remove(ordinal);
        }
        else
        {
            map[ordinal] = filter;
        }

        return this with { Columns = map };
    }
}

/// <summary>
/// <see cref="LocalFilterSpec"/> 을 행에 대어 보는 판정기. 페이지 단위로 부르며 UI 스레드 밖에서 돌아도 된다(값만 읽는다).
/// 열마다 표시 글을 행마다 한 번만 만든다.
/// </summary>
public sealed class LocalRowFilter
{
    private readonly IReadOnlyList<ResultColumn> _columns;
    private readonly GridDisplayOptions _display;
    private readonly string _quick;
    private readonly int[] _quickColumns;
    private readonly (int Ordinal, ColumnFilter Filter, Between? Range)[] _filters;

    private readonly record struct Between(decimal? Lo, decimal? Hi, DateTime? From, DateTime? To, bool Numeric, bool Temporal);

    /// <param name="spec">거를 조건.</param>
    /// <param name="columns">결과 열.</param>
    /// <param name="visibleColumns">빠른 거르기의 대상이 열을 따로 고르지 않았을 때 쓸, 지금 보이는 열.</param>
    /// <param name="display">화면 표시 옵션(표시 글 기준 비교).</param>
    public LocalRowFilter(LocalFilterSpec spec, IReadOnlyList<ResultColumn> columns, IReadOnlyList<int> visibleColumns, GridDisplayOptions display)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(visibleColumns);
        _columns = columns;
        _display = display;
        _quick = spec.QuickText;
        _quickColumns = spec.QuickColumns is { } q ? [.. q] : [.. visibleColumns];
        _filters = [.. spec.Columns.Where(kv => !kv.Value.IsEmpty && kv.Key >= 0 && kv.Key < columns.Count).Select(kv => (kv.Key, kv.Value, Parse(columns[kv.Key], kv.Value.Condition)))];
    }

    /// <summary>
    /// 페이지 한 쪽의 행마다 통과 여부를 <paramref name="result"/> 의 <paramref name="offset"/>(질의 결과 행 번호) 자리부터 쓴다.
    /// 취소 요청은 행 묶음마다 살핀다.
    /// </summary>
    public void Evaluate(ResultPage page, bool[] result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(result);
        var cache = new string?[_columns.Count];
        for (var i = 0; i < page.RowCount; i++)
        {
            if ((i & 0xFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            var row = page.FirstRowIndex + i;
            if (row >= result.Length)
            {
                break;
            }

            Array.Clear(cache);
            result[row] = Matches(page, i, cache);
        }
    }

    private bool Matches(ResultPage page, int i, string?[] cache)
    {
        foreach (var (ordinal, filter, range) in _filters)
        {
            if (!MatchesColumn(page, i, ordinal, filter, range, cache))
            {
                return false;
            }
        }

        if (_quick.Length == 0)
        {
            return true;
        }

        foreach (var o in _quickColumns)
        {
            if (o >= 0 && o < _columns.Count && Text(page, i, o, cache).Contains(_quick, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private string Text(ResultPage page, int i, int ordinal, string?[] cache) =>
        cache[ordinal] ??= CellValueFormatter.Format(_columns[ordinal], page.Columns[ordinal][i], _display);

    private bool MatchesColumn(ResultPage page, int i, int ordinal, ColumnFilter filter, Between? range, string?[] cache)
    {
        var value = page.Columns[ordinal][i];
        var isNull = value is null or DBNull;
        if (filter.Values is { } values && !(isNull ? filter.IncludeNull : values.Contains(Text(page, i, ordinal, cache))))
        {
            return false;
        }

        if (filter.Condition is not { } c)
        {
            return true;
        }

        switch (c.Kind)
        {
            case ConditionKind.IsNull:
                return isNull;
            case ConditionKind.IsNotNull:
                return !isNull;
        }

        if (isNull)
        {
            return false;
        }

        switch (c.Kind)
        {
            case ConditionKind.Contains:
                return Text(page, i, ordinal, cache).Contains(c.Text, StringComparison.OrdinalIgnoreCase);
            case ConditionKind.Equals:
                return string.Equals(Text(page, i, ordinal, cache), c.Text, StringComparison.OrdinalIgnoreCase);
            case ConditionKind.StartsWith:
                return Text(page, i, ordinal, cache).StartsWith(c.Text, StringComparison.OrdinalIgnoreCase);
            default:
                return InRange(value!, Text(page, i, ordinal, cache), c, range);
        }
    }

    private static bool InRange(object value, string text, ColumnCondition c, Between? range)
    {
        if (range is { Numeric: true } n)
        {
            if (!TryNumber(value, out var d))
            {
                return false;
            }

            return (n.Lo is null || d >= n.Lo) && (n.Hi is null || d <= n.Hi);
        }

        if (range is { Temporal: true } t)
        {
            if (value is not DateTime dt)
            {
                return false;
            }

            return (t.From is null || dt >= t.From) && (t.To is null || dt <= t.To);
        }

        return (c.Text.Length == 0 || string.Compare(text, c.Text, StringComparison.OrdinalIgnoreCase) >= 0)
            && (c.Text2.Length == 0 || string.Compare(text, c.Text2, StringComparison.OrdinalIgnoreCase) <= 0);
    }

    private static Between? Parse(ResultColumn column, ColumnCondition? c)
    {
        if (c is not { Kind: ConditionKind.Between })
        {
            return null;
        }

        if (column.Kind is ColumnDataKind.Number or ColumnDataKind.BinaryFloat)
        {
            var lo = TryNumberText(c.Text);
            var hi = TryNumberText(c.Text2);
            // 한쪽이라도 글이 있는데 수가 아니면 숫자 범위로 비교할 수 없다: 사전순으로 돌아간다.
            if ((c.Text.Length == 0 || lo is not null) && (c.Text2.Length == 0 || hi is not null))
            {
                return new Between(lo, hi, null, null, true, false);
            }
        }
        else if (column.Kind is ColumnDataKind.Date or ColumnDataKind.Timestamp)
        {
            var from = TryDateText(c.Text);
            var to = TryDateText(c.Text2);
            if ((c.Text.Length == 0 || from is not null) && (c.Text2.Length == 0 || to is not null))
            {
                return new Between(null, null, from, to, false, true);
            }
        }

        return null;
    }

    private static decimal? TryNumberText(string s) =>
        decimal.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static DateTime? TryDateText(string s) =>
        DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d) ? d : null;

    private static bool TryNumber(object value, out decimal d)
    {
        switch (value)
        {
            case decimal m:
                d = m;
                return true;
            case double or float:
                try
                {
                    d = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    return true;
                }
                catch (OverflowException)
                {
                    d = 0;
                    return false;
                }

            default:
                return decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }
    }
}
