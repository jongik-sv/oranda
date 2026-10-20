namespace Oranda.App.Features.Update;

/// <summary>릴리스 노트 한 줄. <see cref="Bold"/> 는 머리글이다.</summary>
public readonly record struct NoteLine(string Text, bool Bold);

/// <summary>마크다운 릴리스 노트를 읽기 좋은 줄 목록으로 바꾼다: 머리글(#)은 굵은 글자, 목록(-·*)은 「• 」, 나머지는 그대로.</summary>
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
}
