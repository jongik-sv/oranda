using System.Data.Common;
using Oranda.Core.Catalog;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Tibero.Catalog;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Schema;

/// <summary>
/// 스키마·DB 정보 서비스(T3)가 카탈로그 질의를 보내는 도우미(Oracle <c>Schema/SchemaQuery</c> 대응).
/// 문장은 Tibero 카탈로그(<see cref="TiberoEmbeddedCatalog"/>, dbms: tibero)에서만 꺼내고(TC-03), <c>@PFX@</c> 를 사전 뷰 앞머리
/// (<see cref="TiberoSessionQuery.ViewPrefix"/>)로 바꾼 뒤 <see cref="TiberoSessionQuery.RunAsync"/> 로 보낸다(명령 잠금·끊김 처리는 그쪽).
/// </summary>
internal static class TiberoCatalogQuery
{
    /// <summary>사전 뷰 앞머리 자리(<c>dba</c>/<c>all</c>).</summary>
    internal const string PrefixToken = "@PFX@";

    private static readonly Lazy<SqlCatalog> Catalog = new(TiberoEmbeddedCatalog.Load);

    /// <summary>카탈로그 id → 보낼 문장(<c>@PFX@</c>·<paramref name="replacements"/> 바꿈). 서버 버전에 맞는 변형을 고른다.</summary>
    internal static string Sql(ISession session, string id, IReadOnlyDictionary<string, string>? replacements = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var sql = Catalog.Value.Get(id, session.Server).Sql.Replace(PrefixToken, TiberoSessionQuery.ViewPrefix(session), StringComparison.Ordinal);
        if (replacements is not null)
        {
            foreach (var (key, value) in replacements)
            {
                sql = sql.Replace(key, value, StringComparison.Ordinal);
            }
        }

        return sql;
    }

    /// <summary>카탈로그 질의 <paramref name="id"/> 를 보내 행을 모두 읽는다.</summary>
    internal static Task<TiberoSessionQuery.Table> RunAsync(ISession session, string id, Action<DbCommand>? bind = null,
                                                            IReadOnlyDictionary<string, string>? replacements = null, CancellationToken ct = default) =>
        TiberoSessionQuery.RunAsync(session, Sql(session, id, replacements), bind, ct: ct);

    /// <summary>카탈로그 질의 <paramref name="id"/> 를 이름 바인드(<c>:이름</c> → 값)로 보낸다. 서비스의 기본 <see cref="CatalogRunner"/>.</summary>
    internal static Task<TiberoSessionQuery.Table> RunAsync(ISession session, string id, IReadOnlyDictionary<string, object?>? binds, CancellationToken ct) =>
        RunAsync(session, id, binds is null ? null : c =>
        {
            foreach (var (name, value) in binds)
            {
                TiberoSessionQuery.Bind(c, name, value);
            }
        }, ct: ct);

    /// <summary>
    /// 「볼 수 없음」(뷰 없음·권한 부족, <c>DbErrorKinds.IsNoAccess</c>) 인 서버 오류. 스키마 폴더는 이때 「권한 없음」 으로 보이고, 나머지 오류는 던진다.
    /// </summary>
    internal static bool IsNoAccess(Exception exception) => TiberoErrorClassifier.ToDbError(exception, null).IsNoAccess();

    /// <summary>표 하나를 고르는 바인드(<c>:owner</c>, <c>:name</c>).</summary>
    internal static Dictionary<string, object?> TableBinds(DbObjectRef table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return new(StringComparer.Ordinal) { ["owner"] = table.Owner, ["name"] = table.Name };
    }

    /// <summary>
    /// 카탈로그 질의 → 스키마 폴더 항목(<paramref name="map"/> = 행·순번 → 항목). 「볼 수 없음」 = <c>Denied</c>,
    /// 이 서버 버전에 맞는 카탈로그 변형 없음 = 빈 폴더(앱을 멈추지 않는다), 나머지 오류는 던진다 — Oracle 스키마 폴더와 같은 규칙.
    /// </summary>
    internal static async Task<SchemaFolderResult<T>> FolderAsync<T>(CatalogRunner run, ISession session, string id, IReadOnlyDictionary<string, object?> binds,
                                                                     Func<object?[], int, T> map, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(map);
        try
        {
            var table = await run(session, id, binds, ct).ConfigureAwait(false);
            return new SchemaFolderResult<T>([.. table.Rows.Select(map)]);
        }
        catch (CatalogQueryNotFoundException)
        {
            return new SchemaFolderResult<T>([]);
        }
        catch (Exception ex) when (IsNoAccess(ex))
        {
            return new SchemaFolderResult<T>([], Denied: true);
        }
    }
}

/// <summary>카탈로그 질의 실행기(서비스가 쓰고, 단위 시험은 가짜로 바꾼다): 카탈로그 id·이름 바인드 → 표.</summary>
internal delegate Task<TiberoSessionQuery.Table> CatalogRunner(ISession session, string id, IReadOnlyDictionary<string, object?>? binds, CancellationToken ct);
