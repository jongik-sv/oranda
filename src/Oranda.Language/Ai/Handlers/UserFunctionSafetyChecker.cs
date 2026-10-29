using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Composition;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Sessions;

namespace Oranda.Language.Ai.Handlers;

/// <summary>
/// <see cref="IUserFunctionSafetyChecker"/> 구현. 딕셔너리 뷰(ALL_OBJECTS·ALL_DEPENDENCIES·ALL_SOURCE)를 <see cref="IExecutionService"/>(에이전트 출처, SELECT 만)로 읽는다.
/// 이름은 식별자 문자만 받아 문장에 직접 넣는다(바인드를 쓰지 않으므로 그 밖의 글자가 든 이름은 확인하지 못한 것으로 본다).
/// </summary>
[OrandaService(typeof(IUserFunctionSafetyChecker))]
internal sealed class UserFunctionSafetyChecker(IExecutionService execution) : IUserFunctionSafetyChecker
{
    public async ValueTask<UserFunctionVerdict> CheckAsync(ISession db, IReadOnlyList<string> functions, string? defaultSchema, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(functions);
        var schema = (string.IsNullOrWhiteSpace(defaultSchema) ? db.Profile.User : defaultSchema).ToUpperInvariant();
        var walker = new UserFunctionGraphWalker((sql, token) => QueryAsync(db, sql, token));
        return await walker.CheckAsync(functions, schema, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<object?[]>> QueryAsync(ISession db, string sql, CancellationToken ct)
    {
        var stmt = new SqlStatement(sql, default, StatementKind.Query) { Verb = "SELECT" };
        var options = new ExecutionOptions { Source = HistorySource.Agent, SkipHistory = true };
        var result = await execution.ExecuteAsync(db, stmt, new Dictionary<string, BindValue>(), options, ct).ConfigureAwait(false);
        if (result.Status != ExecutionStatus.Succeeded || result.ResultSet is null)
        {
            if (result.ResultSet is not null)
            {
                await result.ResultSet.DisposeAsync().ConfigureAwait(false);
            }

            throw new InvalidOperationException("The dictionary query failed.");
        }

        await using var rs = result.ResultSet;
        var rows = new List<object?[]>();
        for (var page = 0; ; page++)
        {
            var p = await rs.GetPageAsync(page, ct).ConfigureAwait(false);
            for (var i = 0; i < p.RowCount; i++)
            {
                var row = new object?[p.Columns.Count];
                for (var c = 0; c < row.Length; c++)
                {
                    row[c] = p.Columns[c][i];
                }

                rows.Add(row);
            }

            if (p.IsLast || p.RowCount == 0)
            {
                break;
            }
        }

        return rows;
    }
}

/// <summary>
/// 호출 이름 → PL/SQL 객체 → 의존 객체(깊이·개수 상한)를 걸으며 자치 트랜잭션·외부 패키지 의존을 찾는다. 질의는 <c>query</c> 로 받아 시험에서 가짜로 바꾼다.
/// 닫힌 쪽 판정: 객체를 못 찾거나 소스를 못 읽거나(래핑 포함) 상한을 넘거나 질의가 실패하면 <see cref="UserFunctionVerdict.Unverified"/>.
/// 오라클 제공 객체(시스템 스키마·PUBLIC)는 의존으로 닿았을 때 소스를 읽지 않고 따라가지도 않지만, 외부 패키지 이름이면 의존으로 센다.
/// 단 호출이 직접 가리킨 객체와 현재 스키마의 객체는 소유자가 SYSTEM 같은 시스템 스키마여도 늘 읽는다(사용자가 그 스키마에 만든 함수를 건너뛰지 않게).
/// 동적 SQL(<c>EXECUTE IMMEDIATE</c>)로 숨은 호출은 의존 정보에 없어 잡지 못한다(남는 위험).
/// </summary>
internal sealed partial class UserFunctionGraphWalker(Func<string, CancellationToken, Task<IReadOnlyList<object?[]>>> query)
{
    internal const int MaxDepth = 5;
    internal const int MaxObjects = 200;
    private const int ChunkSize = 40;

    private static readonly FrozenSet<string> ExternalPackages = (
        "UTL_HTTP UTL_TCP UTL_SMTP UTL_FILE UTL_MAIL DBMS_PIPE DBMS_SCHEDULER DBMS_JOB DBMS_AQ")
        .Split(' ').ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> OracleOwners = (
        "SYS SYSTEM XDB CTXSYS MDSYS ORDSYS ORDDATA OUTLN DBSNMP WMSYS LBACSYS DVSYS OJVMSYS APPQOSSYS GSMADMIN_INTERNAL PUBLIC")
        .Split(' ').ToFrozenSet(StringComparer.Ordinal);

    /// <summary>소스가 있어 자치 트랜잭션을 확인할 수 있는 객체 종류. 소스가 없어도 되는 종류(뷰·타입 명세)는 따라가기만 한다.</summary>
    private static readonly FrozenSet<string> TraversableTypes = "FUNCTION PROCEDURE PACKAGE TYPE VIEW TRIGGER"
        .Split(' ').ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> SourceRequiredTypes = "FUNCTION PROCEDURE PACKAGE TRIGGER"
        .Split(' ').ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// 오라클 제공 객체 가운데 읽기 전용 실행에 써도 되는 것(허용 목록). 그 밖의 오라클 제공 패키지·타입·함수(UTL_INADDR·HTTPURITYPE·DBMS_LDAP·DBMS_SQL 등)는
    /// 하나씩 막는 대신 확인하지 못한 것으로 본다. 오라클 제공 표·뷰(DUAL·ALL_*·V$)는 읽기만 하므로 허용한다.
    /// </summary>
    private static readonly FrozenSet<string> SafeOracleObjects = (
        "STANDARD DBMS_STANDARD PLITBLM SYS_STUB_FOR_PURITY_ANALYSIS DBMS_OUTPUT DBMS_ASSERT DBMS_LOB UTL_RAW UTL_MATCH DBMS_RANDOM")
        .Split(' ').ToFrozenSet(StringComparer.Ordinal);

    /// <summary>사용자 스키마의 의존 가운데 따라가지 않아도 되는 데이터 객체. 이 밖의 종류(JAVA CLASS·LIBRARY·MLE MODULE·SEQUENCE·OPERATOR 등)는 확인하지 못한 것으로 본다.</summary>
    private static readonly FrozenSet<string> DataTypes = new[] { "TABLE", "MATERIALIZED VIEW", "NON-EXISTENT" }.ToFrozenSet(StringComparer.Ordinal);

    private const int MaxSynonymHops = 3;

    /// <summary>
    /// 소스 줄에서 확인하지 못한 것으로 볼 낱말: 외부 언어 호출 명세(<c>LANGUAGE JAVA|C|JAVASCRIPT</c>, 옛 <c>AS EXTERNAL</c>, MLE),
    /// 파스 때 PL/SQL 을 실행하는 SQL 매크로·다형 표 함수. 주석 안의 낱말도 잡지만 닫힌 쪽이므로 받아들인다.
    /// </summary>
    [GeneratedRegex(@"\b(LANGUAGE|EXTERNAL|MLE|SQL_MACRO|POLYMORPHIC)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex UnverifiableWord();

    private enum Dependency
    {
        Ignore,
        Traverse,
        Synonym,
        External,
        Unverified,
    }

    /// <summary>의존 객체 하나의 판정. 아는 것만 허용하고 모르는 종류·오라클 제공 객체는 확인하지 못한 것으로 본다(닫힌 쪽).</summary>
    private static Dependency Classify(string owner, string name, string type, string link, HashSet<string> ownSchemas)
    {
        if (link.Length > 0 || ExternalPackages.Contains(name))
        {
            return Dependency.External; // 함수 안의 DB 링크(t@remote, pkg.f@remote)·네트워크/파일/큐/작업 패키지
        }

        if (type == "SYNONYM")
        {
            return Dependency.Synonym;
        }

        if (OracleOwners.Contains(owner) && !ownSchemas.Contains(owner))
        {
            return type is "TABLE" or "VIEW" || SafeOracleObjects.Contains(name) ? Dependency.Ignore : Dependency.Unverified;
        }

        return TraversableTypes.Contains(type) ? Dependency.Traverse
            : DataTypes.Contains(type) ? Dependency.Ignore
            : Dependency.Unverified;
    }

    /// <summary>동의어마다 가리키는 객체(소유자·이름·종류·링크)를 하나씩 돌려준다. 가리키는 객체를 볼 수 없으면 그 동의어는 빠진다.</summary>
    private async Task<List<(string Owner, string Name, string Type, string Link)>> ResolveSynonymsAsync(List<(string Owner, string Name)> synonyms, CancellationToken ct)
    {
        var targets = new List<(string Owner, string Name, string Link)>();
        foreach (var chunk in synonyms.Distinct().Chunk(ChunkSize))
        {
            var rows = await query(SynonymsSql(chunk), ct).ConfigureAwait(false);
            targets.AddRange(rows.Select(r => (Str(r[2]), Str(r[3]), Str(r[4]))));
        }

        var resolved = new List<(string Owner, string Name, string Type, string Link)>();
        resolved.AddRange(targets.Where(t => t.Link.Length > 0).Select(t => (t.Owner, t.Name, string.Empty, t.Link)));
        var local = targets.Where(t => t.Link.Length == 0 && Identifier().IsMatch(t.Owner) && Identifier().IsMatch(t.Name)).ToList();
        if (local.Count < targets.Count(t => t.Link.Length == 0))
        {
            return resolved; // 이름에 식별자 밖 글자가 있는 대상은 확인하지 못한다(빠진 수로 드러난다)
        }

        var types = new Dictionary<(string, string), string>();
        foreach (var chunk in local.Select(t => (t.Owner, t.Name)).Distinct().Chunk(ChunkSize))
        {
            foreach (var row in await query(TargetsSql(chunk), ct).ConfigureAwait(false))
            {
                types[(Str(row[0]), Str(row[1]))] = Str(row[2]);
            }
        }

        resolved.AddRange(local.Where(t => types.ContainsKey((t.Owner, t.Name))).Select(t => (t.Owner, t.Name, types[(t.Owner, t.Name)], string.Empty)));
        return resolved;
    }

    [GeneratedRegex(@"^[A-Z][A-Z0-9_$#]*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Identifier();

    /// <summary>래핑된 소스의 첫 줄(<c>FUNCTION f wrapped</c>). ALL_SOURCE 의 1행 글에는 암호화된 줄들이 이어 붙어 있으므로 줄 끝마다 본다.</summary>
    [GeneratedRegex(@"\swrapped[ \t\r]*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline, matchTimeoutMilliseconds: 100)]
    private static partial Regex Wrapped();

    public async ValueTask<UserFunctionVerdict> CheckAsync(IReadOnlyList<string> functions, string schema, CancellationToken ct)
    {
        try
        {
            return await WalkAsync(functions, schema, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UserFunctionVerdict.Unverified; // 질의 실패·예상 밖 모양: 닫힌 쪽
        }
    }

    private async Task<UserFunctionVerdict> WalkAsync(IReadOnlyList<string> functions, string schema, CancellationToken ct)
    {
        if (!Identifier().IsMatch(schema) || functions.Count == 0)
        {
            return UserFunctionVerdict.Unverified;
        }

        var unverified = false;
        var autonomous = false;
        var external = false;

        // 1) 호출 이름이 가리키는 객체를 찾는다. 하나라도 못 찾으면 확인하지 못한 것이다.
        var candidates = new List<(int Call, string Owner, string Name)>();
        for (var i = 0; i < functions.Count; i++)
        {
            var parts = functions[i].Split('.');
            if (parts.Length is < 1 or > 3 || parts.Any(p => !Identifier().IsMatch(p)))
            {
                return UserFunctionVerdict.Unverified;
            }

            switch (parts.Length)
            {
                case 1:
                    candidates.Add((i, schema, parts[0]));
                    break;
                case 2:
                    candidates.Add((i, schema, parts[0])); // PKG.F
                    candidates.Add((i, parts[0], parts[1])); // SCHEMA.F
                    break;
                default:
                    candidates.Add((i, parts[0], parts[1])); // SCHEMA.PKG.F
                    break;
            }
        }

        var found = new Dictionary<(string Owner, string Name), string>();
        foreach (var chunk in candidates.Chunk(ChunkSize))
        {
            var rows = await query(ObjectsSql(chunk.Select(c => (c.Owner, c.Name))), ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                found[(Str(row[0]), Str(row[1]))] = Str(row[2]);
            }
        }

        var frontier = new List<(string Owner, string Name)>();
        var maybeBuiltIns = new List<string>();
        for (var i = 0; i < functions.Count; i++)
        {
            var hits = candidates.Where(c => c.Call == i && found.ContainsKey((c.Owner, c.Name))).ToList();
            if (hits.Count == 0)
            {
                // ROLLUP·CUBE·버전에 따라 없는 JSON_* 같은 이름은 같은 이름의 사용자 함수가 없으면 구문·내장으로 풀린다.
                if (AgentStatementReferenceExtractor.MaybeUserFunctions.Contains(functions[i]))
                {
                    maybeBuiltIns.Add(functions[i]);
                }
                else
                {
                    unverified = true;
                }

                continue;
            }

            frontier.AddRange(hits.Select(h => (h.Owner, h.Name)));
        }

        // 그 이름이 동의어(현재 스키마·PUBLIC)면 함수를 가리킬 수 있으므로 구문·내장으로 보지 않는다.
        foreach (var chunk in maybeBuiltIns.Distinct().SelectMany(n => new[] { (schema, n), ("PUBLIC", n) }).Chunk(ChunkSize))
        {
            unverified |= (await query(SynonymsSql(chunk), ct).ConfigureAwait(false)).Count > 0;
        }

        // 2) 의존을 걸으며 소스를 읽는다(깊이·개수 상한).
        var visited = new Dictionary<(string Owner, string Name), string>();
        foreach (var start in frontier.Distinct())
        {
            visited[start] = found[start];
        }

        var ownSchemas = visited.Keys.Select(k => k.Owner).Append(schema).ToHashSet(StringComparer.Ordinal);
        var current = visited.Keys.ToList();
        var depth = 0;
        while (current.Count > 0)
        {
            var scan = await ScanSourcesAsync(current, visited, ct).ConfigureAwait(false);
            autonomous |= scan.Autonomous;
            unverified |= scan.Unverified;
            var next = new List<(string Owner, string Name)>();
            List<(string Owner, string Name, string Type, string Link)> deps = [];
            foreach (var chunk in current.Chunk(ChunkSize))
            {
                var rows = await query(DependenciesSql(chunk), ct).ConfigureAwait(false);
                deps.AddRange(rows.Select(r => (Str(r[0]), Str(r[1]), Str(r[2]), r.Length > 3 ? Str(r[3]) : string.Empty)));
            }

            // 동의어는 가리키는 객체로 풀어 같은 규칙으로 다시 판정한다(사슬은 상한까지, 넘으면 확인 못 함).
            for (var hop = 0; deps.Count > 0; hop++)
            {
                List<(string Owner, string Name)> synonyms = [];
                foreach (var (owner, name, type, link) in deps)
                {
                    switch (Classify(owner, name, type, link, ownSchemas))
                    {
                        case Dependency.External:
                            external = true;
                            break;
                        case Dependency.Unverified:
                            unverified = true;
                            break;
                        case Dependency.Synonym:
                            synonyms.Add((owner, name));
                            break;
                        case Dependency.Traverse when !visited.ContainsKey((owner, name)):
                            visited[(owner, name)] = type;
                            next.Add((owner, name));
                            break;
                    }
                }

                deps = [];
                if (synonyms.Count == 0)
                {
                    break;
                }

                if (hop >= MaxSynonymHops)
                {
                    unverified = true;
                    break;
                }

                deps = await ResolveSynonymsAsync(synonyms, ct).ConfigureAwait(false);
                if (deps.Count < synonyms.Count)
                {
                    unverified = true; // 가리키는 객체를 찾지 못한 동의어가 있다
                }
            }

            if (visited.Count > MaxObjects)
            {
                return autonomous ? UserFunctionVerdict.Autonomous : external ? UserFunctionVerdict.External : UserFunctionVerdict.Unverified;
            }

            if (next.Count > 0 && depth >= MaxDepth)
            {
                unverified = true; // 더 따라갈 객체가 남았는데 깊이 상한에 닿았다
                break;
            }

            current = next;
            depth++;
        }

        // 시작 객체의 첫 의존 조회에서 외부 패키지를 이미 셌더라도, 오라클 제공 객체가 시작점이면(예: 직접 호출) 이름을 본다.
        external |= frontier.Any(f => ExternalPackages.Contains(f.Name));

        return autonomous ? UserFunctionVerdict.Autonomous
            : external ? UserFunctionVerdict.External
            : unverified ? UserFunctionVerdict.Unverified
            : UserFunctionVerdict.Safe;
    }

    private async Task<(bool Autonomous, bool Unverified)> ScanSourcesAsync(
        List<(string Owner, string Name)> objects, Dictionary<(string Owner, string Name), string> kinds, CancellationToken ct)
    {
        var autonomous = false;
        var unverified = false;
        foreach (var chunk in objects.Chunk(ChunkSize))
        {
            var rows = await query(SourceSql(chunk), ct).ConfigureAwait(false);
            var firstLines = new HashSet<(string, string)>();
            foreach (var row in rows)
            {
                var key = (Str(row[0]), Str(row[1]));
                var line = Convert.ToInt64(row[3], System.Globalization.CultureInfo.InvariantCulture);
                var text = Str(row[4]);
                if (line == 1)
                {
                    firstLines.Add(key);
                    if (Wrapped().IsMatch(text))
                    {
                        unverified = true; // 래핑된 소스는 읽지 못한다
                    }
                }

                if (text.Contains("AUTONOMOUS_TRANSACTION", StringComparison.OrdinalIgnoreCase))
                {
                    autonomous = true;
                }

                if (UnverifiableWord().IsMatch(text))
                {
                    unverified = true; // 외부 언어 호출 명세·SQL 매크로·다형 표 함수
                }
            }

            foreach (var o in chunk)
            {
                if (SourceRequiredTypes.Contains(kinds[o]) && !firstLines.Contains(o))
                {
                    unverified = true; // 소스를 읽지 못했다(권한·없음)
                }
            }
        }

        return (autonomous, unverified);
    }

    private static string Str(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Pairs(IEnumerable<(string Owner, string Name)> pairs, string ownerColumn, string nameColumn)
    {
        var sb = new StringBuilder();
        foreach (var (owner, name) in pairs)
        {
            if (sb.Length > 0)
            {
                sb.Append(" OR ");
            }

            sb.Append('(').Append(ownerColumn).Append(" = '").Append(owner).Append("' AND ").Append(nameColumn).Append(" = '").Append(name).Append("')");
        }

        return sb.ToString();
    }

    private static string ObjectsSql(IEnumerable<(string Owner, string Name)> pairs) =>
        "SELECT owner, object_name, object_type FROM all_objects WHERE object_type IN ('FUNCTION','PROCEDURE','PACKAGE','TYPE') AND ("
        + Pairs(pairs, "owner", "object_name") + ")";

    private static string DependenciesSql(IEnumerable<(string Owner, string Name)> pairs) =>
        "SELECT DISTINCT referenced_owner, referenced_name, referenced_type, referenced_link_name FROM all_dependencies WHERE ("
        + Pairs(pairs, "owner", "name") + ")";

    private static string SynonymsSql(IEnumerable<(string Owner, string Name)> pairs) =>
        "SELECT owner, synonym_name, table_owner, table_name, db_link FROM all_synonyms WHERE ("
        + Pairs(pairs, "owner", "synonym_name") + ")";

    private static string TargetsSql(IEnumerable<(string Owner, string Name)> pairs) =>
        "SELECT owner, object_name, object_type FROM all_objects WHERE object_type NOT IN ('PACKAGE BODY','TYPE BODY') AND ("
        + Pairs(pairs, "owner", "object_name") + ")";

    // 명세(PACKAGE·TYPE)도 읽는다: 외부 언어 호출 명세는 명세에 적힐 수 있다.
    private static string SourceSql(IEnumerable<(string Owner, string Name)> pairs) =>
        "SELECT owner, name, type, line, text FROM all_source WHERE type IN ('FUNCTION','PROCEDURE','PACKAGE','PACKAGE BODY','TYPE','TYPE BODY','TRIGGER') AND ("
        + Pairs(pairs, "owner", "name") + ") AND (line = 1 OR REGEXP_LIKE(text, 'AUTONOMOUS_TRANSACTION|LANGUAGE|EXTERNAL|MLE|SQL_MACRO|POLYMORPHIC', 'i'))";
}
