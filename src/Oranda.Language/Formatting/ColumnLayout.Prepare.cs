using Oranda.Core.Formatting;
using Oranda.Language.Lexing;

namespace Oranda.Language.Formatting;

// ColumnLayout 의 일부: 문장 준비·되돌림(ColumnLayout.cs 에서 나눔, 동작 불변).
internal static partial class FormatEngine
{
    sealed partial class ColumnLayout
    {
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
    }
}
