using System.Text;
using System.Text.RegularExpressions;

namespace Oranda.Language.Lexing;

/// <summary>소유자 붙이기·바꾸기 결과. <paramref name="Sql"/> 은 고친 글(<paramref name="Changed"/> 가 false 면 원문 그대로).</summary>
/// <param name="Changed">글이 바뀌었는지</param>
/// <param name="Uncertain">문장 모양을 몰라 못 고쳤는지(원문을 돌려줌, 호출한 쪽이 ⚠ 표시)</param>
public readonly record struct OwnerRewriteResult(string Sql, bool Changed, bool Uncertain);

/// <summary>
/// 반영 묶음에 넣거나 의뢰 스크립트로 저장할 때 DDL 의 소유자를 다루는 렉서 기반 도우미(설계 9.3·9.4, TE-22·TE-31).
/// Core 는 렉서를 쓸 수 없어 Language 에 둔다. <c>CreateTableDdl.Reowned</c> 는 건드리지 않는다.
/// 문자열 리터럴·주석·힌트는 토큰 하나라 어느 쪽도 그 안을 고치지 않는다. 소유: te-m1b.
/// </summary>
public static class OwnerRewriter
{
    static readonly Regex PlainIdent = new(@"^[A-Z][A-Z0-9_$#]*$", RegexOptions.CultureInvariant);
    static readonly Regex LooseIdent = new(@"^[A-Za-z][A-Za-z0-9_$#]*$", RegexOptions.CultureInvariant);

    // 소유자 개념이 없는 대상(이름 앞에 소유자를 붙이지 않는다).
    static readonly HashSet<string> NoOwnerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "DATABASE", "USER", "ROLE", "TABLESPACE", "DIRECTORY", "PROFILE", "CONTEXT", "SESSION", "SYSTEM", "ROLLBACK", "PLUGGABLE", "RESOURCE", "AUDIT",
    };

    static readonly HashSet<string> OwnedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "TABLE", "INDEX", "VIEW", "SEQUENCE", "SYNONYM", "TRIGGER", "PROCEDURE", "FUNCTION", "PACKAGE", "TYPE", "CLUSTER", "LIBRARY", "OPERATOR", "INDEXTYPE",
        "DOMAIN", "MATERIALIZED",
    };

    static readonly HashSet<string> CreateModifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "OR", "REPLACE", "FORCE", "NO", "EDITIONABLE", "NONEDITIONABLE", "EDITIONING", "UNIQUE", "BITMAP", "MULTIVALUE", "GLOBAL", "PRIVATE", "TEMPORARY",
        "IMMUTABLE", "BLOCKCHAIN", "SHARDED", "DUPLICATED", "PUBLIC", "NONSCHEMA",
    };

    static readonly HashSet<string> UncertainVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "FLASHBACK", "LOCK", "AUDIT", "NOAUDIT", "ASSOCIATE", "DISASSOCIATE", "PURGE", "RENAME",
    };

    readonly record struct Tok(int Start, int End, TokenKind Kind, string Text);

    /// <summary>
    /// DDL 대상 이름(ALTER/CREATE/DROP/COMMENT ON/TRUNCATE/GRANT·REVOKE … ON 의 대상, <c>REFERENCES t</c>, 인덱스·트리거 <c>ON t</c>)이
    /// 소유자 없이 쓰였으면 <c>currentSchema.</c> 를 붙인다. 이미 소유자가 있는 이름·DDL 이 아닌 문장은 그대로(<c>Uncertain=false</c>).
    /// 문장 모양을 모르면 원문 그대로 <c>Changed=false, Uncertain=true</c>.
    /// </summary>
    public static OwnerRewriteResult Qualify(string sql, string currentSchema)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentSchema);

        var sig = Significant(sql);
        var edits = new List<int>();
        var uncertain = false;
        Scan(sig, edits, ref uncertain);
        if (uncertain)
        {
            return new(sql, false, true);
        }

        if (edits.Count == 0)
        {
            return new(sql, false, false);
        }

        var prefix = Schema(currentSchema.Trim()) + ".";
        var sb = new StringBuilder(sql);
        foreach (var at in edits.Distinct().OrderByDescending(x => x))
        {
            sb.Insert(at, prefix);
        }

        return new(sb.ToString(), true, false);
    }

    /// <summary>
    /// <c>OWNER.</c> 뒤에 이름이 오는 꼴(따옴표 이름 포함, 대소문자 무시)의 소유자만 <paramref name="ownerMap"/> 대로 바꾼다.
    /// 문자열 리터럴·주석·힌트 안, <c>@링크</c> 뒤, 앞에 점이 붙은 이름(<c>A.SAMPLE.B</c> 의 가운데)은 그대로.
    /// </summary>
    public static OwnerRewriteResult Rewrite(string sql, IReadOnlyDictionary<string, string> ownerMap)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(ownerMap);
        if (ownerMap.Count == 0)
        {
            return new(sql, false, false);
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in ownerMap)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                map[Unquote(key.Trim())] = value.Trim();
            }
        }

        var sig = Significant(sql);
        var edits = new List<(int Start, int End, string Text)>();
        for (var i = 0; i + 2 < sig.Count; i++)
        {
            if (!IsWord(sig[i]) || !IsDot(sig[i + 1]) || !IsWord(sig[i + 2]))
            {
                continue;
            }

            if (i > 0 && (IsDot(sig[i - 1]) || sig[i - 1].Text == "@"))
            {
                continue;
            }

            if (!map.TryGetValue(Unquote(sig[i].Text), out var to))
            {
                continue;
            }

            var text = to.StartsWith('"') ? to : Name(to, sig[i].Kind == TokenKind.QuotedIdent);
            if (text != sig[i].Text)
            {
                edits.Add((sig[i].Start, sig[i].End, text));
            }
        }

        if (edits.Count == 0)
        {
            return new(sql, false, false);
        }

        var sb = new StringBuilder(sql);
        foreach (var (start, end, text) in edits.OrderByDescending(e => e.Start))
        {
            sb.Remove(start, end - start).Insert(start, text);
        }

        return new(sb.ToString(), true, false);
    }

    // ---- 문장 훑기 ----

    static void Scan(List<Tok> t, List<int> edits, ref bool uncertain)
    {
        if (t.Count == 0 || !IsWord(t[0]))
        {
            return;
        }

        var verb = Upper(t[0]);
        switch (verb)
        {
            case "ALTER" or "ANALYZE" or "CREATE" or "DROP":
                ScanObjectDdl(t, verb, edits, ref uncertain);
                break;
            case "COMMENT":
                ScanComment(t, edits, ref uncertain);
                break;
            case "TRUNCATE":
                {
                    var i = 1;
                    if (Is(t, i, "TABLE") || Is(t, i, "CLUSTER"))
                    {
                        Target(t, i + 1, edits, ref uncertain);
                    }
                    else
                    {
                        uncertain = true;
                    }

                    break;
                }
            case "GRANT" or "REVOKE":
                ScanGrant(t, edits, ref uncertain);
                break;
            default:
                if (UncertainVerbs.Contains(verb))
                {
                    uncertain = true;
                }

                break;
        }
    }

    static void ScanObjectDdl(List<Tok> t, string verb, List<int> edits, ref bool uncertain)
    {
        var i = 1;
        var isPublic = false;
        if (verb == "CREATE")
        {
            while (i < t.Count && IsWord(t[i]) && CreateModifiers.Contains(Upper(t[i])))
            {
                isPublic |= Is(t, i, "PUBLIC");
                i++;
            }
        }
        else if (Is(t, i, "PUBLIC"))
        {
            isPublic = true;
            i++;
        }

        if (i >= t.Count || !IsWord(t[i]))
        {
            uncertain = true;
            return;
        }

        var type = Upper(t[i]);
        if (NoOwnerTypes.Contains(type))
        {
            return; // 소유자가 없는 대상
        }

        if (!OwnedTypes.Contains(type))
        {
            uncertain = true;
            return;
        }

        i++;
        var isMview = type == "MATERIALIZED";
        if (isMview)
        {
            if (!Is(t, i, "VIEW"))
            {
                uncertain = true;
                return;
            }

            i++;
            if (Is(t, i, "LOG")) // MATERIALIZED VIEW LOG ON <테이블>
            {
                i++;
                if (Is(t, i, "ON"))
                {
                    Target(t, i + 1, edits, ref uncertain);
                }
                else
                {
                    uncertain = true;
                }

                ScanReferences(t, edits, ref uncertain);
                return;
            }
        }

        if ((type is "PACKAGE" or "TYPE") && Is(t, i, "BODY"))
        {
            i++;
        }

        if (Is(t, i, "IF"))
        {
            i++;
            if (Is(t, i, "NOT"))
            {
                i++;
            }

            if (Is(t, i, "EXISTS"))
            {
                i++;
            }
        }

        if (!(type == "SYNONYM" && isPublic))
        {
            var end = Target(t, i, edits, ref uncertain);
            if (verb == "CREATE" && !uncertain && end > 0)
            {
                if (type == "INDEX")
                {
                    OnTarget(t, end, edits, ref uncertain, required: true);
                }
                else if (type == "TRIGGER")
                {
                    OnTarget(t, end, edits, ref uncertain, required: false);
                }
            }
        }

        ScanReferences(t, edits, ref uncertain);
    }

    static void ScanReferences(List<Tok> t, List<int> edits, ref bool uncertain)
    {
        for (var i = 0; i + 1 < t.Count; i++)
        {
            if (Is(t, i, "REFERENCES") && IsWord(t[i + 1]))
            {
                Target(t, i + 1, edits, ref uncertain);
            }
        }
    }

    static void ScanComment(List<Tok> t, List<int> edits, ref bool uncertain)
    {
        if (!Is(t, 1, "ON") || t.Count < 4)
        {
            uncertain = true;
            return;
        }

        var kind = Upper(t[2]);
        var i = 3;
        switch (kind)
        {
            case "COLUMN":
                {
                    // [owner.]table.column — 점이 둘이면 소유자가 이미 있다. 하나면 table.column.
                    var (parts, first, _) = ReadName(t, i);
                    if (parts == 2)
                    {
                        edits.Add(t[first].Start);
                    }
                    else if (parts != 3)
                    {
                        uncertain = true;
                    }

                    break;
                }
            case "TABLE" or "INDEXTYPE" or "OPERATOR" or "DOMAIN":
                Target(t, i, edits, ref uncertain);
                break;
            case "MATERIALIZED" when Is(t, 3, "VIEW"):
                Target(t, 4, edits, ref uncertain);
                break;
            default:
                uncertain = true;
                break;
        }
    }

    static void ScanGrant(List<Tok> t, List<int> edits, ref bool uncertain)
    {
        var depth = 0;
        for (var i = 1; i < t.Count; i++)
        {
            if (IsPunct(t[i], "(")) depth++;
            else if (IsPunct(t[i], ")")) depth--;
            else if (depth == 0 && Is(t, i, "ON"))
            {
                var next = i + 1;
                if (Is(t, next, "DIRECTORY"))
                {
                    return; // 디렉터리는 소유자가 없다
                }

                if (Is(t, next, "JAVA") || Is(t, next, "EDITION") || Is(t, next, "USER") || Is(t, next, "MINING") || Is(t, next, "SQL") || Is(t, next, "DOMAIN"))
                {
                    uncertain = true;
                    return;
                }

                Target(t, next, edits, ref uncertain);
                return;
            }
        }
        // ON 이 없으면 역할·시스템 권한 부여 — 고칠 대상이 없다.
    }

    /// <summary>토큰 <paramref name="from"/> 이후 처음 나오는 괄호 밖 <c>ON</c> 다음 이름을 처리한다.</summary>
    static void OnTarget(List<Tok> t, int from, List<int> edits, ref bool uncertain, bool required)
    {
        var depth = 0;
        for (var i = from; i < t.Count; i++)
        {
            if (IsPunct(t[i], "(")) depth++;
            else if (IsPunct(t[i], ")")) depth--;
            else if (depth == 0 && Is(t, i, "ON"))
            {
                if (Is(t, i + 1, "DATABASE") || Is(t, i + 1, "SCHEMA") || Is(t, i + 1, "NESTED") || Is(t, i + 1, "PLUGGABLE"))
                {
                    return;
                }

                Target(t, i + 1, edits, ref uncertain);
                return;
            }
        }

        if (required)
        {
            uncertain = true;
        }
    }

    /// <summary>이름 하나를 읽어 점이 없으면(소유자 없음) 삽입 위치를 기록한다. 이름 뒤 다음 토큰 위치를 돌려주고, 못 읽으면 uncertain.</summary>
    static int Target(List<Tok> t, int i, List<int> edits, ref bool uncertain)
    {
        var (parts, first, end) = ReadName(t, i);
        if (parts == 0)
        {
            uncertain = true;
            return -1;
        }

        if (parts == 1)
        {
            edits.Add(t[first].Start);
        }

        return end;
    }

    static (int Parts, int First, int End) ReadName(List<Tok> t, int i)
    {
        if (i >= t.Count || !IsWord(t[i]))
        {
            return (0, i, i);
        }

        var parts = 1;
        var j = i + 1;
        while (j + 1 < t.Count && IsDot(t[j]) && IsWord(t[j + 1]))
        {
            parts++;
            j += 2;
        }

        return (parts, i, j);
    }

    // ---- 토큰 도우미 ----

    static List<Tok> Significant(string sql)
    {
        var list = new List<Tok>();
        foreach (var tk in SqlLexer.Tokenize(sql))
        {
            if (tk.Kind is TokenKind.Comment or TokenKind.Hint)
            {
                continue;
            }

            list.Add(new Tok(tk.Start, tk.End, tk.Kind, sql.Substring(tk.Start, tk.Length)));
        }

        return list;
    }

    static bool IsWord(Tok t) => t.Kind is TokenKind.Identifier or TokenKind.Keyword or TokenKind.QuotedIdent;

    static bool IsDot(Tok t) => t.Kind == TokenKind.Punct && t.Text == ".";

    static bool IsPunct(Tok t, string text) => t.Kind == TokenKind.Punct && t.Text == text;

    static string Upper(Tok t) => t.Text.ToUpperInvariant();

    static bool Is(List<Tok> t, int i, string word) =>
        i < t.Count && t[i].Kind is TokenKind.Identifier or TokenKind.Keyword && string.Equals(t[i].Text, word, StringComparison.OrdinalIgnoreCase);

    static string Unquote(string name) =>
        name.Length >= 2 && name[0] == '"' && name[^1] == '"' ? name[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : name;

    /// <summary>바꿀 이름 글: 원문이 따옴표 이름이면 따옴표를 유지, 아니면 식별자로 쓸 수 있을 때만 그대로 쓰고 아니면 따옴표를 붙인다.</summary>
    static string Name(string name, bool quoted) =>
        !quoted && LooseIdent.IsMatch(name) ? name : Quote(name);

    /// <summary>현재 스키마 이름 글: 대문자 규칙을 벗어날 때만 따옴표(SqlIdentifier.Quote 와 같은 규칙).</summary>
    static string Schema(string name) => PlainIdent.IsMatch(name) ? name : Quote(name);

    static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
