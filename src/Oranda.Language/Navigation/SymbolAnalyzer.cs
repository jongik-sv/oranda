using Oranda.Core.Language;
using Oranda.Core.Text;
using static Oranda.Language.Navigation.SymbolModel;

namespace Oranda.Language.Navigation;

/// <summary>
/// 커서 아래 식별자의 판별·문서 안 정의 찾기·참조 찾기(L1 토큰 기반). 한 번 부를 때마다 문서를 렉싱해 모델을 만든다(클릭·F12 빈도라 충분).
/// L2(ANTLR) 분석이 준비되면 <see cref="SymbolModel.Build"/> 만 L2 결과로 채우도록 바꾸면 된다.
/// </summary>
public static class SymbolAnalyzer
{
    /// <summary>offset 의 식별자 사슬(<c>owner.name.member</c>)을 풀어 돌려준다. 식별자가 아니면 null.</summary>
    public static SymbolReference? GetSymbolAt(string text, int offset)
    {
        ArgumentNullException.ThrowIfNull(text);
        var m = Build(text);
        return m.Locate(offset) is { } hit ? Describe(m, hit.Chain, hit.Part) : null;
    }

    /// <summary>
    /// offset 의 이름이 문서 안에서 정의된 것(별칭·CTE·변수·커서·로컬 서브프로그램·매개변수)이면 그 정의를 돌려준다.
    /// 테이블 별칭이라도 <c>e.sal</c> 의 <c>sal</c> 처럼 DB 쪽 이름을 가리키면 null. 정의 자리 자신도 null.
    /// </summary>
    public static LocalDefinition? FindLocalDefinition(string text, int offset)
    {
        ArgumentNullException.ThrowIfNull(text);
        var m = Build(text);
        if (m.Locate(offset) is not { } hit)
        {
            return null;
        }

        var def = m.ResolveLocal(hit.Chain);
        if (def is null || def.Token == hit.Chain.Parts[0])
        {
            return null;
        }

        if (hit.Part > 0 && def.Kind == LocalSymbolKind.TableAlias)
        {
            return null;
        }

        return ToDefinition(m, def);
    }

    /// <summary>offset 의 이름과 같은 것을 가리키는 문서 안 범위 전부(자기 자신 포함, 위치 순).</summary>
    public static IReadOnlyList<TextRange> FindReferences(string text, int offset)
    {
        ArgumentNullException.ThrowIfNull(text);
        var m = Build(text);
        if (m.Locate(offset) is not { } hit)
        {
            return [];
        }

        var chain = hit.Chain;
        var local = m.ResolveLocal(chain);
        var result = new List<TextRange>();
        if (local is not null && (hit.Part == 0 || local.Kind != LocalSymbolKind.TableAlias))
        {
            foreach (var c in m.Chains)
            {
                if (m.ResolveLocal(c) is { } d && ReferenceEquals(d, local))
                {
                    Add(m, result, c.Parts[0]);
                }
            }

            return Sorted(result);
        }

        var kinds = Kinds(chain, hit.Part);
        var name = m.Norm(chain.Parts[hit.Part]);
        foreach (var c in m.Chains)
        {
            var localC = m.ResolveLocal(c);
            for (int p = 0; p < c.Parts.Count; p++)
            {
                if (m.Norm(c.Parts[p]) != name)
                {
                    continue;
                }

                if (p == 0 && localC is not null)
                {
                    continue;
                }

                if ((Kinds(c, p) & kinds) != 0)
                {
                    Add(m, result, c.Parts[p]);
                }
            }
        }

        return Sorted(result);
    }

    /// <summary>
    /// 소스 문서(패키지 본문 등)에서 프로시저·함수 <paramref name="name"/> 의 선언 이름 위치. 선언(<c>;</c> 로 끝남)보다 구현(<c>IS/AS</c>)을 먼저 고른다.
    /// 없으면 null.
    /// </summary>
    public static TextRange? FindSubprogram(string text, string name)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(name);
        var m = Build(text);
        var norm = NormalizeIdent(name);
        var hits = m.Defs.Where(d => d.Kind == LocalSymbolKind.Subprogram && d.Name == norm).ToList();
        if (hits.Count == 0)
        {
            return null;
        }

        var impl = hits.FirstOrDefault(d => IsImplementation(m, d.Token)) ?? hits[0];
        return new TextRange(m.Toks[impl.Token].Start, m.Toks[impl.Token].Length);
    }

    private static bool IsImplementation(SymbolModel m, int nameToken)
    {
        int depth = 0;
        for (int i = nameToken + 1; i < m.Toks.Count; i++)
        {
            if (m.IsPunct(i, '('))
            {
                depth++;
            }
            else if (m.IsPunct(i, ')'))
            {
                depth--;
            }
            else if (depth == 0 && m.IsPunct(i, ';'))
            {
                return false;
            }
            else if (depth == 0 && (m.IsWord(i, "IS") || m.IsWord(i, "AS")))
            {
                return true;
            }
        }

        return false;
    }

    // ---- 내부 ----------------------------------------------------------------------------

    private static void Add(SymbolModel m, List<TextRange> list, int token)
        => list.Add(new TextRange(m.Toks[token].Start, m.Toks[token].Length));

    private static List<TextRange> Sorted(List<TextRange> list)
    {
        list.Sort((a, b) => a.Start.CompareTo(b.Start));
        return list;
    }

    private static LocalDefinition ToDefinition(SymbolModel m, Def d)
    {
        string? owner = null, name = null;
        if (d.Target is { } t)
        {
            if (t.Parts.Count >= 2)
            {
                owner = m.Norm(t.Parts[0]);
                name = m.Norm(t.Parts[1]);
            }
            else
            {
                name = m.Norm(t.Parts[0]);
            }
        }

        return new LocalDefinition(m.Toks[d.Token].Start, m.Toks[d.Token].Length, d.Name, d.Kind) { TargetOwner = owner, TargetName = name };
    }

    // 이름이 어떤 자리인가(교집합이 있으면 같은 것을 가리킬 수 있다): 1 = 소유자, 2 = 객체 이름, 4 = 멤버(열·프로시저).
    private static int Kinds(Chain c, int p)
    {
        int n = c.Parts.Count;
        if (c.IsTableRef)
        {
            return n == 1 ? 2 : p == 0 ? 1 : p == 1 ? 2 : 4;
        }

        if (n == 1)
        {
            return 2 | 4;
        }

        return n == 2 ? (p == 0 ? 2 : 4) : (p == 0 ? 1 : p == 1 ? 2 : 4);
    }

    private static SymbolReference Describe(SymbolModel m, Chain c, int k)
    {
        var range = new TextRange(c.Start, c.End - c.Start);
        string P(int i) => m.Norm(c.Parts[i]);

        // 별칭은 풀어서: e.sal → EMP + 멤버 SAL.
        if (m.ResolveLocal(c) is { Kind: LocalSymbolKind.TableAlias, Target: { } t } alias && alias.Token != c.Parts[0])
        {
            string? owner = t.Parts.Count >= 2 ? m.Norm(t.Parts[0]) : null;
            string name = t.Parts.Count >= 2 ? m.Norm(t.Parts[1]) : m.Norm(t.Parts[0]);
            return new SymbolReference(range, name) { Owner = owner, Member = k >= 1 ? P(1) : null };
        }

        if (c.IsTableRef)
        {
            return k switch
            {
                0 => new SymbolReference(range, P(0)),
                1 => new SymbolReference(range, P(1)) { Owner = P(0) },
                _ => new SymbolReference(range, P(1)) { Owner = P(0), Member = P(2) },
            };
        }

        // 정의 자리의 별칭(emp e 의 e)은 가리키는 테이블로 푼다.
        if (k == 0 && c.Parts.Count == 1 && m.DefAtToken(c.Parts[0]) is { Kind: LocalSymbolKind.TableAlias, Target: { } dt })
        {
            return dt.Parts.Count >= 2
                ? new SymbolReference(range, m.Norm(dt.Parts[1])) { Owner = m.Norm(dt.Parts[0]) }
                : new SymbolReference(range, m.Norm(dt.Parts[0]));
        }

        return k switch
        {
            0 => new SymbolReference(range, P(0)),
            1 when c.Parts.Count == 2 => new SymbolReference(range, P(0)) { Member = P(1) },
            1 => new SymbolReference(range, P(1)) { Owner = P(0) },
            _ => new SymbolReference(range, P(1)) { Owner = P(0), Member = P(2) },
        };
    }
}
