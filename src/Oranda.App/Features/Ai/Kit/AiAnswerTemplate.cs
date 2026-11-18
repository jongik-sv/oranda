using System.Text;

namespace Oranda.App.Features.Ai.Kit;

/// <summary>답 틀의 한 칸: 제목, 그리고 표 칸이면 열 이름.</summary>
/// <param name="Title">칸 제목(답에 <c>## 제목</c> 으로 쓰게 한다).</param>
/// <param name="Hint">그 칸에 무엇을 쓸지(프롬프트용, 영어).</param>
/// <param name="Columns">표 칸이면 열 이름(답에 이 머리 줄의 Markdown 표를 쓰게 한다). 글 칸이면 null.</param>
public sealed record AiAnswerSection(string Title, string Hint, IReadOnlyList<string>? Columns = null)
{
    public bool IsTable => Columns is { Count: > 0 };
}

/// <summary>읽은 칸 하나. 표 칸이면 <see cref="Rows"/> 에 행이 있다.</summary>
public sealed record AiAnswerPart(string Title, string Body, IReadOnlyList<IReadOnlyList<string>>? Rows = null);

/// <summary>틀대로 읽은 답.</summary>
public sealed record AiTemplatedAnswer(IReadOnlyList<AiAnswerPart> Parts)
{
    /// <summary>제목으로 칸을 찾는다(없으면 null).</summary>
    public AiAnswerPart? this[string title] => Parts.FirstOrDefault(p => string.Equals(p.Title, title, StringComparison.Ordinal));
}

/// <summary>
/// 한 번짜리 요청의 답 틀(설계 §2 「정해진 답 형식」): 칸 제목·표 칸을 프롬프트로 고정하고(<see cref="Rules"/>), 받은 글을 틀대로 읽는다(<see cref="Parse"/>).
/// 틀이 깨지면(제목이 빠짐·차례가 다름·표 머리가 다름) null 을 돌려주고, 화면은 받은 글을 그대로 보인다. 다시 묻지 않는다.
/// </summary>
public sealed class AiAnswerTemplate
{
    public AiAnswerTemplate(IReadOnlyList<AiAnswerSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        if (sections.Count == 0)
        {
            throw new ArgumentException("칸이 하나도 없습니다.", nameof(sections));
        }

        if (sections.Select(s => s.Title).Distinct(StringComparer.Ordinal).Count() != sections.Count)
        {
            throw new ArgumentException("칸 제목이 겹칩니다.", nameof(sections));
        }

        Sections = sections;
    }

    public IReadOnlyList<AiAnswerSection> Sections { get; }

    /// <summary>프롬프트 끝에 붙는 형식 규칙(영어 지시 + 한국어 제목).</summary>
    public string Rules
    {
        get
        {
            var text = new StringBuilder();
            text.AppendLine("Answer in Korean using exactly this Markdown layout, in this order, and nothing before or after it:");
            foreach (var section in Sections)
            {
                text.Append("## ").AppendLine(section.Title);
                if (section.IsTable)
                {
                    text.Append("| ").Append(string.Join(" | ", section.Columns!)).AppendLine(" |");
                    text.Append('|').Append(string.Concat(section.Columns!.Select(_ => " --- |"))).AppendLine();
                    text.Append('(').Append(section.Hint).AppendLine(": one table row per item, keep the header exactly)");
                }
                else
                {
                    text.Append('(').Append(section.Hint).AppendLine(")");
                }
            }

            text.Append("Keep every heading text exactly as written. Never write tool or function names.");
            return text.ToString();
        }
    }

    /// <summary>받은 글을 틀대로 읽는다. 틀이 깨졌으면 null.</summary>
    public AiTemplatedAnswer? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var blocks = SplitByHeadings(text.ReplaceLineEndings("\n"));
        if (blocks is null || blocks.Count != Sections.Count)
        {
            return null;
        }

        var parts = new List<AiAnswerPart>();
        for (var i = 0; i < Sections.Count; i++)
        {
            var section = Sections[i];
            var (title, body) = blocks[i];
            if (!string.Equals(title, section.Title, StringComparison.Ordinal))
            {
                return null;
            }

            if (!section.IsTable)
            {
                parts.Add(new AiAnswerPart(title, body));
                continue;
            }

            if (ParseTable(body, section.Columns!) is not { } rows)
            {
                return null;
            }

            parts.Add(new AiAnswerPart(title, body, rows));
        }

        return new AiTemplatedAnswer(parts);
    }

    /// <summary><c>## 제목</c> 줄로 나눈다. 첫 제목 앞에 글이 있으면 null(틀 밖의 머리말).</summary>
    private static List<(string Title, string Body)>? SplitByHeadings(string text)
    {
        var blocks = new List<(string Title, StringBuilder Body)>();
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                blocks.Add((line[3..].Trim(), new StringBuilder()));
                continue;
            }

            if (blocks.Count == 0)
            {
                if (line.Trim().Length > 0)
                {
                    return null;
                }

                continue;
            }

            blocks[^1].Body.AppendLine(line);
        }

        return [.. blocks.Select(b => (b.Title, b.Body.ToString().Trim()))];
    }

    /// <summary>머리 줄이 <paramref name="columns"/> 와 같은 Markdown 표를 읽는다. 행 칸 수가 다르거나 표가 아니면 null.</summary>
    private static List<IReadOnlyList<string>>? ParseTable(string body, IReadOnlyList<string> columns)
    {
        var lines = body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count < 2 || Cells(lines[0]) is not { } head || !head.SequenceEqual(columns, StringComparer.Ordinal))
        {
            return null;
        }

        if (Cells(lines[1]) is not { } rule || rule.Count != columns.Count || !rule.All(c => c.Length > 0 && c.Trim(':').Length > 0 && c.Trim(':').All(ch => ch == '-')))
        {
            return null;
        }

        var rows = new List<IReadOnlyList<string>>();
        foreach (var line in lines.Skip(2))
        {
            if (Cells(line) is not { } cells || cells.Count != columns.Count)
            {
                return null;
            }

            rows.Add(cells);
        }

        return rows;
    }

    private static List<string>? Cells(string line)
    {
        if (!line.StartsWith('|') || !line.EndsWith('|') || line.Length < 2)
        {
            return null;
        }

        return [.. line[1..^1].Split('|').Select(c => c.Trim())];
    }
}
