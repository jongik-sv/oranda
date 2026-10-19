using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Language.Lexing;

namespace Oranda.Language.Formatting;

/// <summary>
/// 조각(문서 전체 또는 줄 경계로 넓힌 범위)을 <see cref="FormatItem"/> 열로 바꾼다. <see cref="SqlLexer"/> 로 줄마다 렉싱하고
/// 여러 줄 문자열·주석은 1개로 합친다. <see cref="StatementSplitter"/> 와 같은 규칙으로 SQL*Plus·REM·단독 <c>/</c> 줄은
/// 렉싱하지 않고 원문 줄 항목으로 둔다(포맷 전후 같은 함수로 읽어 토큰 동일성을 비교한다). 소유: TSK-05-01.
/// </summary>
internal static class FormatTokenizer
{
    public sealed class Result
    {
        public required List<FormatItem> Items { get; init; }

        public required List<StatementSplitter.Piece> Pieces { get; init; }

        /// <summary>조각 끝이 닫히지 않은 문자열·주석·따옴표 이름 안이다.</summary>
        public bool Unterminated { get; init; }
    }

    public static Result Read(string text)
    {
        var pieces = StatementSplitter.SplitPieces(text, SplitOptions.Default);
        var lineStarts = LineStarts(text);
        int lineCount = lineStarts.Count;
        var rawLine = new bool[lineCount];
        var inStmt = new bool[lineCount];
        foreach (var p in pieces)
        {
            var r = p.Statement.Range;
            int a = LineOf(lineStarts, r.Start), b = LineOf(lineStarts, Math.Max(r.Start, r.End - 1));
            var mark = p.Statement.Kind == StatementKind.SqlPlus ? rawLine : inStmt;
            for (int l = a; l <= b; l++) mark[l] = true;
        }

        var items = new List<FormatItem>();
        var lineTokens = new List<Token>();
        int state = SqlLexer.Normal;
        for (int l = 0; l < lineCount; l++)
        {
            int pos = lineStarts[l];
            int end = l + 1 < lineCount ? lineStarts[l + 1] - 1 : text.Length;
            var line = text.AsSpan(pos, end - pos);
            if (state == SqlLexer.Normal)
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0 && (rawLine[l] || (trimmed.Length == 1 && trimmed[0] == '/') || (!inStmt[l] && IsRemLine(trimmed))))
                {
                    items.Add(new FormatItem(line.TrimEnd().ToString(), TokenKind.Error, isRaw: true, pos, l, l));
                    continue;
                }
            }

            int entry = state;
            lineTokens.Clear();
            state = SqlLexer.LexLine(line, state, lineTokens);
            for (int k = 0; k < lineTokens.Count; k++)
            {
                var t = lineTokens[k];
                if (k == 0 && entry != SqlLexer.Normal && t.Start == 0 && items.Count > 0)
                {
                    // 앞 줄에서 이어진 문자열·주석: 앞 항목과 합친다.
                    var prev = items[^1];
                    int s = prev.Start;
                    items[^1] = new FormatItem(text.Substring(s, pos + t.End - s), prev.Kind, false, s, prev.Line, l);
                    continue;
                }

                string tt = text.Substring(pos + t.Start, t.Length);
                if (t.Kind == TokenKind.Comment && tt.StartsWith("--", StringComparison.Ordinal)) tt = tt.TrimEnd();
                items.Add(new FormatItem(tt, t.Kind, false, pos + t.Start, l, l));
            }
        }

        AssignPieces(items, pieces);
        return new Result { Items = items, Pieces = pieces, Unterminated = state != SqlLexer.Normal };
    }

    static void AssignPieces(List<FormatItem> items, List<StatementSplitter.Piece> pieces)
    {
        int p = 0;
        foreach (var it in items)
        {
            if (it.IsRaw) continue;
            while (p < pieces.Count && pieces[p].Statement.Range.End <= it.Start) p++;
            if (p >= pieces.Count) break;
            var r = pieces[p].Statement.Range;
            if (pieces[p].Statement.Kind != StatementKind.SqlPlus && it.Start >= r.Start && it.Start < r.End) it.Piece = p;
        }
    }

    static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1)) starts.Add(i + 1);
        return starts;
    }

    static int LineOf(List<int> starts, int offset)
    {
        int i = starts.BinarySearch(offset);
        return i >= 0 ? i : ~i - 1;
    }

    static bool IsRemLine(ReadOnlySpan<char> line)
    {
        int i = 0;
        while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
        var w = line[..i];
        if (!(w.Equals("REM", StringComparison.OrdinalIgnoreCase) || w.Equals("REMARK", StringComparison.OrdinalIgnoreCase))) return false;
        return w.Length == line.Length || char.IsWhiteSpace(line[w.Length]);
    }
}
