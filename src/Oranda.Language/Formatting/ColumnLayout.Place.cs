using Oranda.Core.Formatting;
using Oranda.Language.Lexing;

namespace Oranda.Language.Formatting;

// ColumnLayout 의 일부: 놓기(ColumnLayout.cs 에서 나눔, 동작 불변).
internal static partial class FormatEngine
{
    sealed partial class ColumnLayout
    {
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
    }
}
