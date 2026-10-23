using System.Collections.Frozen;
using Oranda.Core.Formatting;
using Oranda.Language.Lexing;

namespace Oranda.Language.Formatting;

// LayoutRules 의 일부: 키워드 오른쪽 맞춤(LayoutRules.cs 에서 나눔, 동작 불변).
internal static partial class FormatEngine
{
    sealed partial class LayoutRules
    {
        // ---- 키워드 오른쪽 맞춤(TSK-13-38) ----

        /// <summary>강(river)의 오른쪽 끝 = <c>SELECT</c> 글자 수.</summary>
        const int RiverWidth = 6;

        /// <summary>
        /// 절 머리(SELECT·FROM·WHERE·JOIN …) 줄은 키워드 끝을 <c>SELECT</c> 끝에 맞추고(짧은 키워드 앞에 공백), 절 안 줄은 강 오른쪽
        /// (<c>SELECT </c> 다음 칸)에서 시작하게 옮긴다. AND/OR·앞 쉼표 줄은 그 낱말·쉼표가 강 왼쪽에 걸리게 한다.
        /// 줄 처음의 닫는 괄호·CASE 의 END 는 여는 쪽이 있던 줄과 같은 칸에 둔다. WITH 는 강에 올리지 않는다(CTE 본문이 앞 칸에서 시작).
        /// 깊이(<see cref="FormatItem.Depth"/>)는 그대로 두고 <see cref="FormatItem.Extra"/> 만 정하므로 중첩 구조의 상대 들여쓰기는 같다.
        /// 줄을 바꾸지 않으니 다시 포맷해도 같다.
        /// </summary>
        void RiverPass()
        {
            int content = RiverWidth + 1 - _unit;
            var heads = new List<int>(8);
            var open = new List<(bool IsCase, int Extra)>(8);
            var pending = new List<FormatItem>(2);
            int piece = int.MinValue, lineExtra = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (it.IsCode && it.Piece != piece)
                {
                    piece = it.Piece;
                    heads.Clear();
                    open.Clear();
                }

                bool lineStart = i == 0 || it.Gap == GapKind.NewLine;
                if (lineStart && it.IsComment)
                {
                    pending.Add(it);
                    continue;
                }

                if (it.Piece >= 0 && it.Piece < _column.Length && _column[it.Piece])
                {
                    // 사내 서식 배치(ColumnLayout)가 열을 정한 문장.
                    pending.Clear();
                    continue;
                }

                if (lineStart)
                {
                    lineExtra = 0;
                    if (it.IsRaw || !Active(i))
                    {
                        heads.Clear();
                    }
                    else
                    {
                        lineExtra = LineExtra(i, it, heads, open, content);
                    }

                    it.Extra = lineExtra;
                    foreach (var c in pending) c.Extra = lineExtra;
                    pending.Clear();
                }

                if (!it.IsCode || it.IsRaw) continue;
                if (it.IsSym("("))
                {
                    open.Add((false, lineExtra));
                }
                else if (it.IsSym(")"))
                {
                    int at = open.FindLastIndex(o => !o.IsCase);
                    if (at >= 0) open.RemoveRange(at, open.Count - at);
                }
                else if (it.Is("CASE") && CodeBefore(i)?.Is("END") != true)
                {
                    open.Add((true, lineExtra));
                }
                else if (it.Is("END") && open.Count > 0 && open[^1].IsCase)
                {
                    open.RemoveAt(open.Count - 1);
                }
                else if (it.IsSym(";"))
                {
                    heads.Clear();
                    open.Clear();
                }
            }
        }

        int LineExtra(int i, FormatItem it, List<int> heads, List<(bool IsCase, int Extra)> open, int content)
        {
            int d = it.Depth;
            bool closer = (it.IsSym(")") && open.Exists(o => !o.IsCase)) || (it.Is("END") && open.Count > 0 && open[^1].IsCase);
            if (closer)
            {
                // 여는 쪽 줄과 같은 칸. 그 줄의 절 머리는 아직 이어진다(뒤에 오는 같은 절의 줄).
                while (heads.Count > 0 && heads[^1] > d) heads.RemoveAt(heads.Count - 1);
                return it.IsSym(")") ? open[open.FindLastIndex(o => !o.IsCase)].Extra : open[^1].Extra;
            }

            while (heads.Count > 0 && heads[^1] >= d) heads.RemoveAt(heads.Count - 1);
            int extra = heads.Count * content;
            if ((it.ClauseHead && !it.Is("WITH")) || _joinLine[i])
            {
                heads.Add(d);
                return extra + Math.Max(0, RiverWidth - it.OutText.Length);
            }

            if (heads.Count > 0 && d == heads[^1] + 1)
            {
                // 앞 줄 주석 때문에 이미 줄 처음에 온 AND/OR·JOIN 도 같은 자리에 맞춘다.
                if (_condLine[i] || it.Word is "AND" or "OR") return extra - (it.OutText.Length + 1);
                if (IsJoinStart(i)) return extra - content - _unit + Math.Max(0, RiverWidth - it.OutText.Length);
                if (it.IsSym(",") && i + 1 < _items.Count && _items[i + 1].Gap == GapKind.Space) return extra - 2;
            }

            return extra;
        }
    }
}
