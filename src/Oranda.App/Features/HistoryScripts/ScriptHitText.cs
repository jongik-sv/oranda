using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Oranda.App.Features.HistoryScripts;

/// <summary>검색 결과의 미리보기 한 줄: <see cref="Source"/> 에서 <see cref="Ranges"/> 구간만 굵게 강조색으로 그린다.</summary>
public sealed class ScriptHitText : TextBlock
{
    public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<ScriptHitText, string?>(nameof(Source));

    public static readonly StyledProperty<IReadOnlyList<ScriptMatchRange>?> RangesProperty = AvaloniaProperty.Register<ScriptHitText, IReadOnlyList<ScriptMatchRange>?>(nameof(Ranges));

    static ScriptHitText()
    {
        SourceProperty.Changed.AddClassHandler<ScriptHitText>((b, _) => b.Rebuild());
        RangesProperty.Changed.AddClassHandler<ScriptHitText>((b, _) => b.Rebuild());
    }

    public ScriptHitText() => ActualThemeVariantChanged += (_, _) => Rebuild();

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public IReadOnlyList<ScriptMatchRange>? Ranges
    {
        get => GetValue(RangesProperty);
        set => SetValue(RangesProperty, value);
    }

    private void Rebuild()
    {
        var source = Source ?? string.Empty;
        Inlines?.Clear();
        if (Ranges is not { Count: > 0 } ranges)
        {
            Text = source;
            return;
        }

        Text = null;
        var inlines = Inlines ??= [];
        IBrush? accent = this.TryFindResource("OrandaIconOrangeBrush", ActualThemeVariant, out var brush) ? brush as IBrush : null; // 강조색은 테마 브러시(라이트·다크 따로)
        var pos = 0;
        foreach (var range in ranges)
        {
            var start = Math.Clamp(range.Start, pos, source.Length);
            var end = Math.Clamp(range.Start + range.Length, start, source.Length);
            if (start > pos)
            {
                inlines.Add(new Run(source[pos..start]));
            }

            if (end > start)
            {
                var hit = new Run(source[start..end]) { FontWeight = FontWeight.Bold };
                if (accent is not null)
                {
                    hit.Foreground = accent;
                }

                inlines.Add(hit);
            }

            pos = end;
        }

        if (pos < source.Length)
        {
            inlines.Add(new Run(source[pos..]));
        }
    }
}
