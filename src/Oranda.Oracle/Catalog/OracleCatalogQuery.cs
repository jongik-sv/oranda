using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Catalog;
using Oranda.Core.Catalog.Providers;
using Oranda.Core.Sessions;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Catalog;

/// <summary>
/// Oracle 의 카탈로그 질의 포트 구현(P0-b): 계획·모니터·튜닝 서비스가 따로 갖던 같은 모양의 QueryAsync 를 하나로 모았다.
/// 순서 = 세션 형식 확인 → 카탈로그 Get → 라이선스 관문 → <c>@PFX@</c>(선택) → 세션 명령 잠금(D-041) → 명령(BindByName, LOB 끝까지, LONG 은 선택) → 바인드 → 읽기.
/// </summary>
internal static class OracleCatalogQuery
{
    internal static async Task<CatalogRows> QueryAsync(Lazy<SqlCatalog> catalog, ISession s, string id, IReadOnlyList<CatalogBind> binds,
        CatalogQueryOptions options, CancellationToken ct)
    {
        var session = s as OracleSession ?? throw new ArgumentException("OracleSession 이 아닌 세션은 쓸 수 없습니다.", nameof(s));
        var query = catalog.Value.Get(id, s.Server);
        CatalogLicenseGate.Ensure(s, query.Header.License);
        var sql = options.Apply(query.Sql, s.Server);

        var gate = session.CommandGate; // 세션 연결의 명령 잠금(D-041): 실행·결과 페이지·다른 도구와 한 줄로 선다
        await gate.EnterAsync(ct);
        try
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.BindByName = true;
            cmd.InitialLOBFetchSize = -1;
            if (options.ReadLong)
            {
                cmd.InitialLONGFetchSize = -1; // PLAN_TABLE.other 는 LONG(p.* 로 읽는다)
            }

            cmd.CommandText = sql;
            AddBinds(cmd, binds);

            var rows = new List<object?[]>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var names = new List<string>(options.ColumnNames ? reader.FieldCount : 0);
            if (options.ColumnNames)
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    names.Add(reader.GetName(i));
                }
            }

            while (await reader.ReadAsync(ct))
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }

            return new CatalogRows(names, rows);
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

    /// <summary>
    /// 바인드 → <see cref="OracleParameter"/>(이름·순서 그대로, 방향 Input, 크기 지정 없음). null 값은 <see cref="DBNull.Value"/>.
    /// 형: Text → Varchar2, Int32 → Int32, Int64 → Int64, Double → Double, Date → Date(옮기기 전 AddText·AddInt·AddLong·TopSqlNumber 와 같다).
    /// </summary>
    internal static void AddBinds(OracleCommand cmd, IReadOnlyList<CatalogBind> binds)
    {
        foreach (var b in binds)
        {
            cmd.Parameters.Add(new OracleParameter(b.Name, DbType(b.Kind)) { Value = b.Value ?? DBNull.Value });
        }
    }

    internal static OracleDbType DbType(CatalogBindKind kind) => kind switch
    {
        CatalogBindKind.Text => OracleDbType.Varchar2,
        CatalogBindKind.Int32 => OracleDbType.Int32,
        CatalogBindKind.Int64 => OracleDbType.Int64,
        CatalogBindKind.Double => OracleDbType.Double,
        CatalogBindKind.Date => OracleDbType.Date,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
