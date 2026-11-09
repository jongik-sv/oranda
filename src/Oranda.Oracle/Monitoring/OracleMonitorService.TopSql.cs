using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Catalog;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Sessions;
using static Oranda.Core.Catalog.Providers.CatalogValues;
using static Oranda.Core.Monitoring.Providers.MonitorRows;

namespace Oranda.Oracle.Monitoring;

/// <summary>
/// SQL 모니터(Top SQL·SQL 전문·바인드 캡처). 소유: TSK-09-02.
/// <para>카탈로그 <c>sql.*</c>(모두 <c>license: none</c>, <c>gv$sql</c>·<c>gv$sql_bind_capture</c>)만 쓴다. ASH·AWR 는 쓰지 않는다(SQLM-03 은 이후).
/// 쿼리가 팩을 요구한다고 적히면 <see cref="EnsureLicense"/> 가 보유 선언·서버 설정과 대조해 모자라면 드라이버로 보내지 않는다.</para>
/// </summary>
internal sealed partial class OracleMonitorService
{
    private static readonly Lazy<SqlCatalog> TopSqlCatalog = new(EmbeddedCatalog.Load);

    public async Task<IReadOnlyList<TopSqlRow>> GetTopSqlAsync(ISession monitor, TopSqlCriteria c, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(c);

        var rows = await TopSqlQueryAsync(monitor, "sql.top", cmd =>
        {
            AddInt(cmd, "order_by", (int)c.OrderBy);
            AddInt(cmd, "top", Math.Clamp(c.Top, 1, 1000));
            TopSqlNumber(cmd, "min_elapsed_us", c.MinElapsedSeconds is { } s ? Math.Round(s * 1_000_000d) : null);
            TopSqlNumber(cmd, "min_buffer_gets", c.MinBufferGets);
            TopSqlNumber(cmd, "min_executions", c.MinExecutions);
            AddText(cmd, "text", c.TextContains);
            AddText(cmd, "schema", c.ParsingSchema);
            TopSqlNumber(cmd, "active_secs", c.ActiveWithin is { } w ? Math.Max(1, Math.Round(w.TotalSeconds)) : null);
        }, ct);

        return [.. rows.Select(ToTopSqlRow)];
    }

    public async Task<SqlDetail> GetSqlDetailAsync(ISession monitor, CursorRef cursor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(cursor);

        var text = await TopSqlQueryAsync(monitor, "sql.full_text", cmd =>
        {
            TopSqlNumber(cmd, "inst_id", cursor.InstanceId);
            AddText(cmd, "sql_id", cursor.SqlId);
        }, ct);
        if (text.Count == 0 || Str(text[0][0]) is not { } fullText)
        {
            throw new InvalidOperationException($"SQL 을 공유 풀에서 찾을 수 없습니다(밀려났을 수 있습니다): {cursor.SqlId}");
        }

        var binds = await TopSqlQueryAsync(monitor, "sql.binds", cmd =>
        {
            TopSqlNumber(cmd, "inst_id", cursor.InstanceId);
            AddText(cmd, "sql_id", cursor.SqlId);
            TopSqlNumber(cmd, "child", cursor.ChildNumber);
        }, ct);

        return new SqlDetail(cursor, fullText) { Binds = TopSqlBinds(binds) };
    }

    private static void TopSqlNumber(OracleCommand c, string name, double? value) =>
        c.Parameters.Add(new OracleParameter(name, OracleDbType.Double) { Value = value is { } v ? v : DBNull.Value });

    private static void TopSqlNumber(OracleCommand c, string name, long? value) =>
        c.Parameters.Add(new OracleParameter(name, OracleDbType.Int64) { Value = value is { } v ? v : DBNull.Value });

    private static void TopSqlNumber(OracleCommand c, string name, int? value) =>
        c.Parameters.Add(new OracleParameter(name, OracleDbType.Int32) { Value = value is { } v ? v : DBNull.Value });

    /// <summary>카탈로그 <c>sql.*</c> 쿼리를 모니터 세션 연결로 읽는다. 라이선스 대조 뒤에만 보낸다. 세션 명령 잠금(D-041)을 공유해 한 번에 하나만 보낸다.</summary>
    private static async Task<List<object?[]>> TopSqlQueryAsync(ISession s, string id, Action<OracleCommand> bind, CancellationToken ct)
    {
        var session = s as OracleSession ?? throw new ArgumentException("OracleSession 이 아닌 세션은 쓸 수 없습니다.", nameof(s));
        var query = TopSqlCatalog.Value.Get(id, s.Server);
        EnsureLicense(s, query.Header.License);

        var gate = session.CommandGate; // 세션 연결의 명령 잠금(D-041): 실행·결과 페이지·다른 도구와 한 줄로 선다
        await gate.EnterAsync(ct);
        try
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.BindByName = true;
            cmd.InitialLOBFetchSize = -1;
            cmd.CommandText = query.Sql;
            bind(cmd);

            var rows = new List<object?[]>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }

            return rows;
        }
        catch (OracleException ex) when (session.ReportFailure(ex))
        {
            throw;
        }
        finally
        {
            gate.Release();
        }
    }
}
