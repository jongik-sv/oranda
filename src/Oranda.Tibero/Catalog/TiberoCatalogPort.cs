using System.Data;
using System.Data.Common;
using Oranda.Core.Catalog;
using Oranda.Core.Catalog.Providers;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Tibero.Driver;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Catalog;

/// <summary>
/// Tibero 의 카탈로그 질의 포트 구현(P1): 계획·모니터·튜닝 서비스(<c>Catalog*Service.QueryAsync</c>)가 같이 쓴다. Oracle <c>OracleCatalogQuery</c> 대응.
/// 순서 = 세션 형식 확인 → 카탈로그 Get(id, 서버) → 라이선스 관문(<see cref="CatalogLicenseGate"/>, Tibero 파일은 모두 none) → <c>@PFX@</c>(선택, <see cref="CatalogQueryOptions.Apply"/>)
/// → <see cref="TiberoSessionQuery.RunAsync"/>. 명령 잠금·스레드 풀·끊김 보고(ReportFailure)는 RunAsync 가 맡으므로 여기서 다시 잠그지 않는다(재진입 = 교착).
/// <para>
/// <see cref="CatalogQueryOptions.ReadLong"/> 은 무시한다: Oracle 은 LONG 열(PLAN_TABLE.other)을 끝까지 읽으려 <c>InitialLONGFetchSize</c> 를 켜야 하지만,
/// Tibero PLAN_TABLE 은 OTHERS 열이고(LONG 아님) 드라이버에 그 설정이 없다. CLOB 은 <c>GetValue</c> 가 전체 문자열을 준다.
/// 바인드는 이름 바인드라 같은 이름이 SQL 에 두 번 나와도 한 번만 넣는다. 같은 이름에 값이 다르면 조용히 버리지 않고 던진다(<see cref="Dedupe"/>).
/// 형은 드라이버 어댑터(<see cref="TiberoDriver.SetParameterKind"/>)로 정한다. 어댑터가 없는 세션(단위 시험)은 <see cref="DbParameter.DbType"/> 으로 대신한다.
/// </para>
/// </summary>
internal static class TiberoCatalogPort
{
    private static readonly Lazy<SqlCatalog> Catalog = new(TiberoEmbeddedCatalog.Load);

    internal static async Task<CatalogRows> QueryAsync(ISession s, string id, IReadOnlyList<CatalogBind> binds,
        CatalogQueryOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(binds);
        ArgumentNullException.ThrowIfNull(options);
        var tibero = s as TiberoSession ?? throw new ArgumentException("TiberoSession 이 아닌 세션은 쓸 수 없습니다.", nameof(s));

        var query = Catalog.Value.Get(id, s.Server);
        CatalogLicenseGate.Ensure(s, query.Header.License);
        var sql = options.Apply(query.Sql, s.Server);

        // binds 는 지연 목록일 수 있다(CatalogBinds.Defer): 명령을 만든 뒤(= 이 동작 안에서) 처음 읽는다.
        var table = await TiberoSessionQuery.RunAsync(tibero, sql, cmd => ApplyBinds(cmd, tibero.Driver, binds), ct: ct).ConfigureAwait(false);
        return new CatalogRows(options.ColumnNames ? table.Columns : [], table.Rows);
    }

    /// <summary>「볼 수 없음」(뷰 없음·권한 부족) 인 서버 오류. <c>TiberoCatalogQuery.IsNoAccess</c> 와 같은 규칙.</summary>
    internal static bool IsNoAccess(Exception exception) => TiberoErrorClassifier.ToDbError(exception, null).IsNoAccess();

    /// <summary>
    /// 바인드 → 명령 매개변수(이름 그대로). null 값은 DB NULL. 형: <paramref name="driver"/> 가 있으면 <see cref="ParamKindOf"/> 를 어댑터로, 없으면 <see cref="DbTypeOf"/> 를 <see cref="DbParameter.DbType"/> 으로.
    /// </summary>
    internal static void ApplyBinds(DbCommand command, TiberoDriver? driver, IReadOnlyList<CatalogBind> binds)
    {
        ArgumentNullException.ThrowIfNull(command);
        foreach (var b in Dedupe(binds))
        {
            TiberoSessionQuery.Bind(command, b.Name, b.Value);
            var parameter = command.Parameters[command.Parameters.Count - 1];
            if (driver is not null)
            {
                driver.SetParameterKind(parameter, ParamKindOf(b.Kind));
            }
            else
            {
                parameter.DbType = DbTypeOf(b.Kind);
            }
        }
    }

    /// <summary>
    /// 같은 이름(대소문자 무시)이 또 나오면: 값·형이 같으면 한 번만, 다르면 <see cref="ArgumentException"/>(조용히 버리면 SQL 의 한쪽 자리가 틀린 값을 받는다).
    /// 처음 나온 순서를 지킨다.
    /// </summary>
    internal static IReadOnlyList<CatalogBind> Dedupe(IReadOnlyList<CatalogBind> binds)
    {
        ArgumentNullException.ThrowIfNull(binds);
        var seen = new Dictionary<string, CatalogBind>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CatalogBind>(binds.Count);
        foreach (var b in binds)
        {
            if (!seen.TryGetValue(b.Name, out var first))
            {
                seen[b.Name] = b;
                result.Add(b);
            }
            else if (first.Kind != b.Kind || !Equals(first.Value, b.Value))
            {
                throw new ArgumentException($"바인드 '{b.Name}' 가 값이나 형이 다르게 두 번 주어졌습니다.", nameof(binds));
            }
        }

        return result;
    }

    /// <summary>Text = Varchar2, Int32 = Int32, Int64 = Int64, Double = Double, Date = Date(Oracle 의 Varchar2·Int32·Int64·Double·Date 대응).</summary>
    internal static TiberoParamKind ParamKindOf(CatalogBindKind kind) => kind switch
    {
        CatalogBindKind.Text => TiberoParamKind.Varchar2,
        CatalogBindKind.Int32 => TiberoParamKind.Int32,
        CatalogBindKind.Int64 => TiberoParamKind.Int64,
        CatalogBindKind.Double => TiberoParamKind.Double,
        CatalogBindKind.Date => TiberoParamKind.Date,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>드라이버 어댑터가 없을 때의 대신: Text = String, Int32 = Int32, Int64 = Int64, Double = Double, Date = DateTime.</summary>
    internal static DbType DbTypeOf(CatalogBindKind kind) => kind switch
    {
        CatalogBindKind.Text => DbType.String,
        CatalogBindKind.Int32 => DbType.Int32,
        CatalogBindKind.Int64 => DbType.Int64,
        CatalogBindKind.Double => DbType.Double,
        CatalogBindKind.Date => DbType.DateTime,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
