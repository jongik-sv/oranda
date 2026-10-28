using System.Collections.Frozen;
using Oranda.Core.Metadata;
using Oranda.Language.Lexing;

namespace Oranda.Language.Ai.References;

/// <summary>
/// 문장 하나에서 참조 객체(FROM·JOIN·UPDATE·INSERT INTO·MERGE 대상)와 쓰는 열(SELECT·WHERE·JOIN·GROUP·ORDER·HAVING)을 렉서 토큰으로 찾는다.
/// WITH 절·인라인 뷰·스칼라 부분 질의·UNION 가지는 각각 블록으로 나눠 별칭을 따로 푼다. DB 를 모르므로 시노님은 <see cref="ResolveAsync"/> 가 푼다.
/// 한계: 인라인 뷰·WITH 이름을 거친 열은 원래 표로 되짚지 않는다(그 안의 열은 안쪽 블록에서 이미 잡힌다).
/// </summary>
public sealed class StatementReferenceExtractor
{
    /// <summary>FROM 목록에서 별칭으로 보지 않는 낱말.</summary>
    private static readonly FrozenSet<string> AliasStop = new[]
    {
        "WHERE", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "NATURAL", "ON", "USING", "GROUP", "ORDER",
        "HAVING", "CONNECT", "START", "UNION", "INTERSECT", "MINUS", "EXCEPT", "MODEL", "WINDOW", "FETCH", "OFFSET", "FOR",
        "PARTITION", "SUBPARTITION", "SAMPLE", "PIVOT", "UNPIVOT", "AS", "SET", "VALUES", "RETURNING", "WHEN", "THEN",
        "SELECT", "FROM", "INTO", "WITH", "LATERAL", "LIMIT", "AND", "OR", "NOT", "IS", "IN", "VERSIONS",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>열 이름이 아닌 낱말(식 안의 예약어·유사 열·자료형·단위).</summary>
    private static readonly FrozenSet<string> NotColumn = new[]
    {
        "ROWNUM", "ROWID", "LEVEL", "SYSDATE", "SYSTIMESTAMP", "CURRENT_DATE", "CURRENT_TIMESTAMP", "LOCALTIMESTAMP", "USER",
        "NULL", "TRUE", "FALSE", "NEXTVAL", "CURRVAL", "ASC", "DESC", "NULLS", "FIRST", "LAST", "ROW", "ROWS", "ONLY",
        "NUMBER", "DATE", "TIMESTAMP", "INTERVAL", "CHAR", "VARCHAR2", "NVARCHAR2", "DAY", "MONTH", "YEAR", "HOUR", "MINUTE",
        "SECOND", "AND", "OR", "NOT", "IS", "IN", "LIKE", "BETWEEN", "EXISTS", "ANY", "ALL", "SOME", "DISTINCT", "UNIQUE",
        "CASE", "WHEN", "THEN", "ELSE", "END", "OVER", "PARTITION", "BY", "ESCAPE", "ROWS", "RANGE", "UNBOUNDED", "PRECEDING",
        "FOLLOWING", "CURRENT", "PRIOR", "CONNECT_BY_ROOT", "SIBLINGS", "KEEP", "DENSE_RANK", "WITHIN",
        "OFFSET", "NEXT", "SKIP", "LOCKED", "NOWAIT", "WAIT", "NOCYCLE", "NATURAL", "IGNORE", "RESPECT", "LEADING", "TRAILING",
        "BOTH", "PERCENT", "TIES", "OF", "FETCH", "SAMPLE", "BLOCK", "SEED",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>열은 아니지만 식의 끝이 되는 낱말(뒤따르는 식별자는 별칭이다).</summary>
    private static readonly FrozenSet<string> PseudoEnd = new[]
    {
        "ROWNUM", "ROWID", "LEVEL", "SYSDATE", "SYSTIMESTAMP", "CURRENT_DATE", "CURRENT_TIMESTAMP", "LOCALTIMESTAMP", "USER",
        "NULL", "TRUE", "FALSE", "END",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary><paramref name="sql"/> 의 첫 문장(첫 <c>;</c> 앞까지)을 분석한다.</summary>
    public static StatementReferences Extract(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return new Parser(Lex(sql)).Run();
    }

    /// <summary>
    /// 적힌 이름을 실제 소유자.이름으로 푼다(시노님·PUBLIC 시노님 포함). 소유자를 적지 않은 이름은 <paramref name="defaultOwner"/> 스키마에서 시작한다.
    /// DB 링크 객체와 풀 수 없는 시노님은 <see cref="ResolvedReference.Target"/> 이 null 이고 이유가 <see cref="ResolvedReference.Problem"/> 에 있다.
    /// </summary>
    public static async Task<IReadOnlyList<ResolvedReference>> ResolveAsync(
        StatementReferences references, string defaultOwner, Func<string, string, Task<NameStep?>> step)
    {
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(step);
        var result = new List<ResolvedReference>(references.Objects.Count);
        foreach (var o in references.Objects)
        {
            if (o.IsRemote)
            {
                result.Add(new ResolvedReference(o, null, $"{o.Display}@{o.DbLink} 은(는) DB 링크 너머의 객체라 이 DB 에서 조회하지 않습니다."));
                continue;
            }

            try
            {
                var target = await SynonymResolver
                    .ResolveAsync(step, new DbObjectRef(o.Owner ?? defaultOwner, o.Name, "TABLE"), o.Owner is not null)
                    .ConfigureAwait(false);
                result.Add(new ResolvedReference(o, target, null));
            }
            catch (SynonymResolutionException ex)
            {
                result.Add(new ResolvedReference(o, null, ex.Message));
            }
        }

        return result;
    }

    private readonly record struct Tok(string Text, string Upper, TokenKind Kind)
    {
        public bool IsName => Kind is TokenKind.Identifier or TokenKind.QuotedIdent;

        public string Name => Kind == TokenKind.QuotedIdent ? Text.Trim('"') : Upper;
    }

    private static List<Tok> Lex(string sql)
    {
        var list = new List<Tok>();
        foreach (var t in SqlLexer.Tokenize(sql))
        {
            if (t.Kind is TokenKind.Comment or TokenKind.Hint)
            {
                continue;
            }

            var text = sql.Substring(t.Start, t.Length);
            if (t.Kind == TokenKind.Punct && text == ";")
            {
                break;
            }

            list.Add(new Tok(text, text.ToUpperInvariant(), t.Kind));
        }

        return list;
    }

    private sealed class Source(ReferencedObject? obj)
    {
        /// <summary>null 이면 WITH 이름 또는 인라인 뷰.</summary>
        public ReferencedObject? Obj { get; } = obj;
    }

    private sealed record Pending(string[] Qualifier, string Column, ColumnClause Clause);

    private sealed class Block(Block? parent, HashSet<string> ctes, HashSet<string> aliases)
    {
        public Block? Parent { get; } = parent;

        /// <summary>SELECT 목록에서 붙인 별칭(UNION 가지끼리 공유). ORDER BY 에서 이 이름은 열이 아니다.</summary>
        public HashSet<string> Aliases { get; } = aliases;

        /// <summary>같은 질의 수준(UNION 가지끼리)이 공유하는 WITH 이름.</summary>
        public HashSet<string> Ctes { get; } = ctes;

        public Dictionary<string, Source> Sources { get; } = new(StringComparer.Ordinal);

        public List<Pending> Columns { get; } = [];
    }

    private sealed class Parser(List<Tok> t)
    {
        private readonly int _n = t.Count;
        private readonly List<Block> _blocks = [];
        private readonly List<ReferencedObject> _objects = [];
        private readonly List<string> _calls = [];
        private bool _uncertain;
        private bool _remoteLink;
        private bool _forUpdate;
        private int _i;

        public StatementReferences Run()
        {
            ParseQuery(null, false);
            var uses = new Dictionary<string, ColumnUse>(StringComparer.Ordinal);
            foreach (var b in _blocks)
            {
                foreach (var p in b.Columns)
                {
                    Attribute(b, p, uses);
                }
            }

            return new StatementReferences(_objects.Distinct().ToList(), [.. uses.Values]) { Calls = [.. _calls.Distinct()], HasLockingClause = _forUpdate, HasRemoteLink = _remoteLink, Uncertain = _uncertain };
        }

        private static void Attribute(Block b, Pending p, Dictionary<string, ColumnUse> uses)
        {
            ReferencedObject? table = null;
            IReadOnlyList<ReferencedObject> candidates = [];
            if (p.Qualifier.Length > 0)
            {
                var q = string.Join('.', p.Qualifier);
                Source? found = null;
                for (var x = b; x is not null && found is null; x = x.Parent)
                {
                    x.Sources.TryGetValue(q, out found);
                }

                if (found?.Obj is null)
                {
                    return; // WITH 이름·인라인 뷰의 열이거나 알 수 없는 한정자(패키지 상수 등)
                }

                table = found.Obj;
            }
            else
            {
                if (p.Clause == ColumnClause.Order && b.Aliases.Contains(p.Column))
                {
                    return; // ORDER BY 에 쓴 SELECT 별칭
                }

                var own = b.Sources.Values.Select(s => s.Obj).OfType<ReferencedObject>().Distinct().ToList();
                if (own.Count == 0)
                {
                    return;
                }

                // WITH 이름·인라인 뷰가 같이 있으면 한정자 없는 열이 어느 쪽 것인지 모르므로 후보로만 둔다.
                if (own.Count == 1 && b.Sources.Values.All(s => s.Obj is not null))
                {
                    table = own[0];
                }
                else
                {
                    candidates = own;
                }
            }

            var key = (table?.Display ?? string.Join('|', candidates.Select(c => c.Display))) + "#" + table?.DbLink + "#" + p.Column;
            uses[key] = uses.TryGetValue(key, out var prev)
                ? prev with { Clauses = prev.Clauses | p.Clause }
                : new ColumnUse(table, candidates, p.Column, p.Clause);
        }

        private Block NewBlock(Block? parent, HashSet<string> ctes, HashSet<string> aliases)
        {
            var b = new Block(parent, ctes, aliases);
            _blocks.Add(b);
            return b;
        }

        private bool Is(int k, string upper) => k < _n && t[k].Upper == upper;

        private bool IsQueryStart(int k) => k < _n && t[k].Upper is "SELECT" or "WITH";

        private void ParseQuery(Block? parent, bool inParen)
        {
            var ctes = new HashSet<string>(StringComparer.Ordinal);
            var aliases = new HashSet<string>(StringComparer.Ordinal);
            var cur = NewBlock(parent, ctes, aliases);
            var levelStart = _i;
            var clause = ColumnClause.Other;
            var fromList = false;
            var prevEnds = false;
            var depth = 0;
            var dml = string.Empty;
            while (_i < _n)
            {
                var tk = t[_i];
                if (tk.Upper == ")")
                {
                    if (depth > 0)
                    {
                        depth--;
                        _i++;
                        prevEnds = true;
                        continue;
                    }

                    if (inParen)
                    {
                        return;
                    }

                    _i++;
                    continue;
                }

                if (tk.Upper == "(")
                {
                    if (IsQueryStart(_i + 1))
                    {
                        _i++;
                        ParseQuery(cur, true);
                        if (Is(_i, ")"))
                        {
                            _i++;
                        }

                        prevEnds = true;
                        continue;
                    }

                    depth++;
                    _i++;
                    prevEnds = false;
                    continue;
                }

                if (depth == 0 && tk.Kind is TokenKind.Keyword or TokenKind.Identifier or TokenKind.Punct)
                {
                    var handled = true;
                    switch (tk.Upper)
                    {
                        case "WITH" when _i == levelStart:
                            _i++;
                            ParseWith(cur, ctes);
                            break;
                        case "SELECT":
                            if (dml == "INSERT" && cur.Sources.Count > 0)
                            {
                                dml = "INSERT-SELECT";
                                cur = NewBlock(parent, ctes, aliases); // INSERT 대상 블록과 SELECT 블록을 나눈다
                            }

                            clause = ColumnClause.Select;
                            fromList = false;
                            _i++;
                            break;
                        case "FROM":
                            clause = ColumnClause.Other;
                            fromList = true;
                            _i++;
                            ParseTableSource(cur);
                            break;
                        case "JOIN":
                            fromList = true;
                            _i++;
                            ParseTableSource(cur);
                            break;
                        case "," when fromList:
                            _i++;
                            ParseTableSource(cur);
                            break;
                        case "ON":
                            clause = ColumnClause.Join;
                            _i++;
                            break;
                        case "USING" when dml == "MERGE":
                            _i++;
                            ParseTableSource(cur);
                            break;
                        case "USING":
                            clause = ColumnClause.Join;
                            _i++;
                            break;
                        case "WHERE":
                        case "CONNECT" or "START" when Is(_i + 1, "BY") || Is(_i + 1, "WITH"):
                            clause = ColumnClause.Where;
                            fromList = false;
                            _i++;
                            break;
                        case "GROUP" when Is(_i + 1, "BY"):
                            clause = ColumnClause.Group;
                            fromList = false;
                            _i++;
                            break;
                        case "HAVING":
                            clause = ColumnClause.Having;
                            fromList = false;
                            _i++;
                            break;
                        case "ORDER" when Is(_i + 1, "BY") || Is(_i + 1, "SIBLINGS"):
                            clause = ColumnClause.Order;
                            fromList = false;
                            _i++;
                            break;
                        case "SET" or "VALUES":
                            clause = ColumnClause.Other;
                            fromList = false;
                            _i++;
                            break;
                        case "UPDATE" when !(_i > 0 && t[_i - 1].Upper == "FOR"):
                            dml = "UPDATE";
                            _i++;
                            ParseTableSource(cur);
                            break;
                        case "DELETE":
                            dml = "DELETE";
                            _i++;
                            if (_i < _n && t[_i].IsName)
                            {
                                ParseTableSource(cur); // FROM 을 생략한 DELETE 대상
                            }

                            break;
                        case "INSERT" or "MERGE":
                            dml = tk.Upper;
                            _i++;
                            break;
                        case "FOR":
                            _forUpdate |= Is(_i + 1, "UPDATE"); // 주석을 뺀 토큰으로 본다
                            clause = ColumnClause.Other;
                            fromList = false;
                            _i++;
                            break;
                        case "INTO" when dml is not ("INSERT" or "MERGE"):
                            clause = ColumnClause.None; // SELECT … INTO 변수: 변수 이름은 열이 아니다
                            _i++;
                            break;
                        case "INTO" when dml is "INSERT" or "MERGE":
                            clause = ColumnClause.Other;
                            _i++;
                            ParseTableSource(cur, columnListFollows: true);
                            break;
                        case "UNION" or "INTERSECT" or "MINUS" or "EXCEPT":
                            cur = NewBlock(parent, ctes, aliases);
                            clause = ColumnClause.Other;
                            fromList = false;
                            _i++;
                            break;
                        default:
                            handled = false;
                            break;
                    }

                    if (handled)
                    {
                        prevEnds = false;
                        continue;
                    }
                }

                if (tk.IsName)
                {
                    prevEnds = ReadChain(cur, clause, depth, prevEnds);
                    continue;
                }

                prevEnds = tk.Kind is TokenKind.Number or TokenKind.StringLiteral or TokenKind.Bind || PseudoEnd.Contains(tk.Upper);
                _i++;
            }
        }

        private void ParseWith(Block cur, HashSet<string> ctes)
        {
            while (_i < _n)
            {
                if (!t[_i].IsName)
                {
                    return;
                }

                ctes.Add(t[_i].Name);
                _i++;
                if (Is(_i, "("))
                {
                    SkipGroup();
                }

                if (Is(_i, "AS"))
                {
                    _i++;
                }

                if (!Is(_i, "(") || !IsQueryStart(_i + 1))
                {
                    return;
                }

                _i++;
                ParseQuery(cur, true);
                if (Is(_i, ")"))
                {
                    _i++;
                }

                if (!Is(_i, ","))
                {
                    return;
                }

                _i++;
            }
        }

        /// <summary><c>(</c> 에서 시작하는 묶음 안의 함수 호출 이름(<c>a.b(</c>)을 호출 목록에 더한다. 위치는 옮기지 않는다.</summary>
        private void CollectCalls()
        {
            var d = 0;
            for (var k = _i; k < _n; k++)
            {
                if (t[k].Upper == "(")
                {
                    d++;
                }
                else if (t[k].Upper == ")" && --d == 0)
                {
                    return;
                }

                if (!t[k].IsName || (k > 0 && t[k - 1].Upper == "."))
                {
                    continue;
                }

                var parts = new List<string> { t[k].Name };
                var j = k + 1;
                while (j + 1 < _n && t[j].Upper == "." && t[j + 1].IsName)
                {
                    parts.Add(t[j + 1].Name);
                    j += 2;
                }

                if (j < _n && t[j].Upper == "(" && !(parts.Count == 1 && t[k].Kind != TokenKind.QuotedIdent && NotColumn.Contains(parts[0])))
                {
                    _calls.Add(string.Join('.', parts));
                }
            }
        }

        /// <summary><c>(</c> 에서 시작해 짝이 되는 <c>)</c> 다음까지 건너뛴다.</summary>
        private void SkipGroup()
        {
            var d = 0;
            while (_i < _n)
            {
                if (t[_i].Upper == "(")
                {
                    d++;
                }
                else if (t[_i].Upper == ")")
                {
                    d--;
                    if (d == 0)
                    {
                        _i++;
                        return;
                    }
                }

                _i++;
            }
        }

        private void ParseTableSource(Block cur, bool columnListFollows = false)
        {
            if (Is(_i, "LATERAL") && Is(_i + 1, "("))
            {
                _i++;
            }

            if (Is(_i, "("))
            {
                if (IsQueryStart(_i + 1))
                {
                    _i++;
                    ParseQuery(cur, true);
                    if (Is(_i, ")"))
                    {
                        _i++;
                    }

                    var view = ReadAlias();
                    if (view is not null)
                    {
                        cur.Sources[view] = new Source(null);
                    }

                    return;
                }

                _uncertain = true; // 그 밖의 괄호(조인 묶음 등): 안의 객체를 찾지 못하므로 확신하지 않는다
                return;
            }

            if (Is(_i, "TABLE") && Is(_i + 1, "("))
            {
                _i++;
                _uncertain = true;
                CollectCalls();
                SkipGroup();
                var fn = ReadAlias();
                if (fn is not null)
                {
                    cur.Sources[fn] = new Source(null);
                }

                return;
            }

            if (_i >= _n || !t[_i].IsName)
            {
                _uncertain = true; // 이름이 아닌 것(예약어 이름의 표 등)이 FROM 자리에 왔다
                return;
            }

            var parts = new List<string> { t[_i].Name };
            _i++;
            while (_i + 1 < _n && t[_i].Upper == "." && t[_i + 1].IsName)
            {
                parts.Add(t[_i + 1].Name);
                _i += 2;
            }

            string? link = null;
            if (Is(_i, "@"))
            {
                _i++;
                if (_i < _n && t[_i].IsName)
                {
                    var sb = new System.Text.StringBuilder(t[_i].Name);
                    _i++;
                    while (_i + 1 < _n && t[_i].Upper == "." && t[_i + 1].IsName)
                    {
                        sb.Append('.').Append(t[_i + 1].Name);
                        _i += 2;
                    }

                    link = sb.ToString();
                }
            }

            if (Is(_i, "(") && !columnListFollows)
            {
                _calls.Add(string.Join('.', parts));
                return; // FROM 안의 함수 호출(파이프라인 함수 등): 표가 아니다
            }

            var owner = parts.Count >= 2 ? parts[^2] : null;
            var name = parts[^1];
            var alias = ReadAlias();
            if (owner is null && link is null && IsCte(cur, name))
            {
                cur.Sources[alias ?? name] = new Source(null);
                return;
            }

            var obj = new ReferencedObject(owner, name, link);
            _objects.Add(obj);
            if (alias is not null)
            {
                cur.Sources[alias] = new Source(obj);
            }
            else
            {
                cur.Sources[name] = new Source(obj);
                if (owner is not null)
                {
                    cur.Sources[owner + "." + name] = new Source(obj);
                }
            }
        }

        private static bool IsCte(Block b, string name)
        {
            for (var x = b; x is not null; x = x.Parent)
            {
                if (x.Ctes.Contains(name))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>표 이름 뒤의 PARTITION(…)·SAMPLE 을 건너뛰고 별칭을 읽는다.</summary>
        private string? ReadAlias()
        {
            if (_i < _n && t[_i].Upper is "PARTITION" or "SUBPARTITION" && Is(_i + 1, "("))
            {
                _i++;
                SkipGroup();
            }

            if (Is(_i, "SAMPLE"))
            {
                _i++;
                if (Is(_i, "BLOCK"))
                {
                    _i++;
                }

                if (Is(_i, "("))
                {
                    SkipGroup();
                }

                if (Is(_i, "SEED") && Is(_i + 1, "("))
                {
                    _i++;
                    SkipGroup();
                }
            }

            if (Is(_i, "AS") && Is(_i + 1, "OF"))
            {
                _i += 2;
                if (_i < _n && t[_i].Upper is "SCN" or "TIMESTAMP" or "PERIOD")
                {
                    _i++;
                }

                if (Is(_i, "("))
                {
                    _uncertain |= IsQueryStart(_i + 1); // 시점 식 안의 부분 질의가 읽는 객체는 찾지 않는다
                    SkipGroup();
                }
                else if (_i < _n)
                {
                    _i++;
                }
            }

            if (Is(_i, "AS"))
            {
                _i++;
            }

            if (_i < _n && t[_i].IsName && !AliasStop.Contains(t[_i].Upper))
            {
                var alias = t[_i].Name;
                _i++;
                return alias;
            }

            return null;
        }

        /// <summary>
        /// 식별자 사슬(<c>a</c>, <c>t.a</c>, <c>o.t.a</c>)을 읽어 열 사용으로 적는다. 함수 호출·별칭·예약어는 적지 않는다.
        /// </summary>
        /// <returns>다음 낱말이 식 뒤에 붙는 별칭 자리인지(이 사슬이 식의 끝이었는지).</returns>
        private bool ReadChain(Block cur, ColumnClause clause, int depth, bool prevEnds)
        {
            var afterAs = _i > 0 && t[_i - 1].Upper == "AS";
            var parts = new List<string> { t[_i].Name };
            var quoted = t[_i].Kind == TokenKind.QuotedIdent;
            _i++;
            while (_i + 1 < _n && t[_i].Upper == "." && (t[_i + 1].IsName || t[_i + 1].Kind == TokenKind.Keyword || t[_i + 1].Upper == "*"))
            {
                if (t[_i + 1].Upper == "*")
                {
                    _i += 2;
                    return true; // 별칭.* : 열 이름은 모른다
                }

                parts.Add(t[_i + 1].Name);
                _i += 2;
            }

            if (afterAs)
            {
                if (clause == ColumnClause.Select && depth == 0)
                {
                    cur.Aliases.Add(parts[^1]);
                }

                return true; // AS 별칭 또는 CAST(… AS 자료형)
            }

            if (Is(_i, "(") && !(Is(_i + 1, "+") && Is(_i + 2, ")")))
            {
                if (!(parts.Count == 1 && !quoted && NotColumn.Contains(parts[0])))
                {
                    _calls.Add(string.Join('.', parts));
                }

                return false; // 함수 호출: "(" 은 본문 순회가 괄호로 센다
            }

            if (Is(_i, "@"))
            {
                _remoteLink = true; // 원격 함수·열 참조(fn@link)
                _i++;
                while (_i < _n && (t[_i].IsName || t[_i].Upper == "."))
                {
                    _i++;
                }

                return true;
            }

            if (clause == ColumnClause.None)
            {
                return true;
            }

            if (prevEnds && clause == ColumnClause.Select && depth == 0 && parts.Count == 1)
            {
                cur.Aliases.Add(parts[0]);
                return true; // 식 바로 뒤의 식별자 = 암묵적 별칭
            }

            var last = parts[^1];
            if (parts.Count == 1 && !quoted && NotColumn.Contains(last))
            {
                return PseudoEnd.Contains(last);
            }

            if (parts.Count > 1 && last is "NEXTVAL" or "CURRVAL")
            {
                _calls.Add(string.Join('.', parts)); // 시퀀스 값을 올리므로 사용자 함수처럼 다룬다
                return true;
            }

            cur.Columns.Add(new Pending([.. parts[..^1]], last, clause));
            if (Is(_i, "(") && Is(_i + 1, "+") && Is(_i + 2, ")"))
            {
                _i += 3; // 외부 조인 표지 (+)
            }

            return true;
        }
    }
}
