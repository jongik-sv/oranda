using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Composition;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Schema;

/// <summary>객체 검색·참조 관계·휴지통 목록(읽기 전용). 소유: TSK-07-04.</summary>
[OrandaService(typeof(ISchemaAdminProvider))]
internal sealed class OracleSchemaAdminProvider : ISchemaAdminProvider
{
    public async Task<IReadOnlyList<ObjectSearchRow>> SearchObjectsAsync(ISession s, ObjectSearchQuery q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(q);

        // 크기는 세그먼트 뷰에서 구한다. DBA 권한이 없으면 현재 사용자의 세그먼트만 본다.
        var dba = s.Server.Capabilities.CanQueryDictionary;
        var size = $"(SELECT SUM(g.bytes) FROM {(dba ? "dba_segments" : "user_segments")} g "
            + "WHERE g.segment_name = o.object_name AND g.segment_type LIKE o.object_type || '%'"
            + (dba ? " AND g.owner = o.owner" : " AND o.owner = USER")
            + ")";

        var table = await SchemaQuery.RunAsync(s, "dbinfo.object_search", new Dictionary<string, string> { ["@SIZE@"] = size }, c =>
        {
            c.Parameters.Add(new OracleParameter("owner", OracleDbType.Varchar2) { Value = (object?)q.Owner ?? DBNull.Value });
            c.Parameters.Add(new OracleParameter("types", OracleDbType.Varchar2)
            {
                Value = q.ObjectTypes.Count == 0 ? DBNull.Value : "|" + string.Join('|', q.ObjectTypes) + "|",
            });
            c.Parameters.Add(new OracleParameter("name_pattern", OracleDbType.Varchar2) { Value = (object?)q.NamePattern ?? DBNull.Value });
            c.Parameters.Add(new OracleParameter("invalid_only", OracleDbType.Int32) { Value = q.InvalidOnly ? 1 : 0 });
            c.Parameters.Add(new OracleParameter("created_after", OracleDbType.Date) { Value = (object?)q.CreatedAfter ?? DBNull.Value });
            c.Parameters.Add(new OracleParameter("modified_after", OracleDbType.Date) { Value = (object?)q.ModifiedAfter ?? DBNull.Value });
            c.Parameters.Add(new OracleParameter("min_size", OracleDbType.Int64) { Value = (object?)q.MinSizeBytes ?? DBNull.Value });
            c.Parameters.Add(new OracleParameter("max_size", OracleDbType.Int64) { Value = (object?)q.MaxSizeBytes ?? DBNull.Value });
            c.Parameters.Add(new OracleParameter("max_rows", OracleDbType.Int32) { Value = q.Limit <= 0 ? int.MaxValue : q.Limit });
        }, ct);

        return
        [
            .. table.Rows.Select(r => new ObjectSearchRow(
                new DbObjectRef(SchemaQuery.Str(r[0])!, SchemaQuery.Str(r[1])!, SchemaQuery.Str(r[2])!),
                SchemaQuery.Str(r[3]) ?? string.Empty, SchemaQuery.Date(r[4]), SchemaQuery.Date(r[5]), SchemaQuery.Long(r[6]))),
        ];
    }

    public async Task<IReadOnlyList<ObjectReference>> GetReferencesAsync(ISession s, DbObjectRef obj, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var table = await SchemaQuery.RunAsync(s, "dbinfo.references", null, c =>
        {
            c.Parameters.Add("owner", obj.Owner);
            c.Parameters.Add("name", obj.Name);
            c.Parameters.Add("type", obj.ObjectType);
        }, ct);

        return
        [
            .. table.Rows.Select(r => new ObjectReference(
                string.Equals(SchemaQuery.Str(r[0]), "USED_BY", StringComparison.Ordinal),
                new DbObjectRef(SchemaQuery.Str(r[1])!, SchemaQuery.Str(r[2])!, SchemaQuery.Str(r[3])!))),
        ];
    }

    public async Task<IReadOnlyList<RecycleBinItem>> ListRecycleBinAsync(ISession s, CancellationToken ct)
    {
        var table = await SchemaQuery.RunAsync(s, "dbinfo.recyclebin", null, _ => { }, ct);
        return
        [
            .. table.Rows.Select(r => new RecycleBinItem(
                SchemaQuery.Str(r[0])!, SchemaQuery.Str(r[1]) ?? string.Empty, SchemaQuery.Str(r[2]) ?? string.Empty, SchemaQuery.Str(r[3]),
                SchemaQuery.Date(r[4]), string.Equals(SchemaQuery.Str(r[5]), "YES", StringComparison.Ordinal),
                string.Equals(SchemaQuery.Str(r[6]), "YES", StringComparison.Ordinal), SchemaQuery.Long(r[7]))),
        ];
    }
}
