using System.Collections.Frozen;
using Oranda.Core.Formatting;
using Oranda.Language.Lexing;

namespace Oranda.Language.Formatting;

// LayoutRules 의 일부: 줄바꿈·쉼표 배치(LayoutRules.cs 에서 나눔, 동작 불변).
internal static partial class FormatEngine
{
    sealed partial class LayoutRules
    {
        // ---- 줄바꿈(FMT-05)·쉼표(FMT-04) ----

        int _col;

        void Advance(FormatItem it)
        {
            switch (it.Gap)
            {
                case GapKind.NewLine:
                    _col = it.IsRaw ? 0 : Math.Max(0, (it.Depth * _unit) + it.Extra);
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
                else if ((w == "GROUP" ? _lb.GroupOrderBy : w == "ORDER" ? _lb.EffectiveOrderBy : ByListLayout.SingleLine) == ByListLayout.MultiLine)
                {
                    // GROUP BY·ORDER BY 목록 한 줄에 하나씩(TSK-13-38). 원소 범위는 정렬에 쓰지 않는다.
                    top.Kind = ListKind.ByList;
                    top.OnePerLine = true;
                    top.BreakDepth = top.ClauseDepth + 1;
                    top.Elems = [];
                    top.ElemStart = -2;
                }

                return;
            }

            if (top.ClauseWord == "FROM" && it.Gap != GapKind.NewLine && IsJoinStart(i))
            {
                if (_lb.JoinClause && Break(i, top.ClauseDepth)) _joinLine[i] = true;
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
                    if (Break(i, top.CondDepth)) _condLine[i] = true;
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
    }
}
