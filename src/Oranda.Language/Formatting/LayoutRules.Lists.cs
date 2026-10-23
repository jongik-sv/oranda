using System.Collections.Frozen;
using Oranda.Core.Formatting;
using Oranda.Language.Lexing;

namespace Oranda.Language.Formatting;

// LayoutRules 의 일부: 목록 닫기·쉼표·IN 목록·줄 바꾸기(LayoutRules.cs 에서 나눔, 동작 불변).
internal static partial class FormatEngine
{
    sealed partial class LayoutRules
    {
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
    }
}
