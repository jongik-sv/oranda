using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Shell.Appearance;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

/// <summary>
/// 직접 그리기 가상 결과 그리드(TSK-06-03, D-008). 셀 컨트롤 0개, 보이는 행·열만 <see cref="Render"/> 에서 그린다.
/// 상태는 <see cref="ResultGridModel"/>, 스크롤바는 View 가 <see cref="ScrollChanged"/> 로 맞춘다.
/// 왼쪽 좁은 행 머리에 현재 행(셀 커서가 있는 행) ▶ 와 편집 행 상태(새 행 *, 고친 행 ✎, 지울 행 ✕, 실패 !)를 그리고,
/// 현재 행 전체에 옅은 띠를 깐다(오렌지·Toad·SQL Developer 처럼). 텍스트 결과 모드에는 행 머리가 없다.
/// </summary>
public sealed class ResultGridControl : Control
{
    private const double CellPadX = 6;
    private const double RowPadY = 3;
    private const double ResizeGrip = 4;

    /// <summary>머리글 오른쪽 ▾(자동 필터) 영역 너비. 크기 조절 손잡이 안쪽에 둔다.</summary>
    private const double FilterButtonWidth = 16;

    /// <summary>▾ 를 둘 수 있는 가장 좁은 열 너비(이보다 좁으면 제목이 가려지므로 두지 않는다).</summary>
    private const double FilterButtonMinColumn = 56;

    private readonly GridTextCache _cells = new();
    private readonly GridTextCache _headers = new();
    private readonly GridTextCache _rowHeads = new();
    private GridTextFactory? _factory;
    private StreamGeometry? _marker;
    private double _markerSize;
    private ResultGridModel? _model;
    private Palette? _palette;
    private double _fontSize = 13;
    private FontFamily _fontFamily = new(ThemeService.DefaultGridFontFamily);
    private IDisposable? _fontSizeSub;
    private IDisposable? _fontFamilySub;
    private double _offsetX;
    private double _offsetY;
    private bool _dragSelecting;
    private bool _dragRows;
    private int _resizeColumn = -1;
    private int _hoverHeaderCol = -1;

    // 머리글 누르기·끌기(TSK-13-17): 누른 열 위치, 누른 점, 끌기 중인지, 놓일 자리(그 위치 앞). 끌기 없이 놓으면 정렬, 끌고 놓으면 열 순서만 바꾼다.
    private int _headerPressCol = -1;
    private Point _headerPressPoint;
    private bool _headerDragging;
    private int _dropIndex = -1;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private TopLevel? _top;

    public ResultGridControl()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    /// <summary>
    /// 머리글 ▾ 를 눌렀다(열 자동 필터, TSK-13-26). 팝업을 띄우는 쪽(결과 영역)이 받는다.
    /// ▾ 를 누르는 일은 정렬도 열 끌기도 시작하지 않는다.
    /// </summary>
    public event EventHandler<ColumnFilterRequestedEventArgs>? ColumnFilterRequested;

    /// <summary>보이는 열 위치 <paramref name="visibleCol"/> 의 자동 필터를 열어 달라고 알린다(▾ 누르기와 같다).</summary>
    public void RequestColumnFilter(int visibleCol)
    {
        if (_model is not { HasResult: true, IsTextMode: false } m || visibleCol < 0 || visibleCol >= m.VisibleColumns.Count)
        {
            return;
        }

        var ord = m.VisibleColumns[visibleCol];
        var x = RowHeaderWidth + ColumnLeft(visibleCol) - (visibleCol < m.FrozenCount ? 0 : _offsetX);
        ColumnFilterRequested?.Invoke(this, new ColumnFilterRequestedEventArgs(ord, new Rect(x, 0, m.GetColumnWidth(ord), HeaderHeight)));
    }

    /// <summary>스크롤 위치·범위가 바뀌었다(스크롤바 갱신용).</summary>
    public event EventHandler? ScrollChanged;

    /// <summary>
    /// 사용자가 Ctrl+휠로 바꾼 글자 크기를 설정에 저장하는 길. 앱이 시작할 때 채운다(<see cref="ThemeService"/>).
    /// 저장하면 앱 리소스가 바뀌어 열린 모든 그리드가 같은 크기를 따른다.
    /// </summary>
    public static Func<double, Task>? FontSizeSaver { get; set; }

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
                _model.RevealRequested -= OnRevealRequested;
                _model.FlashStarted -= OnFlashStarted;
            }

            _model = value;
            _cells.Clear();
            _headers.Clear();
            _rowHeads.Clear();
            _offsetX = _offsetY = 0;
            if (_model is not null)
            {
                _model.Changed += OnModelChanged;
                _model.RevealRequested += OnRevealRequested;
                _model.FlashStarted += OnFlashStarted;
                if (_model.HasFlash)
                {
                    OnFlashStarted(_model, EventArgs.Empty); // 강조가 컨트롤보다 먼저 시작됐다(다시 읽은 새 그리드)
                }

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

    /// <summary>글꼴. 설정의 결과 그리드 글꼴(앱 리소스)을 따른다.</summary>
    public FontFamily GridFontFamily
    {
        get => _fontFamily;
        set
        {
            if (_fontFamily.Equals(value))
            {
                return;
            }

            _fontFamily = value;
            ResetTextCaches();
            _model?.AutoSizeColumns(Factory.CharWidth);
        }
    }

    private void SubscribeFontResources()
    {
        UnsubscribeFontResources();
        _fontSizeSub = this.GetResourceObservable(ThemeService.GridFontSizeKey).Subscribe(new ActionObserver(v =>
        {
            if (v is double d)
            {
                GridFontSize = d;
            }
        }));
        _fontFamilySub = this.GetResourceObservable(ThemeService.GridFontFamilyKey).Subscribe(new ActionObserver(v =>
        {
            if (v is FontFamily f)
            {
                GridFontFamily = f;
            }
        }));
    }

    private void UnsubscribeFontResources()
    {
        _fontSizeSub?.Dispose();
        _fontFamilySub?.Dispose();
        _fontSizeSub = _fontFamilySub = null;
    }

    private sealed class ActionObserver(Action<object?> onNext) : IObserver<object?>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(object? value) => onNext(value);
    }

    public double RowHeight => Factory.LineHeight + 2 * RowPadY;

    public double HeaderHeight => RowHeight + 2;

    public double OffsetX => _offsetX;

    public double OffsetY => _offsetY;

    public double ExtentWidth => _model is null ? 0 : (_model.IsTextMode ? TextExtentWidth() : RowHeaderWidth + TotalColumnsWidth());

    /// <summary>왼쪽 행 머리 너비(▶ 와 행 상태 글자 한 칸씩). 결과가 없거나 텍스트 결과 모드면 0.</summary>
    public double RowHeaderWidth => _model is null || _model.IsTextMode ? 0 : Math.Ceiling((Factory.CharWidth * 3) + 8);

    public double ExtentHeight => (_model?.RowCount ?? 0) * RowHeight;

    public double ViewportWidth => Bounds.Width;

    public double ViewportHeight => Math.Max(0, Bounds.Height - HeaderHeight);

    public double MaxOffsetY => Math.Max(0, ExtentHeight - ViewportHeight);

    public double MaxOffsetX => Math.Max(0, ExtentWidth - ViewportWidth);

    private GridTextFactory Factory =>
        _factory ??= new GridTextFactory(new Typeface(_fontFamily), _fontSize);

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
            var view = ViewportWidth - ScrollLeft();
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
        SubscribeFontResources();
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
        UnsubscribeFontResources();
        _cells.Clear();
        _headers.Clear();
        _rowHeads.Clear();
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
        _marker = null;
        _cells.Clear();
        _headers.Clear();
        _rowHeads.Clear();
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

    private void OnRevealRequested(object? sender, GridCell cell) => EnsureVisible(cell);

    /// <summary>저장된 행 강조 시간. 지나면 강조를 지운다(입력은 그동안에도 막지 않는다).</summary>
    internal static TimeSpan FlashDuration { get; set; } = TimeSpan.FromMilliseconds(800);

    private DispatcherTimer? _flashTimer;

    private void OnFlashStarted(object? sender, EventArgs e)
    {
        _flashTimer?.Stop();
        _flashTimer = new DispatcherTimer { Interval = FlashDuration };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer?.Stop();
            _model?.ClearFlash();
        };
        _flashTimer.Start();
    }

    // ---- 배치 ----

    private double TotalColumnsWidth() => _model!.VisibleColumns.Sum(c => _model.GetColumnWidth(c));

    private double TextExtentWidth() => (_model!.GetTextHeader().Split(Environment.NewLine)[0].Length + 2) * Factory.CharWidth;

    /// <summary>스크롤하는 열이 시작하는 화면 x(행 머리 + 고정 열). 그 왼쪽은 스크롤해도 움직이지 않는다.</summary>
    private double ScrollLeft() => RowHeaderWidth + FrozenWidth();

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

        var left = RowHeaderWidth + ColumnLeft(cell.Col) - (cell.Col < _model.FrozenCount ? 0 : _offsetX);
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

    /// <summary>
    /// 화면 x 좌표 → (보이는 열 위치, 열 오른쪽 경계까지의 거리). 행 머리·고정 열 영역은 스크롤하지 않는다.
    /// 스크롤 열이 고정 열 밑으로 반쯤 들어가 있으면 보이는 부분만 맞힌다. 행 머리 위면 -1.
    /// </summary>
    private (int Col, double RightEdgeDistance) HitColumn(double x)
    {
        if (_model is null)
        {
            return (-1, double.MaxValue);
        }

        var head = RowHeaderWidth;
        var frozen = ScrollLeft();
        double left = 0;
        for (var i = 0; i < _model.VisibleColumns.Count; i++)
        {
            var w = _model.GetColumnWidth(_model.VisibleColumns[i]);
            var inScrollRegion = i >= _model.FrozenCount;
            var screenLeft = head + (inScrollRegion ? left - _offsetX : left);
            var visibleLeft = inScrollRegion ? Math.Max(screenLeft, frozen) : screenLeft;
            if (x >= visibleLeft && x < screenLeft + w)
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
            _rowHeads.BeginFrame();
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
        var head = RowHeaderWidth;

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
                var x = head + (isFrozen ? left : left - _offsetX);
                left += w;
                if ((pass == 0) == isFrozen || x + w < 0 || x > size.Width)
                {
                    continue;
                }

                DrawColumn(ctx, size, pal, f, scale, i, ord, x, w, first, last, yStart, isFrozen);
            }
        }

        DrawRowHeaders(ctx, size, pal, f, first, last, yStart);

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
                var x = head + (isFrozen ? hx : hx - _offsetX);
                hx += w;
                if ((pass == 0) == isFrozen || x + w < 0 || x > size.Width)
                {
                    continue;
                }

                DrawHeader(ctx, pal, f, scale, ord, x, w, hh, isFrozen);
            }
        }

        ctx.DrawLine(pal.Line, new Point(0, hh - 0.5), new Point(size.Width, hh - 0.5));
        var hx2 = Snap(head, scale);
        ctx.DrawLine(pal.Line, new Point(hx2, 0), new Point(hx2, size.Height));
        if (frozen > 0)
        {
            var fx = Snap(head + frozen, scale);
            ctx.DrawLine(pal.FrozenLine, new Point(fx, 0), new Point(fx, size.Height));
        }

        if (_headerDragging && _headerPressCol >= 0 && _headerPressCol < m.VisibleColumns.Count)
        {
            // 끄는 열은 머리글을 옅게 덮고, 놓일 자리는 세로선으로 보인다.
            var pw = m.GetColumnWidth(m.VisibleColumns[_headerPressCol]);
            ctx.FillRectangle(pal.CurrentRow, new Rect(DropLineXOfColumn(_headerPressCol), 0, pw, hh));
            if (_dropIndex >= 0)
            {
                var lx = Snap(DropLineX(_dropIndex), scale);
                ctx.DrawLine(new Pen(pal.ActiveBorder.Brush, 2), new Point(lx, 0), new Point(lx, size.Height));
            }
        }
    }

    private double DropLineXOfColumn(int col) => DropLineX(col);

    /// <summary>행 머리 글자: 편집 행 상태(새 행 *, 고친 행 ✎, 지울 행 ✕, 반영 실패 !). 상태가 없으면 빈 글자.</summary>
    public static string RowMarkGlyph(GridRowMark mark) => mark switch
    {
        GridRowMark.Inserted => "*",
        GridRowMark.Modified => "✎",
        GridRowMark.Deleted => "✕",
        GridRowMark.Failed => "!",
        _ => string.Empty,
    };

    /// <summary>
    /// 현재 행 ▶ 표시의 화면 사각형(이 컨트롤 좌표). 고른 칸이 없거나, 텍스트 결과 모드거나, 현재 행이 화면 밖이면 null.
    /// 그리기와 같은 계산을 쓴다(시험이 키보드·마우스·스크롤 뒤에 이 값으로 표시 위치를 확인한다).
    /// </summary>
    public Rect? CurrentRowMarkerBounds
    {
        get
        {
            if (_model is not { HasSelection: true } m || m.IsTextMode)
            {
                return null;
            }

            var top = HeaderHeight + (m.Active.Row * RowHeight) - _offsetY;
            if (top + RowHeight <= HeaderHeight || top >= Bounds.Height)
            {
                return null;
            }

            var size = MarkerSize();
            return new Rect(3, top + ((RowHeight - size) / 2), size, size);
        }
    }

    private double MarkerSize() => Math.Max(4, Math.Min(RowHeight * 0.5, Factory.CharWidth * 1.2));

    /// <summary>▶ 모양(크기가 바뀔 때만 다시 만든다).</summary>
    private StreamGeometry Marker(double size)
    {
        if (_marker is null || Math.Abs(_markerSize - size) > 0.01)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(0, 0), true);
                c.LineTo(new Point(size * 0.9, size / 2));
                c.LineTo(new Point(0, size));
                c.EndFigure(true);
            }

            _marker = g;
            _markerSize = size;
        }

        return _marker;
    }

    /// <summary>
    /// 왼쪽 행 머리: 보이는 행만 그린다. 현재 행은 옅은 띠 + ▶, 편집 모드 행은 상태 글자.
    /// 열을 다 그린 뒤 맨 위에 덮으므로 가로 스크롤한 열이 행 머리 밑으로 비치지 않는다.
    /// </summary>
    private void DrawRowHeaders(DrawingContext ctx, Size size, Palette pal, GridTextFactory f, long first, long last, double yStart)
    {
        var m = _model!;
        var rh = RowHeight;
        var hh = HeaderHeight;
        var head = RowHeaderWidth;
        ctx.FillRectangle(pal.Header, new Rect(0, hh, head, Math.Max(0, size.Height - hh)));
        var editing = m.EditOverlay is not null;
        var current = m.HasSelection ? m.Active.Row : -1;
        for (var r = first; r <= last; r++)
        {
            var y = yStart + (r - first) * rh;
            if (r == current)
            {
                ctx.FillRectangle(pal.CurrentRow, new Rect(0, y, head, rh));
                var ms = MarkerSize();
                using (ctx.PushTransform(Matrix.CreateTranslation(3, y + ((rh - ms) / 2))))
                {
                    ctx.DrawGeometry(pal.RowMarker, null, Marker(ms));
                }
            }

            var mark = editing ? m.RowMarkOf(r) : GridRowMark.None;
            if (mark == GridRowMark.None)
            {
                continue;
            }

            var brush = mark is GridRowMark.Deleted or GridRowMark.Failed ? pal.StrikeText : pal.Text;
            var t = _rowHeads.Get((r << 3) | (long)mark, RowMarkGlyph(mark), s => f.Create(s, brush));
            t.Draw(ctx, new Point(head - t.Width - 4, y + ((rh - t.Height) / 2)));
        }
    }

    private void DrawColumn(DrawingContext ctx, Size size, Palette pal, GridTextFactory f, double scale, int visCol, int ord, double x, double w, long first, long last, double yStart, bool isFrozen)
    {
        var m = _model!;
        var rh = RowHeight;
        var hh = HeaderHeight;
        var clipLeft = isFrozen ? x : Math.Max(x, ScrollLeft());
        var current = m.HasSelection ? m.Active.Row : -1;
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
                if (m.EditOverlay is not null && m.NeedsChange(r, ord))
                {
                    editBrush = pal.NeedsChange; // 복제한 새 행의 키 열: 반영 전에 바꿔야 한다
                }

                if (editBrush is not null)
                {
                    ctx.FillRectangle(editBrush, new Rect(x, y, w, rh));
                }

                if (m.IsFlashing(r))
                {
                    ctx.FillRectangle(pal.InsertedRow, new Rect(x, y, w, rh)); // 저장 직후 잠깐(TSK-13-18)
                }

                if (r == current)
                {
                    ctx.FillRectangle(pal.CurrentRow, new Rect(x, y, w, rh)); // 현재 행 띠(반투명, 편집 색 위·선택 색 아래)
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
        var clipLeft = isFrozen ? x : Math.Max(x, ScrollLeft());
        var label = m.Columns[ord].Name + (m.SortColumn == ord ? (m.SortDescending ? " ▼" : " ▲") : string.Empty);
        var filtered = m.LocalFilter.Columns.TryGetValue(ord, out var colFilter) && !colFilter.IsEmpty;
        var hovered = _hoverHeaderCol >= 0 && _hoverHeaderCol < m.VisibleColumns.Count && m.VisibleColumns[_hoverHeaderCol] == ord;
        var showFilter = !m.IsTextMode && w >= FilterButtonMinColumn && (filtered || hovered);
        var textRight = x + w - 1 - (showFilter ? FilterButtonWidth + ResizeGrip : 0);
        using (ctx.PushClip(new Rect(clipLeft, 0, Math.Max(0, textRight - clipLeft), hh)))
        {
            if (isFrozen)
            {
                ctx.FillRectangle(pal.Header, new Rect(x, 0, w, hh));
            }

            var t = _headers.Get(ord, label, s => f.Create(s, pal.Text));
            t.Draw(ctx, new Point(x + CellPadX, (hh - t.Height) / 2));
        }

        if (showFilter)
        {
            var cx = x + w - ResizeGrip - FilterButtonWidth / 2;
            using (ctx.PushClip(new Rect(Math.Max(x, clipLeft), 0, Math.Max(0, x + w - Math.Max(x, clipLeft) - 1), hh)))
            {
                ctx.DrawGeometry(filtered ? pal.ActiveBorder.Brush : pal.NullText, null, FilterGlyph(filtered, cx, hh / 2));
            }
        }

        var lx = Snap(x + w, scale);
        ctx.DrawLine(pal.Line, new Point(lx, 0), new Point(lx, hh));
    }

    /// <summary>머리글 오른쪽의 필터 표시: 걸려 있지 않으면 ▾, 걸려 있으면 깔때기.</summary>
    private static StreamGeometry FilterGlyph(bool funnel, double cx, double cy)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        if (funnel)
        {
            c.BeginFigure(new Point(cx - 5, cy - 4), true);
            c.LineTo(new Point(cx + 5, cy - 4));
            c.LineTo(new Point(cx + 1, cy + 0.5));
            c.LineTo(new Point(cx + 1, cy + 4.5));
            c.LineTo(new Point(cx - 1, cy + 3.5));
            c.LineTo(new Point(cx - 1, cy + 0.5));
        }
        else
        {
            c.BeginFigure(new Point(cx - 4, cy - 2), true);
            c.LineTo(new Point(cx + 4, cy - 2));
            c.LineTo(new Point(cx, cy + 3));
        }

        c.EndFigure(true);
        return g;
    }

    /// <summary>머리글의 ▾ 영역을 맞혔는지: 열 오른쪽 경계까지의 거리가 크기 조절 손잡이 안쪽 <see cref="FilterButtonWidth"/> 안이고 열이 충분히 넓다.</summary>
    private bool IsOnFilterButton(int visibleCol, double rightEdgeDistance) =>
        _model is { IsTextMode: false } m && visibleCol >= 0 && visibleCol < m.VisibleColumns.Count
        && m.GetColumnWidth(m.VisibleColumns[visibleCol]) >= FilterButtonMinColumn
        && rightEdgeDistance > ResizeGrip && rightEdgeDistance <= ResizeGrip + FilterButtonWidth;

    private static double Snap(double v, double scale) => (Math.Floor(v * scale) + 0.5) / scale;

    // ---- 입력 ----

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && _model is not null)
        {
            GridFontSize += e.Delta.Y > 0 ? 1 : -1;
            _ = FontSizeSaver?.Invoke(_fontSize);
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

            if (p.X < RowHeaderWidth)
            {
                _model.SelectAll(); // 행 머리 위 모서리: 전체 선택(Excel·SQL Developer 처럼)
                e.Handled = true;
                return;
            }

            var (col, edge) = HitColumn(p.X);
            if (col < 0)
            {
                return;
            }

            var ord = _model.VisibleColumns[col];
            if (props.IsLeftButtonPressed && IsOnFilterButton(col, edge))
            {
                // ▾: 자동 필터 팝업을 연다. 누른 열·끌기 상태를 기록하지 않으므로 놓아도 정렬하지 않고 끌어도 열이 옮겨지지 않는다.
                RequestColumnFilter(col);
                e.Handled = true;
                return;
            }

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
                    // 누르는 순간에는 정렬하지 않는다. 끌지 않고 놓으면(OnPointerReleased) 그때 정렬한다.
                    _headerPressCol = col;
                    _headerPressPoint = p;
                    _headerDragging = false;
                    _dropIndex = -1;
                    e.Pointer.Capture(this);
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
        else if (p.X < RowHeaderWidth)
        {
            // 행 머리: 그 행 전체를 고르고, 끌면 행 단위로 넓힌다. 고른 행 안에서 오른쪽 클릭은 선택을 유지한다.
            if (!(props.IsRightButtonPressed && _model.IsSelected(row, 0)))
            {
                _model.SelectRow(row, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            }

            _dragSelecting = _dragRows = props.IsLeftButtonPressed;
            if (_dragSelecting)
            {
                e.Pointer.Capture(this);
            }
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

        if (_headerPressCol >= 0)
        {
            if (!_headerDragging && _headerPressCol >= _model.FrozenCount && Math.Abs(p.X - _headerPressPoint.X) >= HeaderDragThreshold(e))
            {
                _headerDragging = true;
            }

            if (_headerDragging)
            {
                _dropIndex = DropIndexAt(p.X);
                InvalidateVisual();
            }

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
            if (_dragRows)
            {
                _model.SelectRow(row, extend: true);
                return;
            }

            if (col < 0)
            {
                col = p.X < RowHeaderWidth ? 0 : _model.VisibleColumns.Count - 1;
            }

            _model.ExtendTo(new GridCell(row, col));
            return;
        }

        var overHeader = p.Y < HeaderHeight && !_model.IsTextMode && p.X >= RowHeaderWidth;
        var (hitCol, hitEdge) = overHeader ? HitColumn(p.X) : (-1, double.MaxValue);
        SetHoverHeader(hitCol);
        Cursor = overHeader && hitEdge <= ResizeGrip
            ? new Cursor(StandardCursorType.SizeWestEast)
            : new Cursor(StandardCursorType.Arrow);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHoverHeader(-1);
    }

    /// <summary>마우스가 올라간 머리글 열을 바꾸고, 바뀌었으면 ▾ 를 보이거나 감추려고 다시 그린다.</summary>
    private void SetHoverHeader(int visibleCol)
    {
        if (_hoverHeaderCol != visibleCol)
        {
            _hoverHeaderCol = visibleCol;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_headerPressCol >= 0 && _model is { } pressed)
        {
            var col = _headerPressCol;
            var dragged = _headerDragging;
            var drop = _dropIndex;
            CancelHeaderPress();
            if (dragged)
            {
                pressed.MoveColumn(col, drop); // 끌었으면 정렬하지 않고 열 순서만 바꾼다
            }
            else if (col < pressed.VisibleColumns.Count)
            {
                _ = SortAsync(pressed.VisibleColumns[col]);
            }
        }

        _dragSelecting = false;
        _dragRows = false;
        _resizeColumn = -1;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        CancelHeaderPress(); // 창을 옮기거나 포인터를 뺏기면 정렬도 이동도 하지 않는다
    }

    /// <summary>머리글 끌기 문턱(DIP). 플랫폼의 누르기 허용 거리, 없으면 4.</summary>
    internal static double HeaderDragThreshold(PointerEventArgs e)
    {
        var tap = Application.Current?.PlatformSettings?.GetTapSize(e.Pointer.Type).Width ?? 0;
        return tap > 0 ? tap : 4;
    }

    /// <summary>머리글 누르기·끌기 상태를 지운다(Esc 취소·놓기·포인터 잃음).</summary>
    private void CancelHeaderPress()
    {
        var was = _headerDragging;
        _headerPressCol = -1;
        _headerDragging = false;
        _dropIndex = -1;
        if (was)
        {
            InvalidateVisual();
        }
    }

    /// <summary>끄는 중인 머리글을 놓을 자리: 화면 x 가 가리키는 열 중간선 기준으로 그 열 앞(마지막 열의 오른쪽이면 열 수). 고정 열 안으로는 놓지 못한다.</summary>
    private int DropIndexAt(double x)
    {
        var m = _model!;
        double left = 0;
        for (var i = 0; i < m.VisibleColumns.Count; i++)
        {
            var w = m.GetColumnWidth(m.VisibleColumns[i]);
            if (i >= m.FrozenCount && x < RowHeaderWidth + left - _offsetX + w / 2)
            {
                return i;
            }

            left += w;
        }

        return m.VisibleColumns.Count;
    }

    /// <summary>열 위치 <paramref name="insertBefore"/> 앞 경계의 화면 x.</summary>
    private double DropLineX(int insertBefore)
    {
        var m = _model!;
        var x = ColumnLeft(insertBefore);
        return RowHeaderWidth + (insertBefore < m.FrozenCount ? x : x - _offsetX);
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

        if (e.Key == Key.Escape && _model.IsSorting)
        {
            _model.CancelSort(); // 정렬하느라 가져오거나 계산하는 중이면 멈춘다(원래 순서 유지)
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _headerPressCol >= 0)
        {
            CancelHeaderPress(); // 끌기를 취소하고, 놓아도 정렬하지 않는다
            e.Handled = true;
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

        public required IBrush StrikeText { get; init; }

        /// <summary>현재 행 띠(반투명: 편집 색이 비치고 선택 색보다 옅다).</summary>
        public required IBrush CurrentRow { get; init; }

        /// <summary>행 머리의 현재 행 ▶.</summary>
        public required IBrush RowMarker { get; init; }

        /// <summary>반영 전에 바꿔야 하는 칸(복제한 새 행의 키 열) 경고 색.</summary>
        public required IBrush NeedsChange { get; init; }

        public required IBrush Text { get; init; }

        public required IBrush NullText { get; init; }

        public required IPen Line { get; init; }

        public required IPen FrozenLine { get; init; }

        public required Pen ActiveBorder { get; init; }

        public static Palette Create(Control owner)
        {
            // 고대비 변형은 어두운 테마를 바탕으로 한다(ThemeService.HighContrastVariant): 어두운 기본값을 쓴다.
            var variant = owner.ActualThemeVariant;
            var dark = variant == ThemeVariant.Dark || variant.InheritVariant == ThemeVariant.Dark;
            IBrush Brush(string key, uint light, uint darkColor) =>
                owner.TryFindResource(key, owner.ActualThemeVariant, out var v) && v is IBrush b
                    ? b
                    : new ImmutableSolidColorBrush(Color.FromUInt32(dark ? darkColor : light));

            var line = Brush("OrandaGridLineBrush", 0xFFD0D4DA, 0xFF3A3F47);
            var accent = Brush("OrandaGridAccentBrush", 0xFF2F6FDE, 0xFF5B9BFF);
            var strike = Brush("OrandaGridStrikeBrush", 0xFFC42B1C, 0xFFFF6B5E);
            return new Palette
            {
                Background = Brush("OrandaGridBackgroundBrush", 0xFFFFFFFF, 0xFF1E2024),
                AltRow = Brush("OrandaGridAltRowBrush", 0xFFF6F8FA, 0xFF24272C),
                Header = Brush("OrandaGridHeaderBrush", 0xFFE8EAEE, 0xFF2D3138),
                Selection = Brush("OrandaGridSelectionBrush", 0xFFCFE0FB, 0xFF2B4A7A),
                EditedCell = Brush("OrandaGridEditedBrush", 0xFFFFF4CE, 0xFF4A3F12),
                InsertedRow = Brush("OrandaGridInsertedBrush", 0xFFE6F4EA, 0xFF1E3A27),
                DeletedRow = Brush("OrandaGridDeletedBrush", 0xFFFDE7E9, 0xFF4A1F24),
                Strike = new Pen(strike, 1),
                StrikeText = strike,
                CurrentRow = Brush("OrandaGridCurrentRowBrush", 0x1A2F6FDE, 0x265B9BFF),
                RowMarker = Brush("OrandaGridRowMarkerBrush", 0xFF2F6FDE, 0xFF5B9BFF),
                NeedsChange = Brush("OrandaGridNeedsChangeBrush", 0xFFFFD8A8, 0xFF6B3A10),
                Text = Brush("OrandaGridTextBrush", 0xFF202020, 0xFFDCDDDE),
                NullText = Brush("OrandaGridNullBrush", 0xFF9AA0A6, 0xFF7C828A),
                Line = new Pen(line, 1),
                FrozenLine = new Pen(accent, 1),
                ActiveBorder = new Pen(accent, 1),
            };
        }
    }
}

/// <summary>머리글 ▾ 를 눌렀다는 알림. <paramref name="Ordinal"/> = 열 ordinal, <paramref name="HeaderBounds"/> = 그 열 머리글 칸(그리드 컨트롤 좌표).</summary>
public sealed class ColumnFilterRequestedEventArgs(int ordinal, Rect headerBounds) : EventArgs
{
    public int Ordinal { get; } = ordinal;

    public Rect HeaderBounds { get; } = headerBounds;
}
