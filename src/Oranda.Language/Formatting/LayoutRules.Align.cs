using System.Collections.Frozen;
using Oranda.Core.Formatting;
using Oranda.Language.Lexing;

namespace Oranda.Language.Formatting;

// LayoutRules 의 일부: 정렬(LayoutRules.cs 에서 나눔, 동작 불변).
internal static partial class FormatEngine
{
    sealed partial class LayoutRules
    {
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
    }
}
