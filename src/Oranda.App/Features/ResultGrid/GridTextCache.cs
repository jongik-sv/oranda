using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Oranda.App.Features.ResultGrid;

/// <summary>
/// 셀 글자 1개의 그리기 단위. 크기를 미리 잡아 두고 <see cref="Draw"/> 만 부른다.
/// ASCII 는 shaping 없는 <see cref="GlyphRun"/>, 그 밖은 <see cref="TextLayout"/>(D-008, spikes/TSK-01-06).
/// </summary>
public sealed class GridText : IDisposable
{
    private readonly GlyphRun? _glyphs;
    private readonly TextLayout? _layout;
    private readonly IBrush? _brush;

    public GridText(string text, GlyphRun glyphs, double lineHeight, IBrush brush)
    {
        Text = text;
        _glyphs = glyphs;
        _brush = brush;
        Width = glyphs.Bounds.Width;
        Height = lineHeight;
    }

    public GridText(string text, TextLayout layout)
    {
        Text = text;
        _layout = layout;
        Width = layout.WidthIncludingTrailingWhitespace;
        Height = layout.Height;
    }

    public string Text { get; }

    public double Width { get; }

    public double Height { get; }

    public void Draw(DrawingContext ctx, Point p)
    {
        if (_glyphs is not null)
        {
            using (ctx.PushTransform(Matrix.CreateTranslation(p.X, p.Y)))
            {
                ctx.DrawGlyphRun(_brush!, _glyphs);
            }
        }
        else
        {
            _layout!.Draw(ctx, p);
        }
    }

    public void Dispose()
    {
        _glyphs?.Dispose();
        _layout?.Dispose();
    }
}

/// <summary>
/// 글꼴 1개·크기 1개에 대한 글자 만들기. ASCII 는 글리프 번호·advance 표로 shaping 없이 GlyphRun 을 만든다(합자·커닝 없음).
/// </summary>
public sealed class GridTextFactory
{
    private readonly Typeface _face;
    private readonly double _size;
    private readonly GlyphTypeface? _gt;
    private readonly ushort[] _glyph = new ushort[128];
    private readonly double[] _adv = new double[128];
    private readonly double _ascent;

    public GridTextFactory(Typeface face, double size)
    {
        _face = face;
        _size = size;
        _gt = face.GlyphTypeface;
        if (_gt is null)
        {
            LineHeight = size * 1.3;
            CharWidth = size * 0.6;
            return;
        }

        var scale = size / _gt.Metrics.DesignEmHeight;
        _ascent = -_gt.Metrics.Ascent * scale;
        LineHeight = (_gt.Metrics.Descent - _gt.Metrics.Ascent + _gt.Metrics.LineGap) * scale;
        for (var c = 32; c < 127; c++)
        {
            _glyph[c] = _gt.CharacterToGlyphMap.GetGlyph(c);
            _adv[c] = _gt.TryGetHorizontalGlyphAdvance(_glyph[c], out var a) ? a * scale : 0;
        }

        CharWidth = _adv['0'] > 0 ? _adv['0'] : size * 0.6;
    }

    public double LineHeight { get; }

    /// <summary>숫자 '0' 의 너비(고정폭 글꼴이면 글자 1칸).</summary>
    public double CharWidth { get; }

    public double FontSize => _size;

    public GridText Create(string s, IBrush brush)
    {
        if (_gt is not null && TryGlyphRun(s) is { } run)
        {
            return new GridText(s, run, LineHeight, brush);
        }

        return new GridText(s, new TextLayout(s, _face, _size, brush));
    }

    private GlyphRun? TryGlyphRun(string s)
    {
        var infos = new GlyphInfo[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch is < ' ' or > '~')
            {
                return null;
            }

            infos[i] = new GlyphInfo(_glyph[ch], i, _adv[ch], default);
        }

        return new GlyphRun(_gt!, _size, s.AsMemory(), infos, new Point(0, _ascent), 0);
    }
}

/// <summary>
/// 2세대 글자 캐시(이번 프레임 / 직전 프레임). 화면에 그려진 셀만 남기므로 스크롤해도 메모리가 늘지 않는다.
/// 키는 <c>행 &lt;&lt; 12 | 열</c>. 같은 키라도 글자가 바뀌면 다시 만든다.
/// </summary>
public sealed class GridTextCache
{
    private Dictionary<long, GridText> _cur = new(2048);
    private Dictionary<long, GridText> _prev = new(2048);

    public int Count => _cur.Count;

    /// <summary>프레임 시작: 직전 프레임에서도 안 쓰인 항목을 버린다.</summary>
    public void BeginFrame()
    {
        foreach (var t in _prev.Values)
        {
            t.Dispose();
        }

        _prev.Clear();
        (_prev, _cur) = (_cur, _prev);
    }

    public GridText Get(long key, string text, Func<string, GridText> create)
    {
        if (_cur.TryGetValue(key, out var t) && t.Text == text)
        {
            return t;
        }

        if (_prev.Remove(key, out t))
        {
            if (t.Text == text)
            {
                _cur[key] = t;
                return t;
            }

            t.Dispose();
        }

        t = create(text);
        _cur[key] = t;
        return t;
    }

    public void Clear()
    {
        foreach (var t in _cur.Values)
        {
            t.Dispose();
        }

        foreach (var t in _prev.Values)
        {
            t.Dispose();
        }

        _cur.Clear();
        _prev.Clear();
    }
}
