using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace GridSpike;

/// <summary>
/// 직접 그리기 가상 그리드 시제품. 셀 컨트롤 0개, 보이는 행·열만 Render 에서 그린다.
/// 텍스트는 셀 단위 FormattedText 캐시(2세대: 이번 프레임 / 직전 프레임).
/// </summary>
public sealed class VirtualGrid : Control
{
    public const double RowHeight = 22, HeaderHeight = 24, FontSize = 12, Pad = 4;

    readonly SyntheticResultSet _rs;
    public readonly PageCache Cache;
    readonly double[] _colX;     // 열 시작 x (누적)
    readonly double[] _colW;
    readonly Typeface _face = new("Consolas");
    readonly IBrush _fg = new ImmutableSolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
    readonly IBrush _headerBg = new ImmutableSolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xEE));
    readonly IBrush _altBg = new ImmutableSolidColorBrush(Color.FromRgb(0xF6, 0xF8, 0xFA));
    readonly IPen _line = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0xD0, 0xD4, 0xDA)), 1);
    Dictionary<long, CellText> _textCur = new(4096), _textPrev = new(4096);
    readonly CellText?[] _headerText;

    public double OffsetX, OffsetY;
    public readonly List<double> RenderMs = new(8192);
    public long TextCreated, TextHits;
    public double PageMsSum, TextMsSum;   // 단계별 누적: 페이지 조회(가짜 fetch 포함), FormattedText 생성
    public int LastVisibleCells;

    public VirtualGrid(SyntheticResultSet rs, int maxPages)
    {
        _rs = rs;
        Cache = new PageCache(rs, maxPages);
        int n = rs.Kinds.Length;
        _colX = new double[n + 1];
        _colW = new double[n];
        for (int c = 0; c < n; c++)
        {
            _colW[c] = rs.Kinds[c] switch { ColKind.Text => 240, ColKind.Date => 150, ColKind.Code => 70, _ => 100 };
            _colX[c + 1] = _colX[c] + _colW[c];
        }
        _headerText = new CellText?[n];
        ClipToBounds = true;
        Focusable = true;
    }

    public double ExtentWidth => _colX[^1];
    public double ExtentHeight => _rs.RowCount * RowHeight;
    public double MaxOffsetY => Math.Max(0, ExtentHeight - (Bounds.Height - HeaderHeight));
    public double MaxOffsetX => Math.Max(0, ExtentWidth - Bounds.Width);

    public void ScrollTo(double x, double y)
    {
        OffsetX = Math.Clamp(x, 0, MaxOffsetX);
        OffsetY = Math.Clamp(y, 0, MaxOffsetY);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ScrollTo(OffsetX - e.Delta.X * 60, OffsetY - e.Delta.Y * RowHeight * 3);
        e.Handled = true;
    }

    /// <summary>텍스트 캐시 방식: formatted = FormattedText(그릴 때마다 줄 재포맷), layout = TextLayout(줄·글리프 1회 shaping 후 재사용).</summary>
    public static string TextMode = "layout";

    GlyphCache? _glyphs;

    public CellText Text(string s)
    {
        if (TextMode == "formatted")
            return new CellText(new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, FontSize, _fg));
        if (TextMode == "glyph")
        {
            _glyphs ??= new GlyphCache(_face, FontSize);
            var run = _glyphs.TryCreate(s);
            if (run != null) return new CellText(run, _glyphs.LineHeight, _fg);
        }
        return new CellText(new Avalonia.Media.TextFormatting.TextLayout(s, _face, FontSize, _fg));
    }

    string Format(Page p, int col, int i) => _rs.Kinds[col] switch
    {
        ColKind.Int64 => ((long[])p.Columns[col])[i].ToString(CultureInfo.InvariantCulture),
        ColKind.Decimal => ((double[])p.Columns[col])[i].ToString("N2", CultureInfo.InvariantCulture),
        ColKind.Date => ((DateTime[])p.Columns[col])[i].ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        _ => ((string[])p.Columns[col])[i],
    };

    CellText Cell(int row, int col, Page p)
    {
        long key = (long)row * 64 + col;
        if (_textCur.TryGetValue(key, out var ft)) { TextHits++; return ft; }
        if (_textPrev.Remove(key, out ft)) { TextHits++; _textCur[key] = ft; return ft; }
        long t = Stopwatch.GetTimestamp();
        ft = Text(Format(p, col, row - p.FirstRow));
        TextMsSum += Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        TextCreated++;
        _textCur[key] = ft;
        return ft;
    }

    public override void Render(DrawingContext ctx)
    {
        long t0 = Stopwatch.GetTimestamp();
        // 세대 교체: 직전 프레임에 그린 셀만 다음 프레임 후보로 남긴다.
        foreach (var old in _textPrev.Values) old.Dispose();   // 직전 프레임 이후 안 쓰인 셀 해제
        _textPrev.Clear();
        (_textPrev, _textCur) = (_textCur, _textPrev);

        var size = Bounds.Size;
        ctx.FillRectangle(Brushes.White, new Rect(size));

        int firstRow = (int)(OffsetY / RowHeight);
        double yStart = HeaderHeight - (OffsetY - firstRow * RowHeight);
        int visRows = (int)Math.Ceiling((size.Height - HeaderHeight) / RowHeight) + 1;
        int lastRow = Math.Min(_rs.RowCount - 1, firstRow + visRows - 1);

        int firstCol = Math.Max(0, Array.BinarySearch(_colX, OffsetX) is var b && b >= 0 ? b : ~b - 1);
        int lastCol = firstCol;
        while (lastCol < _colW.Length - 1 && _colX[lastCol + 1] - OffsetX < size.Width) lastCol++;

        // 교대 행 배경
        for (int r = firstRow; r <= lastRow; r++)
            if ((r & 1) == 1)
                ctx.FillRectangle(_altBg, new Rect(0, yStart + (r - firstRow) * RowHeight, size.Width, RowHeight));

        // 행 → 페이지 조회는 행마다 한 번(열 반복 안에서 재사용)
        int nRows = lastRow - firstRow + 1;
        long tp = Stopwatch.GetTimestamp();
        var pages = new Page[nRows];
        for (int k = 0; k < nRows; k++) pages[k] = Cache.Get(firstRow + k);
        PageMsSum += Stopwatch.GetElapsedTime(tp).TotalMilliseconds;

        int cells = 0;
        for (int c = firstCol; c <= lastCol; c++)
        {
            double x = _colX[c] - OffsetX;
            using (ctx.PushClip(new Rect(x, HeaderHeight, _colW[c] - 1, size.Height - HeaderHeight)))
            {
                bool right = _rs.Kinds[c] is ColKind.Int64 or ColKind.Decimal;
                for (int k = 0; k < nRows; k++)
                {
                    var ft = Cell(firstRow + k, c, pages[k]);
                    double tx = right ? x + _colW[c] - Pad - ft.Width : x + Pad;
                    ft.Draw(ctx, new Point(tx, yStart + k * RowHeight + (RowHeight - ft.Height) / 2));
                    cells++;
                }
            }
            ctx.DrawLine(_line, new Point(x + _colW[c] - 0.5, 0), new Point(x + _colW[c] - 0.5, size.Height));
        }
        for (int k = 0; k <= nRows; k++)
        {
            double y = yStart + k * RowHeight - 0.5;
            ctx.DrawLine(_line, new Point(0, y), new Point(size.Width, y));
        }

        // 머리글
        ctx.FillRectangle(_headerBg, new Rect(0, 0, size.Width, HeaderHeight));
        for (int c = firstCol; c <= lastCol; c++)
        {
            double x = _colX[c] - OffsetX;
            var ft = _headerText[c] ??= Text(_rs.Names[c]);
            using (ctx.PushClip(new Rect(x, 0, _colW[c] - 1, HeaderHeight)))
                ft.Draw(ctx, new Point(x + Pad, (HeaderHeight - ft.Height) / 2));
        }
        LastVisibleCells = cells;
        RenderMs.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
    }
}


/// <summary>캐시 항목: 크기를 미리 잡아 두고 Draw 만 호출한다.</summary>
public sealed class CellText : IDisposable
{
    readonly FormattedText? _f;
    readonly Avalonia.Media.TextFormatting.TextLayout? _l;
    readonly GlyphRun? _g;
    readonly IBrush? _brush;
    public readonly double Width, Height;
    public CellText(FormattedText f) { _f = f; Width = f.Width; Height = f.Height; }
    public CellText(Avalonia.Media.TextFormatting.TextLayout l) { _l = l; Width = l.WidthIncludingTrailingWhitespace; Height = l.Height; }
    public CellText(GlyphRun g, double lineHeight, IBrush brush) { _g = g; _brush = brush; Width = g.Bounds.Width; Height = lineHeight; }
    public void Draw(DrawingContext ctx, Point p)
    {
        if (_g != null) { using (ctx.PushTransform(Matrix.CreateTranslation(p.X, p.Y))) ctx.DrawGlyphRun(_brush!, _g); }
        else if (_l != null) _l.Draw(ctx, p);
        else ctx.DrawText(_f!, p);
    }
    public void Dispose() { _g?.Dispose(); _l?.Dispose(); }
}

/// <summary>
/// ASCII 전용 빠른 경로: 글리프 번호·advance 를 표로 들고 shaping 없이 GlyphRun 을 만든다(합자·커닝 없음).
/// ASCII 밖 문자가 있으면 null → TextLayout 경로.
/// </summary>
public sealed class GlyphCache
{
    readonly GlyphTypeface _gt;
    readonly double _size;
    readonly ushort[] _glyph = new ushort[128];
    readonly double[] _adv = new double[128];
    readonly double _ascent;
    public readonly double LineHeight;

    public GlyphCache(Typeface face, double size)
    {
        _gt = face.GlyphTypeface;
        _size = size;
        double scale = size / _gt.Metrics.DesignEmHeight;
        _ascent = -_gt.Metrics.Ascent * scale;
        LineHeight = (_gt.Metrics.Descent - _gt.Metrics.Ascent + _gt.Metrics.LineGap) * scale;
        for (int c = 32; c < 127; c++)
        {
            _glyph[c] = _gt.CharacterToGlyphMap.GetGlyph(c);
            _adv[c] = _gt.TryGetHorizontalGlyphAdvance(_glyph[c], out var a) ? a * scale : 0;
        }
    }

    public GlyphRun? TryCreate(string s)
    {
        var infos = new Avalonia.Media.TextFormatting.GlyphInfo[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch < 32 || ch > 126) return null;
            infos[i] = new Avalonia.Media.TextFormatting.GlyphInfo(_glyph[ch], i, _adv[ch], default);
        }
        return new GlyphRun(_gt, _size, s.AsMemory(), infos, new Point(0, _ascent), 0);
    }
}
