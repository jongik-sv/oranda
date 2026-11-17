using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Metadata;

/// <summary>
/// 테이블 편집기 「저장」 페이지 LOB 표(T13c, 설계 18.2): LOB 열(CLOB·NCLOB·BLOB)이 있는 테이블만 <c>meta.table_lobs</c>(<c>ALL_LOBS</c>)를 더 묻는다.
/// 권한 등으로 못 읽으면 <see cref="TableInfo.Lobs"/> 를 비운 채 둔다(편집기는 LOB 저장 절을 모름으로 본다 — 바꾸지 않으면 문장에 넣지 않는다).
/// </summary>
internal sealed partial class OracleMetadataProvider
{
    private static async Task<TableInfo> WithLobsAsync(ISession s, TableInfo info, Action<OracleCommand> bindTable, CancellationToken ct)
    {
        // 뷰(meta.table_props 행 없음 → Storage null)와 LOB 열이 없는 테이블은 묻지 않는다.
        if (info.Storage is null || !info.Columns.Any(c => TableEditDefaults.IsLobType(c.DataType)))
        {
            return info;
        }

        var rows = await TryQueryAsync(s, "meta.table_lobs", bindTable, ct);
        return rows is null ? info : OracleTableEditRows.WithLobs(info, rows);
    }
}
