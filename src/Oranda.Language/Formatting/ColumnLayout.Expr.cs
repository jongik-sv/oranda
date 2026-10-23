using Oranda.Core.Formatting;
using Oranda.Language.Lexing;

namespace Oranda.Language.Formatting;

// ColumnLayout 의 일부: 식(ColumnLayout.cs 에서 나눔, 동작 불변).
internal static partial class FormatEngine
{
    sealed partial class ColumnLayout
    {
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
    }
}
