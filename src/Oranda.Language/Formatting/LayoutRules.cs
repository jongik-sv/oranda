using System.Collections.Frozen;
using Oranda.Core.Formatting;
using Oranda.Language.Lexing;

namespace Oranda.Language.Formatting;

/// <summary>
/// 확장 규칙 연결(FMT-04·05·06·10). 기본 엔진이 간격·들여쓰기를 정한 뒤 <see cref="ApplyLayoutRules"/> 가 불린다.
/// 규칙은 항목을 더하거나 빼지 않고 <see cref="FormatItem.Gap"/>·<see cref="FormatItem.Depth"/> 만 고친다. 소유: TSK-05-02.
/// </summary>
internal static partial class FormatEngine
{
    static partial void ApplyLayoutRules(FormatLayout layout) => new LayoutRules(layout).Run();

    /// <summary>
    /// 쉼표 위치·연산자/괄호 공백·줄바꿈(SELECT 목록·JOIN·AND/OR·IN 목록·최대 폭)·정렬(별칭·:=·열 정의·=)·제외 구간.
    /// 모든 결정은 토큰 열과 원문의 강제 줄바꿈(빈 줄·주석)에서만 나오므로 출력을 다시 포맷해도 같다.
    /// </summary>
    sealed class LayoutRules
    {
        enum ListKind : byte
        {
            None,
            Select,
            Set,
            ColumnDef,
        }

        sealed class Level
        {
            public int Open = -1;
            public bool IsCase;
            public int OwnerDepth;
            public int Clause = -1;
            public string ClauseWord = "";
            public int ClauseDepth;
            public int Cond = -1;
            public int CondDepth;
            public bool Between;
            public ListKind Kind;
            public bool OnePerLine;
            public bool WrapAll;
            public int BreakDepth;
            public int ElemStart = -1;
            public List<(int S, int E)>? Elems;
        }

        static readonly FrozenSet<string> ClauseWords = new[]
        {
            "SELECT","FROM","WHERE","GROUP","ORDER","HAVING","CONNECT","START","UNION","INTERSECT","MINUS","EXCEPT","INTO",
            "VALUES","SET","RETURNING","USING","ON","WHEN","BULK","FETCH","OFFSET","FOR","MODEL","WINDOW",
        }.ToFrozenSet(StringComparer.Ordinal);

        static readonly FrozenSet<string> CondHeads = new[] { "WHERE", "HAVING", "ON", "START", "CONNECT" }.ToFrozenSet(StringComparer.Ordinal);

        static readonly FrozenSet<string> JoinWords = new[] { "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "NATURAL", "OUTER" }.ToFrozenSet(StringComparer.Ordinal);

        static readonly FrozenSet<string> JoinPrefix = new[] { "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "NATURAL", "OUTER" }.ToFrozenSet(StringComparer.Ordinal);

        static readonly FrozenSet<string> BinaryOps = new[] { "=", "<>", "!=", "^=", "~=", "<", ">", "<=", ">=", "+", "-", "*", "/", "||", ":=", "=>" }
            .ToFrozenSet(StringComparer.Ordinal);

        static readonly FrozenSet<string> TableConstraintWords = new[] { "CONSTRAINT", "PRIMARY", "FOREIGN", "UNIQUE", "CHECK", "PERIOD", "SUPPLEMENTAL", "LIKE" }
            .ToFrozenSet(StringComparer.Ordinal);

        readonly List<FormatItem> _items;
        readonly List<int> _code;
        readonly FormatterOptions _o;
        readonly LineBreakOptions _lb;
        readonly AlignOptions _al;
        readonly int[] _cpos;
        readonly bool[] _frozen;
        readonly bool[] _mine;
        readonly int[] _nest;
        readonly bool[] _bad;
        readonly int _unit;
        readonly int _maxWidth;
        readonly List<Token> _buf = new(4);
        readonly List<(ListKind Kind, List<(int S, int E)> Elems)> _lists = [];
        readonly List<(int First, int Last, int On)> _regions = [];

        public LayoutRules(FormatLayout layout)
        {
            _items = layout.Items;
            _code = layout.Code;
            _o = layout.Options;
            _lb = _o.LineBreaks;
            _al = _o.Align;
            int n = _items.Count;
            _cpos = new int[n];
            Array.Fill(_cpos, -1);
            for (int k = 0; k < _code.Count; k++) _cpos[_code[k]] = k;
            _frozen = new bool[n];
            _mine = new bool[n];
            _nest = new int[n];
            int maxPiece = -1;
            foreach (var it in _items) maxPiece = Math.Max(maxPiece, it.Piece);
            _bad = new bool[maxPiece + 1];
            _unit = Math.Clamp(_o.Indent.Size, 1, 16);
            _maxWidth = Math.Clamp(_lb.MaxLineWidth, 40, 400);
        }

        public void Run()
        {
            if (_items.Count == 0) return;
            MarkFrozen();
            MarkBad();
            if (!_o.OperatorSpacing) TightOperators();
            if (_o.ParenInnerSpace) InnerSpaces();
            LayoutPass();
            if (_al.Alias) AlignAlias();
            if (_al.Assignment) AlignLines(":=", sameDepth: true);
            if (_al.EqualsSign) AlignLines("=", sameDepth: false);
            if (_al.ColumnDefinition) AlignColumns();
            ApplyFrozen();
        }

        // ---- 준비 ----

        void MarkBad()
        {
            foreach (var it in _items)
            {
                if (it.Piece < 0) continue;
                if (it.Gap == GapKind.Verbatim || (it.IsCode && it.Kind == TokenKind.Error)) _bad[it.Piece] = true;
            }
        }

        bool Active(int i)
        {
            var it = _items[i];
            return it.Piece >= 0 && !_bad[it.Piece] && !_frozen[i];
        }

        FormatItem? CodeBefore(int i)
        {
            int k = _cpos[i];
            if (k < 0)
            {
                for (int j = i - 1; j >= 0; j--)
                {
                    if (_items[j].IsCode) return _items[j];
                }

                return null;
            }

            return k > 0 ? _items[_code[k - 1]] : null;
        }

        // ---- 연산자·괄호 공백(FMT-04) ----

        void TightOperators()
        {
            for (int k = 1; k < _code.Count; k++)
            {
                int ci = _code[k];
                var c = _items[ci];
                if (c.Kind != TokenKind.Operator || !Active(ci) || !BinaryOps.Contains(c.Text)) continue;
                var p = _items[_code[k - 1]];
                if (p.Piece != c.Piece || !IsBinary(c, p)) continue;
                if (c.Gap == GapKind.Space && p.Index == c.Index - 1 && JoinSafe(p, c, _buf)) c.Gap = GapKind.None;
                if (k + 1 < _code.Count)
                {
                    var n = _items[_code[k + 1]];
                    if (n.Gap == GapKind.Space && n.Index == c.Index + 1 && n.Piece == c.Piece && !_frozen[n.Index] && JoinSafe(c, n, _buf))
                        n.Gap = GapKind.None;
                }
            }
        }

        static bool IsBinary(FormatItem op, FormatItem prev)
        {
            if (op.Text is "+" or "-" or "*" or "/") return IsOperandEnd(prev);
            return true;
        }

        static bool IsOperandEnd(FormatItem p) =>
            p.Kind is TokenKind.Identifier or TokenKind.QuotedIdent or TokenKind.Number or TokenKind.StringLiteral
                or TokenKind.Bind or TokenKind.Substitution
            || p.IsSym(")") || p.Is("END");

        void InnerSpaces()
        {
            for (int k = 0; k < _code.Count; k++)
            {
                int ci = _code[k];
                var c = _items[ci];
                if (!Active(ci)) continue;
                if (c.IsSym("(") && k + 1 < _code.Count)
                {
                    var n = _items[_code[k + 1]];
                    bool outer = n.IsSym("+") && k + 2 < _code.Count && _items[_code[k + 2]].IsSym(")");
                    if (n.Index == c.Index + 1 && n.Piece == c.Piece && n.Gap == GapKind.None && !n.IsSym(")") && !outer && !_frozen[n.Index])
                        n.Gap = GapKind.Space;
                }
                else if (c.IsSym(")") && k > 0 && c.Gap == GapKind.None)
                {
                    var p = _items[_code[k - 1]];
                    bool outer = p.IsSym("+") && k > 1 && _items[_code[k - 2]].IsSym("(");
                    if (p.Index == c.Index - 1 && p.Piece == c.Piece && !p.IsSym("(") && !outer) c.Gap = GapKind.Space;
                }
            }
        }

        // ---- 줄바꿈(FMT-05)·쉼표(FMT-04) ----

        int _col;

        void Advance(FormatItem it)
        {
            switch (it.Gap)
            {
                case GapKind.NewLine:
                    _col = it.IsRaw ? 0 : it.Depth * _unit;
                    break;
                case GapKind.Space:
                    _col++;
                    break;
                case GapKind.Verbatim:
                    int nl = it.OrigGap.LastIndexOf('\n');
                    _col = nl >= 0 ? it.OrigGap.Length - nl - 1 : _col + it.OrigGap.Length;
                    break;
            }

            string text = it.IsRaw ? it.Text : it.OutText;
            int last = text.LastIndexOf('\n');
            _col = last >= 0 ? text.Length - last - 1 : _col + text.Length;
        }

        void LayoutPass()
        {
            var stack = new List<Level>(16);
            int curPiece = int.MinValue;
            int baseDepth = 0, pc = -1, pendingFor = -1, pendingDepth = 0;
            bool createTable = false, parenSeen = false;
            _col = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (it.Piece != curPiece)
                {
                    if (stack.Count > 0) CloseList(stack[0], pc);
                    stack.Clear();
                    stack.Add(new Level());
                    curPiece = it.Piece;
                    pc = -1;
                    pendingFor = -1;
                    parenSeen = false;
                    createTable = it.Piece >= 0 && IsCreateTable(i);
                }

                if (it.Gap == GapKind.NewLine && !_mine[i]) baseDepth = it.Depth;
                _nest[i] = stack.Count - 1;
                if (it.IsCode && Active(i))
                {
                    var top = stack[^1];
                    var prev = pc >= 0 ? _items[pc] : null;
                    if (pendingFor == i) Break(i, pendingDepth);
                    pendingFor = -1;
                    CodeItem(i, it, prev, stack, top, pc, baseDepth, ref pendingFor, ref pendingDepth, createTable, ref parenSeen);
                }

                if (it.IsCode) pc = i;
                Advance(it);
            }

            if (stack.Count > 0) CloseList(stack[0], pc);
        }

        void CodeItem(
            int i, FormatItem it, FormatItem? prev, List<Level> stack, Level top, int pc, int baseDepth,
            ref int pendingFor, ref int pendingDepth, bool createTable, ref bool parenSeen)
        {
            string w = it.Word;
            if (top.ElemStart == -2 && !(w is "DISTINCT" or "UNIQUE" or "ALL" && top.Kind == ListKind.Select)) top.ElemStart = i;

            if (it.IsSym(";"))
            {
                foreach (var lv in stack)
                {
                    CloseList(lv, pc);
                    lv.Clause = -1;
                    lv.ClauseWord = "";
                    lv.Cond = -1;
                }

                return;
            }

            if (it.IsSym("("))
            {
                var lv = new Level { Open = i, OwnerDepth = it.LineDepth };
                bool first = !parenSeen;
                parenSeen = true;
                if (pc == i - 1 && prev is not null)
                {
                    if (prev.Is("IN") && _lb.InListThreshold > 0) SetupInList(i, lv);
                    else if (first && createTable && prev.Kind is TokenKind.Identifier or TokenKind.QuotedIdent && _al.ColumnDefinition) SetupColumns(i, lv);
                }

                stack.Add(lv);
                return;
            }

            if (it.IsSym(")"))
            {
                if (stack.Count > 1)
                {
                    while (stack.Count > 1 && stack[^1].IsCase) stack.RemoveAt(stack.Count - 1);
                    var lv = stack[^1];
                    if (stack.Count > 1)
                    {
                        if (lv.WrapAll) Break(i, lv.OwnerDepth);
                        CloseList(lv, pc);
                        stack.RemoveAt(stack.Count - 1);
                    }
                }

                _nest[i] = stack.Count - 1;
                return;
            }

            if (w == "CASE")
            {
                if (prev?.Is("END") != true) stack.Add(new Level { IsCase = true, Open = i, OwnerDepth = it.LineDepth });
                return;
            }

            if (w == "END")
            {
                if (top.IsCase && stack.Count > 1) stack.RemoveAt(stack.Count - 1);
                _nest[i] = stack.Count - 1;
                return;
            }

            if (top.IsCase) return;

            if (it.IsSym(","))
            {
                OnComma(i, it, top, baseDepth);
                return;
            }

            if (w.Length == 0) return;

            if (it.Gap == GapKind.NewLine && ClauseWords.Contains(w) && prev?.IsSym(".") != true)
            {
                CloseList(top, pc);
                top.Clause = i;
                top.ClauseWord = w;
                top.ClauseDepth = it.Depth;
                top.Cond = CondHeads.Contains(w) ? i : -1;
                top.CondDepth = it.Depth + 1;
                top.Between = false;
                if (w == "SELECT" || (w == "SET" && _al.EqualsSign))
                {
                    top.Kind = w == "SELECT" ? ListKind.Select : ListKind.Set;
                    top.OnePerLine = w == "SET" || _lb.SelectList == SelectListLayout.OnePerLine;
                    top.BreakDepth = top.ClauseDepth + 1;
                    top.Elems = [];
                    top.ElemStart = -2;
                }

                return;
            }

            if (top.ClauseWord == "FROM" && it.Gap != GapKind.NewLine && IsJoinStart(i))
            {
                if (_lb.JoinClause) Break(i, top.ClauseDepth);
                top.Cond = -1;
                return;
            }

            if (w == "ON" && top.ClauseWord == "FROM" && it.Gap != GapKind.NewLine && prev?.IsSym(".") != true)
            {
                top.Cond = i;
                top.CondDepth = it.LineDepth + 1;
                return;
            }

            if (top.Cond >= 0 && w is "AND" or "OR")
            {
                if (w == "AND" && top.Between)
                {
                    top.Between = false;
                }
                else if (_lb.AndOr == AndOrBreak.Before)
                {
                    Break(i, top.CondDepth);
                }
                else if (_lb.AndOr == AndOrBreak.After && i + 1 < _items.Count)
                {
                    pendingFor = i + 1;
                    pendingDepth = top.CondDepth;
                }

                return;
            }

            if (w == "BETWEEN") top.Between = true;
        }

        void CloseList(Level lv, int lastCode)
        {
            if (lv.Elems is null) return;
            if (lv.ElemStart >= 0 && lastCode >= lv.ElemStart) lv.Elems.Add((lv.ElemStart, lastCode));
            if (lv.Elems.Count > 0 && (lv.Kind != ListKind.ColumnDef || lv.WrapAll)) _lists.Add((lv.Kind, lv.Elems));
            lv.Elems = null;
            lv.ElemStart = -1;
            lv.Kind = ListKind.None;
            lv.OnePerLine = false;
        }

        void OnComma(int i, FormatItem it, Level top, int baseDepth)
        {
            if (top.Elems is not null && top.ElemStart >= 0)
            {
                int last = i - 1;
                while (last > top.ElemStart && !_items[last].IsCode) last--;
                if (last >= top.ElemStart) top.Elems.Add((top.ElemStart, last));
                top.ElemStart = -2;
            }

            if (i == 0 || i + 1 >= _items.Count) return;
            var prev = _items[i - 1];
            var next = _items[i + 1];
            if (!prev.IsCode || !next.IsCode || next.Piece != it.Piece || next.Gap != GapKind.Space || _frozen[i + 1]) return;
            if (it.Gap != GapKind.None && it.Gap != GapKind.Space) return;

            int depth = top.Kind != ListKind.None || top.WrapAll ? top.BreakDepth
                : top.Open >= 0 ? top.OwnerDepth + 1
                : top.Clause >= 0 ? top.ClauseDepth + 1
                : baseDepth + 1;
            bool doBreak = top.OnePerLine || top.WrapAll;
            if (!doBreak)
            {
                int commaEnd = _col + (it.Gap == GapKind.Space ? 1 : 0) + 1;
                doBreak = commaEnd + 1 + ElemWidth(i + 1) + 1 > _maxWidth;
            }

            if (!doBreak) return;
            if (_o.CommaPosition == CommaPosition.Trailing)
            {
                Break(i + 1, depth);
            }
            else if (Break(i, depth))
            {
                next.Gap = GapKind.Space;
            }
        }

        int ElemWidth(int j0)
        {
            int w = 0, cnt = 0;
            var first = _items[j0];
            for (int j = j0; j < _items.Count; j++)
            {
                var t = _items[j];
                if (t.Piece != first.Piece || (j > j0 && (t.Gap == GapKind.NewLine || t.IsRaw))) break;
                if (t.IsCode)
                {
                    if (t.IsSym(")"))
                    {
                        if (cnt == 0) break;
                        cnt--;
                    }
                    else if (t.IsSym(",") && cnt == 0)
                    {
                        break;
                    }
                    else if (t.IsSym("("))
                    {
                        cnt++;
                    }
                }

                int g = j == j0 ? 0 : t.Gap switch
                {
                    GapKind.Space => 1,
                    GapKind.Verbatim => t.OrigGap.Length,
                    _ => 0,
                };
                w += g + (t.IsRaw ? t.Text.Length : t.OutText.Length);
                if (t.IsLineComment || w > 1000) break;
            }

            return w;
        }

        bool IsCreateTable(int i)
        {
            var words = new List<string>(6);
            int piece = _items[i].Piece;
            for (int j = i; j < _items.Count && words.Count < 6; j++)
            {
                var t = _items[j];
                if (t.Piece != piece) break;
                if (!t.IsCode) continue;
                if (t.IsSym("(")) break;
                words.Add(t.Word);
            }

            return words.Count > 2 && words[0] == "CREATE" && words.Contains("TABLE") && !words.Contains("AS");
        }

        bool IsJoinStart(int i)
        {
            var it = _items[i];
            if (!JoinWords.Contains(it.Word)) return false;
            var p = CodeBefore(i);
            if (p is not null && (p.IsSym(".") || JoinPrefix.Contains(p.Word))) return false;
            int k = _cpos[i];
            for (int n = 0; n < 5 && k < _code.Count; n++, k++)
            {
                string w = _items[_code[k]].Word;
                if (w is "JOIN" or "APPLY") return true;
                if (!JoinPrefix.Contains(w)) return false;
            }

            return false;
        }

        /// <summary>IN 목록: 괄호 안이 단순 목록(주석·서브쿼리·줄바꿈 없음)이고 원소가 임계값보다 많으면 한 줄에 하나씩 감는다.</summary>
        void SetupInList(int open, Level lv)
        {
            int commas = ScanPlainList(open, out int close);
            if (close < 0 || commas + 1 <= _lb.InListThreshold) return;
            lv.WrapAll = true;
            lv.BreakDepth = lv.OwnerDepth + 1;
            Break(open + 1, lv.BreakDepth);
        }

        void SetupColumns(int open, Level lv)
        {
            ScanPlainList(open, out int close);
            if (close < 0) return;
            lv.Kind = ListKind.ColumnDef;
            lv.WrapAll = true;
            lv.BreakDepth = lv.OwnerDepth + 1;
            lv.Elems = [];
            lv.ElemStart = -2;
            Break(open + 1, lv.BreakDepth);
        }

        /// <summary>여는 괄호부터 짝 닫는 괄호까지 읽어 주석·줄바꿈·고정 구간이 없으면 최상위 쉼표 수를, 아니면 close = -1.</summary>
        int ScanPlainList(int open, out int close)
        {
            close = -1;
            int cnt = 0, commas = 0;
            int piece = _items[open].Piece;
            for (int j = open + 1; j < _items.Count; j++)
            {
                var t = _items[j];
                if (t.Piece != piece || !t.IsCode || _frozen[j] || t.Gap is GapKind.NewLine or GapKind.Verbatim) return 0;
                if (j == open + 1 && t.Word is "SELECT" or "WITH") return 0;
                if (t.IsSym("("))
                {
                    cnt++;
                }
                else if (t.IsSym(")"))
                {
                    if (cnt == 0)
                    {
                        if (j == open + 1) return 0;
                        close = j;
                        return commas;
                    }

                    cnt--;
                }
                else if (t.IsSym(",") && cnt == 0)
                {
                    commas++;
                }
            }

            return 0;
        }

        /// <summary>
        /// 항목 <paramref name="i"/> 앞에서 줄을 바꾼다(깊이 <paramref name="depth"/>). 원래 줄 기준으로 정해진 뒤따르는 중첩 줄(CASE·서브쿼리)의
        /// 깊이는 같은 만큼 옮긴다. 앞 항목이 코드가 아니면(주석·원문 줄) 하지 않는다.
        /// </summary>
        bool Break(int i, int depth)
        {
            if (i <= 0 || i >= _items.Count) return false;
            var it = _items[i];
            if (_frozen[i] || !it.IsCode || (it.Gap != GapKind.Space && it.Gap != GapKind.None)) return false;
            var before = _items[i - 1];
            if (!before.IsCode || before.Piece != it.Piece || _frozen[i - 1]) return false;

            depth = Math.Max(0, depth);
            int delta = depth - it.LineDepth;
            it.Gap = GapKind.NewLine;
            it.Depth = depth;
            it.LineDepth = depth;
            it.BlankLines = 0;
            _mine[i] = true;
            int cnt = 0, cur = depth;
            for (int j = i + 1; j < _items.Count; j++)
            {
                var t = _items[j];
                if (t.Piece != it.Piece || _frozen[j] || t.IsRaw || t.Gap == GapKind.Verbatim) break;
                bool closer = false, opener = false;
                if (t.IsCode)
                {
                    if (t.IsSym(")") || t.Is("END")) closer = true;
                    else if (t.IsSym("(") || (t.Is("CASE") && !_items[j - 1].Is("END"))) opener = true;
                }

                bool inside = cnt > 0;
                if (closer)
                {
                    if (cnt == 0) break;
                    inside = true;
                    cnt--;
                }

                if (t.Gap == GapKind.NewLine && !inside) break;
                if (t.Gap == GapKind.NewLine)
                {
                    t.Depth = Math.Max(0, t.Depth + delta);
                    t.LineDepth = t.Depth;
                    cur = t.Depth;
                }
                else
                {
                    t.LineDepth = cur;
                }

                if (opener) cnt++;
            }

            return true;
        }

        // ---- 정렬(FMT-06) ----

        (int[] Start, int[] End) Columns()
        {
            var start = new int[_items.Count];
            var end = new int[_items.Count];
            _col = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                Advance(it);
                int w = (it.IsRaw ? it.Text : it.OutText).Length;
                end[i] = _col;
                start[i] = _col - w;
            }

            return (start, end);
        }

        static string Pad(int n) => new(' ', Math.Max(0, n));

        static void SetPad(FormatItem it, int pad)
        {
            it.Gap = GapKind.Verbatim;
            it.OrigGap = Pad(pad);
        }

        void AlignAlias()
        {
            if (_lists.Count == 0) return;
            var (_, end) = Columns();
            foreach (var (kind, elems) in _lists)
            {
                if (kind != ListKind.Select || elems.Count < 2) continue;
                var found = new List<(int Alias, int Expr)>();
                bool ok = true;
                for (int n = 0; n < elems.Count && ok; n++)
                {
                    var (s, e) = elems[n];
                    bool ownLine = n == 0 || _items[s].Gap == GapKind.NewLine || (_items[s - 1].IsSym(",") && _items[s - 1].Gap == GapKind.NewLine);
                    if (!ownLine) { ok = false; break; }
                    if (FindAlias(s, e) is { } a) found.Add(a);
                }

                if (!ok || found.Count < 2) continue;
                int target = found.Max(f => end[f.Expr]) + 1;
                foreach (var (alias, expr) in found) SetPad(_items[alias], target - end[expr]);
            }
        }

        (int Alias, int Expr)? FindAlias(int s, int e)
        {
            if (e - s < 1) return null;
            for (int j = s; j <= e; j++)
            {
                var t = _items[j];
                if (!t.IsCode || _frozen[j] || (j > s && t.Gap is GapKind.NewLine or GapKind.Verbatim)) return null;
            }

            var last = _items[e];
            var prev = _items[e - 1];
            if (prev.Is("AS") && e - 2 >= s && last.IsWord && _items[e - 1].Gap == GapKind.Space) return (e - 1, e - 2);
            if (last.Kind is TokenKind.Identifier or TokenKind.QuotedIdent && IsOperandEnd(prev) && last.Gap == GapKind.Space) return (e, e - 1);
            return null;
        }

        void AlignColumns()
        {
            if (_lists.Count == 0) return;
            var (_, end) = Columns();
            foreach (var (kind, elems) in _lists)
            {
                if (kind != ListKind.ColumnDef) continue;
                var found = new List<int>();
                foreach (var (s, e) in elems)
                {
                    var name = _items[s];
                    if (e <= s || name.Kind is not (TokenKind.Identifier or TokenKind.QuotedIdent) || TableConstraintWords.Contains(name.Word)) continue;
                    var second = _items[s + 1];
                    bool plain = true;
                    for (int j = s; j <= e && plain; j++) plain = _items[j].IsCode && !_frozen[j];
                    bool ownLine = _items[s].Gap == GapKind.NewLine || (_items[s - 1].IsSym(",") && _items[s - 1].Gap == GapKind.NewLine);
                    if (plain && ownLine && second.Gap == GapKind.Space) found.Add(s);
                }

                if (found.Count < 2) continue;
                int target = found.Max(s => end[s]) + 1;
                foreach (int s in found) SetPad(_items[s + 1], target - end[s]);
            }
        }

        /// <summary>줄마다 첫 <paramref name="op"/>(줄 처음과 같은 중첩) 앞에 공백을 넣어 이어진 줄끼리 열을 맞춘다.</summary>
        void AlignLines(string op, bool sameDepth)
        {
            var (_, end) = Columns();
            var lines = new List<(int Start, int Last, int Op)>();
            int n = _items.Count;
            for (int i = 0; i < n;)
            {
                int j = i + 1;
                while (j < n && _items[j].Gap != GapKind.NewLine && !_items[j].IsRaw) j++;
                lines.Add((i, j - 1, FindOp(i, j - 1, op)));
                i = j;
            }

            int a = 0;
            while (a < lines.Count)
            {
                if (lines[a].Op < 0) { a++; continue; }
                int b = a;
                while (b + 1 < lines.Count && Continues(lines[b], lines[b + 1], sameDepth)) b++;
                if (b > a)
                {
                    int target = 0;
                    for (int k = a; k <= b; k++) target = Math.Max(target, end[lines[k].Op - 1]);
                    target += _o.OperatorSpacing ? 1 : 0;
                    for (int k = a; k <= b; k++) SetPad(_items[lines[k].Op], target - end[lines[k].Op - 1]);
                }

                a = b + 1;
            }
        }

        bool Continues((int Start, int Last, int Op) x, (int Start, int Last, int Op) y, bool sameDepth)
        {
            if (y.Op < 0) return false;
            var f = _items[y.Start];
            var g = _items[x.Start];
            if (f.Piece != g.Piece || f.BlankLines > 0 || f.Gap != GapKind.NewLine) return false;
            if (f.IsWord && ClauseWords.Contains(f.Word)) return false;
            if (_nest[y.Start] != _nest[x.Start]) return false;
            return !sameDepth || f.LineDepth == g.LineDepth;
        }

        int FindOp(int first, int last, string op)
        {
            var s = _items[first];
            if (!s.IsCode || s.IsRaw || !Active(first)) return -1;
            for (int j = first; j <= last; j++)
            {
                var t = _items[j];
                if (!t.IsCode || _frozen[j] || t.Gap == GapKind.Verbatim || (t.EndLine != t.Line && j < last)) return -1;
                if (_nest[j] == _nest[first] && t.IsSym(op) && t.Kind == TokenKind.Operator)
                    return j > first && _items[j - 1].IsCode && t.Gap == GapKind.Space ? j : -1;
            }

            return -1;
        }

        // ---- 제외 구간(FMT-10) ----

        static bool IsMarker(FormatItem it, string state)
        {
            if (!it.IsComment) return false;
            string t = it.Text;
            string body;
            if (t.StartsWith("--", StringComparison.Ordinal)) body = t[2..];
            else if (t.StartsWith("/*", StringComparison.Ordinal) && t.EndsWith("*/", StringComparison.Ordinal) && t.Length >= 4) body = t[2..^2];
            else return false;
            return string.Equals(body.Trim(), "@formatter:" + state, StringComparison.OrdinalIgnoreCase);
        }

        void MarkFrozen()
        {
            if (!_o.HonorFormatterOff) return;
            int start = -1;
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (start < 0)
                {
                    if (IsMarker(it, "off")) start = i + 1;
                    continue;
                }

                if (IsMarker(it, "on"))
                {
                    _regions.Add((start, i - 1, i));
                    start = -1;
                }
                else
                {
                    _frozen[i] = true;
                }
            }

            if (start >= 0 && start < _items.Count) _regions.Add((start, _items.Count - 1, -1));
        }

        void ApplyFrozen()
        {
            if (_regions.Count == 0) return;
            int nl = NewlineLength();
            foreach (var (first, last, on) in _regions)
            {
                int stop = on >= 0 ? on : last;
                for (int i = first; i <= stop; i++)
                {
                    var it = _items[i];
                    if (i <= last) it.OutText = it.Text;
                    var prev = _items[i - 1];
                    if (it.Start < 0 || prev.Start < 0) continue;
                    int lines = it.Line - prev.EndLine;
                    int gap = it.Start - prev.End;
                    string text;
                    if (lines <= 0) text = Pad(gap);
                    else text = new string('\n', lines) + (it.IsRaw ? "" : Pad(gap - (lines * nl)));
                    it.Gap = GapKind.Verbatim;
                    it.OrigGap = text;
                }
            }
        }

        /// <summary>원문 줄바꿈이 CRLF(2)인지 LF(1)인지 추정한다. 항목 위치 차이로 들여쓰기를 되살릴 때 필요하다.</summary>
        int NewlineLength()
        {
            int min = int.MaxValue;
            for (int i = 0; i < _items.Count; i++)
            {
                var t = _items[i];
                if (!t.IsRaw)
                {
                    int at = t.Text.IndexOf('\n', StringComparison.Ordinal);
                    if (at >= 0) return at > 0 && t.Text[at - 1] == '\r' ? 2 : 1;
                }

                if (i == 0) continue;
                var p = _items[i - 1];
                if (t.Start >= 0 && p.Start >= 0 && t.Line - p.EndLine == 1) min = Math.Min(min, t.Start - p.End);
            }

            return min >= 2 && min != int.MaxValue ? 2 : 1;
        }
    }
}
