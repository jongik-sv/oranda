using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Oranda.App.Features.MyBatisWorkspace.Combos;

/// <summary>오류 자리(글 안의 한 구간)를 위험 색 글자·지움 색 바탕·굵게 칠한다.</summary>
internal sealed class ComboErrorSpanTransformer : DocumentColorizingTransformer
{
    private int _start;
    private int _length;

    public void Set(int start, int length)
    {
        _start = start;
        _length = length;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_length <= 0)
        {
            return;
        }

        int from = Math.Max(line.Offset, _start);
        int to = Math.Min(line.EndOffset, _start + _length);
        if (from >= to)
        {
            return;
        }

        var variant = CurrentContext.TextView.ActualThemeVariant;
        var app = Avalonia.Application.Current;
        IBrush? back = null;
        IBrush? fore = null;
        if (app is not null)
        {
            back = app.TryFindResource("OrandaDiffRemovedBrush", variant, out var b) ? b as IBrush : null;
            fore = app.TryFindResource("OrandaDangerForegroundBrush", variant, out var f) ? f as IBrush : null;
        }

        ChangeLinePart(from, to, element =>
        {
            var props = element.TextRunProperties;
            if (back is not null)
            {
                props.SetBackgroundBrush(back);
            }

            if (fore is not null)
            {
                props.SetForegroundBrush(fore);
            }

            props.SetTypeface(new Typeface(props.Typeface.FontFamily, props.Typeface.Style, FontWeight.Bold));
        });
    }
}
