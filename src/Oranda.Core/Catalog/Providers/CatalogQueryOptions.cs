using Oranda.Core.Connection;

namespace Oranda.Core.Catalog.Providers;

/// <summary>
/// 카탈로그 질의 포트(<c>Catalog*Service.QueryAsync</c>)에 넘기는 읽기 방식. 서비스마다 Oracle 질의 도우미가 갈리던 점만 담는다(P0-b):
/// 열 이름(plan.explain_steps·tuning.*), LONG 끝까지 읽기(plan.* 의 PLAN_TABLE.other), <c>@PFX@</c> 치환(session.*·lock.*·tuning.*).
/// 권한 없음 → 빈 결과(lock.*)는 여기 두지 않는다 — 기반 클래스가 공급자의 <c>IsNoAccess</c> 로 판정한다.
/// </summary>
public sealed record CatalogQueryOptions
{
    /// <summary>열 이름 없음·LONG 기본 길이·<c>@PFX@</c> 그대로.</summary>
    public static CatalogQueryOptions Default { get; } = new();

    /// <summary>결과의 <see cref="CatalogRows.Names"/> 를 채운다. 거짓이면 빈 목록.</summary>
    public bool ColumnNames { get; init; }

    /// <summary>LONG 열을 끝까지 읽는다(Oracle <c>InitialLONGFetchSize = -1</c>). PLAN_TABLE.other 는 LONG 이고 <c>p.*</c> 로 읽는다.</summary>
    public bool ReadLong { get; init; }

    /// <summary>문장의 <c>@PFX@</c> 를 딕셔너리 권한이면 <c>dba</c>, 아니면 <c>all</c> 로 바꾼다(<see cref="Apply"/>).</summary>
    public bool DictionaryPrefix { get; init; }

    /// <summary><see cref="DictionaryPrefix"/> 이면 <c>@PFX@</c> 를 바꾼 문장, 아니면 그대로.</summary>
    public string Apply(string sql, ServerInfo server)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(server);
        return DictionaryPrefix
            ? sql.Replace("@PFX@", server.Capabilities.CanQueryDictionary ? "dba" : "all", StringComparison.Ordinal)
            : sql;
    }
}
