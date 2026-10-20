using System.Globalization;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

/// <summary>
/// 고른 칸 집계(GRID-09, DataGrip·DBeaver 의 선택 집계): 개수·합계·평균·최소·최대. 결과 영역 상태 줄에 보인다.
/// 이미 가져온 행만 훑는다(<see cref="ResultGridModel.TryGetValue"/> 만 쓰고 가져오기를 부르지 않는다). 큰 선택은 앞 <see cref="DefaultMaxCells"/> 칸까지만 센다.
/// 개수는 NULL 이 아닌 칸 수(SQL <c>COUNT(열)</c> 과 같다)이고, 합계·평균·최소·최대는 숫자 열(NUMBER·BINARY_FLOAT/DOUBLE) 칸만 쓴다.
/// </summary>
public sealed record SelectionAggregate
{
    /// <summary>한 번에 훑는 칸 수 상한(전체 선택에서도 상태 줄 갱신이 싸게).</summary>
    public const int DefaultMaxCells = 100_000;

    /// <summary>NULL 이 아닌 칸 수(숫자 아닌 칸 포함).</summary>
    public long Count { get; init; }

    /// <summary>합계·평균·최소·최대에 쓴 숫자 칸 수.</summary>
    public long NumericCount { get; init; }

    /// <summary>합계·평균·최소·최대(숫자 칸이 없으면 null). <see cref="decimal"/> 범위를 넘거나 BINARY_* 값이 섞이면 <see cref="double"/>.</summary>
    public object? Sum { get; init; }

    public object? Average { get; init; }

    public object? Min { get; init; }

    public object? Max { get; init; }

    /// <summary>칸 수 상한에 닿았거나 메모리에 없는 행이 있어 일부만 셌다.</summary>
    public bool IsPartial { get; init; }

    /// <summary>집계할 칸 범위(가져온 행으로 자른 선택). 칸이 둘보다 적으면 null(한 칸만 고르면 상태 줄에 보이지 않는다).</summary>
    public static SelectionScope? ScopeOf(ResultGridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.HasSelection || model.RowCount == 0 || model.VisibleColumns.Count == 0)
        {
            return null;
        }

        var (from, to) = model.SelectionRect();
        var lastRow = Math.Min(to.Row, model.RowCount - 1);
        var lastCol = Math.Min(to.Col, model.VisibleColumns.Count - 1);
        if (lastRow < from.Row || lastCol < from.Col || (lastRow == from.Row && lastCol == from.Col))
        {
            return null;
        }

        var hash = new HashCode();
        for (var c = from.Col; c <= lastCol; c++)
        {
            hash.Add(model.VisibleColumns[c]);
        }

        return new SelectionScope(from.Row, lastRow, from.Col, lastCol, hash.ToHashCode(), model.SortColumn, model.SortDescending);
    }

    /// <summary>고른 칸을 집계한다. 칸이 둘보다 적으면 null.</summary>
    public static SelectionAggregate? Compute(ResultGridModel model, int maxCells = DefaultMaxCells)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCells, 1);
        if (ScopeOf(model) is not { } scope)
        {
            return null;
        }

        var acc = new Accumulator();
        long count = 0, scanned = 0;
        var partial = false;
        for (var r = scope.FirstRow; r <= scope.LastRow && !partial; r++)
        {
            for (var c = scope.FirstCol; c <= scope.LastCol; c++)
            {
                if (scanned == maxCells)
                {
                    partial = true;
                    break;
                }

                scanned++;
                var ordinal = model.VisibleColumns[c];
                if (!model.TryGetValue(r, ordinal, out var value))
                {
                    partial = true; // 메모리에서 밀려난 페이지: 다시 가져오지 않고 뺀다
                    continue;
                }

                if (value is null or DBNull)
                {
                    continue;
                }

                count++;
                if (model.Columns[ordinal].Kind is ColumnDataKind.Number or ColumnDataKind.BinaryFloat)
                {
                    acc.Add(value);
                }
            }
        }

        return new SelectionAggregate
        {
            Count = count,
            NumericCount = acc.Count,
            Sum = acc.Sum,
            Average = acc.Average,
            Min = acc.Min,
            Max = acc.Max,
            IsPartial = partial,
        };
    }

    /// <summary>상태 줄 글: <c>개수 5 · 합계 1,234.5 · 평균 246.9 · 최소 1 · 최대 999</c>. 일부만 셌으면 끝에 「(일부)」.</summary>
    public string ToText(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var text = "개수 " + Count.ToString("N0", culture);
        if (NumericCount > 0)
        {
            text += $" · 합계 {Format(Sum, culture)} · 평균 {Format(Average, culture)} · 최소 {Format(Min, culture)} · 최대 {Format(Max, culture)}";
        }

        return IsPartial ? text + " (일부)" : text;
    }

    private static string Format(object? value, CultureInfo culture) => value switch
    {
        decimal d => d.ToString("#,##0.############################", culture),
        double f when double.IsFinite(f) && (Math.Abs(f) >= 1e15 || (f != 0 && Math.Abs(f) < 1e-6)) => f.ToString("G10", culture),
        double f when double.IsFinite(f) => f.ToString("#,##0.##########", culture),
        double f => f.ToString(culture),
        _ => string.Empty,
    };

    /// <summary>숫자 칸 누적. decimal 로 더하다가 범위를 넘거나 BINARY_* 값이 오면 double 로 바꾼다.</summary>
    private sealed class Accumulator
    {
        private decimal _sum, _min, _max;
        private double _fsum, _fmin, _fmax;
        private bool _double;

        public long Count { get; private set; }

        public object? Sum => Count == 0 ? null : _double ? _fsum : _sum;

        public object? Min => Count == 0 ? null : _double ? _fmin : _min;

        public object? Max => Count == 0 ? null : _double ? _fmax : _max;

        // 평균은 화면에 쓸 만큼(소수 10자리)만 남긴다.
        public object? Average => Count == 0 ? null : _double ? _fsum / Count : Math.Round(_sum / Count, 10);

        public void Add(object value)
        {
            switch (value)
            {
                case decimal d:
                    AddDecimal(d);
                    break;
                case double or float:
                    AddDouble(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                    break;
                case int or long or short or byte or sbyte or uint or ulong or ushort:
                    AddDecimal(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                    break;
                case string s when decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                    AddDecimal(parsed); // 드라이버 원시 문자열(decimal 정밀도 안)
                    break;
                case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var big):
                    AddDouble(big); // decimal 범위를 넘는 NUMBER
                    break;
                default:
                    break; // 숫자로 읽을 수 없는 값은 개수에만 든다
            }
        }

        private void AddDecimal(decimal v)
        {
            if (_double)
            {
                AddDouble((double)v);
                return;
            }

            try
            {
                _sum += v;
            }
            catch (OverflowException)
            {
                ToDouble();
                AddDouble((double)v);
                return;
            }

            _min = Count == 0 ? v : Math.Min(_min, v);
            _max = Count == 0 ? v : Math.Max(_max, v);
            Count++;
        }

        private void AddDouble(double v)
        {
            if (double.IsNaN(v))
            {
                return; // BINARY_DOUBLE NaN 은 합계에 넣지 않는다
            }

            if (!_double)
            {
                ToDouble();
            }

            _fsum += v;
            _fmin = Count == 0 ? v : Math.Min(_fmin, v);
            _fmax = Count == 0 ? v : Math.Max(_fmax, v);
            Count++;
        }

        private void ToDouble()
        {
            _double = true;
            _fsum = (double)_sum;
            _fmin = (double)_min;
            _fmax = (double)_max;
        }
    }
}

/// <summary>집계한 칸 범위(보이는 행·열 위치, 가져온 행으로 자름)와 열·정렬 상태. 같으면 다시 셀 필요가 없다.</summary>
public readonly record struct SelectionScope(long FirstRow, long LastRow, int FirstCol, int LastCol, int ColumnsHash, int SortColumn, bool SortDescending);
