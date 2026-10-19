using Oranda.Core.Language;
using Oranda.Core.Text;
using Oranda.Language.Lexing;

namespace Oranda.App.Features.SqlEditor;

/// <summary>짝을 이루는 구조 종류.</summary>
public enum PairKind
{
    Paren,
    Begin,
    If,
    Loop,
    Case,
    BlockComment,
}

/// <summary>짝 하나. <see cref="Open"/>·<see cref="Close"/> 는 각각 여는 쪽·닫는 쪽 표지의 범위(예: <c>END IF</c> 전체).</summary>
public readonly record struct StructurePair(TextRange Open, TextRange Close, PairKind Kind);

/// <summary>접기 구간. <see cref="Name"/> 은 접었을 때 보이는 말.</summary>
public readonly record struct FoldRegion(int Start, int End, string Name);

/// <summary>
/// L1 토큰으로 괄호·BEGIN/END·IF/LOOP/CASE 짝과 접기 구간을 계산한다(EDIT-06). 문자열·주석 안의 글자는 무시한다.
/// 줄 단위 토큰을 쓰므로 UI 와 무관하게 시험할 수 있다.
/// </summary>
public static class SqlStructure
{
    /// <summary>짝 목록(닫는 쪽 위치 순).</summary>
    public static IReadOnlyList<StructurePair> FindPairs(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = SqlLexer.Tokenize(text);
        var pairs = new List<StructurePair>();
        var stack = new List<(PairKind Kind, TextRange Open)>();

        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            switch (t.Kind)
            {
                case TokenKind.Punct:
                    if (text[t.Start] == '(')
                    {
                        stack.Add((PairKind.Paren, new TextRange(t.Start, 1)));
                    }
                    else if (text[t.Start] == ')')
                    {
                        Close(stack, pairs, k => k == PairKind.Paren, new TextRange(t.Start, 1));
                    }

                    break;

                case TokenKind.Keyword:
                case TokenKind.Identifier:
                    HandleWord(text, tokens, ref i, stack, pairs);
                    break;
                default:
                    break;
            }
        }

        // 여러 줄 블록 주석(줄마다 토큰이 나뉘어 있어 이어 붙인다).
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind != TokenKind.Comment || !IsOpenBlockComment(text, t))
            {
                continue;
            }

            int j = i;
            while (j + 1 < tokens.Count && tokens[j + 1].Kind == TokenKind.Comment && !text.AsSpan(tokens[j].Start, tokens[j].Length).EndsWith("*/"))
            {
                j++;
            }

            var last = tokens[j];
            if (j > i && text.AsSpan(last.Start, last.Length).EndsWith("*/"))
            {
                pairs.Add(new StructurePair(new TextRange(t.Start, 2), new TextRange(last.End - 2, 2), PairKind.BlockComment));
            }

            i = j;
        }

        pairs.Sort((a, b) => a.Close.Start.CompareTo(b.Close.Start));
        return pairs;
    }

    private static bool IsOpenBlockComment(string text, Token t)
        => t.Length >= 2 && text[t.Start] == '/' && text[t.Start + 1] == '*' && !text.AsSpan(t.Start, t.Length).EndsWith("*/");

    private static void HandleWord(string text, List<Token> tokens, ref int i, List<(PairKind Kind, TextRange Open)> stack, List<StructurePair> pairs)
    {
        var t = tokens[i];
        var word = text.AsSpan(t.Start, t.Length);
        if (word.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
        {
            stack.Add((PairKind.Begin, new TextRange(t.Start, t.Length)));
        }
        else if (word.Equals("CASE", StringComparison.OrdinalIgnoreCase))
        {
            stack.Add((PairKind.Case, new TextRange(t.Start, t.Length)));
        }
        else if (word.Equals("IF", StringComparison.OrdinalIgnoreCase))
        {
            // DROP TABLE IF EXISTS / CREATE ... IF NOT EXISTS 는 블록이 아니다.
            var next = NextWord(text, tokens, i + 1);
            if (next is not ("EXISTS" or "NOT"))
            {
                stack.Add((PairKind.If, new TextRange(t.Start, t.Length)));
            }
        }
        else if (word.Equals("LOOP", StringComparison.OrdinalIgnoreCase))
        {
            stack.Add((PairKind.Loop, new TextRange(t.Start, t.Length)));
        }
        else if (word.Equals("END", StringComparison.OrdinalIgnoreCase))
        {
            var closeRange = new TextRange(t.Start, t.Length);
            if (i + 1 < tokens.Count && tokens[i + 1].Kind is TokenKind.Keyword or TokenKind.Identifier)
            {
                var n = tokens[i + 1];
                var nw = text.AsSpan(n.Start, n.Length);
                PairKind? kind = nw.Equals("IF", StringComparison.OrdinalIgnoreCase) ? PairKind.If
                    : nw.Equals("LOOP", StringComparison.OrdinalIgnoreCase) ? PairKind.Loop
                    : nw.Equals("CASE", StringComparison.OrdinalIgnoreCase) ? PairKind.Case
                    : null;
                if (kind is { } k)
                {
                    closeRange = new TextRange(t.Start, n.End - t.Start);
                    i++;
                    Close(stack, pairs, x => x == k, closeRange);
                    return;
                }
            }

            Close(stack, pairs, x => x is PairKind.Begin or PairKind.Case, closeRange);
        }
    }

    private static string? NextWord(string text, List<Token> tokens, int index)
    {
        if (index >= tokens.Count || tokens[index].Kind is not (TokenKind.Keyword or TokenKind.Identifier))
        {
            return null;
        }

        return text.Substring(tokens[index].Start, tokens[index].Length).ToUpperInvariant();
    }

    /// <summary>스택 맨 위부터 찾아 맞는 열림을 닫는다. 맞는 열림이 없으면(짝 없는 닫힘) 무시한다.</summary>
    private static void Close(List<(PairKind Kind, TextRange Open)> stack, List<StructurePair> pairs, Func<PairKind, bool> match, TextRange close)
    {
        for (int s = stack.Count - 1; s >= 0; s--)
        {
            if (match(stack[s].Kind))
            {
                pairs.Add(new StructurePair(stack[s].Open, close, stack[s].Kind));
                stack.RemoveRange(s, stack.Count - s);
                return;
            }
        }
    }

    /// <summary>
    /// <paramref name="offset"/> 의 앞뒤 글자가 짝의 한쪽이면 반대쪽 범위를 돌려준다(없으면 null).
    /// 뒤쪽(커서 오른쪽) 표지를 먼저 본다.
    /// </summary>
    public static (TextRange Here, TextRange Other)? FindMatch(IReadOnlyList<StructurePair> pairs, int offset)
    {
        foreach (var p in pairs)
        {
            if (Touches(p.Open, offset))
            {
                return (p.Open, p.Close);
            }

            if (Touches(p.Close, offset))
            {
                return (p.Close, p.Open);
            }
        }

        return null;
    }

    private static bool Touches(TextRange r, int offset) => offset >= r.Start && offset <= r.End;

    /// <summary>
    /// 여러 줄에 걸친 짝과 여러 줄 문장에서 접기 구간을 만든다. 시작 위치 순, 서로 겹치지 않고 포함만 하게 정리한다.
    /// </summary>
    public static IReadOnlyList<FoldRegion> ComputeFolds(string text) => ComputeFolds(text, FindPairs(text));

    /// <summary>이미 구한 짝 목록을 재사용하는 판.</summary>
    public static IReadOnlyList<FoldRegion> ComputeFolds(string text, IReadOnlyList<StructurePair> pairs)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(pairs);
        var candidates = new List<FoldRegion>();

        foreach (var p in pairs)
        {
            int start = p.Open.Start, end = p.Close.End;
            if (!SpansLines(text, start, end))
            {
                continue;
            }

            candidates.Add(new FoldRegion(start, end, TitleOf(text, p)));
        }

        foreach (var s in StatementSplitter.Split(text, SplitOptions.Default))
        {
            int start = s.Range.Start, end = s.Range.End;
            if (end > text.Length || !SpansLines(text, start, end))
            {
                continue;
            }

            candidates.Add(new FoldRegion(start, end, FirstLine(text, start, 48) + " …"));
        }

        // 같은 시작은 큰 쪽만, 그다음 시작 순·큰 것 먼저로 정렬하고 포함 관계가 깨지는 것은 버린다.
        candidates.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        var result = new List<FoldRegion>(candidates.Count);
        var open = new Stack<FoldRegion>();
        foreach (var c in candidates)
        {
            while (open.Count > 0 && open.Peek().End <= c.Start)
            {
                open.Pop();
            }

            if (open.Count > 0)
            {
                var top = open.Peek();
                if (c.Start == top.Start || c.End > top.End)
                {
                    continue; // 같은 시작(작은 쪽) 또는 걸치는 구간
                }
            }

            open.Push(c);
            result.Add(c);
        }

        return result;
    }

    private static bool SpansLines(string text, int start, int end)
        => text.AsSpan(start, Math.Min(end, text.Length) - start).IndexOf('\n') >= 0;

    private static string TitleOf(string text, StructurePair p)
    {
        if (p.Kind == PairKind.BlockComment)
        {
            return "/* … */";
        }

        var closeText = text.Substring(p.Close.Start, p.Close.Length).ToUpperInvariant();
        return $"{FirstLine(text, p.Open.Start, 40)} … {closeText}";
    }

    private static string FirstLine(string text, int start, int max)
    {
        int nl = text.IndexOf('\n', start);
        var line = (nl < 0 ? text[start..] : text[start..nl]).Trim();
        return line.Length <= max ? line : line[..max] + "…";
    }
}
