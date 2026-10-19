using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

/// <summary>
/// 직접 그리기 가상 결과 그리드(TSK-06-03, D-008). 셀 컨트롤 0개, 보이는 행·열만 <see cref="Render"/> 에서 그린다.
/// 상태는 <see cref="ResultGridModel"/>, 스크롤바는 View 가 <see cref="ScrollChanged"/> 로 맞춘다.
/// </summary>
public sealed class ResultGridControl : Control
{
    private const double CellPadX = 6;
    private const double RowPadY = 3;
    private const double ResizeGrip = 4;

    private readonly GridTextCache _cells = new();
    private readonly GridTextCache _headers = new();
    private GridTextFactory? _factory;
    private ResultGridModel? _model;
    private Palette? _palette;
    private double _fontSize = 13;
    private double _offsetX;
    private double _offsetY;
    private bool _dragSelecting;
    private int _resizeColumn = -1;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private TopLevel? _top;

    public ResultGridControl()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    /// <summary>스크롤 위치·범위가 바뀌었다(스크롤바 갱신용).</summary>
    public event EventHandler? ScrollChanged;

    public ResultGridModel? Model
    {
        get => _model;
        set
        {
            if (ReferenceEquals(_model, value))
            {
                return;
            }

            if (_model is not null)
            {
                _model.Changed -= OnModelChanged;
            }

            _model = value;
            _cells.Clear();
            _headers.Clear();
            _offsetX = _offsetY = 0;
            if (_model is not null)
            {
                _model.Changed += OnModelChanged;
                _ = LoadAsync(_model);
            }

            InvalidateVisual();
            ScrollChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>글자 크기(DIP, 8~32). 글자 크게/작게 명령이 바꾼다.</summary>
    public double GridFontSize
    {
        get => _fontSize;
        set
        {
            var v = Math.Clamp(value, 8, 32);
            if (Math.Abs(v - _fontSize) < 0.01)
            {
                return;
            }

            _fontSize = v;
            ResetTextCaches();
            _model?.AutoSizeColumns(Factory.CharWidth);
        }
    }

    public double RowHeight => Factory.LineHeight + 2 * RowPadY;

    public double HeaderHeight => RowHeight + 2;

    public double OffsetX => _offsetX;

    public double OffsetY => _offsetY;

    public double ExtentWidth => _model is null ? 0 : (_model.IsTextMode ? TextExtentWidth() : TotalColumnsWidth());

    public double ExtentHeight => (_model?.RowCount ?? 0) * RowHeight;

    public double ViewportWidth => Bounds.Width;

    public double ViewportHeight => Math.Max(0, Bounds.Height - HeaderHeight);

    public double MaxOffsetY => Math.Max(0, ExtentHeight - ViewportHeight);

    public double MaxOffsetX => Math.Max(0, ExtentWidth - ViewportWidth);

    private GridTextFactory Factory =>
        _factory ??= new GridTextFactory(new Typeface(new FontFamily("Cascadia Mono, Consolas, Menlo, monospace")), _fontSize);

    public void ScrollTo(double x, double y)
    {
        var nx = Math.Clamp(x, 0, MaxOffsetX);
        var ny = Math.Clamp(y, 0, MaxOffsetY);
        if (nx == _offsetX && ny == _offsetY)
        {
            return;
        }

        _offsetX = nx;
        _offsetY = ny;
        InvalidateVisual();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>활성 셀이 보이도록 스크롤한다.</summary>
    public void EnsureVisible(GridCell cell)
    {
        if (_model is null)
        {
            return;
        }

        var top = cell.Row * RowHeight;
        var y = _offsetY;
        if (top < y)
        {
            y = top;
        }
        else if (top + RowHeight > y + ViewportHeight)
        {
            y = top + RowHeight - ViewportHeight;
        }

        var x = _offsetX;
        if (!_model.IsTextMode && cell.Col >= _model.FrozenCount && cell.Col < _model.VisibleColumns.Count)
        {
            var left = ColumnLeft(cell.Col) - FrozenWidth();
            var right = left + _model.GetColumnWidth(_model.VisibleColumns[cell.Col]);
            var view = ViewportWidth - FrozenWidth();
            if (left < x)
            {
                x = left;
            }
            else if (right > x + view)
            {
                x = right - view;
            }
        }

        ScrollTo(x, y);
    }

    // ---- 수명·모델 ----

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        if (_top is not null)
        {
            _top.ScalingChanged += OnScalingChanged;
        }

        ActualThemeVariantChanged += OnThemeChanged;
        _palette = null;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_top is not null)
        {
            _top.ScalingChanged -= OnScalingChanged;
            _top = null;
        }

        ActualThemeVariantChanged -= OnThemeChanged;
        _cells.Clear();
        _headers.Clear();
    }

    private void OnScalingChanged(object? sender, EventArgs e) => ResetTextCaches();

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _palette = null;
        ResetTextCaches();
    }

    private void ResetTextCaches()
    {
        _factory = null;
        _cells.Clear();
        _headers.Clear();
        InvalidateVisual();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadAsync(ResultGridModel m)
    {
        try
        {
            await m.LoadFirstPageAsync().ConfigureAwait(true);
            if (ReferenceEquals(_model, m))
            {
                m.AutoSizeColumns(Factory.CharWidth);
            }
        }
        catch (Exception)
        {
            // 오류는 Model.Error 로 보인다.
        }
    }

    private void OnModelChanged(object? sender, EventArgs e)
    {
        InvalidateVisual();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- 배치 ----

    private double TotalColumnsWidth() => _model!.VisibleColumns.Sum(c => _model.GetColumnWidth(c));

    private double TextExtentWidth() => (_model!.GetTextHeader().Split(Environment.NewLine)[0].Length + 2) * Factory.CharWidth;

    private double FrozenWidth()
    {
        if (_model is null || _model.IsTextMode)
        {
            return 0;
        }

        double w = 0;
        for (var i = 0; i < _model.FrozenCount && i < _model.VisibleColumns.Count; i++)
        {
            w += _model.GetColumnWidth(_model.VisibleColumns[i]);
        }

        return w;
    }

    /// <summary>
    /// 셀의 화면 사각형(이 컨트롤 좌표). 셀 편집기가 그 위에 입력 칸을 띄운다. 화면 밖이면 null.
    /// </summary>
    public Rect? CellBounds(GridCell cell)
    {
        if (_model is null || _model.IsTextMode || cell.Col < 0 || cell.Col >= _model.VisibleColumns.Count)
        {
            return null;
        }

        var left = ColumnLeft(cell.Col) - (cell.Col < _model.FrozenCount ? 0 : _offsetX);
        var top = HeaderHeight + (cell.Row * RowHeight) - _offsetY;
        var w = _model.GetColumnWidth(_model.VisibleColumns[cell.Col]);
        return top < HeaderHeight || top > Bounds.Height ? null : new Rect(left, top, w, RowHeight);
    }

    /// <summary>셀 편집을 열어 달라는 요청(두 번 누르기·F2). 편집 모드인지는 받는 쪽이 판단한다.</summary>
    public event EventHandler<GridCell>? EditRequested;

    protected override void OnDoubleTapped(Avalonia.Input.TappedEventArgs e)
    {
        base.OnDoubleTapped(e);
        if (_model is { HasSelection: true } m && !m.IsTextMode && e.GetPosition(this).Y >= HeaderHeight)
        {
            EditRequested?.Invoke(this, m.Active);
        }
    }

    /// <summary>보이는 열 <paramref name="col"/> 의 왼쪽 x(스크롤 전 절대 좌표).</summary>
    private double ColumnLeft(int col)
    {
        double x = 0;
        for (var i = 0; i < col; i++)
        {
            x += _model!.GetColumnWidth(_model.VisibleColumns[i]);
        }

        return x;
    }

    /// <summary>화면 x 좌표 → (보이는 열 위치, 열 오른쪽 경계까지의 거리). 고정 열 영역은 스크롤하지 않는다.</summary>
    private (int Col, double RightEdgeDistance) HitColumn(double x)
    {
        if (_model is null)
        {
            return (-1, double.MaxValue);
        }

        var frozen = FrozenWidth();
        double left = 0;
        for (var i = 0; i < _model.VisibleColumns.Count; i++)
        {
            var w = _model.GetColumnWidth(_model.VisibleColumns[i]);
            var screenLeft = i < _model.FrozenCount ? left : left - _offsetX;
            var inScrollRegion = i >= _model.FrozenCount;
            if (!(inScrollRegion && screenLeft < frozen) && x >= screenLeft && x < screenLeft + w)
            {
                return (i, screenLeft + w - x);
            }

            left += w;
        }

        return (-1, double.MaxValue);
    }

    private (long Row, int Col) HitCell(Point p)
    {
        var col = _model!.IsTextMode ? 0 : HitColumn(p.X).Col;
        var row = (long)Math.Floor((p.Y - HeaderHeight + _offsetY) / RowHeight);
        return (Math.Clamp(row, 0, Math.Max(0, _model.RowCount - 1)), col);
    }

    // ---- 그리기 ----

    public override void Render(DrawingContext context)
    {
        var ctx = context;
        var size = Bounds.Size;
        var pal = _palette ??= Palette.Create(this);
        ctx.FillRectangle(pal.Background, new Rect(size));
        if (_model is null || !_model.HasResult)
        {
            return;
        }

        if (_model.RowCount > 0 || _model.HasMoreRows)
        {
            _cells.BeginFrame();
            _headers.BeginFrame();
        }

        var rh = RowHeight;
        var hh = HeaderHeight;
        var first = (long)(_offsetY / rh);
        var yStart = hh - (_offsetY - first * rh);
        var visRows = (int)Math.Ceiling((size.Height - hh) / rh) + 1;
        var last = Math.Min(_model.RowCount - 1, first + visRows - 1);
        _model.Prefetch(first, Math.Max(first, first + visRows - 1));

        if (_model.IsTextMode)
        {
            RenderTextMode(ctx, size, pal, first, last, yStart);
        }
        else
        {
            RenderCells(ctx, size, pal, first, last, yStart);
        }
    }

    private void RenderTextMode(DrawingContext ctx, Size size, Palette pal, long first, long last, double yStart)
    {
        var m = _model!;
        var rh = RowHeight;
        var f = Factory;
        var headerLines = m.GetTextHeader().Split(Environment.NewLine);
        for (var r = first; r <= last; r++)
        {
            var y = yStart + (r - first) * rh;
            if (m.IsSelected(r, 0))
            {
                ctx.FillRectangle(pal.Selection, new Rect(0, y, size.Width, rh));
            }
            else if ((r & 1) == 1)
            {
                ctx.FillRectangle(pal.AltRow, new Rect(0, y, size.Width, rh));
            }

            var t = _cells.Get(r << 12, m.GetTextLine(r), s => f.Create(s, pal.Text));
            t.Draw(ctx, new Point(CellPadX - _offsetX, y + (rh - t.Height) / 2));
        }

        ctx.FillRectangle(pal.Header, new Rect(0, 0, size.Width, HeaderHeight));
        var h = _headers.Get(0, headerLines[0], s => f.Create(s, pal.Text));
        h.Draw(ctx, new Point(CellPadX - _offsetX, (HeaderHeight - h.Height) / 2));
        ctx.DrawLine(pal.Line, new Point(0, HeaderHeight - 0.5), new Point(size.Width, HeaderHeight - 0.5));
    }

    private void RenderCells(DrawingContext ctx, Size size, Palette pal, long first, long last, double yStart)
    {
        var m = _model!;
        var rh = RowHeight;
        var hh = HeaderHeight;
        var f = Factory;
        var scale = _top?.RenderScaling ?? 1.0;
        var frozen = FrozenWidth();

        for (var r = first; r <= last; r++)
        {
            var y = yStart + (r - first) * rh;
            if ((r & 1) == 1)
            {
                ctx.FillRectangle(pal.AltRow, new Rect(0, y, size.Width, rh));
            }
        }

        // 스크롤 열 먼저, 고정 열을 위에 덮어 그린다.
        for (var pass = 0; pass < 2; pass++)
        {
            double left = 0;
            for (var i = 0; i < m.VisibleColumns.Count; i++)
            {
                var ord = m.VisibleColumns[i];
                var w = m.GetColumnWidth(ord);
                var isFrozen = i < m.FrozenCount;
                var x = isFrozen ? left : left - _offsetX;
                left += w;
                if ((pass == 0) == isFrozen || x + w < 0 || x > size.Width)
                {
                    continue;
                }

                DrawColumn(ctx, size, pal, f, scale, i, ord, x, w, first, last, yStart, isFrozen);
            }
        }

        // 가로선
        for (var r = first; r <= last + 1; r++)
        {
            var y = Snap(yStart + (r - first) * rh, scale);
            ctx.DrawLine(pal.Line, new Point(0, y), new Point(size.Width, y));
        }

        // 머리글
        ctx.FillRectangle(pal.Header, new Rect(0, 0, size.Width, hh));
        for (var pass = 0; pass < 2; pass++)
        {
            double hx = 0;
            for (var i = 0; i < m.VisibleColumns.Count; i++)
            {
                var ord = m.VisibleColumns[i];
                var w = m.GetColumnWidth(ord);
                var isFrozen = i < m.FrozenCount;
                var x = isFrozen ? hx : hx - _offsetX;
                hx += w;
                if ((pass == 0) == isFrozen || x + w < 0 || x > size.Width)
                {
                    continue;
                }

                DrawHeader(ctx, pal, f, scale, ord, x, w, hh, isFrozen);
            }
        }

        ctx.DrawLine(pal.Line, new Point(0, hh - 0.5), new Point(size.Width, hh - 0.5));
        if (frozen > 0)
        {
            var fx = Snap(frozen, scale);
            ctx.DrawLine(pal.FrozenLine, new Point(fx, 0), new Point(fx, size.Height));
        }
    }

    private void DrawColumn(DrawingContext ctx, Size size, Palette pal, GridTextFactory f, double scale, int visCol, int ord, double x, double w, long first, long last, double yStart, bool isFrozen)
    {
        var m = _model!;
        var rh = RowHeight;
        var hh = HeaderHeight;
        var clipLeft = isFrozen ? x : Math.Max(x, FrozenWidth());
        using (ctx.PushClip(new Rect(clipLeft, hh, Math.Max(0, x + w - clipLeft - 1), Math.Max(0, size.Height - hh))))
        {
            if (isFrozen)
            {
                ctx.FillRectangle(pal.Background, new Rect(x, hh, w, size.Height - hh));
                for (var r = first; r <= last; r++)
                {
                    if ((r & 1) == 1)
                    {
                        ctx.FillRectangle(pal.AltRow, new Rect(x, yStart + (r - first) * rh, w, rh));
                    }
                }
            }

            for (var r = first; r <= last; r++)
            {
                var y = yStart + (r - first) * rh;
                var selected = m.IsSelected(r, visCol);

                // 편집 모드: 새 행은 초록, 지울 행은 빨강, 바꾼 칸은 노랑으로 깐다(선택 색이 위에 온다).
                var mark = m.EditOverlay is null ? GridRowMark.None : m.RowMarkOf(r);
                var editBrush = mark switch
                {
                    GridRowMark.Inserted => pal.InsertedRow,
                    GridRowMark.Deleted or GridRowMark.Failed => pal.DeletedRow,
                    _ => m.EditOverlay is not null && m.IsEdited(r, ord) ? pal.EditedCell : null,
                };
                if (editBrush is not null)
                {
                    ctx.FillRectangle(editBrush, new Rect(x, y, w, rh));
                }

                if (selected)
                {
                    ctx.FillRectangle(pal.Selection, new Rect(x, y, w, rh));
                }

                var text = m.GetCellText(r, ord);
                if (text.Length == 0)
                {
                    continue;
                }

                var isNull = m.IsNull(r, ord);
                var key = (r << 12) | (uint)(ord & 0xFFF) | (isNull ? 1L << 62 : 0);
                var brush = isNull ? pal.NullText : pal.Text;
                var t = _cells.Get(key, text, s => f.Create(s, brush));
                var tx = m.IsRightAligned(ord) ? x + w - CellPadX - t.Width : x + CellPadX;
                t.Draw(ctx, new Point(tx, y + (rh - t.Height) / 2));
                if (mark == GridRowMark.Deleted)
                {
                    var mid = Snap(y + rh / 2, scale);
                    ctx.DrawLine(pal.Strike, new Point(tx, mid), new Point(tx + t.Width, mid)); // 지울 행은 취소선
                }
            }
        }

        var lx = Snap(x + w, scale);
        ctx.DrawLine(pal.Line, new Point(lx, hh), new Point(lx, size.Height));

        if (m.HasSelection && m.Active.Col == visCol && m.Active.Row >= first && m.Active.Row <= last)
        {
            var y = yStart + (m.Active.Row - first) * rh;
            ctx.DrawRectangle(null, pal.ActiveBorder, new Rect(x + 0.5, y + 0.5, w - 1, rh - 1));
        }
    }

    private void DrawHeader(DrawingContext ctx, Palette pal, GridTextFactory f, double scale, int ord, double x, double w, double hh, bool isFrozen)
    {
        var m = _model!;
        var clipLeft = isFrozen ? x : Math.Max(x, FrozenWidth());
        var label = m.Columns[ord].Name + (m.SortColumn == ord ? (m.SortDescending ? " ▼" : " ▲") : string.Empty);
        using (ctx.PushClip(new Rect(clipLeft, 0, Math.Max(0, x + w - clipLeft - 1), hh)))
        {
            if (isFrozen)
            {
                ctx.FillRectangle(pal.Header, new Rect(x, 0, w, hh));
            }

            var t = _headers.Get(ord, label, s => f.Create(s, pal.Text));
            t.Draw(ctx, new Point(x + CellPadX, (hh - t.Height) / 2));
        }

        var lx = Snap(x + w, scale);
        ctx.DrawLine(pal.Line, new Point(lx, 0), new Point(lx, hh));
    }

    private static double Snap(double v, double scale) => (Math.Floor(v * scale) + 0.5) / scale;

    // ---- 입력 ----

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && _model is not null)
        {
            GridFontSize += e.Delta.Y > 0 ? 1 : -1;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            ScrollTo(_offsetX - (e.Delta.Y + e.Delta.X) * 60, _offsetY);
        }
        else
        {
            ScrollTo(_offsetX - e.Delta.X * 60, _offsetY - e.Delta.Y * RowHeight * 3);
        }

        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_model is null || !_model.HasResult)
        {
            return;
        }

        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        if (p.Y < HeaderHeight)
        {
            if (_model.IsTextMode)
            {
                return;
            }

            var (col, edge) = HitColumn(p.X);
            if (col < 0)
            {
                return;
            }

            var ord = _model.VisibleColumns[col];
            if (edge <= ResizeGrip && props.IsLeftButtonPressed)
            {
                _resizeColumn = ord;
                _resizeStartX = p.X;
                _resizeStartWidth = _model.GetColumnWidth(ord);
                e.Pointer.Capture(this);
            }
            else if (props.IsLeftButtonPressed)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    _model.SelectColumn(col);
                }
                else
                {
                    _ = SortAsync(ord);
                }
            }
            else if (props.IsRightButtonPressed && !_model.IsSelected(0, col))
            {
                _model.SelectColumn(col);
            }

            e.Handled = true;
            return;
        }

        var (row, c) = HitCell(p);
        if (_model.IsTextMode)
        {
            _model.SelectRow(row, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        }
        else if (c >= 0)
        {
            var cell = new GridCell(row, c);
            if (props.IsRightButtonPressed && _model.IsSelected(row, c))
            {
                return; // 선택 안에서 오른쪽 클릭은 선택을 유지한다
            }

            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                _model.ExtendTo(cell);
            }
            else
            {
                _model.SelectCell(cell);
            }

            _dragSelecting = props.IsLeftButtonPressed;
            if (_dragSelecting)
            {
                e.Pointer.Capture(this);
            }
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_model is null)
        {
            return;
        }

        var p = e.GetPosition(this);
        if (_resizeColumn >= 0)
        {
            _model.SetColumnWidth(_resizeColumn, _resizeStartWidth + p.X - _resizeStartX);
            return;
        }

        if (_dragSelecting)
        {
            // 가장자리에 닿으면 스크롤하면서 선택을 넓힌다.
            var dy = p.Y < HeaderHeight ? p.Y - HeaderHeight : p.Y > Bounds.Height ? p.Y - Bounds.Height : 0;
            var dx = p.X < 0 ? p.X : p.X > Bounds.Width ? p.X - Bounds.Width : 0;
            if (dx != 0 || dy != 0)
            {
                ScrollTo(_offsetX + dx, _offsetY + dy);
            }

            var (row, col) = HitCell(new Point(Math.Clamp(p.X, 0, Bounds.Width - 1), Math.Clamp(p.Y, HeaderHeight, Math.Max(HeaderHeight, Bounds.Height - 1))));
            if (col < 0)
            {
                col = p.X < 0 ? 0 : _model.VisibleColumns.Count - 1;
            }

            _model.ExtendTo(new GridCell(row, col));
            return;
        }

        Cursor = p.Y < HeaderHeight && !_model.IsTextMode && HitColumn(p.X).RightEdgeDistance <= ResizeGrip
            ? new Cursor(StandardCursorType.SizeWestEast)
            : new Cursor(StandardCursorType.Arrow);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragSelecting = false;
        _resizeColumn = -1;
        e.Pointer.Capture(null);
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        ResultGridCommands.Active = _model;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_model is null || !_model.HasResult)
        {
            return;
        }

        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var cur = _model.HasSelection ? _model.Active : new GridCell(0, 0);
        var page = Math.Max(1, (int)(ViewportHeight / RowHeight) - 1);
        GridCell? next = e.Key switch
        {
            Key.Up => cur with { Row = cur.Row - 1 },
            Key.Down => cur with { Row = cur.Row + 1 },
            Key.Left => cur with { Col = cur.Col - 1 },
            Key.Right => cur with { Col = cur.Col + 1 },
            Key.PageUp => cur with { Row = cur.Row - page },
            Key.PageDown => cur with { Row = cur.Row + page },
            Key.Home => ctrl ? new GridCell(0, 0) : cur with { Col = 0 },
            Key.End when !e.KeyModifiers.HasFlag(KeyModifiers.Alt) => ctrl ? new GridCell(_model.RowCount - 1, _model.VisibleColumns.Count - 1) : cur with { Col = _model.VisibleColumns.Count - 1 },
            _ => null,
        };

        if (next is { } n)
        {
            if (shift)
            {
                _model.ExtendTo(n);
            }
            else
            {
                _model.SelectCell(n);
            }

            EnsureVisible(_model.Active);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.A)
        {
            _model.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && _model.HasSelection && !_model.IsTextMode)
        {
            EditRequested?.Invoke(this, _model.Active);
            e.Handled = true;
        }
    }

    private async Task SortAsync(int ordinal)
    {
        try
        {
            await _model!.ToggleSortAsync(ordinal).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 취소·원본 오류는 Model.Error 로 보인다.
        }
    }

    /// <summary>색 묶음. 테마 리소스(<c>OrandaGrid*</c>)가 있으면 그것, 없으면 밝은/어두운 기본값.</summary>
    private sealed class Palette
    {
        public required IBrush Background { get; init; }

        public required IBrush AltRow { get; init; }

        public required IBrush Header { get; init; }

        public required IBrush Selection { get; init; }

        public required IBrush EditedCell { get; init; }

        public required IBrush InsertedRow { get; init; }

        public required IBrush DeletedRow { get; init; }

        public required IPen Strike { get; init; }

        public required IBrush Text { get; init; }

        public required IBrush NullText { get; init; }

        public required IPen Line { get; init; }

        public required IPen FrozenLine { get; init; }

        public required IPen ActiveBorder { get; init; }

        public static Palette Create(Control owner)
        {
            var dark = owner.ActualThemeVariant == ThemeVariant.Dark;
            IBrush Brush(string key, uint light, uint darkColor) =>
                owner.TryFindResource(key, owner.ActualThemeVariant, out var v) && v is IBrush b
                    ? b
                    : new ImmutableSolidColorBrush(Color.FromUInt32(dark ? darkColor : light));

            var line = Brush("OrandaGridLineBrush", 0xFFD0D4DA, 0xFF3A3F47);
            var accent = Brush("OrandaGridAccentBrush", 0xFF2F6FDE, 0xFF5B9BFF);
            return new Palette
            {
                Background = Brush("OrandaGridBackgroundBrush", 0xFFFFFFFF, 0xFF1E2024),
                AltRow = Brush("OrandaGridAltRowBrush", 0xFFF6F8FA, 0xFF24272C),
                Header = Brush("OrandaGridHeaderBrush", 0xFFE8EAEE, 0xFF2D3138),
                Selection = Brush("OrandaGridSelectionBrush", 0xFFCFE0FB, 0xFF2B4A7A),
                EditedCell = Brush("OrandaGridEditedBrush", 0xFFFFF4CE, 0xFF4A3F12),
                InsertedRow = Brush("OrandaGridInsertedBrush", 0xFFE6F4EA, 0xFF1E3A27),
                DeletedRow = Brush("OrandaGridDeletedBrush", 0xFFFDE7E9, 0xFF4A1F24),
                Strike = new Pen(Brush("OrandaGridStrikeBrush", 0xFFC42B1C, 0xFFFF6B5E), 1),
                Text = Brush("OrandaGridTextBrush", 0xFF202020, 0xFFDCDDDE),
                NullText = Brush("OrandaGridNullBrush", 0xFF9AA0A6, 0xFF7C828A),
                Line = new Pen(line, 1),
                FrozenLine = new Pen(accent, 1),
                ActiveBorder = new Pen(accent, 1),
            };
        }
    }
}
