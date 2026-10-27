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

// ResultGridControl 의 일부: 그리기·팔레트(ResultGridControl.cs 에서 나눔, 동작 불변).
public sealed partial class ResultGridControl
{
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
                var emphasized = !isNull && m.CellEmphasis is not null && m.IsEmphasized(r, ord); // 모니터의 블로커 칸 등
                var key = (r << 12) | (uint)(ord & 0xFFF) | (isNull ? 1L << 62 : 0) | (emphasized ? 1L << 61 : 0);
                var brush = isNull ? pal.NullText : emphasized ? pal.Danger : pal.Text;
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

        /// <summary>강조 칸(<see cref="ResultGridModel.CellEmphasis"/>) 글자 색.</summary>
        public required IBrush Danger { get; init; }

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
                Danger = Brush("OrandaDangerForegroundBrush", 0xFFC42B1C, 0xFFFF6B5E),
                Line = new Pen(line, 1),
                FrozenLine = new Pen(accent, 1),
                ActiveBorder = new Pen(accent, 1),
            };
        }
    }
}
