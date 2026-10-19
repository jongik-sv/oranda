using Avalonia.Headless;
using Avalonia.Input;
using EditorSpike.L1;
using EditorSpike.MultiCaret;
using EditorSpike.Ui;

namespace EditorSpike;

/// <summary>스파이크 자체 검증. 실패하면 예외.</summary>
public static class SelfTest
{
    static int _pass;
    static void Check(bool ok, string what)
    {
        if (!ok) throw new Exception("CHECK FAILED: " + what);
        _pass++;
    }

    public static void Run()
    {
        LexerTokens();
        IncrementalEqualsFull();
        MultiCaret();
        Console.WriteLine($"SELFTEST_OK checks={_pass}");
    }

    static void LexerTokens()
    {
        var t = new List<Token>();
        int st = L1Lexer.LexLine("SELECT 'it''s' , q'[a'b]', :b1, 1.5e3 FROM dual -- c", 0, t);
        Check(st == L1Lexer.Normal, "end state normal");
        Check(t[0].Kind == TokKind.Keyword, "SELECT keyword");
        Check(t[1].Kind == TokKind.String && t[1].Length == 7, "escaped string");
        Check(t[3].Kind == TokKind.String && t[3].Length == 8, "q-quote");
        Check(t[5].Kind == TokKind.Bind, "bind");
        Check(t[7].Kind == TokKind.Number && t[7].Length == 5, "number exp");
        Check(t[^1].Kind == TokKind.Comment, "line comment");
        Check(L1Lexer.LexLine("x /* open", 0, null) == L1Lexer.InBlockComment, "block open");
        Check(L1Lexer.LexLine("still */ y", L1Lexer.InBlockComment, null) == L1Lexer.Normal, "block close");
        Check((L1Lexer.LexLine("v := q'{abc", 0, null) & 0xFF) == L1Lexer.InQString, "qstring open");
        int qs = L1Lexer.LexLine("v := q'{abc", 0, null);
        Check(L1Lexer.LexLine("x}' ;", qs, null) == L1Lexer.Normal, "qstring close");
        Check(L1Lexer.LexLine("'abc", 0, null) == L1Lexer.InString, "string open");
    }

    static void IncrementalEqualsFull()
    {
        var rnd = new Random(42);
        var src = ListLineSource.FromText(PlSqlGen.Package(2000));
        var inc = new IncrementalLexer(src);
        inc.LexAll();
        string[] snippets = ["/*", "*/", "'", "q'[", "]'", "--", "x", "\"", "BEGIN", ""];
        for (int iter = 0; iter < 400; iter++)
        {
            int line = rnd.Next(src.LineCount);
            int op = rnd.Next(3);
            if (op == 0) // 줄 안 편집
            {
                var s = src.Lines[line];
                int pos = rnd.Next(s.Length + 1);
                src.Lines[line] = s.Insert(pos, snippets[rnd.Next(snippets.Length)]);
                inc.ApplyEdit(line, 1, 1);
            }
            else if (op == 1) // 줄 분할(Enter)
            {
                var s = src.Lines[line];
                int pos = rnd.Next(s.Length + 1);
                src.Lines[line] = s[..pos];
                src.Lines.Insert(line + 1, s[pos..]);
                inc.ApplyEdit(line, 1, 2);
            }
            else if (line + 1 < src.LineCount) // 줄 합치기
            {
                src.Lines[line] += src.Lines[line + 1];
                src.Lines.RemoveAt(line + 1);
                inc.ApplyEdit(line, 2, 1);
            }
            var full = new IncrementalLexer(src);
            full.LexAll();
            Check(full.LineCount == inc.LineCount, $"line count iter {iter}");
            for (int i = 0; i < full.LineCount; i++)
            {
                if (full.EndStateOf(i) != inc.EndStateOf(i) || !full.TokensOf(i).AsSpan().SequenceEqual(inc.TokensOf(i)))
                    Check(false, $"incremental != full at iter {iter} line {i}");
            }
        }
        _pass++;
    }

    static void MultiCaret()
    {
        HeadlessHost.Start();
        var text = "aaa\nbbb\nccc\n";
        var (w, ed) = HeadlessHost.CreateEditorWindow(text, 600, 400);
        var col = new L1Colorizer(ed);
        var mc = new MultiCaretController(ed);
        ed.CaretOffset = 1;
        mc.AddCaretVertical(1);
        mc.AddCaretVertical(1);
        Check(mc.Count == 3, "3 carets");
        w.KeyTextInput("X");
        w.KeyTextInput("Y");
        HeadlessHost.Frame(w);
        Check(ed.Text == "aXYaa\nbXYbb\ncXYcc\n", "typed at 3 carets: " + ed.Text);
        w.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
        w.KeyRelease(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
        Check(ed.Text == "aXaa\nbXbb\ncXcc\n", "backspace at 3 carets: " + ed.Text);
        ed.Undo();
        Check(ed.Text == "aXYaa\nbXYbb\ncXYcc\n", "undo backspace in one step");
        ed.Undo(); ed.Undo();
        Check(ed.Text == text, "undo typing (2 steps for 2 keys): " + ed.Text);
        Check(col.Lexer.LineCount == ed.Document.LineCount, "lexer follows document");
        // 렌더러가 예외 없이 그리는지
        HeadlessHost.Frame(w);
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Check(mc.Count == 1, "escape clears");
        w.Close();
    }
}
