using System.Collections.Frozen;
using Oranda.Core.Formatting;
using Oranda.Language.Lexing;

namespace Oranda.Language.Formatting;

// LayoutRules 의 일부: 제외 구간(LayoutRules.cs 에서 나눔, 동작 불변).
internal static partial class FormatEngine
{
    sealed partial class LayoutRules
    {
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
