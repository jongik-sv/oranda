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
public sealed partial class ResultGridControl : Control
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
                _model.SavedOffsetX = _offsetX;
                _model.SavedOffsetY = _offsetY;
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
                if (_model.SavedOffsetX > 0 || _model.SavedOffsetY > 0)
                {
                    ScrollTo(_model.SavedOffsetX, _model.SavedOffsetY); // 범위를 넘으면 ScrollTo 가 잘라 준다
                }
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
}

/// <summary>머리글 ▾ 를 눌렀다는 알림. <paramref name="Ordinal"/> = 열 ordinal, <paramref name="HeaderBounds"/> = 그 열 머리글 칸(그리드 컨트롤 좌표).</summary>
public sealed class ColumnFilterRequestedEventArgs(int ordinal, Rect headerBounds) : EventArgs
{
    public int Ordinal { get; } = ordinal;

    public Rect HeaderBounds { get; } = headerBounds;
}
