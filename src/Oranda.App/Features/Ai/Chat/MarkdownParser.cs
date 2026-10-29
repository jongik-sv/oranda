using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Oranda.App.Features.Ai.Chat;

/// <summary>인라인 글꼴 꾸밈.</summary>
[Flags]
public enum MarkdownStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
}

/// <summary>같은 꾸밈이 이어지는 글 한 토막.</summary>
public sealed record MarkdownSpan(string Text, MarkdownStyle Style = MarkdownStyle.None);

/// <summary>표 칸의 정렬.</summary>
public enum MarkdownAlign
{
    Left,
    Center,
    Right,
}

/// <summary>Markdown 블록의 공통 뿌리.</summary>
public abstract record MarkdownBlock;

/// <summary>제목. <see cref="Level"/> 은 1~3(그보다 깊은 제목도 3 으로 본다).</summary>
public sealed record MarkdownHeading(int Level, IReadOnlyList<MarkdownSpan> Spans) : MarkdownBlock;

/// <summary>문단. 문단 안의 줄바꿈은 글의 <c>\n</c> 으로 남는다.</summary>
public sealed record MarkdownParagraph(IReadOnlyList<MarkdownSpan> Spans) : MarkdownBlock;

/// <summary>코드 블록(``` 울타리). <see cref="Language"/> 는 울타리 뒤의 글(없으면 빈 글).</summary>
public sealed record MarkdownCode(string Language, string Code) : MarkdownBlock;

/// <summary>목록 항목 하나. <see cref="Marker"/> 는 앞에 그리는 표식(<c>•</c> 또는 <c>1.</c>).</summary>
public sealed record MarkdownListItem(int Depth, string Marker, IReadOnlyList<MarkdownSpan> Spans);

/// <summary>글머리·번호 목록.</summary>
public sealed record MarkdownList(IReadOnlyList<MarkdownListItem> Items) : MarkdownBlock;

/// <summary>표. 모든 줄이 <see cref="Aligns"/> 와 같은 칸 수를 갖는다.</summary>
public sealed record MarkdownTable(
    IReadOnlyList<IReadOnlyList<MarkdownSpan>> Header,
    IReadOnlyList<MarkdownAlign> Aligns,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<MarkdownSpan>>> Rows) : MarkdownBlock;

/// <summary>
/// 대화 창 AI 글에 쓰는 작은 Markdown 파서(UI 와 분리). 부분 집합만 읽는다: 제목(#~###), 굵게·기울임, 인라인 코드, 코드 블록, 글머리·번호 목록, 표, 문단.
/// 그 밖의 글은 일반 글로 둔다. 어떤 입력에서도 예외를 내지 않는다(깨진 입력은 문단 하나의 일반 글로 돌려준다).
/// 스트리밍 도중의 반쯤 온 글(닫히지 않은 코드 울타리·굵게 표시)도 그대로 읽는다.
/// </summary>
public static partial class MarkdownParser
{
    /// <summary>이보다 긴 글은 해석하지 않고 일반 글로 둔다(화면 멈춤 방지).</summary>
    public const int MaxChars = 200_000;

    private const int MaxInlineDepth = 4;

    /// <summary>강조를 여는 표시에서 닫는 표시를 찾아 보는 최대 글자 수(긴 글에서 열기만 하고 닫지 않는 표시가 많을 때 비용을 막는다).</summary>
    private const int MaxEmphasisSpan = 2_000;

    // 정규식은 모두 앞뒤가 모호하지 않게 쓰고(공백 덩어리를 두 군데가 나눠 갖지 않는다) 시간 제한도 둔다. 제한을 넘기면 Parse 가 일반 글로 돌린다.
    private const int RegexTimeoutMs = 100;

    [GeneratedRegex(@"^ {0,3}(#{1,6})[ \t]+(.*)$", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<marker>[-*+]|\d{1,9}[.)])[ \t]+(?<text>.*)$", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex ListLine();

    [GeneratedRegex(@"^:?-+:?$", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex TableSeparatorCell();

    private static string HeadingText(string captured)
    {
        // 닫는 # 표시(## 제목 ##)를 뗀다.
        var text = captured.TrimEnd();
        var hashes = text.Length - text.TrimEnd('#').Length;
        return hashes > 0 && hashes < text.Length && text[text.Length - hashes - 1] is ' ' or '\t' ? text[..(text.Length - hashes)].TrimEnd() : text;
    }

    private static bool IsTableSeparator(string line)
    {
        var cells = SplitRow(line);
        return cells.Count > 0 && cells.All(c => TableSeparatorCell().IsMatch(c));
    }

    /// <summary>글 전체를 블록 목록으로 읽는다.</summary>
    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return [];
        }

        if (markdown.Length > MaxChars)
        {
            return [Plain(markdown)];
        }

        try
        {
            return ParseCore(markdown);
        }
#pragma warning disable CA1031 // 렌더러가 대화 창을 죽이면 안 된다: 어떤 실패든 일반 글로 보인다.
        catch (Exception)
#pragma warning restore CA1031
        {
            return [Plain(markdown)];
        }
    }

    /// <summary>인라인 표시(굵게·기울임·코드)를 읽어 글 토막으로 나눈다.</summary>
    public static IReadOnlyList<MarkdownSpan> ParseInline(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        try
        {
            return Merge(Inline(text, MarkdownStyle.None, 0));
        }
#pragma warning disable CA1031 // 인라인 해석이 실패하면 그대로 일반 글.
        catch (Exception)
#pragma warning restore CA1031
        {
            return [new MarkdownSpan(text)];
        }
    }

    private static MarkdownParagraph Plain(string markdown) => new([new MarkdownSpan(markdown)]);

    private static List<MarkdownBlock> ParseCore(string markdown)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        List<MarkdownBlock> blocks = [];
        List<string> paragraph = [];
        var i = 0;

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                blocks.Add(new MarkdownParagraph(ParseInline(string.Join('\n', paragraph))));
                paragraph.Clear();
            }
        }

        while (i < lines.Length)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                i++;
                continue;
            }

            if (TryFence(line, out var fence, out var language))
            {
                FlushParagraph();
                i++;
                StringBuilder code = new();
                var first = true;
                while (i < lines.Length && !IsClosingFence(lines[i], fence))
                {
                    if (!first)
                    {
                        code.Append('\n');
                    }

                    code.Append(lines[i]);
                    first = false;
                    i++;
                }

                i++; // 닫는 울타리(스트리밍 중이라 아직 없으면 글 끝)
                blocks.Add(new MarkdownCode(language, code.ToString()));
                continue;
            }

            if (HeadingLine().Match(line) is { Success: true } heading)
            {
                FlushParagraph();
                blocks.Add(new MarkdownHeading(Math.Min(heading.Groups[1].Length, 3), ParseInline(HeadingText(heading.Groups[2].Value))));
                i++;
                continue;
            }

            if (i + 1 < lines.Length && TryTable(lines, i, out var table, out var next))
            {
                FlushParagraph();
                blocks.Add(table);
                i = next;
                continue;
            }

            if (ListLine().IsMatch(line))
            {
                FlushParagraph();
                blocks.Add(ReadList(lines, ref i));
                continue;
            }

            paragraph.Add(line.TrimEnd());
            i++;
        }

        FlushParagraph();
        return blocks;
    }

    private static bool TryFence(string line, out string fence, out string language)
    {
        var trimmed = line.TrimStart();
        fence = string.Empty;
        language = string.Empty;
        if (line.Length - trimmed.Length > 3 || trimmed.Length < 3)
        {
            return false;
        }

        var ch = trimmed[0];
        if (ch is not ('`' or '~'))
        {
            return false;
        }

        var count = 0;
        while (count < trimmed.Length && trimmed[count] == ch)
        {
            count++;
        }

        if (count < 3)
        {
            return false;
        }

        var info = trimmed[count..].Trim();
        if (ch == '`' && info.Contains('`', StringComparison.Ordinal))
        {
            return false; // ```code``` 같은 한 줄 인라인 코드
        }

        fence = new string(ch, count);
        language = info.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return true;
    }

    private static bool IsClosingFence(string line, string fence)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= fence.Length && trimmed.All(c => c == fence[0]);
    }

    private static bool TryTable(string[] lines, int start, out MarkdownTable table, out int next)
    {
        table = null!;
        next = start;
        var headerLine = lines[start];
        var separatorLine = lines[start + 1];
        if (!headerLine.Contains('|', StringComparison.Ordinal) || !IsTableSeparator(separatorLine))
        {
            return false;
        }

        var header = SplitRow(headerLine);
        var separators = SplitRow(separatorLine);
        if (header.Count == 0 || header.Count != separators.Count)
        {
            return false;
        }

        var aligns = separators.Select(s =>
        {
            var left = s.StartsWith(':');
            var right = s.EndsWith(':');
            return left && right ? MarkdownAlign.Center : right ? MarkdownAlign.Right : MarkdownAlign.Left;
        }).ToList();

        List<IReadOnlyList<IReadOnlyList<MarkdownSpan>>> rows = [];
        var i = start + 2;
        while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && lines[i].Contains('|', StringComparison.Ordinal))
        {
            var cells = SplitRow(lines[i]);
            rows.Add(Fit(cells, header.Count));
            i++;
        }

        table = new MarkdownTable(Fit(header, header.Count), aligns, rows);
        next = i;
        return true;
    }

    private static List<IReadOnlyList<MarkdownSpan>> Fit(List<string> cells, int count)
    {
        List<IReadOnlyList<MarkdownSpan>> result = [];
        for (var c = 0; c < count; c++)
        {
            result.Add(c < cells.Count ? ParseInline(cells[c]) : []);
        }

        return result;
    }

    /// <summary>표 줄을 칸으로 나눈다(앞뒤 <c>|</c> 와 칸 둘레 공백을 빼고, <c>\|</c> 와 코드 안의 <c>|</c> 는 칸 경계로 보지 않는다).</summary>
    private static List<string> SplitRow(string line)
    {
        var text = line.Trim();
        List<string> cells = [];
        StringBuilder cell = new();
        var inCode = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\\' && i + 1 < text.Length && text[i + 1] == '|')
            {
                cell.Append('|');
                i++;
            }
            else if (ch == '`')
            {
                inCode = !inCode;
                cell.Append(ch);
            }
            else if (ch == '|' && !inCode)
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else
            {
                cell.Append(ch);
            }
        }

        cells.Add(cell.ToString().Trim());
        if (text.StartsWith('|') && cells.Count > 0)
        {
            cells.RemoveAt(0);
        }

        if (text.EndsWith('|') && !text.EndsWith("\\|", StringComparison.Ordinal) && cells.Count > 0)
        {
            cells.RemoveAt(cells.Count - 1);
        }

        return cells;
    }

    private static MarkdownList ReadList(string[] lines, ref int i)
    {
        List<MarkdownListItem> items = [];
        List<(int Depth, string Marker, StringBuilder Text)> raw = [];
        var indents = new List<int>(); // 깊이를 정하는 들여쓰기 단계
        while (i < lines.Length)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                // 빈 줄 뒤에 같은 목록이 이어지면 계속한다.
                var peek = i + 1;
                while (peek < lines.Length && string.IsNullOrWhiteSpace(lines[peek]))
                {
                    peek++;
                }

                if (peek < lines.Length && ListLine().IsMatch(lines[peek]))
                {
                    i = peek;
                    continue;
                }

                break;
            }

            if (ListLine().Match(line) is { Success: true } m)
            {
                var indent = Indent(m.Groups["indent"].Value);
                while (indents.Count > 0 && indents[^1] > indent)
                {
                    indents.RemoveAt(indents.Count - 1);
                }

                if (indents.Count == 0 || indents[^1] < indent)
                {
                    indents.Add(indent);
                }

                var marker = m.Groups["marker"].Value;
                var bullet = marker is "-" or "*" or "+";
                raw.Add((indents.Count - 1, bullet ? "•" : marker, new StringBuilder(m.Groups["text"].Value.TrimEnd())));
                i++;
                continue;
            }

            // 목록 항목 밑에 들여 쓴 이어지는 줄은 그 항목의 글로 붙인다. 아니면 목록이 끝난다.
            if (raw.Count > 0 && Indent(line) > 0 && !TryFence(line, out _, out _) && !HeadingLine().IsMatch(line))
            {
                raw[^1].Text.Append('\n').Append(line.Trim());
                i++;
                continue;
            }

            break;
        }

        foreach (var (depth, marker, text) in raw)
        {
            items.Add(new MarkdownListItem(depth, marker, ParseInline(text.ToString())));
        }

        return new MarkdownList(items);
    }

    private static int Indent(string line)
    {
        var width = 0;
        foreach (var ch in line)
        {
            if (ch == ' ')
            {
                width++;
            }
            else if (ch == '\t')
            {
                width += 4;
            }
            else
            {
                break;
            }
        }

        return width;
    }

    private static List<MarkdownSpan> Inline(string text, MarkdownStyle style, int depth)
    {
        List<MarkdownSpan> spans = [];
        StringBuilder buffer = new();

        void Flush()
        {
            if (buffer.Length > 0)
            {
                spans.Add(new MarkdownSpan(buffer.ToString(), style));
                buffer.Clear();
            }
        }

        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];

            if (ch == '`')
            {
                var run = RunLength(text, i, '`');
                var close = FindBacktickClose(text, i + run, run);
                if (close >= 0)
                {
                    Flush();
                    var code = text[(i + run)..close];
                    if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0)
                    {
                        code = code[1..^1];
                    }

                    spans.Add(new MarkdownSpan(code, style | MarkdownStyle.Code));
                    i = close + run;
                    continue;
                }

                buffer.Append('`', run);
                i += run;
                continue;
            }

            if (depth < MaxInlineDepth && (ch == '*' || ch == '_'))
            {
                var run = RunLength(text, i, ch);
                if (TryEmphasis(text, i, ch, run, out var innerStart, out var innerEnd, out var consumed, out var flag))
                {
                    Flush();
                    spans.AddRange(Inline(text[innerStart..innerEnd], style | flag, depth + 1));
                    i += consumed;
                    continue;
                }

                buffer.Append(ch, run);
                i += run;
                continue;
            }

            buffer.Append(ch);
            i++;
        }

        Flush();
        return spans;
    }

    /// <summary>
    /// <c>*</c>·<c>_</c> 묶음이 강조를 여는지 본다. <c>***x***</c> 은 굵은 기울임, <c>**x**</c> 은 굵게, <c>*x*</c> 은 기울임.
    /// SQL 글이 강조로 오인되지 않도록 CommonMark 보다 좁게 연다: 여는 표시는 글머리(시작·공백·여는 괄호·따옴표) 뒤에서 글자·숫자·코드·여는 괄호·따옴표 앞에 올 때만 열린다.
    /// 그래서 <c>a * b</c>, <c>SELECT t.*, u.*</c>, <c>COUNT(*)</c>, <c>/*+INDEX(t)*/</c>, <c>TB_ORDER_ITEM</c>, <c>LIKE '%_ORDER_%'</c> 는 모두 일반 글이다.
    /// 역슬래시 이스케이프는 지원하지 않는다(윈도 경로·<c>ESCAPE '\'</c> 의 역슬래시를 그대로 둔다).
    /// </summary>
    private static bool TryEmphasis(string text, int at, char mark, int run, out int innerStart, out int innerEnd, out int consumed, out MarkdownStyle flag)
    {
        innerStart = innerEnd = consumed = 0;
        flag = MarkdownStyle.None;
        var before = at > 0 ? text[at - 1] : ' ';
        var after = at + run < text.Length ? text[at + run] : ' ';
        if (run > 3 || !(char.IsWhiteSpace(before) || before is '(' or '[' or '{' or '"' or '“' or '‘' or '>')
            || !(char.IsLetterOrDigit(after) || after is '`' or '(' or '[' or '"' or '“' or '*' or '_'))
        {
            return false;
        }

        // 같은 길이의 닫는 묶음을 찾는다(너무 먼 곳까지는 찾지 않는다).
        var limit = Math.Min(text.Length, at + run + MaxEmphasisSpan);
        for (var j = at + run; j < limit; j++)
        {
            if (text[j] == '`')
            {
                var ticks = RunLength(text, j, '`');
                var close = FindBacktickClose(text, j + ticks, ticks);
                j = close >= 0 ? close + ticks - 1 : j + ticks - 1;
                continue;
            }

            if (text[j] != mark)
            {
                continue;
            }

            var closeRun = RunLength(text, j, mark);
            var prev = text[j - 1];
            var next = j + closeRun < text.Length ? text[j + closeRun] : ' ';
            // 별표 뒤에는 한국어 조사가 바로 붙을 수 있다(**FULL**을). 밑줄은 단어 끝일 때만 닫는다(%_ORDER_% 같은 LIKE 패턴을 닫지 않는다).
            var closes = closeRun == run && !char.IsWhiteSpace(prev)
                && (mark == '*' || char.IsWhiteSpace(next) || next is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '"' or '”' or '\'');
            if (closes && j > at + run)
            {
                innerStart = at + run;
                innerEnd = j;
                consumed = j + run - at;
                flag = run switch
                {
                    1 => MarkdownStyle.Italic,
                    2 => MarkdownStyle.Bold,
                    _ => MarkdownStyle.Bold | MarkdownStyle.Italic,
                };
                return true;
            }

            j += closeRun - 1;
        }

        return false;
    }

    private static int RunLength(string text, int at, char ch)
    {
        var n = 0;
        while (at + n < text.Length && text[at + n] == ch)
        {
            n++;
        }

        return n;
    }

    private static int FindBacktickClose(string text, int from, int run)
    {
        var j = from;
        while (j < text.Length)
        {
            if (text[j] == '`')
            {
                var n = RunLength(text, j, '`');
                if (n == run)
                {
                    return j;
                }

                j += n;
            }
            else
            {
                j++;
            }
        }

        return -1;
    }

    private static List<MarkdownSpan> Merge(List<MarkdownSpan> spans)
    {
        List<MarkdownSpan> merged = [];
        foreach (var span in spans)
        {
            if (span.Text.Length == 0)
            {
                continue;
            }

            if (merged.Count > 0 && merged[^1].Style == span.Style)
            {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + span.Text };
            }
            else
            {
                merged.Add(span);
            }
        }

        return merged;
    }

    /// <summary>시험·복사용: 글 토막을 꾸밈 없이 이은 일반 글.</summary>
    public static string ToPlainText(IEnumerable<MarkdownSpan> spans) => string.Concat(spans.Select(s => s.Text));
}
