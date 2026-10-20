using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Composition;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Schema;

/// <summary>열 순서 바꾸기의 표 종류·참조 소스 줄 조회(읽기 전용). 소유: TSK-13-01.</summary>
[OrandaService(typeof(IColumnReorderProvider))]
internal sealed class ColumnReorderProvider : IColumnReorderProvider
{
    /// <summary>한 번에 가져올 소스 줄 수 상한(넘으면 앞쪽만 쓴다).</summary>
    internal const int MaxSourceLines = 5000;

    public async Task<ColumnReorderTraits> GetTraitsAsync(ISession s, DbObjectRef table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(table);

        var result = await SchemaQuery.RunAsync(s, "dbinfo.column_reorder_traits", null, c => BindTable(c, table), ct);
        if (result.Rows.Count == 0)
        {
            // 표가 아닌 객체(뷰 등) 또는 볼 수 없는 표.
            return new ColumnReorderTraits(false, false, null);
        }

        var row = result.Rows[0];
        return new ColumnReorderTraits(
            IsExternal: (SchemaQuery.Long(row[2]) ?? 0) > 0,
            IsTemporary: string.Equals(SchemaQuery.Str(row[0]), "Y", StringComparison.Ordinal),
            ClusterName: SchemaQuery.Str(row[1]));
    }

    public async Task<IReadOnlyList<SourceSearchHit>> FindReferencingSourceLinesAsync(ISession s, DbObjectRef table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(table);

        var result = await SchemaQuery.RunAsync(s, "dbinfo.column_reorder_source", null, c =>
        {
            BindTable(c, table);
            c.Parameters.Add(new OracleParameter("max_rows", OracleDbType.Int32) { Value = MaxSourceLines });
        }, ct);

        return
        [
            .. result.Rows.Select(r => new SourceSearchHit(
                new DbObjectRef(SchemaQuery.Str(r[0])!, SchemaQuery.Str(r[1])!, SchemaQuery.Str(r[2])!),
                (int)(SchemaQuery.Long(r[3]) ?? 0),
                (SchemaQuery.Str(r[4]) ?? string.Empty).TrimEnd('\r', '\n'))),
        ];
    }

    private static void BindTable(OracleCommand c, DbObjectRef table)
    {
        c.Parameters.Add(new OracleParameter("owner", OracleDbType.Varchar2) { Value = table.Owner });
        c.Parameters.Add(new OracleParameter("name", OracleDbType.Varchar2) { Value = table.Name });
    }
}
