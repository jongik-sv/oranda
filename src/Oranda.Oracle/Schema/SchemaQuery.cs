using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Catalog;
using Oranda.Core.Sessions;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Schema;

/// <summary>TSK-07-04 카탈로그 쿼리 실행 도우미. 쿼리는 <c>dbinfo.*</c> 카탈로그에서만 꺼낸다(TC-03).</summary>
internal static class SchemaQuery
{
    private static readonly Lazy<SqlCatalog> Catalog = new(EmbeddedCatalog.Load);

    internal sealed record Table(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows);

    internal static string ViewPrefix(ISession s) => s.Server.Capabilities.CanQueryDictionary ? "dba" : "all";

    internal static async Task<Table> RunAsync(ISession s, string id, IReadOnlyDictionary<string, string>? replacements,
                                               Action<OracleCommand> bind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var session = s as OracleSession ?? throw new ArgumentException("OracleSession 이 아닌 세션은 쓸 수 없습니다.", nameof(s));
        var sql = Catalog.Value.Get(id, s.Server).Sql.Replace("@PFX@", ViewPrefix(s), StringComparison.Ordinal);
        if (replacements is not null)
        {
            foreach (var (key, value) in replacements)
            {
                sql = sql.Replace(key, value, StringComparison.Ordinal);
            }
        }

        var gate = session.CommandGate; // 세션 연결의 명령 잠금(D-041): 실행·결과 페이지·다른 도구와 한 줄로 선다
        await gate.EnterAsync(ct);
        try
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.BindByName = true;
            cmd.CommandText = sql;
            bind(cmd);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var columns = new string[reader.FieldCount];
            for (var i = 0; i < columns.Length; i++)
            {
                columns[i] = reader.GetName(i);
            }

            var rows = new List<object?[]>();
            while (await reader.ReadAsync(ct))
            {
                var row = new object?[columns.Length];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }

            return new Table(columns, rows);
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

    internal static string? Str(object? v) => v switch
    {
        null => null,
        string s => s,
        _ => Convert.ToString(v, CultureInfo.InvariantCulture),
    };

    internal static DateTime? Date(object? v) => v switch
    {
        DateTime d => d,
        DateTimeOffset o => o.DateTime,
        string s when DateTime.TryParseExact(s, "yyyy-MM-dd:HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) => parsed,
        _ => null,
    };

    internal static long? Long(object? v)
        => v is null ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);
}
