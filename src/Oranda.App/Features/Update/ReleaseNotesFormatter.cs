namespace Oranda.App.Features.Update;

/// <summary>한 줄 안의 글자 조각. <see cref="Bold"/> = 굵게(**…** 또는 머리글), <see cref="Code"/> = 고정폭(`…`).</summary>
public readonly record struct NoteSpan(string Text, bool Bold, bool Code);

/// <summary>릴리스 노트 한 줄. <see cref="Bold"/> 는 머리글이다. <see cref="Text"/> 는 인라인 표시(**·`)를 그대로 담고, 그릴 조각은 <see cref="Spans"/> 다.</summary>
public readonly record struct NoteLine(string Text, bool Bold)
{
    /// <summary>줄을 굵게·고정폭 조각으로 나눈 것. 빈 줄도 빈 조각 하나를 낸다.</summary>
    public IReadOnlyList<NoteSpan> Spans => ReleaseNotesFormatter.SplitInline(Text, Bold);
}

/// <summary>마크다운 릴리스 노트를 읽기 좋은 줄 목록으로 바꾼다: 머리글(#)은 굵은 글자, 목록(-·*)은 「• 」, 나머지는 그대로.
/// 줄 안의 **굵게**·`코드` 는 <see cref="SplitInline"/> 이 조각으로 나눈다. 짝이 없는 기호는 글자 그대로 둔다.</summary>
public static class ReleaseNotesFormatter
{
    public static IReadOnlyList<NoteLine> Format(string? markdown)
    {
        var lines = new List<NoteLine>();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return lines;
        }

        foreach (var raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                var title = trimmed.TrimStart('#');
                if (title.Length == 0 || title[0] == ' ')
                {
                    lines.Add(new NoteLine(title.Trim(), true));
                    continue;
                }
            }

            if (trimmed.Length >= 2 && (trimmed[0] is '-' or '*') && trimmed[1] == ' ')
            {
                var indent = line[..(line.Length - trimmed.Length)];
                lines.Add(new NoteLine(indent + "• " + trimmed[2..].TrimStart(), false));
                continue;
            }

            lines.Add(new NoteLine(line, false));
        }

        return lines;
    }

    /// <summary>한 줄을 조각으로 나눈다. <paramref name="bold"/> 가 참이면(머리글) 모든 조각이 굵다.
    /// 「**」 짝 안은 굵게(안의 `…` 는 고정폭), 「`」 짝 안은 고정폭(안의 ** 는 글자 그대로). 짝이 없거나 속이 빈 기호는 글자로 남긴다.</summary>
    public static IReadOnlyList<NoteSpan> SplitInline(string text, bool bold = false)
    {
        var spans = new List<NoteSpan>();
        Split(text ?? string.Empty, bold, allowBold: true, spans);
        if (spans.Count == 0)
        {
            spans.Add(new NoteSpan(string.Empty, bold, false));
        }

        return spans;
    }

    private static void Split(string text, bool bold, bool allowBold, List<NoteSpan> spans)
    {
        var plain = new System.Text.StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    Flush(plain, bold, spans);
                    spans.Add(new NoteSpan(text[(i + 1)..end], bold, true));
                    i = end + 1;
                    continue;
                }
            }
            else if (allowBold && text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush(plain, bold, spans);
                    Split(text[(i + 2)..end], bold: true, allowBold: false, spans);
                    i = end + 2;
                    continue;
                }
            }

            plain.Append(text[i]);
            i++;
        }

        Flush(plain, bold, spans);
    }

    private static void Flush(System.Text.StringBuilder plain, bool bold, List<NoteSpan> spans)
    {
        if (plain.Length > 0)
        {
            spans.Add(new NoteSpan(plain.ToString(), bold, false));
            plain.Clear();
        }
    }
}
