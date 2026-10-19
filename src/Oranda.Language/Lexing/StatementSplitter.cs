using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Text;

namespace Oranda.Language.Lexing;

/// <summary>
/// 문장 분리·분류(TRD 4.3). 렉서 토큰으로 문자열·주석을 건너뛰고, PL/SQL 블록 안의 <c>;</c> 는 끝으로 보지 않는다.
/// 끝 표지 = <c>;</c>, 단독 줄 <c>/</c>, (선택) 빈 줄. SQL*Plus 명령은 줄 끝이 끝이다.
/// </summary>
public static class StatementSplitter
{
    /// <summary>문장 + 커서 소속 판단용 범위(끝 표지와 같은 줄의 뒷공백·주석까지).</summary>
    public readonly record struct Piece(SqlStatement Statement, int ExtentEnd);

    public static IReadOnlyList<SqlStatement> Split(string text, SplitOptions o)
    {
        var pieces = SplitPieces(text, o);
        var result = new SqlStatement[pieces.Count];
        for (int i = 0; i < result.Length; i++) result[i] = pieces[i].Statement;
        return result;
    }

    public static SqlStatement? GetStatementAt(string text, int offset, SplitOptions o)
    {
        if (offset < 0 || offset > text.Length) return null;
        foreach (var p in SplitPieces(text, o))
        {
            var r = p.Statement.Range;
            if (offset < r.Start) return null;
            if (offset <= p.ExtentEnd) return p.Statement;
        }
        return null;
    }

    public static List<Piece> SplitPieces(string text, SplitOptions o)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(o);
        var pieces = new List<Piece>();
        var b = new Builder(text, pieces);
        var lineTokens = new List<Token>();
        int state = SqlLexer.Normal, pos = 0;
        while (pos <= text.Length)
        {
            int nl = text.IndexOf('\n', pos);
            int lineEnd = nl < 0 ? text.Length : nl;
            int next = nl < 0 ? text.Length + 1 : nl + 1;

            if (state == SqlLexer.Normal)
            {
                var line = text.AsSpan(pos, lineEnd - pos);
                var trimmed = line.Trim();
                if (trimmed.Length == 1 && trimmed[0] == '/')
                {
                    b.Finish(pos + line.IndexOf('/') + 1);
                    pos = next;
                    continue;
                }

                if (trimmed.Length == 0)
                {
                    if (o.SplitOnBlankLine && b.CanSplitAtBlankLine) b.Finish(-1);
                    pos = next;
                    continue;
                }

                if (b.IsEmpty && IsRemLine(trimmed))
                {
                    pos = next;
                    continue;
                }

                if (o.RecognizeSqlPlus && b.IsEmpty && IsSqlPlusLine(trimmed))
                {
                    pos = ReadSqlPlus(text, pos, b);
                    continue;
                }
            }

            lineTokens.Clear();
            state = SqlLexer.LexLine(text.AsSpan(pos, lineEnd - pos), state, lineTokens);
            foreach (var t in lineTokens)
            {
                if (t.Kind is TokenKind.Comment or TokenKind.Hint) continue;
                b.Add(t with { Start = t.Start + pos });
            }

            pos = next;
        }

        b.Finish(-1);
        return pieces;
    }

    // ---- SQL*Plus ----

    static readonly string[] SqlPlusWords =
    [
        "SET", "DEFINE", "DEF", "UNDEFINE", "UNDEF", "PROMPT", "PRO", "SPOOL", "SPO", "WHENEVER", "ACCEPT", "ACC",
        "VARIABLE", "VAR", "PRINT", "EXEC", "EXECUTE", "DESC", "DESCRIBE", "SHOW", "COLUMN", "COL", "START", "STA",
        "CLEAR", "CL", "EXIT", "QUIT", "CONNECT", "CONN", "DISCONNECT", "HOST", "TTITLE", "BTITLE", "STORE",
        "PAUSE", "TIMING", "REPHEADER", "REPFOOTER", "BREAK", "COMPUTE", "SQLPLUS",
    ];

    static ReadOnlySpan<char> FirstWord(ReadOnlySpan<char> line)
    {
        int i = 0;
        while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
        return line[..i];
    }

    static bool IsRemLine(ReadOnlySpan<char> line)
    {
        var w = FirstWord(line);
        if (!(w.Equals("REM", StringComparison.OrdinalIgnoreCase) || w.Equals("REMARK", StringComparison.OrdinalIgnoreCase))) return false;
        return w.Length == line.Length || char.IsWhiteSpace(line[w.Length]);
    }

    static bool IsSqlPlusLine(ReadOnlySpan<char> line)
    {
        if (line[0] == '@') return true;
        var w = FirstWord(line);
        if (w.Length == 0) return false;
        if (w.Length < line.Length && !(char.IsWhiteSpace(line[w.Length]) || line[w.Length] == ';')) return false;
        bool known = false;
        foreach (var k in SqlPlusWords)
        {
            if (w.Equals(k, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
        }

        if (!known) return false;
        if (w.Equals("SET", StringComparison.OrdinalIgnoreCase))
        {
            // SET TRANSACTION / CONSTRAINT(S) / ROLE 은 SQL 문이다.
            var rest = line[w.Length..].TrimStart();
            var w2 = FirstWord(rest);
            if (w2.Equals("TRANSACTION", StringComparison.OrdinalIgnoreCase)
                || w2.Equals("CONSTRAINT", StringComparison.OrdinalIgnoreCase)
                || w2.Equals("CONSTRAINTS", StringComparison.OrdinalIgnoreCase)
                || w2.Equals("ROLE", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>SQL*Plus 명령 1건(줄 끝 <c>-</c> 이면 다음 줄 이어짐)을 읽어 문장으로 내보낸다. 다음 줄 시작 오프셋을 돌려준다.</summary>
    static int ReadSqlPlus(string text, int pos, Builder b)
    {
        int cmdStart = -1, cmdEnd = pos;
        while (pos <= text.Length)
        {
            int nl = text.IndexOf('\n', pos);
            int lineEnd = nl < 0 ? text.Length : nl;
            var line = text.AsSpan(pos, lineEnd - pos);
            var trimmed = line.Trim();
            int lead = line.Length - line.TrimStart().Length;
            if (cmdStart < 0) cmdStart = pos + lead;
            cmdEnd = pos + lead + trimmed.Length;
            int next = nl < 0 ? text.Length + 1 : nl + 1;
            pos = next;
            if (!(trimmed.Length > 0 && trimmed[^1] == '-' && nl >= 0)) break;
        }

        var raw = text.AsSpan(cmdStart, cmdEnd - cmdStart);
        if (raw.Length > 0 && raw[^1] == ';') { cmdEnd--; raw = raw[..^1].TrimEnd(); cmdEnd = cmdStart + raw.Length; }
        string t = raw.ToString();
        var w = raw[0] == '@' ? "@" : FirstWord(raw).ToString().ToUpperInvariant();
        b.EmitSqlPlus(t, cmdStart, cmdEnd, w);
        return pos;
    }

    // ---- 문장 조립 ----

    sealed class Scope(bool beginSeen)
    {
        public bool BeginSeen = beginSeen;
        public bool Compound;
    }

    sealed class Builder(string text, List<Piece> sink)
    {
        readonly List<Token> _toks = [];
        readonly Stack<Scope> _stack = new();
        bool _anon, _blockCreate, _kindFixed, _opened, _external, _pendingSub, _pendingEnd, _skipCase;
        bool _trigger, _compound;

        public bool IsEmpty => _toks.Count == 0;

        /// <summary>빈 줄로 문장을 끊어도 되는가(블록 안·블록 헤더 중에는 안 된다).</summary>
        public bool CanSplitAtBlankLine => _toks.Count > 0 && _stack.Count == 0 && !_anon && !_blockCreate;

        bool IsBlockType => _anon || _blockCreate;

        ReadOnlySpan<char> W(Token t) => text.AsSpan(t.Start, t.Length);

        bool Is(int idx, string word) =>
            idx >= 0 && idx < _toks.Count && _toks[idx].Kind is TokenKind.Keyword or TokenKind.Identifier
            && W(_toks[idx]).Equals(word, StringComparison.OrdinalIgnoreCase);

        static bool IsWord(ReadOnlySpan<char> s, string w) => s.Equals(w, StringComparison.OrdinalIgnoreCase);

        public void Add(Token t)
        {
            bool word = t.Kind is TokenKind.Keyword or TokenKind.Identifier;
            var s = W(t);

            // END 는 다음 토큰을 봐야 닫는 범위인지(END IF/LOOP 는 아님) 안다.
            if (_pendingEnd)
            {
                _pendingEnd = false;
                if (word && (IsWord(s, "IF") || IsWord(s, "LOOP")))
                {
                    _toks.Add(t);
                    return;
                }

                if (_stack.Count > 0) _stack.Pop();
                if (word && IsWord(s, "CASE")) _skipCase = true;
            }

            if (t.Kind == TokenKind.Punct && s.Length == 1 && s[0] == ';')
            {
                if (_stack.Count == 0 && (!IsBlockType || _opened || _external))
                {
                    if (IsBlockType) _toks.Add(t);
                    Finish(t.End);
                    return;
                }

                _pendingSub = false;
                _toks.Add(t);
                return;
            }

            _toks.Add(t);
            if (!word) return;
            int n = _toks.Count;

            if (!_kindFixed) DetectKind(n, s);
            if (!IsBlockType && _stack.Count == 0) return;

            if (IsWord(s, "END")) _pendingEnd = true;
            else if (IsWord(s, "CASE"))
            {
                if (_skipCase) _skipCase = false;
                else if (_stack.Count > 0) { _stack.Push(new Scope(true)); _opened = true; }
            }
            else if (IsWord(s, "DECLARE"))
            {
                if (n == 1 || _stack.Count > 0 || _trigger) { _stack.Push(new Scope(false)); _opened = true; }
            }
            else if (IsWord(s, "BEGIN"))
            {
                if (_stack.Count > 0 && !_stack.Peek().BeginSeen) _stack.Peek().BeginSeen = true;
                else { _stack.Push(new Scope(true)); _opened = true; }
            }
            else if (IsWord(s, "COMPOUND") && _trigger)
            {
                _stack.Push(new Scope(true) { Compound = true });
                _opened = true;
                _compound = true;
            }
            else if (IsWord(s, "PROCEDURE") || IsWord(s, "FUNCTION")) _pendingSub = true;
            else if ((IsWord(s, "BEFORE") || IsWord(s, "AFTER") || IsWord(s, "INSTEAD")) && _compound && _stack.Count > 0) _pendingSub = true;
            else if (IsWord(s, "CALL") && _trigger && _stack.Count == 0) _external = true;
            else if (IsWord(s, "IS") || IsWord(s, "AS"))
            {
                if (_pendingSub)
                {
                    _pendingSub = false;
                    _stack.Push(new Scope(false));
                    _opened = true;
                }
            }
            else if ((IsWord(s, "LANGUAGE") || IsWord(s, "EXTERNAL")) && _stack.Count > 0 && !_stack.Peek().BeginSeen
                     && n >= 2 && (IsWord(W(_toks[n - 2]), "IS") || IsWord(W(_toks[n - 2]), "AS")))
            {
                _stack.Pop();
                if (_stack.Count == 0) _external = true;
            }
        }

        void DetectKind(int n, ReadOnlySpan<char> s)
        {
            if (n == 1)
            {
                if (IsWord(s, "DECLARE") || IsWord(s, "BEGIN")) { _anon = true; _kindFixed = true; }
                else if (!IsWord(s, "CREATE")) _kindFixed = true;
                return;
            }

            // CREATE [OR REPLACE] [NO]FORCE [NON]EDITIONABLE <대상>
            if (IsWord(s, "OR") || IsWord(s, "REPLACE") || IsWord(s, "EDITIONABLE") || IsWord(s, "NONEDITIONABLE")
                || IsWord(s, "FORCE") || IsWord(s, "NOFORCE") || IsWord(s, "NO"))
                return;
            if (IsWord(s, "PROCEDURE") || IsWord(s, "FUNCTION") || IsWord(s, "PACKAGE"))
            {
                _blockCreate = true; _kindFixed = true; _pendingSub = true;
            }
            else if (IsWord(s, "TRIGGER")) { _blockCreate = true; _kindFixed = true; _trigger = true; }
            else if (IsWord(s, "TYPE")) return; // BODY 가 이어지는지 본다.
            else if (IsWord(s, "BODY") && Is(n - 2, "TYPE")) { _blockCreate = true; _kindFixed = true; _pendingSub = true; }
            else _kindFixed = true;
        }

        public void EmitSqlPlus(string t, int start, int end, string verb)
        {
            Finish(-1);
            var st = new SqlStatement(t, new TextRange(start, end - start), StatementKind.SqlPlus) { Verb = verb };
            sink.Add(new Piece(st, ExtendToLineEnd(end)));
        }

        /// <summary>끝 표지 위치 <paramref name="termEnd"/>(없으면 -1)로 지금 문장을 닫는다.</summary>
        public void Finish(int termEnd)
        {
            if (_toks.Count > 0)
            {
                int start = _toks[0].Start, end = _toks[^1].End;
                var st = StatementClassifier.Build(text, _toks, start, end, IsBlockType);
                int ext = termEnd >= 0 ? ExtendToLineEnd(termEnd) : ExtendToLineEnd(end);
                sink.Add(new Piece(st, ext));
            }

            _toks.Clear();
            _stack.Clear();
            _anon = _blockCreate = _kindFixed = _opened = _external = _pendingSub = _pendingEnd = _skipCase = false;
            _trigger = _compound = false;
        }

        int ExtendToLineEnd(int p)
        {
            int i = p;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t' || text[i] == '\r')) i++;
            if (i >= text.Length || text[i] == '\n') return i;
            if (text[i] == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                int nl = text.IndexOf('\n', i);
                return nl < 0 ? text.Length : nl;
            }

            return p;
        }
    }
}
