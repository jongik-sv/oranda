using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Oranda.App.Features.Templates;

/// <summary>
/// 찾는 낱말과 맞는 부분을 굵게·강조색으로 그리는 TextBlock 붙임 속성. XAML 에서 <c>Text</c> 대신
/// <c>local:MatchHighlight.Text</c> 와 <c>local:MatchHighlight.Query</c> 를 준다. 맞는 곳이 없으면 그냥 <c>Text</c> 로 둔다.
/// 색은 테마 리소스 <c>OrandaIconBlueBrush</c> 에서 받는다.
/// </summary>
public static class MatchHighlight
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Text", typeof(MatchHighlight));

    public static readonly AttachedProperty<string?> QueryProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Query", typeof(MatchHighlight));

    static MatchHighlight()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>((tb, _) => Apply(tb));
        QueryProperty.Changed.AddClassHandler<TextBlock>((tb, _) => Apply(tb));
    }

    public static string? GetText(TextBlock element) => element.GetValue(TextProperty);

    public static void SetText(TextBlock element, string? value) => element.SetValue(TextProperty, value);

    public static string? GetQuery(TextBlock element) => element.GetValue(QueryProperty);

    public static void SetQuery(TextBlock element, string? value) => element.SetValue(QueryProperty, value);

    /// <summary>
    /// 글에서 찾는 낱말(공백으로 나눔, 대소문자 무시)이 나오는 구간. 겹치거나 붙은 구간은 하나로 합치고 앞에서부터 늘어놓는다.
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> Ranges(string? text, string? query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var found = new List<(int Start, int End)>();
        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            for (var at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(word, at + word.Length, StringComparison.OrdinalIgnoreCase))
            {
                found.Add((at, at + word.Length));
            }
        }

        var merged = new List<(int Start, int Length)>();
        int start = -1, end = -1;
        foreach (var (s, e) in found.OrderBy(r => r.Start))
        {
            if (start >= 0 && s <= end)
            {
                end = Math.Max(end, e);
                continue;
            }

            if (start >= 0)
            {
                merged.Add((start, end - start));
            }

            (start, end) = (s, e);
        }

        if (start >= 0)
        {
            merged.Add((start, end - start));
        }

        return merged;
    }

    private static void Apply(TextBlock tb)
    {
        var text = GetText(tb) ?? string.Empty;
        var ranges = Ranges(text, GetQuery(tb));
        tb.Inlines?.Clear();
        if (ranges.Count == 0)
        {
            tb.Text = text;
            return;
        }

        tb.Text = null;
        var inlines = new InlineCollection();
        var at = 0;
        foreach (var (start, length) in ranges)
        {
            if (start > at)
            {
                inlines.Add(new Run(text[at..start]));
            }

            var match = new Run(text.Substring(start, length)) { FontWeight = FontWeight.Bold };
            match.Bind(TextElement.ForegroundProperty, tb.GetResourceObservable("OrandaIconBlueBrush"));
            inlines.Add(match);
            at = start + length;
        }

        if (at < text.Length)
        {
            inlines.Add(new Run(text[at..]));
        }

        tb.Inlines = inlines;
    }
}
