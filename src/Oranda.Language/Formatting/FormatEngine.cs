using System.Text;
using Oranda.Core.Execution;
using Oranda.Core.Formatting;
using Oranda.Language.Lexing;

namespace Oranda.Language.Formatting;

/// <summary>
/// 포매터 기본 엔진(TRD 4.6). 조각 → 항목 열 → 원문 빈 줄·주석 위치 → 문장 끝 처리 → 구조 배치 → 주석 배치 →
/// 대소문자 → 공백 → 확장 규칙(<see cref="ApplyLayoutRules"/>, TSK-05-02) → 렌더링. 검증(<see cref="Verify"/>)은
/// 출력을 같은 토크나이저로 다시 읽어 토큰 열·주석을 비교한다. 소유: TSK-05-01.
/// </summary>
internal static partial class FormatEngine
{
    public sealed record Output(string Text, List<FormatItem> Items);

    /// <summary>
    /// 확장 규칙(쉼표·줄바꿈·정렬·제외 구간) 자리. TSK-05-02 가 <c>LayoutRules.cs</c> 에서
    /// <c>static partial void ApplyLayoutRules(FormatLayout layout)</c> 를 구현한다. 구현이 없으면 호출이 사라진다.
    /// </summary>
    static partial void ApplyLayoutRules(FormatLayout layout);

    /// <summary>조각 1개를 포맷한다. 바꿀 수 없으면(닫히지 않은 문자열·주석, 빈 조각) null.</summary>
    /// <param name="fragment">줄 처음에서 시작하는 문서 조각</param>
    /// <param name="o">옵션</param>
    /// <param name="baseIndent">모든 줄 앞에 붙일 원래 들여쓰기(범위 포맷)</param>
    /// <param name="newline">줄바꿈 문자열</param>
    /// <param name="read">이미 읽은 <paramref name="fragment"/> 토큰(검증에서 읽은 것을 멱등 검사에 다시 쓴다)</param>
    public static Output? Run(string fragment, FormatterOptions o, string baseIndent, string newline, FormatTokenizer.Result? read = null)
    {
        var tk = read ?? FormatTokenizer.Read(fragment);
        var items = tk.Items;
        if (tk.Unterminated || items.Count == 0) return null;

        bool trailingNewLine = fragment.AsSpan(items[^1].End).Contains('\n');
        Overlay(items, Math.Clamp(o.Comments.MaxBlankLines, 0, 5), o.Comments.Preserve);
        var block = BlockPieces(items, tk.Pieces.Count);
        ApplyTerminator(items, tk.Pieces, block, o.StatementTerminator);
        for (int i = 0; i < items.Count; i++) items[i].Index = i;

        var layout = new FormatLayout(items, o);
        Walk(layout, block, fragment);
        PlaceComments(layout);
        ApplyCase(layout);
        ApplySpacing(layout);
        ApplyLayoutRules(layout);
        GuardLoneSlash(items);
        return new Output(Render(layout, baseIndent, newline, trailingNewLine), items);
    }

    // ---- 원문 빈 줄·줄바꿈 ----

    static void Overlay(List<FormatItem> items, int maxBlank, bool preserve)
    {
        for (int i = 1; i < items.Count; i++)
        {
            var it = items[i];
            var prev = items[i - 1];
            it.OrigNewLineBefore = it.Line > prev.EndLine;
            int blank = Math.Clamp(it.Line - prev.EndLine - 1, 0, maxBlank);
            if (!preserve && it.Piece >= 0 && prev.Piece == it.Piece) blank = 0;
            it.BlankBefore = blank;
        }
    }

    // ---- 블록(PL/SQL) 문장 판정 ----

    static bool[] BlockPieces(List<FormatItem> items, int pieceCount)
    {
        var block = new bool[pieceCount];
        var words = new List<string>(4);
        int cur = -1;
        foreach (var it in items)
        {
            if (!it.IsCode || it.Piece < 0) continue;
            if (it.Piece != cur)
            {
                if (cur >= 0) block[cur] = IsBlock(words);
                cur = it.Piece;
                words.Clear();
            }

            if (words.Count < 8) words.Add(it.IsWord ? it.Word : it.Text);
        }

        if (cur >= 0) block[cur] = IsBlock(words);
        return block;
    }

    static bool IsBlock(List<string> w)
    {
        if (w.Count == 0) return false;
        if (w[0] is "DECLARE" or "BEGIN") return true;
        if (w[0] != "CREATE") return false;
        for (int i = 1; i < w.Count; i++)
        {
            switch (w[i])
            {
                case "OR" or "REPLACE" or "EDITIONABLE" or "NONEDITIONABLE" or "FORCE" or "NOFORCE" or "NO":
                    continue;
                case "PROCEDURE" or "FUNCTION" or "PACKAGE" or "TRIGGER":
                    return true;
                case "TYPE":
                    return i + 1 < w.Count && w[i + 1] == "BODY";
                default:
                    return false;
            }
        }

        return false;
    }

    // ---- 문장 끝 ; / ----

    static void ApplyTerminator(List<FormatItem> items, List<StatementSplitter.Piece> pieces, bool[] block, StatementTerminator mode)
    {
        if (mode == StatementTerminator.Keep) return;
        int stmtCount = pieces.Count;
        var lastOf = new int[pieces.Count];
        Array.Fill(lastOf, -1);
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].IsCode && items[i].Piece >= 0) lastOf[items[i].Piece] = i;
        }

        for (int p = pieces.Count - 1; p >= 0; p--)
        {
            int last = lastOf[p];
            if (last < 0 || pieces[p].Statement.Kind == StatementKind.SqlPlus) continue;
            int n = last + 1;
            while (n < items.Count && items[n].IsComment) n++;
            var next = n < items.Count ? items[n] : null;
            bool slash = next is { IsRaw: true } && next.Text.Trim() == "/";
            bool semi = !block[p] && next is { IsCode: true, Piece: -1 } && next.IsSym(";");
            bool has = slash || semi;
            var lastItem = items[last];

            if (mode == StatementTerminator.Remove)
            {
                if (has && stmtCount == 1 && items.Skip(n + 1).All(i => i.IsComment)) items.RemoveAt(n);
                continue;
            }

            if (has || (!block[p] && (lastItem.IsSym(";") || !Balanced(items, p, last)))) continue;
            if (block[p])
            {
                int at = last + 1;
                while (at < items.Count && items[at].IsComment && !items[at].OrigNewLineBefore) at++;
                items.Insert(at, new FormatItem("/", TokenKind.Error, isRaw: true, -1, lastItem.EndLine + 1, lastItem.EndLine + 1)
                {
                    OrigNewLineBefore = true,
                });
            }
            else
            {
                items.Insert(last + 1, new FormatItem(";", TokenKind.Punct, isRaw: false, -1, lastItem.EndLine, lastItem.EndLine));
            }
        }
    }

    /// <summary>문장 안 괄호 짝이 맞고 렉서 오류 토큰이 없는가(끝 표지를 더해도 되는 문장).</summary>
    static bool Balanced(List<FormatItem> items, int piece, int last)
    {
        int depth = 0;
        for (int i = last; i >= 0 && (items[i].Piece == piece || !items[i].IsCode); i--)
        {
            var it = items[i];
            if (!it.IsCode) continue;
            if (it.Kind == TokenKind.Error) return false;
            if (it.IsSym(")")) depth++;
            else if (it.IsSym("(") && --depth < 0) return false;
        }

        return depth == 0;
    }

    // ---- 구조 배치 ----

    static void Walk(FormatLayout layout, bool[] block, string source)
    {
        var items = layout.Items;
        var code = layout.Code;
        var walker = new StructureWalker(layout);
        int k = 0;
        while (k < code.Count)
        {
            var it = items[code[k]];
            if (it.Piece < 0)
            {
                PlaceLoose(items, it);
                k++;
                continue;
            }

            int e = k;
            while (e < code.Count && items[code[e]].Piece == it.Piece) e++;
            if (!walker.Walk(k, e, block[it.Piece])) KeepOriginal(items, code[k], code[e - 1], source);
            KeepInlineSqlPlusLead(items, it);
            k = e;
        }

        foreach (var it in items)
        {
            if (it.IsRaw)
            {
                it.Gap = GapKind.NewLine;
                it.Depth = 0;
            }

            if (it.Gap == GapKind.NewLine) it.BlankLines = it.BlankBefore;
        }
    }

    /// <summary>문장 밖 코드 토큰(끝 표지 <c>;</c> 등).</summary>
    static void PlaceLoose(List<FormatItem> items, FormatItem it)
    {
        var before = it.Index > 0 ? items[it.Index - 1] : null;
        int lineDepth = before?.LineDepth ?? 0;
        if (it.IsSym(";") && !StructureWalker.SoftBreak(it, before) && before is not null)
        {
            it.Gap = GapKind.Space;
            it.Depth = lineDepth;
            it.LineDepth = lineDepth;
            return;
        }

        it.Gap = GapKind.NewLine;
        it.Depth = 0;
        it.LineDepth = 0;
        KeepInlineSqlPlusLead(items, it);
    }

    /// <summary>줄 가운데 있던 문장 첫 토큰이 SQL*Plus 명령 낱말이면 줄 처음으로 옮기지 않는다.</summary>
    static void KeepInlineSqlPlusLead(List<FormatItem> items, FormatItem it)
    {
        if (it.Index == 0 || it.OrigNewLineBefore || it.BlankBefore > 0 || !FormatWords.IsSqlPlusLead(it)) return;
        var before = items[it.Index - 1];
        if (before.IsRaw || before.IsLineComment) return;
        it.Gap = GapKind.Space;
        it.Depth = before.LineDepth;
        it.LineDepth = before.LineDepth;
    }

    /// <summary>구조를 못 맞춘 문장: 원문 배치(간격)를 그대로 두고 대소문자만 바꾼다.</summary>
    static void KeepOriginal(List<FormatItem> items, int first, int last, string source)
    {
        items[first].Gap = GapKind.NewLine;
        items[first].Depth = 0;
        items[first].LineDepth = 0;
        for (int i = first + 1; i <= last; i++)
        {
            items[i].Gap = GapKind.Verbatim;
            items[i].OrigGap = source[items[i - 1].End..items[i].Start];
            items[i].LineDepth = 0;
        }
    }

    // ---- 주석 ----

    static void PlaceComments(FormatLayout layout)
    {
        var items = layout.Items;
        int p = Math.Clamp(layout.Options.Indent.PlsqlBlock, 0, 4);
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (!it.IsComment || it.Gap == GapKind.Verbatim) continue;
            var prev = i > 0 ? items[i - 1] : null;
            bool own = prev is null || it.OrigNewLineBefore || prev.IsRaw || prev.IsLineComment || it.BlankBefore > 0;
            if (own)
            {
                it.Gap = GapKind.NewLine;
                it.BlankLines = it.BlankBefore;
                it.Depth = CommentDepth(items, i, p);
                it.LineDepth = it.Depth;
            }
            else if (prev is not null)
            {
                // SQL*Plus 명령 낱말로 시작하는 문장 첫 토큰 바로 뒤에 붙은 주석은 붙여 둔다(공백이 생기면 SQL*Plus 줄이 된다).
                it.Gap = Adjacent(prev, it) && FormatWords.IsSqlPlusLead(prev) && IsPieceLead(items, prev) ? GapKind.None : GapKind.Space;
                it.Depth = prev.LineDepth;
                it.LineDepth = prev.LineDepth;
            }
        }
    }

    static bool IsPieceLead(List<FormatItem> items, FormatItem t)
    {
        if (t.Piece < 0) return false;
        for (int i = t.Index - 1; i >= 0; i--)
        {
            if (items[i].IsCode) return items[i].Piece != t.Piece;
        }

        return true;
    }

    static int CommentDepth(List<FormatItem> items, int i, int p)
    {
        int j = i + 1;
        while (j < items.Count && items[j].IsComment) j++;
        if (j >= items.Count || items[j].IsRaw) return 0;
        var n = items[j];
        if (n.Gap == GapKind.NewLine && (n.Is("END") || n.Is("ELSE") || n.Is("ELSIF") || n.Is("EXCEPTION"))) return n.Depth + p;
        return n.Depth;
    }

    // ---- 대소문자 ----

    static void ApplyCase(FormatLayout layout)
    {
        var items = layout.Items;
        var code = layout.Code;
        var c = layout.Options.Case;
        foreach (var it in items) it.OutText = it.Text;
        for (int k = 0; k < code.Count; k++)
        {
            var it = items[code[k]];
            if (!it.IsWord) continue;
            var prev = k > 0 ? items[code[k - 1]] : null;
            var next = k + 1 < code.Count ? items[code[k + 1]] : null;
            bool afterDot = prev?.IsSym(".") == true;
            bool beforeDot = next?.IsSym(".") == true;
            bool call = next?.IsSym("(") == true;
            string s = it.Text;
            LetterCase lc;
            if (afterDot) lc = c.Identifier;
            else if (call && FormatWords.BuiltinFunctions.Contains(s)) lc = c.Function;
            else if (!beforeDot && FormatWords.NoParenFunctions.Contains(s)) lc = c.Function;
            else if (it.Kind == TokenKind.Keyword) lc = c.Keyword;
            else if (!beforeDot && FormatWords.ExtraKeywords.Contains(s)) lc = c.Keyword;
            else lc = c.Identifier;
            it.OutText = FormatWords.Apply(s, lc);
        }
    }

    // ---- 공백 ----

    static void ApplySpacing(FormatLayout layout)
    {
        var items = layout.Items;
        var code = layout.Code;
        var line = new List<Token>();
        for (int k = 1; k < code.Count; k++)
        {
            int i = code[k];
            var c = items[i];
            if (c.Gap != GapKind.Space || i == 0 || !items[i - 1].IsCode) continue;
            var p = items[i - 1];
            bool pieceLead = p.Piece >= 0 && (k < 2 || items[code[k - 2]].Piece != p.Piece);
            if (Adjacent(p, c) && (InSubstitutionRun(items, code, k) || (pieceLead && FormatWords.IsSqlPlusLead(p))))
            {
                // 치환 변수는 글자를 이어 붙인다(&tab..col, &a&b). 원문에서 붙어 있던 것은 그대로 붙인다.
                // 문장 첫 낱말이 SQL*Plus 명령 낱말이면 뒤에 공백을 넣는 순간 SQL*Plus 줄로 읽히므로 그대로 붙인다.
                c.Gap = GapKind.None;
                continue;
            }

            var pp = k >= 2 ? items[code[k - 2]] : null;
            int j = k - 1;
            while (j >= 2 && items[code[j - 1]].IsSym(".")) j -= 2;
            var leader = j >= 1 ? items[code[j - 1]] : null;
            if (Tight(pp, p, c, leader) && JoinSafe(p, c, line)) c.Gap = GapKind.None;
        }
    }

    static bool Adjacent(FormatItem a, FormatItem b) => a.Start >= 0 && b.Start == a.End && a.EndLine == b.Line;

    /// <summary>치환 변수와 붙은 짝인가(<c>&amp;a&amp;b</c>, <c>&amp;x.y</c>, <c>&amp;tab..col</c> 의 <c>..</c> 뒤).</summary>
    static bool InSubstitutionRun(List<FormatItem> items, List<int> code, int k)
    {
        var c = items[code[k]];
        var p = items[code[k - 1]];
        if (c.Kind == TokenKind.Substitution || p.Kind == TokenKind.Substitution) return true;
        if (k < 2 || !(p.IsSym(".") || p.IsSym(".."))) return false;
        var pp = items[code[k - 2]];
        return pp.Kind == TokenKind.Substitution && Adjacent(pp, p);
    }

    /// <summary>두 토큰을 붙여 쓰는가(기본 규칙). 연산자·괄호 안 공백 옵션은 TSK-05-02 가 덮어쓴다.</summary>
    static bool Tight(FormatItem? pp, FormatItem p, FormatItem c, FormatItem? leader)
    {
        if (c.IsSym(",") || c.IsSym(";") || c.IsSym(")")) return true;
        if (p.IsSym("(")) return true;
        if (p.IsSym(".") || c.IsSym(".")) return true;
        if (p.IsSym("%") || c.IsSym("%") || p.IsSym("@") || c.IsSym("@")) return true;
        if (p.IsSym("<<") || c.IsSym(">>")) return true;
        if (c.IsSym("("))
        {
            switch (p.Kind)
            {
                case TokenKind.Identifier or TokenKind.QuotedIdent:
                    return !FormatWords.IdentifiersSpacedParen.Contains(p.Text) && !AfterObjectLeader(leader);
                case TokenKind.Keyword:
                    return FormatWords.KeywordsTightParen.Contains(p.Text) && !AfterObjectLeader(leader);
                case TokenKind.Bind:
                    return true;
                default:
                    return false;
            }
        }

        if ((p.IsSym("-") || p.IsSym("+")) && IsUnaryContext(pp)) return true;
        return false;
    }

    static bool IsUnaryContext(FormatItem? pp) =>
        pp is null
        || pp.Kind == TokenKind.Keyword
        || (pp.Kind == TokenKind.Operator && !pp.IsSym("%"))
        || pp.IsSym("(") || pp.IsSym(",");

    /// <summary><c>INSERT INTO s.t (</c>·<c>CREATE TABLE t (</c> 처럼 객체 이름 뒤 괄호인가.</summary>
    static bool AfterObjectLeader(FormatItem? leader) =>
        leader is { IsWord: true } && FormatWords.ObjectNameLeaders.Contains(leader.Text);

    /// <summary>붙여 써도 렉서가 같은 두 토큰으로 읽는가.</summary>
    static bool JoinSafe(FormatItem p, FormatItem c, List<Token> buf)
    {
        if (p.IsRaw || c.IsRaw) return false;
        if (p.EndLine != p.Line || c.EndLine != c.Line)
        {
            // 여러 줄 토큰은 닫는 구분자로 끝나므로 뒤 문장 부호와는 붙어도 되고, 여는 괄호 뒤에는 붙어도 된다.
            return (p.EndLine == p.Line || c.Kind == TokenKind.Punct) && (c.EndLine == c.Line || p.IsSym("("));
        }
        if (p.IsSym("(") || c.IsSym(",") || c.IsSym(";") || c.IsSym(")")) return true;
        string a = p.OutText, b = c.OutText;
        buf.Clear();
        int len = a.Length + b.Length;
        Span<char> joined = len <= 256 ? stackalloc char[len] : new char[len];
        a.AsSpan().CopyTo(joined);
        b.AsSpan().CopyTo(joined[a.Length..]);
        int state = SqlLexer.LexLine(joined, SqlLexer.Normal, buf);
        return state == SqlLexer.Normal && buf.Count == 2 && buf[0].Start == 0 && buf[0].Length == a.Length
            && buf[0].Kind == p.Kind && buf[1].Kind == c.Kind && buf[1].Length == b.Length;
    }

    /// <summary>나눗셈 <c>/</c> 가 혼자 한 줄이 되면 문장 끝 표지로 읽히므로 앞 줄에 붙인다.</summary>
    static void GuardLoneSlash(List<FormatItem> items)
    {
        for (int i = 1; i < items.Count; i++)
        {
            var it = items[i];
            if (!it.IsSym("/") || it.Gap != GapKind.NewLine) continue;
            bool alone = i + 1 >= items.Count || items[i + 1].Gap is GapKind.NewLine
                || (items[i + 1].Gap == GapKind.Verbatim && items[i + 1].OrigGap.Contains('\n', StringComparison.Ordinal));
            var before = items[i - 1];
            if (alone && !before.IsRaw && !before.IsLineComment) it.Gap = GapKind.Space;
        }
    }

    // ---- 렌더링 ----

    static string Render(FormatLayout layout, string baseIndent, string newline, bool trailingNewLine)
    {
        var items = layout.Items;
        var ind = layout.Options.Indent;
        string unit = ind.UseTabs ? "\t" : new string(' ', Math.Clamp(ind.Size, 1, 16));
        var sb = new StringBuilder(items.Count * 8);
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (i == 0)
            {
                if (!it.IsRaw) Indent(sb, baseIndent, unit, it.Depth);
            }
            else
            {
                switch (it.Gap)
                {
                    case GapKind.None:
                        break;
                    case GapKind.Space:
                        sb.Append(' ');
                        break;
                    case GapKind.NewLine:
                        for (int b = 0; b <= it.BlankLines; b++) sb.Append(newline);
                        if (!it.IsRaw) Indent(sb, baseIndent, unit, it.Depth);
                        break;
                    case GapKind.Verbatim:
                        AppendVerbatim(sb, it.OrigGap, newline);
                        break;
                }
            }

            sb.Append(it.IsRaw ? it.Text : it.OutText);
        }

        if (trailingNewLine) sb.Append(newline);
        return sb.ToString();
    }

    static void Indent(StringBuilder sb, string baseIndent, string unit, int depth)
    {
        sb.Append(baseIndent);
        for (int d = 0; d < depth; d++) sb.Append(unit);
    }

    static void AppendVerbatim(StringBuilder sb, string gap, string newline)
    {
        var parts = gap.Split('\n');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i < parts.Length - 1)
            {
                sb.Append(parts[i].TrimEnd()).Append(newline);
            }
            else
            {
                sb.Append(parts[i].TrimEnd('\r'));
            }
        }
    }

    // ---- 검증(FMT-11) ----

    /// <summary>출력을 다시 읽어 기대 항목 열과 비교한다. 코드 토큰(낱말은 대소문자 무시)·원문 줄·주석 글자와 수가 같아야 한다.</summary>
    public static FormatFailure Verify(List<FormatItem> expected, string output, out string? message) =>
        Verify(expected, FormatTokenizer.Read(output), out message);

    internal static FormatFailure Verify(List<FormatItem> expected, FormatTokenizer.Result tk, out string? message)
    {
        message = null;
        if (tk.Unterminated)
        {
            message = "출력에 닫히지 않은 문자열·주석이 있다.";
            return FormatFailure.TokenMismatch;
        }

        var actual = tk.Items;
        int ec = 0, ac = 0;
        foreach (var it in expected) if (it.IsComment) ec++;
        foreach (var it in actual) if (it.IsComment) ac++;
        if (ec != ac)
        {
            message = $"주석 수가 다르다(입력 {ec}, 출력 {ac}).";
            return FormatFailure.CommentCountMismatch;
        }

        int x = 0, y = 0;
        while (true)
        {
            while (x < expected.Count && expected[x].IsComment) x++;
            while (y < actual.Count && actual[y].IsComment) y++;
            if (x >= expected.Count || y >= actual.Count) break;
            if (!SameToken(expected[x], actual[y]))
            {
                message = $"토큰이 다르다: '{expected[x].Text}' ≠ '{actual[y].Text}'.";
                return FormatFailure.TokenMismatch;
            }

            x++;
            y++;
        }

        if (x < expected.Count || y < actual.Count)
        {
            message = "토큰 수가 다르다.";
            return FormatFailure.TokenMismatch;
        }

        var ecom = expected.Where(i => i.IsComment).Select(i => i.Text.TrimEnd()).ToList();
        var acom = actual.Where(i => i.IsComment).Select(i => i.Text.TrimEnd()).ToList();
        for (int i = 0; i < ecom.Count; i++)
        {
            if (!string.Equals(ecom[i], acom[i], StringComparison.Ordinal))
            {
                message = "주석 내용이 다르다.";
                return FormatFailure.TokenMismatch;
            }
        }

        return FormatFailure.None;
    }

    static bool SameToken(FormatItem e, FormatItem a)
    {
        if (e.IsRaw != a.IsRaw) return false;
        if (e.IsRaw) return string.Equals(e.Text.TrimEnd(), a.Text.TrimEnd(), StringComparison.Ordinal);
        if (e.Kind != a.Kind) return false;
        var cmp = e.Kind is TokenKind.Keyword or TokenKind.Identifier ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(e.Text, a.Text, cmp);
    }
}
