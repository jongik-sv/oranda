using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Oranda.Core.Text;

namespace Oranda.App.Features.SqlEditor;

/// <summary>커서 옆 괄호·BEGIN/END 짝 두 곳에 배경을 칠한다(EDIT-06).</summary>
public sealed class BracketHighlightRenderer : IBackgroundRenderer
{
    private static readonly IBrush Fill = new ImmutableSolidColorBrush(Color.FromArgb(0x50, 0x70, 0xA0, 0xE0));

    public TextRange? First { get; private set; }

    public TextRange? Second { get; private set; }

    public KnownLayer Layer => KnownLayer.Selection;

    public void Set((TextRange Here, TextRange Other)? match)
    {
        First = match?.Here;
        Second = match?.Other;
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        foreach (var r in new[] { First, Second })
        {
            if (r is not { } range)
            {
                continue;
            }

            var seg = new TextSegment { StartOffset = range.Start, Length = range.Length };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, seg))
            {
                drawingContext.FillRectangle(Fill, rect);
            }
        }
    }
}

/// <summary>북마크가 걸린 줄 전체에 옅은 배경을 칠한다(EDIT-07).</summary>
public sealed class BookmarkRenderer(SqlEditorViewModel vm) : IBackgroundRenderer
{
    private static readonly IBrush Fill = new ImmutableSolidColorBrush(Color.FromArgb(0x38, 0x40, 0x90, 0xFF));

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        foreach (int lineNo in vm.BookmarkLines)
        {
            if (lineNo > vm.Document.LineCount)
            {
                continue;
            }

            var line = vm.Document.GetLineByNumber(lineNo);
            var seg = new TextSegment { StartOffset = line.Offset, Length = line.Length };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, seg))
            {
                drawingContext.FillRectangle(Fill, new Rect(0, rect.Y, textView.Bounds.Width, rect.Height));
            }
        }
    }
}

/// <summary>
/// 실행한 범위를 잠깐 칠한다(실행 범위 강조, DataGrip·SQL Developer 의 실행 문장 표시). <see cref="HoldTime"/> 동안 그대로 두고
/// <see cref="FadeTime"/> 동안 옅어지며 사라진다. 타이머는 강조가 있는 동안만 돌고, 붓은 미리 만든 몇 단계를 돌려 쓴다(그릴 때마다 만들지 않는다).
/// 강조가 없으면 <see cref="Draw"/> 는 바로 돌아가므로 입력 경로에 짐이 없다.
/// </summary>
public sealed class RangeFlashRenderer : IBackgroundRenderer
{
    /// <summary>처음 진하게 머무는 시간.</summary>
    public static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(200);

    /// <summary>머문 뒤 사라지기까지 걸리는 시간.</summary>
    public static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(500);

    private const int Steps = 8;
    private const int MaxAlpha = 0x60;
    private static readonly IBrush[] Fills = BuildFills();

    private readonly TextView _view;
    private readonly DispatcherTimer _timer;
    private readonly TextSegment _segment = new();
    private long _startedAt;
    private int _step = -1; // -1 = 강조 없음

    public RangeFlashRenderer(TextView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        _view = view;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) => Advance(Stopwatch.GetElapsedTime(_startedAt));
    }

    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>지금 칠하는 범위(없으면 null).</summary>
    public TextRange? Range => _step < 0 ? null : new TextRange(_segment.StartOffset, _segment.Length);

    /// <summary>지금 붓 단계(0 = 가장 진함, -1 = 강조 없음).</summary>
    public int Step => _step;

    public bool IsTimerRunning => _timer.IsEnabled;

    /// <summary>경과 시간에 맞는 붓 단계(0 = 가장 진함). 다 사라졌으면 -1.</summary>
    public static int StepAt(TimeSpan elapsed)
    {
        if (elapsed < HoldTime)
        {
            return 0;
        }

        double fade = (elapsed - HoldTime) / FadeTime;
        return fade >= 1 ? -1 : Math.Min(Steps - 1, (int)(fade * Steps));
    }

    /// <summary>범위를 처음부터 다시 칠한다(앞의 강조가 남아 있으면 바꾼다).</summary>
    public void Flash(TextRange range)
    {
        _segment.StartOffset = range.Start;
        _segment.Length = range.Length;
        _startedAt = Stopwatch.GetTimestamp();
        _step = 0;
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }

        _view.InvalidateLayer(Layer);
    }

    /// <summary>강조를 지우고 타이머를 멈춘다(글자가 바뀌거나 View 가 떨어질 때). 강조가 없으면 아무것도 하지 않는다(키마다 불린다).</summary>
    public void Clear()
    {
        if (_timer.IsEnabled)
        {
            _timer.Stop();
        }

        if (_step >= 0)
        {
            _step = -1;
            _view.InvalidateLayer(Layer);
        }
    }

    /// <summary>경과 시간으로 단계를 옮긴다(타이머가 부르며, 시험은 직접 부른다). 단계가 바뀔 때만 다시 그린다.</summary>
    public void Advance(TimeSpan elapsed)
    {
        int step = _step < 0 ? -1 : StepAt(elapsed);
        if (step < 0)
        {
            Clear();
        }
        else if (step != _step)
        {
            _step = step;
            _view.InvalidateLayer(Layer);
        }
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_step < 0 || textView.Document is not { } doc || _segment.EndOffset > doc.TextLength)
        {
            return;
        }

        var fill = Fills[_step];
        foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, _segment))
        {
            drawingContext.FillRectangle(fill, rect);
        }
    }

    private static IBrush[] BuildFills()
    {
        // 밝은·어두운 테마 모두에서 보이는 호박색. 단계마다 알파만 줄인다.
        var fills = new IBrush[Steps];
        for (int i = 0; i < Steps; i++)
        {
            fills[i] = new ImmutableSolidColorBrush(Color.FromArgb((byte)(MaxAlpha * (Steps - i) / Steps), 0xF2, 0xB8, 0x3A));
        }

        return fills;
    }
}
