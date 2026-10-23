using Oranda.Core.Formatting;
using Oranda.Language.Lexing;

namespace Oranda.Language.Formatting;

// ColumnLayout 의 일부: 주석·비교 맞춤(ColumnLayout.cs 에서 나눔, 동작 불변).
internal static partial class FormatEngine
{
    sealed partial class ColumnLayout
    {
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
