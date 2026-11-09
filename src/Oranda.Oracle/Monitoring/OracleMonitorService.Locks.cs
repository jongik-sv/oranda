using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Oranda.Core.Sessions;
using Oranda.Oracle.Sessions;
using static Oranda.Core.Monitoring.Providers.MonitorRows;

namespace Oranda.Oracle.Monitoring;

/// <summary>
/// Lock 모니터의 Oracle 고리(TSK-05-02): 잠긴 행 값 SELECT 와 그 값 읽기(CLOB·BLOB 앞부분). 조회 본문은 <see cref="Core.Monitoring.Providers.CatalogMonitorService"/>.
/// <para>잠긴 행 값만 카탈로그 밖 SELECT 다 — 식별자는 딕셔너리에서 얻은 이름만 따옴표로 감싸 넣고, rowid 는 바인드로 넘긴다.</para>
/// </summary>
internal sealed partial class OracleMonitorService
{
    /// <summary>잠긴 행 값 SELECT. 이름에 따옴표가 있으면(따옴표 식별자로도 만들 수 없음) null — 문장을 만들지 않는다.</summary>
    internal static string? LockedRowSelect(string owner, string table)
    {
        if (owner.Length == 0 || table.Length == 0 || owner.Contains('"', StringComparison.Ordinal) || table.Contains('"', StringComparison.Ordinal)
            || owner.Contains('\0', StringComparison.Ordinal) || table.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        return $"SELECT * FROM \"{owner}\".\"{table}\" WHERE ROWID = CHARTOROWID(:r)";
    }

    /// <summary>잠긴 행 값을 모니터 세션 연결로 읽는다(세션 명령 잠금 공유, D-041). 행이 없으면 값 null.</summary>
    protected override async Task<(IReadOnlyList<string> Names, IReadOnlyList<string?>? Values)> ReadLockedRowValuesAsync(
        ISession monitor, string sql, string rowId, CancellationToken ct)
    {
        var session = monitor as OracleSession ?? throw new ArgumentException("OracleSession 이 아닌 세션은 쓸 수 없습니다.", nameof(monitor));
        var gate = session.CommandGate; // 세션 연결의 명령 잠금(D-041): 실행·결과 페이지·다른 도구와 한 줄로 선다
        await gate.EnterAsync(ct);
        try
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.BindByName = true;
            cmd.InitialLONGFetchSize = LockedValueMaxLength; // LONG 은 앞부분만, LOB 은 아래에서 앞부분만 읽는다
            cmd.CommandText = sql;
            cmd.Parameters.Add(new OracleParameter("r", OracleDbType.Varchar2) { Value = rowId });

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var names = new string[reader.FieldCount];
            for (var i = 0; i < names.Length; i++)
            {
                names[i] = reader.GetName(i);
            }

            if (!await reader.ReadAsync(ct))
            {
                return (names, null);
            }

            var values = new string?[names.Length];
            for (var i = 0; i < values.Length; i++)
            {
                if (await reader.IsDBNullAsync(i, ct))
                {
                    continue;
                }

                try
                {
                    values[i] = reader.GetProviderSpecificFieldType(i) switch
                    {
                        var t when t == typeof(OracleClob) => ReadClobHead(reader.GetOracleClob(i)),
                        var t when t == typeof(OracleBlob) => ReadBlobHead(reader.GetOracleBlob(i)),
                        _ => FormatLockedValue(reader.GetValue(i)),
                    };
                }
                catch (Exception ex) when (ex is InvalidCastException or OverflowException)
                {
                    values[i] = FormatLockedValue(reader.GetOracleValue(i)?.ToString()); // NUMBER 정밀도가 .NET decimal 을 넘는 경우
                }
            }

            return (names, values);
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

    private static string ReadClobHead(OracleClob clob)
    {
        using (clob)
        {
            var buffer = new char[LockedValueMaxLength];
            var read = clob.Read(buffer, 0, buffer.Length);
            var text = new string(buffer, 0, Math.Max(0, read));
            return clob.Length / 2 > read ? text + "…" : text; // Length 는 바이트(UTF-16)
        }
    }

    private static string ReadBlobHead(OracleBlob blob)
    {
        using (blob)
        {
            var buffer = new byte[64];
            var read = blob.Read(buffer, 0, buffer.Length);
            var hex = "0x" + Convert.ToHexString(buffer, 0, Math.Max(0, read));
            return blob.Length > read ? hex + $"… ({blob.Length} bytes)" : hex;
        }
    }
}
