using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Catalog;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Monitoring;

/// <summary>
/// SQL 모니터(Top SQL·SQL 전문·바인드 캡처). 소유: TSK-09-02.
/// <para>카탈로그 <c>sql.*</c>(모두 <c>license: none</c>, <c>gv$sql</c>·<c>gv$sql_bind_capture</c>)만 쓴다. ASH·AWR 는 쓰지 않는다(SQLM-03 은 이후).
/// 쿼리가 팩을 요구한다고 적히면 <see cref="EnsureLicense"/> 가 보유 선언·서버 설정과 대조해 모자라면 드라이버로 보내지 않는다.</para>
/// </summary>
internal sealed partial class OracleMonitorService
{
    /// <summary>미리보기 글자 수 상한.</summary>
    internal const int TopSqlPreviewLength = 200;

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

        return [.. rows.Select(TopSqlToRow)];
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

    /// <summary>바인드 행(이름·위치·형·값·캡처 시각·캡처 여부)을 위치 순으로 모은다. 자식이 여럿이면 같은 위치는 최근 캡처만 남긴다.</summary>
    internal static IReadOnlyList<CapturedBind> TopSqlBinds(IEnumerable<object?[]> rows)
    {
        var byPosition = new SortedDictionary<int, CapturedBind>();
        foreach (var r in rows)
        {
            var position = Int(r[1]) ?? 0;
            var captured = r[4] is DateTime dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), TimeSpan.Zero) : (DateTimeOffset?)null;
            var wasCaptured = !string.Equals(Str(r[5]), "NO", StringComparison.OrdinalIgnoreCase);
            var bind = new CapturedBind(Str(r[0]) ?? string.Empty, position, Str(r[2]), wasCaptured ? Str(r[3]) : null) { CapturedAt = captured };
            if (!byPosition.TryGetValue(position, out var old) || (bind.CapturedAt ?? DateTimeOffset.MinValue) >= (old.CapturedAt ?? DateTimeOffset.MinValue))
            {
                byPosition[position] = bind;
            }
        }

        return [.. byPosition.Values];
    }

    private static TopSqlRow TopSqlToRow(object?[] r)
    {
        var preview = (Str(r[3]) ?? string.Empty).ReplaceLineEndings(" ").Trim();
        if (preview.Length > TopSqlPreviewLength)
        {
            preview = preview[..TopSqlPreviewLength];
        }

        return new TopSqlRow(new CursorRef(Str(r[1]) ?? string.Empty, Int(r[2]), Int(r[0])), preview)
        {
            Executions = Long(r[4]) ?? 0,
            ElapsedSeconds = (Long(r[5]) ?? 0) / 1_000_000d,
            CpuSeconds = (Long(r[6]) ?? 0) / 1_000_000d,
            BufferGets = Long(r[7]) ?? 0,
            DiskReads = Long(r[8]) ?? 0,
            RowsProcessed = Long(r[9]) ?? 0,
            ParsingSchema = Str(r[10]),
            Module = Str(r[11]),
            LastActiveTime = r[12] is DateTime dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), TimeSpan.Zero) : null,
        };
    }

    private static void TopSqlNumber(OracleCommand c, string name, double? value) =>
        c.Parameters.Add(new OracleParameter(name, OracleDbType.Double) { Value = value is { } v ? v : DBNull.Value });

    private static void TopSqlNumber(OracleCommand c, string name, long? value) =>
        c.Parameters.Add(new OracleParameter(name, OracleDbType.Int64) { Value = value is { } v ? v : DBNull.Value });

    private static void TopSqlNumber(OracleCommand c, string name, int? value) =>
        c.Parameters.Add(new OracleParameter(name, OracleDbType.Int32) { Value = value is { } v ? v : DBNull.Value });

    /// <summary>카탈로그 <c>sql.*</c> 쿼리를 모니터 세션 연결로 읽는다. 라이선스 대조 뒤에만 보낸다. 세션 문 게이트를 공유해 한 번에 하나만 보낸다.</summary>
    private static async Task<List<object?[]>> TopSqlQueryAsync(ISession s, string id, Action<OracleCommand> bind, CancellationToken ct)
    {
        var session = s as OracleSession ?? throw new ArgumentException("OracleSession 이 아닌 세션은 쓸 수 없습니다.", nameof(s));
        var query = TopSqlCatalog.Value.Get(id, s.Server);
        EnsureLicense(s, query.Header.License);

        var gate = SessionGates.GetValue(session, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
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
