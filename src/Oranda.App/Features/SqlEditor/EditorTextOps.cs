using System.Globalization;
using System.Text;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// 글자 바꾸기 결과: <see cref="Start"/> 부터 <see cref="Length"/> 글자를 <see cref="NewText"/> 로 바꾸고,
/// 바꾼 뒤의 선택을 (<see cref="SelectionStart"/>, <see cref="SelectionLength"/>) 로 둔다. 바꿀 것이 없으면 null 을 돌려준다.
/// </summary>
public sealed record TextOpResult(int Start, int Length, string NewText, int SelectionStart, int SelectionLength);

/// <summary>줄 주석·들여쓰기·대소문자·줄 복제/삭제 같은 글자 연산(EDIT-07, 순수 함수 — UI 없이 시험한다).</summary>
public static class EditorTextOps
{
    public const string DefaultIndent = "    ";

    // ---- 줄 도우미 ------------------------------------------------------------------------

    private readonly record struct LineSpan(int Start, int ContentEnd, int NextStart);

    private static LineSpan LineAt(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int start = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
        int nl = text.IndexOf('\n', offset);
        int end = nl < 0 ? text.Length : nl;
        int contentEnd = end > start && text[end - 1] == '\r' ? end - 1 : end;
        return new LineSpan(start, contentEnd, nl < 0 ? text.Length : nl + 1);
    }

    /// <summary>선택이 닿는 줄들. 선택이 다음 줄 맨 앞에서 끝나면 그 줄은 뺀다.</summary>
    private static List<LineSpan> CoveredLines(string text, int selStart, int selLen)
    {
        var lines = new List<LineSpan>();
        var first = LineAt(text, selStart);
        lines.Add(first);
        int end = selStart + selLen;
        var cur = first;
        while (cur.NextStart <= end && cur.NextStart < text.Length)
        {
            if (cur.NextStart == end && selLen > 0)
            {
                break; // 선택 끝이 줄 맨 앞
            }

            cur = LineAt(text, cur.NextStart);
            lines.Add(cur);
        }

        return lines;
    }

    private static string Newline(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>줄 목록을 한 번에 바꾼다. <paramref name="transform"/> 이 null 을 돌려주면 그 줄은 그대로.</summary>
    private static TextOpResult? MapLines(string text, int selStart, int selLen, Func<string, string?> transform)
    {
        var lines = CoveredLines(text, selStart, selLen);
        int regionStart = lines[0].Start, regionEnd = lines[^1].ContentEnd;
        var sb = new StringBuilder();
        bool changed = false;
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var old = text.Substring(l.Start, l.ContentEnd - l.Start);
            var nw = transform(old);
            if (nw is not null && nw != old)
            {
                changed = true;
            }

            sb.Append(nw ?? old);
            if (i < lines.Count - 1)
            {
                sb.Append(text, l.ContentEnd, l.NextStart - l.ContentEnd);
            }
        }

        if (!changed)
        {
            return null;
        }

        var replaced = sb.ToString();
        return new TextOpResult(regionStart, regionEnd - regionStart, replaced, regionStart, replaced.Length);
    }

    // ---- 주석 ---------------------------------------------------------------------------

    private static bool IsLineCommented(string line) => line.AsSpan().TrimStart().StartsWith("--");

    public static TextOpResult? ToggleLineComment(string text, int selStart, int selLen)
    {
        var lines = CoveredLines(text, selStart, selLen);
        bool allCommented = lines
            .Select(l => text.Substring(l.Start, l.ContentEnd - l.Start))
            .Where(s => s.Trim().Length > 0)
            .All(IsLineCommented);
        return allCommented ? UncommentLines(text, selStart, selLen) : CommentLines(text, selStart, selLen);
    }

    public static TextOpResult? CommentLines(string text, int selStart, int selLen)
        => MapLines(text, selStart, selLen, line =>
        {
            if (line.Trim().Length == 0)
            {
                return null;
            }

            int indent = line.Length - line.TrimStart().Length;
            return line.Insert(indent, "-- ");
        });

    public static TextOpResult? UncommentLines(string text, int selStart, int selLen)
        => MapLines(text, selStart, selLen, line =>
        {
            int indent = line.Length - line.TrimStart().Length;
            if (!line.AsSpan(indent).StartsWith("--"))
            {
                return null;
            }

            int cut = line.Length > indent + 2 && line[indent + 2] == ' ' ? 3 : 2;
            return line.Remove(indent, cut);
        });

    /// <summary>선택(없으면 현재 줄의 글자 부분)을 <c>/* */</c> 로 감싸거나, 이미 감싸져 있으면 벗긴다.</summary>
    public static TextOpResult? ToggleBlockComment(string text, int selStart, int selLen)
    {
        if (selLen == 0)
        {
            var l = LineAt(text, selStart);
            var content = text.Substring(l.Start, l.ContentEnd - l.Start);
            int lead = content.Length - content.TrimStart().Length;
            int trail = content.Length - content.TrimEnd().Length;
            if (content.Trim().Length == 0)
            {
                return null;
            }

            selStart = l.Start + lead;
            selLen = content.Length - lead - trail;
        }

        var sel = text.Substring(selStart, selLen);
        var trimmed = sel.Trim();
        if (trimmed.Length >= 4 && trimmed.StartsWith("/*", StringComparison.Ordinal) && trimmed.EndsWith("*/", StringComparison.Ordinal))
        {
            int lead = sel.IndexOf("/*", StringComparison.Ordinal);
            int tailIdx = sel.LastIndexOf("*/", StringComparison.Ordinal);
            var inner = sel.Substring(lead + 2, tailIdx - lead - 2);
            if (inner.StartsWith(' '))
            {
                inner = inner[1..];
            }

            if (inner.EndsWith(' '))
            {
                inner = inner[..^1];
            }

            var rebuilt = sel[..lead] + inner + sel[(tailIdx + 2)..];
            return new TextOpResult(selStart, selLen, rebuilt, selStart, rebuilt.Length);
        }

        var wrapped = "/* " + sel + " */";
        return new TextOpResult(selStart, selLen, wrapped, selStart, wrapped.Length);
    }

    // ---- 들여쓰기 ------------------------------------------------------------------------

    public static TextOpResult? Indent(string text, int selStart, int selLen, string? unit = null)
    {
        unit ??= DefaultIndent;
        return MapLines(text, selStart, selLen, line => line.Length == 0 ? null : unit + line);
    }

    public static TextOpResult? Outdent(string text, int selStart, int selLen, string? unit = null)
    {
        unit ??= DefaultIndent;
        return MapLines(text, selStart, selLen, line =>
        {
            if (line.StartsWith('\t'))
            {
                return line[1..];
            }

            int n = 0;
            while (n < unit.Length && n < line.Length && line[n] == ' ')
            {
                n++;
            }

            return n == 0 ? null : line[n..];
        });
    }

    // ---- 대소문자 ------------------------------------------------------------------------

    public enum CaseMode
    {
        Upper,
        Lower,
        Title,
        Sentence,
        Invert,
        Cycle,
    }

    /// <summary>선택을 바꾼다. 선택이 없으면 커서가 닿은 낱말(영문·숫자·밑줄·$·#)을 바꾼다.</summary>
    public static TextOpResult? ChangeCase(string text, int selStart, int selLen, CaseMode mode)
    {
        if (selLen == 0)
        {
            int s = selStart, e = selStart;
            while (s > 0 && IsWordChar(text[s - 1]))
            {
                s--;
            }

            while (e < text.Length && IsWordChar(text[e]))
            {
                e++;
            }

            if (e == s)
            {
                return null;
            }

            selStart = s;
            selLen = e - s;
        }

        var src = text.Substring(selStart, selLen);
        var dst = mode switch
        {
            CaseMode.Upper => src.ToUpperInvariant(),
            CaseMode.Lower => src.ToLowerInvariant(),
            CaseMode.Title => TitleCase(src),
            CaseMode.Sentence => SentenceCase(src),
            CaseMode.Invert => InvertCase(src),
            _ => Cycle(src),
        };
        return dst == src ? null : new TextOpResult(selStart, selLen, dst, selStart, dst.Length);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';

    private static string Cycle(string s)
    {
        if (string.Equals(s, s.ToUpperInvariant(), StringComparison.Ordinal))
        {
            return s.ToLowerInvariant();
        }

        if (string.Equals(s, s.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return TitleCase(s);
        }

        return s.ToUpperInvariant();
    }

    private static string TitleCase(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool startOfWord = true;
        foreach (char c in s)
        {
            if (char.IsLetter(c))
            {
                sb.Append(startOfWord ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
                startOfWord = false;
            }
            else
            {
                sb.Append(c);
                startOfWord = !IsWordChar(c);
            }
        }

        return sb.ToString();
    }

    private static string SentenceCase(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool startOfSentence = true;
        foreach (char c in s)
        {
            if (char.IsLetter(c))
            {
                sb.Append(startOfSentence ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
                startOfSentence = false;
            }
            else
            {
                sb.Append(c);
                if (c is '.' or '!' or '?' or '\n')
                {
                    startOfSentence = true;
                }
            }
        }

        return sb.ToString();
    }

    private static string InvertCase(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            sb.Append(char.IsUpper(c) ? char.ToLower(c, CultureInfo.InvariantCulture) : char.ToUpper(c, CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    // ---- 줄 복제·삭제 -----------------------------------------------------------------------

    /// <summary>선택이 닿는 줄들을 바로 아래에 복제한다. 복제본이 선택된다.</summary>
    public static TextOpResult DuplicateLine(string text, int selStart, int selLen)
    {
        var lines = CoveredLines(text, selStart, selLen);
        int start = lines[0].Start, end = lines[^1].ContentEnd;
        var block = text.Substring(start, end - start);
        var nl = Newline(text);
        var inserted = nl + block;
        // 끝 위치에 넣는다(Length 0 삽입).
        return new TextOpResult(end, 0, inserted, end + nl.Length, block.Length);
    }

    /// <summary>선택이 닿는 줄들을 줄바꿈까지 지운다.</summary>
    public static TextOpResult DeleteLine(string text, int selStart, int selLen)
    {
        var lines = CoveredLines(text, selStart, selLen);
        int start = lines[0].Start, end = lines[^1].NextStart;
        if (end == text.Length && start > 0 && end == lines[^1].ContentEnd)
        {
            // 마지막 줄(줄바꿈 없음)이면 앞 줄바꿈을 같이 지워 빈 줄이 남지 않게 한다.
            start = LineAt(text, start - 1).ContentEnd;
        }

        return new TextOpResult(start, end - start, string.Empty, start, 0);
    }
}
