using Oranda.Core.Formatting;
using Oranda.Language.Lexing;

namespace Oranda.Language.Formatting;

/// <summary>
/// 문장 1개의 코드 토큰을 앞에서부터 읽으며 부분 구문 구조(질의 절·괄호·CASE·PL/SQL 블록)를 쌓고,
/// 토큰마다 줄바꿈 여부와 들여쓰기 단계를 정한다. 구조가 맞지 않으면(괄호·END 짝 불일치, 렉서 오류 토큰)
/// false 를 돌려주고 호출자가 그 문장의 원문 배치를 유지한다. 소유: TSK-05-01.
/// </summary>
internal sealed class StructureWalker
{
    enum FrameKind
    {
        Root,
        Query,
        Paren,
        SubParen,
        Case,
        CaseStmt,
        Decl,
        Body,
        If,
        Loop,
    }

    sealed class Frame(FrameKind kind, int depth)
    {
        public FrameKind Kind = kind;
        public int Depth = depth;
        public string Lead = "";
        public bool Fresh;
        public bool Header;
        public bool Exc;

        public bool IsBlock => Kind is FrameKind.Root or FrameKind.Decl or FrameKind.Body or FrameKind.If or FrameKind.Loop or FrameKind.CaseStmt;
    }

    readonly FormatLayout _layout;
    readonly List<FormatItem> _items;
    readonly List<int> _code;
    readonly int _p, _c, _s;
    readonly Stack<Frame> _st = new();

    bool _plsql, _stmtStart, _afterEnd, _afterSetOp, _pendingSub, _inLabel, _compoundPending, _compound, _justOpenedDecl;
    string _stmtHead = "";
    int _stmtDepth, _lineDepth, _breakDepth;

    public StructureWalker(FormatLayout layout)
    {
        _layout = layout;
        _items = layout.Items;
        _code = layout.Code;
        var ind = layout.Options.Indent;
        _p = Math.Clamp(ind.PlsqlBlock, 0, 4);
        _c = Math.Clamp(ind.Case, 0, 4);
        _s = Math.Clamp(ind.Subquery, 0, 4);
    }

    /// <summary>코드 위치 [from, to) 의 문장을 배치한다. 구조 불일치면 false.</summary>
    public bool Walk(int from, int to, bool plsql)
    {
        _st.Clear();
        _plsql = plsql;
        _stmtStart = plsql;
        _afterEnd = _afterSetOp = _pendingSub = _inLabel = _compoundPending = _compound = _justOpenedDecl = false;
        _stmtHead = "";
        _stmtDepth = _lineDepth = _breakDepth = 0;
        if (plsql) _st.Push(new Frame(FrameKind.Root, 0));

        for (int k = from; k < to; k++)
        {
            if (!Step(k, k == from)) return false;
        }

        while (_st.Count > 0 && _st.Peek().Kind == FrameKind.Query) _st.Pop();
        return _st.Count == 0 || (_st.Count == 1 && _st.Peek().Kind == FrameKind.Root);
    }

    FormatItem Code(int k) => _items[_code[k]];

    FormatItem? CodeAt(int k) => k >= 0 && k < _code.Count ? _items[_code[k]] : null;

    static string Up(FormatItem? t) => t?.Word ?? "";

    bool Step(int k, bool first)
    {
        var t = Code(k);
        if (t.Kind == TokenKind.Error) return false;
        var prev = CodeAt(k - 1);
        var next = CodeAt(k + 1);
        string w = Up(t);

        bool brk = false;
        int depth = 0;
        Action? post = null;

        if (first)
        {
            brk = true;
            if (!_plsql) _st.Push(new Frame(FrameKind.Query, 0) { Lead = w });
            if (!_plsql && IsQueryLead(w)) t.ClauseHead = true;
        }

        if (!Decide(t, w, prev, next, ref brk, ref depth, ref post)) return false;

        Place(t, brk, depth, prev);
        post?.Invoke();
        return true;
    }

    void Place(FormatItem t, bool brk, int depth, FormatItem? prev)
    {
        if (brk)
        {
            t.Gap = GapKind.NewLine;
            t.Depth = Math.Max(0, depth);
            _breakDepth = t.Depth;
            _lineDepth = t.Depth;
        }
        else
        {
            t.Depth = _breakDepth + 1;
            if (SoftBreak(t))
            {
                t.Gap = GapKind.NewLine;
                _lineDepth = t.Depth;
            }
            else
            {
                t.Gap = prev is null ? GapKind.NewLine : GapKind.Space;
                if (OwnLineCommentBefore(t)) _lineDepth = t.Depth;
            }
        }

        t.LineDepth = _lineDepth;
    }

    /// <summary>원문의 빈 줄·앞 주석 때문에 이 토큰이 줄 처음에 와야 한다.</summary>
    internal static bool SoftBreak(FormatItem t, FormatItem? before)
    {
        if (before is null) return false;
        if (t.BlankBefore > 0) return true;
        if (before.IsRaw || before.IsLineComment) return true;
        return before.IsComment && t.OrigNewLineBefore;
    }

    bool SoftBreak(FormatItem t)
    {
        int i = t.Index;
        return SoftBreak(t, i > 0 ? _items[i - 1] : null);
    }

    bool OwnLineCommentBefore(FormatItem t)
    {
        int i = t.Index;
        return i > 0 && _items[i - 1].IsComment && _items[i - 1].OrigNewLineBefore;
    }

    int StmtDepth(Frame b) => b.Kind switch
    {
        FrameKind.Root => b.Depth,
        FrameKind.Body when b.Exc => b.Depth + 2 * _p,
        FrameKind.CaseStmt => b.Depth + _c + _p,
        _ => b.Depth + _p,
    };

    bool Decide(FormatItem t, string w, FormatItem? prev, FormatItem? next, ref bool brk, ref int depth, ref Action? post)
    {
        var top = _st.Count > 0 ? _st.Peek() : null;

        // ---- PL/SQL 문장 시작 ----
        if (_plsql && _stmtStart && top is { IsBlock: true })
        {
            _stmtStart = false;
            _justOpenedDecl = false;
            return StatementStart(t, w, top, ref brk, ref depth, ref post);
        }

        if (_afterEnd)
        {
            _afterEnd = false;
            if (w is "IF" or "LOOP" or "CASE") return true;
        }

        if (_inLabel)
        {
            if (t.IsSym(">>")) { _inLabel = false; _stmtStart = true; }
            return true;
        }

        if (t.IsSym(";"))
        {
            if (!_plsql) return true;
            while (top is { Kind: FrameKind.Query })
            {
                _st.Pop();
                top = _st.Count > 0 ? _st.Peek() : null;
            }

            if (top is null || !top.IsBlock) return false;
            _stmtStart = true;
            _pendingSub = false;
            _justOpenedDecl = false;
            _stmtHead = "";
            return true;
        }

        if (t.IsSym("("))
        {
            bool sub = next is not null && (next.Is("SELECT") || next.Is("WITH"));
            post = () =>
            {
                if (sub)
                {
                    _st.Push(new Frame(FrameKind.SubParen, _lineDepth));
                    _st.Push(new Frame(FrameKind.Query, _lineDepth + _s) { Fresh = true });
                }
                else
                {
                    _st.Push(new Frame(FrameKind.Paren, _lineDepth));
                }
            };
            return true;
        }

        if (t.IsSym(")"))
        {
            while (top is { Kind: FrameKind.Query } && _st.Count > 1)
            {
                _st.Pop();
                top = _st.Peek();
            }

            if (top is null || top.Kind is not (FrameKind.Paren or FrameKind.SubParen)) return false;
            if (top.Kind == FrameKind.SubParen) { brk = true; depth = top.Depth; }
            _st.Pop();
            return true;
        }

        // ---- CASE 식 ----
        if (top is { Kind: FrameKind.Case })
        {
            if (w is "WHEN" or "ELSE") { brk = true; depth = top.Depth + _c; return true; }
            if (w == "END") { brk = true; depth = top.Depth; _st.Pop(); return true; }
        }

        if (w == "CASE")
        {
            post = () => _st.Push(new Frame(FrameKind.Case, _lineDepth));
            return true;
        }

        if (w == "END")
        {
            // 블록 END 는 문장 시작에서만 온다. 여기 오면 짝이 맞지 않는다.
            return false;
        }

        // ---- 질의 절 ----
        if (top is { Kind: FrameKind.Query })
        {
            QueryClause(t, w, prev, next, top, ref brk, ref depth);
            return true;
        }

        if (!_plsql || top is null || !top.IsBlock) return true;

        // ---- PL/SQL 문장 머리·안 ----
        return BlockToken(t, w, prev, next, top, ref brk, ref depth, ref post);
    }

    bool StatementStart(FormatItem t, string w, Frame blk, ref bool brk, ref int depth, ref Action? post)
    {
        brk = true;
        switch (w)
        {
            case "END":
                if (blk.Kind == FrameKind.Root) return false;
                depth = blk.Depth;
                _st.Pop();
                _afterEnd = true;
                _stmtHead = "END";
                return true;
            case "ELSIF" when blk.Kind == FrameKind.If:
                depth = blk.Depth;
                blk.Header = true;
                _stmtHead = "ELSIF";
                return true;
            case "ELSE" when blk.Kind == FrameKind.If:
                depth = blk.Depth;
                _stmtStart = true;
                return true;
            case "ELSE" when blk.Kind == FrameKind.CaseStmt:
                depth = blk.Depth + _c;
                _stmtStart = true;
                return true;
            case "WHEN" when blk.Kind == FrameKind.CaseStmt:
                depth = blk.Depth + _c;
                blk.Header = true;
                _stmtHead = "WHEN";
                return true;
            case "EXCEPTION" when blk.Kind == FrameKind.Body && !blk.Exc:
                depth = blk.Depth;
                blk.Exc = true;
                _stmtStart = true;
                return true;
            case "WHEN" when blk.Kind == FrameKind.Body && blk.Exc:
                depth = blk.Depth + _p;
                _stmtHead = "WHEN";
                return true;
            case "BEGIN" when blk.Kind == FrameKind.Decl:
                depth = blk.Depth;
                blk.Kind = FrameKind.Body;
                _stmtStart = true;
                return true;
        }

        int d = StmtDepth(blk);
        depth = d;
        _stmtDepth = d;
        _stmtHead = w;
        if (t.IsSym("<<")) { _inLabel = true; return true; }

        switch (w)
        {
            case "DECLARE":
                _st.Push(new Frame(FrameKind.Decl, d));
                _stmtStart = true;
                break;
            case "BEGIN":
                _st.Push(new Frame(FrameKind.Body, d));
                _stmtStart = true;
                break;
            case "IF":
                _st.Push(new Frame(FrameKind.If, d) { Header = true });
                break;
            case "LOOP":
                _st.Push(new Frame(FrameKind.Loop, d));
                _stmtStart = true;
                break;
            case "CASE":
                _st.Push(new Frame(FrameKind.CaseStmt, d) { Header = true });
                break;
            case "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "MERGE" or "WITH":
                _st.Push(new Frame(FrameKind.Query, d) { Lead = w });
                t.ClauseHead = true;
                break;
            case "PROCEDURE" or "FUNCTION":
                _pendingSub = true;
                break;
            case "BEFORE" or "AFTER" or "INSTEAD" when _compound:
                _pendingSub = true;
                break;
        }

        _ = post;
        return true;
    }

    bool BlockToken(FormatItem t, string w, FormatItem? prev, FormatItem? next, Frame top, ref bool brk, ref int depth, ref Action? post)
    {
        bool justOpened = _justOpenedDecl;
        _justOpenedDecl = false;
        switch (w)
        {
            case "WHEN" when top.Kind == FrameKind.CaseStmt && top.Header:
                brk = true;
                depth = top.Depth + _c;
                _stmtHead = "WHEN";
                return true;
            case "THEN":
                if (top.Kind is FrameKind.If or FrameKind.CaseStmt && top.Header) { top.Header = false; _stmtStart = true; }
                else if (top.Kind == FrameKind.Body && top.Exc && _stmtHead == "WHEN") _stmtStart = true;
                return true;
            case "LOOP":
                if (_stmtHead is "FOR" or "WHILE")
                {
                    int d = _stmtDepth;
                    post = () => _st.Push(new Frame(FrameKind.Loop, d));
                    _stmtStart = true;
                }

                return true;
            case "IS" or "AS":
                if (_pendingSub)
                {
                    _pendingSub = false;
                    int d = _stmtDepth;
                    post = () => _st.Push(new Frame(FrameKind.Decl, d));
                    _stmtStart = true;
                    _justOpenedDecl = true;
                }

                return true;
            case "LANGUAGE" or "EXTERNAL":
                if (justOpened && top.Kind == FrameKind.Decl && (prev?.Is("IS") == true || prev?.Is("AS") == true))
                {
                    _st.Pop();
                    _stmtStart = false;
                }

                return true;
            case "PROCEDURE" or "FUNCTION" or "PACKAGE":
                if (prev?.IsSym(".") != true) _pendingSub = true;
                return true;
            case "BODY":
                if (prev?.Is("TYPE") == true) _pendingSub = true;
                return true;
            case "COMPOUND":
                _compoundPending = true;
                return true;
            case "TRIGGER":
                if (_compoundPending)
                {
                    _compoundPending = false;
                    _compound = true;
                    int d = _stmtDepth;
                    post = () => _st.Push(new Frame(FrameKind.Decl, d));
                    _stmtStart = true;
                }

                return true;
            case "DECLARE" or "BEGIN" when top.Kind == FrameKind.Root:
                // 트리거 머리 뒤 블록
                brk = true;
                depth = top.Depth;
                _st.Push(new Frame(w == "BEGIN" ? FrameKind.Body : FrameKind.Decl, top.Depth));
                _stmtStart = true;
                return true;
            case "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "MERGE"
                when top.Kind != FrameKind.Root && prev?.IsSym(".") != true && prev?.IsSym("%") != true:
                {
                    // 문장 가운데 질의(커서 정의·OPEN FOR·FORALL)
                    brk = true;
                    depth = _stmtDepth + _p;
                    t.ClauseHead = true;
                    int d = depth;
                    string lead = w;
                    post = () => _st.Push(new Frame(FrameKind.Query, d) { Lead = lead });
                    return true;
                }
        }

        _ = t;
        _ = next;
        return true;
    }

    void QueryClause(FormatItem t, string w, FormatItem? prev, FormatItem? next, Frame q, ref bool brk, ref int depth)
    {
        if (q.Fresh)
        {
            q.Fresh = false;
            q.Lead = w;
            brk = true;
            depth = q.Depth;
            t.ClauseHead = true;
            return;
        }

        if (_afterSetOp)
        {
            if (w is "ALL" or "DISTINCT") return;
            _afterSetOp = false;
            brk = true;
            depth = q.Depth;
            t.ClauseHead = true;
            if (w == "SELECT") q.Lead = "SELECT";
            return;
        }

        if (ClauseBreak(q, w, prev, next))
        {
            brk = true;
            depth = q.Depth;
            t.ClauseHead = true;
            if (w is "UNION" or "INTERSECT" or "MINUS" or "EXCEPT") _afterSetOp = true;
            if (w == "SELECT") q.Lead = "SELECT";
        }
    }

    static bool IsQueryLead(string w) => w is "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "MERGE" or "WITH";

    static bool ClauseBreak(Frame q, string w, FormatItem? prev, FormatItem? next)
    {
        if (w.Length == 0) return false;
        string pw = Up(prev), nw = Up(next);
        bool afterParen = prev?.IsSym("(") == true;
        switch (q.Lead)
        {
            case "SELECT" or "WITH":
                switch (w)
                {
                    case "FROM" or "WHERE" or "HAVING" or "UNION" or "INTERSECT" or "MINUS" or "EXCEPT":
                        return true;
                    case "GROUP":
                        return nw == "BY";
                    case "ORDER":
                        return nw is "BY" or "SIBLINGS";
                    case "CONNECT":
                        return nw == "BY";
                    case "START":
                        return nw == "WITH";
                    case "INTO":
                        return q.Lead == "SELECT" && pw != "COLLECT";
                    case "BULK":
                        return nw == "COLLECT";
                    case "FETCH":
                        return nw is "FIRST" or "NEXT";
                    case "OFFSET":
                        return next is { Kind: TokenKind.Number or TokenKind.Bind or TokenKind.Substitution };
                    case "FOR":
                        return nw == "UPDATE";
                    case "SELECT":
                        return q.Lead == "WITH" && !afterParen;
                }

                return false;
            case "INSERT":
                return w switch
                {
                    "VALUES" or "RETURNING" => true,
                    "INTO" => pw is not ("INSERT" or "ALL" or "FIRST" or "THEN" or "ELSE"),
                    "SELECT" or "WITH" => !afterParen,
                    _ => false,
                };
            case "UPDATE":
                return w is "SET" or "WHERE" or "RETURNING";
            case "DELETE":
                return w is "WHERE" or "RETURNING";
            case "MERGE":
                return w is "USING" or "ON" or "WHEN";
            case "CREATE":
                return w == "SELECT" && pw == "AS";
            case "EXPLAIN":
                return w is "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "MERGE" or "WITH" && pw == "FOR";
        }

        return false;
    }
}
