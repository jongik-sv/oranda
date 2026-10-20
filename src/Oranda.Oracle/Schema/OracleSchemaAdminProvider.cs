using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Composition;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Schema;

/// <summary>객체 검색·소스 본문 검색·열 이름 검색·참조 관계·휴지통 목록(읽기 전용). 소유: TSK-07-04.</summary>
[OrandaService(typeof(ISchemaAdminProvider))]
internal sealed class OracleSchemaAdminProvider : ISchemaAdminProvider
{
    public async Task<IReadOnlyList<ObjectSearchRow>> SearchObjectsAsync(ISession s, ObjectSearchQuery q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(q);

        // 크기는 세그먼트 뷰에서 한 번 모아 붙인다(TSK-07-06). DBA 권한이 없으면 현재 사용자의 세그먼트만 본다.
        var segments = s.Server.Capabilities.CanQueryDictionary
            ? "dba_segments"
            : "(SELECT USER AS owner, segment_name, segment_type, bytes FROM user_segments)";

        var table = await SchemaQuery.RunAsync(s, "dbinfo.object_search", new Dictionary<string, string> { ["@SEGMENTS@"] = segments }, c =>
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

    public async Task<SearchHits<SourceSearchHit>> SearchSourceAsync(ISession s, SourceSearchQuery q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(q);

        // 검색어·패턴은 모두 바인드로 보낸다(SQL 글자에 잇지 않는다).
        var binds = SourceSearchPattern.Build(q);
        var limit = q.Limit <= 0 ? 1000 : q.Limit;
        try
        {
            if (q.UseRegex)
            {
                // 걸러진 줄이 없으면 서버가 패턴을 평가하지 않아 틀린 정규식을 놓친다. dual 한 줄로 먼저 평가한다.
                await SchemaQuery.RunAsync(s, "dbinfo.regex_check", null, c => BindPattern(c, binds), ct);
            }

            var table = await SchemaQuery.RunAsync(s, "dbinfo.source_search", null, c =>
            {
                c.Parameters.Add(new OracleParameter("owner", OracleDbType.Varchar2) { Value = (object?)q.Owner ?? DBNull.Value });
                c.Parameters.Add(new OracleParameter("types", OracleDbType.Varchar2)
                {
                    Value = q.ObjectTypes.Count == 0 ? DBNull.Value : "|" + string.Join('|', q.ObjectTypes) + "|",
                });
                c.Parameters.Add(new OracleParameter("needle", OracleDbType.Varchar2) { Value = (object?)binds.Needle ?? DBNull.Value });
                BindPattern(c, binds);
                c.Parameters.Add(new OracleParameter("max_rows", OracleDbType.Int32) { Value = limit + 1 });
            }, ct);

            var hits = table.Rows.Take(limit).Select(r => new SourceSearchHit(
                new DbObjectRef(SchemaQuery.Str(r[0])!, SchemaQuery.Str(r[1])!, SchemaQuery.Str(r[2])!),
                (int)(SchemaQuery.Long(r[3]) ?? 0),
                (SchemaQuery.Str(r[4]) ?? string.Empty).TrimEnd('\r', '\n'))).ToList();
            return new SearchHits<SourceSearchHit>(hits, table.Rows.Count > limit);
        }
        catch (OracleException ex) when (SourceSearchPattern.IsRegexError(ex.Number))
        {
            throw new SearchPatternException(SourceSearchPattern.Describe(ex.Number), ex);
        }
    }

    public async Task<SearchHits<ColumnSearchHit>> SearchColumnsAsync(ISession s, ColumnSearchQuery q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(q);

        var limit = q.Limit <= 0 ? 1000 : q.Limit;
        var table = await SchemaQuery.RunAsync(s, "dbinfo.column_search", null, c =>
        {
            c.Parameters.Add(new OracleParameter("owner", OracleDbType.Varchar2) { Value = (object?)q.Owner ?? DBNull.Value });
            c.Parameters.Add(new OracleParameter("name_pattern", OracleDbType.Varchar2) { Value = q.NamePattern ?? "%" });
            c.Parameters.Add(new OracleParameter("max_rows", OracleDbType.Int32) { Value = limit + 1 });
        }, ct);

        var hits = table.Rows.Take(limit).Select(r => new ColumnSearchHit(
            new DbObjectRef(SchemaQuery.Str(r[0])!, SchemaQuery.Str(r[1])!, SchemaQuery.Str(r[2]) ?? "TABLE"),
            SchemaQuery.Str(r[3])!,
            TypeText(SchemaQuery.Str(r[4]) ?? string.Empty, SchemaQuery.Long(r[5]), SchemaQuery.Long(r[6]), SchemaQuery.Long(r[7])),
            !string.Equals(SchemaQuery.Str(r[8]), "N", StringComparison.Ordinal))).ToList();
        return new SearchHits<ColumnSearchHit>(hits, table.Rows.Count > limit);
    }

    /// <summary>열 형식 표시: <c>VARCHAR2(30)</c>, <c>NUMBER(10,2)</c>, <c>FLOAT(126)</c>. 그 밖은 서버가 준 이름 그대로(<c>TIMESTAMP(6)</c> 등).</summary>
    internal static string TypeText(string dataType, long? length, long? precision, long? scale) => dataType switch
    {
        "NUMBER" when precision is { } p => scale is { } sc and not 0 ? $"NUMBER({p},{sc})" : $"NUMBER({p})",
        "NUMBER" when scale is 0 => "NUMBER(*,0)",
        "FLOAT" when precision is { } p => $"FLOAT({p})",
        "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" or "RAW" when length is { } l => $"{dataType}({l})",
        _ => dataType,
    };

    private static void BindPattern(OracleCommand c, SourceSearchBinds binds)
    {
        c.Parameters.Add(new OracleParameter("pattern", OracleDbType.Varchar2) { Value = binds.Pattern });
        c.Parameters.Add(new OracleParameter("flags", OracleDbType.Varchar2) { Value = binds.Flags });
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
