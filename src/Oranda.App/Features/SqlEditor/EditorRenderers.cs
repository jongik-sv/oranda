using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
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
