using System.Globalization;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// ResultGridModel 의 일부: 선택(ResultGridModel.cs 에서 나눔, 동작 불변).
public sealed partial class ResultGridModel
{
    // ---- 선택 ----

    // 행 머리를 Ctrl 로 눌러 더한 떨어진 행 구간(양끝 포함, 번호순, 서로 겹치지 않음). 현재 칸 범위(Anchor~Active)와 합쳐 「고른 행」이 된다.
    private List<(long First, long Last)> _extraRows = [];

    public void SelectCell(GridCell cell)
    {
        _extraRows.Clear();
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

        _extraRows.Clear();
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
            _extraRows.Clear();
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
            _extraRows.Clear();
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
        return RowRangeSet.Add(_extraRows, f.Row, t.Row);
    }

    /// <summary>고른 행 수(구간 길이의 합).</summary>
    public long SelectedRowCount => SelectedRowRanges().Sum(r => r.Last - r.First + 1);

    public void SelectColumn(int visibleCol)
    {
        if (RowCount == 0)
        {
            return;
        }

        _extraRows.Clear();
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

        return _extraRows.Count > 0 && RowRangeSet.Contains(_extraRows, row); // Ctrl 로 더한 행은 전체 열이 고른 것이다
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
        }
    }
}
