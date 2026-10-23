using Oranda.Core.Formatting;
using Oranda.Language.Lexing;

namespace Oranda.Language.Formatting;

// ColumnLayout 의 일부: 질의(ColumnLayout.cs 에서 나눔, 동작 불변).
internal static partial class FormatEngine
{
    sealed partial class ColumnLayout
    {
        // ---- 질의 ----

        /// <summary>질의 a~b 의 절 머리(코드 위치, 낱말 수). 괄호·CASE 안은 건너뛴다.</summary>
        List<(int Pos, int Words)> Heads(int a, int b)
        {
            var heads = new List<(int, int)>();
            for (int k = a; k <= b; k++)
            {
                if ((Sym(k, "(") || W(k) == "CASE") && _match[k] >= 0)
                {
                    k = _match[k];
                    continue;
                }

                int words = HeadAt(k, a);
                if (words > 0)
                {
                    heads.Add((k, words));
                    k += words - 1;
                }
            }

            return heads;
        }

        int HeadAt(int k, int a)
        {
            string w = W(k), n = W(k + 1);
            if (k == a) return w is "SELECT" or "WITH" ? 1 : 0;
            if (Sym(k - 1, ".")) return 0;
            switch (w)
            {
                case "SELECT" or "FROM" or "WHERE" or "HAVING" or "INTERSECT" or "MINUS":
                    return 1;
                case "UNION" or "EXCEPT":
                    return W(k + 1) is "ALL" or "DISTINCT" ? 2 : 1;
                case "GROUP" or "CONNECT":
                    return n == "BY" ? 2 : 0;
                case "ORDER":
                    return n == "BY" ? 2 : n == "SIBLINGS" && W(k + 2) == "BY" ? 3 : 0;
                case "START":
                    return n == "WITH" ? 2 : 0;
                case "FETCH":
                    return n is "FIRST" or "NEXT" ? 2 : 0;
                case "FOR":
                    return n == "UPDATE" ? 2 : 0;
                case "OFFSET":
                    return k + 1 < _n && It(k + 1).Kind is TokenKind.Number or TokenKind.Bind or TokenKind.Substitution ? 1 : 0;
            }

            return 0;
        }

        bool Query(int a, int b, int baseCol)
        {
            var heads = Heads(a, b);
            if (heads.Count == 0 || heads[0].Pos != a) return false;
            int saveEc = _ec;
            for (int h = 0; h < heads.Count; h++)
            {
                var (hs, hn) = heads[h];
                int bs = hs + hn;
                int be = h + 1 < heads.Count ? heads[h + 1].Pos - 1 : b;
                Put(hs, Place.Nl(baseCol));
                for (int j = hs + 1; j < bs; j++) Put(j, Place.In);
                int bodyCol = baseCol + Math.Max(Body, _cur - baseCol + 1);
                _ec = bodyCol;
                if (bs > be) continue;
                bool ok = W(hs) switch
                {
                    "WITH" => With(bs, be, baseCol),
                    "SELECT" => List(bs, be, baseCol, bodyCol, _lb.SelectList == SelectListLayout.FillToWidth ? ListMode.Fill : ListMode.Each),
                    "FROM" => From(bs, be, baseCol, bodyCol),
                    "WHERE" or "HAVING" or "START" or "CONNECT" => Conds(bs, be, baseCol, Place.Pad(bodyCol), -1),
                    "GROUP" => List(bs, be, baseCol, bodyCol, ByMode(_lb.GroupOrderBy)),
                    "ORDER" => List(bs, be, baseCol, bodyCol, ByMode(_lb.EffectiveOrderBy)),
                    _ => Expr(bs, be, Place.Pad(bodyCol), be),
                };
                if (!ok) return false;
            }

            _ec = saveEc;
            return true;
        }

        static ListMode ByMode(ByListLayout l) => l == ByListLayout.MultiLine ? ListMode.Each : ListMode.Single;

        /// <summary>a~b 를 깊이 0 의 구분 토큰으로 나눈다. 돌려주는 값 = (시작, 끝), 구분 토큰은 각 조각 시작 바로 앞.</summary>
        List<(int S, int E)> Split(int a, int b, Func<int, bool> isSep)
        {
            var parts = new List<(int, int)>();
            int start = a;
            for (int k = a; k <= b; k++)
            {
                if ((Sym(k, "(") || W(k) == "CASE") && _match[k] >= 0)
                {
                    k = _match[k];
                    continue;
                }

                if (k > a && isSep(k))
                {
                    parts.Add((start, k - 1));
                    start = k + 1;
                }
            }

            parts.Add((start, b));
            return parts;
        }

        List<(int S, int E)> Commas(int a, int b) => Split(a, b, k => Sym(k, ","));

        /// <summary>깊이 0 의 AND·OR 로 나눈다(BETWEEN … AND 의 AND 는 빼고).</summary>
        List<(int S, int E)> Logic(int a, int b)
        {
            bool between = false;
            return Split(a, b, k =>
            {
                string w = W(k);
                if (w == "BETWEEN") between = true;
                if (w == "AND" && between)
                {
                    between = false;
                    return false;
                }

                return w is "AND" or "OR";
            });
        }

        bool List(int a, int b, int baseCol, int bodyCol, ListMode mode)
        {
            var elems = Commas(a, b);
            if (elems.Exists(e => e.E < e.S)) return false;
            int cont = baseCol + Body;
            if (mode == ListMode.Single) mode = Fits(bodyCol, a, b) ? ListMode.Fill : ListMode.Each;
            for (int i = 0; i < elems.Count; i++)
            {
                var (s, e) = elems[i];
                if (i == 0)
                {
                    _ec = bodyCol;
                    if (!Expr(s, e, Place.Pad(bodyCol), e)) return false;
                    continue;
                }

                int c = s - 1;
                bool brk = mode == ListMode.Each || (mode == ListMode.Fill && !Fits(_cur + 2, s, e));
                _ec = cont;
                bool ok;
                if (!brk)
                {
                    Put(c, Place.In);
                    ok = Expr(s, e, Place.In, e);
                }
                else if (_o.CommaPosition == CommaPosition.Leading)
                {
                    Put(c, Place.Nl(baseCol + CommaCol));
                    ok = Expr(s, e, Place.Pad(cont), e);
                }
                else
                {
                    Put(c, Place.In);
                    ok = Expr(s, e, Place.Nl(cont), e);
                }

                if (!ok) return false;
            }

            return true;
        }

        bool IsJoinAt(int k)
        {
            string w = W(k);
            if (w is not ("JOIN" or "INNER" or "LEFT" or "RIGHT" or "FULL" or "CROSS" or "NATURAL" or "OUTER")) return false;
            if (Sym(k - 1, ".")) return false;
            for (int j = k; j < Math.Min(_n, k + 5); j++)
            {
                string x = W(j);
                if (x is "JOIN" or "APPLY") return true;
                if (x is not ("INNER" or "LEFT" or "RIGHT" or "FULL" or "CROSS" or "NATURAL" or "OUTER")) return false;
            }

            return false;
        }

        bool From(int a, int b, int baseCol, int bodyCol)
        {
            // 조각: 첫 표, 쉼표 표, 조인.
            var units = new List<(char Kind, int S, int E)>();
            int start = a;
            char kind = 'F';
            for (int k = a; k <= b; k++)
            {
                if ((Sym(k, "(") || W(k) == "CASE") && _match[k] >= 0)
                {
                    k = _match[k];
                    continue;
                }

                if (k == start) continue;
                bool comma = Sym(k, ",");
                bool join = !comma && IsJoinAt(k) && !(kind == 'J' && IsJoinWordRun(start, k));
                if (comma || join)
                {
                    units.Add((kind, start, k - 1));
                    start = k;
                    kind = comma ? ',' : 'J';
                }
            }

            units.Add((kind, start, b));
            int cont = baseCol + Body;
            foreach (var (uk, s, e) in units)
            {
                bool ok;
                switch (uk)
                {
                    case 'F':
                        _ec = bodyCol;
                        ok = Expr(s, e, Place.Pad(bodyCol), e);
                        break;
                    case ',':
                        if (s + 1 > e) return false;
                        _ec = cont;
                        if (_o.CommaPosition == CommaPosition.Leading)
                        {
                            Put(s, Place.Nl(baseCol + CommaCol));
                            ok = Expr(s + 1, e, Place.Pad(cont), e);
                        }
                        else
                        {
                            Put(s, Place.In);
                            ok = Expr(s + 1, e, Place.Nl(cont), e);
                        }

                        break;
                    default:
                        ok = Join(s, e, baseCol);
                        break;
                }

                if (!ok) return false;
            }

            return true;
        }

        bool IsJoinWordRun(int s, int k)
        {
            for (int j = s; j < k; j++)
            {
                if (W(j) is "JOIN" or "APPLY") return false;
            }

            return true;
        }

        bool Join(int s, int e, int baseCol)
        {
            int j = s;
            while (j <= e && W(j) is not ("JOIN" or "APPLY")) j++;
            if (j > e) return false;
            Put(s, _lb.JoinClause ? Place.Nl(baseCol) : Place.In);
            for (int x = s + 1; x <= j; x++) Put(x, Place.In);
            int tableCol = baseCol + Math.Max(Body, _cur - baseCol + 1);
            int on = -1;
            for (int k = j + 1; k <= e; k++)
            {
                if ((Sym(k, "(") || W(k) == "CASE") && _match[k] >= 0)
                {
                    k = _match[k];
                    continue;
                }

                if (W(k) == "ON")
                {
                    on = k;
                    break;
                }
            }

            int te = on >= 0 ? on - 1 : e;
            if (j + 1 > te) return false;
            _ec = tableCol;
            if (!Expr(j + 1, te, _lb.JoinClause ? Place.Pad(tableCol) : Place.In, te)) return false;
            if (on < 0) return true;
            Put(on, Place.Nl(baseCol));
            if (on + 1 > e) return false;
            _ec = baseCol + Body;
            return Conds(on + 1, e, baseCol, Place.Pad(baseCol + Body), -1);
        }

        /// <summary>조건 목록. <paramref name="inner"/> ≥ 0 이면 괄호 묶음 안(AND·OR 를 안쪽 열 앞에 오른쪽으로 붙인다).</summary>
        bool Conds(int a, int b, int baseCol, Place first, int inner)
        {
            var conds = Logic(a, b);
            if (conds.Exists(c => c.E < c.S)) return false;
            bool group = inner >= 0;
            for (int i = 0; i < conds.Count; i++)
            {
                var (s, e) = conds[i];
                Place p;
                if (i == 0)
                {
                    p = first;
                    _ec = group ? inner : StartCol(s, first);
                }
                else
                {
                    int op = s - 1;
                    int opLen = It(op).OutText.Length;
                    if (group)
                    {
                        Put(op, Place.Nl(Math.Max(0, inner - opLen - 1)));
                        p = Place.In;
                        _ec = inner;
                    }
                    else
                    {
                        _ec = baseCol + Body;
                        switch (_lb.AndOr)
                        {
                            case AndOrBreak.Before:
                                Put(op, Place.Nl(baseCol));
                                p = Place.Pad(baseCol + Math.Max(Body, opLen + 1));
                                break;
                            case AndOrBreak.After:
                                Put(op, Place.In);
                                p = Place.Nl(baseCol + Body);
                                break;
                            default:
                                Put(op, Place.In);
                                p = Place.In;
                                break;
                        }
                    }
                }

                if (!Expr(s, e, p, e)) return false;
            }

            if (group || _lb.AndOr != AndOrBreak.None) _alignSets.Add(conds);
            return true;
        }
    }
}
