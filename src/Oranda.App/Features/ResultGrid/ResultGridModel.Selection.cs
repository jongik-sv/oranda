using System.Globalization;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// ResultGridModel 의 일부: 선택(ResultGridModel.cs 에서 나눔, 동작 불변).
public sealed partial class ResultGridModel
{
    // ---- 선택 ----

    public void SelectCell(GridCell cell)
    {
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
            Anchor = Clamp(new GridCell(row, 0));
            Active = Clamp(new GridCell(row, last));
            HasSelection = RowCount > 0;
        }

        RaiseChanged();
    }

    public void SelectColumn(int visibleCol)
    {
        if (RowCount == 0)
        {
            return;
        }

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
        return row >= f.Row && row <= t.Row && visibleCol >= f.Col && visibleCol <= t.Col;
    }

    private GridCell Clamp(GridCell c) =>
        new(Math.Clamp(c.Row, 0, Math.Max(0, RowCount - 1)), Math.Clamp(c.Col, 0, Math.Max(0, _visible.Count - 1)));

    private void ClampSelection()
    {
        if (HasSelection)
        {
            Anchor = Clamp(Anchor);
            Active = Clamp(Active);
        }
    }
}
