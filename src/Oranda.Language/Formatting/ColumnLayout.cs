using Oranda.Core.Formatting;
using Oranda.Language.Lexing;

namespace Oranda.Language.Formatting;

internal static partial class FormatEngine
{
    /// <summary>
    /// 사내 쿼리 서식 배치(<see cref="ClauseLayout.Column"/>, TSK-13-43). 확장 규칙(<see cref="LayoutRules"/>)의 줄바꿈 뒤에 SELECT·WITH·INSERT·
    /// UPDATE·DELETE 문의 줄과 열을 다시 정한다.
    /// <list type="bullet">
    /// <item>절 키워드는 기준 열, 본문은 기준 열 + 7(키워드가 6자를 넘으면 한 칸 뒤). 앞 쉼표는 기준 열 + 5, 항목은 + 7.</item>
    /// <item>AND·OR 는 기준 열에서 7칸으로 채운다(<c>AND    </c>). 괄호 조건 묶음이 한 줄에 안 들어가면 여는 괄호를 그 자리에 두고 안쪽은 괄호 열 + 들여쓰기,
    /// AND·OR 는 안쪽 열 앞에 오른쪽으로 붙이고, 닫는 괄호는 여는 괄호 열에 혼자 둔다.</item>
    /// <item>서브쿼리·인라인 뷰: 짧고(줄 전체가 최대 폭 안) 단순하고(FROM 표 하나·조건 하나·GROUP BY 등 없음) 원문이 한 줄이면 괄호째 한 줄. 아니면 블록:
    /// 괄호가 항목 처음이면 그 자리(<c>FROM   (</c>·<c>     , (</c>), 아니면 키워드 줄을 끝내고 다음 줄 항목 열에 <c>(</c>.
    /// 안쪽은 괄호 열 + 들여쓰기, 닫는 괄호는 괄호 열(뒤에 별칭).</item>
    /// <item>WITH: <c>WITH 이름 AS</c>, 다음 줄 기준 열에 <c>(</c>, 안쪽 + 들여쓰기, <c>)</c>, 다음 CTE 는 기준 열 <c>, 이름 AS</c>.</item>
    /// <item>CASE: WHEN 이 하나이고 원문이 한 줄이고 줄이 최대 폭 안이면 한 줄. 아니면 WHEN·ELSE 를 CASE 열 + 5, END 를 CASE 열.</item>
    /// <item>GROUP BY·ORDER BY: 여러 줄이면 항목마다 앞 쉼표 줄, 한 줄이면 다 들어갈 때만 한 줄(아니면 항목마다 한 줄).</item>
    /// <item>주석: 줄 끝 주석은 앞 코드 뒤 원래 공백 수 그대로, 자기 줄 주석은 다음 코드 줄 열. 주석·빈 줄 뒤 토큰은 줄 처음에 온다.</item>
    /// </list>
    /// 판단은 토큰 글자·기본 간격·원문의 주석·빈 줄·서브쿼리와 CASE 안 줄바꿈에서만 나오므로 다시 포맷해도 같다.
    /// 구조를 못 맞추면(MERGE·괄호 짝·예상 밖 모양) 그 문장은 건드리지 않고 앞 단계 배치를 둔다.
    /// </summary>
    sealed class ColumnLayout
    {
        const int Body = 7;
        const int CommaCol = 5;
        const int CaseStep = 5;
        const int Big = 100_000;

        enum Mode : byte
        {
            Inline,
            NewLine,
            PadTo,
        }

        enum ListMode : byte
        {
            Each,
            Fill,
            Single,
        }

        readonly record struct Place(Mode Mode, int Col)
        {
            public static Place In => new(Mode.Inline, 0);

            public static Place Nl(int col) => new(Mode.NewLine, col);

            public static Place Pad(int col) => new(Mode.PadTo, col);
        }

        sealed record Saved(GapKind Gap, int Depth, int Extra, int LineDepth, int BlankLines, string OrigGap);

        readonly List<FormatItem> _items;
        readonly List<int> _code;
        readonly FormatterOptions _o;
        readonly LineBreakOptions _lb;
        readonly bool[] _bad;
        readonly bool[] _frozen;
        readonly int _unit;
        readonly int _max;
        readonly int _base;
        readonly List<Token> _buf = new(4);

        int[] _t = [];
        int _n;
        int[] _len = [];
        int[] _gw = [];
        GapKind[] _gap = [];
        bool[] _forced = [];
        int[] _match = [];
        int[] _col = [];
        bool[] _brk = [];
        long[] _pre = [];
        int[] _fc = [];
        int _cur;
        int _ec;
        readonly List<List<(int S, int E)>> _alignSets = [];

        public ColumnLayout(FormatLayout layout, bool[] bad, bool[] frozen)
        {
            _items = layout.Items;
            _code = layout.Code;
            _o = layout.Options;
            _lb = _o.LineBreaks;
            _bad = bad;
            _frozen = frozen;
            _unit = Math.Clamp(_o.Indent.Size, 1, 16);
            _max = Math.Clamp(_lb.MaxLineWidth, 40, 400);
            _base = layout.BaseIndent;
        }

        /// <summary>배치한 문장 번호 표시(키워드 오른쪽 맞춤이 건너뛴다).</summary>
        public bool[] Run()
        {
            var handled = new bool[_bad.Length];
            int k = 0;
            while (k < _code.Count)
            {
                int piece = _items[_code[k]].Piece;
                int e = k;
                while (e < _code.Count && _items[_code[e]].Piece == piece) e++;
                if (piece >= 0 && piece < _bad.Length && !_bad[piece]) handled[piece] = TryPiece(k, e);
                k = e;
            }

            return handled;
        }

        // ---- 문장 준비·되돌림 ----

        FormatItem It(int k) => _items[_t[k]];

        string W(int k) => k >= 0 && k < _n ? It(k).Word : "";

        bool Sym(int k, string s) => k >= 0 && k < _n && It(k).IsSym(s);

        bool TryPiece(int cs, int ce)
        {
            _n = ce - cs;
            _t = new int[_n];
            for (int k = 0; k < _n; k++) _t[k] = _code[cs + k];
            if (W(0) is not ("SELECT" or "WITH" or "INSERT" or "UPDATE" or "DELETE" or "CREATE" or "EXPLAIN")) return false;
            for (int i = _t[0]; i <= _t[_n - 1]; i++)
            {
                if (_frozen[i] || _items[i].IsRaw) return false;
            }

            if (!Prepare()) return false;
            var saved = new Saved[_t[_n - 1] - _t[0] + 1];
            for (int i = 0; i < saved.Length; i++)
            {
                var it = _items[_t[0] + i];
                saved[i] = new Saved(it.Gap, it.Depth, it.Extra, it.LineDepth, it.BlankLines, it.OrigGap);
            }

            _cur = 0;
            _ec = Body;
            _alignSets.Clear();
            bool ok = W(0) switch
            {
                "SELECT" or "WITH" => Query(0, _n - 1, 0),
                "CREATE" or "EXPLAIN" => Prefixed(0, _n - 1),
                _ => Dml(0, _n - 1),
            };
            if (ok)
            {
                PlaceComments();
                if (_o.Align.Comparison) AlignComparisons();
                return true;
            }

            for (int i = 0; i < saved.Length; i++)
            {
                var it = _items[_t[0] + i];
                var s = saved[i];
                (it.Gap, it.Depth, it.Extra, it.LineDepth, it.BlankLines, it.OrigGap) = (s.Gap, s.Depth, s.Extra, s.LineDepth, s.BlankLines, s.OrigGap);
            }

            return false;
        }

        bool Prepare()
        {
            _len = new int[_n];
            _gw = new int[_n];
            _gap = new GapKind[_n];
            _forced = new bool[_n];
            _match = new int[_n];
            _col = new int[_n];
            _brk = new bool[_n];
            _pre = new long[_n];
            _fc = new int[_n];
            Array.Fill(_match, -1);
            var stack = new Stack<int>();
            for (int k = 0; k < _n; k++)
            {
                var it = It(k);
                _len[k] = it.OutText.Contains('\n', StringComparison.Ordinal) ? Big : it.OutText.Length;
                if (k > 0)
                {
                    var g = it.Gap is GapKind.Space or GapKind.None ? it.Gap : RecomputedGap(k);
                    if (it.IsSym("(") && It(k - 1).Is("COLUMNS")) g = GapKind.Space;
                    _gap[k] = g;
                    _gw[k] = g == GapKind.Space ? 1 : 0;
                    for (int i = _t[k - 1] + 1; i <= _t[k]; i++)
                    {
                        var x = _items[i];
                        if (x.IsLineComment || (x.IsComment && x.OrigNewLineBefore) || (i == _t[k] && x.BlankBefore > 0)) _forced[k] = true;
                    }

                    _pre[k] = _pre[k - 1] + _gw[k] + _len[k];
                    _fc[k] = _fc[k - 1] + (_forced[k] ? 1 : 0);
                }

                if (it.IsSym("(") || it.Is("CASE"))
                {
                    stack.Push(k);
                }
                else if (it.IsSym(")") || it.Is("END"))
                {
                    if (stack.Count == 0) return false;
                    int o = stack.Pop();
                    if (It(o).IsSym("(") != it.IsSym(")")) return false;
                    _match[o] = k;
                }
            }

            return stack.Count == 0;
        }

        /// <summary>앞 단계가 줄을 바꿨던 토큰의 한 줄 간격. 괄호 안쪽 공백 옵션(<c>parenInnerSpace</c>)도 다시 적용한다.</summary>
        GapKind RecomputedGap(int k)
        {
            var g = PairGap(_items, _code, CodePos(k), _buf);
            if (!_o.ParenInnerSpace || !_items[_t[k] - 1].IsCode) return g;
            var p = It(k - 1);
            var c = It(k);
            bool outer = (p.IsSym("(") && c.IsSym("+") && Sym(k + 1, ")")) || (c.IsSym(")") && p.IsSym("+") && Sym(k - 2, "("));
            if (!outer && ((p.IsSym("(") && !c.IsSym(")")) || (c.IsSym(")") && !p.IsSym("(")))) return GapKind.Space;
            return g;
        }

        int CodePos(int k)
        {
            int i = _t[k];
            int lo = 0, hi = _code.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_code[mid] == i) return mid;
                if (_code[mid] < i) lo = mid + 1;
                else hi = mid - 1;
            }

            return -1;
        }

        /// <summary>코드 위치 a~b 를 한 줄에 이어 쓴 폭(a 앞 간격 제외). 사이에 줄을 바꿔야 하는 토큰이 있으면 아주 큰 값.</summary>
        long Width(int a, int b)
        {
            if (b < a) return 0;
            if (_fc[b] - _fc[a] > 0) return Big;
            return _len[a] + _pre[b] - _pre[a];
        }

        bool Fits(int start, int a, int b) => _base + start + Width(a, b) <= _max;

        /// <summary>원문에서 a 다음부터 b 까지 줄바꿈·주석이 없었다.</summary>
        bool OrigOneLine(int a, int b)
        {
            for (int i = _t[a] + 1; i <= _t[b]; i++)
            {
                if (_items[i].OrigNewLineBefore || _items[i].IsComment) return false;
            }

            return true;
        }

        // ---- 놓기 ----

        int StartCol(int k, Place p)
        {
            if (_forced[k]) return p.Mode == Mode.Inline ? _ec : p.Col;
            return p.Mode switch
            {
                Mode.NewLine => p.Col,
                Mode.PadTo => Math.Max(_cur + 1, p.Col),
                _ => _cur + _gw[k],
            };
        }

        void Put(int k, Place p)
        {
            var it = It(k);
            if (_forced[k] && p.Mode != Mode.NewLine) p = Place.Nl(p.Mode == Mode.PadTo ? p.Col : _ec);
            if (k == 0) p = Place.Nl(p.Col);
            it.LineDepth = 0;
            it.Depth = 0;
            switch (p.Mode)
            {
                case Mode.NewLine:
                    it.Gap = GapKind.NewLine;
                    it.Extra = Math.Max(0, p.Col);
                    it.BlankLines = it.BlankBefore;
                    _brk[k] = true;
                    _col[k] = it.Extra;
                    break;
                case Mode.PadTo:
                    {
                        int pad = Math.Max(1, p.Col - _cur);
                        SetGap(it, pad);
                        it.Extra = 0;
                        it.BlankLines = 0;
                        _col[k] = _cur + pad;
                        break;
                    }

                default:
                    it.Gap = _gap[k];
                    it.Extra = 0;
                    it.BlankLines = 0;
                    _col[k] = _cur + _gw[k];
                    break;
            }

            string text = it.OutText;
            int nl = text.LastIndexOf('\n');
            _cur = nl >= 0 ? text.Length - nl - 1 : _col[k] + text.Length;
        }

        static void SetGap(FormatItem it, int pad)
        {
            if (pad == 1)
            {
                it.Gap = GapKind.Space;
            }
            else
            {
                it.Gap = GapKind.Verbatim;
                it.OrigGap = new string(' ', pad);
            }
        }

        bool Flat(int a, int b, Place p)
        {
            Put(a, p);
            for (int k = a + 1; k <= b; k++) Put(k, Place.In);
            return true;
        }

        // ---- 질의 ----

        /// <summary>질의 a~b 의 절 머리(코드 위치, 낱말 수). 괄호·CASE 안은 건너뛴다.</summary>
        List<(int Pos, int Words)> Heads(int a, int b)
        {
            var heads = new List<(int, int)>();
            for (int k = a; k <= b; k++)
            {
                if ((Sym(k, "(") || W(k) == "CASE") && _match[k] >= 0)
                {
                    k = _match[k];
                    continue;
                }

                int words = HeadAt(k, a);
                if (words > 0)
                {
                    heads.Add((k, words));
                    k += words - 1;
                }
            }

            return heads;
        }

        int HeadAt(int k, int a)
        {
            string w = W(k), n = W(k + 1);
            if (k == a) return w is "SELECT" or "WITH" ? 1 : 0;
            if (Sym(k - 1, ".")) return 0;
            switch (w)
            {
                case "SELECT" or "FROM" or "WHERE" or "HAVING" or "INTERSECT" or "MINUS":
                    return 1;
                case "UNION" or "EXCEPT":
                    return W(k + 1) is "ALL" or "DISTINCT" ? 2 : 1;
                case "GROUP" or "CONNECT":
                    return n == "BY" ? 2 : 0;
                case "ORDER":
                    return n == "BY" ? 2 : n == "SIBLINGS" && W(k + 2) == "BY" ? 3 : 0;
                case "START":
                    return n == "WITH" ? 2 : 0;
                case "FETCH":
                    return n is "FIRST" or "NEXT" ? 2 : 0;
                case "FOR":
                    return n == "UPDATE" ? 2 : 0;
                case "OFFSET":
                    return k + 1 < _n && It(k + 1).Kind is TokenKind.Number or TokenKind.Bind or TokenKind.Substitution ? 1 : 0;
            }

            return 0;
        }

        bool Query(int a, int b, int baseCol)
        {
            var heads = Heads(a, b);
            if (heads.Count == 0 || heads[0].Pos != a) return false;
            int saveEc = _ec;
            for (int h = 0; h < heads.Count; h++)
            {
                var (hs, hn) = heads[h];
                int bs = hs + hn;
                int be = h + 1 < heads.Count ? heads[h + 1].Pos - 1 : b;
                Put(hs, Place.Nl(baseCol));
                for (int j = hs + 1; j < bs; j++) Put(j, Place.In);
                int bodyCol = baseCol + Math.Max(Body, _cur - baseCol + 1);
                _ec = bodyCol;
                if (bs > be) continue;
                bool ok = W(hs) switch
                {
                    "WITH" => With(bs, be, baseCol),
                    "SELECT" => List(bs, be, baseCol, bodyCol, _lb.SelectList == SelectListLayout.FillToWidth ? ListMode.Fill : ListMode.Each),
                    "FROM" => From(bs, be, baseCol, bodyCol),
                    "WHERE" or "HAVING" or "START" or "CONNECT" => Conds(bs, be, baseCol, Place.Pad(bodyCol), -1),
                    "GROUP" => List(bs, be, baseCol, bodyCol, ByMode(_lb.GroupOrderBy)),
                    "ORDER" => List(bs, be, baseCol, bodyCol, ByMode(_lb.EffectiveOrderBy)),
                    _ => Expr(bs, be, Place.Pad(bodyCol), be),
                };
                if (!ok) return false;
            }

            _ec = saveEc;
            return true;
        }

        static ListMode ByMode(ByListLayout l) => l == ByListLayout.MultiLine ? ListMode.Each : ListMode.Single;

        /// <summary>a~b 를 깊이 0 의 구분 토큰으로 나눈다. 돌려주는 값 = (시작, 끝), 구분 토큰은 각 조각 시작 바로 앞.</summary>
        List<(int S, int E)> Split(int a, int b, Func<int, bool> isSep)
        {
            var parts = new List<(int, int)>();
            int start = a;
            for (int k = a; k <= b; k++)
            {
                if ((Sym(k, "(") || W(k) == "CASE") && _match[k] >= 0)
                {
                    k = _match[k];
                    continue;
                }

                if (k > a && isSep(k))
                {
                    parts.Add((start, k - 1));
                    start = k + 1;
                }
            }

            parts.Add((start, b));
            return parts;
        }

        List<(int S, int E)> Commas(int a, int b) => Split(a, b, k => Sym(k, ","));

        /// <summary>깊이 0 의 AND·OR 로 나눈다(BETWEEN … AND 의 AND 는 빼고).</summary>
        List<(int S, int E)> Logic(int a, int b)
        {
            bool between = false;
            return Split(a, b, k =>
            {
                string w = W(k);
                if (w == "BETWEEN") between = true;
                if (w == "AND" && between)
                {
                    between = false;
                    return false;
                }

                return w is "AND" or "OR";
            });
        }

        bool List(int a, int b, int baseCol, int bodyCol, ListMode mode)
        {
            var elems = Commas(a, b);
            if (elems.Exists(e => e.E < e.S)) return false;
            int cont = baseCol + Body;
            if (mode == ListMode.Single) mode = Fits(bodyCol, a, b) ? ListMode.Fill : ListMode.Each;
            for (int i = 0; i < elems.Count; i++)
            {
                var (s, e) = elems[i];
                if (i == 0)
                {
                    _ec = bodyCol;
                    if (!Expr(s, e, Place.Pad(bodyCol), e)) return false;
                    continue;
                }

                int c = s - 1;
                bool brk = mode == ListMode.Each || (mode == ListMode.Fill && !Fits(_cur + 2, s, e));
                _ec = cont;
                bool ok;
                if (!brk)
                {
                    Put(c, Place.In);
                    ok = Expr(s, e, Place.In, e);
                }
                else if (_o.CommaPosition == CommaPosition.Leading)
                {
                    Put(c, Place.Nl(baseCol + CommaCol));
                    ok = Expr(s, e, Place.Pad(cont), e);
                }
                else
                {
                    Put(c, Place.In);
                    ok = Expr(s, e, Place.Nl(cont), e);
                }

                if (!ok) return false;
            }

            return true;
        }

        bool IsJoinAt(int k)
        {
            string w = W(k);
            if (w is not ("JOIN" or "INNER" or "LEFT" or "RIGHT" or "FULL" or "CROSS" or "NATURAL" or "OUTER")) return false;
            if (Sym(k - 1, ".")) return false;
            for (int j = k; j < Math.Min(_n, k + 5); j++)
            {
                string x = W(j);
                if (x is "JOIN" or "APPLY") return true;
                if (x is not ("INNER" or "LEFT" or "RIGHT" or "FULL" or "CROSS" or "NATURAL" or "OUTER")) return false;
            }

            return false;
        }

        bool From(int a, int b, int baseCol, int bodyCol)
        {
            // 조각: 첫 표, 쉼표 표, 조인.
            var units = new List<(char Kind, int S, int E)>();
            int start = a;
            char kind = 'F';
            for (int k = a; k <= b; k++)
            {
                if ((Sym(k, "(") || W(k) == "CASE") && _match[k] >= 0)
                {
                    k = _match[k];
                    continue;
                }

                if (k == start) continue;
                bool comma = Sym(k, ",");
                bool join = !comma && IsJoinAt(k) && !(kind == 'J' && IsJoinWordRun(start, k));
                if (comma || join)
                {
                    units.Add((kind, start, k - 1));
                    start = k;
                    kind = comma ? ',' : 'J';
                }
            }

            units.Add((kind, start, b));
            int cont = baseCol + Body;
            foreach (var (uk, s, e) in units)
            {
                bool ok;
                switch (uk)
                {
                    case 'F':
                        _ec = bodyCol;
                        ok = Expr(s, e, Place.Pad(bodyCol), e);
                        break;
                    case ',':
                        if (s + 1 > e) return false;
                        _ec = cont;
                        if (_o.CommaPosition == CommaPosition.Leading)
                        {
                            Put(s, Place.Nl(baseCol + CommaCol));
                            ok = Expr(s + 1, e, Place.Pad(cont), e);
                        }
                        else
                        {
                            Put(s, Place.In);
                            ok = Expr(s + 1, e, Place.Nl(cont), e);
                        }

                        break;
                    default:
                        ok = Join(s, e, baseCol);
                        break;
                }

                if (!ok) return false;
            }

            return true;
        }

        bool IsJoinWordRun(int s, int k)
        {
            for (int j = s; j < k; j++)
            {
                if (W(j) is "JOIN" or "APPLY") return false;
            }

            return true;
        }

        bool Join(int s, int e, int baseCol)
        {
            int j = s;
            while (j <= e && W(j) is not ("JOIN" or "APPLY")) j++;
            if (j > e) return false;
            Put(s, _lb.JoinClause ? Place.Nl(baseCol) : Place.In);
            for (int x = s + 1; x <= j; x++) Put(x, Place.In);
            int tableCol = baseCol + Math.Max(Body, _cur - baseCol + 1);
            int on = -1;
            for (int k = j + 1; k <= e; k++)
            {
                if ((Sym(k, "(") || W(k) == "CASE") && _match[k] >= 0)
                {
                    k = _match[k];
                    continue;
                }

                if (W(k) == "ON")
                {
                    on = k;
                    break;
                }
            }

            int te = on >= 0 ? on - 1 : e;
            if (j + 1 > te) return false;
            _ec = tableCol;
            if (!Expr(j + 1, te, _lb.JoinClause ? Place.Pad(tableCol) : Place.In, te)) return false;
            if (on < 0) return true;
            Put(on, Place.Nl(baseCol));
            if (on + 1 > e) return false;
            _ec = baseCol + Body;
            return Conds(on + 1, e, baseCol, Place.Pad(baseCol + Body), -1);
        }

        /// <summary>조건 목록. <paramref name="inner"/> ≥ 0 이면 괄호 묶음 안(AND·OR 를 안쪽 열 앞에 오른쪽으로 붙인다).</summary>
        bool Conds(int a, int b, int baseCol, Place first, int inner)
        {
            var conds = Logic(a, b);
            if (conds.Exists(c => c.E < c.S)) return false;
            bool group = inner >= 0;
            for (int i = 0; i < conds.Count; i++)
            {
                var (s, e) = conds[i];
                Place p;
                if (i == 0)
                {
                    p = first;
                    _ec = group ? inner : StartCol(s, first);
                }
                else
                {
                    int op = s - 1;
                    int opLen = It(op).OutText.Length;
                    if (group)
                    {
                        Put(op, Place.Nl(Math.Max(0, inner - opLen - 1)));
                        p = Place.In;
                        _ec = inner;
                    }
                    else
                    {
                        _ec = baseCol + Body;
                        switch (_lb.AndOr)
                        {
                            case AndOrBreak.Before:
                                Put(op, Place.Nl(baseCol));
                                p = Place.Pad(baseCol + Math.Max(Body, opLen + 1));
                                break;
                            case AndOrBreak.After:
                                Put(op, Place.In);
                                p = Place.Nl(baseCol + Body);
                                break;
                            default:
                                Put(op, Place.In);
                                p = Place.In;
                                break;
                        }
                    }
                }

                if (!Expr(s, e, p, e)) return false;
            }

            if (group || _lb.AndOr != AndOrBreak.None) _alignSets.Add(conds);
            return true;
        }

        // ---- 식 ----

        bool IsQueryAt(int k) => W(k) is "SELECT" or "WITH";

        bool Expr(int s, int e, Place p, int le)
        {
            for (int k = s; k <= e; k++)
            {
                var pl = k == s ? p : Place.In;
                if (Sym(k, "("))
                {
                    int m = _match[k];
                    if (m < 0 || m > e) return false;
                    bool ok;
                    if (IsQueryAt(k + 1)) ok = Sub(k, m, pl, le, k == s);
                    else if (_lb.AndOr != AndOrBreak.None && Logic(k + 1, m - 1).Count > 1) ok = Group(k, m, pl, le);
                    else
                    {
                        Put(k, pl);
                        ok = m == k + 1 || Expr(k + 1, m - 1, Place.In, le);
                        Put(m, Place.In);
                    }

                    if (!ok) return false;
                    k = m;
                    continue;
                }

                if (W(k) == "CASE")
                {
                    int m = _match[k];
                    if (m < 0 || m > e || !Case(k, m, pl, le)) return false;
                    k = m;
                    continue;
                }

                if (Sym(k, ")") || W(k) == "END") return false;
                Put(k, pl);
            }

            return true;
        }

        bool Sub(int k, int m, Place pl, int le, bool first)
        {
            int start = first ? StartCol(k, pl) : StartCol(k, Place.In);
            if (Simple(k + 1, m - 1) && OrigOneLine(k, m) && Fits(start, k, le)) return Flat(k, m, pl);
            int saveEc = _ec;
            int pc;
            if (first)
            {
                Put(k, pl);
                pc = _col[k];
            }
            else
            {
                pc = _ec;
                Put(k, Place.Nl(pc));
            }

            if (!Query(k + 1, m - 1, pc + _unit)) return false;
            Put(m, Place.Nl(pc));
            _ec = saveEc;
            return true;
        }

        /// <summary>한 줄로 둘 만한 서브쿼리: SELECT … FROM 표 하나 [WHERE 조건 하나], 안쪽 서브쿼리·다른 절 없음.</summary>
        bool Simple(int a, int b)
        {
            if (W(a) != "SELECT") return false;
            for (int k = a + 1; k <= b; k++)
            {
                if (Sym(k, "(") && IsQueryAt(k + 1)) return false;
            }

            var heads = Heads(a, b);
            for (int h = 0; h < heads.Count; h++)
            {
                var (hs, hn) = heads[h];
                int bs = hs + hn;
                int be = h + 1 < heads.Count ? heads[h + 1].Pos - 1 : b;
                switch (W(hs))
                {
                    case "SELECT" when h == 0:
                        break;
                    case "FROM":
                        if (Commas(bs, be).Count > 1) return false;
                        for (int k = bs; k <= be; k++)
                        {
                            if (IsJoinAt(k)) return false;
                        }

                        break;
                    case "WHERE":
                        if (Logic(bs, be).Count > 1) return false;
                        break;
                    default:
                        return false;
                }
            }

            return true;
        }

        bool Group(int k, int m, Place pl, int le)
        {
            if (Fits(StartCol(k, pl), k, le)) return Flat(k, m, pl);
            int saveEc = _ec;
            Put(k, pl);
            int pc = _col[k];
            if (!Conds(k + 1, m - 1, pc, Place.Nl(pc + _unit), pc + _unit)) return false;
            Put(m, Place.Nl(pc));
            _ec = saveEc;
            return true;
        }

        bool Case(int k, int m, Place pl, int le)
        {
            var whens = new List<int>();
            for (int j = k + 1; j < m; j++)
            {
                if ((Sym(j, "(") || W(j) == "CASE") && _match[j] >= 0)
                {
                    j = _match[j];
                    continue;
                }

                if (W(j) is "WHEN" or "ELSE") whens.Add(j);
            }

            if (whens.Count == 0) return false;
            int whenCount = whens.Count(j => W(j) == "WHEN");
            if (whenCount <= 1 && OrigOneLine(k, m) && Fits(StartCol(k, pl), k, le)) return Flat(k, m, pl);
            int saveEc = _ec;
            Put(k, pl);
            int cc = _col[k];
            _ec = cc + CaseStep;
            if (whens[0] > k + 1 && !Expr(k + 1, whens[0] - 1, Place.In, whens[0] - 1)) return false;
            for (int i = 0; i < whens.Count; i++)
            {
                int w = whens[i];
                int se = (i + 1 < whens.Count ? whens[i + 1] : m) - 1;
                Put(w, i == 0 ? Place.In : Place.Nl(cc + CaseStep));
                if (w + 1 <= se && !Expr(w + 1, se, Place.In, se)) return false;
            }

            Put(m, Place.Nl(cc));
            _ec = saveEc;
            return true;
        }

        bool With(int a, int b, int baseCol)
        {
            var ctes = Commas(a, b);
            for (int i = 0; i < ctes.Count; i++)
            {
                var (s, e) = ctes[i];
                if (e < s) return false;
                if (i == 0)
                {
                    Put(s, Place.In);
                }
                else if (_o.CommaPosition == CommaPosition.Leading)
                {
                    Put(s - 1, Place.Nl(baseCol));
                    Put(s, Place.In);
                }
                else
                {
                    Put(s - 1, Place.In);
                    Put(s, Place.Nl(baseCol));
                }

                int asPos = -1;
                for (int k = s + 1; k <= e; k++)
                {
                    if (Sym(k, "(") && _match[k] >= 0 && asPos < 0)
                    {
                        k = _match[k];
                        continue;
                    }

                    if (W(k) == "AS")
                    {
                        asPos = k;
                        break;
                    }
                }

                if (asPos < 0) return false;
                int open = asPos + 1;
                while (open <= e && It(open).Kind == TokenKind.Hint) open++;
                if (!Sym(open, "(") || !IsQueryAt(open + 1)) return false;
                int close = _match[open];
                if (close < 0 || close > e) return false;
                _ec = baseCol + Body;
                if (!Expr(s + 1, open - 1, Place.In, open - 1)) return false;
                Put(open, Place.Nl(baseCol));
                if (!Query(open + 1, close - 1, baseCol + _unit)) return false;
                Put(close, Place.Nl(baseCol));
                if (close < e && !Expr(close + 1, e, Place.In, e)) return false;
            }

            return true;
        }

        // ---- CREATE … AS 질의·EXPLAIN PLAN … FOR 질의 ----

        /// <summary><c>CREATE [OR REPLACE] VIEW·TABLE … AS 질의</c>, <c>EXPLAIN PLAN … FOR 질의</c>: 머리는 첫 줄에 잇고 질의는 다음 줄 맨 앞 열부터.</summary>
        bool Prefixed(int a, int b)
        {
            string glue = W(a) == "CREATE" ? "AS" : "FOR";
            int q = -1;
            for (int k = a + 1; k <= b; k++)
            {
                if (Sym(k, "(") && _match[k] >= 0)
                {
                    if (IsQueryAt(k + 1)) return false;
                    k = _match[k];
                    continue;
                }

                if (IsQueryAt(k) && W(k - 1) == glue)
                {
                    q = k;
                    break;
                }
            }

            if (q < 0) return false;
            if (W(a) == "CREATE")
            {
                for (int k = a + 1; k < q; k++)
                {
                    if (W(k) is "VIEW" or "TABLE") break;
                    if (k == q - 1) return false;
                }
            }

            _ec = Body;
            return Expr(a, q - 1, Place.Nl(0), q - 1) && Query(q, b, 0);
        }

        // ---- INSERT·UPDATE·DELETE ----

        bool Dml(int a, int b)
        {
            string lead = W(a);
            var heads = new List<(int Pos, int Words)>();
            int query = -1;
            if (lead == "INSERT")
            {
                if (W(a + 1) != "INTO") return false;
                heads.Add((a, 2));
            }
            else
            {
                heads.Add((a, lead == "DELETE" && W(a + 1) == "FROM" ? 2 : 1));
            }

            for (int k = a + heads[0].Words; k <= b; k++)
            {
                if ((Sym(k, "(") || W(k) == "CASE") && _match[k] >= 0)
                {
                    k = _match[k];
                    continue;
                }

                string w = W(k);
                bool head = lead switch
                {
                    "UPDATE" => w is "SET" or "WHERE" or "RETURNING" or "RETURN",
                    "DELETE" => w is "WHERE" or "RETURNING" or "RETURN",
                    _ => w is "VALUES" or "RETURNING" or "RETURN",
                };
                if (lead == "INSERT" && IsQueryAt(k))
                {
                    query = k;
                    break;
                }

                if (head && !Sym(k - 1, ".")) heads.Add((k, 1));
            }

            int end = query >= 0 ? query - 1 : b;
            for (int h = 0; h < heads.Count; h++)
            {
                var (hs, hn) = heads[h];
                int bs = hs + hn;
                int be = h + 1 < heads.Count ? heads[h + 1].Pos - 1 : end;
                Put(hs, Place.Nl(0));
                for (int j = hs + 1; j < bs; j++) Put(j, Place.In);
                int bodyCol = Math.Max(Body, _cur + 1);
                _ec = bodyCol;
                if (bs > be) continue;
                bool ok = W(hs) switch
                {
                    "SET" => List(bs, be, 0, bodyCol, ListMode.Each),
                    "WHERE" => Conds(bs, be, 0, Place.Pad(bodyCol), -1),
                    _ => Expr(bs, be, Place.Pad(bodyCol), be),
                };
                if (!ok) return false;
            }

            return query < 0 || Query(query, b, 0);
        }

        // ---- 주석·비교 맞춤 ----

        void PlaceComments()
        {
            int next = 0;
            for (int i = _t[0] + 1; i < _t[_n - 1]; i++)
            {
                var it = _items[i];
                if (!it.IsComment) continue;
                while (next < _n && _t[next] < i) next++;
                if (next >= _n) break;
                var prev = _items[i - 1];
                it.Depth = 0;
                it.LineDepth = 0;
                if (it.OrigNewLineBefore || prev.IsLineComment || prev.IsRaw)
                {
                    it.Gap = GapKind.NewLine;
                    it.Extra = _col[next];
                    it.BlankLines = it.BlankBefore;
                    continue;
                }

                it.Extra = 0;
                it.BlankLines = 0;
                int gap = prev.Start >= 0 && it.Start >= 0 && prev.EndLine == it.Line ? Math.Max(1, it.Start - prev.End) : 1;
                if (it.IsLineComment) SetGap(it, gap);
                else it.Gap = GapKind.Space;
            }
        }

        static bool IsCompare(FormatItem t) =>
            t.Kind == TokenKind.Operator && t.Text is "=" or "<>" or "!=" or "^=" or "~=" or "<" or ">" or "<=" or ">=";

        int CompareOp(int s, int e)
        {
            for (int k = s; k <= e; k++)
            {
                if ((Sym(k, "(") || W(k) == "CASE") && _match[k] >= 0)
                {
                    k = _match[k];
                    continue;
                }

                if (IsCompare(It(k))) return k > s && k < e ? k : -1;
            }

            return -1;
        }

        bool OuterJoinLhs(int s, int op) => op - s >= 4 && Sym(op - 3, "(") && Sym(op - 2, "+") && Sym(op - 1, ")");

        bool SameLhs(int s1, int op1, int s2, int op2)
        {
            if (op1 - s1 != op2 - s2) return false;
            for (int k = 0; k < op1 - s1; k++)
            {
                if (!string.Equals(It(s1 + k).OutText, It(s2 + k).OutText, StringComparison.Ordinal)) return false;
            }

            return true;
        }

        bool Related((int S, int Op) x, (int S, int Op) y) =>
            SameLhs(x.S, x.Op, y.S, y.Op)
            || (OuterJoinLhs(x.S, x.Op) && OuterJoinLhs(y.S, y.Op) && string.Equals(It(x.S).OutText, It(y.S).OutText, StringComparison.Ordinal));

        /// <summary>외부 조인 (+) 묶음(같은 표 별칭)·같은 왼쪽 식이 이어진 조건 줄의 비교 연산자와 오른쪽 값 시작 열을 맞춘다.</summary>
        void AlignComparisons()
        {
            foreach (var conds in _alignSets)
            {
                var rows = new List<(int S, int Op)>();
                foreach (var (s, e) in conds)
                {
                    int op = CompareOp(s, e);
                    bool oneLine = op >= 0 && _len[s] < Big;
                    for (int k = s + 1; oneLine && k <= op + 1; k++) oneLine = !_brk[k] && It(k).Gap != GapKind.Verbatim && _len[k] < Big;
                    rows.Add(oneLine ? (s, op) : (s, -1));
                }

                int i = 0;
                while (i < rows.Count)
                {
                    int j = i;
                    while (rows[i].Op >= 0 && j + 1 < rows.Count && rows[j + 1].Op >= 0 && Related(rows[j], rows[j + 1])) j++;
                    if (j > i) AlignRun(rows.GetRange(i, j - i + 1));
                    i = j + 1;
                }
            }
        }

        void AlignRun(List<(int S, int Op)> run)
        {
            int opStart = 0;
            foreach (var (_, op) in run) opStart = Math.Max(opStart, _col[op - 1] + _len[op - 1] + 1);
            int rhsStart = 0;
            foreach (var (_, op) in run) rhsStart = Math.Max(rhsStart, opStart + _len[op] + 1);
            foreach (var (_, op) in run)
            {
                int lhsEnd = _col[op - 1] + _len[op - 1];
                SetGap(It(op), opStart - lhsEnd);
                SetGap(It(op + 1), rhsStart - (opStart + _len[op]));
            }
        }
    }
}
