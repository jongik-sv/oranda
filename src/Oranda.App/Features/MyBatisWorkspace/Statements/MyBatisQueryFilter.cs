using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Oranda.App.Features.MyBatisWorkspace.Statements;

/// <summary>
/// 「매퍼 쿼리」 창 검색 규칙: 낱말을 공백으로 나누어 모두 들어 있으면 맞다(대소문자 무시, 부분 일치).
/// 쿼리 목록과 매퍼 폴더 트리가 같이 쓴다.
/// </summary>
public static class MyBatisQueryFilter
{
    /// <summary>검색 글을 낱말로 나눈다(빈 칸이면 빈 목록 = 거르지 않음).</summary>
    public static string[] Terms(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>모든 낱말이 <paramref name="text"/> 안에 있다. 낱말이 없으면 맞다.</summary>
    public static bool Matches(string? text, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var term in terms)
        {
            if (text.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>맞은 부분의 (시작, 길이) 구간. 겹치면 합친다.</summary>
    public static IReadOnlyList<(int Start, int Length)> Ranges(string? text, IReadOnlyList<string> terms)
    {
        var found = new List<(int Start, int Length)>();
        if (string.IsNullOrEmpty(text) || terms.Count == 0)
        {
            return found;
        }

        foreach (var term in terms)
        {
            int at = 0;
            while ((at = text.IndexOf(term, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                found.Add((at, term.Length));
                at += term.Length;
            }
        }

        found.Sort();
        var merged = new List<(int Start, int Length)>();
        foreach (var r in found)
        {
            if (merged.Count > 0 && r.Start <= merged[^1].Start + merged[^1].Length)
            {
                var last = merged[^1];
                merged[^1] = (last.Start, Math.Max(last.Length, r.Start + r.Length - last.Start));
            }
            else
            {
                merged.Add(r);
            }
        }

        return merged;
    }
}

/// <summary>검색어에 맞은 부분을 굵게·강조색으로 보이는 글자 칸. <see cref="Terms"/> 가 비면 그냥 글이다.</summary>
public sealed class HighlightTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<HighlightTextBlock, string?>(nameof(Source));

    public static readonly StyledProperty<string?> TermsProperty = AvaloniaProperty.Register<HighlightTextBlock, string?>(nameof(Terms));

    static HighlightTextBlock()
    {
        SourceProperty.Changed.AddClassHandler<HighlightTextBlock>((b, _) => b.Rebuild());
        TermsProperty.Changed.AddClassHandler<HighlightTextBlock>((b, _) => b.Rebuild());
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public string? Terms
    {
        get => GetValue(TermsProperty);
        set => SetValue(TermsProperty, value);
    }

    private void Rebuild()
    {
        var source = Source ?? string.Empty;
        var ranges = MyBatisQueryFilter.Ranges(source, MyBatisQueryFilter.Terms(Terms));
        Inlines?.Clear();
        if (ranges.Count == 0)
        {
            Text = source;
            return;
        }

        Text = null;
        var inlines = Inlines ??= [];
        int pos = 0;
        foreach (var (start, length) in ranges)
        {
            if (start > pos)
            {
                inlines.Add(new Avalonia.Controls.Documents.Run(source[pos..start]));
            }

            inlines.Add(new Avalonia.Controls.Documents.Run(source.Substring(start, length)) { FontWeight = FontWeight.Bold, Foreground = Brushes.DarkOrange });
            pos = start + length;
        }

        if (pos < source.Length)
        {
            inlines.Add(new Avalonia.Controls.Documents.Run(source[pos..]));
        }
    }
}
