using Oranda.Core.Catalog.Providers;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using static Oranda.Core.Catalog.Providers.CatalogValues;
using static Oranda.Core.Monitoring.Providers.MonitorRows;

namespace Oranda.Core.Monitoring.Providers;

/// <summary>
/// SQL 모니터(Top SQL·SQL 전문·바인드 캡처). 소유: TSK-09-02(본문), P0-b(기반 클래스로 옮김).
/// <para>카탈로그 <c>sql.*</c>(모두 <c>license: none</c>, <c>gv$sql</c>·<c>gv$sql_bind_capture</c>)만 쓴다. ASH·AWR 는 쓰지 않는다(SQLM-03 은 이후).
/// 쿼리가 팩을 요구한다고 적히면 질의 포트의 <see cref="CatalogLicenseGate"/> 가 보유 선언·서버 설정과 대조해 모자라면 드라이버로 보내지 않는다.
/// <c>sql.*</c> 는 <c>@PFX@</c> 를 바꾸지 않는다(<see cref="CatalogQueryOptions.Default"/>).</para>
/// </summary>
public abstract partial class CatalogMonitorService
{
    public async Task<IReadOnlyList<TopSqlRow>> GetTopSqlAsync(ISession monitor, TopSqlCriteria c, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(c);

        var rows = (await TopSqlQueryAsync(monitor, "sql.top",
        [
            CatalogBinds.Int("order_by", (int)c.OrderBy),
            CatalogBinds.Int("top", Math.Clamp(c.Top, 1, 1000)),
            CatalogBinds.Double("min_elapsed_us", c.MinElapsedSeconds is { } s ? Math.Round(s * 1_000_000d) : null),
            CatalogBinds.Long("min_buffer_gets", c.MinBufferGets),
            CatalogBinds.Long("min_executions", c.MinExecutions),
            CatalogBinds.Trimmed("text", c.TextContains),
            CatalogBinds.Trimmed("schema", c.ParsingSchema),
            CatalogBinds.Double("active_secs", c.ActiveWithin is { } w ? Math.Max(1, Math.Round(w.TotalSeconds)) : null),
        ], ct)).Rows;

        return [.. rows.Select(ToTopSqlRow)];
    }

    public async Task<SqlDetail> GetSqlDetailAsync(ISession monitor, CursorRef cursor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(cursor);

        var text = (await TopSqlQueryAsync(monitor, "sql.full_text",
            [CatalogBinds.Int("inst_id", cursor.InstanceId), CatalogBinds.Trimmed("sql_id", cursor.SqlId)], ct)).Rows;
        if (text.Count == 0 || Str(text[0][0]) is not { } fullText)
        {
            throw new InvalidOperationException($"SQL 을 공유 풀에서 찾을 수 없습니다(밀려났을 수 있습니다): {cursor.SqlId}");
        }

        var binds = (await TopSqlQueryAsync(monitor, "sql.binds",
        [
            CatalogBinds.Int("inst_id", cursor.InstanceId),
            CatalogBinds.Trimmed("sql_id", cursor.SqlId),
            CatalogBinds.Int("child", cursor.ChildNumber),
        ], ct)).Rows;

        return new SqlDetail(cursor, fullText) { Binds = TopSqlBinds(binds) };
    }

    /// <summary>카탈로그 <c>sql.*</c> 쿼리를 모니터 세션 연결로 읽는다. 라이선스 대조 뒤에만 보낸다(질의 포트).</summary>
    private Task<CatalogRows> TopSqlQueryAsync(ISession s, string id, IReadOnlyList<CatalogBind> binds, CancellationToken ct) =>
        QueryAsync(s, id, binds, CatalogQueryOptions.Default, ct);
}
