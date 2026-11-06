using Avalonia.Media;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Features.SessionMonitor;

namespace Oranda.App.Features.DataCopy;

/// <summary>
/// 데이터 복사 그리드의 열 너비를 값에 맞춘다. 그리드 자체(<see cref="MonitorGrid"/>·<see cref="MonitorGridBinder{T}"/>)는 고치지 않는다.
/// <list type="bullet">
/// <item>바인더는 새 결과에 이전 결과의 열 너비를 이어 준다. 그래서 빈 목록이나 첫 값(「대기」, 첫 표)으로 정한 너비가 굳는다.
/// 결과를 바꿔 끼울 때마다 새 값이 들어가지 않는 열만 넓힌다(줄이지 않으므로 사용자가 넓힌 너비·정렬·필터는 그대로다).</item>
/// <item>마지막 열(조건·내용·문장·오류·결과)은 남은 폭을 채운다: 열들이 그리드보다 좁으면 늘리고, 넓으면 원래 너비로 둔다.</item>
/// </list>
/// 너비 계산은 조회 결과 그리드의 자동 크기(<see cref="ResultGridModel.AutoSizeColumns"/>)와 같은 규칙(글자 칸 수 × 글자 폭 + 16, 56~360)을 쓴다.
/// </summary>
internal sealed class GridColumnFit
{
    private const double Padding = 16;
    private const double Min = 56;
    private const double Max = 360;
    private const int SampleRows = 200;

    private readonly MonitorGrid _grid;
    private ResultGridModel? _model;
    private double _filled = -1;
    private double _natural;
    private bool _applying;
    private (FontFamily Family, double Size, double Width)? _charWidth;

    public GridColumnFit(MonitorGrid grid)
    {
        _grid = grid;
        grid.GridControl.SizeChanged += (_, _) => FillLast();
    }

    /// <summary>바인더가 결과를 바꿔 끼운 뒤 부른다: 모자란 열을 넓히고 마지막 열을 채운다.</summary>
    public void Track(ResultGridModel? model)
    {
        if (!ReferenceEquals(model, _model))
        {
            if (_model is not null)
            {
                _model.Changed -= OnChanged;
            }

            _model = model;
            if (model is not null)
            {
                model.Changed += OnChanged;
            }
        }

        Grow();
        FillLast();
    }

    private void OnChanged(object? sender, EventArgs e) => FillLast();

    // 새 값이 들어가지 않는 열만 넓힌다(아직 자동 크기 전이면 그리드가 정하게 둔다).
    private void Grow()
    {
        if (_model is not { WidthsDecided: true, IsTextMode: false } m)
        {
            return;
        }

        var control = _grid.GridControl;
        var charWidth = CharWidth(control);
        var rows = (int)Math.Min(m.RowCount, SampleRows);
        _applying = true;
        try
        {
            for (var c = 0; c < m.Columns.Count; c++)
            {
                var chars = Math.Max(ResultGridModel.DisplayWidth(m.Columns[c].Name) + 2, 4);
                for (var r = 0; r < rows; r++)
                {
                    chars = Math.Max(chars, ResultGridModel.DisplayWidth(m.GetCellText(r, c)));
                }

                var need = Math.Clamp((chars * charWidth) + Padding, Min, Max);
                if (need > m.GetColumnWidth(c) + 0.5)
                {
                    m.SetColumnWidth(c, need);
                }
            }
        }
        finally
        {
            _applying = false;
        }
    }

    // 그리드 글꼴의 글자 1칸 너비(글꼴·크기가 그대로면 다시 재지 않는다).
    private double CharWidth(ResultGridControl control)
    {
        if (_charWidth is not { } cached || !Equals(cached.Family, control.GridFontFamily) || cached.Size != control.GridFontSize)
        {
            cached = (control.GridFontFamily, control.GridFontSize, new GridTextFactory(new Typeface(control.GridFontFamily), control.GridFontSize).CharWidth);
            _charWidth = cached;
        }

        return cached.Width;
    }

    // 마지막 열이 남은 폭을 채운다.
    private void FillLast()
    {
        var control = _grid.GridControl;
        if (_applying || _model is not { } m || m.IsTextMode || m.VisibleColumns.Count == 0 || control.ViewportWidth <= 0)
        {
            return;
        }

        var last = m.VisibleColumns[^1];
        var width = m.GetColumnWidth(last);
        if (Math.Abs(width - _filled) > 0.5)
        {
            _natural = width; // 자동 크기·넓히기를 했거나 사용자가 너비를 바꿨다: 그 너비가 새 기준
        }

        var others = control.ExtentWidth - control.RowHeaderWidth - width;
        var want = Math.Max(_natural, Math.Floor(control.ViewportWidth - control.RowHeaderWidth - others - 1));
        if (Math.Abs(want - width) < 0.5)
        {
            _filled = width;
            return;
        }

        _applying = true;
        try
        {
            m.SetColumnWidth(last, want);
            _filled = m.GetColumnWidth(last);
        }
        finally
        {
            _applying = false;
        }
    }
}
