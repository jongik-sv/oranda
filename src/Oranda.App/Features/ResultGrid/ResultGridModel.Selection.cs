using System.Globalization;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// ResultGridModel 의 일부: 선택(ResultGridModel.cs 에서 나눔, 동작 불변).
public sealed partial class ResultGridModel
{
    // ---- 선택 ----

    // 행 머리를 Ctrl 로 눌러 더한 떨어진 행 구간(양끝 포함, 번호순, 서로 겹치지 않음). 현재 칸 범위(Anchor~Active)와 합쳐 「고른 행」이 된다.
    private List<(long First, long Last)> _extraRows = [];

    // 머리글·셀을 Ctrl 로 눌러 더한 떨어진 칸 범위(From 이 왼쪽 위, To 가 오른쪽 아래, 보이는 열 위치). 현재 칸 범위와 합쳐 「고른 칸」이 된다.
    // 열 위치로 기억하므로 열 순서·숨김이 바뀌면 지운다(행 번호도 정렬·거르기로 바뀌면 지운다).
    private List<(GridCell From, GridCell To)> _extraRanges = [];

    public void SelectCell(GridCell cell)
    {
        ClearExtras();
        cell = Clamp(cell);
        Anchor = Active = cell;
        HasSelection = RowCount > 0 && _visible.Count > 0;
        RaiseChanged();
    }

    public void ExtendTo(GridCell cell)
    {
        if (!HasSelection)
        {
            SelectCell(cell);
            return;
        }

        Active = Clamp(cell);
        RaiseChanged();
    }

    public void SelectAll()
    {
        if (RowCount == 0 || _visible.Count == 0)
        {
            return;
        }

        ClearExtras();
        Anchor = new GridCell(0, 0);
        Active = new GridCell(RowCount - 1, _visible.Count - 1);
        HasSelection = true;
        RaiseChanged();
    }

    public void SelectRow(long row, bool extend = false)
    {
        var last = _visible.Count - 1;
        if (extend && HasSelection)
        {
            Active = Clamp(new GridCell(row, last));
        }
        else
        {
            ClearExtras();
            Anchor = Clamp(new GridCell(row, 0));
            Active = Clamp(new GridCell(row, last));
            HasSelection = RowCount > 0;
        }

        RaiseChanged();
    }

    /// <summary>
    /// 행 머리를 Ctrl 로 눌렀다: 그 행을 고른 행에 더하고, 이미 고른 행이면 뺀다(Excel 처럼). 하나만 남은 행은 빼지 않는다.
    /// 더한 행이 현재 행이 된다(Shift 로 이어서 넓힐 수 있다).
    /// </summary>
    public void ToggleRowInSelection(long row)
    {
        if (RowCount == 0 || _visible.Count == 0)
        {
            return;
        }

        row = Math.Clamp(row, 0, RowCount - 1);
        if (!HasSelection)
        {
            SelectRow(row);
            return;
        }

        var ranges = SelectedRowRanges().ToList();
        if (RowRangeSet.Contains(ranges, row))
        {
            if (ranges.Sum(r => r.Last - r.First + 1) <= 1)
            {
                return;
            }

            ranges = RowRangeSet.Remove(ranges, row);
            SetSelectedRows(ranges, ranges[^1].Last);
        }
        else
        {
            ranges = RowRangeSet.Add(ranges, row, row);
            SetSelectedRows(ranges, row);
        }
    }

    /// <summary>
    /// 고른 행을 <paramref name="ranges"/> 로 바꾼다(전체 열). 현재 칸은 <paramref name="active"/> 행이고 나머지는 떨어진 행 구간으로 둔다.
    /// 범위는 결과 행 수 안으로 줄인다. 고를 행이 없으면 선택을 비운다.
    /// </summary>
    public void SetSelectedRows(IEnumerable<(long First, long Last)> ranges, long? active = null)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var merged = RowRangeSet.Normalize(ranges, RowCount);
        if (merged.Count == 0 || _visible.Count == 0)
        {
            ClearExtras();
            HasSelection = false;
            RaiseChanged();
            return;
        }

        var current = active ?? merged[^1].Last;
        var cur = Math.Clamp(current, 0, RowCount - 1);
        if (!RowRangeSet.Contains(merged, cur))
        {
            cur = merged[^1].Last;
        }

        // 현재 행이 든 구간은 현재 칸 범위로, 나머지는 떨어진 구간으로.
        var host = merged.First(r => cur >= r.First && cur <= r.Last);
        _extraRanges.Clear();
        _extraRows = [.. merged.Where(r => r != host)];
        Anchor = new GridCell(host.First, 0);
        Active = new GridCell(host.Last, _visible.Count - 1);
        HasSelection = true;
        RaiseChanged();
    }

    /// <summary>
    /// 고른 행 구간(화면 순서, 번호순, 서로 겹치지 않고 이어진 구간은 합친다). 칸 범위가 걸친 행과 Ctrl 로 더한 행의 합집합이다.
    /// 선택이 없으면 빈 목록. 행 복사·삭제·되돌리기·복제가 이 목록을 함께 쓴다.
    /// </summary>
    public IReadOnlyList<(long First, long Last)> SelectedRowRanges()
    {
        if (!HasSelection)
        {
            return [];
        }

        var (f, t) = SelectionRect();
        var ranges = RowRangeSet.Add(_extraRows, f.Row, t.Row);
        foreach (var (from, to) in _extraRanges)
        {
            ranges = RowRangeSet.Add(ranges, from.Row, to.Row);
        }

        return ranges;
    }

    /// <summary>고른 행 수(구간 길이의 합).</summary>
    public long SelectedRowCount => SelectedRowRanges().Sum(r => r.Last - r.First + 1);

    public void SelectColumn(int visibleCol)
    {
        if (RowCount == 0)
        {
            return;
        }

        ClearExtras();
        Anchor = Clamp(new GridCell(0, visibleCol));
        Active = Clamp(new GridCell(RowCount - 1, visibleCol));
        HasSelection = true;
        RaiseChanged();
    }

    public (GridCell From, GridCell To) SelectionRect() => (
        new GridCell(Math.Min(Anchor.Row, Active.Row), Math.Min(Anchor.Col, Active.Col)),
        new GridCell(Math.Max(Anchor.Row, Active.Row), Math.Max(Anchor.Col, Active.Col)));

    public bool IsSelected(long row, int visibleCol)
    {
        if (!HasSelection)
        {
            return false;
        }

        var (f, t) = SelectionRect();
        if (row >= f.Row && row <= t.Row && visibleCol >= f.Col && visibleCol <= t.Col)
        {
            return true;
        }

        foreach (var (from, to) in _extraRanges)
        {
            if (row >= from.Row && row <= to.Row && visibleCol >= from.Col && visibleCol <= to.Col)
            {
                return true;
            }
        }

        return _extraRows.Count > 0 && RowRangeSet.Contains(_extraRows, row); // Ctrl 로 더한 행은 전체 열이 고른 것이다
    }

    /// <summary>
    /// 머리글을 Ctrl 로 눌렀다: 그 열 전체를 고른 칸에 더하고, 이미 열 전체를 골랐으면 그 열을 모든 범위에서 뺀다(엑셀처럼).
    /// 아직 열 전체를 고르지 않았으면(셀 하나나 칸 범위만 골랐으면) 그 열부터 새로 고른다.
    /// 더한 열이 현재 칸 범위가 된다(Shift 로 이어서 넓힐 수 있다). 하나만 남은 열은 빼지 않는다.
    /// </summary>
    public void ToggleColumnInSelection(int visibleCol)
    {
        if (RowCount == 0 || _visible.Count == 0)
        {
            return;
        }

        visibleCol = Math.Clamp(visibleCol, 0, _visible.Count - 1);
        var last = RowCount - 1;
        var (top, bottom) = SelectionRect();
        if (!HasSelection || (_extraRanges.Count == 0 && _extraRows.Count == 0 && (top.Row != 0 || bottom.Row < last)))
        {
            // 열을 고르지 않은 상태(보던 셀 하나·칸 범위)에서 첫 Ctrl+클릭은 그 열부터 새로 고른다.
            // 남기면 그 칸의 열이 모든 행으로 함께 복사된다(머리글 그냥 클릭은 정렬이라 Ctrl+클릭으로 시작한다).
            SelectColumn(visibleCol);
            return;
        }

        var ranges = SelectionRanges();
        if (!ranges.Any(r => r.From.Row == 0 && r.To.Row >= last && visibleCol >= r.From.Col && visibleCol <= r.To.Col))
        {
            _extraRanges = ranges;
            Anchor = new GridCell(0, visibleCol);
            Active = new GridCell(last, visibleCol);
            RaiseChanged();
            return;
        }

        // 그 열을 모든 범위에서 잘라 낸다. 범위 가운데 열이면 양쪽으로 나뉜다.
        var kept = new List<(GridCell From, GridCell To)>();
        foreach (var (from, to) in ranges)
        {
            if (visibleCol < from.Col || visibleCol > to.Col)
            {
                kept.Add((from, to));
                continue;
            }

            if (from.Col < visibleCol)
            {
                kept.Add((from, new GridCell(to.Row, visibleCol - 1)));
            }

            if (to.Col > visibleCol)
            {
                kept.Add((new GridCell(from.Row, visibleCol + 1), to));
            }
        }

        if (kept.Count == 0)
        {
            return;
        }

        var current = kept[^1];
        kept.RemoveAt(kept.Count - 1);
        _extraRanges = kept;
        Anchor = current.From;
        Active = current.To;
        RaiseChanged();
    }

    /// <summary>
    /// 셀을 Ctrl 로 눌렀다: 지금 칸 범위를 떨어진 범위로 남기고 <paramref name="cell"/> 에서 새 범위를 시작한다(끌면 <see cref="ExtendTo"/> 로 넓힌다).
    /// </summary>
    public void AddCellRange(GridCell cell)
    {
        if (!HasSelection)
        {
            SelectCell(cell);
            return;
        }

        _extraRanges.Add(SelectionRect());
        Anchor = Active = Clamp(cell);
        RaiseChanged();
    }

    /// <summary>
    /// 고른 보이는 열 위치(번호순, 겹치지 않음): 현재 칸 범위와 Ctrl 로 더한 범위가 걸친 열의 합집합. 선택이 없으면 빈 목록.
    /// 복사·열 숨기기가 이 목록을 쓴다.
    /// </summary>
    public IReadOnlyList<int> SelectedVisibleColumns()
    {
        if (!HasSelection || _visible.Count == 0)
        {
            return [];
        }

        var cols = new SortedSet<int>();
        foreach (var (from, to) in SelectionRanges())
        {
            for (var c = from.Col; c <= Math.Min(to.Col, _visible.Count - 1); c++)
            {
                cols.Add(c);
            }
        }

        return [.. cols];
    }

    /// <summary>Ctrl 로 더한 떨어진 칸 범위가 있다(복사는 여러 범위를 합친다).</summary>
    public bool HasMultipleRanges => HasSelection && _extraRanges.Count > 0;

    /// <summary>Ctrl 로 더한 범위 다음에 현재 칸 범위를 둔 목록(왼쪽 위~오른쪽 아래).</summary>
    private List<(GridCell From, GridCell To)> SelectionRanges() => [.. _extraRanges, SelectionRect()];

    private void ClearExtras()
    {
        _extraRows.Clear();
        _extraRanges.Clear();
    }

    private GridCell Clamp(GridCell c) =>
        new(Math.Clamp(c.Row, 0, Math.Max(0, RowCount - 1)), Math.Clamp(c.Col, 0, Math.Max(0, _visible.Count - 1)));

    private void ClampSelection()
    {
        if (HasSelection)
        {
            Anchor = Clamp(Anchor);
            Active = Clamp(Active);
            if (_extraRows.Count > 0)
            {
                _extraRows = RowRangeSet.Normalize(_extraRows, RowCount);
            }

            _extraRanges = [.. _extraRanges.Select(r => (Clamp(r.From), Clamp(r.To)))];
        }
    }
}
