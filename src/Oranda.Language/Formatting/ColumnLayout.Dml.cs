using Oranda.Core.Formatting;
using Oranda.Language.Lexing;

namespace Oranda.Language.Formatting;

// ColumnLayout 의 일부: CREATE AS·INSERT·UPDATE·DELETE(ColumnLayout.cs 에서 나눔, 동작 불변).
internal static partial class FormatEngine
{
    sealed partial class ColumnLayout
    {
        // ---- CREATE … AS 질의·EXPLAIN PLAN … FOR 질의 ----

        /// <summary><c>CREATE [OR REPLACE] VIEW·TABLE … AS 질의</c>, <c>EXPLAIN PLAN … FOR 질의</c>: 머리는 첫 줄에 잇고 질의는 다음 줄 맨 앞 열부터.</summary>
        bool Prefixed(int a, int b)
        {
            string glue = W(a) == "CREATE" ? "AS" : "FOR";
            int q = -1;
            for (int k = a + 1; k <= b; k++)
            {
                if (Sym(k, "(") && _match[k] >= 0)
                {
                    if (IsQueryAt(k + 1)) return false;
                    k = _match[k];
                    continue;
                }

                if (IsQueryAt(k) && W(k - 1) == glue)
                {
                    q = k;
                    break;
                }
            }

            if (q < 0) return false;
            if (W(a) == "CREATE")
            {
                for (int k = a + 1; k < q; k++)
                {
                    if (W(k) is "VIEW" or "TABLE") break;
                    if (k == q - 1) return false;
                }
            }

            _ec = Body;
            return Expr(a, q - 1, Place.Nl(0), q - 1) && Query(q, b, 0);
        }

        // ---- INSERT·UPDATE·DELETE ----

        bool Dml(int a, int b)
        {
            string lead = W(a);
            var heads = new List<(int Pos, int Words)>();
            int query = -1;
            if (lead == "INSERT")
            {
                if (W(a + 1) != "INTO") return false;
                heads.Add((a, 2));
            }
            else
            {
                heads.Add((a, lead == "DELETE" && W(a + 1) == "FROM" ? 2 : 1));
            }

            for (int k = a + heads[0].Words; k <= b; k++)
            {
                if ((Sym(k, "(") || W(k) == "CASE") && _match[k] >= 0)
                {
                    k = _match[k];
                    continue;
                }

                string w = W(k);
                bool head = lead switch
                {
                    "UPDATE" => w is "SET" or "WHERE" or "RETURNING" or "RETURN",
                    "DELETE" => w is "WHERE" or "RETURNING" or "RETURN",
                    _ => w is "VALUES" or "RETURNING" or "RETURN",
                };
                if (lead == "INSERT" && IsQueryAt(k))
                {
                    query = k;
                    break;
                }

                if (head && !Sym(k - 1, ".")) heads.Add((k, 1));
            }

            int end = query >= 0 ? query - 1 : b;
            for (int h = 0; h < heads.Count; h++)
            {
                var (hs, hn) = heads[h];
                int bs = hs + hn;
                int be = h + 1 < heads.Count ? heads[h + 1].Pos - 1 : end;
                Put(hs, Place.Nl(0));
                for (int j = hs + 1; j < bs; j++) Put(j, Place.In);
                int bodyCol = Math.Max(Body, _cur + 1);
                _ec = bodyCol;
                if (bs > be) continue;
                bool ok = W(hs) switch
                {
                    "SET" => List(bs, be, 0, bodyCol, ListMode.Each),
                    "WHERE" => Conds(bs, be, 0, Place.Pad(bodyCol), -1),
                    _ => Expr(bs, be, Place.Pad(bodyCol), be),
                };
                if (!ok) return false;
            }

            return query < 0 || Query(query, b, 0);
        }
    }
}
