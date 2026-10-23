using System.Collections.Frozen;
using Oranda.Core.Formatting;
using Oranda.Language.Lexing;

namespace Oranda.Language.Formatting;

// LayoutRules 의 일부: 준비·연산자·괄호 공백(LayoutRules.cs 에서 나눔, 동작 불변).
internal static partial class FormatEngine
{
    sealed partial class LayoutRules
    {
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
    }
}
