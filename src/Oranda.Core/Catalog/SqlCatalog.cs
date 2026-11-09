using Oranda.Core.Connection;

namespace Oranda.Core.Catalog;

/// <summary>
/// 원문 목록으로 만든 카탈로그. 만들 때 전체를 검사(<see cref="Validate"/>)하고 문제가 하나라도 있으면
/// <see cref="CatalogFormatException"/>. 원문 수집은 어셈블리 쪽(Oranda.Oracle EmbeddedCatalog)이 한다.
/// </summary>
public sealed class SqlCatalog : ISqlCatalog
{
    private readonly Dictionary<string, CatalogQuery[]> _byId;

    public SqlCatalog(IEnumerable<CatalogSource> sources)
    {
        var (queries, problems) = Load(sources);
        if (problems.Count > 0)
        {
            throw new CatalogFormatException(problems);
        }

        Entries = queries.Select(q => q.Header).ToList();
        _byId = queries
            .GroupBy(q => q.Header.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(q => q.Header.MinVersion).ToArray(), StringComparer.Ordinal);
    }

    /// <summary>수집된 모든 파일의 메타데이터.</summary>
    public IReadOnlyList<CatalogHeader> Entries { get; }

    public CatalogQuery Get(string id, ServerInfo server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return Get(id, server.MajorVersion, server.Dbms);
    }

    /// <summary>
    /// 선택 규칙: 같은 id 중 같은 <paramref name="dbms"/> 의 파일만 남기고, 그 가운데 <c>min-version ≤ majorVersion</c> 인 것 중
    /// 가장 높은 것. 다른 DBMS 의 파일로 대신 고르지 않는다.
    /// </summary>
    public CatalogQuery Get(string id, int majorVersion, Dbms dbms = Dbms.Oracle)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (_byId.TryGetValue(id, out var candidates))
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Header.Dbms == dbms && candidate.Header.MinVersion <= majorVersion)
                {
                    return candidate;
                }
            }
        }

        throw new CatalogQueryNotFoundException(id, majorVersion, dbms);
    }

    /// <summary>빌드 검사용: 원문 전체의 문제 목록. 비어 있으면 통과.</summary>
    public static IReadOnlyList<CatalogProblem> Validate(IEnumerable<CatalogSource> sources) => Load(sources).Problems;

    private static (List<CatalogQuery> Queries, List<CatalogProblem> Problems) Load(IEnumerable<CatalogSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var queries = new List<CatalogQuery>();
        var problems = new List<CatalogProblem>();
        foreach (var source in sources)
        {
            var (query, fileProblems) = CatalogParser.Parse(source);
            problems.AddRange(fileProblems);
            if (query is not null)
            {
                queries.Add(query);
            }
        }

        foreach (var duplicate in queries.GroupBy(q => (q.Header.Id, q.Header.Dbms, q.Header.MinVersion)).Where(g => g.Count() > 1))
        {
            var paths = string.Join(", ", duplicate.Select(q => q.Header.Path));
            var dbms = duplicate.Key.Dbms == Dbms.Oracle ? string.Empty : $" dbms {duplicate.Key.Dbms.ToString().ToLowerInvariant()}";
            foreach (var query in duplicate)
            {
                problems.Add(new CatalogProblem(query.Header.Path, CatalogRules.DuplicateId,
                    $"id '{duplicate.Key.Id}'{dbms} min-version {duplicate.Key.MinVersion} 이 여러 파일에 있다: {paths}"));
            }
        }

        return (queries, problems);
    }
}

/// <summary>카탈로그 원문에 형식 문제가 있다. 빌드 검사 시험이 먼저 잡으므로 실행 중에는 나오지 않아야 한다.</summary>
public sealed class CatalogFormatException : Exception
{
    public CatalogFormatException(IReadOnlyList<CatalogProblem> problems)
        : base("SQL 카탈로그 형식 오류:" + Environment.NewLine + string.Join(Environment.NewLine, problems ?? []))
    {
        Problems = problems ?? [];
    }

    public IReadOnlyList<CatalogProblem> Problems { get; }
}

/// <summary>id 가 없거나, 있어도 서버 버전에 맞는 파일이 없다.</summary>
public sealed class CatalogQueryNotFoundException : Exception
{
    public CatalogQueryNotFoundException(string id, int majorVersion)
        : this(id, majorVersion, Dbms.Oracle)
    {
    }

    /// <summary>Oracle 이 아니면 메시지에 DBMS 이름을 넣는다.</summary>
    public CatalogQueryNotFoundException(string id, int majorVersion, Dbms dbms)
        : base(dbms == Dbms.Oracle
            ? $"SQL 카탈로그에 서버 버전 {majorVersion} 에서 쓸 수 있는 '{id}' 가 없다."
            : $"SQL 카탈로그에 {dbms} 서버 버전 {majorVersion} 에서 쓸 수 있는 '{id}' 가 없다.")
    {
        Id = id;
        MajorVersion = majorVersion;
        Dbms = dbms;
    }

    public string Id { get; }

    public int MajorVersion { get; }

    /// <summary>찾던 DBMS. 기본 Oracle.</summary>
    public Dbms Dbms { get; }
}
